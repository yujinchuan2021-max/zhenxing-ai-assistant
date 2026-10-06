<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">{{ t('工具下载', 'Tool downloads') }}</p>
      <h1 class="zx-h1 tool-downloads__title">{{ t('原厂安装包，从枕星下载', 'Original vendor files, downloaded from 枕星') }}</h1>
      <p>{{ t('选择与你的 Windows 系统匹配的版本。文件从本站直接提供，无需先访问 GitHub；下载后按原厂说明安装或运行。', 'Choose a file that matches your Windows system. Files are served directly by this site, without visiting GitHub first. After downloading, follow the vendor’s installation or launch instructions.') }}</p>
      <div class="tool-downloads__nav">
        <RouterLink to="/download">{{ t('客户端下载', 'Client download') }}</RouterLink>
        <RouterLink to="/docs#third-party-licences">{{ t('许可与使用说明', 'Licences and usage instructions') }}</RouterLink>
        <RouterLink v-if="selectedId" to="/tools/download">{{ t('查看全部工具', 'View all tools') }}</RouterLink>
      </div>
    </header>

    <div class="zx-page__body" :aria-busy="loading">
      <section v-if="loading" class="zx-status" role="status" aria-live="polite">
        <span class="zx-status__icon"><ZxIcon name="download" /></span>
        <div><h2>{{ t('正在获取可下载的文件…', 'Loading available files…') }}</h2><p>{{ t('请稍等，文件信息确认后会显示下载入口。', 'Download links will appear once the file information is checked.') }}</p></div>
      </section>

      <section v-else-if="error" class="zx-card tool-downloads__empty" role="alert">
        <h2 class="zx-h3">{{ errorHeading }}</h2>
        <p>{{ errorMessage }}</p>
        <button type="button" class="zx-btn zx-btn--ghost" @click="loadFiles">{{ t('重新获取', 'Try again') }}</button>
      </section>

      <section v-else-if="selectedId && !visibleTools.length" class="zx-card tool-downloads__empty" role="status">
        <h2 class="zx-h3">{{ t('这个工具尚未提供下载', 'This tool is not available for download') }}</h2>
        <p>{{ t('没有找到与这个入口准确匹配的已发布文件。请核对链接，或查看全部工具。', 'No published file matches this link exactly. Check the link or view all tools.') }}</p>
        <RouterLink class="zx-btn zx-btn--ghost" to="/tools/download">{{ t('查看全部工具', 'View all tools') }}</RouterLink>
      </section>

      <section v-else-if="!visibleTools.length" class="zx-card tool-downloads__empty" role="status">
        <h2 class="zx-h3">{{ t('暂时没有可下载的文件', 'No files are currently available') }}</h2>
        <p>{{ t('文件准备完成后会在这里提供，请稍后再试。', 'Files will appear here when they are ready. Please try again later.') }}</p>
        <button type="button" class="zx-btn zx-btn--ghost" @click="loadFiles">{{ t('重新获取', 'Refresh') }}</button>
      </section>

      <template v-else>
        <section class="tool-downloads__toolbar">
          <div>
            <label for="tool-download-architecture">{{ t('你的系统架构', 'Your system architecture') }}</label>
            <select id="tool-download-architecture" v-model="architecture">
              <option value="all">{{ t('显示所有版本', 'Show all versions') }}</option>
              <option value="x64">Windows x64</option>
              <option value="arm64">Windows ARM64</option>
              <option value="x86">Windows x86</option>
            </select>
          </div>
          <p>{{ t('不确定时，打开 Windows「设置 → 系统 → 关于」，查看系统类型。安装器本身的架构可能与目标系统不同，请按下方目标系统选择。', 'If unsure, open Windows Settings → System → About and check System type. The installer executable can use a different architecture; choose the target system shown below.') }}</p>
        </section>

        <div class="tool-downloads__tools">
          <article v-for="tool in visibleTools" :key="tool.id" class="zx-card tool-downloads__tool" :data-tool-id="tool.id">
            <div class="tool-downloads__tool-head">
              <div><h2 class="zx-h3">{{ tool.name }}</h2><p>{{ toolPurpose(tool.id) }}</p></div>
              <RouterLink v-if="!selectedId" :to="`/tools/download/${tool.id}`" class="tool-downloads__product-link">{{ t('单独查看', 'View tool') }}</RouterLink>
            </div>
            <p v-if="tool.id === 'tool-powertoys'" class="zx-note tool-downloads__dependency">{{ t('PowerToys 安装需要 WebView2。系统缺少此组件时，原厂安装程序可能仍需联网获取依赖；这里提供安装包，不代表全程离线。', 'PowerToys requires WebView2. If it is missing, the vendor installer may still need network access to obtain this dependency. Providing the installer does not make the entire installation offline.') }}</p>
            <div v-for="notice in powertoysSourceNotices(tool)" :key="notice.version" class="tool-downloads__source-notice">
              <p>{{ t('许可与对应源码', 'Licences and corresponding source') }} · {{ notice.version }}</p>
              <ul><li v-for="link in notice.links" :key="link.url"><a :href="link.url">{{ link.label }}</a></li></ul>
            </div>
            <p v-if="tool.id === 'pawnio'" class="zx-note tool-downloads__dependency">{{ t('PawnIO 是硬件访问驱动。按安装程序完成权限确认；安装后还需检测驱动是否可用，必要时重启。', 'PawnIO is a hardware-access driver. Complete the installer’s permission prompts, then check that the driver is available and restart if required.') }}</p>

            <div v-if="packagesFor(tool).length" class="tool-downloads__packages">
              <section v-for="file in packagesFor(tool)" :key="file.architecture" class="tool-downloads__package">
                <div class="tool-downloads__file-head">
                  <div>
                    <h3>{{ architectureLabel(file.architecture) }}</h3>
                    <p>{{ t('版本', 'Version') }} {{ file.version }} · {{ file.type === 'portable-exe' ? t('直接运行的 EXE', 'Portable EXE') : t('原厂安装程序', 'Vendor installer') }}</p>
                  </div>
                  <a class="zx-btn zx-btn--primary" :href="file.url" :download="fileName(file)" :aria-label="`${t('下载', 'Download')} ${tool.name} ${architectureLabel(file.architecture)}`" data-vendor-download>
                    <ZxIcon name="download" />{{ t('下载', 'Download') }} {{ file.architecture === 'any' ? 'EXE' : file.architecture.toUpperCase() }}
                  </a>
                </div>
                <p class="tool-downloads__size">{{ formatSize(file.sizeBytes) }}</p>
                <details class="tool-downloads__integrity">
                  <summary>{{ t('查看文件核对信息', 'View file verification details') }}</summary>
                  <dl>
                    <dt>{{ t('文件名', 'File name') }}</dt><dd>{{ fileName(file) }}</dd>
                    <dt>{{ t('精确大小', 'Exact size') }}</dt><dd>{{ file.sizeBytes.toLocaleString(isEn ? 'en-US' : 'zh-CN') }} {{ t('字节', 'bytes') }}</dd>
                    <dt>SHA-256</dt><dd><code>{{ file.sha256 }}</code></dd>
                  </dl>
                  <p>{{ t('下载后请按这里的大小和 SHA-256 核对文件；页面显示下载入口不代表你已下载或安装完成。', 'After downloading, check the file against this size and SHA-256. A download link does not confirm that your download or installation completed.') }}</p>
                </details>
              </section>
            </div>
            <p v-else class="zx-note tool-downloads__no-package" role="status">{{ t('尚未提供与你所选系统匹配的文件，请确认架构或稍后再试。', 'No file is available for the selected system. Check the architecture or try again later.') }}</p>
          </article>
        </div>

        <section class="zx-note">
          <p>{{ t('安装包下载完成后，按原厂安装程序继续操作。账号登录、商店授权、其他依赖和重启要求仍以厂商说明为准；完成下载不等于软件已安装。', 'After downloading an installer, continue with the vendor’s installation steps. Account sign-in, Store licences, other dependencies and restart requirements follow the vendor’s instructions. A completed download does not mean the software is installed.') }}</p>
          <p class="tool-downloads__updated">{{ t('文件信息更新于', 'File information updated') }} {{ updatedAt }}</p>
        </section>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import ZxIcon from '../components/ZxIcon.vue';
