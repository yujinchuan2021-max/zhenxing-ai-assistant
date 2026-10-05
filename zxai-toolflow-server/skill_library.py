"""Text-only skill catalogue and moderated, opt-in community submissions."""
from __future__ import annotations

import hashlib
import hmac
import json
from pathlib import Path
import re
from uuid import UUID

CATEGORIES = frozenset(("development", "design", "writing", "office", "data", "game", "other"))
CATALOGUE_LIMIT = 2000
CATALOGUE_PAGE_SIZE = 100
MAX_BODY = 256 * 1024
DETAIL = re.compile(r"^/v1/skills/([a-zA-Z0-9_-]{1,100})$")
RECEIPT = re.compile(r"^/v1/skill-submissions/([0-9a-f-]{36})$")
ADMIN_DETAIL = re.compile(r"^/v1/admin/skill-submissions/([0-9a-f-]{36})$")
REVIEW = re.compile(r"^/v1/admin/skill-submissions/([0-9a-f-]{36})/review$")


class SkillLibraryError(Exception):
    def __init__(self, status, message):
        self.status, self.message = status, message
        super().__init__(message)


def text(value, name, maximum):
    try:
        valid = isinstance(value, str) and bool(value.strip()) and len(value.encode("utf-8")) <= maximum and "\x00" not in value
    except UnicodeEncodeError:
        valid = False
    if not valid:
        raise SkillLibraryError(400, "invalid " + name)
    return value.strip()


def exact(value, fields):
    if not isinstance(value, dict) or set(value) != set(fields):
        raise SkillLibraryError(400, "invalid fields")
    return value


def canonical_id(value):
    try:
        if not isinstance(value, str) or str(UUID(value)) != value:
            raise ValueError()
        return value
    except (ValueError, TypeError, AttributeError):
        raise SkillLibraryError(400, "invalid submissionId") from None


def owner(token):
    if not isinstance(token, str) or not re.fullmatch(r"[0-9a-f]{64}", token):
        raise SkillLibraryError(401, "skill owner token required")
    return hashlib.sha256(token.encode("ascii")).hexdigest()


def validate(raw):
    obj = exact(raw, ("schemaVersion", "submissionId", "clientVersion", "skill", "license", "shareConsent"))
    if type(obj["schemaVersion"]) is not int or obj["schemaVersion"] != 1 or obj["license"] != "MIT" or obj["shareConsent"] is not True:
        raise SkillLibraryError(400, "explicit MIT sharing consent required")
    row = exact(obj["skill"], ("id", "displayName", "description", "category", "systemPromptFragment", "triggerKeywords"))
    skill_id = text(row["id"], "skill.id", 100)
    if not re.fullmatch(r"usr_[a-zA-Z0-9_-]{1,90}", skill_id):
        raise SkillLibraryError(400, "new custom skill id required")
    category = row["category"]
    if not isinstance(category, str) or category not in CATEGORIES:
        raise SkillLibraryError(400, "invalid category")
    keys = row["triggerKeywords"]
    if not isinstance(keys, list) or not 1 <= len(keys) <= 12:
        raise SkillLibraryError(400, "1 to 12 trigger keywords required")
    keys = [text(k, "keyword", 120) for k in keys]
    if len(set(keys)) != len(keys):
        raise SkillLibraryError(400, "duplicate keywords")
    return dict(schemaVersion=1, submissionId=canonical_id(obj["submissionId"]),
        clientVersion=text(obj["clientVersion"], "clientVersion", 100), license="MIT", shareConsent=True,
        skill=dict(id=skill_id, displayName=text(row["displayName"], "displayName", 240),
            description=text(row["description"], "description", 1600), category=category,
            systemPromptFragment=text(row["systemPromptFragment"], "instructions", 65536), triggerKeywords=keys))


