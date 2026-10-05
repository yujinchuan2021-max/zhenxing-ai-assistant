#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""A08 第四批：卸载数据删除路径安全 + 覆盖升级日志身份 —— 隔离路径规划测试 + 静态断言。

第三次复核确认的三个缺口（本批修复，写入脚本见 installer-tests/apply_a08_fourth_batch.py）：
  ① 任意 Custom 目录保守保留：目录名（含 tuba 字样）、标记文件、文件名名单都不是归属证明 →
     自定义目录一律「完整保留全部内容」，不删除任何条目、也不移除目录本身；
     不再维护「产品独有名字 / 通用名字」名单（没有可信归属记录就不删）。
  ② 删除前逐级检查路径链 reparse（含盘符根/共享根与目标自身）：链接路径一律拒绝删除
     （只检查最终条目不够：祖先 junction 会把删除带到链接目标里去）。
  ③ 卸载日志身份确认：同 AppId + 同安装模式/架构才能直接覆盖；身份不一致 → 直接中止；
     无法确认 / 多于一份 → 默认中止（默认按钮「否」）。「数一数几份日志」不是迁移验证。

安全纪律（本脚本必须遵守）：
  * 只做字符串计算与「删除计划」推演，绝不执行卸载器、绝不删除/创建/探测任何真实目录或文件；
  * 只以文本方式读取仓库里的 installer*.iss；
  * 「文件系统」全部由内存模型 FakeFs 提供（哪些路径存在 / 哪些是重解析点），不做任何磁盘访问；
  * 卸载日志头部用内存里的字节串构造（字段布局取自真实 unins000.dat 的实测结果：
    字段 1 = 格式标记且只有 "(b)" 与 "(b) 64-bit" 两种，字段 2 = AppId；
    实测样本 D:\\AI\\zhenxingAI-toolchains\\inno-7.1.0-full\\unins000.dat 与
    C:\\Program Files (x86)\\MSI\\MSI Center\\unins000.dat —— 两个字段与卸载注册表键名
    "<AppId>_is1" 完全相同）。

结构：
  1)  legacy_plan：第三批之前的逻辑（复现 P1：D:\\Temp/.. 被放行 → DelTree 指向盘根）；
  1b) legacy3_plan：批次三的逻辑（复现缺口① 与缺口② 的具体后果）；
  2)  new_plan：第四批的判定表（分类规则不变，但自定义目录一律保留）、全局不变量、删除点复检；
  2d) 删除门 TubaReadyToDeleteDir + 逐级 reparse 检查（正常 / 末级链接 / 祖先链接 / 根是挂载点）；
  2e) 卸载日志身份检查（放行 / 直接中止 / 默认中止）；
  3)  四份 .iss 的静态断言（含「不存在按文件名删除」「每个 DelTree 都经过删除门」等）。

