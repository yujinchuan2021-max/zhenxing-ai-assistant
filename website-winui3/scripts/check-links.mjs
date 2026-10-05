/**
 * 枕星图吧AI助手 官网 · 入口渲染断言（links → 真链接 / 诚实空态）
 *
 * 站点所有对外入口统一走 `src/zxai/components/ZxLinkOr.vue`：
 *   - `site.config.ts` 里配了 → 渲染 `<a href>`（新窗口 + 「外部链接」标注）；
 *   - 没配（null） → 渲染 fallback 空态文案，**绝不生成占位链接**。
 *
 * 本脚本用真实渲染后的 DOM 断言三种模式：
 *   ① 交付态（--expect none）：只有 `feedback`（mailto）是真链接，其余入口必须空态；空态文案必须出现；页脚说明「入口尚未配置」；
 *   ② 混合态（--expect community）：社区已上线（`community` 必须是真链接，且指向社区 origin、新窗口 + 「外部链接」标注），
 *      feedback 仍是真链接，下载/仓库/文档仍必须空态；页脚同时出现「本项目的正式入口」与「未列出的入口尚未配置」；
 *   ③ 全配置分支（--expect configured --origin <url>）：配置过的入口必须都是 `<a href>` 且以给定 origin 开头。
 *
 * 三种模式都会额外断言**状态卡/头部文案与配置状态自洽**（见 COPY_WHEN_* 表）：
 * 未配置不许暗示「已开通/已建立/已发布」，已配置不许再写「尚未开通/未建立/未公开发布」。
 * `/download` 的已配置分支要求 `site.config.ts` 的 `status.released = true`（与 releaseState() 口径一致）。
 *
 * 用法（先 `npm run preview -- --port 4173`）：
 *   node scripts/check-links.mjs --base http://127.0.0.1:4173 --expect none
 *   node scripts/check-links.mjs --base http://127.0.0.1:4173 --expect community   # 2026-09-24 社区上线后的交付态
 *   node scripts/check-links.mjs --base http://127.0.0.1:4173 --expect configured \
 *     --origin https://example.com --report links-report-configured.json
 */

import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const index = args.indexOf(`--${name}`);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};

const BASE = argOf('base', 'http://127.0.0.1:4173');
const OUT = argOf('out', '');
const PORT = Number(argOf('port', '9344'));
const MODE = argOf('expect', 'none');
const ORIGIN = argOf('origin', '');
/** 混合态下社区入口应该指向的地址（2026-09-24 已上线的自托管 Discourse） */
const COMMUNITY_ORIGIN = argOf('community-origin', 'https://community.zhenxingai.com');
const REPORT_NAME = argOf('report', 'links-report.json');
const ROUTES = ['/', '/download', '/docs', '/community'];

/**
 * 每个入口在当前模式下「该是真链接还是空态」：
 *   none      → 只有 feedback（mailto）是真链接，其余必须空态；
 *   community → feedback + community 是真链接，其余必须空态（社区先上线的混合态）；
 *   configured→ 全部必须真链接。
 */
const linkExpectation = (name) => {
  if (MODE === 'configured') return 'link';
  if (name === 'feedback') return 'link';
  if (name === 'community' && MODE === 'community') return 'link';
  return 'empty';
};

/**
 * 页面文案 × 配置状态 的一致性断言：
 * 文案来源统一在 `site.config.ts`（communityCopy()/docsCopy()/releaseState()），
 * 未配置时必须如实说「尚未开通/未建立/未发布」，已配置后不得再保留这些说法。
 * `/download` 的已配置分支需要 `status.released = true`（与 releaseState() 口径一致）。
 */
const COPY_WHEN_EMPTY = {
  // 首页：收敛后的叙事 + 四状态演示必须真的在页面上；「示意」标记与未发布状态不能丢
  '/': {
    must: [
      '说出你的想法，枕星带你把 AI 用起来',
      '描述目标',
      '比较并选择工具流',
      '配置与教程',
      '生成 Agent 项目说明',
      '示意',
      '暂未开放下载',
    ],
    mustNot: ['社区已开通'],
  },
  '/download': { must: ['尚未公开发布'], mustNot: [] },
  '/docs': { must: ['未建立'], mustNot: ['已建立'] },
  '/community': { must: ['尚未开通'], mustNot: ['社区已开通'] },
};
const COPY_WHEN_CONFIGURED = {
  '/': { must: ['已公开发布'], mustNot: [] },
  '/download': { must: ['已公开发布'], mustNot: ['尚未公开发布'] },
  '/docs': { must: ['已建立'], mustNot: ['未建立', '还没有建立'] },
  '/community': { must: ['已开通'], mustNot: ['尚未开通', '没有可点的地址'] },
};
/**
 * 混合态（2026-09-24 社区先上线、下载与仓库仍未发布）：
 * 社区按「已开通」写，其余入口继续按空态写；任何入口状态变化都要在这张表里如实反映。
 */
