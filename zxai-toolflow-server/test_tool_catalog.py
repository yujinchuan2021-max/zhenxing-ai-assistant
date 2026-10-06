"""Tool contract tests use synthetic PE ZIPs, temporary state and loopback HTTP.

No vendor package is downloaded, extracted or executed; production transport is
replaced with a local HTTP fixture while the full verification logic stays real.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from contextlib import closing, contextmanager
from copy import deepcopy
import hashlib
from http.client import HTTPConnection
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import importlib.util
import json
from pathlib import Path
import tempfile
import struct
import stat
import sqlite3
import threading
import time
import unittest
from unittest.mock import patch
from urllib.parse import urlsplit
import zipfile
import zlib

from server import Store, make_handler
from tool_catalog import ToolCatalogError, ToolCatalogStore, validate_catalog
from tool_package_verifier import verify_catalog_packages
import tool_package_verifier


ADMIN_TOKEN = "synthetic-tool-catalog-admin-token"


def synthetic_pe(architecture="x64"):
    data = bytearray(528)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 60, 128)
    data[128:132] = b"PE\0\0"
    optional = 224 if architecture == "x86" else 240
    struct.pack_into("<HHIIIHH", data, 132, {"x64": 0x8664, "x86": 0x14c, "arm64": 0xaa64}[architecture], 1, 0, 0, 0, optional, 2)
    struct.pack_into("<H", data, 152, 0x10b if architecture == "x86" else 0x20b)
    struct.pack_into("<I", data, 168, 0x1000)
    section = 152 + optional
    data[section:section + 8] = b".text\0\0\0"
    struct.pack_into("<IIII", data, section + 8, 16, 0x1000, 16, 512)
    data[512:] = b"synthetic-only!!"
    return bytes(data)


def synthetic_zip(revision=1, *, entries=None, compression=zipfile.ZIP_DEFLATED):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, body in (entries or [("bin/SyntheticTool.exe", synthetic_pe()), ("README.txt", f"synthetic revision {revision}".encode())]):
            info = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
            info.compress_type = compression
            info.external_attr = 0o100644 << 16
            archive.writestr(info, body)
    return output.getvalue()


class PackageFixture:
    """Real loopback HTTP bodies, behind the production verifier's transport seam."""
    def __init__(self):
        self.responses, self.requests = {}, []
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                fixture.requests.append(self.path)
                revision = int(self.path.rsplit("release-", 1)[-1].split("-", 1)[0]) if "release-" in self.path else 1
                status, headers, body = fixture.responses.get(self.path, (200, {}, synthetic_zip(revision)))
                self.send_response(status)
                for key, value in headers.items():
                    self.send_header(key, value)
                if "Content-Length" not in headers:
                    self.send_header("Content-Length", str(len(body)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *_):
                pass

        self.server = LoopbackServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @contextmanager
    def open(self, url, timeout):
        connection = HTTPConnection("127.0.0.1", self.server.server_port, timeout=timeout)
        try:
            parts = urlsplit(url)
            connection.request("GET", "/" + parts.netloc + parts.path)
            yield connection.getresponse()
        finally:
            connection.close()

    def response(self, url, body, *, status=200, headers=None):
        parts = urlsplit(url)
        self.responses["/" + parts.netloc + parts.path] = (status, headers or {}, body)

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)


def use_package_fixture(test):
    fixture = PackageFixture()
    test.addCleanup(fixture.close)
    replacement = patch("tool_package_verifier._open_package", fixture.open)
    replacement.start()
    test.addCleanup(replacement.stop)
    return fixture


def sample_catalog(revision=1):
    payload = synthetic_zip(revision)
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
                "sizeBytes": len(payload),
                "sha256": hashlib.sha256(payload).hexdigest(),
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
            ("schemaVersion", True), ("schemaVersion", 3),
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

    def test_reviewed_removal_tools_keep_their_exact_vendor_entry_names(self):
        for entry in ("Display Driver Uninstaller.exe", "DDU/Display Driver Uninstaller.exe",
                      "HiBitUninstaller-Portable.exe"):
            with self.subTest(entry=entry):
                catalog = sample_catalog()
                catalog["tools"][0]["packages"][0]["entryPoint"] = entry
                self.assertEqual(validate_catalog(catalog)["tools"][0]["packages"][0]["entryPoint"], entry)
        for entry in ("Display Driver Uninstaller-setup.exe", "HiBitUninstaller-Portable-Installer.exe",
                      "../Display Driver Uninstaller.exe"):
            with self.subTest(entry=entry):
                catalog = sample_catalog()
                catalog["tools"][0]["packages"][0]["entryPoint"] = entry
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
        self.packages = use_package_fixture(self)
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
        self.packages = use_package_fixture(self)
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