退出码：0 = 全部通过；1 = 有失败项。
"""

from __future__ import annotations

import hashlib
import ntpath
import os
import re
import sys

REPO = os.environ.get("TUBA_REPO", os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ISS_FILES = ["installer.iss", "installer-x64.iss", "installer-arm64.iss", "installer-x86.iss"]

# ---------------------------------------------------------------------------
# 模拟环境：常量取值只是字符串（既不是真实路径，也不做任何磁盘操作）
# ---------------------------------------------------------------------------
ENV_PF = {  # {app} 在 C:\Program Files 下的常规安装
    "app": r"C:\Program Files\TubaWinUi3",
    "localappdata": r"C:\Users\tester\AppData\Local",
    "userappdata": r"C:\Users\tester\AppData\Roaming",
    "userprofile": r"C:\Users\tester",
    "userdocs": r"C:\Users\tester\Documents",
    "commondocs": r"C:\Users\Public\Documents",
    "commonappdata": r"C:\ProgramData",
    "commonprograms": r"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",
    "win": r"C:\Windows",
    "sys": r"C:\Windows\System32",
    "pf": r"C:\Program Files",
    "pf32": r"C:\Program Files (x86)",
    "sd": "C:",
}
ENV_D = dict(ENV_PF)  # {app} 在 D:\Apps 下的自定义安装（相对路径用例）
ENV_D.update(
    {
        "app": r"D:\Apps\TubaWinUi3",
        "localappdata": r"D:\Users\tester\AppData\Local",
        "userappdata": r"D:\Users\tester\AppData\Roaming",
        "userprofile": r"D:\Users\tester",
        "userdocs": r"D:\Users\tester\Documents",
        "win": r"D:\Windows",
        "sys": r"D:\Windows\System32",
        "pf": r"D:\Program Files",
        "pf32": r"D:\Program Files (x86)",
    }
)

# 批次三用过的三份名单（批次四已从 .iss 删除）——只用于「复现缺口」，同时用于反向断言：
# 这些名字绝不能再出现在 .iss 里当作删除依据。
LEGACY3_UNIQUE_FILES = [
    "ai_providers.json",
    "engine.json",
    "project-profile.json",
    "launch_history.json",
    "popup_settings.json",
    "sensor_dump.txt",
    "skipped_version.txt",
    "download_queue.json",
    "BenchmarkHistory.json",
    "WinBenchmarkHistory.json",
    "hardware_spoofer_backup.json",
    "game_overlay_auto.log",
    "agent-debug.log",
    ".config_location",
]
LEGACY3_UNIQUE_DIRS = [
    "AiAssistant",
    "GameTunnel",
    "GameLogos",
    "GameMonitorRecords",
    "TimeSync",
    "JunkCleaner",
    "RuntimeRepair",
    "DotnetDownloads",
    "ErrorReports",
    "BenchmarkCache",
    "active_intercept",
    "DesktopIcons",
    "LanShare",
    "officecli",
    "Sysinternals",
    "启动项导出",
    "启动项管理",
]
LEGACY3_GENERIC_FILES = [
    "settings.json",
    "favorites.json",
    "stress_test.log",
    "furmark_settings.json",
    "net_stress_settings.json",
]
LEGACY3_GENERIC_DIRS = [
    "IconCache",
    "Metadata",
    "Backgrounds",
    "Cache",
    "download",
    "downloads",
    "ffmpeg",
    "imagemagick",
    "Tools",
    "WebView2",
]

PROTECTED_CONSTANTS = [
    "win",
    "sys",
    "pf",
    "pf32",
    "localappdata",
    "userappdata",
    "userprofile",
    "userdocs",
    "commondocs",
    "commonappdata",
    "commonprograms",
]
CRITICAL_CONSTANTS = ["win", "sys", "pf", "pf32", "commonappdata"]

# 卸载日志（unins???.dat）头部的两种格式标记（真实安装器二进制里只有这两种字面量）
LOG_TAG_32 = "Inno Setup Uninstall Log (b)"
LOG_TAG_64 = "Inno Setup Uninstall Log (b) 64-bit"

# ===========================================================================
# 第 1 部分：第三批之前的旧逻辑（复现 P1）
# ===========================================================================


def legacy_normalize(s: str) -> str:
    """旧 .iss：NormalizeUninstallPath（Trim + 去尾反斜杠 + LowerCase）。"""
    s = s.strip()
    i = len(s)
    while i > 0 and s[i - 1] == "\\":
        i -= 1
    return s[:i].lower()


def legacy_is_safe(s: str, env: dict) -> bool:
    """旧 .iss：IsSafeUninstallDataDir（字符串级检查，不解析正斜杠/点段）。"""
    norm = legacy_normalize(s)
    app = legacy_normalize(env["app"])
    if norm == "":
        return False
    if len(norm) <= 2:
        return False
    if norm in (".", ".."):
        return False
    if norm.endswith("\\.") or norm.endswith("\\.."):
        return False
    if "\\." + "\\" in norm or "\\.." + "\\" in norm:
        return False
    if norm.startswith("\\\\") and norm.count("\\") <= 3:
        return False
    if norm == app:
        return False
    if app.startswith(norm + "\\"):
        return False
    return True


def legacy_plan(marker: str, env: dict) -> dict:
    """旧 .iss 的删除计划：相对路径拼到 {app} 后校验，其余直接校验；通过就 DelTree 原样字符串。"""
    plan = {"target": None, "why": ""}
    if marker is None:
        plan["why"] = "无标记文件"
        return plan
    if not marker.upper().startswith("CUSTOM:"):
        plan["why"] = "非 Custom"
        return plan
    raw = marker[7:].strip()
    while raw.startswith("\\"):
        raw = raw[1:]
    if len(raw) >= 3 and raw[1] == ":" and raw[2] == "\\":
        cand = raw
    else:
        if raw == "":
            plan["why"] = "空路径"
            return plan
        cand = env["app"] + "\\" + raw  # 旧代码：相对路径按 {app} 拼接
    if legacy_is_safe(cand, env):
        plan["target"] = cand
        plan["why"] = "旧逻辑放行"
    else:
        plan["why"] = "旧逻辑拒绝"
    return plan


def win32_resolve(path: str) -> str:
    """纯字符串模拟 Win32 对路径的解析（等价 ntpath.normpath），用于展示 DelTree 的实际落点。"""
    return ntpath.normpath(path)


# ===========================================================================
# 第 2 部分：第四批的逻辑（与 installer*.iss 的 Tuba* 函数一一对应）
# ===========================================================================


def TubaPosCI(sub: str, s: str) -> int:
    return s.lower().find(sub.lower())


def TubaCountChar(s: str, ch: str) -> int:
    return s.count(ch)


def TubaSameOrUnder(p: str, base: str) -> bool:
    return bool(base) and (p == base or p[: len(base) + 1] == base + "\\")


def TubaApplySegment(seg: str, acc: str, ok: bool) -> tuple[str, bool]:
    if not ok:
        return acc, ok
    if seg in ("", "."):
        return acc, ok
    if seg == "..":
        if acc == "":
            return acc, False  # 已到根再回退 = 越界 → 拒绝
        p = acc.rfind("\\")
        return ("" if p < 0 else acc[:p]), ok
    for c in seg:
        if c in '*?{}:"<>|':
            return acc, False  # 通配符/未展开占位符/数据流 → 拒绝
    return acc + "\\" + seg.lower(), ok


def TubaCanonAppDir(env: dict) -> str:
    s = env["app"].strip()
    if len(s) < 3 or s[1] != ":" or s[2] not in "\\/":
        return ""
    acc, ok, seg = "", True, ""
    for c in s[3:]:
        if c in "\\/":
            acc, ok = TubaApplySegment(seg, acc, ok)
            seg = ""
            if not ok:
                return ""
        else:
            seg += c
    if ok:
        acc, ok = TubaApplySegment(seg, acc, ok)
    if not ok:
        return ""
    return s[:2].lower() + acc


def TubaReplaceToken(s: str, token: str, value: str) -> str:
    out, i, n, L = "", 0, len(s), len(token)
    if L == 0:
        return s
    while i < n:
        if i + L <= n and s[i : i + L].lower() == token.lower():
            out += value
            i += L
        else:
            out += s[i]
            i += 1
    return out


def TubaParentDirOf(d: str) -> str:
    p = d.rfind("\\")
    return d[:p] if p > 0 else ""


def TubaExpandKnownPlaceholders(s: str, env: dict) -> str:
    appd = env["app"].strip()
    parent = TubaParentDirOf(appd)
    out = TubaReplaceToken(s, "{AppDir}", appd)
    if parent:
        out = TubaReplaceToken(out, "{ParentDir}", parent)
    out = TubaReplaceToken(out, "{AppDataDir}", env["localappdata"].strip() + "\\TubaWinUi3")
    return out


def TubaResolvePath(raw: str, env: dict) -> tuple[bool, str, str]:
    """返回 (ok, 规范小写绝对路径, 原因)。复现 .iss 的 TubaResolvePath。"""
    s = TubaExpandKnownPlaceholders(raw.strip(), env).strip()
    if s == "":
        return False, "", "空路径"
    if len(s) >= 3 and s[:2] == "\\\\" and s[2] in "?.":
        return False, "", "设备路径不解析"
    unc = len(s) >= 2 and s[0] in "\\/" and s[1] in "\\/"
    drive = (
        (not unc)
        and len(s) >= 2
        and s[1] == ":"
        and (len(s) == 2 or s[2] in "\\/")
    )
    if (not unc) and (not drive) and len(s) >= 2 and s[1] == ":":
        return False, "", "盘符相对路径（依赖各驱动器的当前目录，不确定）"
    acc = ""
    if drive:
        canon = s[:2].lower()
        rest = s[2:]
    elif unc:
        rest, seg, j, canon = s[2:], "", 0, ""
        for i, c in enumerate(rest):
            if c in "\\/":
                if seg != "":
                    canon = ("\\\\" + seg.lower()) if j == 0 else (canon + "\\" + seg.lower())
                    j += 1
                    seg = ""
                    if j >= 2:
                        rest = rest[i + 1 :]
                        break
            else:
                seg += c
        if j < 2 and j == 1 and seg != "":
            canon = canon + "\\" + seg.lower()
            rest, j = "", 2
        if j < 2 or canon == "":
            return False, "", "UNC 路径缺少 server/share"
    elif s[0] in "\\/":
        return False, "", "无盘符的根相对路径（依赖当前驱动器）"
    else:
        appdir = TubaCanonAppDir(env)
        if appdir == "":
            return False, "", "安装目录无法确定"
        canon, acc, rest = appdir[:2], appdir[2:], s
    ok, seg = True, ""
    for c in rest:
        if c in "\\/":
            acc, ok = TubaApplySegment(seg, acc, ok)
            seg = ""
            if not ok:
                break
        else:
            seg += c
    if ok:
        acc, ok = TubaApplySegment(seg, acc, ok)
    if not ok:
        return False, "", "路径段含非法字符或 .. 越出根（不确定）"
    return True, canon + acc, ""


def TubaCanonConstant(name: str, env: dict) -> str:
    if name not in env:
        return ""
    ok, canon, _ = TubaResolvePath(env[name], env)
    return canon if ok else ""


def TubaIsProtectedUninstallPath(canon: str, env: dict) -> tuple[bool, str]:
    appcanon = TubaCanonAppDir(env)
    # 规则 1：Canon 就是系统/用户共享根，或位于它们之上（上级）→ 跳过删除
    for name in PROTECTED_CONSTANTS:
        w = TubaCanonConstant(name, env)
        if w and (TubaSameOrUnder(w, canon) or w == canon):
            return True, "系统或共享目录: {%s}" % name
    # 规则 2：Canon 落在关键系统目录之内（安装目录 {app} 及其子目录除外）→ 跳过删除
    if TubaSameOrUnder(canon, appcanon):
        return False, ""
    for name in CRITICAL_CONSTANTS:
        w = TubaCanonConstant(name, env)
        if w and TubaSameOrUnder(canon, w):
            return True, "关键系统目录之内: {%s}" % name
    return False, ""


def TubaIsSafeUninstallDataDir(canon: str, env: dict) -> tuple[bool, str]:
    if canon == "":
        return False, "空路径"
    if canon[:2] == "\\\\":
        if TubaCountChar(canon, "\\") < 4:
            return False, "UNC 共享根"
    elif len(canon) <= 2 or TubaCountChar(canon, "\\") == 0:
        return False, "盘根"
    appcanon = TubaCanonAppDir(env)
    if appcanon and canon == appcanon:
        return False, "安装目录本身"
    if appcanon and appcanon[: len(canon) + 1] == canon + "\\":
        return False, "安装目录的上级目录"
    blocked, why = TubaIsProtectedUninstallPath(canon, env)
    if blocked:
        return False, why
    return True, ""


def TubaIsStrictlyInsideAppDir(p: str, env: dict) -> bool:
    ok, canon, _ = TubaResolvePath(p, env)
    appcanon = TubaCanonAppDir(env)
    return bool(ok and appcanon) and canon[: len(appcanon) + 1] == appcanon + "\\"


# ---------------------------------------------------------------------------
# [缺口②] 重解析点与路径链的内存模型（不做任何磁盘访问）
# ---------------------------------------------------------------------------


class FakeFs:
    """离线文件系统模型：登记「哪些路径存在」与「哪些是重解析点（符号链接/联接/挂载点）」。

    复现 .iss 的 TubaIsReparsePoint：查询失败（不在模型里 = 探测不到）→ True（保守当作链接）。
    路径键统一去掉尾部反斜杠（真实 API 对 "D:\\Alias" 与 "D:\\Alias\\" 等价）。
    """

    def __init__(self, dirs=(), reparse=()):
        self.dirs = {norm_path(d) for d in dirs}
        self.reparse = {norm_path(d) for d in reparse}

    def probe(self, p: str) -> bool:
        p = norm_path(p)
        if p not in self.dirs:
            return True  # 探测不到 → 不可确认 → 按链接处理
        return p in self.reparse


def norm_path(p: str) -> str:
    """路径键归一：小写 + 去掉尾部反斜杠（保留盘根 'c:\\'）。"""
    p = p.lower()
    while len(p) > 3 and p.endswith("\\"):
        p = p[:-1]
    return p


def chain_prefixes(canon: str) -> list[str]:
    """TubaAnyReparseInPath 会逐级探测的前缀（含盘符根与目标自身；跳过 UNC 的 server/share 前几级）。"""
    out = []
    for i, ch in enumerate(canon):
        if ch == "\\" or i == len(canon) - 1:
            p = canon[: i + 1]
            norm = p
            while len(norm) > 1 and norm.endswith("\\"):
                norm = norm[:-1]
            if len(p) >= 3 and (p[:2] != "\\\\" or norm.count("\\") >= 4):
                out.append(p)
    return out


def fs_for(*paths: str) -> FakeFs:
    """把给出的路径及其所有会被探测的前缀登记为「存在且不是链接」（正常环境）。"""
    fs = FakeFs()
    for p in paths:
        fs.dirs.add(norm_path(p))
        for pref in chain_prefixes(p.lower()):
            fs.dirs.add(norm_path(pref))
    return fs


def TubaAnyReparseInPath(canon: str, fs: FakeFs) -> tuple[bool, str]:
    """复现 .iss：从根到目标逐级检查；任一级是重解析点/无法确认 → (True, 那一级)。"""
    if canon == "":
        return True, ""
    for pref in chain_prefixes(canon):
        if fs.probe(pref):
            return True, pref
    return False, ""


def TubaReadyToDeleteDir(d: str, env: dict, fs: FakeFs) -> tuple[bool, str, str]:
    """复现 .iss 的删除前最后一道门：存在 → 规范路径 → 边界检查 → 整条路径链无重解析点。"""
    if norm_path(d) not in fs.dirs:
        return False, "", "目标不存在"
    ok, canon, why = TubaResolvePath(d, env)
    if not ok:
        return False, "", why
    safe, why = TubaIsSafeUninstallDataDir(canon, env)
    if not safe:
        return False, canon, why
    bad, where = TubaAnyReparseInPath(canon, fs)
    if bad:
        return False, canon, "路径链上存在重解析点（符号链接/目录联接/挂载卷），拒绝删除: " + where
    return True, canon, ""


# ---------------------------------------------------------------------------
# 删除计划
# ---------------------------------------------------------------------------


def new_plan(marker: str, env: dict, fs: FakeFs | None = None) -> dict:
    """第四批的删除计划（不访问磁盘）：
      * 默认数据目录 / {app}\\Data：过 TubaReadyToDeleteDir → DelTree；
      * 自定义目录：只分类（解析 + 边界检查），一律完整保留 —— 删除条目恒为空。
    """
    fs = FakeFs() if fs is None else fs
    appdata = env["app"] + "\\Data"
    default = env["localappdata"] + "\\TubaWinUi3"
    d_ok, _d_canon, d_why = TubaReadyToDeleteDir(default, env, fs)
    a_ok, _a_canon, a_why = TubaReadyToDeleteDir(appdata, env, fs)
    plan = {
        "default_dir": default if d_ok else None,
        "default_why": d_why,
        "approot": appdata if (TubaIsStrictlyInsideAppDir(appdata, env) and a_ok) else None,
        "approot_why": a_why,
        "custom": None,       # 解析出的规范绝对路径（仅记录）
        "custom_why": "",
        "custom_same_as_default": False,
        "retain_all": False,  # 自定义目录是否「完整保留」（第四批恒为 True）
        "entries": [],        # 计划删除的条目（第四批恒为空）
        "remove_dir_if_empty": False,  # 第四批不再移除自定义目录本身
    }
    if marker is None:
        plan["custom_why"] = "无标记文件"
        return plan
    text = marker.strip()
    pos = TubaPosCI("Custom:", text)
    if pos < 0:
        plan["custom_why"] = "非 Custom 模式（AppRoot/AppData）"
        return plan
    raw = text[pos + 7 :].strip()
    ok, canon, why = TubaResolvePath(raw, env)
    if not ok:
        plan["custom_why"] = why
        return plan
    safe, why = TubaIsSafeUninstallDataDir(canon, env)
    if not safe:
        plan["custom_why"] = why
        return plan
    if canon == env["localappdata"].lower().strip() + "\\tubawinui3":
        plan["custom_same_as_default"] = True
        plan["custom_why"] = "与默认数据目录相同（步骤 1 已处理）"
        return plan
    plan["custom"] = canon
    plan["retain_all"] = True
    plan["custom_why"] = "分类通过；第四批起一律完整保留（不删除任何内容）"
    return plan


def legacy3_plan(marker: str, env: dict) -> dict:
    """批次三的逻辑（第四批之前的 .iss）：自定义目录按「末段目录名含 tuba」决定条目范围。

    只用于复现缺口①②的后果：目录名不是归属证明；删除点也不检查祖先 reparse。
    """
    base = new_plan(marker, env)
    if base["custom"] is None:
        base["entries"] = []
        return base
    canon = base["custom"]
    entries = list(LEGACY3_UNIQUE_FILES) + list(LEGACY3_UNIQUE_DIRS)
    leaf = canon[canon.rfind("\\") + 1 :]
    if TubaPosCI("tuba", leaf) >= 0:
        entries += LEGACY3_GENERIC_FILES + LEGACY3_GENERIC_DIRS
    base["entries"] = entries
    base["remove_dir_if_empty"] = True
    return base


# ===========================================================================
# 第 2e 部分：卸载日志头部的读取与身份判定（内存字节串，不读真实文件）
# ===========================================================================


def make_log_header(tag: str, appid: str, block: int = 64) -> bytes:
    """构造一份卸载日志的头部字节（默认按真实布局：两个 64 字节对齐的 #0 结尾字段）。"""
    def field(s: str) -> bytes:
        b = s.encode("ascii") + b"\x00"
        return b + b"\x00" * max(0, block - len(b))

    return field(tag) + field(appid) + b"\x00" * 32 + b"payload-bytes" + b"\x00" * 16


