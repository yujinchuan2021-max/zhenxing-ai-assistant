from __future__ import annotations

from contextlib import closing
from copy import deepcopy
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from link_probe import LinkCheck, UnsafeLink, check_download_link, normalize_public_https_url, resolve_public_addresses
from server import ApiError, Store, make_handler, validate_event, validate_flow, validate_metrics


FLOW_ID = "a39c67d8-dfe5-4ef1-8490-e9bd9b45a9f5"
SUBMISSION_ID = "de2d9b27-1526-465f-8998-73bf8d491d6c"
ITEM_ID = "139e5758-0595-4256-86e0-37d19aecc527"
EVENT_ID = "bcb48281-dd28-4181-b777-eebf428c6a24"


def sample_flow():
    return {
        "schemaVersion": 1,
        "submissionId": SUBMISSION_ID,
        "flowId": FLOW_ID,
        "origin": "assistant",
        "selectedAt": "2026-09-24T09:00:00Z",
        "flowName": "Godot 2D 游戏创作",
        "projectGoal": "制作一款可在 Windows 发布的 2D 像素游戏",
        "goalDescription": "做一款 2D 游戏",
        "flowText": "先用 Godot 建项目。\n再用选定的 AI Agent 辅助写脚本。",
        "conversation": [
            {"role": "user", "content": "我想做游戏", "at": None},
            {"role": "assistant", "content": "建议先确定平台。", "at": "2026-09-24T08:55:00Z"},
        ],
        "items": [{
            "itemId": ITEM_ID,
            "name": "Godot",
            "kind": "development",
            "version": "4.x",
            "sourceUrl": "https://godotengine.org/",
            "downloadUrl": "https://godotengine.org/download/windows/",
            "installTargetKey": "godot",
        }],
    }


def sample_event(kind="download_succeeded"):
    return {
        "eventId": EVENT_ID,
        "itemId": ITEM_ID,
        "kind": kind,
        "at": "2026-09-24T09:10:00Z",
        "detail": None,
    }


METRICS_BATCH_ID = "5f4b7e56-2f2f-4d1c-8c6d-1e2b3a4c5d6e"


def sample_metrics():
    return {
        "schemaVersion": 1,
        "batchId": METRICS_BATCH_ID,
        "counts": [
            {"kind": "download_clicked", "tool": "CPU-Z", "count": 3},
            {"kind": "download_requested", "tool": "CPU-Z", "count": 1},
            {"kind": "download_clicked", "tool": "GPU-Z", "count": 1},
        ],
    }


