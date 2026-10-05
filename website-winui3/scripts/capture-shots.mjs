/**
 * 枕星图吧AI助手 官网 · 本地预览截图脚本（CDP 驱动无头 Chrome）
 *
 * 用途：对本地 preview / dev 服务生成「桌面 + 手机 × 深色 + 浅色」的验收截图，
 * 并顺带做几项运行时断言（主题属性、资源加载错误、控制台异常）。
 *
 * 用法（先 `npm run preview -- --port 4173`）：
 *   node scripts/capture-shots.mjs --base http://127.0.0.1:4173 --out <输出目录>
 *
 * 不依赖任何 npm 包：用 Node 20+ 内置的 WebSocket 直连 DevTools 协议。
 */

import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const index = args.indexOf(`--${name}`);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};

const BASE = argOf('base', 'http://127.0.0.1:4173');
const OUT = resolve(argOf('out', 'shots'));
const PORT = Number(argOf('port', '9335'));

const CHROME_CANDIDATES = [
  process.env.CHROME_PATH,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
].filter(Boolean);

const chromePath = CHROME_CANDIDATES.find((path) => existsSync(path));
if (!chromePath) {
  console.error('找不到 Chrome/Edge 可执行文件；可用 CHROME_PATH 环境变量指定。');
  process.exit(2);
}

mkdirSync(OUT, { recursive: true });
const profileDir = join(OUT, '.chrome-profile');

const chrome = spawn(chromePath, [
  '--headless=new',
  '--disable-gpu',
  '--no-first-run',
  '--no-default-browser-check',
  '--hide-scrollbars',
  '--force-color-profile=srgb',
  `--user-data-dir=${profileDir}`,
  `--remote-debugging-port=${PORT}`,
  'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });

let chromeStderr = '';
chrome.stderr.on('data', (chunk) => { chromeStderr += chunk.toString(); });

async function waitForDevTools() {
  for (let attempt = 0; attempt < 60; attempt += 1) {
    try {
      const response = await fetch(`http://127.0.0.1:${PORT}/json/version`);
      if (response.ok) return response.json();
    } catch {
      /* not ready yet */
    }
    await sleep(250);
  }
  throw new Error(`DevTools 未就绪：\n${chromeStderr}`);
}

class Cdp {
  constructor(ws) {
    this.ws = ws;
    this.id = 0;
    this.pending = new Map();
    this.events = [];
    ws.addEventListener('message', (event) => {
      const message = JSON.parse(event.data);
      if (message.id && this.pending.has(message.id)) {
        const { resolve: done, reject } = this.pending.get(message.id);
        this.pending.delete(message.id);
        if (message.error) reject(new Error(JSON.stringify(message.error)));
        else done(message.result);
      } else if (message.method) {
        this.events.push(message);
      }
    });
  }

  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((done, fail) => {
      ws.addEventListener('open', done, { once: true });
      ws.addEventListener('error', fail, { once: true });
    });
    return new Cdp(ws);
  }

  send(method, params = {}) {
    this.id += 1;
    const id = this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((done, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`CDP 命令超时（20s）: ${method}`));
      }, 20000);
      this.pending.set(id, {
        resolve: (value) => { clearTimeout(timer); done(value); },
        reject: (error) => { clearTimeout(timer); reject(error); },
      });
    });
  }

  close() {
    this.ws.close();
  }
}

const IGNORED_CONSOLE = [/favicon/i];