def TubaNextLogField(data: bytes, at: int, max_at: int) -> tuple[str, int]:
    """复现 .iss 的 TubaNextLogField：取一个 #0 结尾的 ASCII 字段并跳过 #0 填充。

    max_at 是绝对偏移上限（0 基，右开）——对应 .iss 里的 64 / 128（日志格式 (b) 的两个 64 字节块）。
    """
    out = bytearray()
    while at < len(data) and at < max_at and data[at] != 0 and len(out) < 128:
        out.append(data[at])
        at += 1
    while at < len(data) and at < max_at and data[at] == 0:
        at += 1
    return out.decode("ascii", "replace"), at


def TubaReadLogIdentity(data: bytes | None) -> tuple[bool, str, str]:
    """复现 .iss 的 TubaReadLogIdentity：读不出 / 格式不认识 → (False, tag, appid)。"""
    if data is None:
        return False, "", ""
    at = 0
    tag, at = TubaNextLogField(data, at, 64)     # 字段 1 = 头部第 1 个 64 字节块
    appid, at = TubaNextLogField(data, at, 128)  # 字段 2 = 头部第 2 个 64 字节块
    if tag[:24].lower() != "inno setup uninstall log":
        return False, tag, appid
    if appid == "":
        return False, tag, appid
    return True, tag, appid