import { isEn, t } from '../i18n';

type Architecture = 'x86' | 'x64' | 'arm64' | 'any';
interface VendorFile {
  architecture: Architecture;
  executableArchitecture: Exclude<Architecture, 'any'>;
  version: string;
  type: 'installer-exe' | 'portable-exe';
  url: string;
  sizeBytes: number;
  sha256: string;
}
interface VendorTool { id: string; name: string; packages: VendorFile[] }
interface VendorFiles { schemaVersion: 1; revision: number; publishedAt: string; tools: VendorTool[] }
type LoadError = 'not-published' | 'network' | 'invalid' | 'timeout';
class FileListError extends Error { constructor(readonly kind: LoadError) { super(kind); } }

const route = useRoute();
const files = ref<VendorFiles | null>(null);
const loading = ref(true);
const error = ref<LoadError | null>(null);
const architecture = ref<'all' | Architecture>('all');
const selectedId = computed(() => {
  const pathId = typeof route.params.id === 'string' ? route.params.id : '';
  if (!Object.hasOwn(route.query, 'tool')) return pathId;
  const queryId = route.query.tool;
  if (typeof queryId !== 'string' || !queryId || (pathId && pathId !== queryId)) return '!';
  return queryId;
});
const visibleTools = computed(() => files.value?.tools.filter(tool => !selectedId.value || tool.id === selectedId.value) ?? []);
const updatedAt = computed(() => files.value ? new Date(files.value.publishedAt).toLocaleString(isEn.value ? 'en-US' : 'zh-CN', { timeZone: 'Asia/Shanghai' }) + t('（北京时间）', ' (Beijing time)') : '');
const errorHeading = computed(() => error.value === 'not-published' ? t('下载文件尚未发布', 'Downloads have not been published') : t('暂时无法获取下载文件', 'Download information is temporarily unavailable'));
const errorMessage = computed(() => error.value === 'timeout'
  ? t('获取文件信息超时，请检查网络并重试。', 'The file-information request timed out. Check your connection and try again.')
  : error.value === 'invalid'
    ? t('返回的文件信息没有通过核对，已停止显示下载链接。请稍后重试。', 'The returned file information did not pass validation. Download links have been withheld. Please try again later.')
    : error.value === 'not-published'
      ? t('本站暂未提供这份下载清单，请稍后再试。', 'This download list is not available on this site yet. Please try again later.')
      : t('文件服务暂不可用，请检查网络并重试。', 'The file service is unavailable. Check your connection and try again.'));

