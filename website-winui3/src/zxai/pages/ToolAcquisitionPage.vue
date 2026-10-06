<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">{{ t('工具与获取方式', 'Tools and acquisition') }}</p>
      <h1 class="zx-h1">{{ selected?.name ?? t('找到工具，按正确方式开始', 'Find a tool and get started') }}</h1>
      <p>{{ t('便携工具、安装程序、在线工具和商店应用分别提供入口。先看使用条件，再按步骤获取；下载完成与安装完成分别确认。', 'Portable tools, installers, online tools and Store apps have different entry points. Check the requirements, then follow the acquisition steps. Downloading and installing are separate outcomes.') }}</p>
      <nav class="acquire-nav">
        <RouterLink to="/tools/download">{{ t('原厂 EXE 下载', 'Vendor EXE downloads') }}</RouterLink>
        <RouterLink to="/docs">{{ t('使用指南', 'Getting started') }}</RouterLink>
        <RouterLink v-if="selectedId" to="/tools/acquire">{{ t('查看全部获取方式', 'View all acquisition methods') }}</RouterLink>
      </nav>
    </header>
    <div class="zx-page__body" :aria-busy="loading">
      <section v-if="loading" class="zx-card acquire-status" role="status"><h2>{{ t('正在读取工具信息…', 'Loading tool information…') }}</h2></section>
      <section v-else-if="failure" class="zx-card acquire-status" role="alert">
        <h2>{{ t('暂时无法获取工具信息', 'Tool information is temporarily unavailable') }}</h2>
        <p>{{ t('文件信息未能确认，请稍后重试。不会自动切换到其他工具或来源。', 'The tool information could not be confirmed. Please try again. The page will not switch to another tool or source.') }}</p>
        <button class="zx-btn zx-btn--ghost" type="button" @click="load">{{ t('重新获取', 'Try again') }}</button>
      </section>
      <section v-else-if="selectedId && !selected" class="zx-card acquire-status" role="status">
        <h2>{{ t('没有找到这个工具', 'This tool was not found') }}</h2>
        <p>{{ t('请核对入口，或查看已有工具。', 'Check the link, or browse the available tools.') }}</p>
        <RouterLink class="zx-btn zx-btn--ghost" to="/tools/acquire">{{ t('查看工具', 'Browse tools') }}</RouterLink>
      </section>
      <template v-else>
        <div v-if="!selectedId" class="acquire-filter">
          <label for="acquire-search">{{ t('搜索工具', 'Search tools') }}</label>
          <input id="acquire-search" v-model="search" type="search" :placeholder="t('输入工具名称', 'Enter a tool name')" />
          <label for="acquire-method">{{ t('获取方式', 'Acquisition method') }}</label>
          <select id="acquire-method" v-model="filter">
            <option value="all">{{ t('可用入口', 'Available entry points') }}</option>
            <option value="owned">{{ t('枕星直连文件', 'Files served by 枕星') }}</option>
            <option value="vendor">{{ t('原厂获取', 'Vendor acquisition') }}</option>
            <option value="website">{{ t('在线使用', 'Use online') }}</option>
            <option value="store">{{ t('应用商店', 'App Store') }}</option>
            <option value="unavailable">{{ t('已停用的旧条目', 'Retired entries') }}</option>
          </select>
          <span>{{ visible.length }} {{ t('项', 'items') }}</span>
        </div>
        <p v-if="!visible.length" class="zx-note" role="status">{{ t('没有符合当前筛选的工具。', 'No tool matches the selected filter.') }}</p>
        <div :class="selectedId ? 'acquire-detail' : 'acquire-grid'">
        <article v-for="tool in visible" :key="tool.id" class="zx-card acquire-tool" :data-tool-id="tool.id">
          <header class="acquire-tool-head">
            <div><span class="acquire-badge">{{ methodName(tool.method) }}</span><h2 class="zx-h3">{{ tool.name }}</h2><p v-if="tool.version">{{ t('版本', 'Version') }} {{ tool.version }}</p></div>
            <RouterLink v-if="!selectedId" :to="`/tools/acquire/${tool.id}`">{{ t('查看步骤', 'View steps') }}</RouterLink>
          </header>
          <p>{{ copy(tool.summary) }}</p>
          <p v-if="tool.notice.zh" class="zx-note">{{ copy(tool.notice) }}</p>
          <ol v-if="selectedId && tool.steps.length" class="acquire-steps"><li v-for="(step, index) in tool.steps" :key="index">{{ copy(step) }}</li></ol>
          <div v-if="tool.files.length" class="acquire-files">
            <section v-for="file in tool.files" :key="file.url" class="acquire-file">
              <h3>{{ file.architecture === 'any' ? t('按厂商支持范围使用', 'Use within vendor support requirements') : `Windows ${file.architecture.toUpperCase()}` }}</h3>
              <p>{{ file.version }} · {{ file.format.toUpperCase() }} · {{ formatSize(file.sizeBytes) }}</p>
              <a class="zx-btn zx-btn--primary" :href="file.url" :download="file.url.split('/').at(-1)" data-owned-download>{{ t('从枕星下载', 'Download from 枕星') }}</a>
              <details><summary>{{ t('核对文件', 'Verify the file') }}</summary><p>{{ file.sizeBytes.toLocaleString() }} {{ t('字节', 'bytes') }}</p><p>SHA-256 <code>{{ file.sha256 }}</code></p></details>
            </section>
          </div>
          <a v-if="tool.action" class="zx-btn zx-btn--primary acquire-action" :href="tool.action.url" target="_blank" rel="noopener noreferrer" data-acquisition-action>{{ copy(tool.action.label) }} ↗</a>
          <p v-if="tool.method === 'unavailable'" class="acquire-retired">{{ t('此旧条目不提供安装按钮；客户端不再显示为待下载工具。', 'This retired entry has no installation button and is no longer shown as a tool awaiting download in the client.') }}</p>
          <div v-if="selectedId && tool.references.length" class="acquire-references"><span>{{ t('相关说明', 'Related information') }}</span><a v-for="ref in tool.references" :key="ref.url" :href="ref.url" target="_blank" rel="noopener noreferrer">{{ copy(ref.label) }}</a></div>
        </article>
        </div>
        <p v-if="data" class="acquire-updated">{{ t('信息更新于', 'Information updated') }} {{ new Date(data.publishedAt).toLocaleString(isEn ? 'en-US' : 'zh-CN', { timeZone: 'Asia/Shanghai' }) }} {{ t('（北京时间）', '(Beijing time)') }}</p>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import { isEn, t } from '../i18n';

