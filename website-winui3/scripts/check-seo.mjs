/**
 * 枕星图吧AI助手 官网 · SEO / 元信息核对
 *
 * 逐路由抓取真实渲染后的 <head>，输出：
 *   title / description / keywords / og:* / twitter:* / canonical / robots / JSON-LD / html lang
 * 并按约定断言：
 *   - 域名未配置：不写 canonical 与 og:url；og:image 保持站点内相对路径；
 *   - 域名已配置（--expect-configured <base>）：canonical / og:url / og:image 必须全部是绝对地址，
 *     且 og:image 与 twitter:image 精确等于 <base>/zxai/og.png；
 *   - og:image 与 twitter:image 始终一致；
 *   - 描述文案与入口配置状态自洽（--copy none|configured，**独立于域名开关**）；
 *   - 检查是否残留上游文案、是否编造评分/下载量。
 *
 * 用法（先 `npm run preview -- --port 4173`）：
 *   node scripts/check-seo.mjs --base http://127.0.0.1:4173 --out <目录>                     # 全空态
 *   node scripts/check-seo.mjs --base http://127.0.0.1:4173 --out <目录> \
 *     --expect-configured https://zhenxingai.com --copy none                                # 域名已定、入口未定（当前交付态）
 *   node scripts/check-seo.mjs --base http://127.0.0.1:4173 --out <目录> \
 *     --expect-configured https://example.com --copy configured --report seo-report-configured.json  # A/B：入口也配置时
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
const PORT = Number(argOf('port', '9342'));
const REPORT_NAME = argOf('report', 'seo-report.json');
/** 传了它 = 当前构建是「域名已配置」分支，断言绝对地址 */
const EXPECT_CONFIGURED = argOf('expect-configured', '');
/**
 * 文案断言按「对外入口是否已配置」独立判定（none | community | configured），**不要复用域名开关**：
 * 域名已配置、但仓库/下载/文档仍是 null 是正常状态，
 * 此时描述理应继续写「未发布 / 未建立」，只有已上线的社区按「已开通」写。
 */
const COPY_MODE = argOf('copy', 'none');
const ROUTES = ['/', '/download', '/docs', '/community', '/about'];

/**
 * 描述文案 × 配置状态 的一致性断言（按路由），由 `--copy none|configured` 选择：
 * 页面文案与描述共用 `site.config.ts` 的同一来源，所以两种模式下都必须各自自洽——
 * 入口未配置时不许暗示「已开通/已发布」，入口已配置后不许再说「尚未开通/未建立」。
 *
 * 注意：`/download` 的 configured 分支还需 `status.released = true`（releaseState() 的口径），
 * 复核时把发布开关一并打开，否则描述理应仍然如实写「尚未公开发布」。
 */
const COPY_WHEN_EMPTY = {
  '/download': { must: ['尚未公开发布'], mustNot: [] },
  '/docs': { must: ['整理中'], mustNot: ['文档站已建立'] },
  '/community': { must: ['尚未开通', '尚未启用'], mustNot: ['社区已开通'] },
};
const COPY_WHEN_CONFIGURED = {
  '/download': { must: ['已公开发布'], mustNot: ['尚未公开发布'] },
  '/docs': { must: ['文档站'], mustNot: ['整理中', '文档站未建立'] },
  '/community': { must: ['Discourse'], mustNot: ['尚未开通', '尚未启用'] },
};
/**
 * 混合态（2026-09-24 社区先上线）：社区描述按「已开通」，下载与文档继续按空态。
 * `--copy community` 选它；某个入口状态变了就改这张表，别把整张表切到别的模式。
 */
const COPY_WHEN_COMMUNITY = {
  ...COPY_WHEN_EMPTY,
  '/community': COPY_WHEN_CONFIGURED['/community'],
};

// 上游站点残留文案（出现即为改造不彻底）
const UPSTREAM_LEAKS = [
  'tubawinui3.cn 官方',
  'PC硬件检测与系统维护工具集',
  '完美替代',
  '装机必备',
];

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
  `--user-data-dir=${process.env.TEMP ?? 'C:/Windows/Temp'}/zxai-seo`,
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

const report = { base: BASE, routes: [], failures: [] };

