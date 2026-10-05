"""First-party benchmark intake, moderation and public rankings.

Submission ownership is a random client capability, not a verified user identity.
Only accepted reports are public; moderation does not verify a benchmark run.
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import math
import re
import struct
import zlib
from datetime import datetime, timezone
from uuid import UUID

INTAKE_PATH = "/v1/benchmarks"
MAX_BODY = 3 * 1024 * 1024
MAX_IMAGE = 2 * 1024 * 1024
REPORT_PATH = re.compile(r"^/v1/benchmarks/([0-9a-f-]{36})$")
IMAGE_PATH = re.compile(r"^/v1/benchmarks/([0-9a-f-]{36})/heatmap.png$")
ADMIN_IMAGE_PATH = re.compile(r"^/v1/admin/benchmarks/([0-9a-f-]{36})/heatmap.png$")
REVIEW_PATH = re.compile(r"^/v1/admin/benchmarks/([0-9a-f-]{36})/review$")
SCORE_VERSION = "zxai-performance/1"
HARDWARE = {"cpuName": 400, "gpuName": 400, "osName": 300, "motherboardName": 800,
            "memoryInfo": 4000, "diskInfo": 4000, "displayInfo": 2000}
SCORES = ("cpuSingleCoreScore", "cpuMultiCoreScore", "gpuRenderScore", "memoryCapacityScore",
          "diskSeqReadScore", "diskSeqWriteScore", "disk4KReadScore", "disk4KWriteScore",
          "browserTotalScore", "winFinalScore")
BOARDS = {"gaming": "gamingScore", "office": "officeScore", "cpu": "cpuMultiCoreScore",
          "gpu": "gpuRenderScore", "memory": "memoryCapacityScore", "disk": "diskSeqReadScore",
          "browser": "browserTotalScore", "win": "winFinalScore"}


class BenchmarkError(Exception):
    def __init__(self, status, message):
        self.status, self.message = status, message


def exact(raw, fields):
    if not isinstance(raw, dict) or raw.keys() != fields:
        raise BenchmarkError(400, "invalid benchmark fields")
    return raw


def text(raw, maximum, empty=False):
    if not isinstance(raw, str) or len(raw) > maximum or (not empty and not raw.strip()) \
            or any(ord(c) < 32 for c in raw):
        raise BenchmarkError(400, "invalid benchmark text")
    return raw.strip()


def canonical_id(raw):
    try:
        value = str(UUID(raw))
    except (ValueError, TypeError, AttributeError) as exc:
        raise BenchmarkError(400, "invalid report id") from exc
    if raw != value or value == str(UUID(int=0)):
        raise BenchmarkError(400, "invalid report id")
    return value


def owner_hash(token):
    if not isinstance(token, str) or re.fullmatch(r"[0-9a-f]{64}", token) is None:
        raise BenchmarkError(401, "benchmark ownership token required")
    return hashlib.sha256(token.encode("ascii")).hexdigest()


def png(raw):
    if raw is None:
        return None
    if not isinstance(raw, str) or len(raw) > (MAX_IMAGE + 2) // 3 * 4:
        raise BenchmarkError(413, "heatmap too large")
    try:
        data = base64.b64decode(raw, validate=True)
    except (ValueError, TypeError) as exc:
        raise BenchmarkError(400, "invalid heatmap encoding") from exc
    if not 57 <= len(data) <= MAX_IMAGE or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise BenchmarkError(400, "invalid PNG heatmap")
    offset, first, ended, image_data = 8, True, False, False
    while offset + 12 <= len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        end = offset + 12 + length
        if end > len(data):
            raise BenchmarkError(400, "truncated PNG")
        payload = data[offset + 8:offset + 8 + length]
        crc = struct.unpack_from(">I", data, offset + 8 + length)[0]
        if zlib.crc32(kind + payload) & 0xffffffff != crc:
            raise BenchmarkError(400, "invalid PNG checksum")
        if first:
            if kind != b"IHDR" or length != 13:
                raise BenchmarkError(400, "invalid PNG header")
            width, height = struct.unpack_from(">II", payload)
            if not 1 <= width <= 4096 or not 1 <= height <= 4096 or width * height > 16_000_000:
                raise BenchmarkError(400, "PNG dimensions too large")
            depth, color, compression, filtering, interlace = payload[8:13]
            if depth not in {0: (1, 2, 4, 8, 16), 2: (8, 16), 3: (1, 2, 4, 8), 4: (8, 16), 6: (8, 16)}.get(color, ()) \
                    or compression or filtering or interlace not in (0, 1):
                raise BenchmarkError(400, "invalid PNG format")
            first = False
        elif kind == b"IHDR":
            raise BenchmarkError(400, "duplicate PNG header")
        if kind == b"IDAT" and length:
            image_data = True
        offset = end
        if kind == b"IEND":
            if length or offset != len(data):
                raise BenchmarkError(400, "invalid PNG ending")
            ended = True
            break
    if not ended or not image_data:
        raise BenchmarkError(400, "missing PNG image data or ending")
    return data


def grade(score):
    return next(label for minimum, label in ((130, "S"), (100, "A+"), (75, "A"), (55, "B+"),
                                            (40, "B"), (20, "C"), (10, "D"), (0, "E")) if score >= minimum)


def validate(raw):
    data = exact(raw, {"schemaVersion", "reportId", "kind", "clientVersion", "scoreVersion",
                       "testTime", "durationMode", "report", "heatmapBase64"})
    if type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1:
        raise BenchmarkError(400, "schemaVersion must be 1")
    if data["kind"] not in ("benchmark", "latency") or data["scoreVersion"] != SCORE_VERSION:
        raise BenchmarkError(400, "unsupported benchmark kind or score version")
    report = exact(data["report"], set(HARDWARE) | set(SCORES))
    clean = {key: text(report[key], limit, key != "cpuName") for key, limit in HARDWARE.items()}
    for key in SCORES:
        if type(report[key]) is not int or not 0 <= report[key] <= 100_000:
            raise BenchmarkError(400, "invalid benchmark score")
        clean[key] = report[key]
    time = text(data["testTime"], 40)
    try:
        parsed = datetime.fromisoformat(time.replace("Z", "+00:00"))
        if parsed.tzinfo is None or parsed.utcoffset().total_seconds() != 0:
            raise ValueError()
    except (ValueError, AttributeError) as exc:
        raise BenchmarkError(400, "testTime requires UTC timezone") from exc
    image = png(data["heatmapBase64"])
    if data["kind"] == "latency" and (image is None or any(clean[k] for k in SCORES)):
        raise BenchmarkError(400, "latency-only submission requires image and no scores")
    if data["kind"] == "benchmark" and not any(clean[k] for k in SCORES):
        raise BenchmarkError(400, "no benchmark scores")
    return {"schemaVersion": 1, "reportId": canonical_id(data["reportId"]), "kind": data["kind"],
            "clientVersion": text(data["clientVersion"], 80), "scoreVersion": SCORE_VERSION,
            "testTime": parsed.astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
            "durationMode": text(data["durationMode"], 80, True), "report": clean,
            "heatmapBase64": None if image is None else base64.b64encode(image).decode("ascii")}


class BenchmarkStore:
    def __init__(self, connect, now):
        self.connect, self.now = connect, now
        with connect() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS benchmarks (
                    report_id TEXT PRIMARY KEY, owner_hash TEXT NOT NULL, kind TEXT NOT NULL,
                    payload_json TEXT NOT NULL, received_at TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending', review_note TEXT NOT NULL DEFAULT '',
                    reviewed_at TEXT, heatmap BLOB
                );
                CREATE INDEX IF NOT EXISTS idx_benchmark_status ON benchmarks(status, received_at);
                CREATE INDEX IF NOT EXISTS idx_benchmark_owner ON benchmarks(owner_hash, received_at);
            """)

    def submit(self, payload, token):
        owner = owner_hash(token)
        serialized = json.dumps(payload, sort_keys=True, ensure_ascii=False, separators=(",", ":"))
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            old = db.execute("SELECT * FROM benchmarks WHERE report_id=?", (payload["reportId"],)).fetchone()
            if old:
                if not hmac.compare_digest(old["owner_hash"], owner) or old["payload_json"] != serialized:
                    raise BenchmarkError(409, "report id already used")
                return {"reportId": payload["reportId"], "created": False, "status": old["status"]}
            db.execute("INSERT INTO benchmarks(report_id,owner_hash,kind,payload_json,received_at,heatmap) VALUES(?,?,?,?,?,?)",
                       (payload["reportId"], owner, payload["kind"], serialized, self.now(), png(payload["heatmapBase64"])))
        return {"reportId": payload["reportId"], "created": True, "status": "pending"}

    def entry(self, row):
        data = json.loads(row["payload_json"])
        r = data["report"].copy()
        weights_game = (.25, .10, .35, .05, .05, .03, .05, .02, .10)
        weights_office = (.20, .05, .05, .12, .10, .08, .06, .05, .29)
        values = [r[k] for k in SCORES[:-1]]
        # Match the existing C# left-associated double expression, including truncation.
        # Python 3.12+ sum() uses compensated float summation and can differ by one point.
        def weighted(weights):
            total = values[0] * weights[0]
            for value, weight in zip(values[1:], weights[1:]):
                total += value * weight
            return int(total)
        r["gamingScore"] = weighted(weights_game)
        r["officeScore"] = weighted(weights_office)
        r.update({"id": row["report_id"], "author": "枕星用户-" + row["owner_hash"][:8],
                  "submittedAt": row["received_at"], "testTime": data["testTime"],
                  "gamingGrade": grade(r["gamingScore"]), "officeGrade": grade(r["officeScore"]),
                  "winGrade": grade(r["winFinalScore"]), "status": row["status"],
                  "kind": row["kind"], "scoreVersion": data["scoreVersion"], "scoresVerified": False,
                  "detailsPath": "v1/benchmarks/" + row["report_id"],
                  "hasHeatmap": row["heatmap"] is not None})
        return r

    def _rows(self, owner=None, status="accepted"):
        with self.connect() as db:
            if owner:
                return db.execute("SELECT * FROM benchmarks WHERE owner_hash=? AND status!='withdrawn' ORDER BY received_at DESC,report_id",
                                  (owner,)).fetchall()
            return db.execute("SELECT * FROM benchmarks WHERE status=? ORDER BY received_at DESC,report_id", (status,)).fetchall()

    def mine(self, token):
        return {"reports": [self.entry(row) for row in self._rows(owner_hash(token))]}

    def reports(self, page=0):
        if not 0 <= page <= 100_000:
            raise BenchmarkError(400, "invalid report page")
        rows = [row for row in self._rows() if row["kind"] == "benchmark"]
        return {"sortBy": "all", "page": page, "pageSize": 50, "totalEntries": len(rows),
                "totalPages": math.ceil(len(rows) / 50),
                "entries": [self.entry(row) for row in rows[page * 50:(page + 1) * 50]]}

    def detail(self, report_id, token=None):
        with self.connect() as db:
            row = db.execute("SELECT * FROM benchmarks WHERE report_id=? AND status!='withdrawn'", (canonical_id(report_id),)).fetchone()
        if row is None or (row["status"] != "accepted" and
                           (not token or not hmac.compare_digest(row["owner_hash"], owner_hash(token)))):
            raise BenchmarkError(404, "report not found")
        return self.entry(row)

    def image(self, report_id, admin=False):
        with self.connect() as db:
            row = db.execute("SELECT heatmap FROM benchmarks WHERE report_id=? AND " +
                             ("status!='withdrawn'" if admin else "status='accepted'"), (canonical_id(report_id),)).fetchone()
        if row is None or row["heatmap"] is None:
            raise BenchmarkError(404, "heatmap not found")
        return bytes(row["heatmap"])

    def images(self):
        return {"images": [{"id": row["report_id"], "name": self.entry(row)["cpuName"] + " · " + self.entry(row)["author"],
                            "cpuName": self.entry(row)["cpuName"], "author": self.entry(row)["author"],
                            "sha": hashlib.sha256(row["heatmap"]).hexdigest(),
                            "path": "v1/benchmarks/" + row["report_id"] + "/heatmap.png"}
                           for row in self._rows() if row["heatmap"] is not None]}

    def leaderboard(self, sort_by="gaming", page=0, cpu="", gpu=""):
        if sort_by not in BOARDS or not 0 <= page <= 100_000 or len(cpu) > 400 or len(gpu) > 400:
            raise BenchmarkError(400, "invalid leaderboard query")
        reports = [self.entry(row) for row in self._rows() if row["kind"] == "benchmark"]
        if sort_by in ("gaming", "office"):
            # A partial test is useful in its component board, not a full-system comparison.
            reports = [r for r in reports if all(r[k] > 0 for k in SCORES[:-1])]
        reports = [r for r in reports if r[BOARDS[sort_by]] > 0 and cpu.casefold() in r["cpuName"].casefold()
                   and gpu.casefold() in r["gpuName"].casefold()]
        reports.sort(key=lambda r: (-r[BOARDS[sort_by]], r["submittedAt"], r["id"]))
        for rank, report in enumerate(reports, 1):
            report["rank"] = rank
        return {"sortBy": sort_by, "page": page, "pageSize": 50, "totalEntries": len(reports),
                "totalPages": math.ceil(len(reports) / 50), "entries": reports[page * 50:(page + 1) * 50]}

    def withdraw(self, report_id, token):
        owner = owner_hash(token)
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT owner_hash FROM benchmarks WHERE report_id=?", (canonical_id(report_id),)).fetchone()
            if not row or not hmac.compare_digest(row["owner_hash"], owner):
                raise BenchmarkError(404, "report not found")
            db.execute("UPDATE benchmarks SET status='withdrawn',heatmap=NULL WHERE report_id=?", (report_id,))
        return {"reportId": report_id, "status": "withdrawn"}

    def admin_list(self, status="pending"):
        if status not in ("pending", "accepted", "rejected", "withdrawn"):
            raise BenchmarkError(400, "invalid review status")
        return {"reports": [{**self.entry(row), "reviewNote": row["review_note"], "reviewedAt": row["reviewed_at"]}
                            for row in self._rows(status=status)]}

    def review(self, report_id, raw):
        data = exact(raw, {"decision", "note"})
        if data["decision"] not in ("accepted", "rejected"):
            raise BenchmarkError(400, "invalid review decision")
        note = text(data["note"], 2000, True)
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT * FROM benchmarks WHERE report_id=?", (canonical_id(report_id),)).fetchone()
            if row is None:
                raise BenchmarkError(404, "report not found")
            if row["status"] != "pending":
                if row["status"] == data["decision"] and row["review_note"] == note:
                    return {"reportId": report_id, "status": row["status"], "reviewedAt": row["reviewed_at"]}
                raise BenchmarkError(409, "report already reviewed or withdrawn")
            now = self.now()
            db.execute("UPDATE benchmarks SET status=?,review_note=?,reviewed_at=? WHERE report_id=? AND status='pending'",
                       (data["decision"], note, now, report_id))
        return {"reportId": report_id, "status": data["decision"], "reviewedAt": now}


