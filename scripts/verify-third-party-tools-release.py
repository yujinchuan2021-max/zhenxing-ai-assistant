#!/usr/bin/env python3
"""Block tool-bearing releases until every shipped Tools entry has an audited decision.

The approval manifest is deliberately empty initially. A reviewer must add one record
per actual tool directory (or loose file) only after checking the exact version/files,
the licence, and evidence of redistribution rights. A licence filename alone never
constitutes approval.

Approved record example (values are illustrative, NOT an actual approval):
  {"path": "category/tool", "kind": "directory", "decision": "approved",
   "contentSha256": "<digest reported by --show-digests>",
   "author": "<publisher>", "sourceUrl": "https://...", "sourceVersion": "...",
   "licenseEvidence": "https://... or reviewed archive path",
   "redistributionEvidence": "https://... or reviewed archive path",
   "reviewer": "<name>", "reviewedAt": "YYYY-MM-DD",
   "approvedFor": ["installer", "portable", "tools-zip",
                   "lite-portable", "tools-lite-zip"]}

This script checks coverage, recorded evidence, exact file content, and the intended
release forms. It cannot make a legal judgement about the evidence; that remains the
named reviewer's responsibility. It intentionally treats all current release forms
as required even for tools not currently in Lite, so changes to the Lite selection
cannot silently broaden an earlier approval.
"""

from __future__ import annotations

import argparse
from datetime import date
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

REPO = Path(__file__).resolve().parents[1]
DEFAULT_TOOLS = REPO / "TubaWinUi3.WinUI3" / "Tools"
DEFAULT_MANIFEST = REPO / "zxai-docs" / "third-party-tools-approvals.json"
SCHEMA = "zxai-third-party-tools-approvals/1"
RELEASE_FORMS = {"installer", "portable", "tools-zip", "lite-portable", "tools-lite-zip"}
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
PLACEHOLDERS = {"", "todo", "tbd", "unknown", "待核", "待补", "n/a", "none", "-"}
REPARSE_POINT = 0x400
PROHIBITED_FILENAMES = {"pkey.txt"}  # e.g. AIDA64 key material; never place in a release payload


def fail(message: str) -> None:
    raise ValueError(message)


def regular_kind(path: Path) -> str:
    st = path.lstat()
    if stat.S_ISLNK(st.st_mode) or (getattr(st, "st_file_attributes", 0) & REPARSE_POINT):
        fail(f"reparse point or symlink is not auditable: {path}")
    if stat.S_ISDIR(st.st_mode):
        return "directory"
    if stat.S_ISREG(st.st_mode):
        return "file"
    fail(f"unsupported filesystem entry in Tools: {path}")


def files_under(path: Path) -> list[Path]:
    if regular_kind(path) == "file":
        return [path]
    files: list[Path] = []
    pending = [path]
    while pending:
        directory = pending.pop()
        for child in directory.iterdir():
            kind = regular_kind(child)
            if kind == "directory":
                pending.append(child)
            else:
                files.append(child)
    return sorted(files, key=lambda p: p.relative_to(path).as_posix().casefold())


def discover(tools: Path) -> dict[str, tuple[str, Path]]:
    if not tools.is_dir() or regular_kind(tools) != "directory":
        fail(f"Tools directory missing or invalid: {tools}")
    entries: dict[str, tuple[str, Path]] = {}
    normalized: set[str] = set()

    def add(path: Path) -> None:
        relative = path.relative_to(tools).as_posix()
        folded = relative.casefold()
        if folded in normalized:
            fail(f"case-insensitive duplicate Tools path: {relative}")
        normalized.add(folded)
        entries[relative] = (regular_kind(path), path)
        for file in files_under(path):
            if file.name.casefold() in PROHIBITED_FILENAMES:
                fail(f"credential-like file may be shipped: {file.relative_to(tools).as_posix()}")
        # Also reject nested links/special files while approvals are pending.

    for top in tools.iterdir():
        if regular_kind(top) == "file":
            add(top)
        else:
            # The first level is a category. Its direct subdirectories are tools;
            # any direct files are loose payload and need their own approval.
            for child in top.iterdir():
                add(child)
    return entries