class SkillLibraryStore:
    def __init__(self, connect, now, catalog_path=None):
        self.connect, self.now = connect, now
        self.catalog_path = Path(catalog_path) if catalog_path else Path(__file__).with_name("skill-catalog.json")
        with connect() as db:
            db.executescript("""CREATE TABLE IF NOT EXISTS skill_submissions (
                submission_id TEXT PRIMARY KEY, owner_hash TEXT NOT NULL, payload_json TEXT NOT NULL,
                content_hash TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending', received_at TEXT NOT NULL,
                reviewed_at TEXT, review_note TEXT NOT NULL DEFAULT '');
                CREATE INDEX IF NOT EXISTS idx_skill_submissions_status ON skill_submissions(status,received_at);""")

    @staticmethod
    def receipt(row, created=False):
        return dict(submissionId=row["submission_id"], contentSha256=row["content_hash"],
            status=row["status"], receivedAt=row["received_at"], reviewedAt=row["reviewed_at"],
            reviewNote=row["review_note"], created=created)

    def submit(self, data, token):
        identity = owner(token)
        payload = json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        digest = hashlib.sha256(data["skill"]["systemPromptFragment"].encode("utf-8")).hexdigest()
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            existing = db.execute("SELECT * FROM skill_submissions WHERE submission_id=?", (data["submissionId"],)).fetchone()
            if existing:
                if not hmac.compare_digest(existing["owner_hash"], identity):
                    raise SkillLibraryError(404, "submission not found")
                if existing["payload_json"] != payload:
                    raise SkillLibraryError(409, "submissionId already used for different content")
                return self.receipt(existing)
            db.execute("INSERT INTO skill_submissions(submission_id,owner_hash,payload_json,content_hash,received_at) VALUES(?,?,?,?,?)",
                (data["submissionId"], identity, payload, digest, self.now()))
            return self.receipt(db.execute("SELECT * FROM skill_submissions WHERE submission_id=?", (data["submissionId"],)).fetchone(), True)

    def get(self, sid, token=None, admin=False):
        canonical_id(sid)
        identity = None if admin else owner(token)
        with self.connect() as db:
            row = db.execute("SELECT * FROM skill_submissions WHERE submission_id=?", (sid,)).fetchone()
        if row is None or (not admin and not hmac.compare_digest(row["owner_hash"], identity)):
            raise SkillLibraryError(404, "submission not found")
        result = self.receipt(row)
        if admin:
            result["document"] = json.loads(row["payload_json"])
        return result

    def admin_list(self, status="pending"):
        if status not in ("pending", "accepted", "rejected"):
            raise SkillLibraryError(400, "invalid review status")
        with self.connect() as db:
            rows = db.execute("SELECT * FROM skill_submissions WHERE status=? ORDER BY received_at DESC LIMIT 200", (status,)).fetchall()
        return {"items": [dict(self.receipt(r), skill=json.loads(r["payload_json"])["skill"]) for r in rows]}

    def review(self, sid, raw):
        canonical_id(sid)
        obj = exact(raw, ("decision", "note"))
        if obj["decision"] not in ("accepted", "rejected") or not isinstance(obj["note"], str) or len(obj["note"]) > 2000:
            raise SkillLibraryError(400, "invalid review")
        try:
            obj["note"].encode("utf-8")
        except UnicodeEncodeError:
            raise SkillLibraryError(400, "invalid review note") from None
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            if not db.execute("SELECT 1 FROM skill_submissions WHERE submission_id=?", (sid,)).fetchone():
                raise SkillLibraryError(404, "submission not found")
            db.execute("UPDATE skill_submissions SET status=?,reviewed_at=?,review_note=? WHERE submission_id=?",
                (obj["decision"], self.now(), obj["note"], sid))
        return self.get(sid, admin=True)

    def _official(self):
        try:
            raw = json.loads(self.catalog_path.read_text(encoding="utf-8"))
            items = raw["items"]
            if not isinstance(items, list) or len(items) > CATALOGUE_LIMIT:
                raise ValueError()
        except (OSError, ValueError, KeyError, TypeError):
            raise SkillLibraryError(503, "catalogue unavailable") from None
        return raw, items

    def catalogue(self, include_body=False, page=0, community_id=None):
        if type(page) is not int or not 0 <= page <= 10000:
            raise SkillLibraryError(400, "invalid catalogue page")
        raw, items = self._official()
        start = page * CATALOGUE_PAGE_SIZE
        result = [] if community_id else [dict(i) for i in items[start:start + CATALOGUE_PAGE_SIZE]]
        with self.connect() as db:
            if community_id:
                rows = db.execute("SELECT * FROM skill_submissions WHERE submission_id=? AND status='accepted'", (community_id,)).fetchall()
                has_more = False
            else:
                # Official and accepted skills form one sequence; no rows are skipped
                # where the final official page shares space with community submissions.
                remaining = CATALOGUE_PAGE_SIZE - len(result)
                offset = max(0, start - len(items))
                rows = db.execute("SELECT * FROM skill_submissions WHERE status='accepted' ORDER BY reviewed_at DESC,submission_id LIMIT ? OFFSET ?", (remaining + 1, offset)).fetchall()
                has_more = start + CATALOGUE_PAGE_SIZE < len(items) or len(rows) > remaining
                rows = rows[:remaining]
        for r in rows:
            s = json.loads(r["payload_json"])["skill"]
            result.append(dict(s, id="community_" + r["submission_id"].replace("-", ""),
                kind="community", author="社区投稿", license="MIT", licenseUrl="https://opensource.org/license/mit",
                licenseText="MIT License\nCopyright (c) the submitting author\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the Software), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:\nThe above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\nTHE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.",
                canInstall=True, requirements=[], evaluation="管理员审核通过，未做运行评分", repoStars=None,
                details=s["description"], contentSha256=r["content_hash"], reviewedAt=r["reviewed_at"]))
        if not include_body:
            for item in result:
                item.pop("systemPromptFragment", None)
                item.pop("licenseText", None)
        return dict(schemaVersion=1, updatedAt=raw.get("updatedAt"), items=result, hasMore=has_more, page=page)

    def detail(self, skill_id):
        if re.fullmatch(r"community_[0-9a-f]{32}", skill_id):
            sid = str(UUID(skill_id[len("community_"):]))
            items = self.catalogue(True, community_id=sid)["items"]
        else:
            _, items = self._official()
        for item in items:
            if item["id"] == skill_id:
                return dict(item)
        raise SkillLibraryError(404, "skill not found")


