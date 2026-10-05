#!/usr/bin/env node
// ZXAI Q01 离线假 ACP 服务：模拟 `dsh --profile acp` 的协议面（stdin/stdout JSON-RPC），
// 供 TubaWinUi3.Tests 在无网络、无真实 Key、无 dsh 的环境下覆盖正常/取消/恢复/权限/进程退出分支。
//
// 环境变量（测试控制）：
//   FAKE_DSH_INIT_DELAY_MS    initialize 响应延迟（测初始化竞争/握手期取消）
//   FAKE_DSH_PROMPT_DELAY_MS  prompt 流式 chunk 间隔（测取消）
//   FAKE_DSH_EXIT_AFTER_N     N 个请求后进程退出（测子进程退出分支）
//   FAKE_DSH_IMAGE            "1" 时 initialize 声明 image 能力
//   FAKE_DSH_RESUME_FAIL      "1" 时 session/resume 拒绝（测 resume 失败重建）
//   FAKE_DSH_RESUME_OPTIONS   "1"  时 session/resume 返回【真实 dsh-acp schema】的 configOptions
//                             （id/options + provider 分组，value 为不透明编码）；
//                             "flat" 时返回同 schema 的【扁平】options 形态（兼容分支回归）；
//                             未设置时返回 configOptions:[]（旧 fixture 行为不变）。
//   FAKE_DSH_MODEL_GROUPS_JSON 【第二轮复核】用测试构造的 JSON 覆盖 provider 分组候选列表
//                             （[{group,name,options:[{value,name}]}]；value 必须是 JSON.stringify([provider, model])）
//                             ——覆盖"同名跨 provider / 供应商前缀 / 模型前缀"用；未设置时用默认 deepseek 两组。
//   FAKE_DSH_PROMPT_LOG       文件路径：每收到一条 session/prompt 追加一行 JSON（JSONL）——
//                             测试用它证明"被阻断时确实没有 prompt 被提交"（文件不存在或 0 行）
//   FAKE_DSH_SET_CONFIG_FAIL  "1" 时 session/set_config_option 按 dsh-acp 口径拒绝
//                             （unknown model option → -32602）——验证客户端不得假称已切换
//   FAKE_DSH_SET_CONFIG_NOOP  "1" 时 session/set_config_option 【成功响应但未生效】
//                             （currentValue 不变）——验证客户端必须核对响应而不是只看没报错
//   FAKE_DSH_SET_CONFIG_LOG   文件路径：记录收到的 set_config_option 原文 + 结论
//                             （测试断言"实际取用了服务返回的 option.value"）
//   FAKE_DSH_PERMISSION       "1" 时 prompt 期间发 session/request_permission（真实 ACP 结构：
//                             sessionId + toolCall(ToolCallUpdate) + options[{optionId,name,kind}]，
//                             kind ∈ allow_once|allow_always|reject_once|reject_always）；
//                             客户端应答按 RequestPermissionResponse schema 校验，
//                             非法/超时 → prompt 以 JSON-RPC error 结束（不静默放行）。
//   FAKE_DSH_PERMISSION_DENY  "1" 时 options 只给 reject_*（测客户端无 allow 可选时的兜底选择）
//   FAKE_DSH_PERMISSION_LOG  文件路径：把收到的权限应答原文 + 校验结论写成 JSON（测试断言字段用）
//   FAKE_DSH_PERMISSION_TIMEOUT_MS  等待客户端权限应答的上限（默认 8000）
//
// 协议面自检（无需编译 C#）：python scripts/verify-fake-acp-server.py
//   —— 用原始 JSON-RPC 驱动本文件，断言默认流程 / 权限 allow+deny / 扁平 outcome 负控 /
//      超时 / resume / image / 中途退出 / 取消；缺 node 或本文件时明确失败（不静默跳过）。

'use strict';
const readline = require('readline');
const fs = require('fs');

const INIT_DELAY = +(process.env.FAKE_DSH_INIT_DELAY_MS || 0);
const PROMPT_DELAY = +(process.env.FAKE_DSH_PROMPT_DELAY_MS || 20);
const EXIT_AFTER = +(process.env.FAKE_DSH_EXIT_AFTER_N || 0);
const IMAGE = process.env.FAKE_DSH_IMAGE === '1';
const RESUME_FAIL = process.env.FAKE_DSH_RESUME_FAIL === '1';
const EXIT_MID_PROMPT = process.env.FAKE_DSH_EXIT_MID_PROMPT === '1';
const PERMISSION = process.env.FAKE_DSH_PERMISSION === '1';
const PERMISSION_DENY = process.env.FAKE_DSH_PERMISSION_DENY === '1';
const PERMISSION_LOG = process.env.FAKE_DSH_PERMISSION_LOG || '';
const PERMISSION_TIMEOUT = +(process.env.FAKE_DSH_PERMISSION_TIMEOUT_MS || 8000);
// 【A07】resume 的 configOptions fixture 形态：'' = 空数组，'1' = provider 分组，'flat' = 扁平列表
const RESUME_OPTIONS = process.env.FAKE_DSH_RESUME_OPTIONS || '';
const MODEL_GROUPS_JSON = process.env.FAKE_DSH_MODEL_GROUPS_JSON || '';
const PROMPT_LOG = process.env.FAKE_DSH_PROMPT_LOG || '';
const SET_CONFIG_FAIL = process.env.FAKE_DSH_SET_CONFIG_FAIL === '1';
const SET_CONFIG_NOOP = process.env.FAKE_DSH_SET_CONFIG_NOOP === '1';
const SET_CONFIG_LOG = process.env.FAKE_DSH_SET_CONFIG_LOG || '';