def content_sha256(path: Path) -> str:
    """SHA-256 over each relative file name + each file's SHA-256, in path order."""
    aggregate = hashlib.sha256()
    for file in files_under(path):
        relative = file.relative_to(path).as_posix() if path.is_dir() else file.name
        file_hash = hashlib.sha256()
        with file.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                file_hash.update(chunk)
        aggregate.update(relative.encode("utf-8"))
        aggregate.update(b"\0")
        aggregate.update(file_hash.digest())
    return aggregate.hexdigest()


def provided(value: object) -> bool:
    return isinstance(value, str) and value.strip().casefold() not in PLACEHOLDERS


def verify(tools: Path, manifest: Path) -> int:
    actual = discover(tools)
    if not manifest.is_file():
        fail(f"approval manifest missing: {manifest}")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    if not isinstance(data, dict) or data.get("schema") != SCHEMA or not isinstance(data.get("entries"), list):
        fail(f"approval manifest schema invalid (expected {SCHEMA})")

    decisions: dict[str, dict] = {}
    folded: set[str] = set()
    problems: list[str] = []
    for record in data["entries"]:
        if not isinstance(record, dict):
            problems.append("non-object approval record")
            continue
        path = record.get("path")
        if not isinstance(path, str) or not path or "\\" in path or path.startswith("/") or ".." in path.split("/"):
            problems.append(f"invalid approval path: {path!r}")
            continue
        if path.casefold() in folded:
            problems.append(f"duplicate approval path: {path}")
            continue
        folded.add(path.casefold())
        decisions[path] = record

    for path in sorted(actual.keys() - decisions.keys(), key=str.casefold):
        problems.append(f"no approval record: {path}")
    for path in sorted(decisions.keys() - actual.keys(), key=str.casefold):
        problems.append(f"approval record has no matching payload: {path}")

    approved = 0
    for path, record in decisions.items():
        if path not in actual:
            continue
        kind, source = actual[path]
        if record.get("kind") != kind:
            problems.append(f"kind mismatch: {path} (expected {kind})")
            continue
        decision = record.get("decision")
        if decision != "approved":
            problems.append(f"not approved: {path} ({decision or 'no decision'})")
            continue
        missing = [field for field in ("author", "sourceUrl", "sourceVersion",
                  "licenseEvidence", "redistributionEvidence", "reviewer", "reviewedAt")
                   if not provided(record.get(field))]
        if missing:
            problems.append(f"approved record lacks review evidence: {path} ({', '.join(missing)})")
            continue
        try:
            date.fromisoformat(record["reviewedAt"])
        except ValueError:
            problems.append(f"invalid reviewedAt date: {path}")
            continue
        scopes = record.get("approvedFor")
        if (not isinstance(scopes, list) or not all(isinstance(scope, str) for scope in scopes)
                or len(scopes) != len(RELEASE_FORMS) or set(scopes) != RELEASE_FORMS):
            problems.append(f"release form coverage incomplete: {path}")
            continue
        locked_hash = record.get("contentSha256")
        if not isinstance(locked_hash, str) or not SHA256.fullmatch(locked_hash):
            problems.append(f"missing or invalid contentSha256: {path}")
            continue
        current_hash = content_sha256(source)
        if current_hash != locked_hash:
            problems.append(f"payload changed since review: {path} (current {current_hash})")
            continue
        approved += 1

    if problems:
        print(f"[FAIL] third-party Tools redistribution gate: {approved}/{len(actual)} entries approved")
        for problem in problems[:25]:
            print("  - " + problem)
        if len(problems) > 25:
            print(f"  ... and {len(problems) - 25} more; see {manifest}")
        return 1
    print(f"[OK] third-party Tools redistribution gate: {approved}/{len(actual)} entries approved and content locked")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, default=DEFAULT_TOOLS)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--show-digests", action="store_true", help="print current path/kind/contentSha256 for human review; does not approve anything")
    args = parser.parse_args()
    try:
        if args.show_digests:
            for path, (kind, source) in sorted(discover(args.tools).items(), key=lambda item: item[0].casefold()):
                print(json.dumps({"path": path, "kind": kind, "contentSha256": content_sha256(source)}, ensure_ascii=False))
            return 0
        return verify(args.tools, args.manifest)
    except (OSError, ValueError, json.JSONDecodeError) as ex:
        print(f"[FAIL] third-party Tools redistribution gate: {ex}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
