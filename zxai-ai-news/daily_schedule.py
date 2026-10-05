"""One daily attempt in the existing feed writer, independent of host timezone."""
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import re
import tempfile
import os

BEIJING = timezone(timedelta(hours=8))


class DailySchedule:
    def __init__(self, path, update_time="09:00"):
        if not re.fullmatch(r"(?:[01][0-9]|2[0-3]):[0-5][0-9]", update_time):
            raise ValueError("daily update time must be HH:MM")
        self.path, self.update_time = Path(path), update_time
        self.hour, self.minute = map(int, update_time.split(":"))
        self.attempted_date = None
        if self.path.exists():
            saved = json.loads(self.path.read_text(encoding="utf-8"))
            if not isinstance(saved, dict):
                raise ValueError("daily schedule state damaged")
            value = saved.get("attemptedDate")
            if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", value):
                raise ValueError("daily schedule state damaged")
            datetime.strptime(value, "%Y-%m-%d")
            self.attempted_date = value

    def due(self, now):
        local = now.astimezone(BEIJING)
        target = local.replace(hour=self.hour, minute=self.minute, second=0, microsecond=0)
        return local >= target and (self.attempted_date is None or self.attempted_date < local.date().isoformat())

    def next_time(self, now):
        local = now.astimezone(BEIJING)
        target = local.replace(hour=self.hour, minute=self.minute, second=0, microsecond=0)
        if self.due(now):
            return local
        if local >= target or self.attempted_date == local.date().isoformat():
            target += timedelta(days=1)
        return target

    def claim(self, now):
        if not self.due(now):
            return False
        date = now.astimezone(BEIJING).date().isoformat()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, name = tempfile.mkstemp(prefix=".daily-schedule-", dir=self.path.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                json.dump({"attemptedDate": date, "startedAt": now.isoformat(), "updateTime": self.update_time,
                           "timezone": "Asia/Shanghai"}, stream)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(name, self.path)
        finally:
            if os.path.exists(name):
                os.unlink(name)
        self.attempted_date = date
        return True

    def wait_seconds(self, now):
        return max(1, min(60, (self.next_time(now) - now.astimezone(BEIJING)).total_seconds()))
