"""Versioned tool manifests with complete-package verification before publication.

ZIP bytes are served separately. Publication reads every declared origin, never
extracts or runs vendor code, and commits its receipt with the immutable catalog.
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
V2_CATALOG_PATH = "/v2/tools/catalog"
V2_EVENTS_PATH = "/v2/tools/events"
V2_ADMIN_CATALOG_PATH = "/v2/admin/tools/catalog"
V2_PUBLISH_PATH = "/v2/admin/tools/publish"
MAX_CATALOG_BYTES = 4 * 1024 * 1024
MAX_PACKAGE_BYTES = 512 * 1024 * 1024
MAX_TOOLS = 10_000
MAX_REVISION = 2 ** 53 - 1
MAX_STREAM_SECONDS = 300
# Current bundled/deployed v1 seed is revision 2. A first v2 publication must
# move beyond it as well as any newer v1 current revision seen by the server.
V2_INITIAL_REVISION_FLOOR = 2
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
    portable_removal_tools = {"display driver uninstaller.exe", "hibituninstaller-portable.exe"}
    if (executable and parts[-1].casefold() not in portable_removal_tools
            and any(word in parts[-1].casefold() for word in ("setup", "install", "uninstall", "redist"))):
        raise ToolCatalogError(400, "entryPoint must be a portable tool, not an installer")
    return path


def _https_url(value: Any, field: str, *, package: bool = False, schema_version: int = 1) -> str:
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
            allowed = ("zhenxingai.com",) if schema_version == 1 else (
                "zhenxingai.com", "download.zhenxingai.com", "download-backup.zhenxingai.com")
            if (parsed.netloc not in tuple(host + port for host in allowed for port in ("", ":443"))
                    or not parsed.path.startswith("/downloads/tools/") or parsed.query
                    or not suffix.lower().endswith(".zip")
                    or any(not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,119}", part)
                           or part.endswith(".") for part in suffix.split("/"))):
                raise ValueError()
            _relative_path(parsed.path.lstrip("/"), "package path")
    except (ValueError, UnicodeError) as exc:
        raise ToolCatalogError(400, "invalid " + field) from exc
    # An explicit default port is the same asset URL; store one canonical key.
    return parsed._replace(netloc=parsed.hostname).geturl() if package else url


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
    schema = obj["schemaVersion"]
    if type(schema) is not int or schema not in (1, 2):
        raise ToolCatalogError(400, "schemaVersion must be 1 or 2")
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
            package = _exact(raw_package, {"architecture", "url", "sizeBytes", "sha256", "entryPoint", "kind"}
                             | ({"mirrors"} if schema == 2 else set()))
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
            url = _https_url(package["url"], "package url", package=True, schema_version=schema)
            mirrors = []
            if schema == 2:
                if not isinstance(package["mirrors"], list) or len(package["mirrors"]) > 3:
                    raise ToolCatalogError(400, "mirrors must contain at most 3 entries")
                mirrors = [_https_url(value, "package mirror", package=True, schema_version=schema)
                           for value in package["mirrors"]]
                if len(set([url, *mirrors])) != len(mirrors) + 1:
                    raise ToolCatalogError(400, "duplicate package origin")
            asset = (size, sha.lower())
            for origin in (url, *mirrors):
                if origin in package_assets and package_assets[origin] != asset:
                    raise ToolCatalogError(400, "one package URL has conflicting hashes or sizes")
                package_assets[origin] = asset
            clean_package = {"architecture": architecture, "url": url, "sizeBytes": size,
                             "sha256": sha.lower(), "entryPoint": _relative_path(package["entryPoint"], "entryPoint", executable=True),
                             "kind": "portable-zip"}
            if schema == 2:
                clean_package["mirrors"] = mirrors
            packages.append(clean_package)
        tools.append({"id": identity, "name": _text(tool["name"], "name", 160),
                      "category": _text(tool["category"], "category", 100),
                      "categories": _strings(tool["categories"], "categories", 16),
                      "description": _text(tool["description"], "description", 4096, empty=True),
                      "publisher": _text(tool["publisher"], "publisher", 240, empty=True),
                      "version": _text(tool["version"], "version", 100), "tags": _strings(tool["tags"], "tags", 32),
                      "homepage": _https_url(tool["homepage"], "homepage"), "packages": packages,
                      "legacyPath": _relative_path(tool["legacyPath"], "legacyPath", empty=True), "order": tool["order"]})
    clean = {"schemaVersion": schema, "revision": revision, "publishedAt": _utc(obj["publishedAt"]),
             "minClientVersion": version, "tools": tools}
    if len(_canonical(clean).encode("utf-8")) > MAX_CATALOG_BYTES:
        raise ToolCatalogError(413, "tool catalog is too large")
    return clean


def _canonical(data: dict[str, Any]) -> str:
    return json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


class ToolCatalogStore:
    def __init__(self, data_dir: Path, *, schema_version: int = 1):
        if schema_version not in (1, 2):
            raise ValueError("unsupported catalog schema")
        self.schema_version = schema_version
        self.directory = Path(data_dir) / ("tool-catalog" if schema_version == 1 else "tool-catalog-v2")
        self.directory.mkdir(parents=True, exist_ok=True)
        self.path = self.directory / "catalog.sqlite3"
        self._assets_path = Path(data_dir) / "tool-package-assets.sqlite3"
        self._changed = threading.Condition()
        self.stream_slots = threading.BoundedSemaphore(64)
        self.heartbeat_seconds = 15.0
        self._publish_slot = threading.BoundedSemaphore(1)
        with self._connect() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS catalog_db.catalogs (
                    revision INTEGER PRIMARY KEY, payload TEXT NOT NULL, etag TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS catalog_db.catalog_current (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL,
                    FOREIGN KEY(revision) REFERENCES catalogs(revision)
                );
                CREATE TABLE IF NOT EXISTS package_assets (
                    url TEXT PRIMARY KEY, size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS catalog_db.verification_receipts (
                    revision INTEGER PRIMARY KEY, payload TEXT NOT NULL,
                    FOREIGN KEY(revision) REFERENCES catalogs(revision)
                );
            """)
            # Earlier versions registered URLs inside the v1 history database.
            # Preserve those immutable bindings in a common registry so v2 can
            # never publish different bytes at an already-published v1 URL.
            db.execute("BEGIN IMMEDIATE")
            legacy = db.execute("SELECT name FROM catalog_db.sqlite_master WHERE type='table' AND name='package_assets'").fetchone()
            if legacy:
                for url, size, sha in db.execute("SELECT url, size_bytes, sha256 FROM catalog_db.package_assets").fetchall():
                    old = db.execute("SELECT size_bytes, sha256 FROM main.package_assets WHERE url=?", (url,)).fetchone()
                    if old is not None and old != (size, sha):
                        raise ToolCatalogError(500, "legacy package bindings conflict with the shared registry")
                    db.execute("INSERT OR IGNORE INTO main.package_assets VALUES (?, ?, ?)", (url, size, sha))

    @contextmanager
    def _connect(self):
        db = sqlite3.connect(self._assets_path, timeout=10)
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("PRAGMA synchronous=FULL")
        # A shared main DB serializes URL registration across both schemas. The
        # attached per-schema DB retains independent revisions/current/receipts.
        # Rollback-journal transactions commit both databases atomically.
        db.execute("PRAGMA journal_mode=DELETE")
        db.execute("ATTACH DATABASE ? AS catalog_db", (str(self.path),))
        db.execute("PRAGMA catalog_db.synchronous=FULL")
        db.execute("PRAGMA catalog_db.journal_mode=DELETE")
        legacy_path = self.directory.parent / "tool-catalog" / "catalog.sqlite3"
        if self.schema_version == 2 and legacy_path.exists():
            db.execute("ATTACH DATABASE ? AS v1_db", (str(legacy_path),))
        try:
            with db:
                yield db
        finally:
            db.close()

    def snapshot(self) -> tuple[dict[str, Any], str]:
        with self._connect() as db:
            row = db.execute("SELECT c.payload, c.etag FROM catalogs c JOIN catalog_current p ON p.revision=c.revision WHERE p.singleton=1").fetchone()
        if row is None:
            raise ToolCatalogError(503 if self.schema_version == 1 else 404, "tool catalog has not been published")
        try:
            return json.loads(row[0]), row[1]
        except (json.JSONDecodeError, TypeError) as exc:
            raise ToolCatalogError(500, "tool catalog storage is invalid") from exc

    def current(self) -> dict[str, Any]:
        return self.snapshot()[0]

    def verification_receipt(self, revision: int) -> dict[str, Any]:
        with self._connect() as db:
            row = db.execute("SELECT payload FROM verification_receipts WHERE revision=?", (revision,)).fetchone()
        if row is None:
            raise ToolCatalogError(404, "no package verification receipt for this revision")
        return json.loads(row[0])

    def publish(self, raw: Any) -> dict[str, Any]:
        catalog = validate_catalog(raw)
        if catalog["schemaVersion"] != self.schema_version:
            raise ToolCatalogError(400, "catalog schema does not match this publication endpoint")
        if not self._publish_slot.acquire(blocking=False):
            raise ToolCatalogError(503, "tool catalog verification is busy; retry later")
        try:
            return self._publish_verified(catalog)
        finally:
            self._publish_slot.release()

    def _preflight(self, db, catalog, payload):
        revision = catalog["revision"]
        existing = db.execute("SELECT payload, etag FROM catalogs WHERE revision=?", (revision,)).fetchone()
        if existing is not None:
            if existing[0] != payload:
                raise ToolCatalogError(409, "revision already exists with different contents")
        current = db.execute("SELECT revision FROM catalog_current WHERE singleton=1").fetchone()
        if existing is None and current is not None and revision <= current[0]:
            raise ToolCatalogError(409, "new revision must be greater than current revision")
        if existing is None and current is None and self.schema_version == 2:
            baseline = V2_INITIAL_REVISION_FLOOR
            attached = {row[1] for row in db.execute("PRAGMA database_list")}
            if "v1_db" in attached:
                old_current = db.execute("SELECT revision FROM v1_db.catalog_current WHERE singleton=1").fetchone()
                if old_current is not None:
                    baseline = max(baseline, old_current[0])
            if revision <= baseline:
                raise ToolCatalogError(409, "first v2 revision must be greater than the v1 current/seed revision")
        for tool in catalog["tools"]:
            for package in tool["packages"]:
                for origin in (package["url"], *package.get("mirrors", [])):
                    old = db.execute("SELECT size_bytes, sha256 FROM package_assets WHERE url=?", (origin,)).fetchone()
                    if old is not None and old != (package["sizeBytes"], package["sha256"]):
                        raise ToolCatalogError(409, "published package URLs are immutable; use a new URL")
        return existing

    def _register_assets(self, db, catalog):
        for tool in catalog["tools"]:
            for package in tool["packages"]:
                for origin in (package["url"], *package.get("mirrors", [])):
                    db.execute("INSERT OR IGNORE INTO package_assets VALUES (?, ?, ?)",
                               (origin, package["sizeBytes"], package["sha256"]))

    def _publish_verified(self, catalog):
        # Metadata conflicts are checked before network I/O and rechecked under
        # the write transaction afterwards. Existing receipts enable safe retries;
        # legacy revisions acquire evidence only after successful verification.
        payload = _canonical(catalog)
        revision = catalog["revision"]
        digest = hashlib.sha256(payload.encode("utf-8")).hexdigest()
        etag = f'"tools-{revision}-{digest}"'
        with self._connect() as db:
            existing = self._preflight(db, catalog, payload)
            evidence = db.execute("SELECT payload FROM verification_receipts WHERE revision=?", (revision,)).fetchone()
            if existing is not None and evidence is not None:
                receipt = json.loads(evidence[0])
                if receipt.get("catalogSha256") != digest:
                    raise ToolCatalogError(500, "stored package receipt does not match the catalog")
                return {"revision": revision, "created": False, "etag": existing[1], "verification": receipt}
        from tool_package_verifier import verify_catalog_packages
        receipt = verify_catalog_packages(catalog)
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = self._preflight(db, catalog, payload)
            self._register_assets(db, catalog)
            if existing is not None:
                # Retrying an older revision must never switch the current pointer back.
                db.execute("INSERT OR IGNORE INTO verification_receipts VALUES (?, ?)", (revision, _canonical(receipt)))
                return {"revision": revision, "created": False, "etag": existing[1], "verification": receipt}
            db.execute("INSERT INTO catalogs VALUES (?, ?, ?)", (revision, payload, etag))
            db.execute("INSERT INTO verification_receipts VALUES (?, ?)", (revision, _canonical(receipt)))
            db.execute("INSERT INTO catalog_current VALUES (1, ?) ON CONFLICT(singleton) DO UPDATE SET revision=excluded.revision", (revision,))
        with self._changed:
            self._changed.notify_all()
        return {"revision": revision, "created": True, "etag": etag, "verification": receipt}

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
<p>粘贴完整目录清单。未列出的工具将从可下载目录移除；客户端已安装的文件不删除。先部署工具 ZIP；服务器完整下载每个声明来源并校验大小、哈希、ZIP 内容和入口后才发布。发布后保留原文件和链接。验收只验证包结构，不执行工具。</p>
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
