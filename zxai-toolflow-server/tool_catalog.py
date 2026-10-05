"""Versioned, read-only tool manifests; ZIP bytes are served separately by nginx.

No package is downloaded, extracted or executed here. A SQLite transaction stores
each immutable manifest and switches the current pointer before waking SSE readers.
"""
from __future__ import annotations

from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import threading
from typing import Any
from urllib.parse import urlsplit

CATALOG_PATH = "/v1/tools/catalog"
EVENTS_PATH = "/v1/tools/events"
ADMIN_CATALOG_PATH = "/v1/admin/tools/catalog"
PUBLISH_PATH = "/v1/admin/tools/publish"
MAX_CATALOG_BYTES = 4 * 1024 * 1024
MAX_PACKAGE_BYTES = 512 * 1024 * 1024
MAX_TOOLS = 10_000
MAX_REVISION = 2 ** 53 - 1
MAX_STREAM_SECONDS = 300
_SLUG = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
_WINDOWS_RESERVED = re.compile(r"(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?\Z", re.I)


class ToolCatalogError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status, self.message = status, message


def _exact(value: Any, fields: set[str]) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != fields:
        raise ToolCatalogError(400, "invalid tool catalog fields")
    return value


def _text(value: Any, field: str, maximum: int, *, empty: bool = False) -> str:
    if not isinstance(value, str):
        raise ToolCatalogError(400, "invalid " + field)
    try:
        valid = len(value.encode("utf-8")) <= maximum
    except UnicodeEncodeError:
        valid = False
    if not valid or any(ord(ch) < 32 or ord(ch) == 127 for ch in value):
        raise ToolCatalogError(400, "invalid " + field)
    value = value.strip()
    if not empty and not value:
        raise ToolCatalogError(400, "invalid " + field)
    return value


def _strings(value: Any, field: str, maximum: int) -> list[str]:
    if not isinstance(value, list) or len(value) > maximum:
        raise ToolCatalogError(400, "invalid " + field)
    clean = [_text(item, field, 100) for item in value]
    if len({item.casefold() for item in clean}) != len(clean):
        raise ToolCatalogError(400, "duplicate " + field)
    return clean


def _relative_path(value: Any, field: str, *, executable: bool = False, empty: bool = False) -> str:
    # UTF-8 byte limits are narrower than the client's UTF-16 path limits.
    path = _text(value, field, 240, empty=empty)
    if not path and empty:
        return path
    # Preserve the exact Windows path rather than silently fixing an unsafe one.
    if path != value or len(path) > 240 or "\\" in path:
        raise ToolCatalogError(400, "invalid " + field)
    parts = path.split("/")
    for part in parts:
        if (not part or part in (".", "..") or len(part.encode("utf-8")) > 120
                or part != part.strip() or part.endswith(".")
                or any(ch in '<>:"\\|?*' for ch in part)
                or _WINDOWS_RESERVED.fullmatch(part)):
            raise ToolCatalogError(400, "unsafe " + field)
    if executable and not path.lower().endswith(".exe"):
        raise ToolCatalogError(400, "entryPoint must be a relative .exe path")
    if executable and any(word in parts[-1].casefold() for word in ("setup", "install", "uninstall", "redist")):
        raise ToolCatalogError(400, "entryPoint must be a portable tool, not an installer")
    return path