const PERMISSION_REQ_ID = 9001;   // 服务端→客户端请求的 id（客户端必须原样回显）
const PERMISSION_OPTIONS_ALLOW = [
  { optionId: 'allow_once', name: '允许一次', kind: 'allow_once' },
  { optionId: 'allow_always', name: '始终允许', kind: 'allow_always' },
  { optionId: 'reject_once', name: '拒绝一次', kind: 'reject_once' },
];
const PERMISSION_OPTIONS_DENY_ONLY = [
  { optionId: 'reject_once', name: '拒绝一次', kind: 'reject_once' },
  { optionId: 'reject_always', name: '始终拒绝', kind: 'reject_always' },
];

let reqCount = 0;
let promptAbort = null;
let permissionWait = null;   // { options, resolve } —— 在途的权限请求

function send(obj) { process.stdout.write(JSON.stringify(obj) + '\n'); }
function reply(id, result) { send({ jsonrpc: '2.0', id, result }); }
function replyError(id, code, message) { send({ jsonrpc: '2.0', id, error: { code, message } }); }
function update(sessionId, upd) { send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId, update: upd } }); }
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ───────────── 权限请求（服务端 → 客户端） ─────────────

function permissionOptions() {
  return PERMISSION_DENY ? PERMISSION_OPTIONS_DENY_ONLY : PERMISSION_OPTIONS_ALLOW;
}

/** 按 ACP RequestPermissionRequest schema 发请求，等客户端应答（有界等待）。 */
function requestPermission(sessionId) {
  const options = permissionOptions();
  const req = {
    jsonrpc: '2.0',
    id: PERMISSION_REQ_ID,
    method: 'session/request_permission',
    params: {
      sessionId,
      toolCall: { toolCallId: 'fake-call-1', title: '执行 fake 命令', kind: 'execute', status: 'pending' },
      options,
    },
  };
  return new Promise(resolve => {
    const timer = setTimeout(() => {
      if (!permissionWait) return;
      const w = permissionWait;
      permissionWait = null;
      w.resolve({
        requestId: PERMISSION_REQ_ID, received: null, options,
        offer: PERMISSION_DENY ? 'deny_only' : 'allow_and_deny',
        selectedOptionId: null, valid: false,
        errors: ['等待客户端权限应答超时（' + PERMISSION_TIMEOUT + 'ms）'],
      });
    }, PERMISSION_TIMEOUT);
    permissionWait = {
      options,
      resolve: rec => { clearTimeout(timer); resolve(rec); },
    };
    send(req);
  });
}

/** 按 ACP RequestPermissionResponse schema 校验客户端应答（扁平 outcome 会被拒）。 */
function validatePermissionResponse(msg, options) {
  const ids = options.map(o => o.optionId);
  const errors = [];
  if (msg.jsonrpc !== '2.0') errors.push('jsonrpc 不是 "2.0"：' + JSON.stringify(msg.jsonrpc));
  if (msg.id !== PERMISSION_REQ_ID) errors.push('id 未原样回显：期望 ' + PERMISSION_REQ_ID + '，实际 ' + JSON.stringify(msg.id));
  if (msg.error) errors.push('客户端回了 error：' + JSON.stringify(msg.error));
  const result = msg.result;
  if (!result || typeof result !== 'object') errors.push('缺少 result 对象');
  const outcome = result && result.outcome;
  let selected = null;
  if (typeof outcome === 'string') {
    errors.push('outcome 是扁平字符串（旧结构）；ACP 要求嵌套 {outcome:{outcome,optionId}}');
  } else if (outcome && typeof outcome === 'object') {
    if (outcome.outcome !== 'selected' && outcome.outcome !== 'cancelled')
      errors.push('outcome.outcome 非法："' + outcome.outcome + '"（须 selected|cancelled）');
    if (outcome.outcome === 'selected') {
      selected = outcome.optionId;
      if (typeof selected !== 'string' || selected === '')
        errors.push('selected 缺 optionId');
      else if (!ids.includes(selected))
        errors.push('optionId 不在请求的 options 内："' + selected + '"（可选：' + ids.join(',') + '）');
    }
  } else {
    errors.push('缺少 result.outcome（须为对象）');
  }
  return {
    requestId: PERMISSION_REQ_ID,
    received: msg,
    options,
    offer: PERMISSION_DENY ? 'deny_only' : 'allow_and_deny',
    selectedOptionId: selected,
    valid: errors.length === 0,
    errors,
    at: new Date().toISOString(),
  };
}

