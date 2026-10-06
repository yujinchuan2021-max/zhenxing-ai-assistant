"""Release gate: read complete own-origin ZIPs without extracting or executing.

The receipt describes byte/structure validation, not program functionality or a
redistribution approval. Production publication calls this module itself and
never accepts an administrator-supplied receipt as a substitute for verification.
"""
from __future__ import annotations

from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import http.client
from pathlib import Path
import queue
import stat
import struct
import tempfile
import threading
import time
from urllib.parse import urlsplit
import zipfile
import zlib

from link_probe import _PinnedHTTPSConnection, resolve_public_addresses
from tool_catalog import MAX_PACKAGE_BYTES, ToolCatalogError, _canonical, _relative_path, validate_catalog

MAX_ZIP_ENTRIES = 10_000
MAX_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
MAX_ENTRY_BYTES = 512 * 1024 * 1024
MAX_TOTAL_DOWNLOAD_BYTES = 32 * 1024 * 1024 * 1024
REQUEST_TIMEOUT_SECONDS = 15.0
PACKAGE_TIMEOUT_SECONDS = 180.0
CATALOG_TIMEOUT_SECONDS = 1800.0
CHUNK_BYTES = 128 * 1024
CLIENT_RECEIPT_FILE = ".zxai-cloud-install.json"


def _fail(message):
    raise ToolCatalogError(502, "tool package verification failed: " + message)


def _resolve_with_timeout(host, timeout):
    # getaddrinfo has no portable timeout; a daemon prevents a stuck OS resolver
    # from holding the publishing request or process shutdown indefinitely.
    result = queue.Queue(maxsize=1)

    def resolve():
        try:
            result.put((True, resolve_public_addresses(host)))
        except Exception:
            result.put((False, None))

    threading.Thread(target=resolve, daemon=True).start()
    try:
        success, addresses = result.get(timeout=timeout)
    except queue.Empty:
        _fail("DNS lookup timed out")
    if not success:
        _fail("DNS lookup did not resolve exclusively to public addresses")
    return addresses


@contextmanager
def _open_package(url, timeout):
    """HTTPS GET pinned to checked public DNS; no proxies, cookies or redirects."""
    parts = urlsplit(url)
    addresses = _resolve_with_timeout(parts.hostname, timeout)
    connection = _PinnedHTTPSConnection(parts.hostname, addresses[0], timeout=timeout)
    response = None
    try:
        connection.request("GET", parts.path, headers={
            "User-Agent": "ZhenxingToolPackageVerifier/1", "Accept": "application/zip",
            "Accept-Encoding": "identity", "Connection": "close"})
        response = connection.getresponse()
        yield response
    finally:
        if response is not None:
            response.close()
        connection.close()


def _check_deadline(deadline):
    if time.monotonic() >= deadline:
        _fail("verification deadline exceeded")


def _download(url, expected_size, expected_sha, destination, deadline, opener):
    _check_deadline(deadline)
    try:
        with opener(url, min(REQUEST_TIMEOUT_SECONDS, deadline - time.monotonic())) as response:
            if response.status != 200:
                # Deliberately never follow even an own-origin redirect: the
                # receipt must prove the exact declared URL served these bytes.
                _fail("source returned HTTP " + str(response.status))
            if response.getheader("Content-Encoding", "identity").lower() not in ("", "identity"):
                _fail("encoded HTTP response is not an immutable ZIP asset")
            media = response.getheader("Content-Type", "").split(";", 1)[0].strip().lower()
            if media in ("text/html", "application/xhtml+xml", "application/json", "text/plain"):
                _fail("source returned a page or error document")
            length = response.getheader("Content-Length")
            if length is not None and (not length.isascii() or not length.isdecimal() or int(length) != expected_size):
                _fail("HTTP Content-Length differs from the catalog")
            digest, total = hashlib.sha256(), 0
            with destination.open("xb") as target:
                while True:
                    _check_deadline(deadline)
                    # The extra byte detects overrun even for chunked responses.
                    block = response.read(min(CHUNK_BYTES, expected_size - total + 1))
                    if not block:
                        break
                    total += len(block)
                    if total > expected_size or total > MAX_PACKAGE_BYTES:
                        _fail("download exceeds the declared byte limit")
                    digest.update(block)
                    target.write(block)
            if total != expected_size:
                _fail("download is truncated or has the wrong size")
            actual = digest.hexdigest()
            if actual != expected_sha:
                _fail("download SHA-256 differs from the catalog")
            return total, actual
    except ToolCatalogError:
        raise
    except (OSError, http.client.HTTPException, ValueError):
        # Network exception strings may include proxy/auth context. Receipts and
        # API responses expose fixed diagnostic text, never response bodies.
        _fail("source could not be completely downloaded")


