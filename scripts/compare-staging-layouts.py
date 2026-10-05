#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZXAI: compare two staging layouts file-by-file — build (`app/`) vs self-contained publish
(`app-selfcontained/`).

Answers, with evidence instead of adjectives:
  * which files exist only in the build layout (i.e. what publish drops — the pri/xbf class of bug),
  * which files exist only in the publish layout (the .NET self-contained runtime payload),
  * same-named files whose size differs,
  * presence of the decisive markers: coreclr.dll / hostfxr.dll / hostpolicy.dll / clrjit.dll,
    `$(TargetName).pri`, `TubaWinUi3.exe|dll|runtimeconfig.json`,
  * `runtimeconfig.json`: `frameworks` (framework-dependent) vs `includedFrameworks` (self-contained).

Layout roots are constants below (`STAGING`); adjust or import the module when auditing another
staging directory. Exit code is always 0 — this is a reporting tool, the caller compares output.
"""
import json, os, sys

STAGING = r"D:\tubatools\TubaWinUi3.WinUI3\artifacts\remediation-2026-09-21\staging"
A = os.path.join(STAGING, "app")
B = os.path.join(STAGING, "app-selfcontained")
SKIP_PARTS = ("runtime", "Tools", "IconCache")   # runtime 由 prepare-dsh-runtime 单独组装；Tools 被 ExcludeToolsFromPublish 排除


def listing(root):
    out = {}
    for r, dirs, files in os.walk(root):
        rel_dir = os.path.relpath(r, root)
        parts = [] if rel_dir == "." else rel_dir.replace("\\", "/").split("/")
        if any(p in SKIP_PARTS for p in parts):
            dirs[:] = []
            continue
        for f in files:
            rel = f if rel_dir == "." else "%s/%s" % (rel_dir.replace("\\", "/"), f)
            out[rel.lower()] = os.path.getsize(os.path.join(r, f))
    return out


def size_of(root):
    total = 0
    for r, dirs, files in os.walk(root):
        rel = os.path.relpath(r, root)
        parts = [] if rel == "." else rel.replace("\\", "/").split("/")
        if any(p in SKIP_PARTS for p in parts):
            dirs[:] = []
            continue
        for f in files:
            try:
                total += os.path.getsize(os.path.join(r, f))
            except OSError:
                pass
    return total


a, b = listing(A), listing(B)
print("=== 文件清单（排除 runtime/、Tools/、IconCache/） ===")
print("build(app)            : %d 个文件, %.1f MB" % (len(a), size_of(A) / 1e6))
print("publish(app-selfcontained): %d 个文件, %.1f MB" % (len(b), size_of(B) / 1e6))
only_a = sorted(set(a) - set(b))
only_b = sorted(set(b) - set(a))
print("\n仅 build 有（%d）：" % len(only_a))
for f in only_a[:40]:
    print("   -", f)
print("\n仅 publish 有（%d）：" % len(only_b))
for f in only_b[:60]:
    print("   +", f)
diff_bytes = sorted(((abs(a[k] - b[k]), k) for k in set(a) & set(b) if a[k] != b[k]), reverse=True)
print("\n同名但大小不同（%d）：" % len(diff_bytes))
for d, k in diff_bytes[:10]:
    print("   ~ %-50s build=%d publish=%d" % (k, a[k], b[k]))

print("\n=== 关键判定 ===")
for name in ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "clrjit.dll", "TubaWinUi3.pri",
             "TubaWinUi3.exe", "TubaWinUi3.dll", "TubaWinUi3.runtimeconfig.json"):
    print("  %-28s build=%-6s publish=%-6s" % (
        name, os.path.isfile(os.path.join(A, name)), os.path.isfile(os.path.join(B, name))))

print("\n=== runtimeconfig.json ===")
for label, root in (("build  ", A), ("publish", B)):
    p = os.path.join(root, "TubaWinUi3.runtimeconfig.json")
    if not os.path.isfile(p):
        print("  %s: <缺失>" % label)
        continue
    ro = json.load(open(p, encoding="utf-8-sig"))["runtimeOptions"]
    fw = ro.get("frameworks")
    inc = ro.get("includedFrameworks")
    print("  %s: frameworks=%s includedFrameworks=%s" % (
        label,
        None if fw is None else [f["name"] + "@" + f["version"] for f in fw],
        None if inc is None else [f["name"] + "@" + f["version"] for f in inc]))
print("\n  说明：includedFrameworks = .NET 自包含（.NET 随包携带，无框架依赖）；frameworks = 框架依赖（需目标机装 .NET 10）")

print("\n=== 目录总大小（含 runtime/） ===")
print("  build(app)                : %.1f MB" % (sum(os.path.getsize(os.path.join(r, f)) for r, d, fs in os.walk(A) for f in fs) / 1e6))
print("  publish(app-selfcontained): %.1f MB" % (sum(os.path.getsize(os.path.join(r, f)) for r, d, fs in os.walk(B) for f in fs) / 1e6))