ADMIN_HTML = '''<section><h2>性能测试报告</h2><p>成绩来自客户端投稿。采纳只代表允许公开，并非已独立验证跑分。</p>
<select id="bmStatus"><option value="pending">待审核</option><option value="accepted">已采纳</option><option value="rejected">已退回</option><option value="withdrawn">已撤回</option></select>
<button id="bmLoad">刷新报告</button><p id="bmMessage"></p><div id="bmList"></div></section>'''
ADMIN_SCRIPT = '''
document.getElementById('bmLoad').onclick = async () => {
 const msg=document.getElementById('bmMessage'), list=document.getElementById('bmList');
 try {
  const response=await fetch('/v1/admin/benchmarks?status='+document.getElementById('bmStatus').value,{headers:headers()});
  const data=await response.json(); if(!response.ok) throw Error(data.error||response.status);
  list.replaceChildren(); msg.textContent=data.reports.length+' 份报告';
  for(const report of data.reports) {
   const box=document.createElement('div'), title=document.createElement('h3'), detail=document.createElement('pre');
   title.textContent=report.cpuName+' / '+report.gpuName+' · '+report.author;
   detail.textContent=JSON.stringify(report,null,2);box.append(title,detail);
   if(report.hasHeatmap) {
    const preview=document.createElement('button');preview.textContent='查看热力图';
    preview.onclick=async()=>{try{
     const response=await fetch('/v1/admin/benchmarks/'+report.id+'/heatmap.png',{headers:headers()});
     if(!response.ok)throw Error('图片读取失败：'+response.status);
     const url=URL.createObjectURL(await response.blob()), image=document.createElement('img');
     image.style.maxWidth='100%';image.onload=()=>URL.revokeObjectURL(url);image.onerror=()=>URL.revokeObjectURL(url);image.src=url;
     box.append(image);preview.remove();
    }catch(error){msg.textContent=error.message;}};box.append(preview);
   }
   if(report.status==='pending') for(const [decision,label] of [['accepted','采纳并公开'],['rejected','退回']]) {
    const button=document.createElement('button');button.textContent=label;
    button.onclick=async()=>{button.disabled=true;try {
     const result=await fetch('/v1/admin/benchmarks/'+report.id+'/review',{method:'POST',headers:{...headers(),'Content-Type':'application/json'},body:JSON.stringify({decision,note:''})});
     const receipt=await result.json();if(!result.ok)throw Error(receipt.error||result.status);
     document.getElementById('bmLoad').click();
    }catch(error){msg.textContent=error.message;button.disabled=false;}};box.append(button);
   }
   list.append(box);
  }
 }catch(error){msg.textContent=error.message;}
};
'''