def _pe_architecture(stream, size):
    head = stream.read(64)
    if len(head) != 64 or head[:2] != b"MZ":
        _fail("entry point is not a Windows PE executable")
    offset = struct.unpack_from("<I", head, 60)[0]
    if offset < 64 or offset > min(size - 24, 1024 * 1024):
        _fail("entry point has an invalid PE header offset")
    stream.seek(offset)
    header = stream.read(24)
    if len(header) != 24 or header[:4] != b"PE\0\0":
        _fail("entry point has an invalid PE signature")
    machine, sections = struct.unpack_from("<HH", header, 4)
    optional_size, flags = struct.unpack_from("<HH", header, 20)
    architecture = {0x8664: "x64", 0x14c: "x86", 0xaa64: "arm64"}.get(machine)
    if architecture is None or not 1 <= sections <= 96 or not flags & 2 or flags & 0x2000:
        _fail("entry point is not a supported executable image")
    if optional_size < (96 if architecture == "x86" else 112) or offset + 24 + optional_size + sections * 40 > size:
        _fail("entry point PE header is truncated")
    optional = stream.read(optional_size)
    magic = struct.unpack_from("<H", optional)[0]
    if magic != (0x10b if architecture == "x86" else 0x20b):
        _fail("entry point PE optional header does not match its architecture")
    return architecture


def _zip_effective_name(info):
    """Accept authenticated Info-ZIP Unicode names without permitting truncation."""
    original = info.orig_filename
    if "\0" in original:
        _fail("ZIP contains a truncated path")
    unicode_name = None
    extra = info.extra
    offset = 0
    while offset < len(extra):
        if len(extra) - offset < 4:
            _fail("ZIP extra field is truncated")
        kind, size = struct.unpack_from("<HH", extra, offset)
        offset += 4
        if offset + size > len(extra):
            _fail("ZIP extra field is truncated")
        value = extra[offset:offset + size]
        offset += size
        if kind != 0x7075:
            continue
        if unicode_name is not None or len(value) < 5 or value[0] != 1:
            _fail("ZIP Unicode path field is invalid")
        try:
            raw_name = original.encode("utf-8" if info.flag_bits & 0x800 else "cp437")
            if struct.unpack_from("<I", value, 1)[0] != zlib.crc32(raw_name):
                _fail("ZIP Unicode path checksum differs")
            unicode_name = value[5:].decode("utf-8", "strict")
        except (UnicodeError, ValueError):
            _fail("ZIP Unicode path encoding is invalid")
        if not unicode_name or "\0" in unicode_name or unicode_name.endswith("/") != original.endswith("/"):
            _fail("ZIP Unicode path is unsafe")
        if info.flag_bits & 0x800 and unicode_name != original:
            _fail("ZIP has conflicting UTF-8 path declarations")
    if info.filename != original and info.filename != unicode_name:
        _fail("ZIP contains an unsafe or implicitly normalized path")
    for candidate in (original, unicode_name):
        if candidate is None:
            continue
        try:
            _relative_path(candidate[:-1] if candidate.endswith("/") else candidate, "ZIP entry")
        except ToolCatalogError:
            _fail("ZIP contains an unsafe Windows path")
    return unicode_name or info.filename


