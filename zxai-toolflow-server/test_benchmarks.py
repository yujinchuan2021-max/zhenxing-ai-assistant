import base64
import copy
import json
from pathlib import Path
import struct
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from http.server import ThreadingHTTPServer
from uuid import uuid4
import zlib

from benchmarks import BenchmarkError, BOARDS, HARDWARE, SCORES, png, validate
from server import Store, make_handler

OWNER = "a" * 64
OTHER = "b" * 64
ADMIN = "synthetic-benchmark-admin-token"


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)


def image():
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(b"\x00\x00\xff\x00\xff")) + chunk(b"IEND", b"")


def sample():
    report = {key: "Synthetic hardware" for key in HARDWARE}
    report.update({key: 150 for key in SCORES})
    report["winFinalScore"] = 250
    return {"schemaVersion": 1, "reportId": str(uuid4()), "kind": "benchmark", "clientVersion": "synthetic",
            "scoreVersion": "zxai-performance/1", "testTime": "2026-10-04T00:00:00Z", "durationMode": "standard",
            "report": report, "heatmapBase64": base64.b64encode(image()).decode("ascii")}


class BenchmarkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.temp.name) / "synthetic.sqlite3").benchmarks

    def tearDown(self):
        self.temp.cleanup()

    def submit(self, payload=None):
        payload = payload or sample()
        self.store.submit(validate(payload), OWNER)
        return payload

    def accept(self, payload):
        return self.store.review(payload["reportId"], {"decision": "accepted", "note": "synthetic review"})

    def test_pending_private_then_public_with_win_score(self):
        p = self.submit()
        self.assertEqual(self.store.leaderboard()["totalEntries"], 0)
        self.assertEqual(self.store.reports()["totalEntries"], 0)
        with self.assertRaises(BenchmarkError): self.store.detail(p["reportId"])
        mine = self.store.mine(OWNER)["reports"][0]
        self.assertEqual((mine["status"], mine["winFinalScore"]), ("pending", 250))
        self.assertFalse(mine["scoresVerified"])
        self.assertNotIn(OWNER, json.dumps(mine))
        self.accept(p)
        self.assertEqual(self.store.leaderboard("win")["entries"][0]["winFinalScore"], 250)
        self.assertEqual(self.store.image(p["reportId"]), image())

    def test_idempotency_and_foreign_ownership(self):
        p = self.submit()
        self.assertFalse(self.store.submit(validate(p), OWNER)["created"])
        for payload, owner in ((p, OTHER), ({**p, "durationMode": "other"}, OWNER)):
            with self.assertRaises(BenchmarkError) as error: self.store.submit(validate(payload), owner)
            self.assertEqual(error.exception.status, 409)

    def test_mine_excludes_other_owner(self):
        self.submit()
        self.assertEqual(self.store.mine(OTHER), {"reports": []})

    def test_withdraw_ownership_idempotency_and_no_revival(self):
        p = self.submit(); self.accept(p)
        with self.assertRaises(BenchmarkError): self.store.withdraw(p["reportId"], OTHER)
        self.store.withdraw(p["reportId"], OWNER); self.store.withdraw(p["reportId"], OWNER)
        self.assertEqual(self.store.reports()["totalEntries"], 0)
        self.assertEqual(self.store.images(), {"images": []})
        self.assertEqual(self.store.submit(validate(p), OWNER)["status"], "withdrawn")
        with self.assertRaises(BenchmarkError): self.accept(p)

    def test_review_retry_preserves_timestamp_and_reject_conflict(self):
        p = self.submit()
        first = self.accept(p)
        self.assertEqual(first, self.accept(p))
        with self.assertRaises(BenchmarkError): self.store.review(p["reportId"], {"decision": "rejected", "note": ""})

    def test_partial_excluded_from_composite_but_component_included(self):
        for missing in SCORES[:-1]:
            with self.subTest(missing=missing):
                p = sample(); p["report"][missing] = 0
                self.submit(p); self.accept(p)
        self.assertEqual(self.store.leaderboard("gaming")["totalEntries"], 0)
        self.assertEqual(self.store.leaderboard("office")["totalEntries"], 0)
        self.assertGreater(self.store.leaderboard("win")["totalEntries"], 0)

    def test_weighted_boundary_matches_csharp(self):
        for scores, key, expected in (([421, 905, 1484, 254, 776, 1262, 191, 802, 319], "gamingScore", 861),
                                     ([621, 4, 29, 1365, 190, 1241, 1144, 578, 157], "officeScore", 550)):
            p = sample(); p["report"].update(zip(SCORES[:-1], scores))
            self.submit(p); self.assertEqual(self.store.detail(p["reportId"], OWNER)[key], expected)

    def test_sorted_pagination_and_server_filter(self):
        for i in range(55):
            p = sample(); p["report"]["cpuName"] = "Test CPU " + str(i)
            p["report"]["cpuMultiCoreScore"] = i + 1
            self.submit(p); self.accept(p)
        first, second = self.store.leaderboard("cpu"), self.store.leaderboard("cpu", 1)
        self.assertEqual((len(first["entries"]), len(second["entries"])), (50, 5))
        self.assertEqual(second["entries"][0]["rank"], 51)
        self.assertEqual(self.store.leaderboard("cpu", cpu="Test CPU 54")["totalEntries"], 1)

    def test_reject_extra_fields_bool_score_and_negative(self):
        for mutate in (lambda p: p.update(apiKey="synthetic"), lambda p: p.update(schemaVersion=True),
                       lambda p: p["report"].update(winFinalScore=True), lambda p: p["report"].update(cpuMultiCoreScore=-1),
                       lambda p: p.update(testTime="2026-10-04T08:00:00+08:00")):
            p = sample(); mutate(p)
            with self.assertRaises(BenchmarkError): validate(p)

    def test_png_rejects_missing_pixels_invalid_header_and_crc(self):
        self.assertEqual(png(base64.b64encode(image()).decode()), image())
        for data in (image()[:-1], image() + b"x", image()[:44] + b"bad",
                     image()[:33] + chunk(b"tEXt", b"text") + chunk(b"IEND", b""),
                     b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 7, 6, 0, 0, 0)) + chunk(b"IDAT", b"x") + chunk(b"IEND", b"")):
            with self.assertRaises(BenchmarkError): png(base64.b64encode(data).decode())

    def test_admin_can_preview_pending_image_without_public_exposure(self):
        p = self.submit()
        with self.assertRaises(BenchmarkError): self.store.image(p["reportId"])
        self.assertEqual(self.store.image(p["reportId"], admin=True), image())

    def test_latency_only_does_not_enter_reports_or_scores(self):
        p = sample(); p["kind"] = "latency"; p["report"].update({key: 0 for key in SCORES})
        self.submit(p); self.accept(p)
        self.assertEqual(self.store.reports()["totalEntries"], 0)
        self.assertEqual(self.store.leaderboard("win")["totalEntries"], 0)
        self.assertEqual(len(self.store.images()["images"]), 1)


class BenchmarkHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.temp.name) / "http-synthetic.sqlite3")
        handler = make_handler(self.store, admin_token=ADMIN)
        handler.log_message = lambda *args: None
        self.http = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        self.thread = threading.Thread(target=self.http.serve_forever, daemon=True); self.thread.start()
        self.base = "http://127.0.0.1:" + str(self.http.server_address[1])

    def tearDown(self):
        self.http.shutdown(); self.http.server_close(); self.thread.join(); self.temp.cleanup()

    def request(self, path, method="GET", payload=None, headers=None):
        data = None if payload is None else json.dumps(payload).encode()
        request = Request(self.base + path, data=data, method=method,
                          headers={"Content-Type": "application/json", **(headers or {})})
        try: response = urlopen(request, timeout=3)
        except HTTPError as error: response = error
        with response: return response.status, response.read()

    def test_end_to_end_headers_moderation_reads_and_withdraw(self):
        p = sample(); path = "/v1/benchmarks/" + p["reportId"]
        self.assertEqual(self.request("/v1/benchmarks", "POST", p)[0], 401)
        self.assertEqual(self.request("/v1/benchmarks", "POST", p, {"X-Benchmark-Token": OWNER})[0], 201)
        self.assertEqual(self.request(path)[0], 404)
        self.assertEqual(self.request("/v1/benchmarks/mine", headers={"X-Benchmark-Token": OWNER})[0], 200)
        private_image = "/v1/admin/benchmarks/" + p["reportId"] + "/heatmap.png"
        self.assertEqual(self.request(private_image)[0], 401)
        self.assertEqual(self.request(private_image, headers={"Authorization": "Bearer " + ADMIN}), (200, image()))
        review_path = "/v1/admin/benchmarks/" + p["reportId"] + "/review"
        self.assertEqual(self.request(review_path, "POST", {"bad": True})[0], 401)
        self.assertEqual(self.request(review_path, "POST", {"decision": "accepted", "note": ""}, {"Authorization": "Bearer " + ADMIN})[0], 200)
        self.assertEqual(self.request(path)[0], 200)
        self.assertEqual(self.request(path + "/heatmap.png"), (200, image()))
        self.assertEqual(self.request(path, "DELETE", headers={"X-Benchmark-Token": OTHER})[0], 404)
        self.assertEqual(self.request(path, "DELETE", headers={"X-Benchmark-Token": OWNER})[0], 200)
        self.assertEqual(self.request(path)[0], 404)

    def test_query_validation_and_admin_auth(self):
        self.assertEqual(self.request("/v1/benchmarks/leaderboard?sortBy=win&page=0")[0], 200)
        for query in ("sortBy=unknown", "page=-1", "page=x", "page=1&page=2", "extra=x"):
            self.assertEqual(self.request("/v1/benchmarks/leaderboard?" + query)[0], 400)
        self.assertEqual(self.request("/v1/admin/benchmarks")[0], 401)
        self.assertEqual(self.request("/v1/admin/benchmarks", headers={"Authorization": "Bearer wrong"})[0], 403)
        self.assertEqual(self.request("/admin")[0], 200)


if __name__ == "__main__": unittest.main()
