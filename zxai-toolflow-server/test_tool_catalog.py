"""Tool release contract tests: synthetic manifests, temporary state, loopback only.

No test needs an archive, downloads a URL, or executes an entry point.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from copy import deepcopy
import hashlib
from http.client import HTTPConnection
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest

from server import Store, make_handler
from tool_catalog import ToolCatalogError, ToolCatalogStore, validate_catalog


ADMIN_TOKEN = "synthetic-tool-catalog-admin-token"


def sample_catalog(revision=1):
    return {
        "schemaVersion": 1,
        "revision": revision,
        "publishedAt": "2026-10-05T00:00:00Z",
        "minClientVersion": "0.1.0.0",
        "tools": [{
            "id": "synthetic-tool",
            "name": "隔离测试工具",
            "category": "开发工具",
            "categories": ["开发工具"],
            "description": "只用于验证发布清单。",
            "publisher": "合成发布者",
            "version": f"{revision}.0.0",
            "tags": ["测试", "离线"],
            "homepage": "https://example.invalid/tool",
            "legacyPath": "开发工具/隔离测试工具",
            "order": 0,
            "packages": [{
                "architecture": "x64",
                "url": f"https://zhenxingai.com/downloads/tools/synthetic-tool/release-{revision}-x64.zip",
                "sizeBytes": 128 + revision,
                "sha256": hashlib.sha256(f"synthetic asset {revision}".encode("ascii")).hexdigest(),
                "entryPoint": "bin/SyntheticTool.exe",
                "kind": "portable-zip",
            }],
        }],
    }


class CatalogValidationTests(unittest.TestCase):
    def assert_invalid(self, catalog):
        with self.assertRaises(ToolCatalogError) as caught:
            validate_catalog(catalog)
        self.assertEqual(caught.exception.status, 400)

    def test_empty_catalog_can_remove_all_tools_and_optional_text_can_be_empty(self):
        catalog = sample_catalog()
        catalog["tools"] = []
        self.assertEqual(validate_catalog(catalog)["tools"], [])
        catalog = sample_catalog()
        tool = catalog["tools"][0]
        for field in ("homepage", "description", "publisher", "legacyPath"):
            tool[field] = ""
        tool["tags"] = []
        tool["categories"] = []
        self.assertEqual(validate_catalog(catalog)["tools"][0]["packages"][0]["entryPoint"], "bin/SyntheticTool.exe")

    def test_protocol_version_and_release_order_have_strict_types(self):
        invalid_fields = (
            ("schemaVersion", True), ("schemaVersion", 2),
            ("revision", True), ("revision", 1.0), ("revision", "1"),
            ("revision", 0), ("revision", -1),
            ("publishedAt", "not-a-time"), ("publishedAt", "2026-10-05T00:00:00"),
            ("minClientVersion", "0.1"), ("minClientVersion", "0.1.0.beta"),
        )
        for field, value in invalid_fields:
            with self.subTest(field=field, value=value):
                catalog = sample_catalog()
                catalog[field] = value
                self.assert_invalid(catalog)
        catalog = sample_catalog()
        catalog["unexpected"] = "not-in-the-protocol"
        self.assert_invalid(catalog)
        for order in (True, -1, 1.5, "0"):
            with self.subTest(order=order):
                catalog = sample_catalog()
                catalog["tools"][0]["order"] = order
                self.assert_invalid(catalog)

    def test_identity_and_architecture_collisions_are_rejected(self):
        for tool_id in ("Uppercase", "has_underscore", "../tool", "工具"):
            with self.subTest(tool_id=tool_id):
                catalog = sample_catalog()
                catalog["tools"][0]["id"] = tool_id
                self.assert_invalid(catalog)
        catalog = sample_catalog()
        catalog["tools"].append(deepcopy(catalog["tools"][0]))
        self.assert_invalid(catalog)
        catalog = sample_catalog()
        catalog["tools"][0]["packages"].append(deepcopy(catalog["tools"][0]["packages"][0]))
        self.assert_invalid(catalog)
        catalog = sample_catalog()
        catalog["tools"][0]["packages"] = []
        available_from_homepage = validate_catalog(catalog)["tools"][0]
        self.assertEqual(available_from_homepage["id"], "synthetic-tool")
        self.assertEqual(available_from_homepage["packages"], [])
        self.assertEqual(available_from_homepage["homepage"], "https://example.invalid/tool")

    def test_archive_urls_cannot_escape_the_owned_download_directory(self):
        urls = (
            "http://zhenxingai.com/downloads/tools/tool/a.zip",
            "https://other.invalid/downloads/tools/tool/a.zip",
            "https://zhenxingai.com.evil.invalid/downloads/tools/tool/a.zip",
            "https://user@zhenxingai.com/downloads/tools/tool/a.zip",
            "https://zhenxingai.com:8443/downloads/tools/tool/a.zip",
            "https://zhenxingai.com/downloads/tools/tool/a.zip?token=fixture",
            "https://zhenxingai.com/downloads/tools/tool/a.zip#fragment",
            "https://zhenxingai.com/downloads/tools/../outside.zip",
            "https://zhenxingai.com/downloads/tools/%2e%2e/outside.zip",
            "https://zhenxingai.com/downloads/tools/tool\\a.zip",
            "https://zhenxingai.com/downloads/tools/tool/a.exe",
        )
        for url in urls:
            with self.subTest(url=url):
                catalog = sample_catalog()
                catalog["tools"][0]["packages"][0]["url"] = url
                self.assert_invalid(catalog)

    def test_package_and_legacy_paths_are_safe_for_windows(self):
        entry_points = (
            "../Tool.exe", "C:/Tool.exe", "/Tool.exe", "bin\\Tool.exe",
            "bin/CON.exe", "bin/CONIN$.exe", "bin/COM¹.exe", "bin./Tool.exe", "bin /Tool.exe",
            "bin/Tool.exe.", "bin/Tool.cmd",
        )
        for entry_point in entry_points:
            with self.subTest(entry_point=entry_point):
                catalog = sample_catalog()
                catalog["tools"][0]["packages"][0]["entryPoint"] = entry_point
                self.assert_invalid(catalog)
        for legacy_path in ("../tool", "C:/tool", "folder/CON", "folder./tool"):
            with self.subTest(legacy_path=legacy_path):
                catalog = sample_catalog()
                catalog["tools"][0]["legacyPath"] = legacy_path
                self.assert_invalid(catalog)
        for field, value in (("sizeBytes", 0), ("sizeBytes", True),
                             ("sha256", "g" * 64), ("sha256", "a" * 63), ("kind", "exe")):
            with self.subTest(field=field, value=value):
                catalog = sample_catalog()
                catalog["tools"][0]["packages"][0][field] = value
                self.assert_invalid(catalog)

    def test_release_metadata_utf8_limits_match_client_capacity(self):
        fields = (("name", 160, False), ("category", 100, False),
                  ("version", 100, False), ("tags", 100, True),
                  ("categories", 100, True), ("homepage", 1500, False))
        for field, byte_limit, is_list in fields:
            with self.subTest(field=field):
                if field == "homepage":
                    prefix = "https://example.invalid/"
                    value = prefix + "a" * (byte_limit - len(prefix))
                else:
                    value = "中" * (byte_limit // 3) + "a" * (byte_limit % 3)
                self.assertEqual(len(value.encode("utf-8")), byte_limit)
                catalog = sample_catalog()
                catalog["tools"][0][field] = [value] if is_list else value
                validated = validate_catalog(catalog)["tools"][0]
                self.assertEqual(validated[field], [value] if is_list else value)

                one_byte_more = value + "b"
                self.assertEqual(len(one_byte_more.encode("utf-8")), byte_limit + 1)
                catalog["tools"][0][field] = [one_byte_more] if is_list else one_byte_more
                self.assert_invalid(catalog)

    def test_package_size_accepts_512_mib_and_rejects_one_byte_more(self):
        byte_limit = 512 * 1024 * 1024
        catalog = sample_catalog()
        catalog["tools"][0]["packages"][0]["sizeBytes"] = byte_limit
        self.assertEqual(validate_catalog(catalog)["tools"][0]["packages"][0]["sizeBytes"], byte_limit)
        catalog["tools"][0]["packages"][0]["sizeBytes"] = byte_limit + 1
        self.assert_invalid(catalog)

    def test_windows_device_ids_and_installer_entry_points_are_rejected(self):
        for tool_id in ("con", "prn", "aux", "nul", "com1", "com9", "lpt1", "lpt9"):
            with self.subTest(tool_id=tool_id):
                catalog = sample_catalog()
                catalog["tools"][0]["id"] = tool_id
                self.assert_invalid(catalog)
        for entry_point in ("bin/Setup.exe", "bin/tool-INSTALL.exe", "bin/tool-uninstall.exe", "bin/vcredist.exe"):
            with self.subTest(entry_point=entry_point):
                catalog = sample_catalog()
                catalog["tools"][0]["packages"][0]["entryPoint"] = entry_point
                self.assert_invalid(catalog)

    def test_relative_paths_enforce_total_and_segment_utf8_byte_limits(self):
        first_segment = "中" * 40  # 120 UTF-8 bytes.
        legacy_path = first_segment + "/" + "中" * 39 + "aa"
        self.assertEqual(len(legacy_path.encode("utf-8")), 240)
        catalog = sample_catalog()
        catalog["tools"][0]["legacyPath"] = legacy_path
        self.assertEqual(validate_catalog(catalog)["tools"][0]["legacyPath"], legacy_path)
        with self.subTest(path="legacy-total-241"):
            catalog["tools"][0]["legacyPath"] = first_segment + "/" + first_segment
            self.assertEqual(len(catalog["tools"][0]["legacyPath"].encode("utf-8")), 241)
            self.assert_invalid(catalog)

        entry_point = "😀" * 29 + ".exe"
        self.assertEqual(len(entry_point.encode("utf-8")), 120)
        catalog = sample_catalog()
        catalog["tools"][0]["packages"][0]["entryPoint"] = entry_point
        self.assertEqual(validate_catalog(catalog)["tools"][0]["packages"][0]["entryPoint"], entry_point)
        with self.subTest(path="emoji-segment-121"):
            catalog["tools"][0]["packages"][0]["entryPoint"] = "😀" * 29 + "a.exe"
            self.assertEqual(len(catalog["tools"][0]["packages"][0]["entryPoint"].encode("utf-8")), 121)
            self.assert_invalid(catalog)

        relative_prefix = "downloads/tools/"
        long_folder = "a" * 120
        leaf_bytes = 241 - len(relative_prefix) - len(long_folder) - 1
        long_relative_path = relative_prefix + long_folder + "/" + "b" * (leaf_bytes - 4) + ".zip"
        self.assertEqual(len(long_relative_path.encode("ascii")), 241)
        self.assertTrue(all(len(part.encode("ascii")) <= 120 for part in long_relative_path.split("/")))
        for label, relative_path in (
            ("url-segment-121", relative_prefix + "a" * 117 + ".zip"),
            ("url-total-241", long_relative_path),
        ):
            with self.subTest(path=label):
                catalog = sample_catalog()
                catalog["tools"][0]["packages"][0]["url"] = "https://zhenxingai.com/" + relative_path
                self.assert_invalid(catalog)


class CatalogStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.store = ToolCatalogStore(self.folder)

    def test_unpublished_state_is_unavailable(self):
        for operation in (self.store.current, self.store.snapshot):
            with self.subTest(operation=operation.__name__), self.assertRaises(ToolCatalogError) as caught:
                operation()
            self.assertEqual(caught.exception.status, 503)

    def test_publication_survives_restart_and_returns_a_consistent_snapshot(self):
        first = self.store.publish(sample_catalog(1))
        second = self.store.publish(sample_catalog(2))
        self.assertTrue(first["created"])
        self.assertTrue(second["created"])
        self.assertNotEqual(first["etag"], second["etag"])
        restarted = ToolCatalogStore(self.folder)
        catalog, etag = restarted.snapshot()
        self.assertEqual(catalog, sample_catalog(2))
        self.assertEqual(restarted.current(), catalog)
        self.assertEqual(etag, second["etag"])
        self.assertTrue(etag.startswith('"') and etag.endswith('"'))
        self.assertTrue((self.folder / "tool-catalog").is_dir())

    def test_canonical_retries_are_idempotent_and_history_never_downgrades_current(self):
        first = self.store.publish(sample_catalog(1))
        # Object-key order is not a new manifest.
        reversed_keys = json.loads(json.dumps(sample_catalog(1), sort_keys=True))
        repeat = self.store.publish(reversed_keys)
        self.assertFalse(repeat["created"])
        self.assertEqual(repeat["etag"], first["etag"])
        self.store.publish(sample_catalog(3))
        old_repeat = self.store.publish(sample_catalog(1))
        self.assertFalse(old_repeat["created"])
        self.assertEqual(self.store.current()["revision"], 3)
        changed = sample_catalog(1)
        changed["tools"][0]["description"] = "不同的内容"
        for rejected in (changed, sample_catalog(2)):
            with self.subTest(revision=rejected["revision"]), self.assertRaises(ToolCatalogError) as caught:
                self.store.publish(rejected)
            self.assertEqual(caught.exception.status, 409)
        self.assertEqual(self.store.current()["revision"], 3)

    def test_existing_asset_url_cannot_acquire_different_bytes(self):
        initial = sample_catalog(1)
        self.store.publish(initial)
        initial_package = initial["tools"][0]["packages"][0]
        for changed_field in ("sha256", "sizeBytes"):
            with self.subTest(field=changed_field):
                candidate = sample_catalog(2)
                package = deepcopy(initial_package)
                package[changed_field] = ("f" * 64 if changed_field == "sha256" else initial_package["sizeBytes"] + 1)
                candidate["tools"][0]["packages"] = [package]
                with self.assertRaises(ToolCatalogError) as caught:
                    self.store.publish(candidate)
                self.assertEqual(caught.exception.status, 409)
                self.assertEqual(self.store.current(), initial)

    def test_failed_publication_rolls_back_assets_registered_before_a_conflict(self):
        initial = sample_catalog(1)
        first = self.store.publish(initial)
        candidate = sample_catalog(2)
        new_package = deepcopy(candidate["tools"][0]["packages"][0])
        conflict = deepcopy(initial["tools"][0]["packages"][0])
        conflict["architecture"] = "arm64"
        conflict["sha256"] = "f" * 64
        # A new URL appears first; the later existing URL forces a rollback.
        candidate["tools"][0]["packages"] = [new_package, conflict]
        with self.assertRaises(ToolCatalogError) as caught:
            self.store.publish(candidate)
        self.assertEqual(caught.exception.status, 409)
        self.assertEqual(self.store.snapshot(), (initial, first["etag"]))

        recovery = sample_catalog(3)
        recovered_package = deepcopy(new_package)
        recovered_package["sha256"] = "e" * 64
        recovery["tools"][0]["packages"] = [recovered_package]
        # The failed release must not reserve its new URL or its old hash.
        result = self.store.publish(recovery)
        self.assertTrue(result["created"])
        self.assertEqual(self.store.current(), recovery)

    def test_default_https_port_alias_cannot_replace_an_existing_asset_hash(self):
        initial = sample_catalog(1)
        first = self.store.publish(initial)
        initial_package = initial["tools"][0]["packages"][0]
        candidate = sample_catalog(2)
        package = candidate["tools"][0]["packages"][0]
        package["url"] = initial_package["url"].replace(
            "https://zhenxingai.com/", "https://zhenxingai.com:443/", 1)
        package["sizeBytes"] = initial_package["sizeBytes"]
        self.assertNotEqual(package["sha256"], initial_package["sha256"])
        with self.assertRaises(ToolCatalogError) as caught:
            self.store.publish(candidate)
        self.assertEqual(caught.exception.status, 409)
        self.assertEqual(self.store.snapshot(), (initial, first["etag"]))

    def test_conflicting_concurrent_publishers_have_one_persistent_winner(self):
        self.store.publish(sample_catalog(1))
        other = ToolCatalogStore(self.folder)
        barrier = threading.Barrier(2)
        candidates = [sample_catalog(2), sample_catalog(2)]
        candidates[1]["tools"][0]["name"] = "另一个发布者的清单"

        def publish(store, catalog):
            barrier.wait(timeout=3)
            try:
                return ("created", store.publish(catalog))
            except ToolCatalogError as error:
                return ("error", error.status)

        with ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(publish, store, catalog)
                       for store, catalog in zip((self.store, other), candidates)]
            results = [future.result(timeout=5) for future in futures]
        self.assertEqual([kind for kind, _ in results].count("created"), 1)
        self.assertIn(("error", 409), results)
        winner = next(index for index, (kind, _) in enumerate(results) if kind == "created")
        self.assertTrue(results[winner][1]["created"])
        self.assertEqual(ToolCatalogStore(self.folder).current(), candidates[winner])


class LoopbackServer(ThreadingHTTPServer):
    daemon_threads = True
    block_on_close = False


class CatalogHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.temp.name) / "synthetic.sqlite3")
        self.store.tool_catalog.heartbeat_seconds = 0.05
        self.server = LoopbackServer(("127.0.0.1", 0), make_handler(self.store, admin_token=ADMIN_TOKEN))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)
        self.temp.cleanup()

    def request(self, method, path, body=None, *, token=None, headers=None, raw=None):
        connection = HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        request_headers = dict(headers or {})
        if token is not None:
            request_headers["Authorization"] = "Bearer " + token
        payload = raw
        if body is not None:
            payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
        if payload is not None:
            request_headers["Content-Type"] = "application/json"
        try:
            connection.request(method, path, body=payload, headers=request_headers)
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def publish(self, revision):
        return self.request("POST", "/v1/admin/tools/publish", sample_catalog(revision), token=ADMIN_TOKEN)

    @contextmanager
    def stream(self, last_event_id=None):
        connection = HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        headers = {} if last_event_id is None else {"Last-Event-ID": str(last_event_id)}
        connection.request("GET", "/v1/tools/events", headers=headers)
        response = connection.getresponse()
        try:
            self.assertEqual(response.status, 200)
            self.assertIn("text/event-stream", response.getheader("Content-Type"))
            yield response
        finally:
            response.close()
            connection.close()

    def read_frame(self, response):
        fields, comments = {}, []
        for _ in range(30):
            raw_line = response.readline()
            if not raw_line:
                self.fail("SSE connection closed before its next frame")
            line = raw_line.decode("utf-8").rstrip("\r\n")
            if not line:
                return fields, comments
            if line.startswith(":"):
                comments.append(line)
            elif ":" in line:
                key, value = line.split(":", 1)
                fields[key] = value.lstrip(" ")
        self.fail("unterminated SSE frame")

    def read_catalog_event(self, response):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            fields, _ = self.read_frame(response)
            if fields.get("event") == "catalog":
                return fields, json.loads(fields["data"])
        self.fail("catalog event did not arrive")

    def test_public_catalog_and_stream_are_unavailable_before_first_release(self):
        for path in ("/v1/tools/catalog", "/v1/tools/events"):
            with self.subTest(path=path):
                status, _, body = self.request("GET", path)
                self.assertEqual(status, 503)
                self.assertIn("error", json.loads(body))

    def test_admin_authentication_precedes_json_parsing_and_catalog_lookup(self):
        for token, expected in ((None, 401), ("wrong-synthetic-token", 403)):
            with self.subTest(token=token):
                status, _, _ = self.request("POST", "/v1/admin/tools/publish", token=token, raw=b"not-json")
                self.assertEqual(status, expected)
                status, _, _ = self.request("GET", "/v1/admin/tools/catalog", token=token)
                self.assertEqual(status, expected)
        status, _, _ = self.request("POST", "/v1/admin/tools/publish", token=ADMIN_TOKEN, raw=b"not-json")
        self.assertEqual(status, 400)
        disabled = LoopbackServer(("127.0.0.1", 0), make_handler(self.store))
        disabled_thread = threading.Thread(target=disabled.serve_forever, daemon=True)
        disabled_thread.start()
        connection = HTTPConnection("127.0.0.1", disabled.server_port, timeout=3)
        try:
            connection.request("POST", "/v1/admin/tools/publish", body=b"not-json",
                               headers={"Content-Type": "application/json", "Authorization": "Bearer " + ADMIN_TOKEN})
            response = connection.getresponse()
            self.assertEqual(response.status, 403)
            response.read()
        finally:
            connection.close()
            disabled.shutdown()
            disabled.server_close()
            disabled_thread.join(timeout=3)

    def test_http_publish_retry_and_conflict_keep_the_published_release(self):
        status, _, body = self.publish(1)
        self.assertEqual(status, 201)
        receipt = json.loads(body)
        self.assertEqual(receipt["revision"], 1)
        self.assertTrue(receipt["created"])
        status, _, body = self.publish(1)
        self.assertEqual(status, 200)
        self.assertFalse(json.loads(body)["created"])
        changed = sample_catalog(1)
        changed["tools"][0]["description"] = "不能覆盖同一版本"
        status, _, _ = self.request("POST", "/v1/admin/tools/publish", changed, token=ADMIN_TOKEN)
        self.assertEqual(status, 409)
        status, _, body = self.request("GET", "/v1/admin/tools/catalog", token=ADMIN_TOKEN)
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body), sample_catalog(1))

    def test_catalog_etag_revalidates_and_changes_with_the_next_release(self):
        self.publish(1)
        status, headers, body = self.request("GET", "/v1/tools/catalog")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body), sample_catalog(1))
        etag = headers["ETag"]
        status, headers, body = self.request("GET", "/v1/tools/catalog", headers={"If-None-Match": etag})
        self.assertEqual(status, 304)
        self.assertEqual(body, b"")
        self.assertEqual(headers["ETag"], etag)
        self.publish(2)
        status, headers, body = self.request("GET", "/v1/tools/catalog", headers={"If-None-Match": etag})
        self.assertEqual(status, 200)
        self.assertNotEqual(headers["ETag"], etag)
        self.assertEqual(json.loads(body)["revision"], 2)

    def test_admin_shell_is_static_and_does_not_embed_manifest_or_token(self):
        self.publish(1)
        status, _, body = self.request("GET", "/admin/tools")
        self.assertEqual(status, 200)
        html = body.decode("utf-8")
        self.assertNotIn(ADMIN_TOKEN, html)
        self.assertNotIn("隔离测试工具", html)
        self.assertNotIn(sample_catalog()["tools"][0]["packages"][0]["sha256"], html)

    def test_sse_sends_current_heartbeat_and_next_published_revision(self):
        self.publish(1)
        with self.stream() as response:
            fields, data = self.read_catalog_event(response)
            self.assertEqual(fields["id"], "1")
            self.assertEqual(data, {"revision": 1, "etag": self.store.tool_catalog.snapshot()[1]})
            heartbeat, comments = self.read_frame(response)
            self.assertFalse(heartbeat)
            self.assertTrue(comments)
            self.assertEqual(self.publish(2)[0], 201)
            fields, data = self.read_catalog_event(response)
            self.assertEqual(fields["id"], "2")
            self.assertEqual(data, {"revision": 2, "etag": self.store.tool_catalog.snapshot()[1]})

    def test_sse_reconnection_resynchronizes_to_current_after_missed_releases(self):
        self.publish(1)
        with self.stream() as response:
            self.assertEqual(self.read_catalog_event(response)[0]["id"], "1")
        self.publish(2)
        self.publish(3)
        with self.stream(last_event_id=1) as response:
            fields, data = self.read_catalog_event(response)
            self.assertEqual(fields["id"], "3")
            self.assertEqual(data, {"revision": 3, "etag": self.store.tool_catalog.snapshot()[1]})
        # Even an up-to-date reconnect receives a fresh current snapshot.
        with self.stream(last_event_id=3) as response:
            self.assertEqual(self.read_catalog_event(response)[0]["id"], "3")


if __name__ == "__main__":
    unittest.main()
