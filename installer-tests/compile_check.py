#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""A08：用真实 Inno Setup「console 编译器 ISCC.exe」对四份 installer*.iss 做桩编译检查。

【为什么必须是 ISCC.exe】
官方文档（jrsoftware.org/ishelp/topic_compilercmdline.htm）：
  · `iscc [options] <script>`  是 console-mode 编译器，无任何窗口；exit 0=成功 / 1=参数或内部错误 / 2=编译失败。
  · `iside -cc` / `compil32 /cc` 是「Compiler IDE 从命令行编译」——**不抑制正常的进度显示与错误信息**，
    即会弹出 Compiler Error 对话框（历史上曾因此给用户制造误报弹窗）。
本脚本【绝不】调用 iside -cc / compil32；找不到 ISCC.exe 时明确报告「缺少控制台编译器」，exit=3，
不做任何编译、不生成任何可交互窗口。

安全纪律：只编译，不安装、不卸载、不调用任何卸载器；[Files] 的源被替换成只有一个文本文件的桩目录，
编译产物与桩脚本全部落在「本次专属随机临时目录」；默认结束时仅清理该目录（--keep 保留供人工查看）；
超时只终止本脚本自己启动的编译进程。

用法：
    python installer-tests/compile_check.py                 # 正向：四份桩编译（默认）
    python installer-tests/compile_check.py --negative      # 追加负控：注入语法错误必须被编译器识别
    python installer-tests/compile_check.py --keep          # 保留临时目录供人工检查
环境变量：
    TUBA_ISCC   指定 ISCC.exe 路径（缺省时按 PATH 与常见安装位置查找）
    TUBA_REPO   仓库根（默认 D:\\tubatools）
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile

REPO = os.environ.get("TUBA_REPO", r"D:\tubatools")
ISS_FILES = ["installer.iss", "installer-x64.iss", "installer-arm64.iss", "installer-x86.iss"]
TIMEOUT = 300  # 单份脚本编译超时（秒）

# console 编译器查找顺序（显式 env > PATH > 常见安装位置）
ISCC_CANDIDATES = [
    os.path.join(r"D:\AI\zhenxingAI-toolchains", "inno-7.1.0-full", "ISCC.exe"),
    os.path.join(r"D:\AI\zhenxingAI-toolchains", "inno-7.1.0-runtime", "ISCC.exe"),
    r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    r"C:\Program Files\Inno Setup 6\ISCC.exe",
    r"C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    r"C:\Program Files\Inno Setup 7\ISCC.exe",
]

# 负向对照：注入一个 Pascal 语法错误，编译器必须在「无 UI」下拒绝并给出可识别的错误信息。
BAD_INJECT_FROM = "  if not DirExists(Dir) then Exit;"
BAD_INJECT_TO = "  if not DirExists(Dir) then Exit"


# 已知 GUI 编译器（`-cc` 会弹出 Compiler Error 对话框）——按文件名先拒绝，绝不运行探测。
GUI_COMPILER_NAMES = {"iside.exe", "compil32.exe"}
IMAGE_SUBSYSTEM_WINDOWS_CUI = 3  # 控制台子系统


def _pe_subsystem(path: str) -> int | None:
    """读取 PE OptionalHeader.Subsystem（3=Console，2=GUI）。纯文件读取，不执行目标。"""
    try:
        with open(path, "rb") as fh:
            if fh.read(2) != b"MZ":
                return None
            fh.seek(0x3C)
            e_lfanew = int.from_bytes(fh.read(4), "little")
            fh.seek(e_lfanew)
            if fh.read(4) != b"PE\x00\x00":
                return None
            # OptionalHeader 起点 = PE签名(+4) + COFF头(20) = e_lfanew + 24；
            # Subsystem 位于 OptionalHeader +68（PE32/PE32+ 两格式此相对偏移一致）。
            fh.seek(e_lfanew + 24 + 68)
            return int.from_bytes(fh.read(2), "little")
    except OSError:
        return None


