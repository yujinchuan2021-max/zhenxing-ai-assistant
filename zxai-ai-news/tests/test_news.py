import importlib.util
import json
import sys
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import urlopen
from datetime import datetime, timedelta, timezone
from http.server import ThreadingHTTPServer

sys.path.insert(0, str(Path(__file__).parents[1]))
spec = importlib.util.spec_from_file_location("news_server", Path(__file__).parents[1] / "server.py")
news = importlib.util.module_from_spec(spec)
spec.loader.exec_module(news)
NOW = datetime(2026, 10, 3, tzinfo=timezone.utc)
SOURCE = {"id": "official", "name": "Official", "url": "https://example.org/feed", "category": "ai-products"}


def rss(items):
    return ("<rss><channel>" + "".join("<item><title>" + title + "</title><link>https://example.org/" + str(index) + "</link><pubDate>" + when + "</pubDate><description>&lt;p&gt;Summary&lt;/p&gt;&lt;script&gt;bad&lt;/script&gt;</description></item>" for index, title, when in items) + "</channel></rss>").encode()


class NewsTests(unittest.TestCase):
    def test_dates_order_null_and_dedup(self):
        rows = news.parse_feed(rss([(1, "older", "Thu, 01 Oct 2026 00:00:00 GMT"), (2, "unknown", "wrong"), (3, "newer", "Fri, 02 Oct 2026 00:00:00 GMT"), (1, "latest", "Sat, 03 Oct 2026 00:00:00 GMT")]), SOURCE, NOW)
        self.assertEqual(["latest", "newer", "unknown"], [r["title"] for r in rows])
        self.assertIsNone(rows[-1]["publishedAt"])
        self.assertTrue(all(r["summary"] == "Summary" and not r["selected"] for r in rows))

    def test_atom_relative_and_published(self):
        data = b'<feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://example.org/news/"><entry><title>A &amp; B</title><link rel="self" href="internal"/><link rel="alternate" href="story"/><published>2026-10-01T12:00:00Z</published><updated>2026-10-02T12:00:00Z</updated><content>FULL ARTICLE</content></entry></feed>'
        row = news.parse_feed(data, SOURCE, NOW)[0]
        self.assertEqual("https://example.org/news/story", row["links"]["original"])
        self.assertEqual("2026-10-01T12:00:00.000000Z", row["publishedAt"])
        self.assertEqual("", row["summary"])

    def test_rss_article_summary_ignores_media_caption_and_dc_date_fallback(self):
        data = b'''<rss xmlns:media="http://search.yahoo.com/mrss/" xmlns:dc="http://purl.org/dc/elements/1.1/"><channel><item>
        <media:title>Picture title</media:title><title>September AI updates</title>
        <link>https://example.org/news</link><dc:date>2026-10-02</dc:date>
        <media:description>A video showing the September AI updates</media:description>
        <description>Here are the latest AI updates from September.</description></item></channel></rss>'''
        row = news.parse_feed(data, SOURCE, NOW)[0]
        self.assertEqual("September AI updates", row["title"])
        self.assertEqual("Here are the latest AI updates from September.", row["summary"])
        self.assertEqual("2026-10-02T00:00:00.000000Z", row["publishedAt"])
        media_only = data.replace(b'<description>Here are the latest AI updates from September.</description>', b'')
        self.assertEqual("", news.parse_feed(media_only, SOURCE, NOW)[0]["summary"])

    def test_excerpt_marks_truncation_at_sentence_or_word_boundary(self):
        original = "A complete useful sentence about agent memory. " + "More important source detail. " * 20
        shortened = news.excerpt(original, 70)
        self.assertEqual("A complete useful sentence about agent memory.…", shortened)
        original = "These updates support developers building with persistent agent memory and checkpoints for complex workflows"
        shortened = news.excerpt(original, 75)
        self.assertTrue(shortened.endswith("…"))
        self.assertLessEqual(len(shortened), 75)
        self.assertTrue(original.startswith(shortened[:-1]))
        self.assertNotRegex(shortened, r" memor…| checkpoint…")
        self.assertEqual("原始短摘要。", news.excerpt("原始短摘要。", 180))

    def test_unsafe_xml_and_oversize(self):
        for data in (b'<!DOCTYPE rss [<!ENTITY x "bad">]><rss/>', b'<rss>', b'<html/>', b'x' * (news.MAX_FEED + 1)):
            with self.assertRaises((ValueError, news.ET.ParseError)):
                news.parse_feed(data, SOURCE, NOW)

    def test_old_future_and_non_https(self):
        self.assertEqual([], news.parse_feed(rss([(1, "old", "2025-01-01"), (2, "future", "2027-10-03")]), SOURCE, NOW))
        self.assertEqual("", news.safe_link("http://example.org/"))
        self.assertEqual("", news.safe_link("https://user:pass@example.org/"))

    def test_fractional_second_duplicate_uses_newer_item(self):
        rows = news.parse_feed(rss([(1, "newer", "2026-10-02T08:00:00.500000Z"), (1, "older", "2026-10-02T08:00:00Z")]), SOURCE, NOW)
        self.assertEqual("newer", rows[0]["title"])

    def test_independent_failure_never_refreshes_old_timestamp(self):
        with tempfile.TemporaryDirectory() as temp:
            now = [NOW]
            second = dict(SOURCE, id="second")
            store = news.FeedStore([SOURCE, second], Path(temp) / "cache.json", lambda: now[0])
            loader = lambda s: news.parse_feed(rss([(1, s["id"], "2026-10-02")]).replace(b'https://example.org/', ('https://example.org/' + s["id"] + '/').encode()), s, NOW)
            self.assertEqual(2, store.refresh(loader))
            original_at = store.batches["second"]["at"]
            now[0] += timedelta(hours=1)
            def partial(source):
                if source["id"] == "second":
                    raise HTTPError(source["url"], 503, "unavailable", {}, None)
                return loader(source)
            self.assertEqual(1, store.refresh(partial))
            self.assertEqual(2, len(store.read()["items"]))
            self.assertEqual(original_at, store.batches["second"]["at"])
            now[0] += timedelta(hours=24, minutes=1)
            self.assertEqual([], store.read()["items"])

    def test_empty_feed_does_not_clear_or_renew_previous_batch(self):
        with tempfile.TemporaryDirectory() as temp:
            now = [NOW]
            store = news.FeedStore([SOURCE], Path(temp) / "cache.json", lambda: now[0])
            store.refresh(lambda s: news.parse_feed(rss([(1, "news", "2026-10-02")]), s, NOW))
            old_at = store.batches["official"]["at"]
            now[0] += timedelta(hours=1)
            self.assertEqual(0, store.refresh(lambda s: news.parse_feed(b'<rss><channel/></rss>', s, now[0])))
            self.assertEqual(old_at, store.batches["official"]["at"])
            self.assertEqual(1, len(store.read()["items"]))

    def test_paging_search_category_and_stale_cursor(self):
        rows = news.parse_feed(rss([(i, "release " + str(i), "2026-10-02") for i in range(6)]), SOURCE, NOW)
        snap = {"revision": "r1", "items": rows, "updatedAt": None, "sources": []}
        first = news.list_items(snap, {"limit": "2", "q": "release", "category": "ai-products"})
        second = news.list_items(snap, {"limit": "2", "q": "release", "category": "ai-products", "cursor": first["page"]["nextCursor"]})
        self.assertFalse(set(r["id"] for r in first["items"]) & set(r["id"] for r in second["items"]))
        with self.assertRaises(news.QueryError) as error:
            news.list_items(dict(snap, revision="r2"), {"limit": "2", "q": "release", "category": "ai-products", "cursor": first["page"]["nextCursor"]})
        self.assertEqual(409, error.exception.status)
        for bad in ({"limit": "500"}, {"category": "bad"}, {"q": "a" * 121}, {"mode": "selected"}, {"cursor": "not-json"}):
            with self.assertRaises(news.QueryError):
                news.list_items(snap, bad)

    def test_unchanged_refresh_preserves_discovery_and_page_cursor(self):
        with tempfile.TemporaryDirectory() as temp:
            moment = [NOW]
            store = news.FeedStore([SOURCE], Path(temp) / "cache.json", lambda: moment[0])
            loader = lambda s: news.parse_feed(rss([(i, "Update " + str(i), "2026-10-02") for i in range(3)]), s, moment[0])
            store.refresh(loader)
            original = store.read()
            page = news.list_items(original, {"limit": "2"})
            moment[0] += timedelta(hours=1)
            store.refresh(loader)
            refreshed = store.read()
            self.assertEqual(original["revision"], refreshed["revision"])
            self.assertEqual(original["items"][0]["discoveredAt"], refreshed["items"][0]["discoveredAt"])
            self.assertNotEqual(original["updatedAt"], refreshed["updatedAt"])
            self.assertEqual(1, len(news.list_items(refreshed, {"limit": "2", "cursor": page["page"]["nextCursor"]})["items"]))

    def test_http_contract_and_no_model_config(self):
        with tempfile.TemporaryDirectory() as temp:
            store = news.FeedStore([SOURCE], Path(temp) / "cache.json", lambda: NOW)
            server = ThreadingHTTPServer(("127.0.0.1", 0), news.handler_for(store))
            threading.Thread(target=server.serve_forever, daemon=True).start()
            base = "http://127.0.0.1:" + str(server.server_port)
            try:
                with self.assertRaises(HTTPError) as error:
                    urlopen(base + "/v1/items")
                self.assertEqual(503, error.exception.code)
                store.refresh(lambda s: news.parse_feed(rss([(1, "news", "2026-10-02")]), s, NOW))
                with urlopen(base + "/v1/items?limit=20") as response:
                    body = json.load(response)
                    self.assertEqual(1, body["schemaVersion"])
                    self.assertFalse(body["items"][0]["selected"])
                    self.assertIsNone(body["page"]["nextCursor"])
                for path in ("/v1/items?q=a&q=b", "/v1/items?mode=selected", "/v1/items?cursor=bad"):
                    with self.assertRaises(HTTPError):
                        urlopen(base + path)
            finally:
                server.shutdown(); server.server_close()

    def test_portal_loads_without_feeds_or_model_configuration(self):
        with tempfile.TemporaryDirectory() as temp:
            store = news.FeedStore([SOURCE], Path(temp) / "cache.json", lambda: NOW)
            server = ThreadingHTTPServer(("127.0.0.1", 0), news.handler_for(store))
            threading.Thread(target=server.serve_forever, daemon=True).start()
            base = "http://127.0.0.1:" + str(server.server_port)
            try:
                for path, content_type, token in (("/portal/", "text/html", "枕星AI资讯"),
                    ("/portal/portal.css", "text/css", ".news-grid"), ("/portal/portal.js", "text/javascript", "/api/ai-news/v1/items")):
                    with urlopen(base + path) as response:
                        self.assertIn(content_type, response.headers["Content-Type"])
                        self.assertIn(token, response.read().decode("utf-8"))
                for path in ("/portal/server.py", "/portal/../server.py", "/portal/%2e%2e/server.py"):
                    with self.assertRaises(HTTPError) as error:
                        urlopen(base + path)
                    self.assertEqual(404, error.exception.code)
            finally:
                server.shutdown(); server.server_close()

    def test_publication_filters_saved_release_noise_and_caps_product_updates(self):
        release = dict(SOURCE, id="codex", name="Codex", kind="release")
        model = dict(SOURCE, id="model")
        rows = news.parse_feed(rss([(1, "0.162.0-alpha.8", "2026-10-02"),
            (2, "0.162.0", "2026-10-02"), (3, "0.160.0", "2026-10-01"),
            (4, "0.161.0", "2026-10-02")]), release, NOW)
        for row in rows:
            row["summary"] = "" if row["title"] == "0.162.0" else "A substantial source-provided description of the release changes."
        article = news.parse_feed(rss([(1, "Introducing GPT-6", "2026-10-02")]), model, NOW)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "cache.json"
            path.write_text(json.dumps({"batches": {
                "codex": {"at": news.stamp(NOW), "items": rows},
                "model": {"at": news.stamp(NOW), "items": article},
                "godot": {"at": news.stamp(NOW), "items": rows}}}))
            store = news.FeedStore([release, model], path, lambda: NOW)
            published = store.read()["items"]
            self.assertEqual(2, len(published))
            self.assertEqual(["Codex 版本更新：0.161.0"], [r["title"] for r in published if r["source"]["name"] == "Codex"])
            self.assertTrue(any(r["title"] == "Introducing GPT-6" for r in published))
            self.assertNotIn("godot", store.batches)
            self.assertEqual(0, store.refresh(lambda _: (_ for _ in ()).throw(OSError("unavailable"))))
            self.assertEqual(published, store.read()["items"])
            self.assertTrue(any(r["title"] == "0.162.0-alpha.8" for r in store.batches["codex"]["items"]))

    def test_article_categories_and_missing_summary_are_not_invented(self):
        source = dict(SOURCE, category="industry", classify=True)
        items = news.parse_feed(rss([(1, "Chatham scales with GPT-6", "2026-10-02"),
            (2, "Introducing GPT-6", "2026-10-02"), (3, "A model guide for GPT-6", "2026-10-02")]), source, NOW)
        actual = {row["title"]: news.publication_row(row, source)["category"] for row in items}
        self.assertEqual({"Chatham scales with GPT-6": "industry", "Introducing GPT-6": "ai-models", "A model guide for GPT-6": "tip"}, actual)
        blank = dict(items[0], summary="")
        self.assertEqual("", news.publication_row(blank, source)["summary"])
        research = dict(SOURCE, category="paper", requireAiTopic=True)
        self.assertIsNone(news.publication_row(dict(blank, title="New materials in battery chemistry"), research))
        self.assertIsNotNone(news.publication_row(dict(blank, title="Machine learning for power grids"), research))
        mixed = dict(SOURCE, requireAiTopic=True, requireAiHeadline=True)
        self.assertIsNone(news.publication_row(dict(blank, title="电视硬件涨价", summary="Includes AI upscaling."), mixed))
        self.assertIsNone(news.publication_row(dict(blank, title="IT早报：手机涨价、OpenAI融资"), mixed))
        self.assertIsNotNone(news.publication_row(dict(blank, title="DeepSeek发布智能体新功能"), mixed))
        self.assertIsNotNone(news.publication_row(dict(blank, title="OpenAI发布新工具"), mixed))

    def test_successful_source_with_no_publishable_rows_returns_empty_200(self):
        with tempfile.TemporaryDirectory() as temp:
            source = dict(SOURCE, kind="release")
            store = news.FeedStore([source], Path(temp) / "cache.json", lambda: NOW)
            store.refresh(lambda s: news.parse_feed(rss([(1, "1.0.0-alpha.1", "2026-10-02")]), s, NOW))
            self.assertEqual([], store.read()["items"])
            server = ThreadingHTTPServer(("127.0.0.1", 0), news.handler_for(store))
            threading.Thread(target=server.serve_forever, daemon=True).start()
            try:
                with urlopen("http://127.0.0.1:" + str(server.server_port) + "/v1/items") as response:
                    self.assertEqual(200, response.status)
                    self.assertEqual([], json.load(response)["items"])
            finally:
                server.shutdown(); server.server_close()

    def test_timeline_keeps_real_chronology_across_languages(self):
        zh = dict(SOURCE, id="zh", language="zh")
        en = dict(SOURCE, id="en")
        with tempfile.TemporaryDirectory() as temp:
            store = news.FeedStore([zh, en], Path(temp) / "cache.json", lambda: NOW)
            def loader(s):
                items = [(1, "中文模型发布", "2026-10-01"), (2, "旧中文文章", "2026-09-20")] if s["id"] == "zh" else [(1, "English model release", "2026-10-02")]
                return news.parse_feed(rss(items).replace(b'https://example.org/', ('https://example.org/' + s["id"] + '/').encode()), s, NOW)
            store.refresh(loader)
            self.assertEqual(["English model release", "中文模型发布", "旧中文文章"], [r["title"] for r in store.read()["items"]])

    def test_tracking_duplicates_merge_but_distinct_versions_do_not(self):
        with tempfile.TemporaryDirectory() as temp:
            second = dict(SOURCE, id="second", name="Second")
            store = news.FeedStore([SOURCE, second], Path(temp) / "cache.json", lambda: NOW)
            def loader(s):
                rows = news.parse_feed(rss([(1, "Model release 1", "2026-10-02"), (2, "Model release 2", "2026-10-02")]), s, NOW)
                for item in rows:
                    if s["id"] == "second":
                        item["links"]["original"] += "?utm_source=second"
                return rows
            store.refresh(loader)
            rows = store.read()["items"]
            self.assertEqual(2, len(rows))
            self.assertTrue(all(set(row["reportedBy"]) == {"Official", "Second"} for row in rows))


if __name__ == "__main__":
    unittest.main()
