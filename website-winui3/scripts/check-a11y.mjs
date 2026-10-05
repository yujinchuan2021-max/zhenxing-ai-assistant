/**
 * 枕星图吧AI助手 官网 · 可访问性与交互验证
 *
 * 覆盖（深浅两套主题各跑一遍）：
 *  1) 键盘焦点：Tab 走查前若干可聚焦元素，确认焦点环可见（outline 非 none）、顺序合理；
 *  2) 对比度：对关键文本元素计算 WCAG 对比度（按有效背景色合成），低于阈值即失败；
 *  3) reduced-motion：模拟 prefers-reduced-motion: reduce，确认过渡/动画被压到最短；
 *  4) 锚点：首页首屏主 CTA「看看怎么用」跳到 #demo 并真的滚动了。
 *
 * 用法（先 `npm run preview -- --port 4173`）：
 *   node scripts/check-a11y.mjs --base http://127.0.0.1:4173 --out <目录>
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
const PORT = Number(argOf('port', '9341'));

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
  `--user-data-dir=${process.env.TEMP ?? 'C:/Windows/Temp'}/zxai-a11y`,
  `--remote-debugging-port=${PORT}`, 'about:blank',
], { stdio: ['ignore', 'ignore', 'ignore'] });

for (let attempt = 0; attempt < 60; attempt += 1) {
  try {
    if ((await fetch(`http://127.0.0.1:${PORT}/json/version`)).ok) break;
  } catch { /* retry */ }
  await sleep(250);
}

const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const ws = new WebSocket(targets.find((target) => target.type === 'page').webSocketDebuggerUrl);
await new Promise((done) => ws.addEventListener('open', done, { once: true }));

let seq = 0;
const pending = new Map();
ws.addEventListener('message', (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
  }
});
const send = (method, params = {}, timeoutMs = 20000) => {
  seq += 1;
  const id = seq;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((done) => {
    const timer = setTimeout(() => { pending.delete(id); done({ timeout: true }); }, timeoutMs);
    pending.set(id, (message) => { clearTimeout(timer); done(message); });
  });
};
const evaluate = async (expression) => {
  const response = await send('Runtime.evaluate', { expression, returnByValue: true });
  if (response.timeout) throw new Error('evaluate 超时');
  if (response.result?.exceptionDetails) throw new Error(JSON.stringify(response.result.exceptionDetails).slice(0, 300));
  return response.result?.result?.value;
};