function writePermissionLog(record) {
  if (!PERMISSION_LOG) return;
  try { fs.writeFileSync(PERMISSION_LOG, JSON.stringify(record, null, 2)); } catch { }
}

// ───────────── 【A07】会话配置项（configOptions）fixture ─────────────
// 与 dsh-acp（@deepseek-ai/dsh-acp · AcpModelControl.state）实际返回同构：
//   configOptions: [{ id:'model', name:'Model', category:'model', type:'select',
//                     currentValue: <不透明编码>,
//                     options: [ { group, name, options: [ { value, name } ] } ]      // provider 分组
//                              | [ { value, name } ] ] } ]                            // 扁平（兼容形态）
//   value 由服务端生成（真实实现 = JSON.stringify([provider, model])），客户端【必须原样取用】，
//   不得自行拼造编码。currentValue 永远出现在 options 里（真实实现会把当前模型插入其分组首位）。

const MODEL_CURRENT = JSON.stringify(['deepseek-official', 'deepseek-v4-flash']);   // 历史会话 logged 模型
const MODEL_TARGET = JSON.stringify(['deepseek-official', 'deepseek-v4-pro']);      // 可切换到的另一个模型
const MODEL_LOCAL = JSON.stringify(['local-lmstudio', 'qwen3-8b']);

const OPTION_GROUPS = MODEL_GROUPS_JSON ? JSON.parse(MODEL_GROUPS_JSON) : [
  {
    group: 'deepseek-official', name: 'DeepSeek', options: [
      { value: MODEL_CURRENT, name: 'DeepSeek V4 Flash' },
      { value: MODEL_TARGET, name: 'DeepSeek V4 Pro' },
    ],
  },
  {
    group: 'local-lmstudio', name: 'Local (LM Studio)', options: [
      { value: MODEL_LOCAL, name: 'Local Model' },
    ],
  },
];

// 【第二轮复核】候选列表可由测试用 FAKE_DSH_MODEL_GROUPS_JSON 整体覆盖（同名跨 provider /
// 供应商前缀 / 模型前缀回归用）；未设置时用上面默认的 deepseek-official + local-lmstudio 两组。
let sessionModelValue = OPTION_GROUPS[0].options[0].value;   // 会话当前（logged）模型；set_config_option 生效后更新

/** provider 分组（保证当前取值在列表里，与真实实现一致）。 */
function modelGroups() {
  const groups = OPTION_GROUPS.map(g => ({
    group: g.group, name: g.name, options: g.options.map(o => ({ value: o.value, name: o.name })),
  }));
  if (!groups.some(g => g.options.some(o => o.value === sessionModelValue))) {
    const route = (() => { try { return JSON.parse(sessionModelValue); } catch { return []; } })();
    const provider = Array.isArray(route) && route.length > 0 ? String(route[0]) : 'unknown';
    const model = Array.isArray(route) && route.length > 1 ? String(route[1]) : sessionModelValue;
    let group = groups.find(g => g.group === provider);
    if (!group) { group = { group: provider, name: provider, options: [] }; groups.push(group); }
    group.options.unshift({ value: sessionModelValue, name: model });
  }
  return groups;
}

/** 完整 configOptions 状态（形态由 FAKE_DSH_RESUME_OPTIONS 决定）。 */
function configOptionsState() {
  const groups = modelGroups();
  const modelOptions = RESUME_OPTIONS === 'flat'
    ? groups.flatMap(g => g.options).map(o => ({ value: o.value, name: o.name }))
    : groups.filter(g => g.options.length > 0);
  return [
    {
      id: 'model', name: 'Model', category: 'model', type: 'select',
      currentValue: sessionModelValue, options: modelOptions,
    },
    {
      id: 'reasoning_effort', name: 'Reasoning effort', category: 'thought_level', type: 'select',
      currentValue: '', options: [{ value: '', name: 'Provider default' }],
    },
  ];
}

function writeSetConfigLog(record) {
  if (!SET_CONFIG_LOG) return;
  try { fs.writeFileSync(SET_CONFIG_LOG, JSON.stringify(record, null, 2)); } catch { }
}

/** 【第二轮复核】收到 session/prompt 时追加一行 JSON（JSONL）——测试用它证明
 *  "模型切换未成立被阻断时确实没有 prompt 被提交"（文件不存在/0 行 = 一条都没发）。 */