def TubaCheckUninstallLogs(logs: dict[str, bytes | None], self_tag: str, self_id: str) -> tuple[str, str]:
    """复现 .iss 的 TubaCheckUninstallLogs：返回 (status, detail)。

    status: 'ok'          = 没有日志，或只有一份且身份确认 → 放行直接覆盖
            'foreign'     = 有 AppId 不同的日志（另一个应用）→ 直接中止（不给「继续」的机会）
            'mismatch'    = AppId 相同但模式/架构标记不同 → 默认中止（默认按钮「否」）
            'unconfirmed' = 存在读不出身份的日志 → 默认中止（默认按钮「否」）
            'multi'       = 多于一份，且都确认是本身份 → 默认中止（覆盖只会替换其中一份）
    """
    found = len(logs)
    if found == 0:
        return "ok", "没有历史卸载日志"
    confirmed = foreign = mismatch = unreadable = 0
    foreign_list, mismatch_list, unreadable_list = [], [], []
    for name in sorted(logs):
        ok, tag, appid = TubaReadLogIdentity(logs[name])
        if not ok:
            unreadable += 1
            unreadable_list.append(name)
        elif appid.lower() != self_id.lower():
            foreign += 1
            foreign_list.append("%s[%s|%s]" % (name, tag, appid))
        elif tag.lower() == self_tag.lower():
            confirmed += 1
        else:
            mismatch += 1
            mismatch_list.append("%s[%s]" % (name, tag))
    if foreign:
        return "foreign", "AppId 不一致 %d 份: %s" % (foreign, ", ".join(foreign_list))
    if mismatch:
        return "mismatch", "同 AppId 但模式/架构标记不同 %d 份: %s" % (mismatch, ", ".join(mismatch_list))
    if unreadable:
        return "unconfirmed", "无法确认 %d 份: %s" % (unreadable, ", ".join(unreadable_list))
    if found > 1:
        return "multi", "多份但身份都确认（%d 份，覆盖只会替换其中一份）" % found
    return "ok", "仅一份且身份确认（confirmed=%d）" % confirmed


def read_iss(fname: str) -> str:
    with open(os.path.join(REPO, fname), "r", encoding="utf-8-sig", newline="") as fh:
        return fh.read()


def iss_self_identity(fname: str) -> tuple[str, str]:
    """从 .iss 文本推出「本安装器写入日志的身份」：格式标记 + 有效 AppId。

    有效 AppId = [Setup] AppId 原值，开头的 '{{' 按脚本转义规则还原成单个 '{'
    （与写进 unins???.dat 头部、写进卸载注册表键名 "<AppId>_is1" 的值一致）。
    """
    t = read_iss(fname)
    m = re.search(r"(?m)^AppId=(.*)$", t)
    assert m, "%s 缺少 AppId" % fname
    raw = m.group(1).strip()
    eff = raw[1:] if raw.startswith("{{") else raw
    m64 = re.search(r"(?m)^ArchitecturesInstallIn64BitMode=(.*)$", t)
    assert m64, "%s 缺少 ArchitecturesInstallIn64BitMode" % fname
    in64 = m64.group(1).strip() != ""
    return (LOG_TAG_64 if in64 else LOG_TAG_32), eff


# ===========================================================================
# 第 3 部分：判定表与静态断言
# ===========================================================================

FAILURES: list[str] = []
CHECKS = 0


def noncomment_view(text: str) -> str:
    """去掉 [Setup] 的 ; 注释行与 [Code] 的 // 注释，用于“不得出现”类静态检查。"""
    out = []
    for ln in text.splitlines():
        if ln.strip().startswith(";"):
            continue
        p = ln.find("//")
        if p >= 0:
            ln = ln[:p]
        out.append(ln)
    return "\n".join(out)


def strip_iss_code(text: str) -> str:
    """去掉 Inno 脚本里的字符串字面量、// 注释与 {..} 注释，只留代码字符（用于结构性 lint）。"""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == "'":
            i += 1
            while i < n:
                if text[i] == "'":
                    if i + 1 < n and text[i + 1] == "'":
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            out.append("''")
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] not in "\r\n":
                i += 1
            continue
        if c == "{":
            while i < n and text[i] not in "\r\n":
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def check(name: str, cond: bool, detail: str = "") -> None:
    global CHECKS
    CHECKS += 1
    if cond:
        print("  PASS  %s" % name)
    else:
        print("  FAIL  %s%s" % (name, ("  <- " + detail) if detail else ""))
        FAILURES.append(name + ((" | " + detail) if detail else ""))


def part1_legacy_repro() -> None:
    print("\n[1] 第三批之前的逻辑：P1 复现（审计给出的输入）")
    cases = [
        (r"Custom:D:\Temp/..", "D:\\", "盘根"),
        (r"Custom:Data/../..", r"C:\Program Files", "越过安装目录 → 上级"),
    ]
    for marker, expect_target, note in cases:
        plan = legacy_plan(marker, ENV_PF)
        resolved = win32_resolve(plan["target"]) if plan["target"] else None
        print("      %-22s 旧逻辑放行=%s  DelTree 实际落点=%s  (%s)" % (marker, plan["target"] is not None, resolved, note))
        check(
            "legacy 放行危险输入 %s（证明 P1 存在）" % marker,
            plan["target"] is not None and resolved == expect_target,
            "got target=%r resolved=%r" % (plan["target"], resolved),
        )
    plan = legacy_plan(r"Custom:D:\TubaData", ENV_PF)
    check("legacy 仍然放行普通自定义目录 D:\\TubaData（对照）", plan["target"] == r"D:\TubaData")


def part1b_gap_repro() -> None:
    print("\n[1b] 批次三的逻辑：缺口①（拿目录名当归属证明）与缺口②（只查末级 reparse）复现")
    # 缺口①：无关目录 D:\Projects\tuba-notes（别人的笔记目录，名字里恰好有 tuba）
    marker = r"Custom:D:\Projects\tuba-notes"
    old = legacy3_plan(marker, ENV_PF)
    print("      批次三条目数=%d  含 settings.json=%s  含 ai_providers.json=%s  含 Tools=%s"
          % (len(old["entries"]), "settings.json" in old["entries"],
             "ai_providers.json" in old["entries"], "Tools" in old["entries"]))
    check("缺口①复现：批次三会把 D:\\Projects\\tuba-notes 里的通用名条目列入删除",
          {"settings.json", "Tools", "favorites.json"} <= set(old["entries"]))
    check("缺口①复现：批次三连产品独有名字也会删（ai_providers.json / engine.json）",
          {"ai_providers.json", "engine.json"} <= set(old["entries"]))
    old_other = legacy3_plan(r"Custom:D:\Projects\notes", ENV_PF)
    check("缺口①复现：同一逻辑在没有 tuba 字样的目录里只删独有名字（差异只来自目录名）",
          "ai_providers.json" in old_other["entries"] and "settings.json" not in old_other["entries"])

    # 缺口②：Custom:D:\Alias 是指向 D:\Shared 的目录联接（junction）
    alias = legacy3_plan(r"Custom:D:\Alias", ENV_PF)
    junctions = {"d:\\alias": "d:\\shared"}  # 离线模型：Alias 是指向 Shared 的目录联接

    def land_on(p: str) -> str:
        """按 junction 表把路径改写到链接目标（纯字符串，不接触磁盘）。"""
        low = p.lower()
        for link, target in junctions.items():
            if low == link or low.startswith(link + "\\"):
                return target + p[len(link):]
        return p

    landed = [land_on(win32_resolve("D:\\Alias\\" + n)) for n in alias["entries"][:3]]
    print("      批次三：Alias 条目数=%d，删除实际落点示例=%s" % (len(alias["entries"]), landed))
    check("缺口②复现：批次三对 junction 路径 D:\\Alias 仍会删除条目（删除点只查末级）",
          "ai_providers.json" in alias["entries"])
    check("缺口②复现：这些删除实际落到链接目标 D:\\Shared 下",
          all(p.lower().startswith("d:\\shared\\") for p in landed), repr(landed))