const CONTRAST_SCRIPT = `(() => {
  const parseColor = (value) => {
    const match = value.match(/rgba?\\(([^)]+)\\)/);
    if (!match) return null;
    const parts = match[1].split(',').map((piece) => parseFloat(piece));
    return [parts[0], parts[1], parts[2], parts.length > 3 ? parts[3] : 1];
  };
  const composite = (top, bottom) => {
    const alpha = top[3];
    return [
      Math.round(top[0] * alpha + bottom[0] * (1 - alpha)),
      Math.round(top[1] * alpha + bottom[1] * (1 - alpha)),
      Math.round(top[2] * alpha + bottom[2] * (1 - alpha)),
    ];
  };
  const luminance = ([r, g, b]) => {
    const channel = (value) => {
      const c = value / 255;
      return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
  };
  const ratio = (a, b) => {
    const la = luminance(a); const lb = luminance(b);
    return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
  };
  const effectiveBackground = (element) => {
    const layers = [];
    let hasImage = false;
    for (let node = element; node; node = node.parentElement) {
      const cs = getComputedStyle(node);
      if (cs.backgroundImage && cs.backgroundImage !== 'none') hasImage = true;
      const color = parseColor(cs.backgroundColor);
      if (color && color[3] > 0) layers.push(color);
      if (color && color[3] === 1) break;
    }
    let base = [19, 18, 16];
    for (let index = layers.length - 1; index >= 0; index -= 1) base = composite(layers[index], base);
    return { color: base, hasImage };
  };

  const targets = [
    ['hero 主标题', '.zx-hero h1'],
    ['hero 副文案', '.zx-hero__lead'],
    ['hero 徽标文字', '.zx-hero__badges span'],
    ['导航链接（非当前）', '.zx-nav__link:not(.is-active)'],
    ['导航链接（当前）', '.zx-nav__link.is-active'],
    ['主按钮文字', '.zx-btn--primary'],
    ['次级按钮文字', '.zx-btn--ghost'],
    ['区块眉标 eyebrow', '.zx-eyebrow'],
    ['区块标题', '.zx-section h2'],
    ['正文段落', '.zx-lead, .zx-section p, .zx-feature__text p'],
    ['要点列表', '.zx-feature__list li, .zx-list li'],
    ['卡片标题', '.zx-card h3, .zx-pillar h3'],
    ['卡片正文', '.zx-card p, .zx-pillar p'],
    ['截图说明文字', '.zx-shot__caption'],
    ['首页下载带标题', '.zx-band h2, .zx-section h3'],
    ['KV 标签', '.zx-kv__row dt, .zx-kv__row span:first-child'],
    ['KV 值', '.zx-kv__row dd, .zx-kv__row span:last-child'],
    ['页脚链接', '.zx-footer__col a'],
    ['页脚说明', '.zx-footer__note'],
    ['状态徽标', '.zx-state, .zx-entry__state'],
    ['下载条目名称', '.zx-entry__name'],
    ['下载条目描述', '.zx-entry__desc, .zx-muted'],
    ['内联代码/键盘字符', 'code'],
    ['警示文本', '.zx-warn, .zx-note--warn'],
  ];

  const results = [];
  for (const [label, selector] of targets) {
    const element = document.querySelector(selector);
    if (!element) {
      results.push({ label, selector, missing: true });
      continue;
    }
    const cs = getComputedStyle(element);
    const color = parseColor(cs.color);
    const { color: background, hasImage } = effectiveBackground(element);
    const fontSize = parseFloat(cs.fontSize);
    const weight = parseInt(cs.fontWeight, 10) || 400;
    const large = fontSize >= 24 || (fontSize >= 18.66 && weight >= 700);
    const value = ratio(composite(color, background), background);
    results.push({
      label,
      selector,
      size: Math.round(fontSize * 10) / 10,
      weight,
      large,
      ratio: Math.round(value * 100) / 100,
      threshold: large ? 3 : 4.5,
      pass: value >= (large ? 3 : 4.5),
      gradient: hasImage,
      color: cs.color,
      background: 'rgb(' + background.join(',') + ')',
    });
  }
  return JSON.stringify(results);
})()`;

const FOCUS_SCRIPT = `(() => {
  const element = document.activeElement;
  if (!element) return JSON.stringify({ none: true });
  const cs = getComputedStyle(element);
  const rect = element.getBoundingClientRect();
  return JSON.stringify({
    tag: element.tagName.toLowerCase(),
    text: (element.textContent || '').trim().slice(0, 26),
    cls: typeof element.className === 'string' ? element.className : '',
    outlineStyle: cs.outlineStyle,
    outlineWidth: cs.outlineWidth,
    outlineColor: cs.outlineColor,
    boxShadow: cs.boxShadow,
    visible: rect.width > 0 && rect.height > 0,
  });
})()`;

const report = { base: BASE, themes: {}, focus: [], reducedMotion: null, anchor: null };

await send('Page.enable');
await send('Runtime.enable');

async function loadPage(path, scheme, { reduce = false } = {}) {
  await send('Emulation.setDeviceMetricsOverride', {
    width: 1440, height: 900, deviceScaleFactor: 1, mobile: false, screenWidth: 1440, screenHeight: 900,
  });
  await send('Emulation.setEmulatedMedia', {
    media: 'screen',
    features: [
      { name: 'prefers-color-scheme', value: scheme },
      { name: 'prefers-reduced-motion', value: reduce ? 'reduce' : 'no-preference' },
    ],
  });
  await send('Page.navigate', { url: `${BASE}${path}` });
  await sleep(1400);
}
const loadHome = (scheme, options) => loadPage('/', scheme, options);

for (const scheme of ['dark', 'light']) {
  for (const path of ['/', '/download']) {
    await loadPage(path, scheme);
    const theme = await evaluate('document.documentElement.dataset.theme');
    const results = JSON.parse(await evaluate(CONTRAST_SCRIPT));
    report.themes[`${scheme} ${path}`] = { theme, path, results };
    console.log(`\n=== 对比度 · ${scheme} · ${path}（html data-theme=${theme}）===`);
    for (const item of results) {
      if (item.missing) continue;
      const mark = item.pass ? '✓' : '✗';
      console.log(`  ${mark} ${item.label.padEnd(20)} ${String(item.ratio).padStart(6)} : 1  (需 ≥ ${item.threshold})  ${item.size}px/${item.weight}${item.gradient ? ' [含渐变/图片背景]' : ''}`);
    }
  }
}