const maxResponseBytes = 1024 * 1024;
const maxFileBytes = 2 * 1024 ** 3;
const idPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const devicePattern = /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i;
function record(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value); }
function exactFields(value: Record<string, unknown>, allowed: string[]) { return Object.keys(value).length === allowed.length && Object.keys(value).every(key => allowed.includes(key)); }
function text(value: unknown, maximum: number) { return typeof value === 'string' && value.trim().length > 0 && value.length <= maximum && !/[\u0000-\u001f\u007f]/.test(value); }
function ownedFileUrl(value: unknown): value is string {
  if (typeof value !== 'string' || !value.startsWith('https://zhenxingai.com/downloads/installers/') || !value.endsWith('.exe') || value.length > 1500) return false;
  const relative = value.slice('https://zhenxingai.com/'.length);
  return relative.length <= 240 && relative.split('/').every(part => part.length > 0 && part.length <= 120 && part !== '.' && part !== '..' && !part.endsWith('.') && /^[A-Za-z0-9_.-]+$/.test(part) && !devicePattern.test(part.split('.')[0] ?? ''));
}
function parseFiles(value: unknown): VendorFiles {
  const invalid = () => { throw new FileListError('invalid'); };
  if (!record(value) || !exactFields(value, ['schemaVersion', 'revision', 'publishedAt', 'tools']) || value.schemaVersion !== 1 || !Number.isSafeInteger(value.revision) || Number(value.revision) <= 0 || typeof value.publishedAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|\+00:00)$/.test(value.publishedAt) || !Number.isFinite(Date.parse(value.publishedAt)) || !Array.isArray(value.tools) || value.tools.length > 256) return invalid();
  const ids = new Set<string>();
  for (const tool of value.tools) {
    if (!record(tool) || !exactFields(tool, ['id', 'name', 'packages']) || typeof tool.id !== 'string' || tool.id.length > 80 || !idPattern.test(tool.id) || devicePattern.test(tool.id) || ids.has(tool.id) || !text(tool.name, 160) || !Array.isArray(tool.packages) || tool.packages.length > 4) return invalid();
    ids.add(tool.id);
    const architectures = new Set<string>();
    for (const file of tool.packages) {
      if (!record(file) || !exactFields(file, ['architecture', 'executableArchitecture', 'version', 'type', 'url', 'sizeBytes', 'sha256']) || typeof file.architecture !== 'string' || !['x86', 'x64', 'arm64', 'any'].includes(file.architecture) || architectures.has(file.architecture) || typeof file.executableArchitecture !== 'string' || !['x86', 'x64', 'arm64'].includes(file.executableArchitecture) || !text(file.version, 80) || !['installer-exe', 'portable-exe'].includes(String(file.type)) || !ownedFileUrl(file.url) || !Number.isSafeInteger(file.sizeBytes) || Number(file.sizeBytes) < 64 || Number(file.sizeBytes) > maxFileBytes || typeof file.sha256 !== 'string' || !/^[a-fA-F0-9]{64}$/.test(file.sha256)) return invalid();
      if ((file.architecture === 'x86' && file.executableArchitecture !== 'x86') || (file.architecture === 'x64' && file.executableArchitecture === 'arm64') || (file.architecture === 'any' && file.executableArchitecture !== 'x86')) return invalid();
      architectures.add(file.architecture);
    }
  }
  return value as unknown as VendorFiles;
}