def _verify_archive(path, deadline):
    # Check both ends so HTML/EXE prefixed archives and truncated files cannot
    # pass merely because ZipFile found a central directory somewhere inside.
    with path.open("rb") as stream:
        if stream.read(4) != b"PK\x03\x04":
            _fail("download does not begin with a ZIP file header")
        stream.seek(max(0, path.stat().st_size - 65557))
        tail = stream.read()
        eocd = tail.rfind(b"PK\x05\x06")
        if eocd < 0 or len(tail) - eocd < 22 or eocd + 22 + struct.unpack_from("<H", tail, eocd + 20)[0] != len(tail):
            _fail("ZIP end-of-directory record is missing or invalid")
    result, seen, implied_directories, expanded = {}, {}, set(), 0
    alias_paths, alias_directories = {}, set()
    try:
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if not entries or len(entries) > MAX_ZIP_ENTRIES:
                _fail("ZIP entry count exceeds the limit")
            for info in entries:
                _check_deadline(deadline)
                effective_name = _zip_effective_name(info)
                name = effective_name[:-1] if info.is_dir() else effective_name
                # Different runtimes may use either the legacy spelling or the
                # authenticated Unicode spelling. Both must remain collision-free.
                aliases = {name, info.orig_filename[:-1] if info.is_dir() else info.orig_filename}
                for alias in aliases:
                    alias_folded = alias.casefold()
                    if alias_folded in alias_paths:
                        _fail("ZIP contains colliding legacy or Unicode paths")
                    for parent in Path(alias).parents:
                        parent_name = parent.as_posix().casefold()
                        if str(parent) != "." and alias_paths.get(parent_name) == "file":
                            _fail("ZIP legacy or Unicode file and directory paths collide")
                        if str(parent) != ".":
                            alias_directories.add(parent_name)
                    if not info.is_dir() and alias_folded in alias_directories:
                        _fail("ZIP legacy or Unicode file and directory paths collide")
                for alias in aliases:
                    alias_paths[alias.casefold()] = "directory" if info.is_dir() else "file"
                try:
                    _relative_path(name, "ZIP entry")
                except ToolCatalogError:
                    _fail("ZIP contains an unsafe Windows path")
                folded = name.casefold()
                if folded == CLIENT_RECEIPT_FILE:
                    _fail("ZIP contains the client's reserved installation receipt path")
                if folded in seen:
                    _fail("ZIP contains duplicate or case-colliding paths")
                for parent in Path(name).parents:
                    parent_name = parent.as_posix().casefold()
                    if str(parent) != "." and seen.get(parent_name) == "file":
                        _fail("ZIP file and directory paths collide")
                    if str(parent) != ".":
                        implied_directories.add(parent_name)
                if not info.is_dir() and folded in implied_directories:
                    _fail("ZIP file and directory paths collide")
                seen[folded] = "directory" if info.is_dir() else "file"
                mode = info.external_attr >> 16
                kind = stat.S_IFMT(mode)
                if kind not in (0, stat.S_IFREG, stat.S_IFDIR) or bool(info.external_attr & 0x400):
                    _fail("ZIP contains a link, reparse point or special file")
                if info.flag_bits & 1 or info.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    _fail("ZIP is encrypted or uses unsupported compression")
                if info.file_size > MAX_ENTRY_BYTES or info.file_size < 0:
                    _fail("ZIP entry exceeds the expanded byte limit")
                expanded += info.file_size
                if expanded > MAX_EXPANDED_BYTES:
                    _fail("ZIP expanded bytes exceed the limit")
                total = 0
                with archive.open(info) as stream:
                    while True:
                        _check_deadline(deadline)
                        block = stream.read(CHUNK_BYTES)
                        if not block:
                            break
                        total += len(block)
                        if total > info.file_size:
                            _fail("ZIP entry expands beyond its declared size")
                if total != info.file_size:
                    _fail("ZIP entry is truncated")
                # Reading every entry to EOF above makes ZipFile check each CRC.
                if not info.is_dir():
                    result[folded] = (info.filename, info.file_size, info.CRC)
            return {"fileCount": len(result), "expandedBytes": expanded, "entries": result}
    except ToolCatalogError:
        raise
    except (zipfile.BadZipFile, RuntimeError, OSError, EOFError, ValueError, NotImplementedError, zlib.error):
        _fail("ZIP structure or an entry CRC is invalid")


def verify_catalog_packages(raw):
    """Return a receipt only when every declared main/backup URL fully passes."""
    catalog = validate_catalog(raw)
    payload = _canonical(catalog).encode("utf-8")
    deadline = time.monotonic() + CATALOG_TIMEOUT_SECONDS
    assets = {}
    total_bytes = 0
    for tool in catalog["tools"]:
        for package in tool["packages"]:
            for url in (package["url"], *package.get("mirrors", [])):
                assets.setdefault(url, {"sizeBytes": package["sizeBytes"], "sha256": package["sha256"], "entries": []})
                asset = assets[url]
                profile = {"toolId": tool["id"], "entryPoint": package["entryPoint"], "architecture": package["architecture"]}
                if profile not in asset["entries"]:
                    asset["entries"].append(profile)
    if sum(asset["sizeBytes"] for asset in assets.values()) > MAX_TOTAL_DOWNLOAD_BYTES:
        _fail("catalog exceeds the aggregate download byte limit")
    reports = []
    # Private, generated paths only. Never extract archive-controlled paths.
    with tempfile.TemporaryDirectory(prefix="zxai-package-verification-") as folder:
        for index, (url, asset) in enumerate(assets.items()):
            package_deadline = min(deadline, time.monotonic() + PACKAGE_TIMEOUT_SECONDS)
            path = Path(folder) / (str(index) + ".zip")
            size, digest = _download(url, asset["sizeBytes"], asset["sha256"], path, package_deadline, _open_package)
            metadata = _verify_archive(path, package_deadline)
            verified_entries = []
            with zipfile.ZipFile(path) as archive:
                for profile in asset["entries"]:
                    _check_deadline(package_deadline)
                    found = metadata["entries"].get(profile["entryPoint"].casefold())
                    if found is None:
                        _fail("declared entry point is missing from the ZIP")
                    with archive.open(found[0]) as stream:
                        architecture = _pe_architecture(stream, found[1])
                    if profile["architecture"] != "any" and architecture != profile["architecture"]:
                        _fail("entry point architecture differs from the catalog")
                    verified_entries.append({**profile, "peArchitecture": architecture, "crc32": f"{found[2]:08x}"})
            reports.append({"url": url, "sizeBytes": size, "sha256": digest,
                            "fileCount": metadata["fileCount"], "expandedBytes": metadata["expandedBytes"], "entries": verified_entries})
            total_bytes += size
            path.unlink()
    return {"schema": "zxai-tool-package-verification/1", "catalogSchemaVersion": catalog["schemaVersion"],
            "revision": catalog["revision"], "catalogSha256": hashlib.sha256(payload).hexdigest(),
            "catalogBytes": len(payload), "verifiedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "originCount": len(reports), "downloadedBytes": total_bytes, "toolsExecuted": False, "packages": reports}