async function capture(cdp, { name, url, width, height, mobile, scheme, clickSelector, extraWait = 400, dsf = 1, fullPage = true }) {
  const errors = [];
  const onEvent = (message) => {
    if (message.method === 'Runtime.exceptionThrown') {
      errors.push(`exception: ${message.params.exceptionDetails?.text ?? 'unknown'}`);
    }
    if (message.method === 'Log.entryAdded' && message.params.entry.level === 'error') {
      const text = message.params.entry.text ?? '';
      if (!IGNORED_CONSOLE.some((pattern) => pattern.test(text))) errors.push(`log: ${text}`);
    }
  };
  const listener = (event) => onEvent(JSON.parse(event.data));
  cdp.ws.addEventListener('message', listener);
  const startIndex = cdp.events.length;

  const step = async (label, method, params = {}) => {
    const response = await cdp.send(method, params);
    console.log(`   [${label}] ${method} ok`);
    return response;
  };

  await step('nav', 'Page.navigate', { url });
  // 导航提交前发起 Runtime.evaluate 会拿不到响应（实测挂死）——先等一会儿再轮询
  await sleep(700);
  // 不用 loadEventFired 事件（有「监听注册晚于事件」的竞态），直接轮询 readyState
  let ready = false;
  for (let attempt = 0; attempt < 120; attempt += 1) {
    const state = await step(`ready-${attempt}`, 'Runtime.evaluate', {
      expression: 'document.readyState',
      returnByValue: true,
    });
    if (state.result.value === 'complete') {
      ready = true;
      break;
    }
    await sleep(100);
  }
  if (!ready) errors.push('页面 12 秒内未到达 readyState=complete');

  // 视口与配色方案模拟放在导航之后（先设后导航会让首个 evaluate 丢响应）
  await step('metrics', 'Emulation.setDeviceMetricsOverride', {
    width,
    height,
    deviceScaleFactor: dsf,
    mobile,
    screenWidth: width,
    screenHeight: height,
  });
  await step('media', 'Emulation.setEmulatedMedia', {
    media: 'screen',
    features: [{ name: 'prefers-color-scheme', value: scheme }],
  });
  await sleep(extraWait);

  if (clickSelector) {
    await cdp.send('Runtime.evaluate', {
      expression: `document.querySelector(${JSON.stringify(clickSelector)})?.click()`,
    });
    await sleep(350);
  }

  // 懒加载图片：真实浏览会滚动，截图同样先滚动一遍触发加载（否则整页截图里是空框）
  const firstMetrics = await cdp.send('Page.getLayoutMetrics');
  const pageHeight = Math.ceil((firstMetrics.cssContentSize ?? firstMetrics.contentSize).height);
  // ⚠ 站点 CSS 有 scroll-behavior: smooth：滚动预热后直接 scrollTo(0,0) 时页面「还在回家的路上」，
  // 首屏截图会拍到滚动中途的位置（曾把主标题滚出画面、看起来不像首屏）。
  // 处理：临时把 scroll-behavior 改成 auto → 瞬时复位 → 轮询断言 scrollY===0 再截图。
  await cdp.send('Runtime.evaluate', {
    expression: `(() => { document.documentElement.style.scrollBehavior = 'auto'; return true; })()`,
    returnByValue: true,
  });
  for (let y = 0; y < pageHeight; y += height) {
    await cdp.send('Runtime.evaluate', { expression: `window.scrollTo({ top: ${y}, behavior: 'instant' })` });
    await sleep(140);
  }
  await cdp.send('Runtime.evaluate', { expression: `window.scrollTo({ top: 0, behavior: 'instant' })` });
  let scrollY = -1;
  for (let attempt = 0; attempt < 25; attempt += 1) {
    const probe = await cdp.send('Runtime.evaluate', { expression: 'window.scrollY', returnByValue: true });
    scrollY = Number(probe.result.value);
    if (scrollY === 0) break;
    await sleep(80);
  }
  if (scrollY !== 0) {
    errors.push(`首屏复位失败：scrollY=${scrollY}（首屏截图会拍到滚动中途的位置）`);
  }
  await sleep(120);

  // 等字体与图片就绪：用轮询而不是 awaitPromise
  //（awaitPromise 一旦返回的 promise 不落定，CDP 侧会一直挂着不给响应）
  for (let attempt = 0; attempt < 80; attempt += 1) {
    const probe = await cdp.send('Runtime.evaluate', {
      expression: `JSON.stringify({
        fonts: document.fonts ? document.fonts.status : 'unsupported',
        pending: document.images ? [...document.images].filter((img) => img.complete && img.naturalWidth > 0).length + '/' + document.images.length : '0/0'
      })`,
      returnByValue: true,
    });
    const info = JSON.parse(probe.result.value);
    const [loaded, total] = info.pending.split('/').map(Number);
    if (info.fonts === 'loaded' && loaded === total) {
      console.log(`   [assets] 就绪（第 ${attempt + 1} 次轮询，图片 ${loaded}/${total}）`);
      break;
    }
    await sleep(150);
  }
  await sleep(120);

  const state = await cdp.send('Runtime.evaluate', {
    expression: `JSON.stringify({
      theme: document.documentElement.dataset.theme,
      mode: document.documentElement.dataset.themeMode,
      title: document.title,
      navOpen: !!document.querySelector('.zx-nav.is-open'),
      bodyW: document.body.scrollWidth,
      viewportW: window.innerWidth,
      overflowX: document.body.scrollWidth > window.innerWidth,
      font: document.documentElement.dataset.font,
      fontLoaded: document.fonts
        ? [...document.fonts].filter((face) => face.status === 'loaded').map((face) => face.family + ' ' + face.weight)
        : null
    })`,
    returnByValue: true,
  });

  // 整页截图：sticky/fixed 元素在 captureBeyondViewport 下不会被正确绘制（顶栏会变成空条），
  // 截图前把它们归位成静态流；视口截图不动，保持真实渲染。
  if (fullPage) {
    await cdp.send('Runtime.evaluate', {
      expression: `(() => {
        if (document.getElementById('zx-capture-reset')) return true;
        const style = document.createElement('style');
        style.id = 'zx-capture-reset';
        style.textContent = '.zx-header, .zx-nav { position: static !important; }';
        document.head.appendChild(style);
        return true;
      })()`,
      returnByValue: true,
    });
    await sleep(150);
  }

  const metrics = await cdp.send('Page.getLayoutMetrics');
  const size = metrics.cssContentSize ?? metrics.contentSize;
  const shot = fullPage
    ? await cdp.send('Page.captureScreenshot', {
      format: 'png',
      captureBeyondViewport: true,
      clip: {
        x: 0,
        y: 0,
        width: Math.ceil(size.width),
        height: Math.ceil(size.height),
        scale: 1,
      },
    })
    : await cdp.send('Page.captureScreenshot', { format: 'png' });

  const file = join(OUT, `${name}.png`);
  writeFileSync(file, Buffer.from(shot.data, 'base64'));
  cdp.ws.removeEventListener('message', listener);
  cdp.events.splice(startIndex);
  console.log(`${name}: ${Math.ceil(size.width)}x${Math.ceil(size.height)} -> ${file}`);
  console.log(
    `   state=${state.result.value}`,
  );
  console.log(`   scrollY=${scrollY}（首屏复位断言${scrollY === 0 ? '通过' : '失败'}）${errors.length ? `\n   errors=${JSON.stringify(errors)}` : ''}`);
  return { name, file, errors, size, scrollY, fullPage: !!fullPage };
}