let disposed = false;
let active: { controller: AbortController; timer: ReturnType<typeof setTimeout> } | null = null;
async function loadFiles() {
  if (disposed) return;
  if (active) { active.controller.abort(); clearTimeout(active.timer); }
  files.value = null;
  error.value = null;
  loading.value = true;
  let timedOut = false;
  const controller = new AbortController();
  const request = { controller, timer: setTimeout(() => { timedOut = true; controller.abort(); }, 15000) };
  active = request;
  try {
    const response = await fetch('/downloads/installers/catalog.json', { signal: controller.signal, cache: 'no-store', credentials: 'omit', redirect: 'error' });
    if (response.status === 404) throw new FileListError('not-published');
    if (response.status !== 200) throw new FileListError('network');
    if (!/^application\/json(?:\s*;|$)/i.test(response.headers.get('content-type') ?? '') || Number(response.headers.get('content-length')) > maxResponseBytes || !response.body) throw new FileListError('invalid');
    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8', { fatal: true });
    let received = 0;
    let json = '';
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) break;
        received += chunk.value.byteLength;
        if (received > maxResponseBytes) { await reader.cancel(); throw new FileListError('invalid'); }
        json += decoder.decode(chunk.value, { stream: true });
      }
      json += decoder.decode();
    } finally { reader.releaseLock(); }
    let parsed: VendorFiles;
    try { parsed = parseFiles(JSON.parse(json)); }
    catch { throw new FileListError('invalid'); }
    if (!disposed && active === request) files.value = parsed;
  } catch (failure) {
    if (!disposed && active === request) error.value = timedOut ? 'timeout' : failure instanceof FileListError ? failure.kind : 'network';
  } finally {
    clearTimeout(request.timer);
    if (!disposed && active === request) { loading.value = false; active = null; }
  }
}
onMounted(loadFiles);
onBeforeUnmount(() => { disposed = true; if (active) { active.controller.abort(); clearTimeout(active.timer); active = null; } });

