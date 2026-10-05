"""Frozen SKILL.md submissions and private review decisions.

The client-reported base document is comparison material, not proof that the
document came from an official release. Accepting a revision only records the
administrator's decision; this module never publishes or installs a skill.
"""

from __future__ import annotations

from datetime import datetime, timezone
import difflib
import hashlib
import json
import re
from typing import Any, Callable
from uuid import UUID


MAX_REVISION_BODY_BYTES = 768 * 1024
MAX_REVIEW_BODY_BYTES = 16 * 1024
MAX_DOCUMENT_BYTES = 128 * 1024
SKILL_ID = "ai_agent_workflow"
SKILL_NAME = "zhenxing-assistant"
STATUSES = frozenset(("pending", "accepted", "rejected"))
INTAKE_PATH = "/v1/skill-revisions"
ADMIN_LIST_PATH = "/v1/admin/skill-revisions"
ADMIN_DETAIL_PATH = re.compile(r"^/v1/admin/skill-revisions/([0-9a-fA-F-]{36})$")
ADMIN_REVIEW_PATH = re.compile(r"^/v1/admin/skill-revisions/([0-9a-fA-F-]{36})/review$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_NAME_KEY = re.compile(r"^[ \t]*(?:name|[\"']name[\"'])[ \t]*:", re.MULTILINE)
_SIMPLE_HEADER_FIELD = re.compile(r"^[A-Za-z][A-Za-z0-9_-]*:[ \t]*[^\r\n]*$")


class RevisionError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status
        self.message = message


def _fields(value: Any, required: set[str]) -> dict[str, Any]:
    if type(value) is not dict:
        raise RevisionError(400, "expected a JSON object")
    if value.keys() != required:
        raise RevisionError(400, "invalid skill revision fields")
    return value


def _text(value: Any, field: str, maximum: int, *, allow_empty: bool = False) -> str:
    if type(value) is not str:
        raise RevisionError(400, f"invalid {field}")
    try:
        # Match .NET String.Length and reject lone UTF-16 surrogates before
        # hashing, JSON serialization, or SQLite encounters invalid Unicode.
        length = len(value.encode("utf-16-le")) // 2
    except UnicodeEncodeError as exc:
        raise RevisionError(400, f"invalid Unicode in {field}") from exc
    if length > maximum or (not allow_empty and not value.strip()):
        raise RevisionError(400, f"invalid {field}")
    return value


def submission_uuid(value: Any) -> str:
    if type(value) is not str:
        raise RevisionError(400, "invalid submissionId: expected canonical UUID")
    try:
        parsed = UUID(value)
    except ValueError as exc:
        raise RevisionError(400, "invalid submissionId: expected canonical UUID") from exc
    if parsed.int == 0 or str(parsed) != value.lower():
        raise RevisionError(400, "invalid submissionId: expected nonempty canonical UUID")
    return str(parsed)


def _utc_time(value: Any) -> str:
    if type(value) is not str or len(value) > 40 or "T" not in value:
        raise RevisionError(400, "invalid submittedAt: expected UTC ISO 8601 timestamp")
    try:
        timestamp = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise RevisionError(400, "invalid submittedAt: expected UTC ISO 8601 timestamp") from exc
    if timestamp.tzinfo is None or timestamp.utcoffset() != timezone.utc.utcoffset(timestamp):
        raise RevisionError(400, "invalid submittedAt: UTC offset required")
    # Keep the submitted snapshot intact, including its original UTC spelling.
    return value


def split_document(value: Any, field: str) -> tuple[str, str]:
    if type(value) is not str:
        raise RevisionError(400, f"invalid {field}")
    try:
        size = len(value.encode("utf-8"))
    except UnicodeEncodeError as exc:
        raise RevisionError(400, f"invalid Unicode in {field}") from exc
    if size > MAX_DOCUMENT_BYTES:
        raise RevisionError(400, f"{field} exceeds 128 KiB UTF-8")
    normalized = value.replace("\r\n", "\n")
    end = normalized.find("\n---\n", 4)
    if not normalized.startswith("---\n") or end < 0:
        raise RevisionError(400, f"invalid SKILL.md frontmatter in {field}")
    header, body = normalized[:end + 5], normalized[end + 5:]
    # The shipped header uses simple, flat field lines. Reject alternate YAML
    # key syntax and multiline constructs instead of interpreting arbitrary
    # YAML or accidentally accepting a hidden name key or merge field.
    for line in header[4:-5].split("\n"):
        if line and not line.startswith("#") and _SIMPLE_HEADER_FIELD.fullmatch(line) is None:
            raise RevisionError(400, f"unsupported SKILL.md frontmatter syntax in {field}")
    if (f"name: {SKILL_NAME}" not in header.split("\n") or
            len(_NAME_KEY.findall(header)) != 1):
        raise RevisionError(400, f"invalid skill name in {field}")
    if not body.strip():
        raise RevisionError(400, f"empty SKILL.md body in {field}")
    return header, body


def validate_revision(raw: Any) -> dict[str, Any]:
    data = _fields(raw, {
        "schemaVersion", "submissionId", "submittedAt", "clientVersion",
        "skillId", "skillName", "baseDocument", "modifiedDocument",
        "baseSha256", "modifiedSha256", "changeSummary",
    })
    if type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1:
        raise RevisionError(400, "schemaVersion must be 1")
    if data["skillId"] != SKILL_ID or data["skillName"] != SKILL_NAME:
        raise RevisionError(400, "unsupported skill identity")
    revision = dict(data)
    revision["submissionId"] = submission_uuid(data["submissionId"])
    _utc_time(data["submittedAt"])
    _text(data["clientVersion"], "clientVersion", 80)
    _text(data["changeSummary"], "changeSummary", 2000)
    base_header, base_body = split_document(data["baseDocument"], "baseDocument")
    modified_header, modified_body = split_document(data["modifiedDocument"], "modifiedDocument")
    if base_header != modified_header:
        raise RevisionError(400, "SKILL.md frontmatter must remain unchanged")
    if base_body == modified_body:
        raise RevisionError(400, "SKILL.md body must change")
    for prefix in ("base", "modified"):
        digest = data[prefix + "Sha256"]
        if type(digest) is not str or _SHA256.fullmatch(digest) is None:
            raise RevisionError(400, f"invalid {prefix}Sha256")
        actual = hashlib.sha256(data[prefix + "Document"].encode("utf-8")).hexdigest()
        if digest != actual:
            raise RevisionError(400, f"{prefix}Sha256 does not match the document")
    return revision


def validate_review(raw: Any) -> dict[str, str]:
    data = _fields(raw, {"decision", "reviewNote", "expectedStatus"})
    if type(data["decision"]) is not str or data["decision"] not in ("accepted", "rejected"):
        raise RevisionError(400, "decision must be accepted or rejected")
    if type(data["expectedStatus"]) is not str or data["expectedStatus"] != "pending":
        raise RevisionError(400, "expectedStatus must be pending")
    _text(data["reviewNote"], "reviewNote", 2000, allow_empty=True)
    return dict(data)


def validate_status(value: Any) -> str | None:
    if value is None:
        return None
    if type(value) is not str or value not in STATUSES:
        raise RevisionError(400, "invalid skill revision status")
    return value


def _canonical(payload: dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


class SkillRevisionStore:
    """Uses the parent Store's transactional connection to the same SQLite DB."""

    def __init__(self, connect: Callable, now: Callable[[], str]):
        self._connect = connect
        self._now = now
        with self._connect() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS skill_revisions (
                    submission_id TEXT PRIMARY KEY,
                    skill_id TEXT NOT NULL, skill_name TEXT NOT NULL,
                    client_version TEXT NOT NULL, submitted_at TEXT NOT NULL,
                    received_at TEXT NOT NULL,
                    base_sha256 TEXT NOT NULL, modified_sha256 TEXT NOT NULL,
                    change_summary TEXT NOT NULL,
                    payload_json TEXT NOT NULL, payload_sha256 TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'accepted', 'rejected')),
                    review_note TEXT, reviewed_at TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_skill_revisions_status_received
                    ON skill_revisions(status, received_at DESC, submission_id);
            """)

    @staticmethod
    def _receipt(row: Any) -> dict[str, Any]:
        return {
            "submissionId": row["submission_id"],
            "modifiedSha256": row["modified_sha256"],
            "status": row["status"], "receivedAt": row["received_at"],
        }

    def save(self, revision: dict[str, Any]) -> dict[str, Any]:
        payload = _canonical(revision)
        digest = hashlib.sha256(payload.encode("utf-8")).hexdigest()
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute("SELECT * FROM skill_revisions WHERE submission_id=?",
                                  (revision["submissionId"],)).fetchone()
            if existing:
                if existing["payload_sha256"] != digest or existing["payload_json"] != payload:
                    raise RevisionError(409, "submissionId was already used for different content")
                return {**self._receipt(existing), "created": False}
            received_at = self._now()
            db.execute("""
                INSERT INTO skill_revisions
                    (submission_id, skill_id, skill_name, client_version, submitted_at,
                     received_at, base_sha256, modified_sha256, change_summary,
                     payload_json, payload_sha256)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, (revision["submissionId"], revision["skillId"], revision["skillName"],
                  revision["clientVersion"], revision["submittedAt"], received_at,
                  revision["baseSha256"], revision["modifiedSha256"], revision["changeSummary"],
                  payload, digest))
        return {"submissionId": revision["submissionId"],
                "modifiedSha256": revision["modifiedSha256"], "status": "pending",
                "receivedAt": received_at, "created": True}

    def list(self, status: str | None = None) -> dict[str, Any]:
        status = validate_status(status)
        where = " WHERE status=?" if status is not None else ""
        with self._connect() as db:
            rows = db.execute(
                "SELECT submission_id, modified_sha256, status, received_at, skill_id, skill_name, "
                "client_version, submitted_at, change_summary, review_note, reviewed_at "
                "FROM skill_revisions" + where +
                " ORDER BY received_at DESC, submission_id LIMIT 1000",
                (status,) if status is not None else (),
            ).fetchall()
        return {"revisions": [{
            **self._receipt(row), "skillId": row["skill_id"], "skillName": row["skill_name"],
            "clientVersion": row["client_version"], "submittedAt": row["submitted_at"],
            "changeSummary": row["change_summary"], "reviewNote": row["review_note"],
            "reviewedAt": row["reviewed_at"],
        } for row in rows]}

    def get(self, submission_id: str) -> dict[str, Any]:
        submission_id = submission_uuid(submission_id)
        with self._connect() as db:
            row = db.execute("SELECT * FROM skill_revisions WHERE submission_id=?",
                             (submission_id,)).fetchone()
        if row is None:
            raise RevisionError(404, "unknown skill revision submissionId")
        payload = json.loads(row["payload_json"])
        difference = "\n".join(difflib.unified_diff(
            payload["baseDocument"].replace("\r\n", "\n").split("\n"),
            payload["modifiedDocument"].replace("\r\n", "\n").split("\n"),
            fromfile="base/SKILL.md", tofile="modified/SKILL.md", lineterm="",
        ))
        return {**payload, **self._receipt(row), "reviewNote": row["review_note"],
                "reviewedAt": row["reviewed_at"], "diff": difference}

    def review(self, submission_id: str, review: dict[str, str]) -> dict[str, Any]:
        submission_id = submission_uuid(submission_id)
        review = validate_review(review)
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute("SELECT * FROM skill_revisions WHERE submission_id=?",
                                  (submission_id,)).fetchone()
            if existing is None:
                raise RevisionError(404, "unknown skill revision submissionId")
            if existing["status"] != "pending":
                if (existing["status"] == review["decision"] and
                        existing["review_note"] == review["reviewNote"]):
                    return {**self._receipt(existing), "reviewNote": existing["review_note"],
                            "reviewedAt": existing["reviewed_at"]}
                raise RevisionError(409, "skill revision has already been reviewed")
            reviewed_at = self._now()
            result = db.execute("""
                UPDATE skill_revisions SET status=?, review_note=?, reviewed_at=?
                WHERE submission_id=? AND status=?
            """, (review["decision"], review["reviewNote"], reviewed_at,
                  submission_id, review["expectedStatus"]))
            if result.rowcount != 1:
                raise RevisionError(409, "skill revision review conflict")
        return {**self._receipt(existing), "status": review["decision"],
                "reviewNote": review["reviewNote"], "reviewedAt": reviewed_at}


# Only static markup and script are included in /admin. Submitted content is
# fetched after authentication and rendered as literal text, never HTML.
SKILL_ADMIN_HTML = """
<section aria-labelledby="skillHeading">
  <h2 id="skillHeading">技能修订审核</h2>
  <p class="muted">原文是客户端报告的版本，并非服务器认证的官方来源。采纳只记录审核结果，不会发布或下发技能。</p>
  <p>
    <label for="skillFilter">状态</label>
    <select id="skillFilter">
      <option value="pending">等待审核</option>
      <option value="accepted">已采纳</option>
      <option value="rejected">已退回</option>
      <option value="">全部</option>
    </select>
    <button type="button" id="loadSkills">读取技能列表</button>
    <span id="skillStatus" class="muted" role="status"></span>
  </p>
  <div id="skillList"></div>
  <section id="skillDetail" hidden aria-label="技能修订详情">
    <h3 id="skillTitle"></h3>
    <p id="skillMetadata" class="muted"></p>
    <h4>修改说明</h4>
    <pre id="skillSummary"></pre>
    <h4>客户端报告的原文</h4>
    <pre id="skillBase"></pre>
    <h4>提交的新文</h4>
    <pre id="skillModified"></pre>
    <h4>差异（按 LF 比较）</h4>
    <pre id="skillDiff"></pre>
    <h4>审核记录</h4>
    <pre id="skillReviewRecord"></pre>
    <p><label for="skillReviewNote">审核意见（可留空，最多 2000 字）</label></p>
    <textarea id="skillReviewNote" maxlength="2000" rows="4" style="box-sizing: border-box; width: 100%"></textarea>
    <p>
      <button type="button" id="acceptSkill" disabled>采纳修订</button>
      <button type="button" id="rejectSkill" disabled>退回修订</button>
    </p>
  </section>
</section>
"""

SKILL_ADMIN_SCRIPT = """
const skillStatusEl = document.getElementById('skillStatus');
const skillLabels = { pending: '等待审核', accepted: '已采纳', rejected: '已退回' };
let currentSkillRevision = null;
let skillRequestGeneration = 0;
let skillListGeneration = 0;
let skillReviewBusy = false;

function skillControls() {
  const editable = currentSkillRevision && currentSkillRevision.status === 'pending' && !skillReviewBusy;
  document.getElementById('acceptSkill').disabled = !editable;
  document.getElementById('rejectSkill').disabled = !editable;
  document.getElementById('skillReviewNote').disabled = !editable;
}
async function skillResponse(response) {
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || String(response.status));
  return data;
}
async function loadSkillList() {
  const generation = ++skillListGeneration;
  try {
    const status = document.getElementById('skillFilter').value;
    const suffix = status ? '?status=' + encodeURIComponent(status) : '';
    const data = await skillResponse(await fetch('/v1/admin/skill-revisions' + suffix,
      { headers: headers(), cache: 'no-store' }));
    if (generation !== skillListGeneration) return;
    const list = document.getElementById('skillList');
    list.textContent = '';
    for (const revision of data.revisions) {
      const row = document.createElement('button');
      row.type = 'button';
      row.style.cssText = 'display: block; text-align: left; width: 100%; margin-bottom: 6px';
      row.textContent = revision.skillName + ' · ' + skillLabels[revision.status] + ' · ' + revision.receivedAt + ' · ' + revision.changeSummary;
      row.onclick = () => { if (!skillReviewBusy) showSkillRevision(revision.submissionId); };
      list.appendChild(row);
    }
    skillStatusEl.textContent = '共 ' + data.revisions.length + ' 条（最多显示 1000 条）';
  } catch (error) {
    if (generation === skillListGeneration) skillStatusEl.textContent = '读取失败：' + error.message;
  }
}
async function showSkillRevision(id) {
  const generation = ++skillRequestGeneration;
  currentSkillRevision = null;
  document.getElementById('skillDetail').hidden = true;
  document.getElementById('skillReviewNote').value = '';
  skillControls();
  try {
    const revision = await skillResponse(await fetch('/v1/admin/skill-revisions/' + encodeURIComponent(id),
      { headers: headers(), cache: 'no-store' }));
    if (generation !== skillRequestGeneration) return;
    currentSkillRevision = revision;
    document.getElementById('skillTitle').textContent = revision.skillName + ' · ' + skillLabels[revision.status];
    document.getElementById('skillMetadata').textContent = '提交编号：' + revision.submissionId + '\\n客户端：' + revision.clientVersion + ' · 收到：' + revision.receivedAt;
    document.getElementById('skillSummary').textContent = revision.changeSummary;
    document.getElementById('skillBase').textContent = revision.baseDocument;
    document.getElementById('skillModified').textContent = revision.modifiedDocument;
    document.getElementById('skillDiff').textContent = revision.diff;
    document.getElementById('skillReviewRecord').textContent = revision.reviewedAt
      ? skillLabels[revision.status] + ' · ' + revision.reviewedAt + '\\n' + revision.reviewNote : '尚未审核';
    document.getElementById('skillDetail').hidden = false;
    skillControls();
  } catch (error) {
    if (generation === skillRequestGeneration) skillStatusEl.textContent = '读取失败：' + error.message;
  }
}
async function reviewSkillRevision(decision) {
  if (skillReviewBusy || !currentSkillRevision || currentSkillRevision.status !== 'pending') return;
  const id = currentSkillRevision.submissionId;
  const note = document.getElementById('skillReviewNote').value;
  skillReviewBusy = true;
  skillControls();
  try {
    await skillResponse(await fetch('/v1/admin/skill-revisions/' + encodeURIComponent(id) + '/review', {
      method: 'POST', headers: { ...headers(), 'Content-Type': 'application/json' }, cache: 'no-store',
      body: JSON.stringify({ decision: decision, reviewNote: note, expectedStatus: 'pending' })
    }));
    await showSkillRevision(id);
    await loadSkillList();
    skillStatusEl.textContent = '审核已记录；技能不会自动发布或下发。';
  } catch (error) { skillStatusEl.textContent = '审核未完成：' + error.message; }
  finally { skillReviewBusy = false; skillControls(); }
}
document.getElementById('loadSkills').onclick = loadSkillList;
document.getElementById('skillFilter').onchange = loadSkillList;
document.getElementById('acceptSkill').onclick = () => reviewSkillRevision('accepted');
document.getElementById('rejectSkill').onclick = () => reviewSkillRevision('rejected');
"""
