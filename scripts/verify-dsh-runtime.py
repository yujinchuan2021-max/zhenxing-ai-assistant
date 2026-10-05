#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZXAI A16: offline smoke test for the app-private dsh runtime (staging candidate).

What it proves (automated evidence, no model requests):
  * the runtime starts using ONLY the bundled `<runtime>/node.exe` + `<runtime>/dsh/lib/bin.js`
    (absolute paths; PATH restricted to the runtime dir so no system/Hermes dsh can be picked up);
  * the version lock in `<runtime>/versions.json` matches the REAL files (node.exe --version and
    dsh/package.json) and declares `failClosed: true`; anything else is a hard failure;
  * a FRESH throwaway DSH_HOME (never the user profile, never ~/.dsh) is sufficient:
    the built-in acp profile self-initializes offline;
  * the ACP protocol works end to end WITHOUT sending any prompt:
    initialize -> session/new -> [fresh process] initialize -> session/resume -> session/cancel -> dispose
    (the cancel step is sent on an IDLE session -- idle cancel only; in-flight cancel semantics are
    covered by the fake-ACP tests, not by this smoke);
  * every spawned child is reclaimed before exit (stdin EOF -> bounded wait -> `taskkill /F /T`
    tree kill -> TerminateProcess); each child's pid / exit code / kill flag is reported;
  * the profile dependency closure is materialized inside the runtime install
    (`<DSH_HOME>/profiles/node_modules` = healProfilesModuleFallback / INSTALL_ANCHOR behaviour):
    every entry must resolve into `<runtime>/dsh/node_modules/...` (junction / symlink / ESM proxy),
    the ACP stack must be present through that closure with versions matching the runtime install,
    and nothing may be resolved from the user's `~/.dsh` (no user module mirror is copied);
  * the throwaway DSH_HOME + session cwd are deleted WITHOUT following reparse points: the closure
    entries are junctions into the runtime install, and on Windows `os.path.islink()` is False for
    a junction while `is_dir(follow_symlinks=False)` is True (measured) -- so the removal pass
    strips reparse points explicitly first, then the runtime closure is re-counted and must be
    unchanged (a naive walk could delete the shipped dependencies themselves).

Telemetry is explicitly disabled for every child (`DSH_TELEMETRY_DISABLED=1`; dsh treats ANY
non-empty value as opt-out -- lib/profile-boot `resolveTelemetryPatch`). The API key is an invalid
placeholder; no real key is read; nothing is sent to any provider.

Usage:
    python scripts/verify-dsh-runtime.py [runtime_dir] [--report PATH]

--report writes a machine-readable run report (schema zxai-dsh-smoke/1) that the staging packer
embeds into BUILD-INFO.json. It is written on success AND on failure (`ok: false`).

Exit code: 0 all steps passed / 1 any step failed (runtime dir incomplete counts as a failed step).
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time

DEFAULT_RUNTIME = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..",
    "TubaWinUi3.WinUI3", "artifacts", "remediation-2026-09-21", "staging", "app", "runtime")

_ap = argparse.ArgumentParser(description="ZXAI A16 offline dsh runtime smoke test")
_ap.add_argument("runtime_dir", nargs="?", default=DEFAULT_RUNTIME,
                 help="app-private runtime dir (contains node.exe + dsh/)")
_ap.add_argument("--report", default=None, help="write a JSON run report to this path")
_args = _ap.parse_args()

RUNTIME = os.path.abspath(_args.runtime_dir)
REPORT_PATH = os.path.abspath(_args.report) if _args.report else None
NODE = os.path.join(RUNTIME, "node.exe")
BIN = os.path.join(RUNTIME, "dsh", "lib", "bin.js")
DSH_ROOT = os.path.join(RUNTIME, "dsh")
DSH_NODE_MODULES = os.path.join(DSH_ROOT, "node_modules")
VERSIONS_JSON = os.path.join(RUNTIME, "versions.json")