ADMIN_HTML = """<section><h2>用户制作的技能</h2><p>审核通过后进入技能库；先检查用途、指导正文和分享许可。</p>
<select id="newSkillStatus"><option value="pending">待审核</option><option value="accepted">已通过</option><option value="rejected">已退回</option></select>
<button onclick="loadNewSkills()">加载投稿</button><div id="newSkillList"></div><pre id="newSkillDetail"></pre>
<input id="newSkillNote" placeholder="审核说明"><button onclick="reviewNewSkill('accepted')">通过并发布</button><button onclick="reviewNewSkill('rejected')">退回/下架</button><p id="newSkillFeedback"></p></section>"""
ADMIN_SCRIPT = """let newSkillId=null;
async function newSkillRequest(path,options={}) {const r=await fetch(path,{...options,headers:{...headers(),'Content-Type':'application/json'}});const d=await r.json();if(!r.ok)throw Error(d.error||r.status);return d;}
async function loadNewSkills(){try{const d=await newSkillRequest('/v1/admin/skill-submissions?status='+document.getElementById('newSkillStatus').value);const p=document.getElementById('newSkillList');p.replaceChildren();for(const row of d.items){const b=document.createElement('button');b.textContent=row.skill.displayName+' · '+row.status;b.onclick=async()=>{try{const detail=await newSkillRequest('/v1/admin/skill-submissions/'+row.submissionId);newSkillId=row.submissionId;document.getElementById('newSkillDetail').textContent=JSON.stringify(detail,null,2);}catch(e){document.getElementById('newSkillFeedback').textContent=e.message;}};p.append(b);}}catch(e){document.getElementById('newSkillFeedback').textContent=e.message;}}
async function reviewNewSkill(decision){if(!newSkillId)return;try{const d=await newSkillRequest('/v1/admin/skill-submissions/'+newSkillId+'/review',{method:'POST',body:JSON.stringify({decision,note:document.getElementById('newSkillNote').value})});document.getElementById('newSkillDetail').textContent=JSON.stringify(d,null,2);document.getElementById('newSkillFeedback').textContent='已保存：'+d.status;await loadNewSkills();}catch(e){document.getElementById('newSkillFeedback').textContent=e.message;}}"""