const combos = [
  // 全页（DSF 1，用于整体版式核对与交付）
  { name: '01-desktop-dark-home', url: `${BASE}/`, width: 1440, height: 900, mobile: false, scheme: 'dark' },
  { name: '02-desktop-light-home', url: `${BASE}/`, width: 1440, height: 900, mobile: false, scheme: 'light' },
  { name: '03-desktop-dark-download', url: `${BASE}/download`, width: 1440, height: 900, mobile: false, scheme: 'dark' },
  { name: '04-desktop-dark-about', url: `${BASE}/about`, width: 1440, height: 900, mobile: false, scheme: 'dark' },
  { name: '05-mobile-dark-home', url: `${BASE}/`, width: 390, height: 844, mobile: true, scheme: 'dark' },
  { name: '06-mobile-light-home', url: `${BASE}/`, width: 390, height: 844, mobile: true, scheme: 'light' },
  {
    name: '07-mobile-light-nav-open',
    url: `${BASE}/`,
    width: 390,
    height: 844,
    mobile: true,
    scheme: 'light',
    clickSelector: '.zx-menu-btn',
    dsf: 2,
    fullPage: false,
  },
  { name: '08-desktop-light-community', url: `${BASE}/community`, width: 1440, height: 900, mobile: false, scheme: 'light' },
  {
    name: '09-mobile-dark-nav-open',
    url: `${BASE}/`,
    width: 390,
    height: 844,
    mobile: true,
    scheme: 'dark',
    clickSelector: '.zx-menu-btn',
    dsf: 2,
    fullPage: false,
  },
  { name: '10-desktop-dark-docs', url: `${BASE}/docs`, width: 1440, height: 900, mobile: false, scheme: 'dark' },
  // 首屏（DSF 2，用于设计细节核对）
  { name: '11-hero-desktop-dark', url: `${BASE}/`, width: 1440, height: 900, mobile: false, scheme: 'dark', dsf: 2, fullPage: false },
  { name: '12-hero-desktop-light', url: `${BASE}/`, width: 1440, height: 900, mobile: false, scheme: 'light', dsf: 2, fullPage: false },
  { name: '13-hero-mobile-dark', url: `${BASE}/`, width: 390, height: 844, mobile: true, scheme: 'dark', dsf: 2, fullPage: false },
  { name: '14-hero-mobile-light', url: `${BASE}/`, width: 390, height: 844, mobile: true, scheme: 'light', dsf: 2, fullPage: false },
];