STEP_TIMEOUT = 90  # 首次 profile 初始化可能较慢

# profiles 闭包断言：必须能解析到 runtime 内 dsh 依赖的包（ACP 链路关键项）
REQUIRED_CLOSURE_PACKAGES = (
    "@deepseek-ai/dsh",
    "@deepseek-ai/dsh-acp",
    "@agentclientprotocol/sdk",
)
MIN_CLOSURE_ENTRIES = 50      # 闭包规模下限（真实闭包数百项；空/残缺闭包必须失败）
FILE_ATTRIBUTE_REPARSE_POINT = 0x400

# ── 运行报告状态（成功/失败都会 flush 到 --report）─────────────────────────────
_STATE: dict = {"schema": "zxai-dsh-smoke/1", "ok": False, "steps": [], "failure": None}
_FLUSHED = {"done": False}
_PROCS: list = []             # 本次启动的全部子进程（finally 里统一回收）


def now() -> str:
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run_soft(cmd, timeout: int = 60):
    """软执行（不因失败退出）：返回 (exitCode, stdout+stderr)。

    显式 encoding+errors：中文 Windows 上 taskkill/node 的本地化输出不是 UTF-8，
    默认解码会在读取线程里抛 UnicodeDecodeError（实测）。
    """
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=timeout,
                           encoding="utf-8", errors="replace",
                           creationflags=subprocess.CREATE_NO_WINDOW)
        return r.returncode, ((r.stdout or "") + (r.stderr or "")).strip()
    except Exception as ex:
        return -1, "unavailable: %s" % ex


def flush_report() -> None:
    """把本次运行的状态写成 --report 指定的 JSON（成功与失败都写；只反映本次运行）。"""
    if REPORT_PATH is None or _FLUSHED["done"]:
        return
    _FLUSHED["done"] = True
    doc = dict(_STATE)
    doc["runtimeDir"] = RUNTIME
    doc["reportPath"] = REPORT_PATH
    doc["finishedAt"] = now()
    if "startEpoch" in doc:
        doc["durationSeconds"] = round(time.time() - doc.pop("startEpoch"), 1)
    try:
        with open(REPORT_PATH, "w", encoding="utf-8") as fh:
            json.dump(doc, fh, indent=2, ensure_ascii=False)
            fh.write("\n")
        print("== 运行报告: %s（ok=%s）" % (REPORT_PATH, doc.get("ok")))
    except Exception as ex:   # 报告写不进去不得掩盖原始结论
        print("[WARN] 报告写入失败：%s" % ex)


def fail(msg: str) -> None:
    _STATE["failure"] = msg
    _STATE["ok"] = False
    print("[FAIL] " + msg)
    sys.exit(1)


def ok_step(name: str, detail: str = "") -> None:
    _STATE.setdefault("steps", []).append(name)
    print("[OK] %s%s" % (name, ("（%s）" % detail) if detail else ""))


# ───────────────────── 运行时完整性 / 版本锁定（fail-closed）─────────────────────

