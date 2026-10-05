from datetime import datetime, timedelta, timezone
from pathlib import Path
import tempfile
import unittest
from daily_schedule import DailySchedule

class DailyScheduleTests(unittest.TestCase):
    def test_beijing_nine_and_restart_no_duplicate(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/"schedule.json";s=DailySchedule(path)
            before=datetime(2026,10,4,0,59,tzinfo=timezone.utc)
            due=datetime(2026,10,4,1,0,tzinfo=timezone.utc)
            self.assertFalse(s.claim(before));self.assertTrue(s.claim(due))
            self.assertFalse(DailySchedule(path).claim(due+timedelta(hours=4)))
            self.assertTrue(DailySchedule(path).claim(due+timedelta(days=1)))
    def test_missed_time_catches_up_once_and_calendar_does_not_drift(self):
        with tempfile.TemporaryDirectory() as temp:
            s=DailySchedule(Path(temp)/"s.json")
            after=datetime(2026,10,4,10,0,tzinfo=timezone.utc)
            self.assertTrue(s.claim(after))
            self.assertEqual(s.next_time(after).isoformat(),"2026-10-05T09:00:00+08:00")
    def test_bad_state_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/"s.json";p.write_text("corrupt")
            with self.assertRaises(ValueError):DailySchedule(p)
            self.assertEqual(p.read_text(),"corrupt")
    def test_no_network_retry_for_failed_day(self):
        with tempfile.TemporaryDirectory() as temp:
            s=DailySchedule(Path(temp)/"s.json")
            now=datetime(2026,10,4,1,0,tzinfo=timezone.utc)
            self.assertTrue(s.claim(now));self.assertFalse(s.claim(now+timedelta(minutes=5)))
            self.assertLessEqual(s.wait_seconds(now),60)
    def test_non_object_state_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/"s.json"
            for raw in ("null", "[]", '"text"'):
                p.write_text(raw)
                with self.assertRaises(ValueError):DailySchedule(p)
                self.assertEqual(p.read_text(),raw)