class SchemaTests(unittest.TestCase):
    def test_preserves_flow_text_and_unknown_message_time(self):
        clean = validate_flow(sample_flow())
        self.assertEqual(clean["flowName"], sample_flow()["flowName"])
        self.assertEqual(clean["projectGoal"], sample_flow()["projectGoal"])
        self.assertEqual(clean["flowText"], sample_flow()["flowText"])
        self.assertIsNone(clean["conversation"][0]["at"])

    def test_requires_bounded_flow_name_and_project_goal(self):
        for field, invalid in (("flowName", None), ("flowName", " "),
                               ("flowName", "x" * 121), ("projectGoal", ""),
                               ("projectGoal", "x" * 65537)):
            with self.subTest(field=field, invalid=invalid):
                flow = sample_flow()
                flow[field] = invalid
                with self.assertRaisesRegex(ApiError, f"invalid {field}"):
                    validate_flow(flow)
        for field in ("flowName", "projectGoal"):
            with self.subTest(missing=field):
                flow = sample_flow()
                del flow[field]
                with self.assertRaises(ApiError):
                    validate_flow(flow)

    def test_rejects_unselected_or_overlarge_payload(self):
        flow = sample_flow()
        flow["items"] = []
        with self.assertRaises(ApiError):
            validate_flow(flow)
        flow = sample_flow()
        flow["flowText"] = "x" * 65537
        with self.assertRaises(ApiError):
            validate_flow(flow)

    def test_rejects_wrong_event_kind_and_reference_shape(self):
        event = sample_event()
        event["kind"] = ["install_succeeded"]
        with self.assertRaises(ApiError):
            validate_event(event, FLOW_ID)

    def test_metrics_validation_bounds(self):
        clean = validate_metrics(sample_metrics())
        self.assertEqual(clean["counts"][0], {"kind": "download_clicked", "tool": "CPU-Z", "count": 3})

        batch = sample_metrics()
        batch["counts"][0]["kind"] = "clicked_download"
        with self.assertRaises(ApiError):
            validate_metrics(batch)

        batch = sample_metrics()
        batch["counts"][2] = {"kind": "download_clicked", "tool": "CPU-Z", "count": 2}
        with self.assertRaises(ApiError):
            validate_metrics(batch)

        batch = sample_metrics()
        batch["counts"][0]["count"] = 0
        with self.assertRaises(ApiError):
            validate_metrics(batch)

        batch = sample_metrics()
        batch["counts"][0]["tool"] = "x" * 121
        with self.assertRaises(ApiError):
            validate_metrics(batch)

        batch = sample_metrics()
        batch["batchId"] = "not-a-uuid"
        with self.assertRaises(ApiError):
            validate_metrics(batch)

        batch = sample_metrics()
        batch["extra"] = 1
        with self.assertRaises(ApiError):
            validate_metrics(batch)


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.temp.name) / "test.sqlite3")

    def tearDown(self):
        self.temp.cleanup()

    def test_idempotency_conflicts_and_statistics(self):
        flow = validate_flow(sample_flow())
        self.assertTrue(self.store.save_flow(flow))
        self.assertFalse(self.store.save_flow(flow))
        changed = deepcopy(flow)
        changed["flowText"] = "different plan"
        with self.assertRaisesRegex(ApiError, "already used"):
            self.store.save_flow(changed)

        event = validate_event(sample_event(), FLOW_ID)
        self.assertTrue(self.store.save_event(event))
        self.assertFalse(self.store.save_event(event))
        self.assertEqual(self.store.stats()["selectedFlows"], 1)
        self.assertEqual(self.store.stats()["eventsByKind"]["download_succeeded"], 1)
        self.assertEqual(self.store.stats()["itemsBySourceHost"]["godotengine.org"], 1)
        self.assertEqual(self.store.stats()["versions"][0]["version"], "4.x")
        self.assertEqual(self.store.stats()["itemActivity"][0]["downloadSucceeded"], 1)
        self.assertEqual(self.store.stats()["topFlowNames"],
                         [{"flowName": flow["flowName"], "selectedCount": 1}])
        reopened = Store(Path(self.temp.name) / "test.sqlite3")
        self.assertEqual(reopened.get_flow(FLOW_ID)["flowText"], flow["flowText"])
        self.assertEqual(reopened.get_flow(FLOW_ID)["projectGoal"], flow["projectGoal"])
        self.assertEqual(reopened.stats()["eventsByKind"]["download_succeeded"], 1)

    def test_flow_names_group_exact_submitted_text_without_exposing_project_goals(self):
        for number, name in enumerate(("游戏开发", "游戏开发", "游戏开发 "), start=1):
            flow = sample_flow()
            flow["flowId"] = f"00000000-0000-4000-8000-{number:012x}"
            flow["submissionId"] = f"10000000-0000-4000-8000-{number:012x}"
            flow["items"][0]["itemId"] = f"20000000-0000-4000-8000-{number:012x}"
            flow["flowName"] = name
            flow["projectGoal"] = f"只存在于单条记录的项目需求 {number}"
            self.store.save_flow(validate_flow(flow))
        stats = self.store.stats()
        self.assertEqual(stats["topFlowNames"], [
            {"flowName": "游戏开发", "selectedCount": 2},
            {"flowName": "游戏开发 ", "selectedCount": 1},
        ])
        self.assertNotIn("只存在于单条记录", json.dumps(stats, ensure_ascii=False))
        self.assertNotIn("conversation", json.dumps(stats, ensure_ascii=False))

    def test_reopens_earlier_local_database_without_dropping_flows(self):
        path = Path(self.temp.name) / "earlier.sqlite3"
        with closing(sqlite3.connect(path)) as db, db:
            db.execute("""CREATE TABLE flows (
                flow_id TEXT PRIMARY KEY, submission_id TEXT UNIQUE NOT NULL,
                origin TEXT NOT NULL, selected_at TEXT NOT NULL,
                goal_description TEXT NOT NULL, payload_json TEXT NOT NULL,
                received_at TEXT NOT NULL)""")
            db.execute("""INSERT INTO flows VALUES (?, ?, ?, ?, ?, ?, ?)""",
                       (FLOW_ID, SUBMISSION_ID, "assistant", "2026-09-24T09:00:00Z",
                        "旧项目", json.dumps({"flowId": FLOW_ID}), "2026-09-24T09:00:01Z"))
        reopened = Store(path)
        self.assertEqual(reopened.get_flow(FLOW_ID)["flowId"], FLOW_ID)
        self.assertEqual(reopened.stats()["selectedFlows"], 1)
        self.assertEqual(reopened.stats()["topFlowNames"], [])
        new_flow = sample_flow()
        new_flow["flowId"] = "00000000-0000-4000-8000-000000000002"
        new_flow["submissionId"] = "10000000-0000-4000-8000-000000000002"
        new_flow["items"][0]["itemId"] = "20000000-0000-4000-8000-000000000002"
        self.assertTrue(reopened.save_flow(validate_flow(new_flow)))
        self.assertEqual(reopened.stats()["topFlowNames"][0]["flowName"], new_flow["flowName"])

    def test_event_requires_selected_item(self):
        self.store.save_flow(validate_flow(sample_flow()))
        event = validate_event(sample_event(), FLOW_ID)
        event["itemId"] = "7bd456f5-1214-4c16-b238-3aa3e7b8e33a"
        with self.assertRaises(ApiError) as caught:
            self.store.save_event(event)
        self.assertEqual(caught.exception.status, 404)

    def test_metrics_persist_dedupe_and_conflict(self):
        batch = validate_metrics(sample_metrics())
        self.assertTrue(self.store.save_metrics(batch))
        self.assertFalse(self.store.save_metrics(batch))
        changed = deepcopy(batch)
        changed["counts"][0]["count"] = 4
        with self.assertRaisesRegex(ApiError, "already used"):
            self.store.save_metrics(changed)
        reopened = Store(Path(self.temp.name) / "test.sqlite3")
        metrics = reopened.stats()["downloadMetrics"]
        self.assertEqual(metrics["byKind"]["download_clicked"], 4)
        self.assertEqual(metrics["byKind"]["download_requested"], 1)
        self.assertEqual(metrics["topTools"][0]["tool"], "CPU-Z")
        self.assertEqual(metrics["topTools"][0]["downloadClicked"], 3)
        self.assertEqual(metrics["topTools"][0]["downloadRequested"], 1)

    def test_link_check_is_explicit_and_recorded(self):
        self.store.save_flow(validate_flow(sample_flow()))
        self.assertEqual(self.store.stats()["linkStatusCounts"], {"unchecked": 1})
        called = []
        result = self.store.check_link(FLOW_ID, ITEM_ID,
                                       lambda url: called.append(url) or LinkCheck("available", 200))
        self.assertEqual(called, ["https://godotengine.org/download/windows/"])
        self.assertEqual(result["status"], "available")
        self.assertEqual(self.store.stats()["linkStatusCounts"], {"available": 1})