def check_runtime() -> None:
    print("runtime dir : %s" % RUNTIME)
    if not os.path.isfile(NODE):
        fail("缺少 <runtime>\\node.exe")
    if not os.path.isfile(BIN):
        fail("缺少 <runtime>\\dsh\\lib\\bin.js")
    if not os.path.isfile(VERSIONS_JSON):
        fail("缺少 <runtime>\\versions.json —— 没有版本锁定清单的 runtime 不是可发布运行时（fail-closed）")
    print("node        : %s" % NODE)
    print("dsh bin.js  : %s" % BIN)
    # 证明 node 属于候选包（版本可读且工作）
    code, out = run_soft([NODE, "--version"], timeout=30)
    if code != 0:
        fail("bundled node.exe 无法运行（--version exit=%d：%s）" % (code, out[:200]))
    node_ver = out.strip().splitlines()[-1].strip() if out.strip() else ""
    if not node_ver.startswith("v"):
        fail("bundled node.exe --version 输出异常：%r" % out[:200])
    print("bundled node version: %s" % node_ver)

    # 版本锁定 vs 实物：清单里写的必须就是这次真正带着的二进制/包
    try:
        versions = json.load(open(VERSIONS_JSON, encoding="utf-8-sig"))
    except Exception as ex:
        fail("versions.json 无法解析：%s" % ex)
    try:
        dsh_pkg = json.load(open(os.path.join(DSH_ROOT, "package.json"), encoding="utf-8-sig"))
    except Exception as ex:
        fail("dsh/package.json 无法解析：%s" % ex)
    dsh_ver = str(dsh_pkg.get("version") or "")
    problems = []
    if not versions.get("failClosed"):
        problems.append("versions.json 未声明 failClosed=true")
    if versions.get("node") != node_ver:
        problems.append("versions.json.node=%r ≠ 实物 node %r" % (versions.get("node"), node_ver))
    if versions.get("lockedNode") != node_ver:
        problems.append("lockedNode=%r ≠ 实物 node %r" % (versions.get("lockedNode"), node_ver))
    if versions.get("dsh") != dsh_ver:
        problems.append("versions.json.dsh=%r ≠ 实物 dsh %r" % (versions.get("dsh"), dsh_ver))
    if versions.get("lockedDsh") != dsh_ver:
        problems.append("lockedDsh=%r ≠ 实物 dsh %r" % (versions.get("lockedDsh"), dsh_ver))
    if problems:
        fail("版本锁定与实物不一致：" + "；".join(problems))
    print("[OK] 版本锁定与实物一致：node %s / dsh %s（failClosed=true）" % (node_ver, dsh_ver))

    _STATE["node"] = {"path": NODE, "version": node_ver, "bytes": os.path.getsize(NODE),
                      "sha256": sha256_file(NODE)}
    _STATE["dsh"] = {"binJs": BIN, "binJsBytes": os.path.getsize(BIN), "binJsSha256": sha256_file(BIN),
                     "version": dsh_ver,
                     "packageJsonSha256": sha256_file(os.path.join(DSH_ROOT, "package.json"))}
    _STATE["versionsJson"] = versions
    _STATE["versionLockCheck"] = {
        "failClosed": True, "nodeMatches": True, "dshMatches": True,
        "lockedNode": versions.get("lockedNode"), "lockedDsh": versions.get("lockedDsh"),
    }


# ───────────────────────── profiles 依赖闭包断言 ─────────────────────────

def _is_reparse_point(path: str) -> bool:
    """junction / 符号链接均为重解析点（Windows 上 junction 不是 islink()）。"""
    try:
        st = os.lstat(path)
    except OSError:
        return False
    return bool(getattr(st, "st_file_attributes", 0) & FILE_ATTRIBUTE_REPARSE_POINT)


def _iter_leaf_packages(modules_dir: str):
    """枚举 profiles/node_modules 下的叶子包（展开 @scope）。"""
    for name in sorted(os.listdir(modules_dir)):
        p = os.path.join(modules_dir, name)
        is_scope = name.startswith("@") and os.path.isdir(p) and not os.path.isfile(os.path.join(p, "package.json"))
        if is_scope:
            for sub in sorted(os.listdir(p)):
                yield "%s/%s" % (name, sub), os.path.join(p, sub)
        else:
            yield name, p