async function main() {
  const watchdog = setTimeout(() => {
    console.error('看门狗触发：总时长超过 240 秒，强制退出。');
    chrome.kill();
    process.exit(1);
  }, 240000);

  const version = await waitForDevTools();
  console.log(`浏览器: ${version.Browser}`);

  const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
  const page = targets.find((target) => target.type === 'page');
  if (!page) throw new Error(`没有可用的 page target：${JSON.stringify(targets.map((t) => t.type))}`);
  const cdp = await Cdp.connect(page.webSocketDebuggerUrl);
  console.log('已连接页面调试通道');

  await cdp.send('Page.enable');
  await cdp.send('Runtime.enable');
  await cdp.send('Log.enable');

  const results = [];
  for (const combo of combos) {
    console.log(`-- ${combo.name}`);
    results.push(await capture(cdp, combo));
  }

  // 主题切换功能断言：点击「深色」→「浅色」→「跟随系统」，读回 data-theme 与 localStorage
  await cdp.send('Emulation.setEmulatedMedia', {
    media: 'screen',
    features: [{ name: 'prefers-color-scheme', value: 'dark' }],
  });
  await cdp.send('Page.navigate', { url: `${BASE}/` });
  await sleep(600);
  const toggle = await cdp.send('Runtime.evaluate', {
    expression: `(async () => {
      const read = () => JSON.stringify({
        theme: document.documentElement.dataset.theme,
        mode: document.documentElement.dataset.themeMode,
        stored: localStorage.getItem('zxai-theme'),
        checked: [...document.querySelectorAll('.zx-theme__btn')]
          .map((b) => b.getAttribute('aria-checked')),
      });
      const buttons = [...document.querySelectorAll('.zx-theme__btn')];
      const before = read();
      buttons[1].click();               // 浅色
      await new Promise((r) => setTimeout(r, 120));
      const light = read();
      buttons[2].click();               // 深色
      await new Promise((r) => setTimeout(r, 120));
      const dark = read();
      buttons[0].click();               // 跟随系统
      await new Promise((r) => setTimeout(r, 120));
      const system = read();
      return JSON.stringify({ before, light, dark, system });
    })()`,
    awaitPromise: true,
    returnByValue: true,
  });
  console.log('\nTheme toggle assertion:', toggle.result.value);

  const allErrors = results.flatMap((result) => result.errors);
  console.log(`\n共 ${results.length} 张截图；运行时错误 ${allErrors.length} 条。`);
  const heroShots = results.filter((result) => !result.fullPage);
  console.log(
    `首屏截图（视口）${heroShots.length} 张，scrollY 断言：${heroShots.every((result) => result.scrollY === 0) ? '全部为 0（真首屏）' : '存在非 0，见下'}`,
  );
  writeFileSync(join(OUT, 'capture-report.json'), JSON.stringify({
    base: BASE,
    browser: version.Browser,
    shots: results.map((result) => ({
      name: result.name,
      size: result.size,
      fullPage: result.fullPage,
      /** 截图前页面滚动位置：视口截图必须为 0（首屏），否则拍到的是滚动中途 */
      scrollY: result.scrollY,
      errors: result.errors,
    })),
    themeToggle: toggle.result.value,
  }, null, 2));

  cdp.close();
  chrome.kill();
  clearTimeout(watchdog);
}

main().catch((error) => {
  console.error(error);
  chrome.kill();
  process.exit(1);
});