def _https_url(value: Any, field: str, *, package: bool = False) -> str:
    url = _text(value, field, 1500, empty=not package)
    if not url and not package:
        return url
    try:
        parsed = urlsplit(url)
        valid = (parsed.scheme == "https" and bool(parsed.hostname)
                 and parsed.username is None and parsed.password is None
                 and parsed.port in (None, 443) and not parsed.fragment
                 and "\\" not in url and not any(ch.isspace() for ch in url))
        if not valid:
            raise ValueError()
        # A hostname must also be representable by the client's URI parser.
        host = parsed.hostname.encode("idna").decode("ascii")
        if len(host) > 253 or any(not re.fullmatch(r"[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?", label)
                                  for label in host.rstrip(".").split(".")):
            raise ValueError()
        if package:
            suffix = parsed.path.removeprefix("/downloads/tools/")
            if (parsed.netloc not in ("zhenxingai.com", "zhenxingai.com:443")
                    or not parsed.path.startswith("/downloads/tools/") or parsed.query
                    or not suffix.lower().endswith(".zip")
                    or any(not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,119}", part)
                           or part.endswith(".") for part in suffix.split("/"))):
                raise ValueError()
            _relative_path(parsed.path.lstrip("/"), "package path")
    except (ValueError, UnicodeError) as exc:
        raise ToolCatalogError(400, "invalid " + field) from exc
    # An explicit default port is the same asset URL; store one canonical key.
    return parsed._replace(netloc="zhenxingai.com").geturl() if package else url


def _utc(value: Any) -> str:
    value = _text(value, "publishedAt", 40)
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None or parsed.utcoffset() != timezone.utc.utcoffset(parsed):
            raise ValueError()
    except ValueError as exc:
        raise ToolCatalogError(400, "publishedAt must be a UTC ISO 8601 timestamp") from exc
    return parsed.isoformat().replace("+00:00", "Z")


def validate_catalog(raw: Any) -> dict[str, Any]:
    obj = _exact(raw, {"schemaVersion", "revision", "publishedAt", "minClientVersion", "tools"})
    if type(obj["schemaVersion"]) is not int or obj["schemaVersion"] != 1:
        raise ToolCatalogError(400, "schemaVersion must be 1")
    revision = obj["revision"]
    if type(revision) is not int or not 1 <= revision <= MAX_REVISION:
        raise ToolCatalogError(400, "revision must be a positive integer")
    version = _text(obj["minClientVersion"], "minClientVersion", 32)
    if not re.fullmatch(r"(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){3}", version) \
            or any(int(part) > 65535 for part in version.split(".")):
        raise ToolCatalogError(400, "minClientVersion must contain four version components")
    if not isinstance(obj["tools"], list) or len(obj["tools"]) > MAX_TOOLS:
        raise ToolCatalogError(400, "too many tools")
    tools, ids = [], set()
    package_assets: dict[str, tuple[int, str]] = {}
    for raw_tool in obj["tools"]:
        tool = _exact(raw_tool, {"id", "name", "category", "categories", "description", "publisher",
                                 "version", "tags", "homepage", "packages", "legacyPath", "order"})
        identity = _text(tool["id"], "id", 80)
        if not _SLUG.fullmatch(identity) or identity in ids or _WINDOWS_RESERVED.fullmatch(identity):
            raise ToolCatalogError(400, "invalid or duplicate tool id")
        ids.add(identity)
        if type(tool["order"]) is not int or not 0 <= tool["order"] <= 2 ** 31 - 1:
            raise ToolCatalogError(400, "order must be a non-negative integer")
        if not isinstance(tool["packages"], list) or len(tool["packages"]) > 4:
            raise ToolCatalogError(400, "packages must contain at most 4 entries")
        packages, architectures = [], set()
        for raw_package in tool["packages"]:
            package = _exact(raw_package, {"architecture", "url", "sizeBytes", "sha256", "entryPoint", "kind"})
            architecture = package["architecture"]
            if not isinstance(architecture, str) or architecture not in ("x64", "arm64", "x86", "any") \
                    or architecture in architectures:
                raise ToolCatalogError(400, "invalid or duplicate package architecture")
            architectures.add(architecture)
            if package["kind"] != "portable-zip":
                raise ToolCatalogError(400, "package kind must be portable-zip")
            size = package["sizeBytes"]
            if type(size) is not int or not 1 <= size <= MAX_PACKAGE_BYTES:
                raise ToolCatalogError(400, "sizeBytes must be between 1 byte and 512 MiB")
            sha = package["sha256"]
            if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", sha):
                raise ToolCatalogError(400, "invalid package sha256")
            url = _https_url(package["url"], "package url", package=True)
            asset = (size, sha.lower())
            if url in package_assets and package_assets[url] != asset:
                raise ToolCatalogError(400, "one package URL has conflicting hashes or sizes")
            package_assets[url] = asset
            packages.append({"architecture": architecture, "url": url, "sizeBytes": size,
                             "sha256": sha.lower(), "entryPoint": _relative_path(package["entryPoint"], "entryPoint", executable=True),
                             "kind": "portable-zip"})
        tools.append({"id": identity, "name": _text(tool["name"], "name", 160),
                      "category": _text(tool["category"], "category", 100),
                      "categories": _strings(tool["categories"], "categories", 16),
                      "description": _text(tool["description"], "description", 4096, empty=True),
                      "publisher": _text(tool["publisher"], "publisher", 240, empty=True),
                      "version": _text(tool["version"], "version", 100), "tags": _strings(tool["tags"], "tags", 32),
                      "homepage": _https_url(tool["homepage"], "homepage"), "packages": packages,
                      "legacyPath": _relative_path(tool["legacyPath"], "legacyPath", empty=True), "order": tool["order"]})
    clean = {"schemaVersion": 1, "revision": revision, "publishedAt": _utc(obj["publishedAt"]),
             "minClientVersion": version, "tools": tools}
    if len(_canonical(clean).encode("utf-8")) > MAX_CATALOG_BYTES:
        raise ToolCatalogError(413, "tool catalog is too large")
    return clean