def _resolve_targets(path: str):
    """返回 (form, targets)：包实际解析到的物理路径列表。

    三种形态（dsh-app-boot 的 module fallback）：
      symlink  符号链接              -> realpath 目标
      junction 目录联接（本机常态）  -> realpath 目标
      proxy    pkg 打包形态写入的 ESM 代理目录 -> package.json 的 dsh.moduleFallback.targets
    """
    if os.path.islink(path):
        return "symlink", [os.path.realpath(path)]
    pkg_json = os.path.join(path, "package.json")
    if os.path.isfile(pkg_json):
        try:
            meta = json.load(open(pkg_json, encoding="utf-8"))
        except Exception:
            meta = {}
        targets = ((meta.get("dsh") or {}).get("moduleFallback") or {}).get("targets")
        if isinstance(targets, dict) and targets:
            return "proxy", [os.path.realpath(t) for t in targets.values()]
        if isinstance(targets, list) and targets:
            return "proxy", [os.path.realpath(t) for t in targets]
    if os.path.isdir(path):
        return ("junction" if _is_reparse_point(path) else "dir"), [os.path.realpath(path)]
    return "unknown", []


def check_profiles_closure(home: str) -> int:
    """断言 DSH_HOME 初始化后的 profiles 依赖闭包（healProfilesModuleFallback / INSTALL_ANCHOR）。

    要求：
      * <DSH_HOME>/profiles/node_modules 已生成且规模达标；
      * 每个条目都解析到 <runtime>/dsh/node_modules 内（junction / symlink / proxy 三形态）；
      * 关键 ACP 包（@deepseek-ai/dsh|dsh-acp、@agentclientprotocol/sdk）在闭包内且版本与
        runtime 内安装一致 —— 证明确实来自「runtime 内 dsh 依赖」而不是别处；
      * 闭包不得引用用户 ~/.dsh（不复制用户 profile/module 镜像）。
    """
    profiles = os.path.join(home, "profiles")
    modules = os.path.join(profiles, "node_modules")
    dsh_root = os.path.realpath(DSH_ROOT).lower()   # dsh 安装根（含 node_modules；dsh 自身即此目录）
    user_dsh = os.path.realpath(os.path.join(os.path.expanduser("~"), ".dsh")).lower()

    if not os.path.isdir(profiles):
        fail("profiles 依赖闭包缺失：<DSH_HOME>\\profiles 不存在（%s）——healProfilesModuleFallback 未执行？" % profiles)
    if not os.path.isdir(modules):
        fail("profiles 依赖闭包缺失：<DSH_HOME>\\profiles\\node_modules 不存在（%s）" % modules)

    entries = list(_iter_leaf_packages(modules))
    print("[OK] profiles 依赖闭包生成：%s（%d 个包条目）" % (modules, len(entries)))
    if len(entries) < MIN_CLOSURE_ENTRIES:
        fail("profiles 依赖闭包不完整：仅 %d 个条目（下限 %d）——runtime 内 dsh 依赖未映射到 profiles"
             % (len(entries), MIN_CLOSURE_ENTRIES))

    outside, unknown, from_user, dangling = [], [], [], []
    forms: dict = {}
    for name, path in entries:
        form, targets = _resolve_targets(path)
        forms[form] = forms.get(form, 0) + 1
        if form == "unknown" or not targets:
            unknown.append(name)
            continue
        for t in targets:
            tl = t.lower()
            if not os.path.exists(t):
                dangling.append("%s -> %s" % (name, t))
            if user_dsh and (tl == user_dsh or tl.startswith(user_dsh + os.sep)):
                from_user.append("%s -> %s" % (name, t))
            if not (tl == dsh_root or tl.startswith(dsh_root + os.sep)):
                outside.append("%s -> %s" % (name, t))

    print("  条目形态：%s" % ", ".join("%s=%d" % (k, v) for k, v in sorted(forms.items())))
    if unknown:
        fail("profiles 闭包有 %d 个条目无法解析（既非链接/junction，也无 module fallback 记录）：%s"
             % (len(unknown), ", ".join(unknown[:8])))
    if dangling:
        fail("profiles 闭包存在断链（目标不存在）：%s%s"
             % (", ".join(dangling[:5]), " …" if len(dangling) > 5 else ""))
    if from_user:
        fail("profiles 闭包引用了用户 ~/.dsh（禁止复制用户 profile 模块镜像）：%s" % ", ".join(from_user[:5]))
    if outside:
        fail("profiles 闭包有 %d 个条目未解析到 runtime 内 dsh 安装目录（<runtime>\\dsh）：%s%s"
             % (len(outside), ", ".join(outside[:5]), " …" if len(outside) > 5 else ""))
    print("[OK] 闭包 %d 个条目全部解析到 runtime 内 dsh 安装目录：%s" % (len(entries), DSH_ROOT))
    print("[OK] 未引用用户 ~/.dsh（%s）——用户 profile 模块镜像未被复制"
          % os.path.join(os.path.expanduser("~"), ".dsh"))

    closure_map = dict(entries)
    missing = [p for p in REQUIRED_CLOSURE_PACKAGES if p not in closure_map]
    if missing:
        fail("profiles 闭包缺少关键包：%s" % ", ".join(missing))

    def _runtime_manifest(pkg: str) -> str:
        # @deepseek-ai/dsh 即安装锚点（INSTALL_ANCHOR=<runtime>\dsh\package.json），
        # 其余包在 <runtime>\dsh\node_modules 下
        if pkg == "@deepseek-ai/dsh":
            return os.path.join(DSH_ROOT, "package.json")
        return os.path.join(DSH_NODE_MODULES, *pkg.split("/"), "package.json")

    critical_versions = {}
    bad_ver = []
    for pkg in REQUIRED_CLOSURE_PACKAGES:
        linked_pkg_json = os.path.join(closure_map[pkg], "package.json")
        runtime_pkg_json = _runtime_manifest(pkg)
        if not os.path.isfile(runtime_pkg_json):
            bad_ver.append("%s：runtime 内无 package.json（%s）" % (pkg, runtime_pkg_json))
            continue
        try:
            linked_ver = json.load(open(linked_pkg_json, encoding="utf-8")).get("version")
            runtime_ver = json.load(open(runtime_pkg_json, encoding="utf-8")).get("version")
        except Exception as ex:
            bad_ver.append("%s：读取版本失败 %s" % (pkg, ex))
            continue
        critical_versions[pkg] = runtime_ver
        if linked_ver != runtime_ver:
            bad_ver.append("%s：闭包 %s ≠ runtime %s" % (pkg, linked_ver, runtime_ver))
        else:
            print("  %-28s %s（与 runtime 安装一致）" % (pkg, linked_ver))
    if bad_ver:
        fail("profiles 闭包与 runtime 内 dsh 依赖不一致：%s" % "；".join(bad_ver))

    print("[OK] profiles 依赖闭包满足 healProfilesModuleFallback / INSTALL_ANCHOR 语义（%d 个包）" % len(entries))
    _STATE["closure"] = {"entryCount": len(entries), "forms": forms, "packages": critical_versions,
                         "danglingCount": 0, "unknownCount": 0, "userDshReferenced": False,
                         "resolvedInto": DSH_ROOT}
    return len(entries)


