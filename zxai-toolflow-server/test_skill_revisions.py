from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
import hashlib
from html.parser import HTMLParser
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from uuid import uuid4

from server import ADMIN_PAGE_HTML, Store, make_handler
from skill_revisions import (
    ADMIN_LIST_PATH, INTAKE_PATH, MAX_DOCUMENT_BYTES, MAX_REVISION_BODY_BYTES,
    RevisionError, SKILL_ADMIN_SCRIPT, split_document, validate_revision, validate_review,
)


SUBMISSION_ID = "a5e5cf57-2a17-4974-8ffd-bc9ed5eb2a8b"
HEADER = "---\nname: zhenxing-assistant\ndescription: 枕星助手\n---\n"


def sample_revision():
    original = HEADER + "先了解目标，然后执行。\n"
    modified = HEADER + "先了解目标，列出计划，然后执行并验证。\n"
    return {
        "schemaVersion": 1, "submissionId": SUBMISSION_ID,
        "submittedAt": "2026-10-02T05:10:00+00:00", "clientVersion": "0.1",
        "skillId": "ai_agent_workflow", "skillName": "zhenxing-assistant",
        "baseDocument": original, "modifiedDocument": modified,
        "baseSha256": hashlib.sha256(original.encode("utf-8")).hexdigest(),
        "modifiedSha256": hashlib.sha256(modified.encode("utf-8")).hexdigest(),
        "changeSummary": "增加计划与验证步骤",
    }


def with_documents(original, modified):
    revision = sample_revision()
    revision.update(baseDocument=original, modifiedDocument=modified,
                    baseSha256=hashlib.sha256(original.encode("utf-8")).hexdigest(),
                    modifiedSha256=hashlib.sha256(modified.encode("utf-8")).hexdigest())
    return revision


def review(decision="accepted", note="确认采纳"):
    return {"decision": decision, "reviewNote": note, "expectedStatus": "pending"}