class LinkSafetyTests(unittest.TestCase):
    def test_rejects_local_or_non_https_destinations(self):
        for value in (
            "http://example.org/file", "https://127.0.0.1/file", "https://[::1]/file",
            "https://localhost/file", "https://host.internal/file", "https://example.org:8443/file",
            "https://user:pass@example.org/file", "https://example.org/file#fragment",
            "https://example.org/\ud800",
        ):
            with self.subTest(value=value), self.assertRaises(UnsafeLink):
                normalize_public_https_url(value)

    def test_rejects_any_private_dns_answer(self):
        def mixed_resolver(host, port, type):
            return [(2, 1, 6, "", ("8.8.8.8", 443)),
                    (2, 1, 6, "", ("127.0.0.1", 443))]
        with self.assertRaises(UnsafeLink):
            resolve_public_addresses("example.org", mixed_resolver)

    def test_public_dns_result_is_pinned_and_redirect_not_followed(self):
        calls = []

        class FakeConnection:
            def __init__(self, host, address, timeout):
                calls.append(("connect", host, address, timeout))

            def request(self, method, target, headers):
                calls.append(("request", method, target))

            def getresponse(self):
                return type("Response", (), {"status": 302})()

            def close(self):
                pass

        resolver = lambda host, port, type: [(2, 1, 6, "", ("8.8.8.8", 443))]
        with patch("link_probe._PinnedHTTPSConnection", FakeConnection):
            result = check_download_link("https://example.org/file", resolver)
        self.assertEqual(result, LinkCheck("redirect_unchecked", 302))
        self.assertEqual(calls[0][2], "8.8.8.8")
        self.assertEqual(calls[1], ("request", "HEAD", "/file"))


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.temp.name) / "test.sqlite3")
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(
            self.store, lambda _: LinkCheck("available", 200), admin_token="test-admin-token"))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.temp.cleanup()

    def request(self, method, path, body=None, token="test-admin-token"):
        payload = None if body is None else json.dumps(body).encode("utf-8")
        headers: dict[str, str] = {}
        if payload:
            headers["Content-Type"] = "application/json"
        if token is not None:
            headers["Authorization"] = "Bearer " + token
        request = Request(self.base + path, data=payload, method=method, headers=headers)
        try:
            with urlopen(request, timeout=2) as response:
                return response.status, json.load(response)
        except HTTPError as error:
            return error.code, json.load(error)

    def test_end_to_end_local_http(self):
        self.assertEqual(self.request("GET", "/health"), (200, {"status": "ok"}))
        self.assertEqual(self.request("POST", "/v1/toolflows", sample_flow())[0], 201)
        self.assertEqual(self.request("POST", "/v1/toolflows", sample_flow())[0], 200)
        self.assertEqual(self.request("POST", f"/v1/toolflows/{FLOW_ID}/events", sample_event())[0], 201)
        self.assertEqual(self.request("POST", "/v1/links/check", {"flowId": FLOW_ID, "itemId": ITEM_ID})[1]["status"], "available")
        flow = self.request("GET", f"/v1/toolflows/{FLOW_ID}")[1]
        self.assertEqual(flow["flowName"], sample_flow()["flowName"])
        self.assertEqual(flow["projectGoal"], sample_flow()["projectGoal"])
        self.assertEqual(flow["flowText"], sample_flow()["flowText"])
        self.assertIsNone(flow["conversation"][0]["at"])
        self.assertEqual(flow["linkChecks"][ITEM_ID]["status"], "available")
        stats = self.request("GET", "/v1/stats")[1]
        self.assertEqual(stats["eventsByKind"]["download_succeeded"], 1)
        self.assertEqual(stats["topFlowNames"],
                         [{"flowName": sample_flow()["flowName"], "selectedCount": 1}])
        self.assertNotIn("projectGoal", stats)
        self.assertNotIn("conversation", stats)

    def test_admin_reads_require_token(self):
        self.request("POST", "/v1/toolflows", sample_flow())
        for path in ("/v1/stats", f"/v1/toolflows/{FLOW_ID}", "/v1/admin/flows"):
            with self.subTest(path=path):
                self.assertEqual(self.request("GET", path, token=None)[0], 401)
                self.assertEqual(self.request("GET", path, token="wrong-token")[0], 403)
                self.assertEqual(self.request("GET", path)[0], 200)
        self.assertEqual(self.request("GET", "/v1/admin/export.jsonl", token=None)[0], 401)
        self.assertEqual(self.request("GET", "/v1/admin/export.jsonl", token="wrong-token")[0], 403)

    def test_admin_endpoints_disabled_without_token(self):
        server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(self.store))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            request = Request(f"http://127.0.0.1:{server.server_port}/v1/stats", method="GET")
            with self.assertRaises(HTTPError) as caught:
                urlopen(request, timeout=2)
            self.assertEqual(caught.exception.code, 403)
            self.assertEqual(json.load(caught.exception)["error"],
                             "admin access is disabled; restart with --admin-token to enable it")
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_admin_flow_list_summaries(self):
        self.request("POST", "/v1/toolflows", sample_flow())
        self.request("POST", f"/v1/toolflows/{FLOW_ID}/events", sample_event())
        flows = self.request("GET", "/v1/admin/flows")[1]["flows"]
        self.assertEqual(len(flows), 1)
        self.assertEqual(flows[0]["flowName"], sample_flow()["flowName"])
        self.assertEqual(flows[0]["itemCount"], 1)
        self.assertEqual(flows[0]["eventCount"], 1)
        self.assertNotIn("conversation", json.dumps(flows, ensure_ascii=False))

    def test_metrics_intake_dedupe_and_stats(self):
        self.assertEqual(self.request("POST", "/v1/metrics", sample_metrics())[0], 201)
        self.assertEqual(self.request("POST", "/v1/metrics", sample_metrics())[0], 200)
        changed = deepcopy(sample_metrics())
        changed["counts"][0]["count"] = 9
        self.assertEqual(self.request("POST", "/v1/metrics", changed)[0], 409)
        stats = self.request("GET", "/v1/stats")[1]
        self.assertEqual(stats["downloadMetrics"]["byKind"]["download_clicked"], 4)
        self.assertEqual(stats["downloadMetrics"]["byKind"]["download_requested"], 1)
        tools = {tool["tool"]: tool for tool in stats["downloadMetrics"]["topTools"]}
        self.assertEqual(tools["CPU-Z"]["downloadClicked"], 3)
        self.assertEqual(tools["GPU-Z"]["downloadClicked"], 1)

    def test_export_contains_raw_flows_events_and_metrics(self):
        self.request("POST", "/v1/toolflows", sample_flow())
        self.request("POST", f"/v1/toolflows/{FLOW_ID}/events", sample_event())
        self.request("POST", "/v1/metrics", sample_metrics())
        request = Request(self.base + "/v1/admin/export.jsonl", method="GET",
                          headers={"Authorization": "Bearer test-admin-token"})
        with urlopen(request, timeout=2) as response:
            self.assertEqual(response.status, 200)
            text = response.read().decode("utf-8")
        lines = [json.loads(line) for line in text.splitlines()]
        self.assertEqual([line["type"] for line in lines], ["flow", "event", "metrics"])
        self.assertEqual(lines[0]["payload"]["conversation"][0]["content"], "我想做游戏")
        self.assertEqual(lines[2]["payload"]["batchId"], METRICS_BATCH_ID)
        self.assertEqual(lines[2]["payload"]["counts"][0]["tool"], "CPU-Z")


if __name__ == "__main__":
    unittest.main()