def validate_iscc(path: str) -> tuple[bool, str]:
    """执行前的静态校验（不运行任何进程）：存在、是 PE、Subsystem=Console、且非已知 GUI 编译器。"""
    if not path:
        return False, "路径为空"
    if not os.path.isfile(path):
        return False, "路径不存在或不是文件"
    name = os.path.basename(path).lower()
    if name in GUI_COMPILER_NAMES:
        return False, "这是 GUI 编译器（运行 -cc 会弹出 Compiler Error 对话框），本脚本拒绝使用"
    sub = _pe_subsystem(path)
    if sub is None:
        return False, "不是有效的 PE 可执行文件"
    if sub != IMAGE_SUBSYSTEM_WINDOWS_CUI:
        return False, "不是控制台程序（PE Subsystem=%d，控制台应为 3）" % sub
    return True, ""


def resolve_iscc() -> tuple[str | None, list[str]]:
    """解析可用的 ISCC。返回 (路径, 诊断列表)。显式 TUBA_ISCC 无效时【不静默回退】——清晰退出。"""
    diags: list[str] = []
    explicit = (os.environ.get("TUBA_ISCC") or "").strip()
    if explicit:
        ok, why = validate_iscc(explicit)
        if ok:
            return explicit, diags
        diags.append("TUBA_ISCC 显式指定的路径不可用：%s（%s）" % (explicit, why))
        return None, diags  # 显式配置错误：无 UI 清晰退出，不猜、不回退
    hit = shutil.which("iscc") or shutil.which("ISCC")
    if hit:
        ok, why = validate_iscc(hit)
        if ok:
            return hit, diags
        diags.append("跳过 PATH 中的 iscc（%s）：%s" % (hit, why))
    for cand in ISCC_CANDIDATES:
        if not os.path.isfile(cand):
            continue
        ok, why = validate_iscc(cand)
        if ok:
            return cand, diags
        diags.append("跳过 %s：%s" % (cand, why))
    return None, diags


def build_stub_dir(base: str) -> list[tuple[str, str, bool]]:
    """在专属临时目录生成四份桩脚本（+ 可选负控）。返回 (label, script_path, is_negative)。"""
    os.makedirs(os.path.join(base, "stub_payload"))
    with open(os.path.join(base, "stub_payload", "test.txt"), "w", encoding="utf-8") as fh:
        fh.write("stub payload\n")
    shutil.copy(os.path.join(REPO, "ChineseSimplified.isl"), base)
    shutil.copy(os.path.join(REPO, "License.txt"), base)
    assets = os.path.join(base, "TubaWinUi3.WinUI3", "Assets")
    os.makedirs(assets)
    shutil.copy(os.path.join(REPO, "TubaWinUi3.WinUI3", "Assets", "AppIcon.ico"), assets)

    src_line = ('Source: "stub_payload\\*"; DestDir: "{app}"; '
                "Flags: ignoreversion recursesubdirs createallsubdirs")
    scripts: list[tuple[str, str, bool]] = []
    for f in ISS_FILES:
        text = open(os.path.join(REPO, f), encoding="utf-8-sig", newline="").read()
        text = re.sub(r"(?m)^Source: .*$", lambda m: src_line, text)
        text = re.sub(r"(?m)^OutputDir=.*$", lambda m: "OutputDir=" + base, text)
        text = re.sub(r"(?m)^OutputBaseFilename=.*$",
                      lambda m: "OutputBaseFilename=stub_" + f.replace(".iss", ""), text)
        out = os.path.join(base, "stub-" + f)
        with open(out, "w", encoding="utf-8", newline="") as fh:
            fh.write(text)
        scripts.append((f, out, False))
    return scripts


def make_negative_script(base: str, first_script: str) -> tuple[str, str, bool]:
    text = open(first_script, encoding="utf-8").read()
    bad = text.replace(BAD_INJECT_FROM, BAD_INJECT_TO, 1)
    assert bad != text, "负向对照注入失败（锚点不存在）"
    bad = bad.replace("OutputBaseFilename=stub_installer", "OutputBaseFilename=stub_bad")
    bad_path = os.path.join(base, "stub-bad.iss")
    with open(bad_path, "w", encoding="utf-8", newline="") as fh:
        fh.write(bad)
    return ("<negative control: injected Pascal error>", bad_path, True)


def run_iscc(iscc: str, script: str) -> tuple[int | None, str, str]:
    """运行 console 编译器：捕获 stdout/stderr/exit code；超时仅终止本次启动的进程。"""
    creationflags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    with subprocess.Popen(
        [iscc, "/Qp", script],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=creationflags,
    ) as proc:
        try:
            out, err = proc.communicate(timeout=TIMEOUT)
        except subprocess.TimeoutExpired:
            proc.kill()  # 只杀自己启动的进程
            out, err = proc.communicate()
            return None, out, err
        return proc.returncode, out, err