def part2_table() -> None:
    print("\n[2] 第四批的判定表（路径规划 + 内存文件系统，无磁盘访问）")
    default_pf = ENV_PF["localappdata"] + "\\TubaWinUi3"
    fs_pf = fs_for(ENV_PF["app"], ENV_PF["app"] + "\\Data", default_pf)
    fs_d = fs_for(ENV_D["app"], ENV_D["app"] + "\\Data", ENV_D["localappdata"] + "\\TubaWinUi3")
    fs_map = {id(ENV_PF): fs_pf, id(ENV_D): fs_d}

    # (marker, env, 期望 custom 路径 或 None, 期望原因子串)
    table = [
        (None, ENV_PF, None, "无标记文件"),
        ("AppRoot", ENV_PF, None, "非 Custom"),
        ("custom:D:\\TubaData", ENV_PF, r"d:\tubadata", ""),          # 前缀大小写不敏感（ConfigManager 一致）
        (r"Custom:D:\Temp/..", ENV_PF, None, "盘根"),                  # 审计样例 1
        (r"Custom:D:\Temp\..\..\x", ENV_PF, None, "越出根"),
        (r"Custom:Data/../..", ENV_D, None, "上级目录"),                # 审计样例 2（相对路径，解析后越过安装目录）
        (r"Custom:..", ENV_D, None, "上级目录"),
        (r"Custom:..\..\Windows", ENV_D, None, "系统或共享目录"),
        (r"Custom:..\SiblingConfig", ENV_D, r"d:\apps\siblingconfig", ""),
        (r"Custom:Data\Sub", ENV_D, r"d:\apps\tubawinui3\data\sub", ""),
        (r"Custom:Data/..\Data", ENV_D, r"d:\apps\tubawinui3\data", ""),
        (r"Custom:%TEMP%\TubaTmp", ENV_D, r"d:\apps\tubawinui3\%temp%\tubatmp", ""),
        (r"Custom:{AppDir}\Data", ENV_PF, r"c:\program files\tubawinui3\data", ""),
        (r"Custom:{DataDir}\Backgrounds", ENV_PF, None, "非法字符"),
        (r"Custom:{ToolsRoot}\cfg", ENV_PF, None, "非法字符"),
        ("Custom:" + default_pf, ENV_PF, None, "与默认数据目录相同"),
        (r"Custom:C:", ENV_PF, None, "盘根"),
        ("Custom:C:\\", ENV_PF, None, "盘根"),
        (r"Custom:C:foo", ENV_PF, None, "不确定"),
        (r"Custom:\Data", ENV_PF, None, "根相对路径"),
        (r"Custom:\\server\share", ENV_PF, None, "UNC 共享根"),
        ("Custom:\\\\server\\share\\", ENV_PF, None, "UNC 共享根"),
        (r"Custom:\\server\share\data", ENV_PF, r"\\server\share\data", ""),
        (r"Custom:\\?\C:\Tuba", ENV_PF, None, "设备路径"),
        (r"Custom:C:\Data:stream", ENV_PF, None, "非法字符"),
        (r"Custom:C:\Tuba*\Data", ENV_PF, None, "非法字符"),
        (r"Custom:C:\Temp\TubaWinUi3", ENV_PF, r"c:\temp\tubawinui3", ""),       # 【缺口①】名字含 tuba 不再豁免
        (r"Custom:D:\Projects\tuba-notes", ENV_PF, r"d:\projects\tuba-notes", ""),  # 【缺口①】无关目录
        (r"Custom:C:\Windows", ENV_PF, None, "系统或共享目录"),
        (r"Custom:C:\Windows\System32\config", ENV_PF, None, "关键系统目录之内"),
        (r"Custom:C:\Program Files", ENV_PF, None, "上级目录"),
        (r"Custom:C:\Program Files\OtherApp", ENV_PF, None, "关键系统目录之内"),
        (r"Custom:C:\Temp", ENV_PF, r"c:\temp", ""),
        (r"Custom:C:\Program Files\TubaWinUi3", ENV_PF, None, "安装目录本身"),
        (r"Custom:C:\Users\tester\Documents", ENV_PF, None, "系统或共享目录"),
        (r"Custom:C:\Users\tester\Documents\MyAppData", ENV_PF, r"c:\users\tester\documents\myappdata", ""),
        (r"Custom:C:\Users\tester\AppData", ENV_PF, None, "系统或共享目录"),
        (r"Custom:D:\Temp\..\..\x", ENV_PF, None, "越出根"),
    ]

    for marker, env, exp_path, exp_why in table:
        plan = new_plan(marker, env, fs_map[id(env)])
        got_path = plan["custom"]
        label = "%s" % (marker if marker is not None else "<no marker>")
        if exp_path is None:
            check(
                "拒绝/跳过: %-46s → %s" % (label, plan["custom_why"] or "-"),
                got_path is None and (exp_why in plan["custom_why"]),
                "path=%r why=%r 期望原因含 %r" % (got_path, plan["custom_why"], exp_why),
            )
        else:
            check(
                "分类允许（但一律完整保留）: %-28s → %s" % (label, got_path),
                got_path == exp_path,
                "期望 %r" % exp_path,
            )
            check(
                "  【缺口①】删除条目为空、也不移除目录本身: %s" % label,
                plan["entries"] == [] and plan["retain_all"] and not plan["remove_dir_if_empty"],
                "entries=%d retain=%s remove=%s" % (len(plan["entries"]), plan["retain_all"], plan["remove_dir_if_empty"]),
            )

    # 全局不变量：任何计划都不得指向安装目录、安装目录上级、盘根、系统/共享目录
    print("\n[2b] 全局不变量（所有允许的计划）")
    for marker, env, exp_path, _why in table:
        plan = new_plan(marker, env, fs_map[id(env)])
        target = plan["custom"]
        if target is None:
            continue
        appcanon = TubaCanonAppDir(env)
        check("  目标不是安装目录: %s" % target, target != appcanon)
        check("  目标不是安装目录上级: %s" % target, appcanon[: len(target) + 1] != target + "\\")
        check("  目标不是盘根/共享根: %s" % target, not (len(target) <= 2 or (target[:2] == "\\\\" and target.count("\\") < 4)))
        blocked, why = TubaIsProtectedUninstallPath(target, env)
        check("  目标不在系统/共享目录: %s" % target, not blocked, why)
        check(
            "  固定路径（默认数据目录 / {app}\\Data）仍按 DelTree 删除: %s" % target,
            plan["default_dir"] == env["localappdata"] + "\\TubaWinUi3" and plan["approot"] == env["app"] + "\\Data",
            "default=%r approot=%r (%s / %s)" % (plan["default_dir"], plan["approot"], plan["default_why"], plan["approot_why"]),
        )

    # 删除点重新校验：.iss 的 TubaDeleteUserData 会把保存下来的规范路径再解析一次、再判一次边界
    print("\n[2c] 删除点重新校验（规范路径自稳定 + 边界仍通过；危险目标必须被拒）")
    for marker, env, exp_path, _why in table:
        plan = new_plan(marker, env, fs_map[id(env)])
        target = plan["custom"]
        if target is None:
            continue
        ok, again, why = TubaResolvePath(target, env)
        check("  规范路径自稳定: %s" % target, ok and again == target, "再解析得 %r (%s)" % (again, why))
        if ok:
            safe, why = TubaIsSafeUninstallDataDir(again, env)
            check("  删除点边界检查仍通过: %s" % target, safe, why)
    for bad in ["D:\\", "C:\\", r"\\server\share", r"C:\Program Files", r"C:\Windows", r"D:\Apps"]:
        env = ENV_PF if bad[0].lower() == "c" else ENV_D
        ok, canon, why = TubaResolvePath(bad, env)
        safe = False
        detail = why
        if ok:
            safe, why = TubaIsSafeUninstallDataDir(canon, env)
            detail = "%s → %s" % (canon, why)
        check("  删除点拒绝危险目标: %-22s %s" % (bad, detail), not safe)