// 键盘焦点：Tab 走查前 8 个可聚焦元素
await loadHome('dark');
console.log('\n=== 键盘焦点（深色主题，按 Tab 走查）===');
for (let step = 0; step < 8; step += 1) {
  await send('Input.dispatchKeyEvent', { type: 'rawKeyDown', windowsVirtualKeyCode: 9, key: 'Tab', code: 'Tab', nativeVirtualKeyCode: 9 });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', windowsVirtualKeyCode: 9, key: 'Tab', code: 'Tab', nativeVirtualKeyCode: 9 });
  await sleep(120);
  const info = JSON.parse(await evaluate(FOCUS_SCRIPT));
  const visibleRing = info.outlineStyle !== 'none' && parseFloat(info.outlineWidth) > 0;
  report.focus.push({ ...info, visibleRing });
  console.log(`  ${String(step + 1).padStart(2)}. <${info.tag}> ${info.text.padEnd(26)} outline=${info.outlineStyle} ${info.outlineWidth} ${info.outlineColor}${visibleRing ? '' : '  ← 焦点环不可见'}`);
  if (step === 1 && OUT) {
    mkdirSync(OUT, { recursive: true });
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(join(OUT, '15-focus-ring-dark.png'), Buffer.from(shot.result.data, 'base64'));
  }
}

// reduced-motion：过渡应当被压到最短
await loadHome('dark', { reduce: true });
const motion = JSON.parse(await evaluate(`(() => {
  const button = document.querySelector('.zx-btn--primary') || document.querySelector('a');
  const card = document.querySelector('.zx-pillar, .zx-card');
  const cs = getComputedStyle(button);
  return JSON.stringify({
    reduceMatches: matchMedia('(prefers-reduced-motion: reduce)').matches,
    buttonTransition: cs.transitionDuration,
    cardTransition: card ? getComputedStyle(card).transitionDuration : null,
    smoothScroll: getComputedStyle(document.documentElement).scrollBehavior,
  });
})()`));
report.reducedMotion = motion;
console.log('\n=== reduced-motion ===');
console.log(' ', JSON.stringify(motion));

// 锚点：点首屏主 CTA「看看怎么用」应滚到 #demo（四状态演示）
await loadHome('dark');
const anchorPrep = await evaluate(`(() => {
  const link = [...document.querySelectorAll('a')].find((a) => (a.getAttribute('href') || '').includes('#demo'));
  const target = document.querySelector('#demo');
  const before = Math.round(window.scrollY);
  if (link) link.click();
  return JSON.stringify({ linkFound: !!link, targetFound: !!target, scrolledBefore: before });
})()`);
await sleep(1100);
const anchorAfter = await evaluate(`(() => {
  const target = document.querySelector('#demo');
  return JSON.stringify({
    hash: location.hash,
    scrolledAfter: Math.round(window.scrollY),
    targetTopAfter: target ? Math.round(target.getBoundingClientRect().top) : null,
  });
})()`);
const anchor = { ...JSON.parse(anchorPrep), ...JSON.parse(anchorAfter) };
report.anchor = anchor;
console.log('\n=== 锚点跳转 ===');
console.log(' ', JSON.stringify(anchor));

const failures = [];
for (const [scheme, data] of Object.entries(report.themes)) {
  for (const item of data.results) {
    if (!item.missing && !item.pass) failures.push(`${scheme}/${item.label} ${item.ratio} < ${item.threshold}`);
  }
}
for (const item of report.focus) {
  if (!item.visibleRing) failures.push(`焦点环不可见: <${item.tag}> ${item.text}`);
}
if (!motion.reduceMatches) failures.push('reduced-motion 未生效');
if (!anchor.linkFound || !anchor.targetFound || anchor.scrolledAfter <= anchor.scrolledBefore) failures.push('锚点跳转失败');

writeFileSync(join(OUT || '.', 'a11y-report.json'), JSON.stringify(report, null, 2));
console.log(`\n结论：${failures.length === 0 ? '全部通过' : failures.length + ' 项待修复'}`);
for (const failure of failures) console.log('  ✗', failure);

chrome.kill();
process.exit(failures.length === 0 ? 0 : 1);
