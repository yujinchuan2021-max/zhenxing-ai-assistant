"""Catalogue paging uses synthetic files and a temporary database only."""
import json
import sqlite3
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path
from uuid import UUID

from skill_library import SkillLibraryStore, SkillLibraryError


class SkillLibraryPagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.folder = Path(self.temp.name)
        self.catalog = self.folder / "catalog.json"
        self.database = self.folder / "synthetic.sqlite3"
        self.write_catalog(0)
        @contextmanager
        def connect():
            db = sqlite3.connect(self.database)
            db.row_factory = sqlite3.Row
            try:
                with db:
                    yield db
            finally:
                db.close()
        self.store = SkillLibraryStore(connect, lambda: "2026-10-04T00:00:00+00:00", self.catalog)

    def tearDown(self):
        self.temp.cleanup()

    def write_catalog(self, count):
        self.catalog.write_text(json.dumps({"schemaVersion": 1, "updatedAt": "fixture", "items": [
            {"id": f"official-{i}", "displayName": f"Synthetic {i}", "systemPromptFragment": "synthetic body",
             "licenseText": "synthetic permission"} for i in range(count)]}), encoding="utf-8")

    def accepted(self, count):
        ids = []
        for i in range(count):
            sid = str(UUID(int=i + 1))
            payload = {"skill": {"id": f"usr_fixture_{i}", "displayName": f"Accepted {i}",
                "description": "synthetic", "category": "other", "triggerKeywords": ["synthetic"],
                "systemPromptFragment": "synthetic body"}}
            with self.store.connect() as db:
                db.execute("INSERT INTO skill_submissions(submission_id,owner_hash,payload_json,content_hash,status,received_at,reviewed_at) VALUES(?,?,?,?,?,?,?)",
                    (sid, "not-a-real-token", json.dumps(payload), "fixture-hash", "accepted", "fixture", "fixture"))
            ids.append("community_" + sid.replace("-", ""))
        return ids

    def pages(self):
        result = []
        for page in range(30):
            data = self.store.catalogue(page=page)
            self.assertEqual(data["page"], page)
            self.assertLessEqual(len(data["items"]), 100)
            result.extend(item["id"] for item in data["items"])
            if not data["hasMore"]:
                return result
        self.fail("paging did not terminate")

    def test_existing_small_catalog(self):
        self.write_catalog(10)
        self.assertEqual(self.pages(), [f"official-{i}" for i in range(10)])

    def test_official_catalog_across_pages(self):
        self.write_catalog(231)
        self.assertEqual(self.pages(), [f"official-{i}" for i in range(231)])
        self.assertEqual(len(self.store.catalogue(page=2)["items"]), 31)

    def test_mixed_last_official_page_has_no_gaps(self):
        self.write_catalog(195)
        accepted = self.accepted(12)
        self.assertEqual(self.pages(), [f"official-{i}" for i in range(195)] + accepted)
        self.assertEqual(len(self.store.catalogue(page=2)["items"]), 7)

    def test_exact_official_page_boundary(self):
        self.write_catalog(100)
        accepted = self.accepted(7)
        self.assertTrue(self.store.catalogue()["hasMore"])
        self.assertEqual(self.pages(), [f"official-{i}" for i in range(100)] + accepted)

    def test_community_only_multiple_pages(self):
        accepted = self.accepted(205)
        self.assertEqual(self.pages(), accepted)

    def test_public_list_never_contains_body_or_license_text(self):
        self.write_catalog(120)
        self.accepted(2)
        for page in range(2):
            for item in self.store.catalogue(page=page)["items"]:
                self.assertNotIn("systemPromptFragment", item)
                self.assertNotIn("licenseText", item)
        self.assertEqual(self.store.detail("official-119")["systemPromptFragment"], "synthetic body")

    def test_detail_official_outside_first_page(self):
        self.write_catalog(2000)
        self.assertEqual(self.store.detail("official-1999")["id"], "official-1999")
        self.assertEqual(len(self.pages()), 2000)

    def test_expanded_catalog_old_limit_boundary(self):
        self.write_catalog(1501)
        self.assertEqual(len(self.pages()), 1501)
        self.assertEqual(self.store.detail("official-1500")["id"], "official-1500")

    def test_full_catalog_community_boundary(self):
        self.write_catalog(2000)
        accepted = self.accepted(101)
        self.assertEqual(self.pages(), [f"official-{i}" for i in range(2000)] + accepted)
        self.assertEqual(len(self.store.catalogue(page=20)["items"]), 100)
        self.assertEqual(len(self.store.catalogue(page=21)["items"]), 1)
        self.assertEqual(self.store.detail(accepted[-1])["id"], accepted[-1])

    def test_detail_accepted_outside_first_page(self):
        self.write_catalog(150)
        accepted = self.accepted(105)
        self.assertEqual(self.store.detail(accepted[-1])["id"], accepted[-1])

    def test_unaccepted_submission_is_not_public(self):
        accepted = self.accepted(1)
        with self.store.connect() as db:
            db.execute("UPDATE skill_submissions SET status='pending'")
        self.assertEqual(self.pages(), [])
        with self.assertRaises(SkillLibraryError):
            self.store.detail(accepted[0])

    def test_invalid_pages_and_missing_detail(self):
        for page in (-1, 10001, True, "0"):
            with self.subTest(page=page), self.assertRaises(SkillLibraryError):
                self.store.catalogue(page=page)
        with self.assertRaises(SkillLibraryError):
            self.store.detail("missing")

    def test_larger_catalog_rejected(self):
        self.write_catalog(2001)
        with self.assertRaises(SkillLibraryError):
            self.store.catalogue()

    def test_corrupt_catalog_rejected(self):
        self.catalog.write_text("not-json", encoding="utf-8")
        with self.assertRaises(SkillLibraryError):
            self.store.catalogue()


if __name__ == "__main__":
    unittest.main()