def part2d_reparse_chain() -> None:
    print("\n[2d] 【缺口②】删除门 + 逐级 reparse 检查（内存文件系统）")
    default_pf = ENV_PF["localappdata"] + "\\TubaWinUi3"
    canon_default = TubaResolvePath(default_pf, ENV_PF)[1]
    canon_approot = TubaResolvePath(ENV_PF["app"] + "\\Data", ENV_PF)[1]

    # 1) 正常环境：默认数据目录与 {app}\Data 都可删
    fs_ok = fs_for(ENV_PF["app"], ENV_PF["app"] + "\\Data", default_pf)
    ok_d, c, why = TubaReadyToDeleteDir(default_pf, ENV_PF, fs_ok)
    check("  正常环境：默认数据目录允许删除（%s）" % c, ok_d, why)
    ok_a, c2, why2 = TubaReadyToDeleteDir(ENV_PF["app"] + "\\Data", ENV_PF, fs_ok)
    check("  正常环境：{app}\\Data 允许删除（%s）" % c2, ok_a, why2)

    # 2) 盘符根是挂载卷（重解析点）→ 按「含根」要求拒绝
    fs_root = fs_for(ENV_PF["app"], ENV_PF["app"] + "\\Data", default_pf)
    fs_root.reparse.add("c:\\")
    bad, where = TubaAnyReparseInPath(canon_default, fs_root)
    check("  根是挂载点（c:\\）→ 整条链判定经由链接，落点给出根: %s" % where,
          bad and norm_path(where) == "c:\\")
    ok_d2, _c, why = TubaReadyToDeleteDir(default_pf, ENV_PF, fs_root)
    check("  根是挂载点 → 默认数据目录被删除门拒绝", not ok_d2, why)

    # 3) 末级是链接：Custom:D:\Alias → D:\Shared（junction）
    fs_alias = fs_for(r"D:\Alias", r"D:\Shared", r"D:\Shared\sub")
    fs_alias.reparse.add("d:\\alias")
    bad, where = TubaAnyReparseInPath("d:\\alias", fs_alias)
    check("  末级是 junction（d:\\alias）→ 判定经由链接（%s）" % where,
          bad and norm_path(where) == "d:\\alias")

    # 4) 祖先 junction：D:\Alias\sub（Alias 是 junction，sub 自己是普通目录）
    bad, where = TubaAnyReparseInPath("d:\\alias\\sub", fs_alias)
    check("  祖先是 junction（d:\\alias）+ 末级普通（sub）→ 仍判定经由链接（%s）" % where,
          bad and norm_path(where) == "d:\\alias")

    # 5) 更深一层：D:\Alias\sub\cfg 也必须被拦住（逐级而不是只看末级/两级）
    bad, where = TubaAnyReparseInPath("d:\\alias\\sub\\cfg", fs_alias)
    check("  更深路径 d:\\alias\\sub\\cfg 同样被拦住（逐级检查，落点 %s）" % where,
          bad and norm_path(where) == "d:\\alias")

    # 6) 探测不到的前缀（查询失败）→ 保守当作链接
    fs_unknown = FakeFs(dirs=["d:\\alias"])
    bad, where = TubaAnyReparseInPath("d:\\alias\\sub", fs_unknown)
    check("  前缀探测不到（查询失败）→ 保守当作链接（%s）" % where, bad)

    # 7) UNC 前缀不探测：不发起网络访问；比共享根更深的完整路径才探测
    bad, where = TubaAnyReparseInPath(r"\\server\share\data", FakeFs())
    check("  UNC：只探测完整共享路径（不探测 \\\\server\\ / 共享根这些前缀）",
          where == r"\\server\share\data", where)
    fs_unc = FakeFs(dirs=[r"\\server\share", r"\\server\share\data"])
    ok_u, canon_u, why_u = TubaReadyToDeleteDir(r"\\server\share", ENV_PF, fs_unc)
    check("  UNC 共享根被边界检查拒绝（%s）" % why_u, not ok_u and why_u == "UNC 共享根")
    ok_u2, canon_u2, why_u2 = TubaReadyToDeleteDir(r"\\server\share\data", ENV_PF, fs_unc)
    check("  UNC 深路径在模型里通过删除门（边界规则允许它；是否链接由属性探测决定）",
          ok_u2 and canon_u2 == r"\\server\share\data", why_u2)
    ok_u3, _c, why_u3 = TubaReadyToDeleteDir(r"\\server\share\data", ENV_PF, FakeFs())
    check("  模型里未登记的路径：存在性检查先拦下（%s）" % why_u3, not ok_u3 and why_u3 == "目标不存在")
    bad3, where3 = TubaAnyReparseInPath(r"\\server\share\data", FakeFs())
    check("  属性探测不到（探测失败）→ 保守当作链接（%s）" % where3,
          bad3 and where3 == r"\\server\share\data")

    # 8) 自定义目录（含 tuba 名字 / 链接）一律完整保留，且计划里没有任何删除条目
    plan_alias = new_plan(r"Custom:D:\Alias", ENV_PF, fs_alias)
    plan_notes = new_plan(r"Custom:D:\Projects\tuba-notes", ENV_PF, fs_for(r"D:\Projects\tuba-notes"))
    check("  Custom:D:\\Alias（junction）→ 无删除条目", plan_alias["entries"] == [] and plan_alias["retain_all"])
    check("  Custom:D:\\Projects\\tuba-notes → 无删除条目、不移除目录",
          plan_notes["entries"] == [] and plan_notes["retain_all"] and not plan_notes["remove_dir_if_empty"])

    # 9) 祖先链接只影响它自己的子树：AppData\Local 是链接时，{app}\Data 不受影响、默认数据目录被拒
    fs_bad = fs_for(ENV_PF["app"], ENV_PF["app"] + "\\Data", default_pf)
    fs_bad.reparse.add("c:\\users\\tester\\appdata\\local")
    ok_a2, _c, why = TubaReadyToDeleteDir(ENV_PF["app"] + "\\Data", ENV_PF, fs_bad)
    check("  AppData\\Local 是链接 → 不同子树（{app}\\Data）不受影响，仍可删", ok_a2, why)
    ok_d3, _c, why = TubaReadyToDeleteDir(default_pf, ENV_PF, fs_bad)
    check("  AppData\\Local 是链接 → 默认数据目录被删除门拒绝（祖先链接）", not ok_d3, why)
    check("  规范路径本身稳定（%s）" % canon_approot, "tubawinui3\\data" in canon_approot)