function packagesFor(tool: VendorTool) { return tool.packages.filter(file => architecture.value === 'all' || file.architecture === architecture.value || file.architecture === 'any'); }
function fileName(file: VendorFile) { return file.url.split('/').at(-1) ?? ''; }
function powertoysSourceNotices(tool: VendorTool) {
  if (tool.id !== 'tool-powertoys') return [];
  const notices = new Map<string, { version: string; links: { label: string; url: string }[] }>();
  for (const file of tool.packages) {
    const directory = file.url.slice(0, file.url.lastIndexOf('/') + 1);
    if (!/^[A-Za-z0-9_.-]+$/.test(file.version) || directory !== `https://zhenxingai.com/downloads/installers/powertoys/${file.version}/`) continue;
    const links = [
      { label: t('MIT 许可', 'MIT licence'), url: `${directory}powertoys-license.txt` },
      { label: t('第三方告知', 'Third-party notices'), url: `${directory}powertoys-third-party-notices.md` },
      { label: t('PowerToys 对应源码', 'PowerToys corresponding source'), url: `${directory}powertoys-source-${file.version}.zip` },
      { label: t('源码获取说明', 'Source-availability notice'), url: `${directory}powertoys-source-availability.txt` },
    ];
    if (file.version === '0.101.2362.0') links.push({ label: t('UTF.Unknown 2.6.0 对应源码', 'UTF.Unknown 2.6.0 corresponding source'), url: `${directory}utf-unknown-source-2.6.0.zip` });
    notices.set(file.version, { version: file.version, links });
  }
  return [...notices.values()];
}
function formatSize(bytes: number) { return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(2)} GiB` : `${(bytes / 1024 ** 2).toFixed(1)} MiB`; }
function architectureLabel(value: Architecture) { return value === 'any' ? t('通用版本（按厂商说明）', 'Universal version (see vendor requirements)') : `Windows ${value.toUpperCase()}`; }
function toolPurpose(id: string) {
  if (id === 'tool-powertoys') return t('Microsoft Windows 效率工具集。', 'Microsoft productivity tools for Windows.');
  if (id === 'unigetui') return t('通过图形界面管理软件包。', 'Manage software packages through a graphical interface.');
  if (id === 'pawnio') return t('为受支持的工具提供硬件访问能力。', 'Provides hardware access for supported tools.');
  if (id === 'sandboxie') return t('按厂商说明在沙箱中运行程序。', 'Run applications in a sandbox following the vendor’s instructions.');
  if (id === 'optimizer') return t('查看和调整 Windows 系统设置。', 'Review and adjust Windows system settings.');
  return t('原厂文件，按对应版本说明安装或运行。', 'Original vendor files; follow the instructions for the selected version.');
}
</script>

<style scoped>
.tool-downloads__title { font-size: clamp(30px, 3.6vw, 44px); }
.tool-downloads__nav { display: flex; flex-wrap: wrap; gap: 12px 24px; margin-top: 20px; }
.tool-downloads__empty { display: grid; gap: 18px; justify-items: start; }
.tool-downloads__toolbar { display: grid; gap: 12px; }
.tool-downloads__toolbar > div { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.tool-downloads__toolbar label { font-weight: 700; }
.tool-downloads__toolbar select { font: inherit; color: var(--zx-text); background: var(--zx-surface); border: 1px solid var(--zx-border-strong); border-radius: var(--zx-radius-s); padding: 8px 36px 8px 12px; }
.tool-downloads__toolbar p, .tool-downloads__updated { font-size: 14px; color: var(--zx-text-2); }
.tool-downloads__tools { display: grid; gap: 24px; }
.tool-downloads__tool-head, .tool-downloads__file-head { display: flex; justify-content: space-between; align-items: start; gap: 16px; }
.tool-downloads__product-link { flex-shrink: 0; font-size: 14px; }
.tool-downloads__tool-head p { margin-top: 8px; }
.tool-downloads__dependency { margin-top: 18px; }
.tool-downloads__source-notice { margin-top: 12px; font-size: 14px; }
.tool-downloads__source-notice ul { display: flex; flex-wrap: wrap; gap: 8px 20px; list-style: none; padding: 0; margin: 8px 0 0; }
.tool-downloads__packages { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; margin-top: 22px; }
.tool-downloads__package { border: 1px solid var(--zx-border); border-radius: var(--zx-radius-m); background: var(--zx-bg-soft); padding: 20px; min-width: 0; }
.tool-downloads__file-head { flex-wrap: wrap; }
.tool-downloads__file-head h3 { font-size: 17px; }
.tool-downloads__file-head .zx-btn { min-width: 130px; }
.tool-downloads__file-head .zx-btn svg { width: 18px; height: 18px; }
.tool-downloads__size { margin-top: 14px; }
.tool-downloads__integrity { margin-top: 16px; border-top: 1px solid var(--zx-border); padding-top: 12px; font-size: 14px; color: var(--zx-text-2); }
.tool-downloads__integrity summary { cursor: pointer; color: var(--zx-text); font-weight: 500; }
.tool-downloads__integrity dl { margin: 16px 0 12px; display: grid; gap: 4px; }
.tool-downloads__integrity dt { font-weight: 700; color: var(--zx-text); }
.tool-downloads__integrity dd { margin: 0 0 8px; overflow-wrap: anywhere; }
.tool-downloads__integrity code { color: var(--zx-text); font-size: 13px; }
.tool-downloads__no-package { margin-top: 20px; }
.tool-downloads__updated { margin-top: 8px; }
@media (max-width: 760px) { .tool-downloads__packages { grid-template-columns: minmax(0, 1fr); } .tool-downloads__tool { padding: 22px 20px; } .tool-downloads__package { padding: 18px 16px; } }
</style>
