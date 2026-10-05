import copy
from dataclasses import replace
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

sys.path.insert(0, str(Path(__file__).parents[1]))
import editorial
import server

NOW = datetime(2026, 10, 3, tzinfo=timezone.utc)
CONFIG = editorial.EditorialConfig("https://model.example/v1", "test-model", "synthetic-key")
ITEM = {"id": "one", "title": "New agent memory", "summary": "The agent stores task memory and supports persistent checkpoints.",
        "links": {"original": "https://news.example/agent"}, "publishedAt": "2026-10-02T12:00:00Z",
        "source": {"name": "Official"}, "category": "ai-products", "selected": False}
RESULT = {"relevant": True, "titleZh": "智能体新增任务记忆", "summaryZh": "该智能体可保存任务记忆，并支持持久化检查点。",
          "category": "ai-products", "score": 80, "reasonZh": "可保存任务记忆，便于持续执行。", "evidenceQuotes": ["stores task memory"]}


class EditorialTests(unittest.TestCase):
    def test_dedicated_config_only_and_disabled_keeps_baseline(self):
        for env in ({"OPENAI_API_KEY": "synthetic"}, {"ZXAI_NEWS_EDITOR_ENABLED": "1"},
                    {"ZXAI_NEWS_EDITOR_ENABLED": "1", "ZXAI_NEWS_EDITOR_BASE_URL": "http://model.example", "ZXAI_NEWS_EDITOR_MODEL": "test", "ZXAI_NEWS_EDITOR_API_KEY": "synthetic"}):
            self.assertIsNone(editorial.EditorialConfig.from_env(env))
        with tempfile.TemporaryDirectory() as temp:
            calls = []
            store = editorial.EditorialStore(Path(temp) / "edits.json", None, lambda: NOW, lambda *args: calls.append(args))
            self.assertEqual(0, store.run([ITEM]))
            self.assertEqual(ITEM, store.overlay(ITEM, ITEM))
            self.assertEqual([], calls)

    def test_results_persist_without_repeat_and_changed_input_not_overlaid(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "edits.json"
            calls = []
            def model(*args):
                calls.append(args)
                return RESULT
            first = editorial.EditorialStore(path, CONFIG, lambda: NOW, model)
            self.assertEqual(1, first.run([ITEM]))
            second = editorial.EditorialStore(path, CONFIG, lambda: NOW, model)
            self.assertEqual(0, second.run([ITEM]))
            edited = second.overlay(ITEM, ITEM)
            self.assertTrue(edited["selected"])
            self.assertEqual(RESULT["titleZh"], edited["title"])
            for field in ("id", "source", "links", "publishedAt"):
                self.assertEqual(ITEM[field], edited[field])
            changed = dict(ITEM, summary=ITEM["summary"] + " New details.")
            self.assertEqual(changed, second.overlay(changed, changed))
            self.assertEqual(1, len(calls))

    def test_reserved_budget_survives_restart_and_atomic_competition(self):
        with tempfile.TemporaryDirectory() as temp:
            moment = [NOW]
            cfg = replace(CONFIG, per_hour=1, per_day=1)
            path = Path(temp) / "edits.json"
            store = editorial.EditorialStore(path, cfg, lambda: moment[0], lambda *args: RESULT)
            claims = []
            threads = [threading.Thread(target=lambda key=key: claims.append(store._claim(key))) for key in ("a", "b")]
            for thread in threads: thread.start()
            for thread in threads: thread.join()
            self.assertEqual(1, sum(claims))
            restarted = editorial.EditorialStore(path, cfg, lambda: moment[0], lambda *args: RESULT)
            self.assertFalse(restarted._claim("c"))
            self.assertTrue(all(e["state"] == "failed" for e in restarted.saved["entries"].values()))
            self.assertFalse(restarted._claim(next(iter(restarted.saved["entries"]))))
            moment[0] += timedelta(days=1)
            self.assertTrue(restarted._claim("c"))

    def test_invalid_results_and_uncertain_failures_never_publish(self):
        invalid = [dict(RESULT, score=True), dict(RESULT, titleZh="新模型新增 99 项能力"),
                   dict(RESULT, evidenceQuotes=["does not occur"]), {"relevant": "true"},
                   dict(RESULT, summaryZh="No Chinese"), dict(RESULT, category="bad"),
                   dict(RESULT, summaryZh="详情见 https://unprovided.example/news 。"),
                   dict(RESULT, titleZh="Microsoft 发布新模型")]
        for bad in invalid:
            with self.subTest(bad=bad), tempfile.TemporaryDirectory() as temp:
                store = editorial.EditorialStore(Path(temp) / "edits.json", CONFIG, lambda: NOW, lambda *args: bad)
                self.assertEqual(1, store.run([ITEM]))
                self.assertEqual(ITEM, store.overlay(ITEM, ITEM))
                self.assertEqual(0, store.run([ITEM]))
        with tempfile.TemporaryDirectory() as temp:
            def timeout(*args): raise TimeoutError()
            store = editorial.EditorialStore(Path(temp) / "edits.json", CONFIG, lambda: NOW, timeout)
            self.assertEqual(1, store.run([ITEM]))
            self.assertEqual(0, store.run([ITEM]))

    def test_402_pauses_across_restart_until_explicit_resume(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "edits.json"
            attempts = []
            def unavailable(*args):
                attempts.append(1)
                raise HTTPError(CONFIG.base_url, 402, "synthetic", {}, None)
            store = editorial.EditorialStore(path, CONFIG, lambda: NOW, unavailable)
            other = dict(ITEM, id="two", links={"original": "https://news.example/other"})
            self.assertEqual(1, store.run([ITEM, other]))
            restarted = editorial.EditorialStore(path, CONFIG, lambda: NOW, unavailable)
            self.assertEqual(0, restarted.run([ITEM, other]))
            self.assertEqual(1, len(attempts))
            resumed = editorial.EditorialStore(path, replace(CONFIG, resume="user-recharged"), lambda: NOW, lambda *args: RESULT)
            self.assertEqual(1, resumed.run([ITEM]))
            self.assertTrue(resumed.overlay(ITEM, ITEM)["selected"])
            self.assertNotIn("synthetic-key", path.read_text(encoding="utf-8"))

    def test_read_never_infers_and_edit_does_not_extend_feed_freshness(self):
        with tempfile.TemporaryDirectory() as temp:
            moment, calls = [NOW], []
            def model(*args): calls.append(1); return RESULT
            editor = editorial.EditorialStore(Path(temp) / "edits.json", CONFIG, lambda: moment[0], model)
            source = {"id": "official", "name": "Official", "url": "https://news.example/feed", "category": "ai-products"}
            store = server.FeedStore([source], Path(temp) / "feed.json", lambda: moment[0], editor)
            store.refresh(lambda s: [copy.deepcopy(ITEM)])
            before = store.batches["official"]["at"]
            self.assertEqual(0, len(calls))
            store.read(); store.read()
            self.assertEqual(0, len(calls))
            self.assertEqual(1, store.edit_recent())
            self.assertEqual(before, store.batches["official"]["at"])
            self.assertEqual(RESULT["titleZh"], store.read()["items"][0]["title"])
            moment[0] += timedelta(hours=25)
            self.assertEqual([], store.read()["items"])
            self.assertEqual(0, store.edit_recent())
            self.assertEqual(1, len(calls))

    def test_receipts_pruned_during_normal_running_and_budget_kept(self):
        with tempfile.TemporaryDirectory() as temp:
            moment = [NOW]
            store = editorial.EditorialStore(Path(temp) / "edits.json", CONFIG, lambda: moment[0], lambda *args: RESULT)
            store.run([ITEM])
            moment[0] += timedelta(days=46)
            self.assertTrue(store._claim("new-item"))
            self.assertNotIn(editorial.input_for(ITEM, CONFIG.version)[0], store.saved["entries"])
            self.assertEqual([moment[0].timestamp()], store.saved["calls"])

    def test_explicit_model_parameters_no_network_or_silent_retries(self):
        cfg = replace(CONFIG, token_field="max_completion_tokens", json_mode=False)
        self.assertNotEqual(CONFIG.version, cfg.version)
        class Response:
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def read(self, maximum):
                return json.dumps({"choices": [{"finish_reason": "stop", "message": {"content": json.dumps(RESULT)}}]}).encode()
        class Opener:
            def open(self, req, timeout):
                self.payload = json.loads(req.data)
                return Response()
        opener = Opener()
        with patch.object(editorial, "build_opener", return_value=opener):
            self.assertEqual(RESULT, editorial.request_edit(cfg, {"title": "Synthetic", "summary": "Synthetic source"}))
        self.assertEqual(800, opener.payload["max_completion_tokens"])
        self.assertNotIn("max_tokens", opener.payload)
        self.assertNotIn("response_format", opener.payload)
        self.assertNotIn("temperature", opener.payload)


if __name__ == "__main__":
    unittest.main()