const COPY_WHEN_COMMUNITY = {
  '/': { must: ['论坛已上线', '尚未公开发布'], mustNot: ['尚未开通', '计划中'] },
  '/download': {
    // 下载页状态卡用的是 site.config.ts 的 status.note 原文（「社区入口已开放」）：
    // 与「社区已上线」是同一事实的两种说法，用 any 兜住，避免把措辞差异当成状态矛盾
    must: ['尚未公开发布'],
    any: ['社区已上线', '社区入口已开放'],
    mustNot: ['社区入口仍在确定中'],
  },
  '/docs': { must: ['未建立'], mustNot: ['已建立'] },
  '/community': {
    must: ['已开通', 'Discourse'],
    mustNot: ['尚未开通', '没有可点的地址', '计划中的社区内容', '没有账号体系'],
  },
};

const chromePath = [
  process.env.CHROME_PATH,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
].filter(Boolean).find((path) => existsSync(path));
if (!chromePath) {
  console.error('找不到 Chrome；可用 CHROME_PATH 指定。');
  process.exit(2);
}

const chrome = spawn(chromePath, [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  `--user-data-dir=${process.env.TEMP ?? 'C:/Windows/Temp'}/zxai-links`,
  `--remote-debugging-port=${PORT}`, 'about:blank',
], { stdio: ['ignore', 'ignore', 'ignore'] });

for (let attempt = 0; attempt < 60; attempt += 1) {
  try { if ((await fetch(`http://127.0.0.1:${PORT}/json/version`)).ok) break; } catch { /* retry */ }
  await sleep(250);
}
const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const ws = new WebSocket(targets.find((target) => target.type === 'page').webSocketDebuggerUrl);
await new Promise((done) => ws.addEventListener('open', done, { once: true }));
let seq = 0; const pending = new Map();
ws.addEventListener('message', (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) { pending.get(message.id)(message); pending.delete(message.id); }
});
const send = (method, params = {}, timeoutMs = 20000) => {
  seq += 1; const id = seq;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((done) => {
    const timer = setTimeout(() => { pending.delete(id); done({ timeout: true }); }, timeoutMs);
    pending.set(id, (message) => { clearTimeout(timer); done(message); });
  });
};
const evaluate = async (expression) => {
  const response = await send('Runtime.evaluate', { expression, returnByValue: true });
  if (response.timeout) throw new Error('evaluate 超时');
  return response.result?.result?.value;
};

await send('Page.enable');
await send('Runtime.enable');

const report = { base: BASE, mode: MODE, origin: ORIGIN, routes: [], failures: [] };

