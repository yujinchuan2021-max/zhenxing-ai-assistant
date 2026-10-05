#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""verify-content-lock.py — 独立复核 zxai-content-lock/1 内容锁（不依赖打包脚本）。

用法 1（复核真实目录）:
    python scripts/verify-content-lock.py --dir <目录> --lock <lock.json> [--exclude-part <目录名> ...]

用法 2（固定样本自检，钉住聚合算法）:
    python scripts/verify-content-lock.py --selftest

复核模式做三件事（全部独立实现，不复用 make-staging.py 的扫描代码）:
  * 重新扫描目录逐文件算 sha256，与锁文件比对：missing / changed / extra 必须全 0；
  * 以磁盘实际内容独立重算聚合值，必须等于锁文件 aggregateSha256；
  * 文件数与字节数必须等于锁记录。

自检模式用一份固定样本（内容和相对路径全部写死）验证 make-staging.py 的 content_lock：
  * 聚合值必须等于独立推导的预登记常量（排序序算法）；
  * 聚合值必须不等于修复前“遍历序”算法的值（负控：防止回退到旧实现）；
  * 锁 JSON 的 files 键序必须为排序序；
  * 篡改任一文件内容 / 改名后，聚合值必须变化；
  * 本脚本的独立实现与 make-staging 实现交叉一致。

退出码：0 = 全部通过；1 = 任一不一致。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAKE_STAGING = os.path.join(REPO, "scripts", "make-staging.py")

FILE_ATTRIBUTE_REPARSE_POINT = 0x400

# ── 固定样本与预登记常量 ──
# 样本内容/路径写死；两个常量均在该脚本之外、用独立代码对同一算法推导：
#   EXPECTED          = sha256 over 排序行的「relpath\tbytes\tsha256」
#   OLD_TRAVERSAL_AGG = 修复前遍历序（根文件 a.txt,z.txt → dir1 → dir1/c）的值（负控用）
SELFTEST_FIXTURE = {
    "a.txt": b"alpha",
    "z.txt": b"zulu",
    "dir1/b.txt": b"bravo",
    "dir1/c/d.txt": b"delta",
}
SELFTEST_EXPECTED_AGG = "a0c4245b3042fd1db0c18a9052fb36b1315a2e2e7213717dada66a47f2ab297d"
SELFTEST_OLD_TRAVERSAL_AGG = "7a715d6e6aea00128f3b4bcad94b5d674177fdf5b436c197b3e9967b4bb9efca"


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def is_reparse_point(path: str) -> bool:
    try:
        st = os.lstat(path)
    except OSError:
        return False
    return bool(getattr(st, "st_file_attributes", 0) & FILE_ATTRIBUTE_REPARSE_POINT)


def independent_scan(root: str, exclude_parts: tuple = ()):
    """独立扫描实现。返回 (entries: rel -> (size, sha), total, reparse)。"""
    entries = {}
    total = 0
    reparse = []
    for dirpath, dirnames, filenames in os.walk(root):
        rel_dir = os.path.relpath(dirpath, root)
        parts = [] if rel_dir == "." else rel_dir.replace("\\", "/").split("/")
        if any(p in exclude_parts for p in parts):
            dirnames[:] = []
            continue
        if rel_dir != ".":
            for d in list(dirnames):
                if is_reparse_point(os.path.join(dirpath, d)):
                    reparse.append("%s/%s" % (rel_dir.replace("\\", "/"), d))
                    dirnames.remove(d)
        for f in filenames:
            p = os.path.join(dirpath, f)
            if is_reparse_point(p):
                reparse.append(f if rel_dir == "." else "%s/%s" % (rel_dir.replace("\\", "/"), f))
                continue
            rel = f if rel_dir == "." else "%s/%s" % (rel_dir.replace("\\", "/"), f)
            size = os.path.getsize(p)
            entries[rel] = (size, sha256_file(p))
            total += size
    return entries, total, reparse


def independent_aggregate(entries) -> str:
    h = hashlib.sha256()
    for rel in sorted(entries):
        size, digest = entries[rel]
        h.update(("%s\t%d\t%s\n" % (rel, size, digest)).encode("utf-8"))
    return h.hexdigest()


