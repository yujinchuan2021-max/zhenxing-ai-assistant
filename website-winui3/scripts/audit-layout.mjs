/**
 * 枕星图吧AI助手 官网 · 窄屏布局审计
 *
 * 在多个视口宽度下检查「横向溢出」：列出宽度超过视口、导致整页被撑开的元素，
 * 以及被压扁到不可点的交互目标。用 mobile=false 的桌面式窄视口，
 * 这样溢出会真实体现在 document.scrollWidth 上（mobile=true 会被 Chrome 的
 * shrink-to-fit 掩盖）。
 *
 * 用法（先 `npm run preview -- --port 4173`）：
 *   node scripts/audit-layout.mjs --base http://127.0.0.1:4173
 */

import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const index = args.indexOf(`--${name}`);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};

const BASE = argOf('base', 'http://127.0.0.1:4173');
const PORT = Number(argOf('port', '9337'));
const PAGES = ['/', '/download', '/docs', '/community', '/about'];
const VIEWPORTS = [
  { name: 'mobile-360', width: 360, height: 780 },
  { name: 'mobile-390', width: 390, height: 844 },
  { name: 'tablet-768', width: 768, height: 1024 },
  { name: 'desktop-1280', width: 1280, height: 800 },
];

const chromePath = [
  process.env.CHROME_PATH,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
].filter(Boolean).find((path) => existsSync(path));
if (!chromePath) {
  console.error('找不到 Chrome；可用 CHROME_PATH 指定。');
  process.exit(2);
}

const chrome = spawn(chromePath, [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--hide-scrollbars',
  `--user-data-dir=${process.env.TEMP ?? 'C:/Windows/Temp'}/zxai-layout-audit`,
  `--remote-debugging-port=${PORT}`, 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });

for (let attempt = 0; attempt < 60; attempt += 1) {
  try {
    const response = await fetch(`http://127.0.0.1:${PORT}/json/version`);
    if (response.ok) break;
  } catch { /* retry */ }
  await sleep(250);
}

const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const page = targets.find((target) => target.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((done) => ws.addEventListener('open', done, { once: true }));

let seq = 0;
const pending = new Map();
ws.addEventListener('message', (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    const resolve = pending.get(message.id);
    pending.delete(message.id);
    resolve(message);
  }
});

function send(method, params = {}, timeoutMs = 15000) {
  seq += 1;
  const id = seq;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((done) => {
    const timer = setTimeout(() => { pending.delete(id); done({ timeout: true, method }); }, timeoutMs);
    pending.set(id, (message) => { clearTimeout(timer); done(message); });
  });
}

await send('Page.enable');
await send('Runtime.enable');

const AUDIT = `(() => {
  const viewport = window.innerWidth;
  const describe = (element) => {
    const cls = typeof element.className === 'string' ? element.className : '';
    return element.tagName.toLowerCase()
      + (element.id ? '#' + element.id : '')
      + (cls ? '.' + cls.trim().split(/\\s+/).slice(0, 2).join('.') : '');
  };
  const offenders = [...document.querySelectorAll('body *')]
    .map((element) => ({ element, rect: element.getBoundingClientRect() }))
    .filter(({ rect }) => rect.width > viewport + 1 || rect.right > viewport + 1)
    .map(({ element, rect }) => ({
      selector: describe(element),
      width: Math.round(rect.width),
      right: Math.round(rect.right),
      text: (element.textContent || '').trim().slice(0, 32),
    }))
    .slice(0, 12);

  // 自身内容溢出（scrollWidth > clientWidth）——真正把页面撑开的元凶
  const selfOverflow = [...document.querySelectorAll('body *')]
    .filter((element) => element.scrollWidth > element.clientWidth + 1 && element.clientWidth > 0)
    .map((element) => ({
      selector: describe(element),
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
      text: (element.textContent || '').trim().slice(0, 40),
    }))
    .sort((a, b) => (b.scrollWidth - b.clientWidth) - (a.scrollWidth - a.clientWidth))
    .slice(0, 8);

  // 触控目标：可点元素在窄屏下是否小于 24px
  const small = [...document.querySelectorAll('a, button, [role="radio"]')]
    .map((element) => ({ element, rect: element.getBoundingClientRect() }))
    .filter(({ rect }) => rect.width > 0 && (rect.height < 24 || rect.width < 24))
    .map(({ element, rect }) => ({
      selector: describe(element),
      size: Math.round(rect.width) + 'x' + Math.round(rect.height),
      text: (element.textContent || '').trim().slice(0, 24),
    }))
    .slice(0, 12);

  return JSON.stringify({
    viewport,
    docScrollWidth: document.documentElement.scrollWidth,
    bodyScrollWidth: document.body.scrollWidth,
    overflow: document.documentElement.scrollWidth > viewport + 1,
    offenders,
    selfOverflow,
    smallTargets: small,
  });
})()`;

let failures = 0;
for (const viewport of VIEWPORTS) {
  for (const path of PAGES) {
    await send('Emulation.setDeviceMetricsOverride', {
      width: viewport.width, height: viewport.height, deviceScaleFactor: 1, mobile: false,
      screenWidth: viewport.width, screenHeight: viewport.height,
    });
    await send('Page.navigate', { url: `${BASE}${path}` });
    await sleep(900);
    const response = await send('Runtime.evaluate', { expression: AUDIT, returnByValue: true });
    if (response.timeout) {
      console.log(`${viewport.name} ${path}: 审计脚本超时`);
      failures += 1;
      continue;
    }
    const report = JSON.parse(response.result.result.value);
    const flag = report.overflow ? '✗ 横向溢出' : '✓';
    console.log(`${flag} ${viewport.name.padEnd(12)} ${path.padEnd(11)} 视口=${report.viewport} 文档宽=${report.docScrollWidth}`);
    if (report.overflow) {
      failures += 1;
      for (const item of report.offenders.slice(0, 4)) {
        console.log(`      ↳ ${item.selector}  宽 ${item.width} 右边界 ${item.right}  「${item.text}」`);
      }
      for (const item of report.selfOverflow) {
        console.log(`      ⇒ 自身溢出 ${item.selector} client=${item.clientWidth} scroll=${item.scrollWidth} 「${item.text}」`);
      }
    }
    if (report.smallTargets.length) {
      for (const item of report.smallTargets) {
        console.log(`      · 小点击目标 ${item.selector} ${item.size} 「${item.text}」`);
      }
    }
  }
}

console.log(`\n结论：${failures === 0 ? '所有视口无横向溢出' : failures + ' 处需要修复'}`);
chrome.kill();
process.exit(failures === 0 ? 0 : 1);
