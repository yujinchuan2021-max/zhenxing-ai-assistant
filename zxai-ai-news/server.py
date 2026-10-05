"""Independent news feeds with optional, persistent server-owned Chinese editorial."""
from __future__ import annotations

import argparse
import base64
import concurrent.futures
from datetime import datetime, timedelta, timezone
from email.utils import parsedate_to_datetime
import hashlib
from html.parser import HTMLParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import logging
from pathlib import Path
import re
import threading
import time
from urllib.parse import parse_qs, urljoin, urlsplit, urlunsplit
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET
from editorial import EditorialConfig, EditorialStore, canonical_link
from daily_schedule import DailySchedule

UTC = timezone.utc
MAX_FEED = 2 * 1024 * 1024
MAX_PER_SOURCE = 80
CATEGORIES = {"ai-models", "ai-products", "industry", "paper", "tip"}
AI_TOPIC = re.compile(r"(?<![a-z])(?:AI|LLMs?|GPT(?:[- ]?\d+)?|ChatGPT|Gemini|DeepSeek|Qwen|Claude|Copilot|OpenAI|Anthropic)(?![a-z])|artificial intelligence|machine learning|deep learning|neural|language model|diffusion|reinforcement learning|人工智能|大模型|智能体|机器学习|神经网络|生成式|语言模型", re.I)


def utcnow():
    return datetime.now(UTC)