# ───────────────────────── 子进程（含回收契约）─────────────────────────

def _kill_tree(pid: int) -> bool:
    """强杀整棵进程树（`taskkill /F /T`）——只用于优雅退出超时的兜底。"""
    try:
        r = subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)],
                           capture_output=True, timeout=30,
                           encoding="utf-8", errors="replace",
                           creationflags=subprocess.CREATE_NO_WINDOW)
        return r.returncode == 0
    except Exception:
        return False


class AcpProc:
    """One dsh ACP child process with a line-reader thread (Windows-pipe-safe timeouts).

    dispose() is the single exit path -- stdin EOF (graceful) -> bounded wait -> tree kill ->
    TerminateProcess. It is idempotent and always reports pid / exit code / kill flag, so the
    smoke can prove that no child survived the run.
    """

    def __init__(self, home: str, work: str, label: str):
        self.label = label
        env = {
            "DSH_HOME": home,                              # 全新临时 home（不用 ~/.dsh）
            "DEEPSEEK_API_KEY": "«redacted:sk-…»",  # 无效占位，绝不读真实 Key
            "DSH_PERMISSION_MODE": "danger-full-access",
            "DSH_TELEMETRY_DISABLED": "1",                 # 显式禁遥测（dsh：任意非空值 = 关闭）
            "PATH": RUNTIME,                               # 限制 PATH：只允许包内运行时
            "SystemRoot": os.environ.get("SystemRoot", r"C:\Windows"),
            "TEMP": os.environ.get("TEMP", ""),
            "TMP": os.environ.get("TMP", ""),
        }
        self.proc = subprocess.Popen(
            [NODE, BIN, "--profile", "acp"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            env=env, cwd=work, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.lines: "queue.Queue[str]" = queue.Queue()
        self.stderr_buf: list[str] = []
        threading.Thread(target=self._pump, args=(self.proc.stdout, self.lines), daemon=True).start()
        threading.Thread(target=self._pump_err, daemon=True).start()
        self.next_id = 0
        self._record: dict = {"label": label, "pid": self.proc.pid, "exitCode": None,
                              "killed": False, "reclaimed": False, "disposed": False}
        _PROCS.append(self)

    def _pump(self, stream, q: "queue.Queue[str]") -> None:
        try:
            for line in stream:
                q.put(line.rstrip("\r\n"))
        except Exception:
            pass

    def _pump_err(self) -> None:
        try:
            for line in self.proc.stderr:
                self.stderr_buf.append(line.rstrip("\r\n"))
        except Exception:
            pass

    def request(self, method: str, params: dict, timeout: int = STEP_TIMEOUT) -> dict:
        self.next_id += 1
        rid = self.next_id
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}) + "\n")
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while True:
            try:
                line = self.lines.get(timeout=max(0.1, deadline - time.time()))
            except queue.Empty:
                fail("%s 响应超时（%ds；%s 子进程 pid=%d）" % (method, timeout, self.label, self.proc.pid))
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") != rid:
                continue  # 通知或其它消息
            if "error" in msg:
                return {"__error__": msg["error"]}
            return msg.get("result") or {}

    def notify(self, method: str, params: dict) -> None:
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method, "params": params}) + "\n")
        self.proc.stdin.flush()

    def dispose(self, graceful_timeout: int = 15) -> dict:
        """回收子进程（幂等）：stdin EOF → 有界等待 → taskkill /F /T 树杀 → TerminateProcess。"""
        if self._record["disposed"]:
            return self._record
        self._record["disposed"] = True
        try:
            self.proc.stdin.close()          # 关 stdin：子进程读到 EOF 自行退出
        except Exception:
            pass
        try:
            self.proc.wait(timeout=graceful_timeout)
        except subprocess.TimeoutExpired:
            if self.proc.poll() is None:
                self._record["killed"] = _kill_tree(self.proc.pid)
                try:
                    self.proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    try:
                        self.proc.kill()      # TerminateProcess 兜底
                    except Exception:
                        pass
                    try:
                        self.proc.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        pass
                if self.proc.poll() is None and not self._record["killed"]:
                    self._record["killed"] = True
        for stream in (self.proc.stdout, self.proc.stderr):
            try:
                stream.close()               # 关读写端，泵线程收尾
            except Exception:
                pass
        self._record["exitCode"] = self.proc.poll()
        self._record["reclaimed"] = self.proc.poll() is not None
        return self._record

    @property
    def record(self) -> dict:
        return self._record