for (const route of ROUTES) {
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false, screenWidth: 1440, screenHeight: 900 });
  await send('Page.navigate', { url: `${BASE}${route}` });
  await sleep(1300);
  const raw = await evaluate(`(() => {
    // 只取「本该是入口链接」的元素（ZxLinkOr 两分支都带 data-zx-link），不靠 .zx-entry 猜
    const entries = [...document.querySelectorAll('[data-zx-link]')].map((node) => {
      const anchor = node.tagName === 'A' ? node : null;
      const entry = node.closest('.zx-entry');
      return {
        name: node.getAttribute('data-zx-link'),
        label: (entry?.querySelector('.zx-entry__label')?.textContent ?? node.getAttribute('data-zx-link') ?? '').trim(),
        href: anchor?.getAttribute('href') ?? null,
        text: (node.textContent ?? '').trim(),
        externalMarked: /外部链接/.test(node.textContent ?? ''),
        target: anchor?.getAttribute('target') ?? null,
        rel: anchor?.getAttribute('rel') ?? null,
      };
    });
    const footer = (document.querySelector('.zx-footer__note')?.innerText ?? '').replace(/\\s+/g, ' ');
    // 页面文案（头部说明 + 状态卡）：用于断言文案随配置状态切换，而不是固定写「未开通/未建立」
    const headText = (document.querySelector('.zx-page__head')?.innerText ?? '').replace(/\\s+/g, ' ').trim();
    const statusText = (document.querySelector('.zx-status')?.innerText ?? '').replace(/\\s+/g, ' ').trim();
    // 整页可见文案：首页没有 .zx-page__head/.zx-status（叙事收敛后首屏是自定义结构），只能按整页取
    const bodyText = (document.body.innerText ?? '').replace(/\\s+/g, ' ').trim();
    return JSON.stringify({ entries, footer, headText, statusText, bodyText, entryLinks: entries.filter((entry) => entry.href).length });
  })()`);
  const data = JSON.parse(raw);
  const failures = [];

  if (!data.entries.length && route !== '/') failures.push('页面上没有任何 data-zx-link 入口（选择器失效？）');

  // ① 该是真链接的必须渲染 <a>，该是空态的绝不允许有 href（不得生成占位链接）
  const wrongState = data.entries.filter((entry) =>
    linkExpectation(entry.name) === 'link' ? !entry.href : Boolean(entry.href),
  );
  if (wrongState.length) {
    failures.push(
      `入口渲染与配置状态不符（${MODE} 模式）: ${wrongState
        .map((entry) => `${entry.label}=${entry.href ?? '空态'}`)
        .join('、')}`,
    );
  }

  // ② 空态分支必须给出如实文案
  const missingFallback = data.entries.filter((entry) => !entry.href && !entry.text);
  if (missingFallback.length) {
    failures.push(`入口缺少空态文案: ${missingFallback.map((entry) => entry.label).join('、')}`);
  }

  // ③ 真正离开本站的链接要有外链标注；同站 ZIP 下载在当前窗口完成。
  const linked = data.entries.filter((entry) => entry.href && !entry.href.startsWith('mailto:'));
  const badExternal = linked.filter((entry) =>
    entry.name === 'download' || entry.name === 'sourceArchive'
      ? entry.target === '_blank' || entry.externalMarked
      : entry.target !== '_blank' || !entry.externalMarked,
  );
  if (badExternal.length) {
    failures.push(
      `外链缺少 target=_blank/「外部链接」标注: ${badExternal.map((entry) => entry.label).join('、')}`,
    );
  }

  // ④ origin 口径：全配置模式要求都指向给定 origin；混合态单独校验社区地址
  if (MODE === 'configured') {
    const expected = ORIGIN.replace(/\/$/, '');
    const wrongOrigin = linked.filter((entry) => !entry.href.startsWith(expected));
    if (wrongOrigin.length) {
      failures.push(`href 未指向给定 origin: ${wrongOrigin.map((entry) => `${entry.label}=${entry.href}`).join('、')}`);
    }
  }
  if (MODE === 'community') {
    const communityEntry = data.entries.find((entry) => entry.name === 'community');
    if (communityEntry?.href && !communityEntry.href.startsWith(COMMUNITY_ORIGIN)) {
      failures.push(`社区入口 href 不是已上线的社区地址: ${communityEntry.href}`);
    }
  }

  // ⑤ 页脚：已上线的入口列出来，未上线的仍要如实说明
  if (MODE === 'configured') {
    if (!/本项目的正式入口/.test(data.footer)) failures.push('页脚未切换为真实入口列表（应出现「本项目的正式入口」）');
  } else {
    if (!/入口尚未配置/.test(data.footer)) failures.push('页脚缺少「入口尚未配置」的诚实说明');
  }
  if (MODE === 'community' && !/本项目的正式入口/.test(data.footer)) {
    failures.push('混合态页脚应出现「本项目的正式入口」（社区已上线，应列出社区）');
  }

  // 文案随配置状态切换的断言（页面文案与 SEO 描述共用 site.config.ts 的同一来源）
  const copyTable =
    MODE === 'configured' ? COPY_WHEN_CONFIGURED : MODE === 'community' ? COPY_WHEN_COMMUNITY : COPY_WHEN_EMPTY;
  const copyExpect = copyTable[route] ?? { must: [], mustNot: [] };
  const pageText = route === '/' ? data.bodyText : `${data.headText} ${data.statusText}`;
  for (const phrase of copyExpect.must ?? []) {
    if (!pageText.includes(phrase)) failures.push(`页面文案与当前配置不符：应包含「${phrase}」`);
  }
  if (copyExpect.any?.length && !copyExpect.any.some((phrase) => pageText.includes(phrase))) {
    failures.push(`页面文案与当前配置不符：应包含「${copyExpect.any.join('」或「')}」之一`);
  }
  for (const phrase of copyExpect.mustNot ?? []) {
    if (pageText.includes(phrase)) failures.push(`页面文案仍写着与配置状态矛盾的「${phrase}」`);
  }

  data.failures = failures;
  report.routes.push(data);
  report.failures.push(...failures.map((failure) => `${route}: ${failure}`));

  console.log(`\n=== ${route} ===`);
  for (const entry of data.entries) {
    console.log(`  ${entry.label.padEnd(8, ' ')} → ${entry.href ?? `（空态）${entry.text}`}`);
  }
  console.log('  状态卡文案 :', data.statusText.slice(0, 72));
  console.log('  断言       :', failures.length ? `✗ ${failures.join('; ')}` : '✓ 全部通过');
}

if (OUT) {
  mkdirSync(OUT, { recursive: true });
  writeFileSync(join(OUT, REPORT_NAME), JSON.stringify(report, null, 2));
}

console.log(
  `\n模式：${
    MODE === 'configured'
      ? `已配置（${ORIGIN}）`
      : MODE === 'community'
        ? `混合态：社区已上线（${COMMUNITY_ORIGIN}）、其余入口空态`
        : '未配置（交付态）'
  }`,
);
console.log(`结论：${report.failures.length === 0 ? '入口渲染符合约定' : report.failures.length + ' 项不通过'}`);
chrome.kill();
process.exit(report.failures.length === 0 ? 0 : 1);