def verify(args) -> int:
    lock = json.load(open(args.lock, "r", encoding="utf-8-sig"))
    locked = lock.get("files") or {}
    entries, total, reparse = independent_scan(args.dir, tuple(args.exclude_part or ()))

    missing = sorted(set(locked) - set(entries))
    extra = sorted(set(entries) - set(locked))
    changed = sorted(r for r in set(entries) & set(locked) if entries[r][1] != locked[r])
    agg = independent_aggregate(entries)

    ok = (not missing and not extra and not changed
          and agg == lock.get("aggregateSha256")
          and total == lock.get("sizeBytes")
          and len(entries) == lock.get("fileCount"))

    print("== verify-content-lock")
    print("   dir:  %s" % args.dir)
    print("   lock: %s" % args.lock)
    print("   scheme: %s   label: %s" % (lock.get("schema"), lock.get("label")))
    print("   files: 磁盘 %d / 锁 %s    bytes: 磁盘 %d / 锁 %s" % (len(entries), lock.get("fileCount"), total, lock.get("sizeBytes")))
    print("   aggregate: 磁盘 %s / 锁 %s" % (agg, lock.get("aggregateSha256")))
    print("   missing: %d   changed: %d   extra: %d" % (len(missing), len(changed), len(extra)))
    for r in missing[:10]:
        print("     MISSING %s" % r)
    for r in changed[:10]:
        print("     CHANGED %s (锁 %s -> 磁盘 %s)" % (r, locked[r][:16], entries[r][1][:16]))
    for r in extra[:10]:
        print("     EXTRA   %s" % r)
    if reparse:
        print("   reparse points not followed: %d（锁记录 %d）" % (len(reparse), len(lock.get("reparsePointsNotFollowed") or [])))
    print("[VERIFY-%s]" % ("OK" if ok else "FAIL"))
    return 0 if ok else 1


def selftest() -> int:
    import importlib.util
    spec = importlib.util.spec_from_file_location("zxai_make_staging", MAKE_STAGING)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    checks = []
    tmp = tempfile.mkdtemp(prefix="zxai-lock-selftest-")
    try:
        fix = os.path.join(tmp, "fixture")
        for rel, data in SELFTEST_FIXTURE.items():
            p = os.path.join(fix, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as fh:
                fh.write(data)

        lock_path = os.path.join(tmp, "lock.json")
        summary = mod.content_lock(fix, lock_path=lock_path, label="selftest-fixture")
        checks.append(("content_lock 聚合 == 预登记常量（排序序）",
                       summary["aggregateSha256"] == SELFTEST_EXPECTED_AGG))
        checks.append(("聚合 != 旧遍历序值（负控：防回退）",
                       summary["aggregateSha256"] != SELFTEST_OLD_TRAVERSAL_AGG))
        checks.append(("fileCount/sizeBytes 与样本一致",
                       summary["fileCount"] == len(SELFTEST_FIXTURE)
                       and summary["sizeBytes"] == sum(len(v) for v in SELFTEST_FIXTURE.values())))

        lock_doc = json.load(open(lock_path, "r", encoding="utf-8-sig"))
        checks.append(("lock.files 键序 = 排序序（JSON 可复现）",
                       list(lock_doc["files"].keys()) == sorted(lock_doc["files"].keys())))

        entries, total, _ = independent_scan(fix)
        checks.append(("独立实现聚合一致", independent_aggregate(entries) == SELFTEST_EXPECTED_AGG))
        checks.append(("独立实现文件集一致", set(entries) == set(SELFTEST_FIXTURE)))

        with open(os.path.join(fix, "dir1", "b.txt"), "ab") as fh:
            fh.write(b"!")
        tampered = mod.content_lock(fix, label="selftest-tampered")
        checks.append(("篡改内容后聚合必变化", tampered["aggregateSha256"] != SELFTEST_EXPECTED_AGG))

        # 还原内容；再做改名负控
        with open(os.path.join(fix, "dir1", "b.txt"), "wb") as fh:
            fh.write(SELFTEST_FIXTURE["dir1/b.txt"])
        restored = mod.content_lock(fix, label="selftest-restored")
        checks.append(("还原后聚合回到常量", restored["aggregateSha256"] == SELFTEST_EXPECTED_AGG))
        os.rename(os.path.join(fix, "a.txt"), os.path.join(fix, "renamed.txt"))
        renamed = mod.content_lock(fix, label="selftest-renamed")
        checks.append(("改名后聚合必变化", renamed["aggregateSha256"] != SELFTEST_EXPECTED_AGG))

        # 增文件负控
        with open(os.path.join(fix, "extra.bin"), "wb") as fh:
            fh.write(b"x")
        added = mod.content_lock(fix, label="selftest-added")
        checks.append(("新增文件后聚合必变化", added["aggregateSha256"] != SELFTEST_EXPECTED_AGG))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    for name, ok in checks:
        print(("  [OK]   " if ok else "  [FAIL] ") + name)
    allok = all(ok for _, ok in checks)
    print("[SELFTEST-%s]  %d/%d" % ("OK" if allok else "FAIL", sum(1 for _, ok in checks if ok), len(checks)))
    return 0 if allok else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="独立复核 zxai-content-lock/1 内容锁")
    ap.add_argument("--dir")
    ap.add_argument("--lock")
    ap.add_argument("--exclude-part", action="append", default=None,
                    help="排除目录名（可重复；与锁生成时的 exclude_parts 语义一致）")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args()

    if args.selftest:
        return selftest()
    if not args.dir or not args.lock:
        ap.error("需要 --dir 与 --lock（或使用 --selftest）")
    return verify(args)


if __name__ == "__main__":
    sys.exit(main())
