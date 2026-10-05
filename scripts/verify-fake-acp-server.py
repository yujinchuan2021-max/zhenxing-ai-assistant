#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZXAI Q01: offline protocol harness for the fake ACP service (TubaWinUi3.Tests/TestAssets/fake-dsh.cjs).

Why this exists: the fake ACP server is the gate behind DshAcpFakeServerTests, and its wire
behaviour (permission request/response structure, timeout handling, cancel/resume/exit branches)
must be verifiable WITHOUT building the C# test project. This script speaks raw JSON-RPC over
stdio to the same fake, exactly like DshAcpClient does, and asserts the protocol surface.

Checks (all offline: no network, no real keys, no dsh):
  [1] default flow (initialize / session/new / prompt streaming / end_turn)
  [2] permission ALLOW arm  — request uses the real ACP structures
      (params.toolCall = ToolCallUpdate, options[{optionId,name,kind}] with kind in
       allow_once|allow_always|reject_once|reject_always); the nested client response
       {"outcome":{"outcome":"selected","optionId":...}} passes schema validation and is
       logged (FAKE_DSH_PERMISSION_LOG) for field-level assertions.
  [3] permission DENY arm   — options carry only reject_* (no allow available); the exchange
      still completes (no hang, no crash) and the selected option must be one of the offered ones.
  [4] negative control      — a FLAT (legacy) outcome must be REJECTED with a JSON-RPC error
      instead of silently passing.
  [5] timeout               — a client that never answers must produce an error, not a hang.
  [6]-[8] regressions      — resume reject/accept, image capability, exit-mid-prompt, cancel.

Usage:
    python scripts/verify-fake-acp-server.py            # exits 0 when every check passes, 1 otherwise
    TUBA_TEST_NODE=<node.exe> python scripts/verify-fake-acp-server.py

