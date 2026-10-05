#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZXAI A16: reproducible staging build -> ONE self-contained app directory.

Target layout (PRI + XBF + .NET runtime + Node + dsh all in the same directory):

  <out>/app/                        dotnet publish --self-contained true -r win-x64 -c Release
                                    (csproj target CompleteAppPublishLayout injects
                                     TubaWinUi3.pri + every **/*.xbf + the AssistStudio payload)
  <out>/app/runtime/                app-private Node + dsh (scripts/prepare-dsh-runtime.ps1)
                                      node.exe / dsh/lib/bin.js / versions.json (fail-closed lock)
  <out>/verify-runtime-smoke.log     offline ACP smoke output of THIS run (human evidence)
  <out>/smoke-report.json            machine-readable smoke report of THIS run (--report)
  <out>/runtime-dependencies.json    installed dsh dependency inventory (nested tree, per-package)
  <out>/app-content-lock.json        sha256 of EVERY file in app/ (excluding runtime/)
  <out>/runtime-content-lock.json    sha256 of EVERY file in app/runtime/ (node.exe + dsh/**)
  <out>/source-fingerprint.json      source content fingerprint captured BEFORE and AFTER the build
  <out>/BUILD-INFO.json              release manifest describing the directory above
  <out>/app/BUILD-INFO.json          identical copy: the staging dir is self-describing

Why publish instead of build (2026-09-22): `dotnet publish` used to drop the app's own
`TubaWinUi3.pri` and all 83 `.xbf` files (the WinUI/MSIX toolchain only wires them into the
Build copy pipeline, never into ResolvedFileToPublish). The csproj target
`CompleteAppPublishLayout` now injects pri + xbf + AssistStudio payload before the publish item
list is computed, so the self-contained publish output is a strict superset of the build output
(verified: 0 build-only files) AND carries .NET itself (coreclr/hostfxr/hostpolicy,
`includedFrameworks` runtimeconfig). One directory, one smoke test, one manifest.

Evidence contract (what is real, what is bounded):
  * every sha256 in the manifest is computed by THIS run from the real files inside <out>
    (never copied from an earlier manifest); `app-content-lock.json` / `runtime-content-lock.json`
    hash every single released file, so the manifest names exact bytes;
  * the source fingerprint is CONTENT based, not status based: the tracked diff is the raw
    `git diff HEAD` patch text hashed, and every untracked (non-ignored) file is hashed
    individually -- equal-length edits or "same path list, different content" both change it.
    It is captured before the build AND after the smoke test; a difference means the binaries
    do not correspond to the recorded source revision and the run fails;
  * boundaries (nothing implied beyond this): dependency inventory covers the full installed
    node_modules tree (top level + nested) with per-package name/version/package.json hash, plus
    dsh's declared dependencies resolved to the installed copies -- semver range satisfaction is
    NOT solved, devDependencies are not shipped; the .NET/MSBuild toolchain itself is not locked,
    only the files it copies into the release dir (see manifest `boundaries`).

Steps (any failure stops the run with exit 1 -- fail-closed):
  1) dotnet publish        -> <out>/app            (--skip-publish to reuse an existing layout)
  2) dsh runtime assembly  -> <out>/app/runtime    (--skip-runtime; refuses to combine with publish
                                                    because publish rebuilds app/ from scratch)
  3) offline ACP smoke     (scripts/verify-dsh-runtime.py --report ...; no model requests)
  4) BUILD-INFO.json       (manifest of this run; `complete` = publish+runtime+smoke all ran)

Usage:
  python scripts/make-staging.py [--out DIR] [--node-exe PATH] [--dsh-source PATH]
                                 [--skip-publish] [--skip-runtime] [--skip-smoke] [--verbose]

Defaults target the remediation staging dir; node/dsh default to the local dev environment
(for packaging on a release machine pass explicit paths).
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = os.environ.get("TUBA_DOTNET", r"D:\AI\zhenxingAI-toolchains\dotnet-sdk-10\dotnet.exe")
DEFAULT_OUT = os.path.join(REPO, "TubaWinUi3.WinUI3", "artifacts", "remediation-2026-09-21", "staging")
DEFAULT_NODE = os.path.join(os.environ.get("LOCALAPPDATA", ""), "hermes", "node", "node.exe")
DEFAULT_DSH = os.path.join(os.environ.get("LOCALAPPDATA", ""), "hermes", "node", "node_modules", "@deepseek-ai", "dsh")
POWERSHELL = r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
CSPROJ = os.path.join(REPO, "TubaWinUi3.WinUI3", "TubaWinUi3.csproj")

PUBLISH_TIMEOUT = 3600          # Release / win-x64 / self-contained + ReadyToRun：慢是正常的
RUNTIME_TIMEOUT = 1800          # 复制 ~360MB runtime（node.exe + dsh 闭包）
SMOKE_TIMEOUT = 900             # 烟测自身每步 90s 超时；整脚本给足余量

# publish 布局必须存在的自包含标记（缺失即 fail-closed）
DOTNET_RUNTIME_MARKERS = ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "clrjit.dll")
BACKEND_KEY_FILES = ("backend/TubaWinUI3.BackEnd.exe", "backend/TubaWinUI3.BackEnd.dll",
                     "backend/TubaWinUI3.BackEnd.deps.json", "backend/TubaWinUI3.BackEnd.runtimeconfig.json")
APP_KEY_FILES = ("TubaWinUi3.exe", "TubaWinUi3.dll", "TubaWinUi3.pri",
                 "TubaWinUi3.runtimeconfig.json", "TubaWinUi3.deps.json") + BACKEND_KEY_FILES
RUNTIME_KEY_FILES = ("node.exe", "dsh/lib/bin.js", "dsh/package.json", "versions.json")
FILE_ATTRIBUTE_REPARSE_POINT = 0x400

VERBOSE = False


# ─────────────────────────────── small helpers ───────────────────────────────

def fail(msg: str) -> None:
    print("[FAIL] " + msg)
    sys.exit(1)


def now() -> str:
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def shell_join(cmd) -> str:
    out = []
    for part in cmd:
        part = str(part)
        out.append('"%s"' % part if (" " in part or "\t" in part) else part)
    return " ".join(out)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_json(path: str) -> dict:
    with open(path, "r", encoding="utf-8-sig") as fh:
        return json.load(fh)


def rel_or_abs(path: str, base: str) -> str:
    """相对 base 的路径；跨盘符时 relpath 会抛 ValueError —— 退回绝对路径（不因探针/异盘布局崩）。"""
    try:
        return os.path.relpath(path, base).replace("\\", "/")
    except ValueError:
        return os.path.abspath(path).replace("\\", "/")


def write_json(path: str, doc) -> str:
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    return sha256_file(path)


def capture(cmd, cwd: str = REPO, timeout: int = 60, raw: bool = False):
    """软执行（不因失败退出）：返回 (exitCode, stdout+stderr)。

    显式 encoding+errors：中文 Windows 上 git/taskkill 等本地化输出不是 UTF-8，
    默认解码会在读取线程里抛 UnicodeDecodeError（实测）。
    """
    try:
        r = subprocess.run(cmd, cwd=cwd, capture_output=True, timeout=timeout,
                           encoding=None if raw else "utf-8",
                           errors=None if raw else "replace")
        out = (r.stdout or b"") + (r.stderr or b"") if raw else (r.stdout or "") + (r.stderr or "")
        return r.returncode, out
    except Exception as ex:  # 缺工具/超时都只记录，不致命
        return -1, (b"unavailable: " + str(ex).encode("utf-8")) if raw else "unavailable: %s" % ex


def run(cmd, what: str, timeout: int = 900, cwd: str = REPO, log_path: str = None):
    """硬执行：非 0 退出即 [FAIL] + exit 1（fail-closed）。可选把合并输出写入 log_path。"""
    started = now()
    t0 = time.time()
    print("== %s\n   $ %s" % (what, shell_join(cmd)))
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=timeout)
    duration = time.time() - t0
    combined = (r.stdout or "") + (r.stderr or "")
    if log_path:
        with open(log_path, "w", encoding="utf-8") as fh:
            fh.write("# %s\n# $ %s\n# started %s  exit=%d  duration=%.1fs\n\n"
                     % (what, shell_join(cmd), started, r.returncode, duration))
            fh.write(combined)
    if r.returncode != 0:
        print(combined[-4000:])
        fail("%s (exit=%d)" % (what, r.returncode))
    if VERBOSE:
        print(combined[-8000:])
    print("   [OK] %s（%.1fs）" % (what, duration))
    return r, duration


def _is_reparse_point(path: str) -> bool:
    try:
        st = os.lstat(path)
    except OSError:
        return False
    return bool(getattr(st, "st_file_attributes", 0) & FILE_ATTRIBUTE_REPARSE_POINT)


# ─────────────────── 内容锁（逐文件 sha256，本次真实文件）───────────────────

def content_lock(root: str, exclude_parts: tuple = (), lock_path: str = None, label: str = "") -> dict:
    """对 root 下每个文件计算 sha256，写 <lock_path>，返回聚合摘要。

    aggregate = sha256 over **全局按相对路径排序**的「相对路径\\t字节数\\tsha256」行 —— 任何文件的内容/增删/改名都会改变它。
    排序在聚合之前显式执行：聚合值与 os.walk 的遍历顺序无关（2026-09-23 修正 —— 此前按遍历顺序即时更新
    哈希，与“排序后”的算法声明不符，同一份内容在两次遍历顺序不同的情况下会得到不同的聚合值）。
    lock JSON 的 files 也按同一排序写出（文件可逐字节复现）。
    重解析点（junction/symlink）不下钻、只记录：避免顺着链接跑出该目录。
    """
    entries: dict = {}   # rel -> (size, digest)，收集后统一排序
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
                if _is_reparse_point(os.path.join(dirpath, d)):
                    reparse.append(("%s/%s" % (rel_dir.replace("\\", "/"), d)))
                    dirnames.remove(d)
        for f in filenames:
            p = os.path.join(dirpath, f)
            if _is_reparse_point(p):
                reparse.append(f if rel_dir == "." else "%s/%s" % (rel_dir.replace("\\", "/"), f))
                continue
            try:
                size = os.path.getsize(p)
                digest = sha256_file(p)
            except OSError as ex:
                fail("内容锁无法读取 %s：%s" % (p, ex))
            rel = f if rel_dir == "." else "%s/%s" % (rel_dir.replace("\\", "/"), f)
            entries[rel] = (size, digest)
            total += size
    files: dict = {}
    h = hashlib.sha256()
    for rel in sorted(entries):
        size, digest = entries[rel]
        files[rel] = digest
        h.update(("%s\t%d\t%s\n" % (rel, size, digest)).encode("utf-8"))
    summary = {
        "label": label,
        "root": root,
        "fileCount": len(files),
        "sizeBytes": total,
        "aggregateSha256": h.hexdigest(),
        "algorithm": "sha256 over globally relpath-sorted 'relpath\\tbytes\\tsha256' lines",
        "sortOrder": "entries sorted by relpath before hashing — independent of os.walk traversal order",
        "reparsePointsNotFollowed": reparse,
    }
    if lock_path:
        summary["lockPath"] = os.path.basename(lock_path)
        summary["lockSha256"] = write_json(lock_path, {
            "schema": "zxai-content-lock/1", "generatedAt": now(), **summary, "files": files})
    return summary


def key_records(lock_files: dict, rels: tuple) -> list:
    """从内容锁里取关键文件的实物记录（哈希已在锁里算过，不重复读盘）。"""
    out = []
    for rel in rels:
        digest = lock_files.get(rel)
        out.append({"path": rel, "present": digest is not None, "sha256": digest})
    return out


def iter_installed_packages(modules_dir: str):
    """枚举已安装包目录：**顶层 + 嵌套 node_modules**（逐层剪枝，只下钻 node_modules）。"""
    stack = [modules_dir]
    while stack:
        base = stack.pop()
        try:
            names = sorted(os.listdir(base))
        except OSError:
            continue
        for name in names:
            p = os.path.join(base, name)
            if not os.path.isdir(p) or os.path.islink(p):
                continue
            if os.path.isfile(os.path.join(p, "package.json")):
                yield name, p
                nested = os.path.join(p, "node_modules")
                if os.path.isdir(nested):
                    stack.append(nested)
            elif name.startswith("@"):
                stack.append(p)      # scope 目录（@scope/pkg 在下一层）


# ───────────────────────────────── 1) publish ─────────────────────────────────

def validate_backend_payload(app: str) -> dict:
    """只读检查托管后端自身运行库与 deps 闭包；不启动后端或访问用户配置。"""
    backend = os.path.join(app, "backend")
    required = BACKEND_KEY_FILES + tuple("backend/" + name for name in DOTNET_RUNTIME_MARKERS)
    missing = [rel for rel in required if not os.path.isfile(os.path.join(app, rel))]
    if missing:
        fail("发布输出缺少主动拦截/游戏监控后端载荷：%s" % ", ".join(missing))
    cfg = read_json(os.path.join(backend, "TubaWinUI3.BackEnd.runtimeconfig.json")).get("runtimeOptions") or {}
    included = cfg.get("includedFrameworks") or []
    if cfg.get("framework") or cfg.get("frameworks") or not any(
            f.get("name") == "Microsoft.NETCore.App" and f.get("version") for f in included):
        fail("后端必须独立自包含：runtimeconfig 缺少 includedFrameworks 或仍依赖系统 .NET")
    deps = read_json(os.path.join(backend, "TubaWinUI3.BackEnd.deps.json"))
    target = (deps.get("runtimeTarget") or {}).get("name", "")
    libraries = (deps.get("targets") or {}).get(target)
    if not target.endswith("/win-x64") or not libraries:
        fail("后端 deps.json 缺少 win-x64 依赖目标")
    assets = set()
    for library in libraries.values():
        for group in ("runtime", "native", "resources"):
            for asset in (library.get(group) or {}):
                if asset == "_._" or asset.endswith("/_._"):
                    continue
                parts = asset.replace("\\", "/").split("/")
                if any(part in ("", ".", "..") for part in parts) or ":" in asset:
                    fail("后端 deps 包含无效载荷路径：%s" % asset)
                if group == "resources":
                    locale = (library[group][asset] or {}).get("locale")
                    rel = "%s/%s" % (locale, parts[-1]) if locale else parts[-1]
                else:
                    rel = parts[-1]
                assets.add(rel)
    if not assets:
        fail("后端 deps 未列出实际托管/原生载荷")
    missing_assets = sorted(rel for rel in assets if not os.path.isfile(os.path.join(backend, rel)))
    if missing_assets:
        fail("后端依赖闭包不完整：%s" % ", ".join(missing_assets))
    return {
        "directory": "backend",
        "mode": "managed self-contained; isolated runtime directory",
        "runtimeTarget": target,
        "includedFrameworks": included,
        "dependencyAssetCount": len(assets),
        "dependencyAssets": sorted(assets),
        "executed": False,
    }


def backend_lock_records(lock_files: dict) -> dict:
    files = {rel: digest for rel, digest in lock_files.items() if rel.startswith("backend/")}
    return {"fileCount": len(files), "files": files,
            "keyFiles": key_records(lock_files, BACKEND_KEY_FILES),
            "note": "每个后端文件的实际 SHA256 同时属于 app-content-lock.json；未启动后端。"}


def publish_app(out: str, app: str) -> dict:
    """dotnet publish --self-contained true -> <out>/app（含 pri/xbf 注入）。返回发布布局指纹。"""
    if os.path.isdir(app):
        shutil.rmtree(app)
    cmd = [DOTNET, "publish", CSPROJ, "--self-contained", "true", "-r", "win-x64",
           "-c", "Release", "-p:Platform=x64", "-p:ExcludeToolsFromPublish=true", "-o", app]
    r, duration = run(cmd, "dotnet publish（self-contained） -> app/", timeout=PUBLISH_TIMEOUT)
    output = (r.stdout or "") + (r.stderr or "")
    warnings = [ln for ln in output.splitlines() if " warning " in ln]

    # ── fail-closed 门：pri / xbf / 自包含 .NET 标记 / runtimeconfig 形态 ──
    pri = os.path.join(app, "TubaWinUi3.pri")
    if not os.path.isfile(pri):
        fail("发布输出缺少 TubaWinUi3.pri —— 发布布局不完整（CompleteAppPublishLayout 未生效？）")
    xbf = []
    for dirpath, dirnames, filenames in os.walk(app):
        rel_dir = os.path.relpath(dirpath, app)
        parts = [] if rel_dir == "." else rel_dir.replace("\\", "/").split("/")
        if "runtime" in parts:
            dirnames[:] = []
            continue
        for f in filenames:
            if f.lower().endswith(".xbf"):
                xbf.append(f if rel_dir == "." else "%s/%s" % (rel_dir.replace("\\", "/"), f))
    xbf.sort()
    if not xbf:
        fail("发布输出里 0 个 .xbf —— XAML 编译产物未进发布清单（运行时 XAML 装载会失败）")
    missing_marks = [m for m in DOTNET_RUNTIME_MARKERS if not os.path.isfile(os.path.join(app, m))]
    if missing_marks:
        fail("发布输出不是自包含 .NET：缺少 %s（检查 --self-contained true / -r win-x64）" % ", ".join(missing_marks))

    rc_path = os.path.join(app, "TubaWinUi3.runtimeconfig.json")
    if not os.path.isfile(rc_path):
        fail("发布输出缺少 TubaWinUi3.runtimeconfig.json")
    rc_cfg = read_json(rc_path).get("runtimeOptions") or {}
    frameworks = rc_cfg.get("frameworks")
    included = rc_cfg.get("includedFrameworks")
    if frameworks or not included:
        fail("runtimeconfig.json 仍是框架依赖形态（frameworks=%s, includedFrameworks=%s）—— "
             "目标布局要求 includedFrameworks（.NET 随包携带）" % (frameworks, included))

    print("   [OK] 发布布局补齐：TubaWinUi3.pri %d B + %d 个 .xbf + .NET 自包含标记 %s"
          % (os.path.getsize(pri), len(xbf), ", ".join(DOTNET_RUNTIME_MARKERS)))
    print("   [OK] runtimeconfig：includedFrameworks=%s（无 frameworks 字段 → 不依赖目标机 .NET）"
          % ", ".join("%s %s" % (f.get("name"), f.get("version")) for f in included))
    backend = validate_backend_payload(app)
    print("   [OK] 后端独立自包含目录：%d 个 deps 载荷已逐项确认（未启动）"
          % backend["dependencyAssetCount"])

    # ── 内容锁：app/ 的每一个文件（runtime/ 单独锁）──
    t0 = time.time()
    lock_path = os.path.join(out, "app-content-lock.json")
    lock = content_lock(app, exclude_parts=("runtime",), lock_path=lock_path, label="app (excluding runtime/)")
    if lock["fileCount"] < 100:
        fail("app 内容锁只有 %d 个文件 —— 发布布局明显不完整" % lock["fileCount"])
    print("   [OK] app 内容锁：%d 文件 / %.1f MB / aggregate=%s（%.1fs）"
          % (lock["fileCount"], lock["sizeBytes"] / 1e6, lock["aggregateSha256"][:16], time.time() - t0))
    lock_files = read_json(lock_path)["files"]

    xbf_digests = {rel: lock_files[rel] for rel in xbf if rel in lock_files}
    return {
        "output": rel_or_abs(app, out),
        "command": shell_join(cmd),
        "argv": [str(c) for c in cmd],
        "selfContained": True,
        "rid": "win-x64",
        "configuration": "Release",
        "platform": "x64",
        "excludeToolsFromPublish": True,
        "durationSeconds": round(duration, 1),
        "warningLineCount": len(warnings),
        "contentLock": lock,
        "keyFiles": key_records(lock_files, APP_KEY_FILES),
        "xbfCount": len(xbf),
        "xbfAggregateSha256": sha256_bytes("\n".join("%s\t%s" % (k, v) for k, v in sorted(xbf_digests.items())).encode("utf-8")),
        "dotnetRuntime": key_records(lock_files, DOTNET_RUNTIME_MARKERS),
        "backend": {**backend, **backend_lock_records(lock_files)},
        "runtimeConfig": {
            "mode": "self-contained (includedFrameworks)",
            "frameworks": None,
            "includedFrameworks": ["%s %s" % (f.get("name"), f.get("version")) for f in included],
        },
    }


# ───────────────────────────────── 2) runtime ─────────────────────────────────

def runtime_manifest(out: str, app: str, node_exe: str, dsh_source: str, assembled: bool) -> dict:
    """组装（或复用）app/runtime，并对本次真实文件做依赖清单 + 全量内容锁。"""
    runtime_dir = os.path.join(app, "runtime")
    if assembled:
        run([POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             os.path.join(REPO, "scripts", "prepare-dsh-runtime.ps1"),
             "-NodeExe", node_exe, "-DshSource", dsh_source, "-RuntimeDir", runtime_dir],
            "assemble app-private dsh runtime -> app/runtime", timeout=RUNTIME_TIMEOUT)
    else:
        if not os.path.isdir(runtime_dir):
            fail("--skip-runtime 但 %s 不存在：无法复用未组装的 runtime" % runtime_dir)
        print("== 复用既有 app/runtime（--skip-runtime）")

    # ── fail-closed 门 ──
    node = os.path.join(runtime_dir, "node.exe")
    binjs = os.path.join(runtime_dir, "dsh", "lib", "bin.js")
    vjson = os.path.join(runtime_dir, "versions.json")
    for p, what in ((node, "runtime\\node.exe"), (binjs, "runtime\\dsh\\lib\\bin.js"),
                    (vjson, "runtime\\versions.json")):
        if not os.path.isfile(p):
            fail("runtime 组装后仍缺 %s（%s）" % (what, p))

    versions = read_json(vjson)
    code, node_out = capture([node, "--version"], timeout=60)
    node_ver = node_out.strip().splitlines()[-1].strip() if code == 0 and node_out.strip() else ""
    dsh_pkg = read_json(os.path.join(runtime_dir, "dsh", "package.json"))
    dsh_ver = str(dsh_pkg.get("version") or "")
    lock = {
        "failClosed": bool(versions.get("failClosed")),
        "node": versions.get("node"),
        "dsh": versions.get("dsh"),
        "lockedNode": versions.get("lockedNode"),
        "lockedDsh": versions.get("lockedDsh"),
    }
    bad = []
    if not lock["failClosed"]:
        bad.append("versions.json 未声明 failClosed=true")
    if not node_ver.startswith("v"):
        bad.append("runtime\\node.exe 无法执行（--version 输出 %r）" % node_out[:200])
    elif lock["node"] != node_ver:
        bad.append("versions.json.node=%r ≠ 实物 node %r" % (lock["node"], node_ver))
    if lock["lockedNode"] != node_ver:
        bad.append("锁定值和实测 node 不一致（locked=%r, actual=%r）" % (lock["lockedNode"], node_ver))
    if dsh_ver != lock["dsh"]:
        bad.append("dsh/package.json version=%r ≠ versions.json.dsh=%r" % (dsh_ver, lock["dsh"]))
    if lock["lockedDsh"] != dsh_ver:
        bad.append("锁定值和实物 dsh 不一致（locked=%r, actual=%r）" % (lock["lockedDsh"], dsh_ver))
    if bad:
        fail("runtime 版本清单与实物不一致：" + "；".join(bad))

    # ── 内容锁：app/runtime 的每一个文件（node.exe + versions.json + dsh/**）──
    t0 = time.time()
    lock_path = os.path.join(out, "runtime-content-lock.json")
    rt_lock = content_lock(runtime_dir, lock_path=lock_path, label="app/runtime (node.exe + versions.json + dsh/**)")
    rt_files = read_json(lock_path)["files"]
    print("   [OK] runtime 内容锁：%d 文件 / %.1f MB / aggregate=%s（%.1fs）"
          % (rt_lock["fileCount"], rt_lock["sizeBytes"] / 1e6, rt_lock["aggregateSha256"][:16], time.time() - t0))

    # ── 完整依赖清单：全量 node_modules（顶层 + 嵌套） + dsh 声明依赖逐项解析 ──
    dsh_root = os.path.join(runtime_dir, "dsh")
    installed = []
    for name, pkgdir in iter_installed_packages(os.path.join(dsh_root, "node_modules")):
        pj = os.path.join(pkgdir, "package.json")
        depth = os.path.relpath(pkgdir, dsh_root).replace("\\", "/").count("/node_modules/")
        rec = {"path": rel_or_abs(pkgdir, dsh_root), "depth": depth,
               "name": None, "version": None, "packageJsonSha256": None}
        if os.path.isfile(pj):
            try:
                meta = read_json(pj)
                rec["name"] = meta.get("name")
                rec["version"] = meta.get("version")
            except Exception:
                rec["name"] = "<unreadable package.json>"
            rec["packageJsonSha256"] = sha256_file(pj)
        installed.append(rec)
    installed.sort(key=lambda r: (r["path"], r["name"] or ""))
    by_name: dict = {}
    for rec in installed:
        if rec["name"]:
            by_name.setdefault(rec["name"], []).append(rec)

    declared = {}
    for field in ("dependencies", "optionalDependencies", "peerDependencies"):
        for k, v in (dsh_pkg.get(field) or {}).items():
            declared.setdefault(k, {"spec": v, "field": field})
    resolved = {}
    missing_required = []
    for name, info in sorted(declared.items()):
        copies = [{"path": c["path"], "version": c["version"]} for c in by_name.get(name, [])]
        resolved[name] = {**info, "installedCopies": copies, "present": bool(copies)}
        if not copies and info["field"] == "dependencies":
            missing_required.append(name)
    if missing_required:
        fail("dsh 声明的 dependencies 有 %d 项在 runtime 内找不到（依赖清单不完整）：%s"
             % (len(missing_required), ", ".join(missing_required[:10])))

    deps_doc = {
        "schema": "zxai-dsh-dependencies/1",
        "generatedAt": now(),
        "runtimeDir": rel_or_abs(runtime_dir, out),
        "node": {"version": node_ver, "path": "node.exe", "sha256": rt_files.get("node.exe")},
        "dsh": {"version": dsh_ver, "packageJsonSha256": rt_files.get("dsh/package.json"),
                "binJs": "dsh/lib/bin.js", "binJsSha256": rt_files.get("dsh/lib/bin.js")},
        "anchor": {"package": "@deepseek-ai/dsh", "version": dsh_ver, "path": "dsh",
                   "note": "INSTALL_ANCHOR：dsh 自身即 <runtime>/dsh（不在自己的 node_modules 内）"},
        "coverage": {
            "installed": "runtime/dsh/node_modules 全量（顶层 + 嵌套 node_modules，逐包一条记录）",
            "declared": "dsh/package.json 的 dependencies + optionalDependencies + peerDependencies 逐项解析到实际安装副本",
            "notCovered": "semver 范围满足性求解；devDependencies（不随发布携带）",
        },
        "installedCount": len(installed),
        "installedTopLevelCount": sum(1 for r in installed if r["depth"] == 0),
        "installedNestedCount": sum(1 for r in installed if r["depth"] > 0),
        "duplicateNames": {k: [c["version"] for c in v] for k, v in sorted(by_name.items()) if len(v) > 1},
        "installed": installed,
        "declaredCount": len(declared),
        "declaredResolved": resolved,
    }
    deps_path = os.path.join(out, "runtime-dependencies.json")
    deps_sha = write_json(deps_path, deps_doc)
    print("   [OK] 依赖清单：installed %d（顶层 %d / 嵌套 %d，重名 %d）；dsh 声明 %d 项全部有实物"
          % (len(installed), deps_doc["installedTopLevelCount"], deps_doc["installedNestedCount"],
             len(deps_doc["duplicateNames"]), len(declared)))

    critical_versions = {}
    for k in ("@deepseek-ai/dsh", "@deepseek-ai/dsh-acp", "@agentclientprotocol/sdk"):
        v = next((c["version"] for c in by_name.get(k, [])), None)
        if v is None and k == "@deepseek-ai/dsh":
            v = dsh_ver          # INSTALL_ANCHOR：dsh 自身即 <runtime>/dsh，不在自己的 node_modules 内
        critical_versions[k] = v

    return {
        "path": rel_or_abs(runtime_dir, out),
        "assembledThisRun": assembled,
        "node": {"version": node_ver, "sha256": rt_files.get("node.exe"),
                 "bytes": os.path.getsize(node)},
        "dsh": {"version": dsh_ver, "binJsSha256": rt_files.get("dsh/lib/bin.js"),
                "binJsBytes": os.path.getsize(binjs),
                "packageJsonSha256": rt_files.get("dsh/package.json")},
        "versionsJson": versions,
        "versionLockCheck": {"failClosed": lock["failClosed"], "nodeMatches": lock["node"] == node_ver,
                             "dshMatches": lock["dsh"] == dsh_ver, "lockedNode": lock["lockedNode"],
                             "lockedDsh": lock["lockedDsh"]},
        "keyFiles": key_records(rt_files, RUNTIME_KEY_FILES),
        "contentLock": rt_lock,
        "dependencies": {
            "inventoryPath": rel_or_abs(deps_path, out),
            "inventorySha256": deps_sha,
            "installedCount": len(installed),
            "installedTopLevelCount": deps_doc["installedTopLevelCount"],
            "installedNestedCount": deps_doc["installedNestedCount"],
            "declaredCount": len(declared),
            "declaredPresentCount": sum(1 for r in resolved.values() if r["present"]),
            "duplicateNameCount": len(deps_doc["duplicateNames"]),
            "critical": critical_versions,
            "anchor": {"package": "@deepseek-ai/dsh", "version": dsh_ver, "path": "dsh",
                       "note": "INSTALL_ANCHOR：dsh 自身即 <runtime>/dsh（不在自己的 node_modules 内）"},
        },
        "sources": {"nodeExe": node_exe if assembled else None, "dshSource": dsh_source if assembled else None},
    }


# ────────────────────────────────── 3) smoke ──────────────────────────────────

def run_smoke(out: str, app: str) -> dict:
    """在该目录内跑离线 ACP 烟测（无模型请求）；日志 + JSON 报告都留在 <out>。"""
    runtime_dir = os.path.join(app, "runtime")
    log_path = os.path.join(out, "verify-runtime-smoke.log")
    report_path = os.path.join(out, "smoke-report.json")
    if os.path.isfile(report_path):
        os.remove(report_path)   # 绝不把上一轮的 report 当本轮证据
    cmd = [sys.executable, os.path.join(REPO, "scripts", "verify-dsh-runtime.py"),
           runtime_dir, "--report", report_path]
    r, duration = run(cmd, "offline ACP smoke（无模型请求）", timeout=SMOKE_TIMEOUT, log_path=log_path)
    if not os.path.isfile(report_path):
        fail("烟测退出码为 0 但未产出报告 %s（脚本与本打包器版本不匹配？）" % report_path)
    report = read_json(report_path)
    if not report.get("ok"):
        fail("烟测报告 ok=false：%s" % report.get("failure"))
    rec = {
        "ran": True,
        "ok": True,
        "exitCode": r.returncode,
        "durationSeconds": round(duration, 1),
        "script": "scripts/verify-dsh-runtime.py",
        "command": shell_join(cmd),
        "runtimeDir": rel_or_abs(runtime_dir, out),
        "logPath": rel_or_abs(log_path, out),
        "logAbsPath": log_path,
        "logBytes": os.path.getsize(log_path),
        "logSha256": sha256_file(log_path),
        "reportPath": rel_or_abs(report_path, out),
        "reportAbsPath": report_path,
        "reportSha256": sha256_file(report_path),
        "node": report.get("node"),
        "dsh": report.get("dsh"),
        "stepCount": report.get("stepCount") or len(report.get("steps") or []),
        "steps": report.get("steps"),
        "closure": report.get("closure"),
        "processes": report.get("processes"),
        "tempDirsRemoved": report.get("tempDirsRemoved"),
        "runtimeClosureEntriesBefore": report.get("runtimeClosureEntriesBefore"),
        "runtimeClosureEntriesAfter": report.get("runtimeClosureEntriesAfter"),
        "telemetryDisabled": report.get("telemetryDisabled"),
        "promptSent": report.get("promptSent"),
    }
    print("   [OK] 烟测报告：%s（%d 步；闭包 %s 包）"
          % (report_path, rec["stepCount"], (rec["closure"] or {}).get("entryCount", "?")))
    return rec


# ─────────────────── 4) source fingerprint (content, before/after) ───────────────────

def source_fingerprint(out_dir: str) -> dict:
    """源码内容指纹（不是状态指纹）。

    * tracked：`git diff HEAD` 的**原始补丁字节**做 sha256 —— 等长改动同样改变它；
    * untracked：`git ls-files --others --exclude-standard` 逐个文件算 sha256（内容哈希），
      并给聚合值 —— 「路径相同、内容不同」必然改变；
    * 未被 tracked diff 覆盖的未修改文件由 HEAD 的 blob SHA 内容寻址覆盖，因此
      head + trackedDiff.patchSha256 + untracked.contentSha256 构成源码内容锁。
    * 排除：本次输出目录（<out>，git 已 ignore artifacts/，这里再显式排除一次）。
    """
    fp = {"capturedAt": now(), "repo": REPO}
    code, head = capture(["git", "rev-parse", "HEAD"], timeout=30)
    fp["head"] = head.strip() if code == 0 else None
    code, branch = capture(["git", "rev-parse", "--abbrev-ref", "HEAD"], timeout=30)
    fp["branch"] = branch.strip() if code == 0 else None

    code, patch = capture(["git", "diff", "HEAD"], timeout=600, raw=True)
    fp["trackedDiff"] = {
        "exitCode": code,
        "patchBytes": len(patch),
        "lineCount": patch.count(b"\n"),
        "patchSha256": sha256_bytes(patch),
        "note": "sha256 of the raw `git diff HEAD` patch text (content, not status)",
    }
    code, ns = capture(["git", "diff", "HEAD", "--name-status"], timeout=300)
    ns_lines = [ln for ln in (ns or "").splitlines() if ln.strip()] if code == 0 else []
    fp["trackedDiff"]["changedFileCount"] = len(ns_lines)
    fp["trackedDiff"]["nameStatusSha256"] = sha256_bytes("\n".join(ns_lines).encode("utf-8"))
    fp["trackedDiff"]["changedFiles"] = ns_lines

    code, untracked_raw = capture(["git", "ls-files", "--others", "--exclude-standard", "-z"], timeout=300)
    paths = [p for p in (untracked_raw or "").split("\0") if p.strip()] if code == 0 else []
    out_abs = os.path.abspath(out_dir)
    entries = []
    excluded = []
    h = hashlib.sha256()
    total = 0
    for rel in sorted(set(paths)):
        p = os.path.abspath(os.path.join(REPO, rel))
        if p == out_abs or p.startswith(out_abs + os.sep):
            excluded.append(rel.replace("\\", "/"))
            continue
        if not os.path.isfile(p):
            continue   # 目录/断链：文件由 ls-files 逐个给出
        digest = sha256_file(p)
        size = os.path.getsize(p)
        entries.append({"path": rel.replace("\\", "/"), "bytes": size, "sha256": digest})
        total += size
        h.update(("%s\t%d\t%s\n" % (rel.replace("\\", "/"), size, digest)).encode("utf-8"))
    fp["untracked"] = {
        "fileCount": len(entries),
        "totalBytes": total,
        "contentSha256": h.hexdigest(),
        "excludedOutputDirEntries": excluded,
        "entries": entries,
        "note": "sha256 of every untracked, non-ignored file (content hash per file)",
    }

    code, porcelain = capture(["git", "status", "--porcelain"], timeout=300)
    lines = [ln for ln in (porcelain or "").splitlines() if ln.strip()] if code == 0 else []
    fp["porcelain"] = {"entryCount": len(lines),
                       "untrackedCount": sum(1 for ln in lines if ln.startswith("??")),
                       "sha256": sha256_bytes("\n".join(lines).encode("utf-8")),
                       "entries": lines[:80]}
    code, shortstat = capture(["git", "diff", "HEAD", "--shortstat"], timeout=300)
    fp["diffShortStat"] = shortstat.strip() if code == 0 else None
    fp["dirty"] = bool(lines)
    fp["contentDigest"] = sha256_bytes(json.dumps({
        "head": fp["head"],
        "trackedDiffPatch": fp["trackedDiff"]["patchSha256"],
        "untrackedContent": fp["untracked"]["contentSha256"],
    }, sort_keys=True).encode("utf-8"))
    return fp


def compact_fingerprint(fp: dict) -> dict:
    """放进 BUILD-INFO 的紧凑形态（完整逐文件明细在 source-fingerprint.json）。"""
    return {
        "capturedAt": fp.get("capturedAt"),
        "head": fp.get("head"),
        "branch": fp.get("branch"),
        "contentDigest": fp.get("contentDigest"),
        "trackedDiff": {k: v for k, v in fp["trackedDiff"].items() if k not in ("changedFiles",)},
        "untracked": {k: v for k, v in fp["untracked"].items()
                      if k not in ("entries", "excludedOutputDirEntries")},
        "porcelain": {k: v for k, v in fp["porcelain"].items() if k != "entries"},
        "diffShortStat": fp.get("diffShortStat"),
        "dirty": fp.get("dirty"),
    }


def compare_fingerprints(before: dict, after: dict) -> list:
    """逐部件比较，返回变化部件名（空 = 稳定）。"""
    changed = []
    for key in ("head", "contentDigest", "diffShortStat"):
        if before.get(key) != after.get(key):
            changed.append(key)
    for part, keys in (("trackedDiff", ("patchSha256", "patchBytes", "changedFileCount")),
                       ("untracked", ("fileCount", "totalBytes", "contentSha256")),
                       ("porcelain", ("entryCount", "sha256"))):
        for k in keys:
            if before.get(part, {}).get(k) != after.get(part, {}).get(k):
                changed.append("%s.%s" % (part, k))
    return sorted(set(changed))


# ─────────────────────────────────── main ────────────────────────────────────

def main() -> int:
    global VERBOSE
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--node-exe", default=DEFAULT_NODE)
    ap.add_argument("--dsh-source", default=DEFAULT_DSH)
    ap.add_argument("--skip-publish", action="store_true", help="reuse an existing <out>/app (no rebuild)")
    ap.add_argument("--skip-runtime", action="store_true", help="do not reassemble <out>/app/runtime")
    ap.add_argument("--skip-smoke", action="store_true", help="do not run the offline ACP smoke test")
    ap.add_argument("--verbose", action="store_true", help="echo full captured output of each step")
    args = ap.parse_args()
    VERBOSE = args.verbose

    out = os.path.abspath(args.out)
    app = os.path.join(out, "app")
    os.makedirs(out, exist_ok=True)

    stats = {"skipped": [], "runs": []}
    if not args.skip_publish and args.skip_runtime:
        fail("--skip-runtime 与重新发布互斥：publish 会重建 app/，runtime 必须重新组装（fail-closed）")
    if args.skip_publish and not os.path.isdir(app):
        fail("--skip-publish 但 %s 不存在：没有可复用的发布布局" % app)

    started = now()
    t0 = time.time()

    # ── 源码内容指纹（构建前）──
    fp_before = source_fingerprint(out)
    print("== 源码指纹（构建前）：HEAD=%s tracked patch=%s untracked=%d 文件/%s…"
          % ((fp_before["head"] or "?")[:12], fp_before["trackedDiff"]["patchSha256"][:12],
             fp_before["untracked"]["fileCount"], fp_before["untracked"]["contentSha256"][:12]))

    # ── 1) publish ──
    if args.skip_publish:
        stats["skipped"].append("publish")
        print("== 跳过 publish（--skip-publish）；复用 %s" % app)
        publish = {"output": "app", "skipped": True,
                   "contentLock": content_lock(app, exclude_parts=("runtime",),
                                               lock_path=os.path.join(out, "app-content-lock.json"),
                                               label="app (excluding runtime/)")}
        publish["backend"] = {**validate_backend_payload(app), **backend_lock_records(
            read_json(os.path.join(out, "app-content-lock.json"))["files"])}
    else:
        publish = publish_app(out, app)
        stats["runs"].append("publish")

    # ── 2) runtime ──
    if args.skip_runtime:
        stats["skipped"].append("runtime")
    else:
        stats["runs"].append("runtime")
    runtime = runtime_manifest(out, app, os.path.abspath(args.node_exe),
                               os.path.abspath(args.dsh_source), assembled=not args.skip_runtime)

    # ── 3) smoke ──
    if args.skip_smoke:
        stats["skipped"].append("smoke")
        print("== 跳过离线烟测（--skip-smoke）")
        smoke = {"ran": False, "ok": None, "reason": "--skip-smoke"}
    else:
        smoke = run_smoke(out, app)
        stats["runs"].append("smoke")

    # ── 4) BUILD-INFO.json ──
    fp_after = source_fingerprint(out)
    changed_parts = compare_fingerprints(fp_before, fp_after)
    fp_doc = {"schema": "zxai-source-fingerprint/1", "generatedAt": now(),
              "before": fp_before, "after": fp_after,
              "stable": not changed_parts, "changedParts": changed_parts,
              "note": "内容指纹（非状态指纹）：tracked = git diff HEAD 补丁文本 sha256；"
                      "untracked = 逐文件内容 sha256；构建前后各采集一次并比较，"
                      "不一致说明产物与所记录的源码修订不对应。"}
    fp_path = os.path.join(out, "source-fingerprint.json")
    fp_sha = write_json(fp_path, fp_doc)

    ok, sdk = capture([DOTNET, "--version"], timeout=120)
    toolchain = {
        "dotnetPath": DOTNET,
        "dotnetSdk": sdk.strip() if ok == 0 else None,
        "python": sys.executable,
        "pythonVersion": sys.version.split()[0],
        "powershell": POWERSHELL,
        "nodeSource": os.path.abspath(args.node_exe) if not args.skip_runtime else None,
        "dshSource": os.path.abspath(args.dsh_source) if not args.skip_runtime else None,
    }
    complete = (not args.skip_publish) and (not args.skip_runtime) and (not args.skip_smoke)
    manifest = {
        "schema": "zxai-staging/2",
        "builtAt": started,
        "finishedAt": now(),
        "durationSeconds": round(time.time() - t0, 1),
        "complete": complete and not changed_parts,
        "layout": {
            "kind": "self-contained single directory（PRI + XBF + .NET + Node + dsh 同目录）",
            "outDir": out,
            "appDir": "app",
            "backendDir": "app/backend",
            "dshRuntimeDir": "app/runtime",
            "manifestPaths": ["BUILD-INFO.json", "app/BUILD-INFO.json"],
            "note": "app/ 由 dotnet publish --self-contained true 产出（csproj CompleteAppPublishLayout 注入 "
                    "pri/xbf/AssistStudio 载荷及 backend/ 自包含后端）；app/runtime/ 由 prepare-dsh-runtime.ps1 组装；烟测就在该目录内跑。",
        },
        "steps": {"ran": stats["runs"], "skipped": stats["skipped"]},
        "toolchain": toolchain,
        "publish": publish,
        "dshRuntimeIncluded": os.path.isfile(os.path.join(app, "runtime", "node.exe")),
        "runtime": runtime,
        "smoke": smoke,
        "sourceFingerprint": {
            "before": compact_fingerprint(fp_before),
            "after": compact_fingerprint(fp_after),
            "stable": not changed_parts,
            "changedParts": changed_parts,
            "sidecar": {"path": rel_or_abs(fp_path, out), "sha256": fp_sha,
                        "schema": "zxai-source-fingerprint/1",
                        "note": "完整逐文件明细（untracked 每个文件的 sha256、tracked diff 文件清单）在此"},
        },
        "boundaries": {
            "contentLockCovers": [
                "app/ 的每一个文件（runtime/ 另有一把锁）——逐文件 sha256 + 聚合值",
                "app/runtime 的每一个文件：node.exe、versions.json、dsh/ 全量文件",
            ],
            "contentLockDoesNotCover": [
                "NuGet/MSBuild/.NET SDK 工具链自身（只锁它们复制进发布目录的产物）",
                "签名证书与安装器（.iss）打包产物（不属本 staging 目录）",
            ],
            "dependencyInventoryCovers": [
                "runtime/dsh/node_modules 全量：顶层 + 嵌套 node_modules，逐包 name/version/package.json sha256",
                "dsh/package.json 的 dependencies/optionalDependencies/peerDependencies 逐项解析到实际安装副本",
            ],
            "dependencyInventoryDoesNotCover": [
                "semver 范围满足性求解（只记录 spec 与实际安装版本；range 是否满足不在此断言）",
                "devDependencies（不随发布携带）",
                "profile 组合在 DSH_HOME 初始化时才生成 —— 烟测只断言其解析结果（闭包）",
            ],
        },
        "hashNote": "所有 sha256 均由本次运行对 <out> 内的真实文件计算（app/ 与 app/runtime/），无一项来自历史清单；"
                    "app-content-lock.json / runtime-content-lock.json 含逐文件哈希，聚合算法见其 algorithm 字段。",
    }
    if not manifest["dshRuntimeIncluded"]:
        fail("最终 app 目录内没有 runtime\\node.exe —— 不是「自包含单目录」布局")
    paths = [os.path.join(out, "BUILD-INFO.json"), os.path.join(app, "BUILD-INFO.json")]
    for target in paths:
        write_json(target, manifest)

    exe = next((k["sha256"] for k in publish.get("keyFiles", []) if k["path"] == "TubaWinUi3.exe"), None)
    pri = next((k["sha256"] for k in publish.get("keyFiles", []) if k["path"] == "TubaWinUi3.pri"), None)
    print("== BUILD-INFO.json 已写入（同一份描述自包含单目录）:")
    for p in paths:
        print("     %s" % p)
    print("== 关键指纹：exe=%s pri=%s node=%s dsh-bin.js=%s smoke=%s complete=%s"
          % ((exe or "n/a")[:12], (pri or "n/a")[:12],
             (runtime["node"]["sha256"] or "n/a")[:12], (runtime["dsh"]["binJsSha256"] or "n/a")[:12],
             smoke["ran"], manifest["complete"]))
    print("== 源码指纹：stable=%s%s" % (not changed_parts, "" if not changed_parts else "（变化：%s）" % ",".join(changed_parts)))
    print("==== staging 构建完成（%s） ====" % ("全部步骤本次真实执行" if complete else
                                             "部分步骤被跳过：" + ",".join(stats["skipped"])))
    if changed_parts:
        fail("构建前后源码内容指纹不一致（%s）：产物与所记录的源码修订不对应 —— BUILD-INFO.json 已写出供诊断，"
             "本次运行不算通过" % ", ".join(changed_parts))
    return 0


if __name__ == "__main__":
    sys.exit(main())