def stamp(value):
    return value.astimezone(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")


def date(value):
    if not value:
        return None
    try:
        result = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    except (ValueError, TypeError):
        try:
            result = parsedate_to_datetime(value)
        except (ValueError, TypeError, OverflowError):
            return None
    if result.tzinfo is None:
        result = result.replace(tzinfo=UTC)
    return result.astimezone(UTC)


class PlainText(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts = []
        self.skip = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style"):
            self.skip += 1
        if tag in ("p", "div", "br", "li"):
            self.parts.append(" ")

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self.skip = max(0, self.skip - 1)
        if tag in ("p", "div", "li"):
            self.parts.append(" ")

    def handle_data(self, value):
        if not self.skip:
            self.parts.append(value)


def plain(value, maximum=1200):
    parser = PlainText()
    parser.feed(value[:100_000])
    return re.sub(r"\s+", " ", "".join(parser.parts)).strip()[:maximum]


def excerpt(value, maximum):
    text = plain(value, 1200)
    if len(text) <= maximum:
        return text
    clipped = text[:maximum - 1]
    sentences = list(re.finditer(r"[。！？]|[.!?](?=\s|$)", clipped))
    if sentences and sentences[-1].end() >= maximum * .5:
        return clipped[:sentences[-1].end()] + "…"
    # Do not leave English words/product names split without a truncation marker.
    boundary = clipped.rfind(" ")
    if boundary >= maximum * .65:
        clipped = clipped[:boundary]
    elif re.search(r"[A-Za-z0-9]$", clipped) and re.match(r"[A-Za-z0-9]", text[maximum - 1:]):
        clipped = re.sub(r"[A-Za-z0-9._+-]+$", "", clipped)
    return clipped.rstrip() + "…"


def safe_link(raw, base=""):
    try:
        value = urljoin(base, raw.strip())
        uri = urlsplit(value)
        if uri.scheme != "https" or not uri.hostname or uri.username or uri.password:
            return ""
        return urlunsplit((uri.scheme, uri.netloc, uri.path, uri.query, ""))[:2048]
    except (ValueError, AttributeError):
        return ""


def local(tag):
    return tag.rsplit("}", 1)[-1]


def child_text(node, names):
    # Media extensions also contain title/description, but describe an image/video.
    # Prefer syndication fields, retaining Dublin Core dates for RSS 1.0 feeds.
    namespaces = ("", "{http://www.w3.org/2005/Atom}", "{http://purl.org/rss/1.0/}")
    for name in names:
        for child in node:
            if child.tag in tuple(namespace + name for namespace in namespaces):
                return "".join(child.itertext()).strip()
    for child in node:
        if child.tag == "{http://purl.org/dc/elements/1.1/}date" and "date" in names:
            return "".join(child.itertext()).strip()
    return ""


def parse_feed(data, source, discovered=None):
    if len(data) > MAX_FEED or re.search(br"<!\s*(?:DOCTYPE|ENTITY)\b", data, re.I):
        raise ValueError("Unsafe or oversized feed")
    root = ET.fromstring(data)
    if local(root.tag) not in ("rss", "feed", "RDF"):
        raise ValueError("Not a syndication feed")
    discovered = discovered or utcnow()
    xmlbase = "{http://www.w3.org/XML/1998/namespace}base"
    base = urljoin(source["url"], root.attrib.get(xmlbase, ""))
    rows = [node for node in root.iter() if local(node.tag) in ("item", "entry")]
    found = {}
    for node in rows[:3000]:
        item_base = urljoin(base, node.attrib.get(xmlbase, ""))
        if local(node.tag) == "entry":
            candidates = [c for c in node if local(c.tag) == "link" and c.attrib.get("rel", "alternate") == "alternate"]
            raw_link = candidates[0].attrib.get("href", "") if candidates else ""
            when = child_text(node, ("published",)) or child_text(node, ("updated",))
            # Atom content is often the complete article; only supplied summary is reused.
            summary = child_text(node, ("summary",))
        else:
            raw_link = child_text(node, ("link",))
            when = child_text(node, ("pubDate", "date"))
            summary = child_text(node, ("description",))
        link = safe_link(raw_link, item_base)
        title = plain(child_text(node, ("title",)), 400)
        if not link or not title:
            continue
        published = date(when)
        if published and (published < discovered - timedelta(days=120) or published > discovered + timedelta(days=1)):
            continue
        identity = hashlib.sha256((source["id"] + "\n" + link).encode()).hexdigest()[:32]
        row = {"id": identity, "title": title, "summary": plain(summary), "source": {"name": source["name"]},
               "links": {"original": link}, "publishedAt": stamp(published) if published else None,
               "discoveredAt": stamp(discovered), "category": source["category"], "selected": False,
               "attribution": {"name": source["name"], "url": source["url"]}}
        if identity not in found or (row["publishedAt"] or "") > (found[identity]["publishedAt"] or ""):
            found[identity] = row
    return sorted(found.values(), key=lambda item: (item["publishedAt"] or "", item["id"]), reverse=True)[:MAX_PER_SOURCE]


def fetch(source):
    req = Request(source["url"], headers={"User-Agent": "ZhenxingAI-News/0.1 (official feed reader)", "Accept": "application/atom+xml, application/rss+xml, application/xml", "Accept-Encoding": "identity"})
    with urlopen(req, timeout=12) as response:
        if not safe_link(response.url):
            raise ValueError("Unexpected feed redirect")
        if response.headers.get("Content-Encoding", "identity").lower() != "identity":
            raise ValueError("Unsupported feed encoding")
        data = response.read(MAX_FEED + 1)
    return parse_feed(data, source)


def publication_row(item, source):
    """Apply publication rules to fresh feeds and saved batches alike."""
    row = dict(item)
    title = row["title"]
    row["language"] = source.get("language", "en")
    if source.get("requireAiTopic") and not AI_TOPIC.search(title + " " + row.get("summary", "")):
        return None
    if source.get("requireAiHeadline") and (not AI_TOPIC.search(title) or re.search(r"早报|晚报|科技日报|科技周报|每日科技|科技汇总", title)):
        return None
    if source.get("kind") == "release":
        # Frequent alpha/nightly builds are development activity, not news.
        tag = urlsplit(row["links"]["original"]).path.rsplit("/", 1)[-1]
        if re.search(r"(?:^|[.\-_/ ])(?:alpha|beta|rc|preview|nightly|canary|dev|pre)(?:[.\-_/ \d]|$)",
                     title + " " + tag, re.I):
            return None
        if len(plain(row.get("summary", ""))) < 40:
            return None  # A bare version number with no change description is not an article.
        version = re.fullmatch(r"(?:rust-)?v?(\d+\.\d+(?:\.\d+)?)", title.strip())
        if version:
            row["title"] = source["name"] + " 版本更新：" + version[1]
        row["category"] = "ai-products"
    elif source.get("classify"):
        lower = title.casefold()
        # Business case studies remain industry news even when they mention a model.
        if re.search(r"\b(scales|reimagining|financial|partner(?:s|ship)?|businesses|retail)\b|企业|合作|融资", lower):
            row["category"] = "industry"
        elif re.search(r"\b(guide|tutorial|tips)\b|how to\b|教程|指南|技巧", lower):
            row["category"] = "tip"
        elif source["category"] == "paper" or re.search(r"论文|研究成果|团队新作", title):
            row["category"] = "paper"
        elif re.search(r"^(?:gpt|gemini|deepseek|qwen)[- ]?\d", lower) or (re.search(r"introducing|launch|releas|open.sourc|发布|推出|开源", lower) and re.search(
                r"\b(gpt|gemini|deepseek|qwen|model)\b|gpt[- ]?\d|模型", lower)):
            row["category"] = "ai-models"
        else:
            row["category"] = source["category"]
    # Only short source-provided excerpts are published; no invented summaries.
    row["summary"] = excerpt(row.get("summary", ""), source.get("summaryLimit", 240))
    return row


class FeedStore:
    def __init__(self, sources, path, clock=utcnow, editor=None, cache_max_age_hours=24):
        self.sources, self.path, self.clock = sources, Path(path), clock
        self.editor = editor
        self.cache_max_age = timedelta(hours=cache_max_age_hours)
        self.lock = threading.Lock()
        self.batches = {}
        self.last_attempt = None
        self.snapshot = {"revision": "", "items": [], "updatedAt": None, "sources": []}
        self._load()

    def _load(self):
        try:
            if self.path.stat().st_size > 4 * 1024 * 1024:
                return
            saved = json.loads(self.path.read_text())
            allowed = {s["id"] for s in self.sources}
            self.batches = {k: v for k, v in saved["batches"].items() if k in allowed}
            self._publish()
        except (OSError, ValueError, KeyError, TypeError):
            self.batches = {}

    def _publish(self):
        now = self.clock()
        rows, statuses = [], []
        for source in self.sources:
            batch = self.batches.get(source["id"])
            at = date(batch.get("at")) if batch else None
            # Never prolong a failed source's freshness. Old data expires independently.
            usable = at is not None and timedelta(0) <= now - at <= self.cache_max_age
            if usable:
                candidates = []
                for item in batch["items"]:
                    row = publication_row(item, source)
                    if row is not None:
                        candidates.append(self.editor.overlay(row, item) if self.editor else row)
                candidates.sort(key=lambda i: (i["publishedAt"] or "", i["id"]), reverse=True)
                # Show one current release per product, rather than a version history.
                rows.extend(candidates[:1] if source.get("kind") == "release" else candidates)
            statuses.append({"id": source["id"], "name": source["name"], "updatedAt": stamp(at) if at else None, "available": usable})
        rows.sort(key=lambda item: (item["publishedAt"] or "", item["id"]), reverse=True)
        # Merge only the same original URL after removing explicit tracking fields.
        # Similar headlines/versions are not sufficient evidence of the same event.
        unique = {}
        for row in rows:
            identity = canonical_link(row["links"]["original"])
            if identity not in unique:
                unique[identity] = dict(row, reportedBy=[row["source"]["name"]])
            elif row["source"]["name"] not in unique[identity]["reportedBy"]:
                unique[identity]["reportedBy"].append(row["source"]["name"])
        rows = list(unique.values())
        payload = json.dumps(rows, ensure_ascii=False, sort_keys=True).encode()
        revision = hashlib.sha256(payload).hexdigest()[:20]
        ats = [s["updatedAt"] for s in statuses if s["available"]]
        self.snapshot = {"revision": revision, "items": rows, "updatedAt": max(ats) if ats else None, "sources": statuses}

    def refresh(self, loader=fetch):
        updates = {}
        # Fixed sources only; client requests cannot add arbitrary destinations.
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
            tasks = {pool.submit(loader, s): s for s in self.sources}
            for future in concurrent.futures.as_completed(tasks):
                source = tasks[future]
                try:
                    items = future.result()
                    if not items:
                        raise ValueError("Feed has no usable updates")
                    # Publish completed sources immediately; one slow destination
                    # must not hold back all new headlines during a refresh.
                    with self.lock:
                        previous = {item["id"]: item for item in self.batches.get(source["id"], {}).get("items", [])}
                        items = [dict(item, discoveredAt=previous[item["id"]]["discoveredAt"])
                                 if item["id"] in previous and previous[item["id"]].get("discoveredAt") else item
                                 for item in items]
                        updates[source["id"]] = {"at": stamp(self.clock()), "items": items}
                        self.batches[source["id"]] = updates[source["id"]]
                        self._publish()
                except Exception as exc:
                    logging.warning("feed %s: %s", source["id"], type(exc).__name__)
        with self.lock:
            self.last_attempt = stamp(self.clock())
            self.batches.update(updates)
            self._publish()
            self.path.parent.mkdir(parents=True, exist_ok=True)
            temp = self.path.with_suffix(".tmp")
            temp.write_text(json.dumps({"batches": self.batches}, ensure_ascii=False), encoding="utf-8")
            temp.replace(self.path)
        return len(updates)

    def read(self):
        with self.lock:
            self._publish()
            return self.snapshot

    def edit_recent(self, stop=None):
        if self.editor is None:
            return 0
        with self.lock:
            snapshot = self.read_unlocked()
            published = {row["id"] for row in snapshot["items"]}
            candidates = [dict(item) for batch in self.batches.values() for item in batch["items"] if item["id"] in published]
        candidates.sort(key=lambda item: (item["publishedAt"] or "", item["id"]), reverse=True)
        # Network inference runs outside the feed lock; requests keep serving prior results.
        count = self.editor.run(candidates, stop)
        with self.lock:
            self._publish()
        return count

    def read_unlocked(self):
        self._publish()
        return self.snapshot


class QueryError(Exception):
    def __init__(self, status=400):
        self.status = status


def list_items(snapshot, params):
    category, search, cursor = params.get("category", ""), params.get("q", "").strip(), params.get("cursor", "")
    if category and category not in CATEGORIES or len(search) > 120 or len(cursor) > 2048 or params.get("mode", "all") != "all":
        raise QueryError()
    try:
        limit = int(params.get("limit", "20"))
    except ValueError:
        raise QueryError() from None
    if not 1 <= limit <= 50:
        raise QueryError()
    query_hash = hashlib.sha256(json.dumps([category, search.casefold(), limit]).encode()).hexdigest()[:16]
    offset = 0
    if cursor:
        try:
            saved = json.loads(base64.urlsafe_b64decode(cursor + "=" * (-len(cursor) % 4)))
            if saved[0] != snapshot["revision"]:
                raise QueryError(409)
            if saved[1] != query_hash or type(saved[2]) is not int or saved[2] < 0:
                raise QueryError()
            offset = saved[2]
        except (ValueError, KeyError, TypeError, IndexError):
            raise QueryError() from None
    rows = [i for i in snapshot["items"] if (not category or i["category"] == category)
            and (not search or search.casefold() in (i["title"] + " " + i["summary"] + " " + i["source"]["name"]).casefold())]
    if offset > len(rows):
        raise QueryError()
    items = rows[offset:offset + limit]
    more = offset + limit < len(rows)
    next_cursor = base64.urlsafe_b64encode(json.dumps([snapshot["revision"], query_hash, offset + limit]).encode()).decode().rstrip("=") if more else None
    return {"schemaVersion": 1, "query": {"mode": "all", "category": category or None, "q": search or None},
            "items": items, "page": {"count": len(items), "hasMore": more, "nextCursor": next_cursor},
            "updatedAt": snapshot["updatedAt"], "sources": snapshot["sources"]}


def handler_for(store):
    portal = Path(__file__).with_name("portal")
    static_files = {"/portal/": ("index.html", "text/html"), "/portal/portal.css": ("portal.css", "text/css"),
                    "/portal/portal.js": ("portal.js", "text/javascript")}
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            uri = urlsplit(self.path)
            if uri.path in static_files:
                name, content_type = static_files[uri.path]
                try:
                    data = (portal / name).read_bytes()
                except OSError:
                    return self.send_json(503, {"error": "portal_unavailable"})
                self.send_response(200)
                self.send_header("Content-Type", content_type + "; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-cache")
                self.send_header("X-Content-Type-Options", "nosniff")
                self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'self'")
                self.end_headers()
                self.wfile.write(data)
                return
            if uri.path == "/health":
                snapshot = store.read()
                return self.send_json(200, {"ok": True, "items": len(snapshot["items"]), "updatedAt": snapshot["updatedAt"],
                    "serverEditorial": {"enabled": store.editor is not None,
                        "publishedCount": sum(row.get("editorial", {}).get("status") == "ready" for row in snapshot["items"])}})
            if uri.path != "/v1/items":
                return self.send_json(404, {"error": "not_found"})
            try:
                values = parse_qs(uri.query, max_num_fields=12, keep_blank_values=True)
                if any(len(v) != 1 for v in values.values()):
                    raise QueryError()
                snapshot = store.read()
                if not any(source["available"] for source in snapshot["sources"]):
                    return self.send_json(503, {"error": "feeds_unavailable"})
                self.send_json(200, list_items(snapshot, {k: v[0] for k, v in values.items()}))
            except (ValueError, QueryError) as exc:
                self.send_json(getattr(exc, "status", 400), {"error": "refresh_required" if getattr(exc, "status", 400) == 409 else "invalid_query"})

        def do_POST(self):
            self.send_json(405, {"error": "method_not_allowed"})

        def send_json(self, status, value):
            data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "public, max-age=120" if status == 200 else "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args):
            pass  # URLs/search terms are not persisted.
    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sources", default=str(Path(__file__).with_name("sources.json")))
    parser.add_argument("--data", default="/var/lib/zxai-ai-news/feed-cache.json")
    parser.add_argument("--port", type=int, default=8769)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--daily-at", default="09:00", help="Daily update time in Asia/Shanghai (HH:MM)")
    args = parser.parse_args()
    sources = json.loads(Path(args.sources).read_text(encoding="utf-8"))
    config = EditorialConfig.from_env()
    editor = None
    if config:
        try:
            editor = EditorialStore(Path(args.data).with_name("editorial-cache.json"), config)
        except Exception as exc:
            logging.error("server editorial disabled: %s", type(exc).__name__)
    store = FeedStore(sources, args.data, editor=editor, cache_max_age_hours=72)
    if args.once:
        updated = store.refresh()
        store.edit_recent(threading.Event())
        print(json.dumps({"updatedSources": updated, "items": len(store.read()["items"])}))
        return
    stop = threading.Event()
    # One writer only. Do not run --once against the cache of an active service.
    try:
        schedule = DailySchedule(Path(args.data).with_name("daily-schedule.json"), args.daily_at)
    except (OSError, ValueError, TypeError):
        logging.error("daily schedule disabled: state/configuration unavailable; serving existing cache")
        schedule = None
    def worker():
        while not stop.is_set() and schedule is not None:
            try:
                claimed = schedule.claim(utcnow())
            except (OSError, ValueError):
                logging.error("daily schedule disabled: marker could not be saved; serving existing cache")
                return
            try:
                if claimed:
                    logging.info("daily news update started (Asia/Shanghai %s)", args.daily_at)
                    store.refresh()
                    store.edit_recent(stop)
            except Exception as exc:
                logging.error("refresh: %s", type(exc).__name__)
            stop.wait(schedule.wait_seconds(utcnow()))
    threading.Thread(target=worker, daemon=True).start()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), handler_for(store))
    server.daemon_threads = True
    try:
        server.serve_forever()
    finally:
        stop.set()
        server.server_close()


if __name__ == "__main__":
    main()