def _canonical(data: dict[str, Any]) -> str:
    return json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


class ToolCatalogStore:
    def __init__(self, data_dir: Path):
        self.directory = Path(data_dir) / "tool-catalog"
        self.directory.mkdir(parents=True, exist_ok=True)
        self.path = self.directory / "catalog.sqlite3"
        self._changed = threading.Condition()
        self.stream_slots = threading.BoundedSemaphore(64)
        self.heartbeat_seconds = 15.0
        with self._connect() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS catalogs (
                    revision INTEGER PRIMARY KEY, payload TEXT NOT NULL, etag TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS catalog_current (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL,
                    FOREIGN KEY(revision) REFERENCES catalogs(revision)
                );
                CREATE TABLE IF NOT EXISTS package_assets (
                    url TEXT PRIMARY KEY, size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL
                );
            """)

    @contextmanager
    def _connect(self):
        db = sqlite3.connect(self.path, timeout=10)
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("PRAGMA synchronous=FULL")
        try:
            with db:
                yield db
        finally:
            db.close()

    def snapshot(self) -> tuple[dict[str, Any], str]:
        with self._connect() as db:
            row = db.execute("SELECT c.payload, c.etag FROM catalogs c JOIN catalog_current p ON p.revision=c.revision WHERE p.singleton=1").fetchone()
        if row is None:
            raise ToolCatalogError(503, "tool catalog has not been published")
        try:
            return json.loads(row[0]), row[1]
        except (json.JSONDecodeError, TypeError) as exc:
            raise ToolCatalogError(500, "tool catalog storage is invalid") from exc

    def current(self) -> dict[str, Any]:
        return self.snapshot()[0]

    def publish(self, raw: Any) -> dict[str, Any]:
        catalog = validate_catalog(raw)
        payload = _canonical(catalog)
        revision = catalog["revision"]
        digest = hashlib.sha256(payload.encode("utf-8")).hexdigest()
        etag = f'"tools-{revision}-{digest}"'
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute("SELECT payload, etag FROM catalogs WHERE revision=?", (revision,)).fetchone()
            if existing is not None:
                if existing[0] != payload:
                    raise ToolCatalogError(409, "revision already exists with different contents")
                # Retrying an older revision must never switch the current pointer back.
                return {"revision": revision, "created": False, "etag": existing[1]}
            current = db.execute("SELECT revision FROM catalog_current WHERE singleton=1").fetchone()
            if current is not None and revision <= current[0]:
                raise ToolCatalogError(409, "new revision must be greater than current revision")
            for tool in catalog["tools"]:
                for package in tool["packages"]:
                    old = db.execute("SELECT size_bytes, sha256 FROM package_assets WHERE url=?", (package["url"],)).fetchone()
                    if old is not None and old != (package["sizeBytes"], package["sha256"]):
                        raise ToolCatalogError(409, "published package URLs are immutable; use a new URL")
                    db.execute("INSERT OR IGNORE INTO package_assets VALUES (?, ?, ?)",
                               (package["url"], package["sizeBytes"], package["sha256"]))
            db.execute("INSERT INTO catalogs VALUES (?, ?, ?)", (revision, payload, etag))
            db.execute("INSERT INTO catalog_current VALUES (1, ?) ON CONFLICT(singleton) DO UPDATE SET revision=excluded.revision", (revision,))
        with self._changed:
            self._changed.notify_all()
        return {"revision": revision, "created": True, "etag": etag}

    def wait_for_revision(self, previous: int, timeout: float) -> tuple[dict[str, Any], str]:
        with self._changed:
            current = self.snapshot()
            if current[0]["revision"] == previous:
                self._changed.wait(timeout)
                current = self.snapshot()
            return current


ADMIN_HTML = """<!doctype html><html lang="zh-CN"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>枕星工具目录发布</title>
<style>:root{color-scheme:light dark}body{font:15px system-ui;margin:32px auto;padding:0 20px;max-width:960px}
input,textarea,button{font:inherit;padding:10px;border:1px solid #888;border-radius:6px}textarea{width:100%;box-sizing:border-box;margin:16px 0;font-family:monospace}button{cursor:pointer;margin:8px 8px 0 0}#status{white-space:pre-wrap}</style>
<h1>工具目录发布</h1><p><a href="/admin">返回后台</a></p>
<p>粘贴完整目录清单。未列出的工具将从可下载目录移除；客户端已安装的文件不删除。工具 ZIP 须先放到自有下载目录，发布后保留原文件和链接。</p>
<label>管理员令牌 <input id="token" type="password" autocomplete="off"></label>
<p>读取后修改清单时，请增加 revision 并更新 publishedAt（UTC）。同一修订仅允许重复提交相同内容。</p>
<textarea id="manifest" rows="22" spellcheck="false" placeholder="粘贴经过核对的 JSON 工具清单"></textarea>
<button id="read">读取当前清单</button><button id="publish">发布清单</button><pre id="status" role="status" aria-live="polite"></pre>
<script>
const token=document.getElementById('token'), manifest=document.getElementById('manifest'), status=document.getElementById('status');
async function request(path,body){const options={headers:{Authorization:'Bearer '+token.value}};if(body!==undefined){options.method='POST';options.headers['Content-Type']='application/json';options.body=JSON.stringify(body)}const response=await fetch(path,options);const result=await response.json();if(!response.ok)throw new Error(result.error||'请求失败');return result}
async function run(action){document.querySelectorAll('button').forEach(b=>b.disabled=true);try{await action()}catch(error){status.textContent=error.message}finally{document.querySelectorAll('button').forEach(b=>b.disabled=false)}}
document.getElementById('read').onclick=()=>run(async()=>{const result=await request('/v1/admin/tools/catalog');manifest.value=JSON.stringify(result,null,2);status.textContent='当前修订：'+result.revision});
document.getElementById('publish').onclick=()=>run(async()=>{const result=await request('/v1/admin/tools/publish',JSON.parse(manifest.value));status.textContent=result.created?'已发布修订 '+result.revision:'相同修订已发布，本次为重复提交'});
</script></html>"""