def produced_exe(script: str) -> str | None:
    m = re.search(r"(?m)^OutputBaseFilename=(.*)$", open(script, encoding="utf-8").read())
    if not m:
        return None
    # 桩脚本的 OutputDir 被替换成临时目录
    d = re.search(r"(?m)^OutputDir=(.*)$", open(script, encoding="utf-8").read())
    out_dir = d.group(1).strip() if d else os.path.dirname(script)
    cand = os.path.join(out_dir, m.group(1).strip() + ".exe")
    return cand if os.path.exists(cand) else None


def main() -> int:
    keep = "--keep" in sys.argv
    with_negative = "--negative" in sys.argv

    iscc, diags = resolve_iscc()
    for d in diags:
        print("[诊断] %s" % d)
    if not iscc:
        print("没有可用的控制台编译器 ISCC.exe —— 本脚本不会回退到 GUI 编译器")
        print("（iside -cc / compil32 /cc 会弹出 Compiler Error 对话框）。【未启动任何编译进程。】")
        print("请安装 Inno Setup 6/7（自带 ISCC.exe），或用环境变量 TUBA_ISCC 指向其路径。")
        print("查找位置依次为：TUBA_ISCC → PATH → 以下常见路径：")
        for cand in ISCC_CANDIDATES:
            print("  - %s" % cand)
        return 3

    base = tempfile.mkdtemp(prefix="tuba-a08-compile-")  # 专属随机临时目录
    print("ISCC: %s" % iscc)
    print("桩编译目录（本次专属）: %s\n" % base)
    failures = 0
    try:
        scripts = build_stub_dir(base)
        if with_negative:
            scripts.append(make_negative_script(base, scripts[0][1]))
        else:
            print("（负向对照未启用；显式传 --negative 才会运行）\n")

        for label, script, is_negative in scripts:
            rc, out, err = run_iscc(iscc, script)
            exe = produced_exe(script)   # 无条件查磁盘：负控同样要确认【没有残留产物】
            blob = out + "\n" + err
            if is_negative:
                # 负控证据链（全部为硬断言）：
                # ① 退出码 = 2（编译失败）② 磁盘上无 stub_bad.exe
                # ③ 精确诊断短语 "Semicolon (';') expected"（不允许只匹配 semicolon|expected 任一词）
                # ④ 错误信息带行号+列号定位（Error on line N ... Column M）
                rc_ok = rc == 2
                no_artifact = exe is None
                exact = "Semicolon (';') expected" in blob
                has_pos = re.search(r"Error on line \d+ .*Column \d+", blob) is not None
                ok = rc_ok and no_artifact and exact and has_pos
                print("      负控证据链: rc=2 [%s] 无残留产物 [%s] 精确诊断 [%s] 行号定位 [%s]"
                      % ("OK" if rc_ok else "FAIL", "OK" if no_artifact else "FAIL",
                         "OK" if exact else "FAIL", "OK" if has_pos else "FAIL"))
            else:
                ok = (rc == 0) and (exe is not None)
            if not ok:
                failures += 1
            size = os.path.getsize(exe) if exe else 0
            print("  [%s] %-46s exit=%s exe=%s%s"
                  % ("PASS" if ok else "FAIL", label, "timeout" if rc is None else rc,
                     os.path.basename(exe) if exe else "-",
                     (" size=%d" % size) if exe else ""))
            if not ok or is_negative:
                # 失败/负控：输出真实编译器文本（正向成功时只留一行摘要）
                print("    ---- 编译器输出 ----")
                for line in (out + err).splitlines():
                    print("    | " + line)
                print("    --------------------")
        print("\n==== 编译器检查：%d 项，失败 %d 项 ====" % (len(scripts), failures))
        return 1 if failures else 0
    finally:
        if keep:
            print("产物保留在: %s" % base)
        else:
            shutil.rmtree(base, ignore_errors=True)  # 只清理本次自己创建的随机目录
            print("本次临时目录已清理（--keep 可保留）")


if __name__ == "__main__":
    sys.exit(main())