def part2e_log_identity() -> None:
    print("\n[2e] 【缺口③】卸载日志身份检查（内存字节串；同 AppId + 同模式才放行）")
    ident = {}
    for f in ISS_FILES:
        ident[f] = iss_self_identity(f)
        print("      %-20s 日志标记=%-34s AppId=%s" % (f, ident[f][0], ident[f][1]))
    tags = {f: ident[f][0] for f in ISS_FILES}
    ids = {f: ident[f][1] for f in ISS_FILES}
    check("四份脚本的日志标记随安装模式不同（x86 = 32 位模式，其余 = 64 位模式）",
          tags["installer-x86.iss"] == LOG_TAG_32 and tags["installer.iss"] == LOG_TAG_64
          and tags["installer-x64.iss"] == LOG_TAG_64 and tags["installer-arm64.iss"] == LOG_TAG_64,
          repr(tags))
    check("四份脚本使用同一枕星产品 AppId（安装模式另行校验）",
          len(set(ids.values())) == 1,
          repr(ids))
    check("installer.iss 与 installer-x64.iss 共用同一 AppId + 同一模式（同一次 64 位安装身份）",
          ids["installer.iss"] == ids["installer-x64.iss"] and tags["installer.iss"] == tags["installer-x64.iss"])

    x64_tag, x64_id = ident["installer-x64.iss"]
    arm_tag, arm_id = ident["installer-arm64.iss"]
    x86_tag, x86_id = ident["installer-x86.iss"]
    legacy_arm_id = "{DA3D64F4-winui3-Tuba-arm64-2025}"
    legacy_x86_id = "{DA3D64F4-winui3-Tuba-x86-2025}"
    legacy_x64_id = "{DA3D64F4-winui3-Tuba-2025}"
    other_id = "{11111111-2222-3333-4444-555555555555}"

    ok = make_log_header(x64_tag, x64_id)
    cases = [
        ("没有日志", {}, "ok"),
        ("一份本身份日志（x64）", {"unins000.dat": ok}, "ok"),
        ("枕星 ARM64 同产品同64位模式日志", {"unins000.dat": make_log_header(arm_tag, arm_id)}, "ok"),
        ("原 CE x64 日志", {"unins000.dat": make_log_header(x64_tag, legacy_x64_id)}, "foreign"),
        ("一份本身份日志（大小写不同）", {"unins000.dat": make_log_header(x64_tag.upper(), x64_id.upper())}, "ok"),
        ("一份本身份日志（字段紧凑布局，无 64 字节块）", {"unins000.dat": make_log_header(x64_tag, x64_id, block=0)}, "ok"),
        ("旧 arm64 AppId 的日志（同一目录）", {"unins000.dat": make_log_header(arm_tag, legacy_arm_id)}, "foreign"),
        ("旧 x86 AppId 的日志（同一目录）", {"unins000.dat": make_log_header(x86_tag, legacy_x86_id)}, "foreign"),
        ("其它应用的 Inno 日志", {"unins000.dat": make_log_header(LOG_TAG_64, other_id)}, "foreign"),
        ("身份都对但模式不同（32 位模式日志 ← 64 位安装器）", {"unins000.dat": make_log_header(LOG_TAG_32, x64_id)}, "mismatch"),
        ("不是 Inno 的日志（头部不认识）", {"unins000.dat": b"\x00\x01binary-junk" * 8}, "unconfirmed"),
        ("Inno 日志但格式标记更新（(c)）", {"unins000.dat": make_log_header("Inno Setup Uninstall Log (c)", x64_id)}, "mismatch"),
        ("Inno 日志但格式标记更新且 AppId 也不认识", {"unins000.dat": make_log_header("Inno Setup Uninstall Log (c)", other_id)}, "foreign"),
        ("日志读不出（LoadStringFromFile 失败）", {"unins000.dat": None}, "unconfirmed"),
        ("头部只有格式标记（AppId 缺失）", {"unins000.dat": make_log_header(x64_tag, "")}, "unconfirmed"),
        ("两份都确认是本身份", {"unins000.dat": ok, "unins001.dat": ok}, "multi"),
        ("一份本身份 + 一份其它应用", {"unins000.dat": ok, "unins001.dat": make_log_header(LOG_TAG_64, other_id)}, "foreign"),
        ("一份本身份 + 一份读不出", {"unins000.dat": ok, "unins001.dat": None}, "unconfirmed"),
        ("一份本身份 + 一份模式不同", {"unins000.dat": ok, "unins001.dat": make_log_header(LOG_TAG_32, x64_id)}, "mismatch"),
        ("旧 arm64 日志 + 其它应用日志", {"unins000.dat": make_log_header(arm_tag, legacy_arm_id),
                                          "unins001.dat": make_log_header(LOG_TAG_64, other_id)}, "foreign"),
    ]
    for name, logs, expect in cases:
        status, detail = TubaCheckUninstallLogs(logs, x64_tag, x64_id)
        check("  %-48s → %s（%s）" % (name, status, detail), status == expect,
              "期望 %s，实际 %s" % (expect, status))

    # 判定语义：只有 'ok' 放行；'foreign'（AppId 不同 = 另一个应用）直接中止；
    # 'mismatch' / 'unconfirmed' / 'multi' 走默认中止（默认按钮「否」）
    check("  只有 'ok' 放行直接覆盖", TubaCheckUninstallLogs({"unins000.dat": ok}, x64_tag, x64_id)[0] == "ok")
    check("  AppId 不同（其它应用）走「直接中止」而不是「默认中止」（用户点「继续」也不该放行）",
          TubaCheckUninstallLogs({"unins000.dat": make_log_header(LOG_TAG_64, other_id)}, x64_tag, x64_id)[0] == "foreign")
    check("  同 AppId 但模式不同走「默认中止」（可显式确认）",
          TubaCheckUninstallLogs({"unins000.dat": make_log_header(LOG_TAG_32, x64_id)}, x64_tag, x64_id)[0] == "mismatch")

    # 「份数」不是验证：单份但身份不符 → 旧「数份数」逻辑放行，新逻辑直接中止
    single_foreign = {"unins000.dat": make_log_header(arm_tag, legacy_arm_id)}
    check("  反例：单份（旧「数份数」逻辑会放行）但 AppId 是 arm64 → 新逻辑 foreign 中止",
          len(single_foreign) == 1 and TubaCheckUninstallLogs(single_foreign, x64_tag, x64_id)[0] == "foreign")
    # 反过来：多份但身份都对 → 仍要默认中止（覆盖只会替换其中一份）
    check("  反例：多份但身份都确认 → 仍默认中止（multi）",
          TubaCheckUninstallLogs({"unins000.dat": ok, "unins001.dat": ok}, x64_tag, x64_id)[0] == "multi")
    # 头部解析边界：字段只在头 128 字节内读取（格式 (b) 的两个 64 字节块）
    check("  头部解析限定在前 128 字节（不把后续数据当成 AppId）",
          TubaReadLogIdentity(make_log_header(x64_tag, "") + b"notappid") == (False, x64_tag, ""))


