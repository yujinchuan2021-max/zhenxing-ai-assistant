"""Loopback prototype for selected toolflow submissions and install events.

Only Python's standard library is used.  This module does not start a server on
import; run ``python server.py`` to listen on 127.0.0.1 explicitly.
Admin endpoints (raw flow detail, statistics, export, skill reviews) require the
Bearer token given via ``--admin-token`` (or ``ZXAI_TOOLFLOW_ADMIN_TOKEN``);
without it they stay disabled.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hmac
import json
import os
from pathlib import Path
import re
import sqlite3
import time
from typing import Any, Callable
from urllib.parse import parse_qs, urlsplit
from uuid import UUID

from benchmarks import (
    BenchmarkError, BenchmarkStore, INTAKE_PATH as BENCHMARK_INTAKE,
    MAX_BODY as MAX_BENCHMARK_BODY, REPORT_PATH as BENCHMARK_REPORT,
    IMAGE_PATH as BENCHMARK_IMAGE, REVIEW_PATH as BENCHMARK_REVIEW,
    ADMIN_IMAGE_PATH as BENCHMARK_ADMIN_IMAGE,
    ADMIN_HTML as BENCHMARK_ADMIN_HTML, ADMIN_SCRIPT as BENCHMARK_ADMIN_SCRIPT,
    validate as validate_benchmark,
)

from link_probe import LinkCheck, UnsafeLink, check_download_link, normalize_public_https_url
import skill_library
import tool_catalog
from skill_revisions import (
    ADMIN_DETAIL_PATH as SKILL_DETAIL_PATH, ADMIN_LIST_PATH as SKILL_LIST_PATH,
    ADMIN_REVIEW_PATH as SKILL_REVIEW_PATH, INTAKE_PATH as SKILL_INTAKE_PATH,
    MAX_REVISION_BODY_BYTES, MAX_REVIEW_BODY_BYTES, RevisionError, SkillRevisionStore,
    SKILL_ADMIN_HTML, SKILL_ADMIN_SCRIPT, validate_revision, validate_review,
)


MAX_BODY_BYTES = 8 * 1024 * 1024
FLOW_PATH = re.compile(r"^/v1/toolflows/([0-9a-fA-F-]{36})$")
EVENT_PATH = re.compile(r"^/v1/toolflows/([0-9a-fA-F-]{36})/events$")
EVENT_KINDS = frozenset(
    ("download_started", "download_succeeded", "download_failed",
     "install_succeeded", "install_failed", "verified")
)
METRIC_KINDS = frozenset(
    ("download_clicked", "download_requested", "download_succeeded", "download_failed")
)
ADMIN_FLOW_LIST_PATH = "/v1/admin/flows"
ADMIN_EXPORT_PATH = "/v1/admin/export.jsonl"


class ApiError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status
        self.message = message


def _object_without_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    obj: dict[str, Any] = {}
    for key, value in pairs:
        if key in obj:
            raise ValueError(f"duplicate JSON key: {key}")
        obj[key] = value
    return obj


def _exact_fields(value: Any, required: set[str], optional: set[str] = frozenset()) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ApiError(400, "expected a JSON object")
    missing = required - value.keys()
    extra = value.keys() - required - optional
    if missing or extra:
        raise ApiError(400, f"invalid fields: missing={sorted(missing)}, extra={sorted(extra)}")
    return value


def _text(value: Any, field: str, limit: int, *, allow_empty: bool = False) -> str:
    if not isinstance(value, str) or len(value) > limit or (not allow_empty and not value.strip()):
        raise ApiError(400, f"invalid {field}")
    return value


def _nullable_text(value: Any, field: str, limit: int) -> str | None:
    return None if value is None else _text(value, field, limit)


def _uuid(value: Any, field: str) -> str:
    try:
        parsed = UUID(value)
    except (TypeError, ValueError, AttributeError) as exc:
        raise ApiError(400, f"invalid {field}: expected UUID") from exc
    if not isinstance(value, str) or str(parsed) != value.lower():
        raise ApiError(400, f"invalid {field}: expected canonical UUID")
    return str(parsed)


def _utc_time(value: Any, field: str) -> str:
    if not isinstance(value, str) or len(value) > 40:
        raise ApiError(400, f"invalid {field}: expected UTC ISO 8601 timestamp")
    try:
        dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise ApiError(400, f"invalid {field}: expected UTC ISO 8601 timestamp") from exc
    if dt.tzinfo is None or dt.utcoffset() != timezone.utc.utcoffset(dt):
        raise ApiError(400, f"invalid {field}: UTC offset required")
    return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _url(value: Any, field: str) -> str | None:
    if value is None:
        return None
    try:
        return normalize_public_https_url(value)
    except UnsafeLink as exc:
        raise ApiError(400, f"invalid {field}: {exc}") from exc


def validate_flow(raw: Any) -> dict[str, Any]:
    data = _exact_fields(raw, {
        "schemaVersion", "submissionId", "flowId", "origin", "selectedAt",
        "flowName", "projectGoal", "goalDescription", "flowText", "conversation", "items",
    })
    if type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1:
        raise ApiError(400, "schemaVersion must be 1")
    origin = data["origin"]
    if origin not in ("assistant", "user"):
        raise ApiError(400, "origin must be assistant or user")
    conversation = data["conversation"]
    if not isinstance(conversation, list) or len(conversation) > 200:
        raise ApiError(400, "conversation must have at most 200 messages")
    clean_messages = []
    for message in conversation:
        msg = _exact_fields(message, {"role", "content", "at"})
        if msg["role"] not in ("user", "assistant"):
            raise ApiError(400, "conversation role must be user or assistant")
        clean_messages.append({
            "role": msg["role"],
            "content": _text(msg["content"], "conversation content", 8192),
            "at": None if msg["at"] is None else _utc_time(msg["at"], "conversation.at"),
        })
    items = data["items"]
    if not isinstance(items, list) or not 1 <= len(items) <= 32:
        raise ApiError(400, "items must contain 1 to 32 tools/resources")
    clean_items = []
    seen: set[str] = set()
    for item in items:
        row = _exact_fields(item, {
            "itemId", "name", "kind", "version", "sourceUrl", "downloadUrl", "installTargetKey",
        })
        item_id = _uuid(row["itemId"], "itemId")
        if item_id in seen:
            raise ApiError(400, "duplicate itemId")
        seen.add(item_id)
        clean_items.append({
            "itemId": item_id,
            "name": _text(row["name"], "item.name", 120),
            "kind": _text(row["kind"], "item.kind", 64),
            "version": _nullable_text(row["version"], "item.version", 80),
            "sourceUrl": _url(row["sourceUrl"], "item.sourceUrl"),
            "downloadUrl": _url(row["downloadUrl"], "item.downloadUrl"),
            "installTargetKey": _nullable_text(row["installTargetKey"], "item.installTargetKey", 100),
        })
    return {
        "schemaVersion": 1,
        "submissionId": _uuid(data["submissionId"], "submissionId"),
        "flowId": _uuid(data["flowId"], "flowId"),
        "origin": origin,
        "selectedAt": _utc_time(data["selectedAt"], "selectedAt"),
        "flowName": _text(data["flowName"], "flowName", 120),
        "projectGoal": _text(data["projectGoal"], "projectGoal", 65536),
        "goalDescription": _text(data["goalDescription"], "goalDescription", 4096),
        "flowText": _text(data["flowText"], "flowText", 65536),
        "conversation": clean_messages,
        "items": clean_items,
    }


def validate_event(raw: Any, path_flow_id: str) -> dict[str, Any]:
    event = _exact_fields(raw, {"eventId", "itemId", "kind", "at", "detail"})
    if not isinstance(event["kind"], str) or event["kind"] not in EVENT_KINDS:
        raise ApiError(400, "unknown event kind")
    return {
        "eventId": _uuid(event["eventId"], "eventId"),
        "flowId": _uuid(path_flow_id, "flowId"),
        "itemId": _uuid(event["itemId"], "itemId"),
        "kind": event["kind"],
        "at": _utc_time(event["at"], "at"),
        "detail": _nullable_text(event["detail"], "detail", 1000),
    }


def validate_metrics(raw: Any) -> dict[str, Any]:
    """结构化计数汇总（下载按钮点击 / 真实下载请求等）。不含对话正文与 URL。"""
    data = _exact_fields(raw, {"schemaVersion", "batchId", "counts"})
    if type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1:
        raise ApiError(400, "schemaVersion must be 1")
    counts = data["counts"]
    if not isinstance(counts, list) or not 1 <= len(counts) <= 64:
        raise ApiError(400, "counts must contain 1 to 64 entries")
    clean_counts = []
    seen: set[tuple[str, str]] = set()
    for entry in counts:
        row = _exact_fields(entry, {"kind", "tool", "count"})
        if not isinstance(row["kind"], str) or row["kind"] not in METRIC_KINDS:
            raise ApiError(400, "unknown metric kind")
        tool = row["tool"]
        if (not isinstance(tool, str) or not tool.strip() or len(tool) > 120
                or any(ord(ch) < 32 for ch in tool)):
            raise ApiError(400, "invalid metric tool")
        count = row["count"]
        if type(count) is not int or not 1 <= count <= 1_000_000:
            raise ApiError(400, "metric count must be an integer between 1 and 1000000")
        key = (row["kind"], tool)
        if key in seen:
            raise ApiError(400, "duplicate metric kind/tool pair")
        seen.add(key)
        clean_counts.append({"kind": row["kind"], "tool": tool, "count": count})
    return {
        "schemaVersion": 1,
        "batchId": _uuid(data["batchId"], "batchId"),
        "counts": clean_counts,
    }


def _canonical(data: dict[str, Any]) -> str:
    return json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


class Store:
    def __init__(self, path: Path):
        self.path = path
        self.data_dir = path.parent
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS flows (
                    flow_id TEXT PRIMARY KEY, submission_id TEXT UNIQUE NOT NULL,
                    origin TEXT NOT NULL, selected_at TEXT NOT NULL,
                    flow_name TEXT, project_goal TEXT,
                    goal_description TEXT NOT NULL, payload_json TEXT NOT NULL,
                    received_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS items (
                    flow_id TEXT NOT NULL, item_id TEXT NOT NULL, name TEXT NOT NULL,
                    kind TEXT NOT NULL, version TEXT, source_url TEXT, download_url TEXT,
                    install_target_key TEXT, link_status TEXT NOT NULL DEFAULT 'unchecked',
                    link_http_status INTEGER, link_checked_at TEXT,
                    PRIMARY KEY (flow_id, item_id),
                    FOREIGN KEY (flow_id) REFERENCES flows(flow_id)
                );
                CREATE TABLE IF NOT EXISTS events (
                    event_id TEXT PRIMARY KEY, flow_id TEXT NOT NULL, item_id TEXT NOT NULL,
                    kind TEXT NOT NULL, at TEXT NOT NULL, detail TEXT, payload_json TEXT NOT NULL,
                    received_at TEXT NOT NULL,
                    FOREIGN KEY (flow_id, item_id) REFERENCES items(flow_id, item_id)
                );
                CREATE INDEX IF NOT EXISTS idx_events_item ON events(flow_id, item_id);
                CREATE TABLE IF NOT EXISTS metric_batches (
                    batch_id TEXT PRIMARY KEY, payload_json TEXT NOT NULL, received_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS metric_counts (
                    batch_id TEXT NOT NULL, kind TEXT NOT NULL, tool TEXT NOT NULL,
                    count INTEGER NOT NULL,
                    PRIMARY KEY (batch_id, kind, tool),
                    FOREIGN KEY (batch_id) REFERENCES metric_batches(batch_id)
                );
                CREATE INDEX IF NOT EXISTS idx_metric_counts_kind ON metric_counts(kind);
            """)
            # Earlier local prototype databases have neither column. Keep their
            # original rows readable; all newly accepted submissions require both.
            columns = {row["name"] for row in db.execute("PRAGMA table_info(flows)")}
            if "flow_name" not in columns:
                db.execute("ALTER TABLE flows ADD COLUMN flow_name TEXT")
            if "project_goal" not in columns:
                db.execute("ALTER TABLE flows ADD COLUMN project_goal TEXT")
            db.execute("CREATE INDEX IF NOT EXISTS idx_flows_name ON flows(flow_name)")
        self.skill_revisions = SkillRevisionStore(self._connect, self._now)
        self.benchmarks = BenchmarkStore(self._connect, self._now)
        self.skill_library = skill_library.SkillLibraryStore(self._connect, self._now)
        self.tool_catalog = tool_catalog.ToolCatalogStore(self.data_dir)
        self.tool_catalog_v2 = tool_catalog.ToolCatalogStore(self.data_dir, schema_version=2)

    @contextmanager
    def _connect(self):
        db = sqlite3.connect(self.path, timeout=10)
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA foreign_keys = ON")
        try:
            with db:
                yield db
        finally:
            db.close()

    @staticmethod
    def _now() -> str:
        return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")

    def save_flow(self, flow: dict[str, Any]) -> bool:
        payload = _canonical(flow)
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute(
                "SELECT flow_id, submission_id, payload_json FROM flows WHERE flow_id=? OR submission_id=?",
                (flow["flowId"], flow["submissionId"]),
            ).fetchone()
            if existing:
                if existing["flow_id"] == flow["flowId"] and existing["submission_id"] == flow["submissionId"] and existing["payload_json"] == payload:
                    return False
                raise ApiError(409, "flowId or submissionId was already used for different content")
            db.execute(
                """INSERT INTO flows (flow_id, submission_id, origin, selected_at,
                                      flow_name, project_goal, goal_description,
                                      payload_json, received_at)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (flow["flowId"], flow["submissionId"], flow["origin"], flow["selectedAt"],
                 flow["flowName"], flow["projectGoal"], flow["goalDescription"],
                 payload, self._now()),
            )
            for item in flow["items"]:
                db.execute("""
                    INSERT INTO items (flow_id, item_id, name, kind, version, source_url,
                                       download_url, install_target_key)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """, (flow["flowId"], item["itemId"], item["name"], item["kind"],
                      item["version"], item["sourceUrl"], item["downloadUrl"], item["installTargetKey"]))
        return True

    def save_event(self, event: dict[str, Any]) -> bool:
        payload = _canonical(event)
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute("SELECT payload_json FROM events WHERE event_id=?", (event["eventId"],)).fetchone()
            if existing:
                if existing["payload_json"] == payload:
                    return False
                raise ApiError(409, "eventId was already used for different content")
            item = db.execute(
                "SELECT 1 FROM items WHERE flow_id=? AND item_id=?", (event["flowId"], event["itemId"])
            ).fetchone()
            if not item:
                raise ApiError(404, "unknown flowId or itemId")
            db.execute("INSERT INTO events VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                       (event["eventId"], event["flowId"], event["itemId"], event["kind"],
                        event["at"], event["detail"], payload, self._now()))
        return True

    def save_metrics(self, batch: dict[str, Any]) -> bool:
        payload = _canonical(batch)
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute("SELECT payload_json FROM metric_batches WHERE batch_id=?",
                                  (batch["batchId"],)).fetchone()
            if existing:
                if existing["payload_json"] == payload:
                    return False
                raise ApiError(409, "batchId was already used for different content")
            db.execute("INSERT INTO metric_batches VALUES (?, ?, ?)",
                       (batch["batchId"], payload, self._now()))
            for entry in batch["counts"]:
                db.execute("INSERT INTO metric_counts VALUES (?, ?, ?, ?)",
                           (batch["batchId"], entry["kind"], entry["tool"], entry["count"]))
        return True

    def admin_flow_list(self) -> dict[str, Any]:
        with self._connect() as db:
            rows = db.execute("""
                SELECT f.flow_id, f.submission_id, f.origin, f.selected_at, f.flow_name,
                       f.received_at,
                       (SELECT COUNT(*) FROM items i WHERE i.flow_id = f.flow_id) AS item_count,
                       (SELECT COUNT(*) FROM events e WHERE e.flow_id = f.flow_id) AS event_count
                FROM flows f ORDER BY f.received_at DESC, f.flow_id LIMIT 1000
            """).fetchall()
        return {"flows": [{
            "flowId": row["flow_id"], "submissionId": row["submission_id"], "origin": row["origin"],
            "selectedAt": row["selected_at"], "flowName": row["flow_name"], "receivedAt": row["received_at"],
            "itemCount": row["item_count"], "eventCount": row["event_count"],
        } for row in rows]}

    def export_lines(self) -> list[str]:
        """导出用 JSONL：每条原始提交一行（含完整对话与计数）；仅管理员可读。"""
        with self._connect() as db:
            flows = [row["payload_json"] for row in
                     db.execute("SELECT payload_json FROM flows ORDER BY received_at")]
            events = [row["payload_json"] for row in
                      db.execute("SELECT payload_json FROM events ORDER BY received_at")]
            batches = [row["payload_json"] for row in
                       db.execute("SELECT payload_json FROM metric_batches ORDER BY received_at")]
        return [json.dumps({"type": kind, "payload": json.loads(payload)},
                           ensure_ascii=False, separators=(",", ":"))
                for kind, payloads in (("flow", flows), ("event", events), ("metrics", batches))
                for payload in payloads]

    def get_flow(self, flow_id: str) -> dict[str, Any]:
        with self._connect() as db:
            row = db.execute("SELECT payload_json FROM flows WHERE flow_id=?", (flow_id,)).fetchone()
            if not row:
                raise ApiError(404, "unknown flowId")
            flow = json.loads(row["payload_json"])
            checks = db.execute(
                "SELECT item_id, link_status, link_http_status, link_checked_at FROM items WHERE flow_id=?",
                (flow_id,),
            ).fetchall()
            flow["linkChecks"] = {
                check["item_id"]: {"status": check["link_status"], "httpStatus": check["link_http_status"],
                                   "checkedAt": check["link_checked_at"]} for check in checks
            }
            return flow

    def check_link(self, flow_id: str, item_id: str, checker: Callable[[str], LinkCheck]) -> dict[str, Any]:
        with self._connect() as db:
            item = db.execute(
                "SELECT download_url FROM items WHERE flow_id=? AND item_id=?", (flow_id, item_id)
            ).fetchone()
        if item is None:
            raise ApiError(404, "unknown flowId or itemId")
        if item["download_url"] is None:
            raise ApiError(400, "item has no downloadUrl")
        result = checker(item["download_url"])
        checked_at = self._now()
        with self._connect() as db:
            db.execute("""
                UPDATE items SET link_status=?, link_http_status=?, link_checked_at=?
                WHERE flow_id=? AND item_id=?
            """, (result.status, result.http_status, checked_at, flow_id, item_id))
        return {"flowId": flow_id, "itemId": item_id, "status": result.status,
                "httpStatus": result.http_status, "checkedAt": checked_at}

    def stats(self) -> dict[str, Any]:
        with self._connect() as db:
            scalar = lambda sql: db.execute(sql).fetchone()[0]
            counts = lambda sql: {str(row[0]): row[1] for row in db.execute(sql)}
            source_hosts = counts("""
                SELECT CASE WHEN source_url IS NULL THEN 'unspecified'
                            ELSE substr(source_url, 9, instr(substr(source_url, 9), '/') - 1) END, COUNT(*)
                FROM items GROUP BY 1
            """)
            versions = [dict(row) for row in db.execute("""
                SELECT name, COALESCE(version, 'unspecified') AS version, COUNT(*) AS selectedCount
                FROM items GROUP BY name, version ORDER BY selectedCount DESC, name LIMIT 100
            """)]
            item_activity = [dict(row) for row in db.execute("""
                SELECT i.name, COALESCE(i.version, 'unspecified') AS version,
                       i.kind, COUNT(DISTINCT i.flow_id || ':' || i.item_id) AS selectedCount,
                       SUM(CASE WHEN e.kind='download_succeeded' THEN 1 ELSE 0 END) AS downloadSucceeded,
                       SUM(CASE WHEN e.kind='download_failed' THEN 1 ELSE 0 END) AS downloadFailed,
                       SUM(CASE WHEN e.kind='install_succeeded' THEN 1 ELSE 0 END) AS installSucceeded,
                       SUM(CASE WHEN e.kind='install_failed' THEN 1 ELSE 0 END) AS installFailed,
                       SUM(CASE WHEN e.kind='verified' THEN 1 ELSE 0 END) AS verified
                FROM items i LEFT JOIN events e ON e.flow_id=i.flow_id AND e.item_id=i.item_id
                GROUP BY i.name, i.version, i.kind
                ORDER BY selectedCount DESC, i.name LIMIT 100
            """)]
            top_flow_names = [dict(row) for row in db.execute("""
                SELECT flow_name AS flowName, COUNT(*) AS selectedCount
                FROM flows WHERE flow_name IS NOT NULL
                GROUP BY flow_name ORDER BY selectedCount DESC, flow_name LIMIT 100
            """)]
            metrics_by_kind = counts("SELECT kind, SUM(count) FROM metric_counts GROUP BY kind")
            tool_totals: dict[str, dict[str, int]] = {}
            for row in db.execute("SELECT tool, kind, SUM(count) AS total FROM metric_counts GROUP BY tool, kind"):
                tool_totals.setdefault(row["tool"], {})[row["kind"]] = row["total"]
            top_tools = sorted(({
                "tool": tool, "downloadClicked": kinds.get("download_clicked", 0),
                "downloadRequested": kinds.get("download_requested", 0),
                "downloadSucceeded": kinds.get("download_succeeded", 0),
                "downloadFailed": kinds.get("download_failed", 0),
                "total": sum(kinds.values()),
            } for tool, kinds in tool_totals.items()),
                key=lambda item: (-item["total"], item["tool"]))
            return {
                "selectedFlows": scalar("SELECT COUNT(*) FROM flows"),
                "topFlowNames": top_flow_names,
                "selectedItems": scalar("SELECT COUNT(*) FROM items"),
                "flowsByOrigin": counts("SELECT origin, COUNT(*) FROM flows GROUP BY origin"),
                "itemsByKind": counts("SELECT kind, COUNT(*) FROM items GROUP BY kind"),
                "itemsBySourceHost": source_hosts,
                "versions": versions,
                "itemActivity": item_activity,
                "eventsByKind": counts("SELECT kind, COUNT(*) FROM events GROUP BY kind"),
                "linkStatusCounts": counts("SELECT link_status, COUNT(*) FROM items GROUP BY link_status"),
                "downloadMetrics": {"byKind": metrics_by_kind, "topTools": top_tools[:100]},
            }


ADMIN_PAGE_HTML = """<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>枕星工具流与技能审核后台</title>
<style>
  body { font-family: system-ui, "Microsoft YaHei UI", sans-serif; margin: 24px; background: #101418; color: #e8eaed; }
  h1 { font-size: 18px; }
  input, button, select, textarea { font: inherit; padding: 6px 10px; border-radius: 6px; border: 1px solid #3c4043; background: #1b1f24; color: inherit; }
  button { cursor: pointer; }
  #list div { padding: 6px 8px; border-bottom: 1px solid #2a2f34; cursor: pointer; }
  #list div:hover { background: #1b1f24; }
  pre { white-space: pre-wrap; word-break: break-word; background: #0b0e11; padding: 12px; border-radius: 8px; }
  .muted { color: #9aa0a6; font-size: 12px; }
</style>
</head>
<body>
<h1>枕星工具流与技能审核后台</h1>
<p class="muted">本机后台：工具流只读；技能修订可以采纳或退回。数据与操作均需管理员令牌。</p>
<p>
  <input id="token" type="password" placeholder="管理员令牌" style="width: 260px">
  <button onclick="loadList()">读取列表</button>
  <button onclick="doExport()">导出 JSONL</button>
  <span id="status" class="muted"></span>
</p>
<h2>工具流提交</h2>
<div id="list"></div>
<pre id="detail" hidden></pre>
<script>
const statusEl = document.getElementById('status');
function headers() { return { 'Authorization': 'Bearer ' + document.getElementById('token').value }; }
async function loadList() {
  try {
    const response = await fetch('/v1/admin/flows', { headers: headers() });
    if (!response.ok) throw new Error((await response.json()).error || response.status);
    const data = await response.json();
    const list = document.getElementById('list');
    list.textContent = '';
    for (const flow of data.flows) {
      const row = document.createElement('div');
      row.textContent = flow.flowName + ' · ' + flow.origin + ' · 条目 ' + flow.itemCount + ' · 事件 ' + flow.eventCount + ' · ' + flow.receivedAt;
      row.onclick = () => showFlow(flow.flowId);
      list.appendChild(row);
    }
    statusEl.textContent = '共 ' + data.flows.length + ' 条提交';
  } catch (error) { statusEl.textContent = '读取失败：' + error.message; }
}
async function showFlow(id) {
  try {
    const response = await fetch('/v1/toolflows/' + id, { headers: headers() });
    if (!response.ok) throw new Error((await response.json()).error || response.status);
    const detail = document.getElementById('detail');
    detail.hidden = false;
    detail.textContent = JSON.stringify(await response.json(), null, 2);
  } catch (error) { statusEl.textContent = '读取失败：' + error.message; }
}
async function doExport() {
  try {
    const response = await fetch('/v1/admin/export.jsonl', { headers: headers() });
    if (!response.ok) throw new Error((await response.json()).error || response.status);
    const blob = await response.blob();
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.download = 'toolflow-export.jsonl';
    link.click();
    URL.revokeObjectURL(link.href);
  } catch (error) { statusEl.textContent = '导出失败：' + error.message; }
}
</script>
</body>
</html>
""".replace("</body>", SKILL_ADMIN_HTML + "<script>\n" + SKILL_ADMIN_SCRIPT + "\n</script>\n</body>")
ADMIN_PAGE_HTML = ADMIN_PAGE_HTML.replace("</body>", BENCHMARK_ADMIN_HTML + "<script>\n" + BENCHMARK_ADMIN_SCRIPT + "\n</script>\n</body>")
ADMIN_PAGE_HTML = ADMIN_PAGE_HTML.replace("</body>", skill_library.ADMIN_HTML + "<script>\n" + skill_library.ADMIN_SCRIPT + "\n</script>\n</body>")
ADMIN_PAGE_HTML = ADMIN_PAGE_HTML.replace("</body>", '<p><a href="/admin/tools">工具目录发布</a></p></body>')


def make_handler(store: Store, checker: Callable[[str], LinkCheck] = check_download_link,
                 admin_token: str | None = None):
    class Handler(BaseHTTPRequestHandler):
        server_version = "ZhenxingToolflowLocal/0.2"

        def _require_admin(self) -> None:
            """私有后台（原始内容、统计、导出、审核）的管理员 Bearer 令牌访问控制。"""
            if not admin_token:
                raise ApiError(403, "admin access is disabled; restart with --admin-token to enable it")
            header = self.headers.get("Authorization", "")
            if not header.startswith("Bearer "):
                raise ApiError(401, "admin token required")
            if not hmac.compare_digest(header[len("Bearer "):].encode("utf-8"), admin_token.encode("utf-8")):
                raise ApiError(403, "invalid admin token")

        def _reply_html(self, html: str) -> None:
            payload = html.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def _handle_export(self) -> None:
            try:
                self._require_admin()
                lines = store.export_lines()
            except ApiError as exc:
                self._reply(exc.status, {"error": exc.message})
                return
            except sqlite3.Error:
                self._reply(500, {"error": "storage unavailable"})
                return
            payload = ("\n".join(lines) + ("\n" if lines else "")).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/x-ndjson; charset=utf-8")
            self.send_header("Content-Disposition", "attachment; filename=toolflow-export.jsonl")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, format: str, *args: object) -> None:
            pass  # Never log request paths or bodies; URLs and conversations are sensitive.

        def _reply(self, status: int, body: dict[str, Any]) -> None:
            payload = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def _handle_tool_catalog(self, catalog_store, expected_path) -> None:
            try:
                if self.path != expected_path:
                    raise ApiError(400, "tool catalog does not accept query parameters")
                catalog, etag = catalog_store.snapshot()
            except (ApiError, tool_catalog.ToolCatalogError) as exc:
                self._reply(exc.status, {"error": exc.message})
                return
            except sqlite3.Error:
                self._reply(500, {"error": "storage unavailable"})
                return
            candidates = [value.strip().removeprefix("W/") for value in self.headers.get("If-None-Match", "").split(",")]
            not_modified = etag in candidates or "*" in candidates
            payload = b"" if not_modified else json.dumps(catalog, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            self.send_response(304 if not_modified else 200)
            self.send_header("ETag", etag)
            self.send_header("Cache-Control", "no-cache")
            self.send_header("X-Content-Type-Options", "nosniff")
            if not not_modified:
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            if payload:
                self.wfile.write(payload)

        def _handle_tool_events(self, catalog_store, expected_path) -> None:
            try:
                if self.path != expected_path:
                    raise ApiError(400, "tool events do not accept query parameters")
                last_id = self.headers.get("Last-Event-ID")
                if last_id is not None and (not re.fullmatch(r"[0-9]{1,16}", last_id)
                                            or int(last_id) > tool_catalog.MAX_REVISION):
                    raise ApiError(400, "invalid Last-Event-ID")
                catalog, etag = catalog_store.snapshot()
            except (ApiError, tool_catalog.ToolCatalogError) as exc:
                self._reply(exc.status, {"error": exc.message})
                return
            except sqlite3.Error:
                self._reply(500, {"error": "storage unavailable"})
                return
            if not catalog_store.stream_slots.acquire(blocking=False):
                self._reply(503, {"error": "tool notification stream is busy; retry later"})
                return
            # Bounded connections, periodic heartbeat and eventual reconnect keep
            # dead clients from holding a thread forever. A reconnect always gets
            # the current revision, including when intermediate events were missed.
            self.close_connection = True
            started = time.monotonic()
            try:
                self.connection.settimeout(30)
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream; charset=utf-8")
                self.send_header("Cache-Control", "no-cache")
                self.send_header("X-Accel-Buffering", "no")
                self.send_header("X-Content-Type-Options", "nosniff")
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(b"retry: 5000\n\n")
                while True:
                    revision = catalog["revision"]
                    data = json.dumps({"revision": revision, "etag": etag}, separators=(",", ":"))
                    self.wfile.write(f"event: catalog\nid: {revision}\ndata: {data}\n\n".encode("utf-8"))
                    self.wfile.flush()
                    while True:
                        remaining = tool_catalog.MAX_STREAM_SECONDS - (time.monotonic() - started)
                        if remaining <= 0:
                            return
                        catalog, etag = catalog_store.wait_for_revision(revision, min(catalog_store.heartbeat_seconds, remaining))
                        if catalog["revision"] != revision:
                            break
                        self.wfile.write(b": heartbeat\n\n")
                        self.wfile.flush()
            except (OSError, sqlite3.Error, tool_catalog.ToolCatalogError):
                pass  # Disconnected stream clients recover using GET catalog.
            finally:
                catalog_store.stream_slots.release()

        def _body(self, maximum: int = MAX_BODY_BYTES) -> Any:
            try:
                length = int(self.headers.get("Content-Length", ""))
            except ValueError as exc:
                raise ApiError(411, "Content-Length required") from exc
            if length < 1 or length > maximum:
                raise ApiError(413, f"request body must be 1 byte to {maximum} bytes")
            if self.headers.get("Content-Type", "").split(";", 1)[0].strip().lower() != "application/json":
                raise ApiError(415, "Content-Type must be application/json")
            try:
                return json.loads(self.rfile.read(length), object_pairs_hook=_object_without_duplicate_keys)
            except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as exc:
                raise ApiError(400, "invalid JSON") from exc

        def _handle(self, action: Callable[[], tuple[int, dict[str, Any]]]) -> None:
            try:
                status, body = action()
            except (ApiError, RevisionError, BenchmarkError, skill_library.SkillLibraryError, tool_catalog.ToolCatalogError) as exc:
                status, body = exc.status, {"error": exc.message}
            except sqlite3.Error:
                status, body = 500, {"error": "storage unavailable"}
            self._reply(status, body)

        def do_POST(self) -> None:
            def action() -> tuple[int, dict[str, Any]]:
                if self.path in (tool_catalog.PUBLISH_PATH, tool_catalog.V2_PUBLISH_PATH):
                    self._require_admin()
                    target = store.tool_catalog if self.path == tool_catalog.PUBLISH_PATH else store.tool_catalog_v2
                    receipt = target.publish(self._body(tool_catalog.MAX_CATALOG_BYTES))
                    return (201 if receipt["created"] else 200), receipt
                if self.path == "/v1/skill-submissions":
                    receipt = store.skill_library.submit(skill_library.validate(self._body(skill_library.MAX_BODY)), self.headers.get("X-Skill-Token"))
                    return (201 if receipt["created"] else 200), receipt
                new_skill_review = skill_library.REVIEW.fullmatch(self.path)
                if new_skill_review:
                    self._require_admin()
                    return 200, store.skill_library.review(new_skill_review.group(1), self._body(8192))
                if self.path == BENCHMARK_INTAKE:
                    receipt = store.benchmarks.submit(validate_benchmark(self._body(MAX_BENCHMARK_BODY)),
                                                       self.headers.get("X-Benchmark-Token"))
                    return (201 if receipt["created"] else 200), receipt
                benchmark_review = BENCHMARK_REVIEW.fullmatch(self.path)
                if benchmark_review:
                    self._require_admin()
                    return 200, store.benchmarks.review(benchmark_review.group(1), self._body(8192))
                if self.path == SKILL_INTAKE_PATH:
                    revision = validate_revision(self._body(MAX_REVISION_BODY_BYTES))
                    receipt = store.skill_revisions.save(revision)
                    return (201 if receipt["created"] else 200), receipt
                review_match = SKILL_REVIEW_PATH.fullmatch(self.path)
                if review_match:
                    self._require_admin()
                    review = validate_review(self._body(MAX_REVIEW_BODY_BYTES))
                    return 200, store.skill_revisions.review(review_match.group(1), review)
                if self.path == "/v1/toolflows":
                    flow = validate_flow(self._body())
                    created = store.save_flow(flow)
                    return (201 if created else 200), {"flowId": flow["flowId"], "created": created}
                event_match = EVENT_PATH.fullmatch(self.path)
                if event_match:
                    event = validate_event(self._body(), event_match.group(1))
                    created = store.save_event(event)
                    return (201 if created else 200), {"eventId": event["eventId"], "created": created}
                if self.path == "/v1/links/check":
                    body = _exact_fields(self._body(), {"flowId", "itemId"})
                    checked = store.check_link(_uuid(body["flowId"], "flowId"),
                                               _uuid(body["itemId"], "itemId"), checker)
                    return 200, checked
                if self.path == "/v1/metrics":
                    batch = validate_metrics(self._body())
                    created = store.save_metrics(batch)
                    return (201 if created else 200), {"batchId": batch["batchId"], "created": created}
                raise ApiError(404, "unknown endpoint")
            self._handle(action)

        def do_GET(self) -> None:
            path = urlsplit(self.path).path
            if path in (tool_catalog.CATALOG_PATH, tool_catalog.V2_CATALOG_PATH):
                target = store.tool_catalog if path == tool_catalog.CATALOG_PATH else store.tool_catalog_v2
                self._handle_tool_catalog(target, path)
                return
            if path in (tool_catalog.EVENTS_PATH, tool_catalog.V2_EVENTS_PATH):
                target = store.tool_catalog if path == tool_catalog.EVENTS_PATH else store.tool_catalog_v2
                self._handle_tool_events(target, path)
                return
            if self.path in ("/admin/tools", "/admin/tools-v2"):
                html = tool_catalog.ADMIN_HTML if self.path == "/admin/tools" else tool_catalog.ADMIN_HTML.replace("/v1/admin/tools/", "/v2/admin/tools/")
                self._reply_html(html)
                return
            if self.path == "/admin":
                self._reply_html(ADMIN_PAGE_HTML)
                return
            if self.path == ADMIN_EXPORT_PATH:
                self._handle_export()
                return
            admin_image_match = BENCHMARK_ADMIN_IMAGE.fullmatch(self.path)
            image_match = BENCHMARK_IMAGE.fullmatch(self.path) or admin_image_match
            if image_match:
                try:
                    if admin_image_match:
                        self._require_admin()
                    image = store.benchmarks.image(image_match.group(1), admin=bool(admin_image_match))
                except (BenchmarkError, ApiError) as exc:
                    self._reply(exc.status, {"error": exc.message})
                    return
                except sqlite3.Error:
                    self._reply(500, {"error": "storage unavailable"})
                    return
                self.send_response(200)
                self.send_header("Content-Type", "image/png")
                self.send_header("X-Content-Type-Options", "nosniff")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Content-Length", str(len(image)))
                self.end_headers()
                self.wfile.write(image)
                return

            def action() -> tuple[int, dict[str, Any]]:
                path = urlsplit(self.path).path
                if path in (tool_catalog.ADMIN_CATALOG_PATH, tool_catalog.V2_ADMIN_CATALOG_PATH):
                    self._require_admin()
                    if self.path != path:
                        raise ApiError(400, "admin tool catalog does not accept query parameters")
                    target = store.tool_catalog if path == tool_catalog.ADMIN_CATALOG_PATH else store.tool_catalog_v2
                    return 200, target.current()
                if path == "/v1/skills":
                    try:
                        query = parse_qs(urlsplit(self.path).query, keep_blank_values=True, strict_parsing=True)
                        if query.keys() - {"page"} or any(len(v) != 1 for v in query.values()): raise ValueError()
                        page = int(query.get("page", ["0"])[0])
                    except ValueError as exc:
                        raise ApiError(400, "invalid catalogue query") from exc
                    return 200, store.skill_library.catalogue(page=page)
                new_skill_detail = skill_library.DETAIL.fullmatch(self.path)
                if new_skill_detail:
                    return 200, store.skill_library.detail(new_skill_detail.group(1))
                new_skill_receipt = skill_library.RECEIPT.fullmatch(self.path)
                if new_skill_receipt:
                    return 200, store.skill_library.get(new_skill_receipt.group(1), self.headers.get("X-Skill-Token"))
                new_skill_admin = skill_library.ADMIN_DETAIL.fullmatch(self.path)
                if new_skill_admin:
                    self._require_admin()
                    return 200, store.skill_library.get(new_skill_admin.group(1), admin=True)
                if path == "/v1/admin/skill-submissions":
                    self._require_admin()
                    try:
                        query = parse_qs(urlsplit(self.path).query, keep_blank_values=True, strict_parsing=True)
                        if query.keys() - {"status"} or any(len(v) != 1 for v in query.values()): raise ValueError()
                    except ValueError as exc:
                        raise ApiError(400, "invalid skill query") from exc
                    return 200, store.skill_library.admin_list(query.get("status", ["pending"])[0])
                if path in ("/v1/benchmarks/leaderboard", "/v1/benchmarks/reports", "/v1/admin/benchmarks"):
                    if path.startswith("/v1/admin/"):
                        self._require_admin()
                    try:
                        query = parse_qs(urlsplit(self.path).query, keep_blank_values=True, strict_parsing=True)
                        allowed = {"status"} if path.startswith("/v1/admin/") else ({"page"} if path.endswith("/reports") else {"sortBy", "page", "cpu", "gpu"})
                        if query.keys() - allowed or any(len(values) != 1 for values in query.values()):
                            raise ValueError()
                        if path.startswith("/v1/admin/"):
                            return 200, store.benchmarks.admin_list(query.get("status", ["pending"])[0])
                        if path.endswith("/reports"):
                            return 200, store.benchmarks.reports(int(query.get("page", ["0"])[0]))
                        return 200, store.benchmarks.leaderboard(query.get("sortBy", ["gaming"])[0],
                            int(query.get("page", ["0"])[0]), query.get("cpu", [""])[0], query.get("gpu", [""])[0])
                    except ValueError as exc:
                        raise ApiError(400, "invalid benchmark query") from exc
                if self.path == "/v1/benchmarks/mine":
                    return 200, store.benchmarks.mine(self.headers.get("X-Benchmark-Token"))
                if self.path == "/v1/benchmarks/heatmaps":
                    return 200, store.benchmarks.images()
                benchmark_report = BENCHMARK_REPORT.fullmatch(self.path)
                if benchmark_report:
                    return 200, store.benchmarks.detail(benchmark_report.group(1), self.headers.get("X-Benchmark-Token"))
                if urlsplit(self.path).path == SKILL_LIST_PATH:
                    self._require_admin()
                    try:
                        query = parse_qs(urlsplit(self.path).query, keep_blank_values=True, strict_parsing=True)
                    except ValueError as exc:
                        raise ApiError(400, "invalid skill revision query") from exc
                    if query.keys() - {"status"} or any(len(values) != 1 for values in query.values()):
                        raise ApiError(400, "invalid skill revision query")
                    return 200, store.skill_revisions.list(query.get("status", [None])[0])
                detail_match = SKILL_DETAIL_PATH.fullmatch(self.path)
                if detail_match:
                    self._require_admin()
                    return 200, store.skill_revisions.get(detail_match.group(1))
                if self.path == "/health":
                    return 200, {"status": "ok"}
                if self.path == "/v1/stats":
                    self._require_admin()
                    return 200, store.stats()
                if self.path == ADMIN_FLOW_LIST_PATH:
                    self._require_admin()
                    return 200, store.admin_flow_list()
                match = FLOW_PATH.fullmatch(self.path)
                if match:
                    self._require_admin()
                    return 200, store.get_flow(_uuid(match.group(1), "flowId"))
                raise ApiError(404, "unknown endpoint")
            self._handle(action)

        def do_DELETE(self) -> None:
            def action() -> tuple[int, dict[str, Any]]:
                match = BENCHMARK_REPORT.fullmatch(self.path)
                if not match:
                    raise ApiError(404, "unknown endpoint")
                return 200, store.benchmarks.withdraw(match.group(1), self.headers.get("X-Benchmark-Token"))
            self._handle(action)

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser(description="Local selected-toolflow analysis prototype")
    parser.add_argument("--port", type=int, default=8768)
    parser.add_argument("--db", type=Path, default=Path(__file__).with_name("toolflows.sqlite3"))
    parser.add_argument("--admin-token", default=os.environ.get("ZXAI_TOOLFLOW_ADMIN_TOKEN"),
                        help="Bearer token for the private admin endpoints and skill reviews; "
                             "omit to keep them disabled")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("port must be between 1 and 65535")
    admin_token = args.admin_token.strip() if args.admin_token else None
    if admin_token and len(admin_token) < 16:
        print("Warning: admin token is shorter than 16 characters; use a longer random value.")
    store = Store(args.db)
    server = ThreadingHTTPServer(("127.0.0.1", args.port),
                                 make_handler(store, admin_token=admin_token))
    print(f"Toolflow prototype listening only on http://127.0.0.1:{args.port}")
    if admin_token:
        print(f"Private admin console: http://127.0.0.1:{args.port}/admin (Bearer token required)")
    else:
        print("Admin endpoints are disabled; restart with --admin-token TOKEN to enable /admin.")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
