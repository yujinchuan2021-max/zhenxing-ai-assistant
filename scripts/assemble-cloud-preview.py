#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Assemble a fresh cloud-tools preview after make-staging.py succeeds.

This script does not publish, launch, download, install or create a ZIP. It only
renames the candidate's app/ to src/, copies release root documents/launcher,
and recalculates/verifies the content locks. No source Tools/ tree is merged.
The resulting layout is compatible with cand6-release.py's separate zip step.

Usage:
  python scripts/assemble-cloud-preview.py --candidate D:\\AI2\\TubaWinUi3CE-cand6-cloud-tools-preview-YYYYMMDD --version 0.1.0
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys
import time
from types import SimpleNamespace


AI2_ROOT = Path(r"D:\AI2")
SOURCE_ROOT = AI2_ROOT / "tubatools"
CANDIDATE_PREFIX = "TubaWinUi3CE-cand6-cloud-tools-preview-"
CANDIDATE_PREFIXES = (CANDIDATE_PREFIX, "TubaWinUi3CE-cand6-app-center-local-only-preview-",
                      "TubaWinUi3CE-cand6-workflow-auto-preview-", "TubaWinUi3CE-cand6-workbench-preview-")
LAUNCHER_NAME = "枕星图吧AI助手.exe"
REPARSE_ATTRIBUTE = 0x400
ROOT_DOCUMENTS = ("License.txt", "PrivacyPolicy.txt", "PrivacyPolicy.html")
STAGING_LOCK_NAMES = ("app-content-lock.json", "runtime-content-lock.json")


def sha256(path: Path) -> str:
    check_ancestors(path)
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def check_ancestors(path: Path) -> None:
    """Do not resolve symlinks first: that would erase evidence of a redirected path."""
    for component in (path, *path.parents):
        try:
            info = os.lstat(component)
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & REPARSE_ATTRIBUTE:
            raise ValueError("路径含符号链接或重解析点：" + str(component))


def safe_tree(root: Path):
    check_ancestors(root)
    if not root.is_dir():
        raise ValueError("不是普通目录：" + str(root))

    def on_error(error):
        raise error

    for directory, dirs, files in os.walk(root, followlinks=False, onerror=on_error):
        for name in dirs + files:
            path = Path(directory) / name
            check_ancestors(path)
            info = os.lstat(path)
            if not (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)):
                raise ValueError("目录内含特殊文件：" + str(path))
        for name in files:
            path = Path(directory) / name
            yield path.relative_to(root).as_posix(), path


def absolute_path(value: str) -> Path:
    if not os.path.isabs(value):
        raise ValueError("必须提供绝对路径：" + value)
    path = Path(os.path.abspath(value))
    check_ancestors(path)
    return path


def same_path(left: Path, right: Path) -> bool:
    return os.path.normcase(os.path.abspath(left)) == os.path.normcase(os.path.abspath(right))


def candidate_path(value: str) -> Path:
    path = absolute_path(value)
    prefix = next((p for p in CANDIDATE_PREFIXES if path.name.casefold().startswith(p.casefold())), None)
    if (not same_path(path.parent, AI2_ROOT)
            or prefix is None
            or not re.fullmatch(r"[A-Za-z0-9-]+", path.name)
            or len(path.name) <= len(prefix)
            or not path.is_dir()):
        raise ValueError("candidate 必须是 D:\\AI2 下已存在的指定预览版专用直接子目录")
    return path


def require_absent(path: Path) -> None:
    check_ancestors(path)
    if os.path.lexists(path):
        raise FileExistsError("拒绝覆盖已有输出：" + str(path))


def read_json(path: Path) -> dict:
    check_ancestors(path)
    with path.open("r", encoding="utf-8-sig") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise ValueError("JSON 根不是对象：" + str(path))
    return value