Node resolution mirrors the C# probe (FakeAcpDependencies): TUBA_TEST_NODE (authoritative when
set) -> PATH -> Hermes dev node dir. Missing node => loud failure, never a silent skip.
"""
import json, os, queue, shutil, subprocess, sys, tempfile, threading, time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FAKE = os.path.join(REPO, "TubaWinUi3.Tests", "TestAssets", "fake-dsh.cjs")

def _resolve_node():
    tried = []
    override = os.environ.get("TUBA_TEST_NODE")
    if override:
        # 显式指定即权威：文件不存在就报错，不回退（与 C# 的 FakeAcpDependencies 一致）
        if os.path.isfile(override):
            return override
        print("缺少测试依赖 node（离线 fake ACP 服务验证是必要门禁，缺依赖必须失败而不是跳过）：")
        print("  - TUBA_TEST_NODE=%s（文件不存在；显式指定不回退）" % override)
        sys.exit(1)
    tried.append("TUBA_TEST_NODE: unset")
    onpath = shutil.which("node")
    if onpath:
        return onpath
    tried.append("PATH: no node")
    hermes = os.path.join(os.environ.get("LOCALAPPDATA", ""), "hermes", "node", "node.exe")
    if os.path.isfile(hermes):
        return hermes
    tried.append("Hermes dev node: %s (not found)" % hermes)
    print("缺少测试依赖 node（离线 fake ACP 服务验证是必要门禁，缺依赖必须失败而不是跳过）：")
    print("\n".join("  - " + t for t in tried))
    sys.exit(1)

NODE = _resolve_node()
if not os.path.isfile(FAKE):
    print("[FAIL] 缺少 fake-dsh.cjs：%s" % FAKE)
    sys.exit(1)
print("node:", NODE, "|", subprocess.run([NODE, "--version"], capture_output=True, text=True).stdout.strip())
print("fake:", FAKE)

FAILED = []


def check(name, cond, detail=""):
    print(("  [OK]   " if cond else "  [FAIL] ") + name + (("  -> " + str(detail)) if detail else ""))
    if not cond:
        FAILED.append(name)


class Proc:
    def __init__(self, env_extra):
        self.dir = tempfile.mkdtemp(prefix="fakeharness-")
        env = dict(os.environ)
        env.update(env_extra)
        self.proc = subprocess.Popen([NODE, FAKE, "--profile", "acp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                                     env=env, cwd=self.dir, creationflags=subprocess.CREATE_NO_WINDOW)
        self.q = queue.Queue()
        self.err = []
        threading.Thread(target=lambda: [self.q.put(l.rstrip("\n")) for l in self.proc.stdout], daemon=True).start()
        threading.Thread(target=lambda: [self.err.append(l.rstrip("\n")) for l in self.proc.stderr], daemon=True).start()
        self.nid = 0

    def send(self, obj):
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def req(self, method, params, timeout=20):
        self.nid += 1
        rid = self.nid
        self.send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        dl = time.time() + timeout
        while time.time() < dl:
            try:
                line = self.q.get(timeout=max(0.1, dl - time.time()))
            except queue.Empty:
                break
            try:
                m = json.loads(line)
            except Exception:
                continue
            if "id" in m and "method" in m:
                self.client_requests.append(m)
                continue
            if m.get("method") == "session/update":
                self.updates.append(m["params"]["update"])
                continue
            if m.get("id") == rid:
                return m
        return {"__timeout__": True}

    client_requests = None
    updates = None

    def drain_updates(self, seconds=1.5):
        upd = []
        dl = time.time() + seconds
        while time.time() < dl:
            try:
                m = json.loads(self.q.get(timeout=0.2))
            except Exception:
                continue
            if m.get("method") == "session/update":
                upd.append(m["params"]["update"])
            elif "id" in m and "method" in m:
                self.client_requests.append(m)
        return upd

    def close(self):
        try:
            self.proc.stdin.close(); self.proc.wait(timeout=8)
        except Exception:
            try: self.proc.kill()
            except Exception: pass


def start(extra):
    p = Proc(extra)
    p.client_requests = []
    p.updates = []
    return p


# ── 1) 默认流程（回归） ──────────────────────────────────────────────
print("\n[1] default flow (no permission)")
p = start({})
r = p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
check("initialize -> agentInfo", r.get("result", {}).get("agentInfo", {}).get("name") == "fake-dsh", r)
r = p.req("session/new", {"cwd": p.dir, "mcpServers": []})
check("session/new -> fake-sess-1", r.get("result", {}).get("sessionId") == "fake-sess-1", r)
r = p.req("session/prompt", {"sessionId": "fake-sess-1", "prompt": [{"type": "text", "text": "hi"}]})
check("prompt -> end_turn", r.get("result", {}).get("stopReason") == "end_turn", r)
texts = "".join(u.get("content", {}).get("text", "") for u in p.updates) or \
        "".join(u.get("content", {}).get("text", "") for u in p.drain_updates(0.3))
check("streamed text 你好（fake）", "你好（fake）" in texts, texts)
check("no client requests in default flow", p.client_requests == [], p.client_requests)
p.close()

# ── 2) 权限 allow 路（真实 ACP 结构） ────────────────────────────────
print("\n[2] permission allow arm")
log = os.path.join(tempfile.mkdtemp(prefix="fakeperm-"), "perm.json")
p = start({"FAKE_DSH_PERMISSION": "1", "FAKE_DSH_PERMISSION_LOG": log})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
p.req("session/new", {"cwd": p.dir, "mcpServers": []})
p.send({"jsonrpc": "2.0", "id": p.nid + 1, "method": "session/prompt",
        "params": {"sessionId": "fake-sess-1", "prompt": [{"type": "text", "text": "hi"}]}})
# 等权限请求到达
perm = None
dl = time.time() + 10
while time.time() < dl and perm is None:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("method") == "session/request_permission":
        perm = m
check("server sent session/request_permission", perm is not None)
if perm:
    params = perm["params"]
    check("request id == 9001", perm.get("id") == 9001, perm.get("id"))
    check("params.sessionId present", params.get("sessionId") == "fake-sess-1", params.get("sessionId"))
    tc = params.get("toolCall") or {}
    check("params.toolCall.toolCallId (real ToolCallUpdate)", tc.get("toolCallId") == "fake-call-1", tc)
    opts = params.get("options") or []
    check("options[] has optionId+name+kind",
          opts and all(set(o) >= {"optionId", "name", "kind"} for o in opts), opts)
    check("kinds are real ACP PermissionOptionKind",
          all(o["kind"] in ("allow_once", "allow_always", "reject_once", "reject_always") for o in opts),
          [o["kind"] for o in opts])
    # 客户端应答（与 DshAcpClient.HandleServerRequestAsync 相同结构）
    p.send({"jsonrpc": "2.0", "id": 9001, "result": {"outcome": {"outcome": "selected", "optionId": "allow_once"}}})
# 读 prompt 应答
resp = None
dl = time.time() + 15
while time.time() < dl and resp is None:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("id") == p.nid + 1:
        resp = m
check("prompt -> end_turn after allow", (resp or {}).get("result", {}).get("stopReason") == "end_turn", resp)
rec = json.load(open(log, encoding="utf-8")) if os.path.isfile(log) else None
check("permission log written", rec is not None)
if rec:
    check("log.valid == true", rec["valid"] is True, rec.get("errors"))
    check("log.selectedOptionId == allow_once", rec["selectedOptionId"] == "allow_once", rec["selectedOptionId"])
    check("log.offer == allow_and_deny", rec["offer"] == "allow_and_deny", rec.get("offer"))
    check("log.received.id == 9001 (echoed)", rec["received"]["id"] == 9001, rec["received"]["id"])
    check("log.received.result.outcome nested object",
          isinstance(rec["received"]["result"]["outcome"], dict)
          and rec["received"]["result"]["outcome"]["outcome"] == "selected", rec["received"]["result"])
p.close()

# ── 3) 权限 deny 路（只给 reject_* 可选） ────────────────────────────
print("\n[3] permission deny arm (deny-only options)")
log2 = os.path.join(tempfile.mkdtemp(prefix="fakeperm2-"), "perm.json")
p = start({"FAKE_DSH_PERMISSION": "1", "FAKE_DSH_PERMISSION_DENY": "1", "FAKE_DSH_PERMISSION_LOG": log2})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
p.req("session/new", {"cwd": p.dir, "mcpServers": []})
prompt_id = p.nid + 1
p.send({"jsonrpc": "2.0", "id": prompt_id, "method": "session/prompt",
        "params": {"sessionId": "fake-sess-1", "prompt": [{"type": "text", "text": "hi"}]}})
perm = None
dl = time.time() + 10
while time.time() < dl and perm is None:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("method") == "session/request_permission":
        perm = m
check("deny arm: permission request sent", perm is not None)
kinds = [o["kind"] for o in (perm or {}).get("params", {}).get("options", [])]
check("deny arm: only reject_* offered", kinds and all(k.startswith("reject") for k in kinds), kinds)
# 客户端在此情形下按源码兜底选第一个（reject_once）
p.send({"jsonrpc": "2.0", "id": 9001, "result": {"outcome": {"outcome": "selected", "optionId": "reject_once"}}})
resp = None
dl = time.time() + 15
while time.time() < dl and resp is None:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("id") == prompt_id:
        resp = m
check("deny arm: prompt still completes (no hang)", (resp or {}).get("result", {}).get("stopReason") == "end_turn", resp)
rec = json.load(open(log2, encoding="utf-8")) if os.path.isfile(log2) else None
check("deny arm: log.offer == deny_only", (rec or {}).get("offer") == "deny_only", (rec or {}).get("offer"))
check("deny arm: selected reject_once", (rec or {}).get("selectedOptionId") == "reject_once", (rec or {}).get("selectedOptionId"))
check("deny arm: response valid", (rec or {}).get("valid") is True, (rec or {}).get("errors"))
p.close()

# ── 4) 负控：扁平 outcome（旧结构）必须被拒 ──────────────────────────
print("\n[4] negative control: flat outcome must be rejected")
log3 = os.path.join(tempfile.mkdtemp(prefix="fakeperm3-"), "perm.json")
p = start({"FAKE_DSH_PERMISSION": "1", "FAKE_DSH_PERMISSION_LOG": log3})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
p.req("session/new", {"cwd": p.dir, "mcpServers": []})
prompt_id = p.nid + 1
p.send({"jsonrpc": "2.0", "id": prompt_id, "method": "session/prompt",
        "params": {"sessionId": "fake-sess-1", "prompt": [{"type": "text", "text": "hi"}]}})
dl = time.time() + 10
got = False
while time.time() < dl and not got:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("method") == "session/request_permission":
        got = True
p.send({"jsonrpc": "2.0", "id": 9001, "result": {"outcome": "selected", "optionId": "allow_once"}})  # 扁平=旧结构
resp = None
dl = time.time() + 15
while time.time() < dl and resp is None:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("id") == prompt_id:
        resp = m
check("flat outcome -> prompt error (not silent pass)", "error" in (resp or {}), resp)
rec = json.load(open(log3, encoding="utf-8")) if os.path.isfile(log3) else None
check("negative control log.valid == false", (rec or {}).get("valid") is False, rec)
p.close()

# ── 5) 权限超时（客户端不应答）→ 报错而非静默挂起 ────────────────────
print("\n[5] permission timeout when client never answers")
p = start({"FAKE_DSH_PERMISSION": "1", "FAKE_DSH_PERMISSION_TIMEOUT_MS": "600"})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
p.req("session/new", {"cwd": p.dir, "mcpServers": []})
prompt_id = p.nid + 1
p.send({"jsonrpc": "2.0", "id": prompt_id, "method": "session/prompt",
        "params": {"sessionId": "fake-sess-1", "prompt": [{"type": "text", "text": "hi"}]}})
resp = None
dl = time.time() + 12
while time.time() < dl and resp is None:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("id") == prompt_id:
        resp = m
check("timeout -> prompt error", "error" in (resp or {}), resp)
p.close()

# ── 6) 回归：resume 拒绝 / image / resume 成功 ───────────────────────
print("\n[6] regressions: resume fail / image capability")
p = start({"FAKE_DSH_RESUME_FAIL": "1"})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
r = p.req("session/resume", {"sessionId": "fake-sess-1", "cwd": p.dir, "mcpServers": []})
check("resume fail -> error", "error" in r, r)
p.close()
p = start({"FAKE_DSH_IMAGE": "1"})
r = p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
check("image capability reflected",
      r.get("result", {}).get("agentCapabilities", {}).get("promptCapabilities", {}).get("image") is True, r)
p.close()
p = start({})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
r = p.req("session/resume", {"sessionId": "fake-sess-1", "cwd": p.dir, "mcpServers": []})
check("resume ok -> no error", "error" not in r and "result" in r, r)
p.close()

# ── 7) 回归：prompt 中途进程死亡（exit 7） ───────────────────────────
print("\n[7] regression: exit mid prompt")
p = start({"FAKE_DSH_EXIT_MID_PROMPT": "1"})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
p.req("session/new", {"cwd": p.dir, "mcpServers": []})
p.send({"jsonrpc": "2.0", "id": p.nid + 1, "method": "session/prompt",
        "params": {"sessionId": "fake-sess-1", "prompt": [{"type": "text", "text": "hi"}]}})
time.sleep(1.0)
check("child exited mid prompt", p.proc.poll() is not None, p.proc.poll())
try:
    p.proc.stdin.close(); p.proc.wait(timeout=5)
except Exception:
    p.proc.kill()

# ── 8) 回归：取消 ───────────────────────────────────────────────────
print("\n[8] regression: cancel during prompt")
p = start({"FAKE_DSH_PROMPT_DELAY_MS": "150"})
p.req("initialize", {"protocolVersion": 1, "clientCapabilities": {}})
p.req("session/new", {"cwd": p.dir, "mcpServers": []})
prompt_id = p.nid + 1
p.send({"jsonrpc": "2.0", "id": prompt_id, "method": "session/prompt",
        "params": {"sessionId": "fake-sess-1", "prompt": [{"type": "text", "text": "hi"}]}})
time.sleep(0.2)
p.send({"jsonrpc": "2.0", "method": "session/cancel", "params": {"sessionId": "fake-sess-1"}})
resp = None
dl = time.time() + 10
while time.time() < dl and resp is None:
    try:
        m = json.loads(p.q.get(timeout=0.5))
    except Exception:
        continue
    if m.get("id") == prompt_id:
        resp = m
check("cancel -> stopReason cancelled", (resp or {}).get("result", {}).get("stopReason") == "cancelled", resp)
p.close()

print("\n==== %s ====" % ("ALL HARNESS CHECKS PASSED" if not FAILED else "FAILURES: " + ", ".join(FAILED)))
sys.exit(1 if FAILED else 0)