class RevisionValidationTests(unittest.TestCase):
    def test_preserves_original_snapshots_and_validates_raw_hashes(self):
        original = sample_revision()["baseDocument"].replace("\n", "\r\n")
        modified = sample_revision()["modifiedDocument"]
        revision = with_documents(original, modified)
        self.assertEqual(validate_revision(revision), revision)
        self.assertEqual(split_document(original, "baseDocument")[0], HEADER)
        revision["baseSha256"] = hashlib.sha256(original.replace("\r\n", "\n").encode()).hexdigest()
        with self.assertRaisesRegex(RevisionError, "baseSha256 does not match"):
            validate_revision(revision)

    def test_exact_fields_reject_client_review_metadata_and_aliases(self):
        for extra in ("status", "reviewNote", "reviewedAt", "receivedAt", "reviewer", "EditedBody"):
            with self.subTest(extra=extra):
                invalid = sample_revision()
                invalid[extra] = "accepted"
                with self.assertRaises(RevisionError):
                    validate_revision(invalid)
        for field in sample_revision():
            with self.subTest(field=field):
                invalid = sample_revision()
                invalid[field[0].upper() + field[1:]] = invalid.pop(field)
                with self.assertRaises(RevisionError):
                    validate_revision(invalid)

    def test_strict_types_null_and_lengths(self):
        for invalid in (None, [], True, "text"):
            with self.subTest(top=invalid), self.assertRaises(RevisionError):
                validate_revision(invalid)
        cases = {
            "schemaVersion": (True, False, None, 1.0, "1", 2),
            "submissionId": (None, True, [], "invalid", "00000000-0000-0000-0000-000000000000"),
            "submittedAt": (None, [], "2026-10-02T00:00:00", "2026-10-02T00:00:00+08:00", "2026-10-02"),
            "clientVersion": (None, [], 1, "", " \t", "x" * 81),
            "skillId": (None, [], "other"), "skillName": (None, {}, "other"),
            "baseDocument": (None, [], 1, ""), "modifiedDocument": (None, {}, True, ""),
            "baseSha256": (None, [], "F" * 64, "0" * 63, "0" * 64),
            "modifiedSha256": (None, {}, "G" * 64, "0" * 65, "0" * 64),
            "changeSummary": (None, [], 2, "", " \n\t", "x" * 2001, "\ud800"),
        }
        for field, values in cases.items():
            for invalid_value in values:
                with self.subTest(field=field, invalid=repr(invalid_value)):
                    invalid = sample_revision()
                    invalid[field] = invalid_value
                    with self.assertRaises(RevisionError):
                        validate_revision(invalid)
        valid = sample_revision()
        valid["clientVersion"] = "x" * 80
        valid["changeSummary"] = "好" * 2000
        self.assertEqual(validate_revision(valid), valid)
        valid["changeSummary"] = "😀" * 1001
        with self.assertRaises(RevisionError):
            validate_revision(valid)

    def test_header_identity_and_other_frontmatter_must_remain_unchanged(self):
        modified = sample_revision()["modifiedDocument"]
        variants = (
            modified.replace("name: zhenxing-assistant", "name: other"),
            modified.replace("description: 枕星助手", "description: different"),
            modified.replace("---\n先", "extra: value\n---\n先"),
        )
        for invalid in variants:
            with self.subTest(document=invalid), self.assertRaises(RevisionError):
                validate_revision(with_documents(sample_revision()["baseDocument"], invalid))
        for bad_header in (
            "name: zhenxing-assistant\n", "---\nname: zhenxing-assistant\n",
            "---\nname: other\n---\n", "\ufeff" + HEADER,
            HEADER.replace("description:", "name: other\ndescription:"),
            HEADER.replace("description:", "'name': other\ndescription:"),
            HEADER.replace("description:", "? name\n: other\ndescription:"),
            HEADER.replace("description:", '"na\\u006de": other\ndescription:'),
            HEADER.replace("description:", "<<: *other\ndescription:"),
        ):
            with self.subTest(header=bad_header), self.assertRaises(RevisionError):
                validate_revision(with_documents(bad_header + "old", bad_header + "new"))

    def test_empty_unchanged_and_only_newline_changes_are_rejected(self):
        original = sample_revision()["baseDocument"]
        for modified in (HEADER, HEADER + " \t\n", original, original.replace("\n", "\r\n")):
            with self.subTest(document=modified), self.assertRaises(RevisionError):
                validate_revision(with_documents(original, modified))

    def test_utf8_document_limit_and_surrogate_are_handled(self):
        header_size = len(HEADER.encode("utf-8"))
        maximum = HEADER + "x" * (MAX_DOCUMENT_BYTES - header_size)
        self.assertEqual(len(maximum.encode()), MAX_DOCUMENT_BYTES)
        self.assertEqual(validate_revision(with_documents(maximum, maximum[:-1] + "y"))["baseDocument"], maximum)
        for field in ("baseDocument", "modifiedDocument"):
            invalid = sample_revision()
            invalid[field] = maximum + "x"
            with self.subTest(field=field), self.assertRaisesRegex(RevisionError, "128 KiB"):
                validate_revision(invalid)
            invalid[field] = HEADER + "\ud800"
            with self.subTest(field=field), self.assertRaisesRegex(RevisionError, "Unicode"):
                validate_revision(invalid)
        # Byte limits, not Python code-point counts, apply to Chinese text.
        invalid = with_documents(HEADER + "汉" * (MAX_DOCUMENT_BYTES // 3), HEADER + "new")
        with self.assertRaises(RevisionError):
            validate_revision(invalid)

    def test_review_requires_exact_types_and_fields(self):
        self.assertEqual(validate_review(review(note="")), review(note=""))
        for invalid in (None, [], {}, {**review(), "status": "accepted"}):
            with self.subTest(top=invalid), self.assertRaises(RevisionError):
                validate_review(invalid)
        for field, values in {
            "decision": (None, [], True, "pending", "publish"),
            "expectedStatus": (None, [], "accepted", True),
            "reviewNote": (None, [], 5, "x" * 2001, "\ud800"),
        }.items():
            for value in values:
                with self.subTest(field=field, value=repr(value)), self.assertRaises(RevisionError):
                    validate_review({**review(), field: value})


class RevisionStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "test.sqlite3"
        self.parent = Store(self.path)
        self.store = self.parent.skill_revisions

    def tearDown(self):
        self.temp.cleanup()

    def test_frozen_idempotency_and_reopen(self):
        revision = validate_revision(sample_revision())
        receipt = self.store.save(revision)
        self.assertTrue(receipt["created"])
        repeated = self.store.save(deepcopy(revision))
        self.assertEqual(repeated, {**receipt, "created": False})
        changed = deepcopy(revision)
        changed["changeSummary"] += " 改动"
        with self.assertRaises(RevisionError) as caught:
            self.store.save(changed)
        self.assertEqual(caught.exception.status, 409)
        reopened = Store(self.path).skill_revisions
        detail = reopened.get(SUBMISSION_ID)
        for field, value in revision.items():
            self.assertEqual(detail[field], value)
        self.assertIn("-先了解目标，然后执行。", detail["diff"])
        self.assertIn("+先了解目标，列出计划", detail["diff"])
        self.assertEqual(len(reopened.list()["revisions"]), 1)
        self.assertEqual(self.parent.stats()["selectedFlows"], 0)
        with self.parent._connect() as db:
            row = db.execute("SELECT payload_json, payload_sha256 FROM skill_revisions").fetchone()
        self.assertEqual(row["payload_sha256"], hashlib.sha256(row["payload_json"].encode()).hexdigest())

    def test_decision_idempotency_conflicts_and_preservation(self):
        revision = validate_revision(sample_revision())
        receipt = self.store.save(revision)
        accepted = self.store.review(SUBMISSION_ID, review())
        self.assertEqual(accepted["status"], "accepted")
        self.assertEqual(self.store.review(SUBMISSION_ID, review()), accepted)
        self.assertEqual(self.store.save(revision), {**receipt, "created": False, "status": "accepted"})
        for changed_review in (review("rejected"), review(note="different")):
            with self.subTest(review=changed_review), self.assertRaises(RevisionError) as caught:
                self.store.review(SUBMISSION_ID, changed_review)
            self.assertEqual(caught.exception.status, 409)
        detail = self.store.get(SUBMISSION_ID)
        for field, value in revision.items():
            self.assertEqual(detail[field], value)
        self.assertEqual(detail["receivedAt"], receipt["receivedAt"])
        self.assertEqual(detail["reviewedAt"], accepted["reviewedAt"])
        self.assertEqual(Store(self.path).skill_revisions.get(SUBMISSION_ID)["reviewNote"], "确认采纳")

    def test_lists_filter_without_documents_and_unknown_ids(self):
        for number, state in enumerate(("pending", "accepted", "rejected"), start=1):
            revision = sample_revision()
            revision["submissionId"] = f"00000000-0000-4000-8000-{number:012x}"
            self.store.save(validate_revision(revision))
            if state != "pending":
                self.store.review(revision["submissionId"], review(state, ""))
            filtered = self.store.list(state)["revisions"]
            self.assertEqual(len(filtered), 1)
            self.assertEqual(filtered[0]["status"], state)
            self.assertNotIn("baseDocument", filtered[0])
            self.assertNotIn("modifiedDocument", filtered[0])
        self.assertEqual(len(self.store.list()["revisions"]), 3)
        with self.assertRaises(RevisionError):
            self.store.list("publish")
        for action in (lambda: self.store.get(str(uuid4())), lambda: self.store.review(str(uuid4()), review())):
            with self.assertRaises(RevisionError) as caught:
                action()
            self.assertEqual(caught.exception.status, 404)

    def test_concurrent_same_submission_creates_one_row(self):
        barrier = threading.Barrier(2)
        def submit():
            barrier.wait(timeout=2)
            return self.store.save(validate_revision(sample_revision()))
        with ThreadPoolExecutor(max_workers=2) as executor:
            receipts = list(executor.map(lambda _: submit(), range(2)))
        self.assertEqual(sorted(receipt["created"] for receipt in receipts), [False, True])
        self.assertEqual(receipts[0]["receivedAt"], receipts[1]["receivedAt"])
        self.assertEqual(len(self.store.list()["revisions"]), 1)

    def test_concurrent_opposite_decisions_have_one_winner(self):
        self.store.save(validate_revision(sample_revision()))
        barrier = threading.Barrier(2)
        def decide(decision):
            barrier.wait(timeout=2)
            try:
                return 200, self.store.review(SUBMISSION_ID, review(decision))
            except RevisionError as error:
                return error.status, None
        with ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(decide, ("accepted", "rejected")))
        self.assertEqual(sorted(result[0] for result in results), [200, 409])
        winner = next(result[1] for result in results if result[0] == 200)
        detail = self.store.get(SUBMISSION_ID)
        self.assertEqual(detail["status"], winner["status"])
        self.assertEqual(detail["reviewedAt"], winner["reviewedAt"])

    def test_resubmission_racing_with_review_preserves_frozen_content(self):
        revision = validate_revision(sample_revision())
        self.store.save(revision)
        barrier = threading.Barrier(2)
        def submit():
            barrier.wait(timeout=2)
            return self.store.save(revision)
        def decide():
            barrier.wait(timeout=2)
            return self.store.review(SUBMISSION_ID, review())
        with ThreadPoolExecutor(max_workers=2) as executor:
            submit_result = executor.submit(submit)
            review_result = executor.submit(decide)
            receipt, accepted = submit_result.result(), review_result.result()
        self.assertIn(receipt["status"], ("pending", "accepted"))
        self.assertFalse(receipt["created"])
        detail = self.store.get(SUBMISSION_ID)
        self.assertEqual(detail["status"], accepted["status"])
        self.assertEqual(detail["modifiedDocument"], revision["modifiedDocument"])


class RevisionHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.temp.name) / "test.sqlite3")
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(self.store, admin_token="synthetic-admin-token"))
        self.thread = threading.Thread(target=lambda: self.server.serve_forever(poll_interval=0.01), daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_port}"
        self.detail_path = ADMIN_LIST_PATH + "/" + SUBMISSION_ID

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.temp.cleanup()

    def request(self, method, path, body=None, token=None, raw=None, content_type="application/json"):
        payload = raw if raw is not None else None if body is None else json.dumps(body).encode("utf-8")
        headers = {"Content-Type": content_type} if payload is not None else {}
        if token is not None:
            headers["Authorization"] = "Bearer " + token
        request = Request(self.base + path, data=payload, method=method, headers=headers)
        try:
            with urlopen(request, timeout=3) as response:
                data = response.read()
                return response.status, (json.loads(data) if response.headers.get_content_type() == "application/json" else data.decode())
        except HTTPError as error:
            return error.code, json.load(error)

    def test_public_post_receipts_and_private_reviews(self):
        status, first = self.request("POST", INTAKE_PATH, sample_revision())
        self.assertEqual(status, 201)
        self.assertEqual(first["submissionId"], SUBMISSION_ID)
        self.assertEqual(first["modifiedSha256"], sample_revision()["modifiedSha256"])
        self.assertEqual(first["status"], "pending")
        self.assertEqual(self.request("POST", INTAKE_PATH, sample_revision()), (200, {**first, "created": False}))
        status, decision = self.request("POST", self.detail_path + "/review", review(), token="synthetic-admin-token")
        self.assertEqual(status, 200)
        self.assertEqual(decision["status"], "accepted")
        self.assertEqual(self.request("POST", INTAKE_PATH, sample_revision())[1]["status"], "accepted")
        self.assertEqual(self.request("POST", self.detail_path + "/review", review(), token="synthetic-admin-token"), (200, decision))
        self.assertEqual(self.request("POST", self.detail_path + "/review", review("rejected"), token="synthetic-admin-token")[0], 409)
        self.assertEqual(self.request("GET", INTAKE_PATH + "/" + SUBMISSION_ID)[0], 404)

    def test_private_list_detail_and_decisions_require_token_before_body(self):
        self.request("POST", INTAKE_PATH, sample_revision())
        for method, path in (("GET", ADMIN_LIST_PATH), ("GET", self.detail_path),
                             ("POST", self.detail_path + "/review")):
            with self.subTest(method=method, path=path):
                # Deliberately malformed JSON must never bypass authentication.
                raw = b"{" if method == "POST" else None
                self.assertEqual(self.request(method, path, raw=raw)[0], 401)
                self.assertEqual(self.request(method, path, raw=raw, token="wrong")[0], 403)
                self.assertEqual(self.request(method, path, raw=raw, token="wrong-é")[0], 403)
        self.assertEqual(self.store.skill_revisions.get(SUBMISSION_ID)["status"], "pending")

    def test_admin_access_is_disabled_without_configuration(self):
        disabled = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(self.store))
        thread = threading.Thread(target=lambda: disabled.serve_forever(poll_interval=0.01), daemon=True)
        thread.start()
        original_base = self.base
        self.base = f"http://127.0.0.1:{disabled.server_port}"
        try:
            for method, path in (("GET", ADMIN_LIST_PATH), ("GET", self.detail_path),
                                 ("POST", self.detail_path + "/review")):
                self.assertEqual(self.request(method, path, token="synthetic-admin-token")[0], 403)
        finally:
            self.base = original_base
            disabled.shutdown()
            disabled.server_close()
            thread.join(timeout=2)

    def test_malformed_duplicate_keys_content_type_and_body_size(self):
        for raw in (b"{", b"null", b"[]", b'{"schemaVersion":1,"schemaVersion":1}'):
            with self.subTest(raw=raw):
                self.assertEqual(self.request("POST", INTAKE_PATH, raw=raw)[0], 400)
        self.assertEqual(self.request("POST", INTAKE_PATH, sample_revision(), content_type="text/plain")[0], 415)
        self.assertEqual(self.request("POST", INTAKE_PATH, raw=b" " * (MAX_REVISION_BODY_BYTES + 1))[0], 413)
        invalid = sample_revision()
        invalid["modifiedDocument"] = HEADER + "\ud800"
        self.assertEqual(self.request("POST", INTAKE_PATH, invalid)[0], 400)
        invalid = sample_revision()
        invalid["status"] = "accepted"
        self.assertEqual(self.request("POST", INTAKE_PATH, invalid)[0], 400)
        self.assertEqual(len(self.store.skill_revisions.list()["revisions"]), 0)

    def test_maximum_chinese_documents_accept_default_json_escaping(self):
        count = (MAX_DOCUMENT_BYTES - len(HEADER.encode())) // 3
        revision = with_documents(HEADER + "汉" * count, HEADER + "好" * count)
        payload = json.dumps(revision, ensure_ascii=True).encode()
        self.assertGreater(len(payload), 256 * 1024)
        self.assertLess(len(payload), MAX_REVISION_BODY_BYTES)
        self.assertEqual(self.request("POST", INTAKE_PATH, raw=payload)[0], 201)

    def test_status_filter_and_query_validation(self):
        self.request("POST", INTAKE_PATH, sample_revision())
        self.assertEqual(len(self.request("GET", ADMIN_LIST_PATH + "?status=pending", token="synthetic-admin-token")[1]["revisions"]), 1)
        self.assertEqual(self.request("GET", ADMIN_LIST_PATH + "?status=accepted", token="synthetic-admin-token")[1], {"revisions": []})
        for query in ("status=", "status=publish", "status=pending&status=accepted", "other=pending", "status"):
            with self.subTest(query=query):
                self.assertEqual(self.request("GET", ADMIN_LIST_PATH + "?" + query, token="synthetic-admin-token")[0], 400)
        self.assertEqual(self.request("GET", ADMIN_LIST_PATH + "?other=x")[0], 401)

    def test_unknown_ids_and_conflicting_posts(self):
        unknown = ADMIN_LIST_PATH + "/" + str(uuid4())
        self.assertEqual(self.request("GET", unknown, token="synthetic-admin-token")[0], 404)
        self.assertEqual(self.request("POST", unknown + "/review", review(), token="synthetic-admin-token")[0], 404)
        self.request("POST", INTAKE_PATH, sample_revision())
        changed = sample_revision()
        changed["changeSummary"] += "new"
        self.assertEqual(self.request("POST", INTAKE_PATH, changed)[0], 409)

    def test_untrusted_text_stays_literal_and_admin_shell_has_no_user_data(self):
        attack = '</pre><img src=x onerror="alert(1)"></script><script>alert(2)</script><svg onload="alert(3)">javascript:alert(4)'
        revision = with_documents(HEADER + "original", HEADER + attack)
        revision["changeSummary"] = attack
        self.request("POST", INTAKE_PATH, revision)
        status, detail = self.request("GET", self.detail_path, token="synthetic-admin-token")
        self.assertEqual(status, 200)
        self.assertEqual(detail["modifiedDocument"], HEADER + attack)
        self.assertIn(attack, detail["diff"])
        listed = self.request("GET", ADMIN_LIST_PATH, token="synthetic-admin-token")[1]
        self.assertEqual(listed["revisions"][0]["changeSummary"], attack)
        self.request("POST", self.detail_path + "/review", review(note=attack), token="synthetic-admin-token")
        self.assertEqual(self.request("GET", self.detail_path, token="synthetic-admin-token")[1]["reviewNote"], attack)
        status, shell = self.request("GET", "/admin")
        self.assertEqual(status, 200)
        self.assertNotIn(attack, shell)
        self.assertIn("采纳只记录审核结果", shell)
        self.assertNotIn("innerHTML", shell)
        self.assertNotIn("insertAdjacentHTML", shell)
        self.assertNotIn("document.write", shell)
        self.assertNotIn("localStorage", shell)
        for element, field in (("skillBase", "baseDocument"), ("skillModified", "modifiedDocument"),
                               ("skillSummary", "changeSummary"), ("skillDiff", "diff")):
            self.assertIn(f"document.getElementById('{element}').textContent = revision.{field}", SKILL_ADMIN_SCRIPT)
        class ShellParser(HTMLParser):
            def __init__(self):
                super().__init__()
                self.ids = []
            def handle_starttag(self, tag, attrs):
                self.ids.extend(value for name, value in attrs if name == "id")
        parser = ShellParser()
        parser.feed(shell)
        self.assertEqual(len(parser.ids), len(set(parser.ids)))


if __name__ == "__main__":
    unittest.main()