def write_new_json(path: Path, value: dict) -> None:
    require_absent(path)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def copy_new_file(source: Path, destination: Path) -> str:
    check_ancestors(source)
    require_absent(destination)
    if not source.is_file():
        raise FileNotFoundError(source)
    before = sha256(source)
    with source.open("rb") as input_stream, destination.open("xb") as output_stream:
        shutil.copyfileobj(input_stream, output_stream, length=1 << 20)
    shutil.copystat(source, destination, follow_symlinks=False)
    if sha256(destination) != before or sha256(source) != before:
        raise ValueError("复制时源文件变化或内容不一致：" + str(source))
    return before


def load_script(path: Path, name: str):
    check_ancestors(path)
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ValueError("不能读取本地脚本：" + str(path))
    module = importlib.util.module_from_spec(spec)
    previous = sys.dont_write_bytecode
    try:
        sys.dont_write_bytecode = True
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = previous
    return module


def validate_old_lock(directory: Path, lock: dict, verifier, *, app: bool) -> None:
    if lock.get("schema") != "zxai-content-lock/1" or not isinstance(lock.get("files"), dict):
        raise ValueError("make-staging 内容锁格式无效")
    entries, _, reparse = verifier.independent_scan(str(directory), ("runtime",) if app else ())
    if reparse:
        raise ValueError("内容锁目录含重解析点")
    expected = lock["files"]
    # make-staging writes this exact duplicate AFTER its app content lock.
    permitted_extra = {"BUILD-INFO.json"} if app else set()
    missing = set(expected) - set(entries)
    extra = set(entries) - set(expected) - permitted_extra
    changed = [rel for rel in set(expected) & set(entries) if expected[rel] != entries[rel][1]]
    locked_entries = {rel: entries[rel] for rel in expected if rel in entries}
    if (missing or extra or changed or len(locked_entries) != lock.get("fileCount")
            or sum(size for size, _ in locked_entries.values()) != lock.get("sizeBytes")
            or verifier.independent_aggregate(locked_entries) != lock.get("aggregateSha256")):
        raise ValueError("staging 内容已变化：missing=%d extra=%d changed=%d" %
                         (len(missing), len(extra), len(changed)))


def startup_guide(version: str, workbench: bool = False) -> str:
    edition = "任务工作台预览版" if workbench else "云端工具预览版"
    workbench_note = ("选定方案后进入任务工作台：宽窗口左侧显示目标与当前步骤，右侧保留助手对话。\n"
                      "小窗口通过「任务工作台 / 与助手沟通」切换；草稿、记录和准备进度保留。\n\n") if workbench else ""
    return f"""枕星图吧AI助手 · {edition} v{version}（Windows 64 位）

完整解压后，双击根目录的「{LAUNCHER_NAME}」。
也可以直接打开 src\\TubaWinUi3.exe；请保留 src 文件夹完整。
需要 Windows 10 2004（19041）及以上或 Windows 11；本包自带运行环境。

系统内置功能随客户端提供。新工具请在「图吧工具」按需下载；不能自动下载的条目，有来源入口时可打开网页，未配置入口时暂不可用。
应用中心只管理本机已有工具和已发起的下载任务，可查看进度、更新和打开工具，查看安装位置或创建桌面快捷方式。
由本应用下载管理的便携工具，联网后接收云端目录推送并自动更新；工具使用中会延后更新。
自动更新保留新增配置与存档；路径冲突时保留旧版本，完整旧目录备份也会保留。
已经存在的手动安装或旧随包工具继续复用，不会被自动覆盖或移除。
客户端代码和界面的更新仍通过单独的新版本包提供。

{workbench_note}确认整份方案后，会连续处理全部支持自动安装的项目，并复用已安装工具。
图形软件就绪后自动创建桌面图标；命令行工具显示终端使用指引，可用时提供启动命令。
注册、登录、订阅或付费，以及模型密钥配置，需要你本人完成。

AI 对话使用你自行配置的模型服务。隐私说明见 PrivacyPolicy.txt / PrivacyPolicy.html。
"""