def reap_all() -> list:
    """finally 兜底：把本次启动的所有子进程全部回收，并返回逐进程记录。"""
    records = []
    for p in _PROCS:
        try:
            records.append(p.dispose())
        except Exception as ex:
            records.append({"label": p.label, "pid": getattr(p.proc, "pid", None),
                            "reclaimed": False, "disposed": False, "error": str(ex)})
    for r in records:
        print("[OK] 子进程回收：%s pid=%s exit=%s killed=%s reclaimed=%s"
              % (r.get("label"), r.get("pid"), r.get("exitCode"), r.get("killed"), r.get("reclaimed")))
    alive = [r for r in records if not r.get("reclaimed")]
    if alive:
        print("[FAIL] 子进程未被回收：%s" % json.dumps(alive, ensure_ascii=False))
    return records


# ───────────────────── 临时目录清理（绝不跟随重解析点）─────────────────────

def _strip_reparse_points(root: str) -> None:
    """先摘除树内所有重解析点（junction / symlink）。

    Windows 上 junction 不是 islink()，而 `is_dir(follow_symlinks=False)` 是 True（实测），
    即 os.walk/rmtree 都可能把它当普通目录下钻 —— 在 DSH_HOME 里那意味着顺着数百个 junction
    走进 runtime 的依赖目录。这里显式先摘链路（os.rmdir 只删 reparse point 本身）。
    """
    try:
        entries = list(os.scandir(root))
    except OSError:
        return
    for e in entries:
        try:
            if _is_reparse_point(e.path):
                try:
                    os.rmdir(e.path)     # junction / 目录软链：只删链路（RemoveDirectory 语义）
                except OSError:
                    try:
                        os.unlink(e.path)
                    except OSError:
                        pass
            elif e.is_dir(follow_symlinks=False):
                _strip_reparse_points(e.path)
        except OSError:
            pass


