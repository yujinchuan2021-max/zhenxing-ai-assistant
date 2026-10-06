#!/usr/bin/env python3
"""Produce complete own-origin package evidence; never publish or execute tools.

The production POST publisher repeats verification itself. This CLI's JSON is a
review artifact and cannot bypass the publication gate.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "zxai-toolflow-server"))

from tool_catalog import MAX_CATALOG_BYTES, ToolCatalogError
from tool_package_verifier import verify_catalog_packages


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate catalog JSON key")
        result[key] = value
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--receipt", required=True, type=Path,
                        help="new evidence JSON path; existing receipts are never overwritten")
    args = parser.parse_args()
    try:
        if args.catalog.stat().st_size > MAX_CATALOG_BYTES:
            raise ValueError("catalog exceeds the 4 MiB limit")
        if args.receipt.exists():
            raise ValueError("receipt output already exists; choose a new path")
        source = args.catalog.read_bytes()
        catalog = json.loads(source.decode("utf-8-sig"), object_pairs_hook=unique_object)
        receipt = verify_catalog_packages(catalog)
        # Keep the raw source file's hash as additional evidence. The production
        # receipt binds the canonical validated JSON actually stored/published.
        receipt["sourceFileSha256"] = hashlib.sha256(source).hexdigest()
        with args.receipt.open("x", encoding="utf-8") as output:
            json.dump(receipt, output, ensure_ascii=False, indent=2)
            output.write("\n")
        print(json.dumps({"revision": receipt["revision"], "catalogSha256": receipt["catalogSha256"],
                          "verifiedOrigins": receipt["originCount"], "downloadedBytes": receipt["downloadedBytes"],
                          "toolsExecuted": False}, ensure_ascii=False))
        return 0
    except (ToolCatalogError, ValueError, OSError, UnicodeError) as error:
        # No HTTP payload, authentication headers, OS proxy environment or raw
        # network exception is serialized by the verifier.
        message = error.message if isinstance(error, ToolCatalogError) else str(error)
        print("Package verification failed: " + message, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