def assemble(args) -> None:
    candidate = candidate_path(args.candidate)
    repo = absolute_path(args.repo)
    if not same_path(repo, SOURCE_ROOT) or not repo.is_dir():
        raise ValueError("repo 必须是 D:\\AI2\\tubatools 源码目录")
    if not re.fullmatch(r"\d+(?:\.\d+){1,3}", args.version):
        raise ValueError("version 必须是两至四段数字版本号")
    app, src = candidate / "app", candidate / "src"
    require_absent(src)
    if not app.is_dir():
        raise ValueError("候选目录缺少 make-staging 产出的 app/")
    # Validate the whole fresh candidate before any rename or write.
    list(safe_tree(candidate))
    launcher_source = repo / "Launcher" / "bin" / "图吧工具箱WinUI3_x64.exe"
    documents = [repo / name for name in ROOT_DOCUMENTS]
    if os.path.lexists(repo / "LICENSE"):
        documents.append(repo / "LICENSE")
    staging_script = repo / "scripts" / "make-staging.py"
    verifier_script = repo / "scripts" / "verify-content-lock.py"
    for source in (launcher_source, *documents, staging_script, verifier_script):
        check_ancestors(source)
        if not source.is_file():
            raise FileNotFoundError(source)
    new_outputs = [candidate / LAUNCHER_NAME, candidate / "启动说明.txt",
                   candidate / "tools-merge-manifest.json", candidate / "candidate-build-info.json",
                   *(candidate / source.name for source in documents),
                   *(candidate / ("staging-" + name) for name in STAGING_LOCK_NAMES)]
    for output in new_outputs:
        require_absent(output)

    build = read_json(candidate / "BUILD-INFO.json")
    fingerprint = read_json(candidate / "source-fingerprint.json")
    if (build.get("schema") != "zxai-staging/2" or build.get("complete") is not True
            or build.get("smoke", {}).get("ok") is not True
            or fingerprint.get("stable") is not True
            or build.get("publish", {}).get("excludeToolsFromPublish") is not True
            or build.get("publish", {}).get("rid") != "win-x64"):
        raise ValueError("只接受成功的 x64 完整 staging，且 publish 必须排除全量 Tools")
    sidecar_sha = build.get("sourceFingerprint", {}).get("sidecar", {}).get("sha256")
    if sidecar_sha != sha256(candidate / "source-fingerprint.json"):
        raise ValueError("源码指纹与 staging 记录不一致")
    if sha256(app / "BUILD-INFO.json") != sha256(candidate / "BUILD-INFO.json"):
        raise ValueError("app/BUILD-INFO.json 与 staging 根清单不一致")
    staging = load_script(staging_script, "zxai_cloud_preview_staging")
    verifier = load_script(verifier_script, "zxai_cloud_preview_verifier")
    for relative in (*staging.APP_KEY_FILES, *staging.DOTNET_RUNTIME_MARKERS,
                     "Metadata/cloud-tools.json", *("runtime/" + p for p in staging.RUNTIME_KEY_FILES)):
        path = app.joinpath(*relative.split("/"))
        check_ancestors(path)
        if not path.is_file():
            raise FileNotFoundError(path)
    app_old = read_json(candidate / STAGING_LOCK_NAMES[0])
    runtime_old = read_json(candidate / STAGING_LOCK_NAMES[1])
    validate_old_lock(app, app_old, verifier, app=True)
    validate_old_lock(app / "runtime", runtime_old, verifier, app=False)

    # No Tools source is read or merged: preserve only bytes already in the excluded-tools publish.
    old_tools = {rel[6:]: digest for rel, digest in app_old["files"].items() if rel.startswith("Tools/")}
    for name in STAGING_LOCK_NAMES:
        copy_new_file(candidate / name, candidate / ("staging-" + name))
    check_ancestors(app)
    require_absent(src)
    app.rename(src)
    tools = src / "Tools"
    actual_tools = dict(safe_tree(tools)) if tools.is_dir() else {}
    if set(actual_tools) != set(old_tools) or any(sha256(path) != old_tools[rel] for rel, path in actual_tools.items()):
        raise ValueError("publish 自带 Tools 依赖的文件或内容变化")
    tools_manifest = {
        "schema": "zxai-cloud-preview-tools/1", "generatedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "source": "make-staging excluded-tools publish", "zipSource": None, "dest": str(tools),
        "policy": {"publishDependenciesOnly": True, "mergedFullThirdPartyTools": False,
                   "excludedFiles": [], "excludedDirs": [], "addedFromZip": []},
        "fileCount": len(actual_tools), "excluded": [], "addedFromZip": [],
        "files": {rel: path.stat().st_size for rel, path in sorted(actual_tools.items())},
        "sha256": dict(sorted(old_tools.items())),
    }
    write_new_json(candidate / "tools-merge-manifest.json", tools_manifest)
    root_hashes = {LAUNCHER_NAME: copy_new_file(launcher_source, candidate / LAUNCHER_NAME)}
    for source in documents:
        root_hashes[source.name] = copy_new_file(source, candidate / source.name)
    guide = candidate / "启动说明.txt"
    require_absent(guide)
    with guide.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(startup_guide(args.version, candidate.name.startswith("TubaWinUi3CE-cand6-workbench-preview-")))
    root_hashes[guide.name] = sha256(guide)

    # These two existing staging locks are the only explicitly permitted replacements.
    # Their original bytes have already been preserved in staging-*.json above.
    list(safe_tree(src))
    app_lock_path, runtime_lock_path = (candidate / n for n in STAGING_LOCK_NAMES)
    for path in (app_lock_path, runtime_lock_path):
        check_ancestors(path)
    app_lock = staging.content_lock(str(src), exclude_parts=("runtime",), lock_path=str(app_lock_path), label="src (excluding runtime/)")
    runtime_lock = staging.content_lock(str(src / "runtime"), lock_path=str(runtime_lock_path), label="src/runtime")
    list(safe_tree(src))
    for directory, lock, excluded in ((src, app_lock_path, ["runtime"]), (src / "runtime", runtime_lock_path, [])):
        if verifier.verify(SimpleNamespace(dir=str(directory), lock=str(lock), exclude_part=excluded)) != 0:
            raise ValueError("整理后内容锁校验失败")
    info = {
        "schema": "zxai-cloud-preview-candidate/1", "generatedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "candidateRoot": str(candidate), "version": args.version,
        "layout": {"launcher": LAUNCHER_NAME, "appDir": "src", "toolsDir": "src/Tools"},
        "launcherSha256": root_hashes[LAUNCHER_NAME], "rootFilesSha256": root_hashes,
        "tools": {"fileCount": len(actual_tools), "excludedCount": 0, "addedFromZipCount": 0,
                  "manifestPath": "tools-merge-manifest.json", "fullThirdPartyToolsMerged": False},
        "finalLocks": {}, "boundaries": {"thirdPartyTools": "cloud/on-demand; no full Tools merge",
            "clientUpdate": "separate client package", "assemblyLaunchedOrInstalledAnything": False},
    }
    for key, summary, path in (("app", app_lock, app_lock_path), ("runtime", runtime_lock, runtime_lock_path)):
        info["finalLocks"][key] = {"fileCount": summary["fileCount"], "sizeBytes": summary["sizeBytes"],
            "aggregateSha256": summary["aggregateSha256"], "lockFileSha256": sha256(path)}
    for name in ("BUILD-INFO.json", "source-fingerprint.json", "tools-merge-manifest.json",
                 *("staging-" + n for n in STAGING_LOCK_NAMES)):
        info[name] = {"sha256": sha256(candidate / name)}
    # Written last: the existing ZIP helper requires this success artifact.
    write_new_json(candidate / "candidate-build-info.json", info)
    print("ASSEMBLE PASS: %s; publish Tools dependencies=%d; no full Tools merge; app=%s; runtime=%s" %
          (candidate, len(actual_tools), app_lock["aggregateSha256"], runtime_lock["aggregateSha256"]))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--repo", default=str(SOURCE_ROOT))
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    try:
        assemble(args)
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, "FAIL: %s\n" % error)


if __name__ == "__main__":
    main()