def safe_rmtree(path: str) -> bool:
    """删除临时目录但不跟随重解析点（否则会顺着 junction 删掉 runtime 内的真实依赖）。"""
    if not os.path.lexists(path):
        return True
    _strip_reparse_points(path)
    shutil.rmtree(path, ignore_errors=True)
    return not os.path.lexists(path)


def closure_entry_count() -> int:
    if not os.path.isdir(DSH_NODE_MODULES):
        return -1
    return len(list(_iter_leaf_packages(DSH_NODE_MODULES)))


# ─────────────────────────────────── main ────────────────────────────────────

def _run_all() -> int:
    _STATE["startedAt"] = now()
    check_runtime()
    home = tempfile.mkdtemp(prefix="zxai-smoke-home-")   # 全新临时 DSH_HOME
    work = tempfile.mkdtemp(prefix="zxai-smoke-work-")   # 会话 cwd（resume 需匹配）
    _STATE["freshDshHome"] = home
    _STATE["sessionCwd"] = work
    _STATE["telemetryDisabled"] = "DSH_TELEMETRY_DISABLED=1"
    _STATE["apiKey"] = "invalid placeholder（不读真实 Key）"
    _STATE["promptSent"] = False           # 全程不发送 prompt
    runtime_closure_before = closure_entry_count()
    _STATE["runtimeClosureEntriesBefore"] = runtime_closure_before
    print("fresh DSH_HOME: %s" % home)
    print("session cwd   : %s" % work)
    print()
    try:
        # ── 进程 1：initialize → session/new ────────────────────────────────
        p1 = AcpProc(home, work, "p1")
        t0 = time.time()
        init = p1.request("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
        if "__error__" in init:
            print("stderr:", "\n".join(p1.stderr_buf[-15:]))
            fail("initialize 失败：%s" % init["__error__"])
        agent = (init.get("agentInfo") or {})
        ok_step("initialize", "%.1fs agentInfo=%s" % (time.time() - t0, agent.get("name") or agent))

        new_res = p1.request("session/new", {"cwd": work, "mcpServers": []})
        if "__error__" in new_res:
            fail("session/new 失败：%s" % new_res["__error__"])
        sid = new_res.get("sessionId")
        if not sid:
            fail("session/new 未返回 sessionId")
        ok_step("session/new", "sessionId=%s" % sid)
        p1.dispose()   # 会话变为非活动，允许随后 resume
        ok_step("dispose 进程 1", "会话进入非活动；pid=%s exit=%s" % (p1.record["pid"], p1.record["exitCode"]))

        # ── 进程 2（全新进程）：initialize → session/resume → session/cancel → dispose ──
        p2 = AcpProc(home, work, "p2")
        init2 = p2.request("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
        if "__error__" in init2:
            fail("第 2 进程 initialize 失败：%s" % init2["__error__"])
        ok_step("第 2 进程 initialize")

        resume = p2.request("session/resume", {"sessionId": sid, "cwd": work, "mcpServers": []})
        if "__error__" in resume:
            fail("session/resume 失败（cwd=%s）：%s" % (work, resume["__error__"]))
        ok_step("session/resume", "-> %s" % (resume.get("sessionId") or sid))

        p2.notify("session/cancel", {"sessionId": sid})   # notification；此时无运行中 prompt，安全
        ok_step("session/cancel", "notification；空闲会话取消（执行中取消语义不在本烟测范围）")

        p2.dispose()
        ok_step("dispose 进程 2", "pid=%s exit=%s" % (p2.record["pid"], p2.record["exitCode"]))

        # ── profiles 依赖闭包（healProfilesModuleFallback / INSTALL_ANCHOR）──
        pkg_count = check_profiles_closure(home)
        ok_step("profiles 依赖闭包断言", "%d 个包" % pkg_count)

        steps = len(_STATE["steps"])
        print()
        print("==== 烟测通过：%d 步全部成功（未发送任何 prompt、未联系任何供应商） ====" % steps)
        print("sanity: fresh DSH_HOME 下自动初始化成功；profiles 依赖闭包 %d 个包全部解析到 runtime 内；"
              "进程全程只用 %s；子进程已全部回收；遥测已显式关闭（DSH_TELEMETRY_DISABLED=1）"
              % (pkg_count, RUNTIME))
        print("边界：session/cancel 是空闲会话通知 —— 「执行中取消」语义由 fake ACP 测试覆盖，本烟测不作此声明。")
        _STATE["ok"] = True
        return 0
    finally:
        _STATE["processes"] = reap_all()
        # 【主审门禁】回收失败必须在报告与退出码上体现——禁止"日志打印 [FAIL] 但 report.ok=true / exit 0"。
        _unreclaimed = [r for r in _STATE["processes"] if not (r or {}).get("reclaimed")]
        if _unreclaimed:
            _STATE["ok"] = False
            _STATE["failure"] = ("子进程未被完全回收（%d 个）：%s"
                                 % (len(_unreclaimed), json.dumps(_unreclaimed, ensure_ascii=False)))
            print("[FAIL] %s" % _STATE["failure"])
        _STATE["tempDirsRemoved"] = {"home": safe_rmtree(home), "work": safe_rmtree(work)}
        runtime_closure_after = closure_entry_count()
        _STATE["runtimeClosureEntriesAfter"] = runtime_closure_after
        if runtime_closure_before != runtime_closure_after:
            _STATE["ok"] = False
            _STATE["failure"] = ("清理临时 DSH_HOME 时破坏了 runtime 依赖目录：闭包条目 %d -> %d"
                                 % (runtime_closure_before, runtime_closure_after))
            print("[FAIL] %s" % _STATE["failure"])
        else:
            print("[OK] 临时目录已删除且 runtime 依赖目录完好（%d 个叶子包仍在 %s）"
                  % (runtime_closure_after, DSH_NODE_MODULES))
        if not all(_STATE["tempDirsRemoved"].values()):
            _STATE["ok"] = False
            _STATE["failure"] = "临时目录未能删除：%s" % _STATE["tempDirsRemoved"]
            print("[FAIL] %s" % _STATE["failure"])


def main() -> int:
    _STATE["startEpoch"] = time.time()
    try:
        rc = _run_all()
        if not _STATE.get("ok"):
            return 1          # finally 里判定的失败（清理异常 / 进程未回收等）
        return rc
    finally:
        flush_report()


if __name__ == "__main__":
    sys.exit(main())