for (const route of ROUTES) {
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false, screenWidth: 1440, screenHeight: 900 });
  await send('Page.navigate', { url: `${BASE}${route}` });
  await sleep(1300);
  const raw = await evaluate(`(() => {
    const meta = (selector) => document.querySelector(selector)?.getAttribute('content') ?? null;
    const link = (rel) => document.querySelector('link[rel="' + rel + '"]')?.getAttribute('href') ?? null;
    const jsonLd = [...document.querySelectorAll('script[type="application/ld+json"]')].map((node) => node.textContent);
    const text = document.body.innerText;
    return JSON.stringify({
      url: location.href,
      lang: document.documentElement.lang,
      title: document.title,
      description: meta('meta[name="description"]'),
      keywords: meta('meta[name="keywords"]'),
      robots: meta('meta[name="robots"]'),
      ogTitle: meta('meta[property="og:title"]'),
      ogDescription: meta('meta[property="og:description"]'),
      ogType: meta('meta[property="og:type"]'),
      ogImage: meta('meta[property="og:image"]'),
      ogLocale: meta('meta[property="og:locale"]'),
      ogSiteName: meta('meta[property="og:site_name"]'),
      ogUrl: meta('meta[property="og:url"]'),
      twitterCard: meta('meta[name="twitter:card"]'),
      twitterImage: meta('meta[name="twitter:image"]'),
      canonical: link('canonical'),
      favicon: link('icon'),
      themeColor: meta('meta[name="theme-color"]'),
      htmlTheme: document.documentElement.dataset.theme,
      jsonLd,
      bodySnippet: text.slice(0, 240).replace(/\\s+/g, ' '),
    });
  })()`);
  const data = JSON.parse(raw);
  report.routes.push(data);

  const failures = [];
  if (!data.title || !data.title.includes('枕星图吧AI助手')) failures.push('title 未包含品牌名');
  if (!data.description) failures.push('缺少 description');
  if (EXPECT_CONFIGURED) {
    // 域名已配置分支：canonical / og:url / og:image 必须全部升级为绝对地址
    const expected = EXPECT_CONFIGURED.endsWith('/') ? EXPECT_CONFIGURED : `${EXPECT_CONFIGURED}/`;
    if (!data.canonical?.startsWith(expected)) failures.push(`已配置域名却无绝对 canonical: ${data.canonical}`);
    if (!data.ogUrl?.startsWith(expected)) failures.push(`已配置域名却无绝对 og:url: ${data.ogUrl}`);
    if (!data.ogImage?.startsWith(expected)) failures.push(`已配置域名却无绝对 og:image: ${data.ogImage}`);
    if (!data.twitterImage?.startsWith(expected)) failures.push(`已配置域名却无绝对 twitter:image: ${data.twitterImage}`);
    // 精确值：两图必须等于 <域名>/zxai/og.png（不只是以域名开头）
    const expectedImage = `${expected}zxai/og.png`;
    if (data.ogImage !== expectedImage) failures.push(`og:image 应精确等于 ${expectedImage}，实际 ${data.ogImage}`);
    if (data.twitterImage !== expectedImage) failures.push(`twitter:image 应精确等于 ${expectedImage}，实际 ${data.twitterImage}`);
  } else {
    if (data.canonical) failures.push(`未配置域名却写了 canonical: ${data.canonical}`);
    if (data.ogUrl) failures.push(`未配置域名却写了 og:url: ${data.ogUrl}`);
    if (data.ogImage && /^https?:\/\//.test(data.ogImage)) failures.push(`og:image 是绝对地址（域名未配置）: ${data.ogImage}`);
    // 未配置时保持站点内相对路径，且与 index.html 声明一致
    if (data.ogImage !== '/zxai/og.png') failures.push(`未配置时 og:image 应为 /zxai/og.png，实际 ${data.ogImage}`);
  }
  if ((data.ogImage ?? null) !== (data.twitterImage ?? null)) {
    failures.push(`og:image 与 twitter:image 不一致: ${data.ogImage} / ${data.twitterImage}`);
  }
  // description 必须与当前配置状态自洽（文案来源见 site.config.ts 的 *Copy() 函数；模式由 --copy 决定）
  const copyExpect =
    (COPY_MODE === 'configured'
      ? COPY_WHEN_CONFIGURED
      : COPY_MODE === 'community'
        ? COPY_WHEN_COMMUNITY
        : COPY_WHEN_EMPTY)[route] ?? { must: [], mustNot: [] };
  for (const phrase of copyExpect.must) {
    if (!(data.description || '').includes(phrase)) {
      failures.push(`描述与当前配置不符：应包含「${phrase}」`);
    }
  }
  for (const phrase of copyExpect.mustNot) {
    if ((data.description || '').includes(phrase)) {
      failures.push(`描述仍写着与配置状态矛盾的「${phrase}」`);
    }
  }
  if (data.lang !== 'zh-CN') failures.push(`html lang = ${data.lang}`);
  for (const leak of UPSTREAM_LEAKS) {
    if (data.bodySnippet.includes(leak) || (data.description || '').includes(leak)) failures.push(`疑似上游文案残留: ${leak}`);
  }
  // 结构化数据不得出现评分/下载量等编造字段
  for (const block of data.jsonLd) {
    if (/"aggregateRating"|"ratingValue"|"reviewCount"|"downloadCount"/.test(block)) {
      failures.push('JSON-LD 含评分/下载量字段（本站不编造这类数据）');
    }
  }
  data.failures = failures;
  report.failures.push(...failures.map((failure) => `${route}: ${failure}`));

  console.log(`\n=== ${route} ===`);
  console.log('  title      :', data.title);
  console.log('  description:', (data.description || '').slice(0, 110));
  console.log('  og         :', JSON.stringify({ title: data.ogTitle, type: data.ogType, image: data.ogImage, locale: data.ogLocale, siteName: data.ogSiteName }));
  console.log('  twitter    :', JSON.stringify({ card: data.twitterCard, image: data.twitterImage }));
  console.log('  canonical  :', data.canonical, '| og:url:', data.ogUrl, '| robots:', data.robots);
  console.log('  json-ld    :', data.jsonLd.length ? data.jsonLd.map((block) => block.slice(0, 90)).join(' | ') : '无');
  console.log('  断言       :', failures.length ? `✗ ${failures.join('; ')}` : '✓ 全部通过');
}

if (OUT) {
  mkdirSync(OUT, { recursive: true });
  writeFileSync(join(OUT, REPORT_NAME), JSON.stringify(report, null, 2));
}

console.log(
  `\n模式：${EXPECT_CONFIGURED ? `域名已配置（${EXPECT_CONFIGURED}）` : '域名未配置'}` +
    ` / 入口文案断言：${
      COPY_MODE === 'configured' ? '已配置' : COPY_MODE === 'community' ? '混合态（社区已上线）' : '未配置（空态）'
    }`,
);
console.log(`结论：${report.failures.length === 0 ? 'SEO 元信息符合约定' : report.failures.length + ' 项不通过'}`);
chrome.kill();
process.exit(report.failures.length === 0 ? 0 : 1);