function writePromptLog(sessionId, prompt) {
  if (!PROMPT_LOG) return;
  try {
    fs.appendFileSync(PROMPT_LOG,
      JSON.stringify({ at: new Date().toISOString(), sessionId, prompt }) + '\n');
  } catch { }
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', async line => {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }

  // 客户端 → 服务端的【应答】（无 method）：权限请求的应答在此消费
  if (msg.method === undefined) {
    if (permissionWait && msg.id === PERMISSION_REQ_ID) {
      const w = permissionWait;
      permissionWait = null;
      const record = validatePermissionResponse(msg, w.options);
      writePermissionLog(record);
      w.resolve(record);
    }
    return;   // 其它无 method 的行（未知应答）：忽略，且不计入请求计数
  }

  reqCount++;
  if (EXIT_AFTER && reqCount >= EXIT_AFTER && msg.method !== 'initialize') {
    process.exit(0); // 模拟子进程中途退出
  }
  const id = msg.id;

  switch (msg.method) {
    case 'initialize':
      if (INIT_DELAY) await sleep(INIT_DELAY);
      reply(id, {
        agentInfo: { name: 'fake-dsh', version: '0.0.1-fake' },
        agentCapabilities: { promptCapabilities: { image: IMAGE } },
      });
      break;

    case 'session/new':
      reply(id, { sessionId: 'fake-sess-1' });
      break;

    case 'session/resume':
      if (RESUME_FAIL) {
        send({ jsonrpc: '2.0', id, error: { code: -32000, message: 'resume 被拒绝（fake）' } });
      } else if (RESUME_OPTIONS) {
        // 【A07】真实 schema：resume 不返回 sessionId，只返回 configOptions（历史 logged 模型在这里）
        sessionModelValue = OPTION_GROUPS[0].options[0].value;
        reply(id, { configOptions: configOptionsState() });
      } else {
        reply(id, { configOptions: [] });
      }
      break;

    case 'session/set_config_option': {
      const params = msg.params || {};
      const record = {
        at: new Date().toISOString(),
        method: 'session/set_config_option',
        sessionId: params.sessionId === undefined ? null : params.sessionId,
        configId: params.configId === undefined ? null : params.configId,
        value: params.value === undefined ? null : params.value,
      };
      const reject = message => {
        writeSetConfigLog({ ...record, outcome: 'error', error: { code: -32602, message } });
        replyError(id, -32602, message);
      };
      if (SET_CONFIG_FAIL) {
        reject('unknown model option: ' + JSON.stringify(record.value) + '（fake 拒绝）');
        break;
      }
      if (record.configId !== 'model') {
        reject('unknown session config option: ' + record.configId);
        break;
      }
      // 与 dsh-acp 的 AcpModelConfigError 同口径：value 必须是服务先前给出的 option.value
      if (!modelGroups().some(g => g.options.some(o => o.value === record.value))) {
        reject('unknown model option: ' + JSON.stringify(record.value));
        break;
      }
      if (!SET_CONFIG_NOOP) sessionModelValue = record.value;   // NOOP：成功响应但设置未生效
      const state = configOptionsState();
      writeSetConfigLog({ ...record, outcome: 'ok', applied: !SET_CONFIG_NOOP, configOptions: state });
      reply(id, { configOptions: state });
      break;
    }

    case 'session/prompt': {
      const sid = (msg.params && msg.params.sessionId) || 'fake-sess-1';
      writePromptLog(sid, (msg.params && msg.params.prompt) || null);
      let cancelled = false;
      promptAbort = () => { cancelled = true; };
      if (PERMISSION) {
        // 权限请求 → 等客户端应答；非法/超时即报错结束本轮（不放行、不静默挂起）
        const rec = await requestPermission(sid);
        if (!rec.valid) {
          promptAbort = null;
          replyError(id, -32602, '客户端权限应答不符合 ACP schema：' + rec.errors.join('；'));
          break;
        }
      }
      for (const c of ['你', '好', '（fake）']) {
        await sleep(PROMPT_DELAY);
        if (cancelled) break;
        update(sid, { sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: c } });
        if (EXIT_MID_PROMPT) process.exit(7); // 模拟子进程在 prompt 中途死亡
      }
      update(sid, { sessionUpdate: 'usage_update', used: 100, size: 1000000 });
      reply(id, { stopReason: cancelled ? 'cancelled' : 'end_turn' });
      promptAbort = null;
      break;
    }

    case 'session/cancel':
      if (promptAbort) promptAbort(); // notification（无 id，不回）
      break;

    default:
      if (id !== undefined) reply(id, {}); // 未知方法：空响应兜底
      break;
  }
});

rl.on('close', () => process.exit(0));