type Copy = { zh: string; en: string };
type Method = 'owned' | 'vendor' | 'website' | 'store' | 'unavailable';
type Link = { label: Copy; url: string };
interface File { architecture: 'x86' | 'x64' | 'arm64' | 'any'; version: string; format: 'zip' | 'exe' | 'msi'; url: string; sizeBytes: number; sha256: string }
interface Tool { id: string; name: string; version: string; method: Method; summary: Copy; notice: Copy; steps: Copy[]; action: Link | null; files: File[]; references: Link[] }
interface Catalogue { schemaVersion: 1; revision: number; publishedAt: string; tools: Tool[] }
const route = useRoute();
const data = ref<Catalogue | null>(null);
const loading = ref(true);
const failure = ref(false);
const filter = ref<'all' | Method>('all');
const search = ref('');
const selectedId = computed(() => typeof route.params.id === 'string' ? route.params.id : '');
const selected = computed(() => data.value?.tools.find(tool => tool.id === selectedId.value));
const visible = computed(() => selectedId.value ? selected.value ? [selected.value] : [] : data.value?.tools.filter(tool => (filter.value === 'all' ? tool.method !== 'unavailable' : tool.method === filter.value) && tool.name.toLocaleLowerCase().includes(search.value.trim().toLocaleLowerCase())) ?? []);
const copy = (text: Copy) => isEn.value ? text.en : text.zh;
const methods: Method[] = ['owned', 'vendor', 'website', 'store', 'unavailable'];
const externalHosts = new Set(['api.github.com', 'apps.microsoft.com', 'assets.unigine.com', 'benchmark.unigine.com', 'benchmarks.ul.com', 'blurbusters.com', 'cdn.geekbench.com', 'chuyu.me', 'dl.ludashi.com', 'dm.weishi.360.cn', 'down.360safe.com', 'download-2.msi.com', 'download.maxon.net', 'download.msi.com', 'download.semiconductor.samsung.com', 'downloadmirror.intel.com', 'downloads.ocbase.com', 'drivers.amd.com', 'fm-us.xfjportal.com', 'forums.passmark.com', 'gamepp.com', 'geekuninstaller.com', 'getfancontrol.com', 'github.com', 'hddscan.com', 'hibitsoft.ir', 'next.itellyou.cn', 'products.s.kaspersky-labs.com', 'raw.githubusercontent.com', 's1.ssl.qhimg.com', 'semiconductor.samsung.com', 'store.steampowered.com', 'support.kaspersky.com', 'tbtool.cn', 'testufo.com', 'www.360.cn', 'www.amd.com', 'www.chuyu.me', 'www.gamepp.com', 'www.geekbench.com', 'www.guru3d.com', 'www.hddscan.com', 'www.hdtune.com', 'www.hibitsoft.ir', 'www.intel.cn', 'www.intel.com', 'www.kaspersky.com.cn', 'www.ludashi.com', 'www.maxon.net', 'www.memtest86.com', 'www.msi.com', 'www.nvidia.cn', 'www.nvidia.com', 'www.ocbase.com', 'www.pcbox.tech', 'www.primatelabs.com', 'www.seagate.com', 'www.tbtool.cn', 'www.techpowerup.com', 'www.testufo.com', 'zhenxingai.com']);
function object(value: unknown): Record<string, unknown> { if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('object'); return value as Record<string, unknown>; }
function exact(value: Record<string, unknown>, keys: string[]) { if (Object.keys(value).sort().join('|') !== [...keys].sort().join('|')) throw new Error('fields'); }
function text(value: unknown, max: number, empty = false): string { if (typeof value !== 'string' || value.length > max || (!empty && !value.trim()) || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(value)) throw new Error('text'); return value; }
function parseCopy(value: unknown): Copy { const row = object(value); exact(row, ['zh', 'en']); return { zh: text(row.zh, 1200, true), en: text(row.en, 1600, true) }; }
function safeUrl(value: unknown, owned = false): string {
  const raw = text(value, 1600); const url = new URL(raw);
  if (url.protocol !== 'https:' || url.username || url.password || url.port || !externalHosts.has(url.hostname) || raw.includes('\\') || /[\s\u0000-\u001f]/.test(raw)) throw new Error('URL');
  if (owned && (raw !== url.origin + url.pathname || url.origin !== 'https://zhenxingai.com' || url.search || url.hash || !/^\/downloads\/(tools|installers)\/(?:[A-Za-z0-9][A-Za-z0-9._-]*\/)*[A-Za-z0-9][A-Za-z0-9._-]*\.(zip|exe|msi)$/.test(url.pathname) || url.pathname.split('/').some(part => part === '.' || part === '..' || part.endsWith('.')))) throw new Error('owned URL');
  return raw;
}
function parseLink(value: unknown): Link { const row = object(value); exact(row, ['label', 'url']); const label = parseCopy(row.label); if (!label.zh.trim() || !label.en.trim()) throw new Error('link label'); return { label, url: safeUrl(row.url) }; }
function parseFile(value: unknown): File {
  const row = object(value); exact(row, ['architecture', 'version', 'format', 'url', 'sizeBytes', 'sha256']);
  if (!['x86', 'x64', 'arm64', 'any'].includes(String(row.architecture)) || !['zip', 'exe', 'msi'].includes(String(row.format)) || !Number.isSafeInteger(row.sizeBytes) || Number(row.sizeBytes) < 1 || Number(row.sizeBytes) > 2 * 1024 ** 3 || typeof row.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(row.sha256)) throw new Error('file');
  const url = safeUrl(row.url, true); if (!url.endsWith(`.${row.format}`)) throw new Error('file format');
  return { architecture: row.architecture as File['architecture'], version: text(row.version, 80), format: row.format as File['format'], url, sizeBytes: Number(row.sizeBytes), sha256: row.sha256 };
}
function parseCatalogue(value: unknown): Catalogue {
  const root = object(value); exact(root, ['schemaVersion', 'revision', 'publishedAt', 'tools']);
  const publishedAt = text(root.publishedAt, 48); if (root.schemaVersion !== 1 || !Number.isSafeInteger(root.revision) || Number(root.revision) < 1 || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?(Z|\+00:00)$/.test(publishedAt) || !Number.isFinite(Date.parse(publishedAt)) || !Array.isArray(root.tools) || root.tools.length > 256) throw new Error('catalogue');
  const ids = new Set<string>();
  const tools = root.tools.map(value => {
    const row = object(value); exact(row, ['id', 'name', 'version', 'method', 'summary', 'notice', 'steps', 'action', 'files', 'references']);
    const id = text(row.id, 80); if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(id) || ids.has(id) || !methods.includes(row.method as Method)) throw new Error('identity'); ids.add(id);
    if (!Array.isArray(row.steps) || row.steps.length > 8 || !Array.isArray(row.files) || row.files.length > 8 || !Array.isArray(row.references) || row.references.length > 8) throw new Error('arrays');
    const method = row.method as Method; const files = row.files.map(parseFile); const action = row.action === null ? null : parseLink(row.action);
    if (method === 'owned' ? !files.length || action !== null : files.length > 0) throw new Error('method files');
    if (method === 'unavailable' ? action !== null : method !== 'owned' && action === null) throw new Error('method action');
    if (method === 'store' && new URL(action!.url).hostname !== 'apps.microsoft.com') throw new Error('Store URL');
    if (new Set(files.map(file => file.url)).size !== files.length) throw new Error('duplicate file');
    const summary = parseCopy(row.summary); const notice = parseCopy(row.notice); const steps = row.steps.map(parseCopy);
    if (!summary.zh.trim() || !summary.en.trim() || Boolean(notice.zh.trim()) !== Boolean(notice.en.trim()) || steps.some(step => !step.zh.trim() || !step.en.trim())) throw new Error('copy');
    return { id, name: text(row.name, 160), version: text(row.version, 80, true), method, summary, notice, steps, action, files, references: row.references.map(parseLink) };
  });
  return { schemaVersion: 1, revision: Number(root.revision), publishedAt, tools };
}
let active: AbortController | null = null;
let disposed = false;
async function load() {
  active?.abort(); const request = new AbortController(); active = request; loading.value = true; failure.value = false; data.value = null;
  const timer = setTimeout(() => request.abort(), 15000);
  try {
    const response = await fetch('/downloads/installers/acquisition.json', { signal: request.signal, cache: 'no-store', credentials: 'omit', redirect: 'error', headers: { Accept: 'application/json' } });
    if (!response.ok || !response.headers.get('content-type')?.startsWith('application/json') || !response.body) throw new Error('response');
    const reader = response.body.getReader(); const chunks: Uint8Array[] = []; let bytes = 0;
    try { while (true) { const result = await reader.read(); if (result.done) break; bytes += result.value.byteLength; if (bytes > 1024 ** 2) { await reader.cancel(); throw new Error('size'); } chunks.push(result.value); } } finally { reader.releaseLock(); }
    const body = new Uint8Array(bytes); let offset = 0; for (const chunk of chunks) { body.set(chunk, offset); offset += chunk.byteLength; }
    const parsed = parseCatalogue(JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(body)));
    if (!disposed && active === request) data.value = parsed;
  } catch { if (!disposed && active === request) failure.value = true; }
  finally { clearTimeout(timer); if (!disposed && active === request) { loading.value = false; active = null; } }
}
function methodName(method: Method) { return method === 'owned' ? t('枕星直连文件', 'File served by 枕星') : method === 'vendor' ? t('原厂获取', 'Vendor acquisition') : method === 'website' ? t('在线使用，无需安装', 'Online tool; no installation') : method === 'store' ? t('应用商店', 'App Store') : t('旧条目已停用', 'Retired entry'); }
function formatSize(bytes: number) { return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(2)} GiB` : `${(bytes / 1024 ** 2).toFixed(1)} MiB`; }
onMounted(load);
onBeforeUnmount(() => { disposed = true; active?.abort(); });
</script>

<style scoped>
.zx-page.zx-container { padding-inline: var(--zx-gutter); }
.acquire-nav, .acquire-filter, .acquire-references { display: flex; flex-wrap: wrap; align-items: center; gap: 12px 24px; }
.acquire-nav { margin-top: 20px; }
.acquire-filter { margin-bottom: 24px; }
.acquire-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 24px; margin-bottom: 24px; }
.acquire-grid .acquire-tool { margin-bottom: 0; }
.acquire-grid .acquire-tool-head > a { flex-shrink: 0; }
.acquire-grid .acquire-action { align-self: end; }
.acquire-filter select, .acquire-filter input { font: inherit; color: var(--zx-text); background: var(--zx-surface); border: 1px solid var(--zx-border-strong); border-radius: var(--zx-radius-s); padding: 8px 12px; max-width: 100%; }
.acquire-tool, .acquire-status { display: grid; grid-template-columns: minmax(0, 1fr); min-width: 0; gap: 18px; margin-bottom: 24px; }
.acquire-status { justify-items: start; }
.acquire-tool-head { display: flex; align-items: start; justify-content: space-between; gap: 20px; }
.acquire-tool-head > div { min-width: 0; overflow-wrap: anywhere; }
.acquire-tool-head h2 { margin-top: 10px; }
.acquire-tool-head p, .acquire-updated, .acquire-retired { color: var(--zx-text-2); font-size: 14px; }
.acquire-badge { display: inline-flex; background: var(--zx-bg-soft); color: var(--zx-text); border: 1px solid var(--zx-border); padding: 4px 10px; border-radius: 20px; font-size: 13px; }
.acquire-steps { padding-left: 24px; display: grid; gap: 10px; overflow-wrap: anywhere; }
.acquire-action { justify-self: start; max-width: 100%; white-space: normal; text-align: center; }
.acquire-files { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.acquire-file { min-width: 0; padding: 20px; border-radius: var(--zx-radius-m); border: 1px solid var(--zx-border); background: var(--zx-bg-soft); display: grid; gap: 12px; justify-items: start; }
.acquire-file h3 { font-size: 17px; }
.acquire-file details { width: 100%; font-size: 14px; }
.acquire-file summary { cursor: pointer; }
.acquire-file code { overflow-wrap: anywhere; color: var(--zx-text); }
.acquire-references { border-top: 1px solid var(--zx-border); padding-top: 16px; font-size: 14px; }
@media (max-width: 760px) { .acquire-files, .acquire-grid { grid-template-columns: minmax(0, 1fr); } .acquire-tool { padding: 22px 20px; } }
</style>