class PackageVerificationTests(unittest.TestCase):
    def setUp(self):
        self.packages = use_package_fixture(self)

    def test_valid_unicode_path_extra_is_not_misreported_as_a_bad_package(self):
        info = zipfile.ZipInfo("legacy-help.txt")
        path = "帮助.txt".encode("utf-8")
        value = b"\x01" + struct.pack("<I", zlib.crc32(info.filename.encode("cp437"))) + path
        info.extra = struct.pack("<HH", 0x7075, len(value)) + value
        self.assertEqual(tool_package_verifier._zip_effective_name(info), "帮助.txt")
        # Python releases that interpret this standard field change filename;
        # orig_filename still retains the central-directory spelling.
        info.filename = "帮助.txt"
        self.assertEqual(tool_package_verifier._zip_effective_name(info), "帮助.txt")

    def test_unicode_path_field_cannot_hide_traversal_or_wrong_name_checksum(self):
        for decoded, checksum in (("../escaped.txt", True), ("safe.txt", False), ("safe\0.txt", True)):
            with self.subTest(decoded=decoded, checksum=checksum):
                info = zipfile.ZipInfo("legacy.txt")
                value = b"\x01" + struct.pack("<I", zlib.crc32(b"legacy.txt") if checksum else 0) + decoded.encode("utf-8")
                info.extra = struct.pack("<HH", 0x7075, len(value)) + value
                with self.assertRaises(ToolCatalogError):
                    tool_package_verifier._zip_effective_name(info)

    def test_plain_path_truncation_still_fails_without_unicode_evidence(self):
        info = zipfile.ZipInfo("safe.txt")
        info.orig_filename = "safe.txt\0hidden.txt"
        with self.assertRaises(ToolCatalogError):
            tool_package_verifier._zip_effective_name(info)

    def candidate_with_bytes(self, body, *, catalog=None):
        catalog = deepcopy(catalog or sample_catalog())
        package = catalog["tools"][0]["packages"][0]
        package.update(sizeBytes=len(body), sha256=hashlib.sha256(body).hexdigest())
        self.packages.response(package["url"], body)
        for mirror in package.get("mirrors", []):
            self.packages.response(mirror, body)
        return catalog

    def assert_verification_rejected(self, catalog, message=None):
        with self.assertRaises(ToolCatalogError) as caught:
            verify_catalog_packages(catalog)
        self.assertEqual(caught.exception.status, 502)
        if message:
            self.assertIn(message, caught.exception.message)

    def v2(self):
        catalog = sample_catalog()
        catalog["schemaVersion"] = 2
        catalog["tools"][0]["packages"][0]["mirrors"] = [
            "https://download.zhenxingai.com/downloads/tools/synthetic-tool/release-1-x64.zip",
            "https://download-backup.zhenxingai.com/downloads/tools/synthetic-tool/release-1-x64.zip"]
        return catalog

    def test_full_zip_receipt_is_bound_to_canonical_catalog_and_entry(self):
        catalog = sample_catalog()
        receipt = verify_catalog_packages(catalog)
        expected = json.dumps(validate_catalog(catalog), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
        self.assertEqual(receipt["catalogSha256"], hashlib.sha256(expected).hexdigest())
        self.assertEqual(receipt["catalogBytes"], len(expected))
        self.assertEqual(receipt["revision"], 1)
        self.assertFalse(receipt["toolsExecuted"])
        self.assertEqual(receipt["originCount"], 1)
        self.assertEqual(receipt["packages"][0]["fileCount"], 2)
        self.assertEqual(receipt["packages"][0]["entries"][0]["peArchitecture"], "x64")

    def test_v2_requires_every_declared_origin_to_serve_the_same_bytes(self):
        catalog = self.v2()
        receipt = verify_catalog_packages(catalog)
        self.assertEqual(receipt["originCount"], 3)
        self.assertEqual(len(self.packages.requests), 3)
        self.assertEqual(len({asset["sha256"] for asset in receipt["packages"]}), 1)
        mirrors = catalog["tools"][0]["packages"][0]["mirrors"]
        self.packages.response(mirrors[-1], b"<html>wrong backup</html>", headers={"Content-Type": "text/html"})
        self.assert_verification_rejected(catalog, "page")

    def test_empty_catalog_has_an_explicit_zero_asset_receipt(self):
        catalog = sample_catalog()
        catalog["tools"] = []
        receipt = verify_catalog_packages(catalog)
        self.assertEqual(receipt["originCount"], 0)
        self.assertEqual(receipt["packages"], [])
        self.assertEqual(self.packages.requests, [])

    def test_http_redirect_error_html_and_encoded_bodies_are_rejected(self):
        catalog = sample_catalog()
        package = catalog["tools"][0]["packages"][0]
        for code, headers, message in (
            (302, {"Location": "https://github.com/private/asset.zip"}, "HTTP 302"),
            (404, {}, "HTTP 404"),
            (206, {}, "HTTP 206"),
            (200, {"Content-Type": "text/html"}, "page"),
            (200, {"Content-Encoding": "gzip"}, "encoded"),
        ):
            with self.subTest(code=code, headers=headers):
                self.packages.response(package["url"], synthetic_zip(), status=code, headers=headers)
                self.assert_verification_rejected(catalog, message)

    def test_wrong_length_and_digest_are_rejected(self):
        catalog = sample_catalog()
        url = catalog["tools"][0]["packages"][0]["url"]
        self.packages.response(url, synthetic_zip()[:-1])
        self.assert_verification_rejected(catalog, "Content-Length")
        self.packages.response(url, synthetic_zip())
        catalog["tools"][0]["packages"][0]["sha256"] = "a" * 64
        self.assert_verification_rejected(catalog, "SHA-256")

    def test_html_without_content_type_truncation_and_prefixed_zip_are_rejected(self):
        for body in (b"<html>not ZIP</html>", synthetic_zip()[:-8], b"junk" + synthetic_zip()):
            with self.subTest(body_prefix=body[:4]):
                self.assert_verification_rejected(self.candidate_with_bytes(body))

    def test_crc_is_checked_for_non_entry_files_too(self):
        body = bytearray(synthetic_zip(entries=[("bin/SyntheticTool.exe", synthetic_pe()), ("data.txt", b"crc-marker")], compression=zipfile.ZIP_STORED))
        at = body.index(b"crc-marker")
        body[at] ^= 1
        self.assert_verification_rejected(self.candidate_with_bytes(bytes(body)), "CRC")

    def test_path_traversal_devices_and_case_collisions_are_rejected(self):
        for name in ("../escape.txt", "/absolute.txt", "C:/escape.txt", "bin\\escape.txt", "bin/NUL", "bin/space .txt", "bin/ADS:payload"):
            # Internal spaces in filenames are allowed on Windows.
            if name == "bin/space .txt":
                name = "bin/trailing-space /x.txt"
            with self.subTest(name=name):
                body = synthetic_zip(entries=[("bin/SyntheticTool.exe", synthetic_pe()), (name, b"x")])
                # ZipInfo normalizes OS separators during creation on Windows;
                # mutate the stored headers to exercise the actual unsafe ZIP.
                if "\\" in name:
                    body = body.replace(name.replace("\\", "/").encode(), name.encode())
                self.assert_verification_rejected(self.candidate_with_bytes(body), "unsafe")
        for name in ("bin/synthetictool.EXE", "bin"):
            body = synthetic_zip(entries=[("bin/SyntheticTool.exe", synthetic_pe()), (name, b"x")])
            self.assert_verification_rejected(self.candidate_with_bytes(body), "collid")

    def test_links_and_unsupported_compression_are_rejected(self):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w") as archive:
            archive.writestr("bin/SyntheticTool.exe", synthetic_pe())
            info = zipfile.ZipInfo("evil-link")
            info.create_system = 3
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            archive.writestr(info, "../escape")
        self.assert_verification_rejected(self.candidate_with_bytes(output.getvalue()), "link")
        self.assert_verification_rejected(self.candidate_with_bytes(synthetic_zip(compression=zipfile.ZIP_BZIP2)), "unsupported")

    def test_client_reserved_installation_receipt_cannot_be_shipped_in_a_package(self):
        body = synthetic_zip(entries=[("bin/SyntheticTool.exe", synthetic_pe()), (".ZXAI-CLOUD-INSTALL.JSON", b"{}")])
        self.assert_verification_rejected(self.candidate_with_bytes(body), "reserved")

    def test_missing_invalid_and_wrong_architecture_entries_are_rejected(self):
        cases = ([("other.exe", synthetic_pe())], [("bin/SyntheticTool.exe", b"not a PE")],
                 [("bin/SyntheticTool.exe", synthetic_pe("x86"))])
        for entries in cases:
            with self.subTest(name=entries[0][0]):
                self.assert_verification_rejected(self.candidate_with_bytes(synthetic_zip(entries=entries)))

    def test_count_expanded_download_and_time_limits_are_enforced(self):
        for setting, value in (("MAX_ZIP_ENTRIES", 1), ("MAX_EXPANDED_BYTES", 20),
                               ("MAX_ENTRY_BYTES", 20), ("MAX_TOTAL_DOWNLOAD_BYTES", 1),
                               ("CATALOG_TIMEOUT_SECONDS", -1)):
            with self.subTest(setting=setting), patch("tool_package_verifier." + setting, value):
                self.assert_verification_rejected(sample_catalog())

    def test_shared_asset_is_read_once_but_every_entry_profile_is_verified(self):
        body = synthetic_zip(entries=[("bin/SyntheticTool.exe", synthetic_pe()), ("x86/Tool.exe", synthetic_pe("x86"))])
        catalog = self.candidate_with_bytes(body)
        other = deepcopy(catalog["tools"][0]["packages"][0])
        other.update(architecture="x86", entryPoint="x86/Tool.exe")
        catalog["tools"][0]["packages"].append(other)
        receipt = verify_catalog_packages(catalog)
        self.assertEqual(len(self.packages.requests), 1)
        self.assertEqual(len(receipt["packages"][0]["entries"]), 2)


class CatalogGateStoreTests(CatalogStoreTests):
    def test_failure_preserves_pointer_history_assets_and_receipts(self):
        first = self.store.publish(sample_catalog())
        bad = sample_catalog(2)
        self.packages.response(bad["tools"][0]["packages"][0]["url"], b"bad")
        with self.assertRaises(ToolCatalogError) as caught:
            self.store.publish(bad)
        self.assertEqual(caught.exception.status, 502)
        self.assertEqual(self.store.snapshot(), (sample_catalog(), first["etag"]))
        with self.assertRaises(ToolCatalogError) as caught:
            self.store.verification_receipt(2)
        self.assertEqual(caught.exception.status, 404)
        with self.store._connect() as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM catalogs").fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM package_assets").fetchone()[0], 1)

    def test_receipt_persists_and_identical_retries_reuse_original_evidence(self):
        first = self.store.publish(sample_catalog())
        self.assertEqual(ToolCatalogStore(self.folder).verification_receipt(1), first["verification"])
        self.packages.requests.clear()
        retry = self.store.publish(sample_catalog())
        self.assertFalse(retry["created"])
        self.assertEqual(retry["verification"], first["verification"])
        self.assertEqual(self.packages.requests, [])

    def test_old_database_is_readable_but_legacy_retry_must_obtain_real_evidence(self):
        catalog = sample_catalog()
        payload = json.dumps(catalog, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        with self.store._connect() as db:
            db.execute("INSERT INTO catalogs VALUES (?, ?, ?)", (1, payload, '"legacy"'))
            db.execute("INSERT INTO catalog_current VALUES (1, 1)")
        self.assertEqual(self.store.current(), catalog)
        with self.assertRaises(ToolCatalogError):
            self.store.verification_receipt(1)
        self.packages.response(catalog["tools"][0]["packages"][0]["url"], b"bad")
        with self.assertRaises(ToolCatalogError):
            self.store.publish(catalog)
        self.assertEqual(self.store.snapshot()[1], '"legacy"')
        self.packages.responses.clear()
        retry = self.store.publish(catalog)
        self.assertFalse(retry["created"])
        self.assertEqual(retry["verification"]["originCount"], 1)

    def test_original_v1_database_migrates_without_changing_its_current_payload(self):
        legacy_root = self.folder / "original-format"
        legacy_dir = legacy_root / "tool-catalog"
        legacy_dir.mkdir(parents=True)
        catalog = sample_catalog()
        package = catalog["tools"][0]["packages"][0]
        payload = json.dumps(catalog, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        with closing(sqlite3.connect(legacy_dir / "catalog.sqlite3")) as old, old:
            old.executescript("""
                CREATE TABLE catalogs (revision INTEGER PRIMARY KEY, payload TEXT NOT NULL, etag TEXT NOT NULL);
                CREATE TABLE catalog_current (singleton INTEGER PRIMARY KEY, revision INTEGER NOT NULL);
                CREATE TABLE package_assets (url TEXT PRIMARY KEY, size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL);
            """)
            old.execute("INSERT INTO catalogs VALUES (?, ?, ?)", (1, payload, '"original-etag"'))
            old.execute("INSERT INTO catalog_current VALUES (1, 1)")
            old.execute("INSERT INTO package_assets VALUES (?, ?, ?)", (package["url"], package["sizeBytes"], package["sha256"]))
        upgraded = ToolCatalogStore(legacy_root)
        self.assertEqual(upgraded.snapshot(), (catalog, '"original-etag"'))
        with upgraded._connect() as current:
            binding = current.execute("SELECT size_bytes, sha256 FROM main.package_assets WHERE url=?", (package["url"],)).fetchone()
            self.assertEqual(binding, (package["sizeBytes"], package["sha256"]))
            self.assertEqual(current.execute("SELECT COUNT(*) FROM catalog_db.verification_receipts").fetchone()[0], 0)
        self.assertTrue(upgraded.publish(sample_catalog(2))["created"])
        self.assertEqual(upgraded.current()["revision"], 2)

    def test_v1_and_v2_have_independent_current_history_and_assets(self):
        self.store.publish(sample_catalog(3))
        catalog = sample_catalog(4)
        catalog["schemaVersion"] = 2
        catalog["tools"][0]["packages"][0]["mirrors"] = []
        other = ToolCatalogStore(self.folder, schema_version=2)
        other.publish(catalog)
        self.assertEqual(other.current()["revision"], 4)
        self.assertEqual(self.store.current()["revision"], 3)
        with self.assertRaises(ToolCatalogError):
            self.store.publish(catalog)
        with self.assertRaises(ToolCatalogError):
            other.publish(sample_catalog(4))

    def test_first_v2_revision_advances_beyond_v1_current_and_bundled_seed(self):
        other = ToolCatalogStore(self.folder, schema_version=2)
        for revision in (1, 2):
            candidate = sample_catalog(revision)
            candidate["schemaVersion"] = 2
            candidate["tools"][0]["packages"][0]["mirrors"] = []
            with self.assertRaises(ToolCatalogError) as caught:
                other.publish(candidate)
            self.assertEqual(caught.exception.status, 409)
        self.store.publish(sample_catalog(8))
        candidate = sample_catalog(8)
        candidate["schemaVersion"] = 2
        candidate["tools"][0]["packages"][0]["mirrors"] = []
        with self.assertRaises(ToolCatalogError) as caught:
            other.publish(candidate)
        self.assertEqual(caught.exception.status, 409)
        candidate["revision"] = 9
        self.assertTrue(other.publish(candidate)["created"])

    def test_url_immutability_is_shared_across_schema_versions(self):
        self.store.publish(sample_catalog())
        candidate = sample_catalog(4)
        candidate["schemaVersion"] = 2
        package = candidate["tools"][0]["packages"][0]
        package["url"] = sample_catalog()["tools"][0]["packages"][0]["url"]
        package["mirrors"] = []
        other = ToolCatalogStore(self.folder, schema_version=2)
        with self.assertRaises(ToolCatalogError) as caught:
            other.publish(candidate)
        self.assertEqual(caught.exception.status, 409)
        self.assertIn("immutable", caught.exception.message)
        self.assertEqual(self.store.current()["revision"], 1)
        with self.assertRaises(ToolCatalogError):
            other.current()


class V2CatalogValidationTests(unittest.TestCase):
    def sample(self):
        catalog = sample_catalog()
        catalog["schemaVersion"] = 2
        catalog["tools"][0]["packages"][0]["mirrors"] = []
        return catalog

    def test_v1_strict_fields_and_v2_origin_whitelist(self):
        old = sample_catalog()
        old["tools"][0]["packages"][0]["mirrors"] = []
        with self.assertRaises(ToolCatalogError):
            validate_catalog(old)
        catalog = self.sample()
        package = catalog["tools"][0]["packages"][0]
        for host in ("zhenxingai.com", "download.zhenxingai.com", "download-backup.zhenxingai.com"):
            package["url"] = f"https://{host}:443/downloads/tools/tool.zip"
            self.assertEqual(validate_catalog(catalog)["tools"][0]["packages"][0]["url"], f"https://{host}/downloads/tools/tool.zip")
        for url in ("https://github.com/tool.zip", "http://zhenxingai.com/downloads/tools/tool.zip",
                    "https://zhenxingai.com.evil.test/downloads/tools/tool.zip", "https://zhenxingai.com/downloads/tools/../tool.zip",
                    "https://zhenxingai.com/downloads/tools/tool.zip?secret=x", "https://u:p@zhenxingai.com/downloads/tools/tool.zip"):
            with self.subTest(url=url), self.assertRaises(ToolCatalogError):
                package["url"] = url
                validate_catalog(catalog)

    def test_mirrors_limit_duplicates_and_conflicting_bytes(self):
        catalog = self.sample()
        package = catalog["tools"][0]["packages"][0]
        for mirrors in ([package["url"]], [package["url"].replace(".com/", ".com:443/")], "not-list",
                        [f"https://zhenxingai.com/downloads/tools/{i}.zip" for i in range(4)]):
            with self.subTest(mirrors=mirrors), self.assertRaises(ToolCatalogError):
                package["mirrors"] = mirrors
                validate_catalog(catalog)
        package["mirrors"] = ["https://download.zhenxingai.com/downloads/tools/backup.zip"]
        other = deepcopy(package)
        other.update(architecture="x86", url=package["mirrors"][0], mirrors=[], sha256="f" * 64)
        catalog["tools"][0]["packages"].append(other)
        with self.assertRaises(ToolCatalogError):
            validate_catalog(catalog)


class V2CatalogHttpTests(CatalogHttpTests):
    def test_separate_v2_publish_catalog_events_and_admin_auth(self):
        self.publish(3)
        catalog = sample_catalog(4)
        catalog["schemaVersion"] = 2
        catalog["tools"][0]["packages"][0]["mirrors"] = []
        for path in ("/v2/tools/catalog", "/v2/tools/events"):
            self.assertEqual(self.request("GET", path)[0], 404)
        self.assertEqual(self.request("POST", "/v2/admin/tools/publish", catalog)[0], 401)
        self.assertEqual(self.request("POST", "/v1/admin/tools/publish", catalog, token=ADMIN_TOKEN)[0], 400)
        status, _, body = self.request("POST", "/v2/admin/tools/publish", catalog, token=ADMIN_TOKEN)
        self.assertEqual(status, 201)
        self.assertEqual(json.loads(body)["verification"]["catalogSchemaVersion"], 2)
        self.assertEqual(json.loads(self.request("GET", "/v1/tools/catalog")[2])["revision"], 3)
        self.assertEqual(json.loads(self.request("GET", "/v2/tools/catalog")[2]), catalog)
        self.assertEqual(self.request("GET", "/v2/admin/tools/catalog")[0], 401)
        self.assertEqual(json.loads(self.request("GET", "/v2/admin/tools/catalog", token=ADMIN_TOKEN)[2]), catalog)
        self.assertEqual(self.request("GET", "/v2/tools/catalog?unexpected=1")[0], 400)
        html = self.request("GET", "/admin/tools-v2")[2]
        self.assertIn(b"/v2/admin/tools/publish", html)
        self.assertNotIn(ADMIN_TOKEN.encode(), html)
        connection = HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        try:
            connection.request("GET", "/v2/tools/events")
            response = connection.getresponse()
            self.assertEqual(response.status, 200)
            _, data = self.read_catalog_event(response)
            self.assertEqual(data["revision"], 4)
        finally:
            connection.close()


class PackageTransportTests(unittest.TestCase):
    def test_fixed_public_ip_tls_host_get_and_headers_do_not_include_credentials(self):
        url = sample_catalog()["tools"][0]["packages"][0]["url"]
        with patch("tool_package_verifier._resolve_with_timeout", return_value=["93.184.216.34"]), patch("tool_package_verifier._PinnedHTTPSConnection") as factory:
            connection = factory.return_value
            response = connection.getresponse.return_value
            with tool_package_verifier._open_package(url, 3) as received:
                self.assertIs(received, response)
            factory.assert_called_once_with("zhenxingai.com", "93.184.216.34", timeout=3)
            args, kwargs = connection.request.call_args
            self.assertEqual(args, ("GET", urlsplit(url).path))
            self.assertEqual(kwargs["headers"]["Accept-Encoding"], "identity")
            self.assertFalse(set(key.lower() for key in kwargs["headers"]) & {"authorization", "cookie", "proxy-authorization"})
            response.close.assert_called_once()
            connection.close.assert_called_once()

    def test_failed_dns_and_resolver_deadline_fail_closed(self):
        with patch("tool_package_verifier.resolve_public_addresses", side_effect=ValueError("secret resolver context")):
            with self.assertRaises(ToolCatalogError) as caught:
                tool_package_verifier._resolve_with_timeout("zhenxingai.com", 1)
            self.assertNotIn("secret", caught.exception.message)
        with patch("tool_package_verifier.resolve_public_addresses", side_effect=lambda _: time.sleep(0.04)):
            with self.assertRaises(ToolCatalogError) as caught:
                tool_package_verifier._resolve_with_timeout("zhenxingai.com", 0.001)
            self.assertIn("timed out", caught.exception.message)


class PackageVerifierCliTests(unittest.TestCase):
    def setUp(self):
        self.packages = use_package_fixture(self)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.input = self.folder / "catalog.json"
        self.output = self.folder / "receipt.json"
        source = Path(__file__).resolve().parents[1] / "scripts" / "verify-tool-packages.py"
        spec = importlib.util.spec_from_file_location("synthetic_verifier_cli", source)
        self.cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.cli)

    def run_cli(self):
        with patch("sys.argv", ["verify-tool-packages", "--catalog", str(self.input), "--receipt", str(self.output)]), patch("sys.stdout", io.StringIO()), patch("sys.stderr", io.StringIO()):
            return self.cli.main()

    def test_success_creates_review_receipt_and_does_not_overwrite_it(self):
        self.input.write_text(json.dumps(sample_catalog(), ensure_ascii=False, indent=2), encoding="utf-8")
        self.assertEqual(self.run_cli(), 0)
        original = self.output.read_bytes()
        receipt = json.loads(original)
        self.assertEqual(receipt["sourceFileSha256"], hashlib.sha256(self.input.read_bytes()).hexdigest())
        self.assertEqual(receipt["originCount"], 1)
        self.assertEqual(self.run_cli(), 1)
        self.assertEqual(self.output.read_bytes(), original)

    def test_failed_package_and_duplicate_json_do_not_create_success_receipt(self):
        catalog = sample_catalog()
        self.input.write_text(json.dumps(catalog), encoding="utf-8")
        self.packages.response(catalog["tools"][0]["packages"][0]["url"], b"bad page")
        self.assertEqual(self.run_cli(), 1)
        self.assertFalse(self.output.exists())
        self.input.write_text('{"schemaVersion":1,"schemaVersion":2}', encoding="utf-8")
        self.assertEqual(self.run_cli(), 1)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