def part3_static() -> None:
    print("\n[3] 四份 .iss 静态断言")
    appids = {
        "installer.iss": "AppId={{92B08A5C-463A-45B8-B03A-875D9C348180}",
        "installer-x64.iss": "AppId={{92B08A5C-463A-45B8-B03A-875D9C348180}",
        "installer-arm64.iss": "AppId={{92B08A5C-463A-45B8-B03A-875D9C348180}",
        "installer-x86.iss": "AppId={{92B08A5C-463A-45B8-B03A-875D9C348180}",
    }
    blocks = {}
    pre_regions = {}
    for f in ISS_FILES:
        p = os.path.join(REPO, f)
        if not os.path.exists(p):
            check("%s 存在" % f, False, p)
            continue
        t = read_iss(f)
        nv = noncomment_view(t)
        check("%s 存在且为 CRLF 文本" % f, "\r\n" in t and t.count("\n") == t.count("\r\n"))
        check(
            "%s AppId 为独立枕星身份（%s）" % (f, appids[f]),
            any(ln.strip() == appids[f] for ln in t.splitlines()),
        )

        check("%s 不读取原 CE 卸载注册表路径" % f, "DA3D64F4" not in nv)
        check("%s 默认安装目录独立于原 CE" % f,
              "DefaultDirName={autopf}\\Zhenxing AI Assistant" in nv)

        # 1) 覆盖升级：官方 overwrite 策略（注释里提到不算）
        check(
            "%s [Setup] 含 UninstallLogMode=overwrite（恰好一次）" % f,
            nv.count("UninstallLogMode=overwrite") == 1,
            "计数 %d" % nv.count("UninstallLogMode=overwrite"),
        )
        check("%s 未使用 UninstallLogMode=append/new" % f, not re.search(r"UninstallLogMode=(append|new)\b", nv))

        # 2) 不再有整根删除
        check("%s 无 filesandordirs（注释除外）" % f, "filesandordirs" not in nv)
        m_ud = re.search(r"\[UninstallDelete\]\r?\n([\s\S]*?)(?=\r?\n\[)", t)
        ud_lines = [
            ln.strip()
            for ln in (m_ud.group(1) if m_ud else "").splitlines()
            if ln.strip() and not ln.strip().startswith(";")
        ]
        check(
            "%s [UninstallDelete] 仅剩安装器自有标记文件（%r）" % (f, ud_lines),
            ud_lines == ['Type: files; Name: "{app}\\.installed"'],
            "实际内容 %r" % ud_lines,
        )

        # 3) 【缺口③】日志身份检查：读日志头部 + 编译期取 AppId + 两种中止方式
        for need in [
            "function TubaNextLogField",
            "function TubaReadLogIdentity",
            "function TubaSelfLogTag",
            "function TubaSelfAppId",
            "function TubaCheckUninstallLogs",
            "function TubaConfirmRiskyOverwrite",
            "'{#SetupSetting(\"AppId\")}'",
            "'Inno Setup Uninstall Log (b)'",
            "'Inno Setup Uninstall Log (b) 64-bit'",
            "Is64BitInstallMode",
            "TubaCheckUninstallLogs(ExpandConstant('{app}'))",
            "MB_YESNO or MB_DEFBUTTON2, IDNO",
        ]:
            check("%s 含 %s" % (f, need), need in t)
        check("%s 不再只靠「日志份数」判定（无 LogCount / CountUninstallLogFiles）" % f,
              "LogCount" not in t and "CountUninstallLogFiles" not in t)
        check("%s 日志头部只在两个 64 字节块内解析（64 / 128 上限）" % f,
              "TubaNextLogField(Data, At, 64)" in t and "TubaNextLogField(Data, At, 128)" in t)
        check("%s AppId 不一致（其它应用）时不提供「继续安装」的对话框" % f,
              re.search(r"if Foreign > 0 then\r?\n  begin\r?\n    Result := '目标安装目录中存在【其它应用或其它架构】", t) is not None)
        check("%s 同 AppId 但模式/架构不同时走「默认中止」对话框" % f,
              re.search(r"if Mismatch > 0 then\r?\n  begin\r?\n    Result := TubaConfirmRiskyOverwrite", t) is not None)
        check("%s 无法确认身份与多份日志都走「默认中止」对话框" % f,
              re.search(r"if Unreadable > 0 then\r?\n  begin\r?\n    Result := TubaConfirmRiskyOverwrite", t) is not None
              and re.search(r"if Found > 1 then\r?\n  begin\r?\n    Result := TubaConfirmRiskyOverwrite", t) is not None)
        check("%s 身份判定顺序：先 AppId、再模式标记（AppId 相同才算同应用）" % f,
              re.search(r"if CompareText\(AppId, SelfId\) <> 0 then[\s\S]*?else if CompareText\(Tag, SelfTag\) = 0 then", t) is not None)

        # 4) 【缺口②】路径链 reparse 检查必须在删除门里
        for fn in [
            "function TubaResolvePath",
            "procedure TubaApplySegment",
            "function TubaCanonAppDir",
            "function TubaExpandKnownPlaceholders",
            "function TubaIsProtectedUninstallPath",
            "function TubaIsSafeUninstallDataDir",
            "function TubaIsReparsePoint",
            "function TubaAnyReparseInPath",
            "function TubaReadyToDeleteDir",
            "function TubaIsStrictlyInsideAppDir",
        ]:
            check("%s 含 %s" % (f, fn), fn in t)
        check("%s TubaReadyToDeleteDir 内：先边界检查、再逐级 reparse、最后才允许删除" % f,
              re.search(r"function TubaReadyToDeleteDir[\s\S]*?TubaIsSafeUninstallDataDir\(Canon, Why\)[\s\S]*?TubaAnyReparseInPath\(Canon, Why\)[\s\S]*?Result := True;", t) is not None)
        check("%s TubaAnyReparseInPath 逐级探测（含盘符根；UNC 只探测共享根之下）" % f,
              "function TubaAnyReparseInPath" in t and "Length(Prefix) >= 3" in t
              and "Norm := Prefix;" in t and "TubaCountChar(Norm, '\\') >= 4" in t
              and "TubaIsReparsePoint(Prefix)" in t)
        check("%s TubaIsReparsePoint 查询失败时保守当作链接" % f,
              re.search(r"function TubaIsReparsePoint[\s\S]*?Result := True;[\s\S]*?FindFirst", t) is not None)

        # 5) 【缺口①】不再有任何「按文件名删除」的机制
        code = strip_iss_code(t[t.find("[Code]"):])
        for gone in [
            "TubaIsProductNamedDir",
            "TubaDeleteOwnedFile",
            "TubaDeleteOwnedDir",
            "TubaDeleteOwnedEntries",
            "IncludeGenericNames",
            "RemoveDir(",
            "TubaIsFixedDirPlausible",
        ]:
            check("%s 已移除 %s" % (f, gone), gone not in t)
        del_names = LEGACY3_UNIQUE_FILES + LEGACY3_UNIQUE_DIRS + LEGACY3_GENERIC_FILES + LEGACY3_GENERIC_DIRS
        still_used = [n for n in del_names if ("'%s'" % n) in t]
        check("%s [Code] 不再出现任何名单条目名（%d 个名字已全部不再是删除依据）" % (f, len(del_names)),
              not still_used, "仍出现 %r" % still_used)
        check("%s [Code] 无 DeleteFile 调用，DelTree 恰好 2 处（都在删除门之后）" % f,
              "DeleteFile(" not in code and code.count("DelTree(") == 2, "DelTree 计数 %d" % code.count("DelTree("))

        # 6) 每个 DelTree 之前都必须经过删除门（静态近似检查）
        body = t[t.index("procedure TubaDeleteUserData"):]
        dt_hits = [m.start() for m in re.finditer(r"DelTree\(", body)]
        guarded = all("TubaReadyToDeleteDir(" in body[max(0, i - 400):i] for i in dt_hits)
        check("%s 每个 DelTree 都由 TubaReadyToDeleteDir 把关（%d 处）" % (f, len(dt_hits)),
              len(dt_hits) == 2 and guarded)
        check("%s 未对标记文件里的自定义路径直接 DelTree" % f,
              not re.search(r"DelTree\(\s*(UninstallCustomDataDir|CustomDir|Canon)", t))

        # 7) 自定义目录分支里没有任何删除调用
        custom_part = t[t.index("// 3) 自定义数据目录（标记文件里的 Custom:<路径>）：【缺口①】"):]
        custom_code = strip_iss_code(custom_part)
        check("%s 自定义目录分支内无任何删除调用（DelTree/DeleteFile/RemoveDir）" % f,
              not any(k in custom_code for k in ("DelTree(", "DeleteFile(", "RemoveDir(")))
        check("%s 自定义目录分支明确「完整保留」" % f, "完整保留" in custom_part)
        check("%s 卸载提示语说明自定义目录会完整保留" % f, "该目录内的内容会完整保留" in t)

        # 8) 结构性 lint：begin/end 与括号配平（先去字符串/注释）
        nb = len(re.findall(r"\bbegin\b", code))
        ne = len(re.findall(r"\bend\b", code))
        check("%s [Code] begin/end 配平（%d/%d）" % (f, nb, ne), nb == ne)
        check("%s [Code] 圆括号配平（%d/%d）" % (f, code.count("("), code.count(")")), code.count("(") == code.count(")"))

        # 9) 【A08】检查段与卸载块四份一致
        m = re.search(r"// === \[A08\] 卸载[\s\S]*$", t)
        check("%s 含统一卸载块标记头" % f, m is not None)
        if m:
            blocks[f] = hashlib.sha256(m.group(0).encode("utf-8")).hexdigest()
        m_pre = re.search(r"// \[A08\] 覆盖升级前的历史卸载日志检查[\s\S]*?(?=// === \[A08\] 卸载)", t)
        check("%s 含 [A08] 日志身份检查段头" % f, m_pre is not None)
        if m_pre:
            pre_regions[f] = hashlib.sha256(m_pre.group(0).encode("utf-8")).hexdigest()

    check("四份脚本卸载块逐字节一致（同一份逻辑）",
          len(set(blocks.values())) == 1 and len(blocks) == len(ISS_FILES), repr(blocks))
    check("四份脚本 [A08] 日志身份检查段逐字节一致",
          len(set(pre_regions.values())) == 1 and len(pre_regions) == len(ISS_FILES), repr(pre_regions))


def main() -> int:
    print("A08 第四批 · 卸载数据删除路径安全 + 覆盖升级日志身份：隔离测试（不执行卸载、不触碰真实路径）")
    part1_legacy_repro()
    part1b_gap_repro()
    part2_table()
    part2d_reparse_chain()
    part2e_log_identity()
    part3_static()
    print("\n==== 汇总：%d 项检查，失败 %d 项 ====" % (CHECKS, len(FAILURES)))
    if FAILURES:
        for x in FAILURES:
            print("  - " + x)
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
