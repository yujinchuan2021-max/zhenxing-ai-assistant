<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">{{ t('下载与开始使用', 'Download and get started') }}</p>
      <h1 class="zx-h1" style="font-size: clamp(30px, 3.6vw, 44px)">{{ t('下载与发布状态', 'Download and release status') }}</h1>
      <p>
        {{ t('V0.1 · r10 公开预览版，2026 年 10 月 5 日发布。下载完整 ZIP，解压后即可从桌面启动程序进入。', 'V0.1 · r10 public preview, released on 5 October 2026. Download the complete ZIP, extract it and run the desktop launcher.') }}
      </p>
    </header>

    <div class="zx-page__body">
      <!-- 当前状态（文案由 releaseState() 统一决定：发布开关 + 下载入口一致才算已发布） -->
      <section class="zx-status">
        <span class="zx-status__icon"><ZxIcon name="info" /></span>
        <div>
          <h2>{{ t('当前状态：', 'Status: ') }}{{ status().label }}</h2>
          <p>{{ status().note }}</p>
        </div>
      </section>

      <!-- 入口清单：已配置的入口渲染真链接，未配置的渲染诚实空态（同一套判断，见 ZxLinkOr） -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('下载 r10 完整包', 'Download the complete r10 package') }}</h2>
        <div class="zx-entry-list" style="margin-top: 0">
          <div class="zx-entry">
            <ZxIcon name="download" style="width: 18px; height: 18px; color: var(--zx-text-3)" />
            <span class="zx-entry__label">{{ t('Windows x64 便携版', 'Windows x64 portable build') }}</span>
            <ZxLinkOr
              name="download"
              :href="status().released ? siteConfig.links.download : null"
              :text="t('从官网下载完整 ZIP', 'Download the complete ZIP from this site')"
              :fallback="t('尚未开放下载', 'Download not open yet')"
              :external="false"
              @activate="trackDownloadClick('download-page-entry')"
            />
          </div>
          <div v-if="status().released && isConfigured(siteConfig.links.repo)" class="zx-entry">
            <ZxIcon name="terminal" style="width: 18px; height: 18px; color: var(--zx-text-3)" />
            <span class="zx-entry__label">{{ t('GitHub 源码', 'GitHub source') }}</span>
            <ZxLinkOr
              name="repo"
              :href="siteConfig.links.repo"
              :text="t('在 GitHub 查看源码', 'View source on GitHub')"
              fallback=""
            />
          </div>
          <div class="zx-entry">
            <ZxIcon name="book" style="width: 18px; height: 18px; color: var(--zx-text-3)" />
            <span class="zx-entry__label">{{ t('本次更新与已知问题', 'Changes and known issues') }}</span>
            <ZxLinkOr name="releaseNotes" :href="siteConfig.links.releaseNotes" :text="t('查看 r10 更新说明', 'Read the r10 release notes')" :fallback="t('暂不可用', 'Unavailable')" />
          </div>
          <div class="zx-entry">
            <ZxIcon name="users" style="width: 18px; height: 18px; color: var(--zx-text-3)" />
            <span class="zx-entry__label">{{ t('社区入口', 'Community') }}</span>
            <ZxLinkOr
              name="community"
              :href="siteConfig.links.community"
              :text="t('进入枕星社区', 'Open 枕星AI社区')"
              :fallback="t('Discourse 论坛 · 地址已准备，尚未开通', 'Discourse forum · address prepared, not open yet')"
            />
          </div>
        </div>
        <div v-if="status().released" class="zx-kv" style="margin-top: 18px">
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('版本', 'Version') }}</span><span class="zx-kv__val">{{ t(siteConfig.releaseArtifact.version ?? '', siteConfig.releaseArtifact.versionEn ?? '') }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('发布日期', 'Released') }}</span><span class="zx-kv__val">2026-10-05 {{ t('（北京时间）', '(Beijing time)') }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('文件大小', 'File size') }}</span><span class="zx-kv__val">{{ formatSize(siteConfig.releaseArtifact.sizeBytes) }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">SHA-256</span><code class="zx-kv__val" style="overflow-wrap: anywhere">{{ siteConfig.releaseArtifact.sha256 }}</code></div>
        </div>
        <p v-else class="zx-note" style="margin-top: 18px">{{ t('下载文件和校验信息确认后会在这里一起公布。', 'The download file and checksums will be published here together once confirmed.') }}</p>
        <p class="zx-note" style="margin-top: 12px">
          {{ t('官网提供已校验的完整包镜像，GitHub 发布页保留备用下载。页面只统计入口点击，', 'This site provides a verified package mirror; the GitHub release remains a backup. The page counts entry clicks; ') }}
          <strong>{{ t('点击不代表下载完成', 'a click does not confirm a completed download') }}</strong>{{ t('；下载文件的信息可在 GitHub 核对。数据说明详见', '. File information can be checked on GitHub. See the') }}
          <RouterLink to="/about">{{ t('「关于」页的数据与统计', 'data and analytics section on the About page') }}</RouterLink>{{ t('。', '.') }}
        </p>
      </section>

      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('第一次启动，按这三步', 'First launch in three steps') }}</h2>
        <ol class="zx-list zx-list--dense">
          <li>{{ t('关闭正在运行的旧客户端。将完整 ZIP 解压到一个新文件夹，保留全部子目录与文件。', 'Close the running older client. Extract the complete ZIP into a new folder, preserving all files and subfolders.') }}</li>
          <li>{{ t('运行根目录的「枕星图吧AI助手.exe」。请使用这个启动入口，不要从压缩包内运行，也不要只复制一个 EXE。', 'Run “枕星图吧AI助手.exe” in the extracted root folder. Use this launcher; do not run from inside the archive or copy just one EXE.') }}</li>
          <li>{{ t('如需 AI，进入“设置 → AI 与 Agent”，保存接口、模型与 Agent 并测试连接，再在首页说出目标。硬件信息、工具库和官方资讯可先直接查看。', 'For AI features, open Settings → AI & Agent, save the endpoint, model and agent, then test the connection and describe a goal on the home page. Hardware information, the tool library and official news can be viewed first without AI setup.') }}</li>
        </ol>
        <p style="margin-top: 14px"><RouterLink to="/docs">{{ t('完整新手指南 →', 'Full getting-started guide →') }}</RouterLink></p>
      </section>

      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('r10 更新了什么', 'What changed in r10') }}</h2>
        <p>{{ t('本轮重点修复社区注册、登录中的授权会话衔接。受支持的官方授权继续在同一个内置浏览器中完成；出现外部登录要求时，会明确提供重新登录入口。此前目标工作台、深浅主题修复、技能库瀑布流和桌面 Agent 优先等改动保留。', 'This update focuses on session continuity during community registration and login. Supported official authorization stays in the same internal browser; flows requiring an external browser show an explicit restart option. The goal workbench, light/dark theme fixes, masonry skill library and desktop-agent preference remain available.') }}</p>
        <p class="zx-note" style="margin-top: 14px">{{ t('社区相关测试、隔离浏览器功能用例和离线运行时检查已通过；真实账号注册与登录仍待复测，部分原生环境退出问题尚未完成验收。具体测试范围与已知限制请阅读 r10 更新说明。', 'Community logic tests, isolated browser functional cases and offline runtime checks passed. Real-account registration and login still need retesting, and native exit issues in some environments remain unresolved. Read the r10 release notes for the full validation scope and known limitations.') }}</p>
      </section>

      <!-- 系统要求 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('系统要求（客户端）', 'System requirements (client)') }}</h2>
        <div class="zx-kv">
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('操作系统', 'OS') }}</span><span class="zx-kv__val">{{ facts().minOs }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('架构', 'Architecture') }}</span><span class="zx-kv__val">{{ status().released ? 'x64' : facts().archs }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('运行时', 'Runtime') }}</span><span class="zx-kv__val">{{ facts().runtime }}</span></div>
          <div class="zx-kv__row">
            <span class="zx-kv__key">{{ t('AI 模型', 'AI model') }}</span>
            <span class="zx-kv__val">{{ t('在统一 AI 设置选择接口、模型与 Agent，云端服务使用你的账号或 Key；兼容接口与本地模型需实际测试连接，硬件也要适合。不使用 AI 仍可查看资讯、硬件和工具库。', 'Choose the endpoint, model and agent in unified AI settings. Cloud services use your account or key; compatible APIs and local models require connection tests and suitable hardware. News, hardware and the tool library can be viewed without AI.') }}</span>
          </div>
        </div>
      </section>

      <!-- 常见问题 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('下载与使用常见问题', 'Download and usage FAQ') }}</h2>
        <div class="zx-grid-2">
          <article v-for="item in faq" :key="item.question" class="zx-card"><h3>{{ item.question }}</h3><p>{{ item.answer }}</p></article>
        </div>
        <p style="margin-top: 18px"><a :href="siteConfig.links.issues ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('在 GitHub 反馈问题 →', 'Report an issue on GitHub →') }}</a> · <RouterLink to="/about">{{ t('来源、许可与数据说明 →', 'Attribution, licensing and data →') }}</RouterLink></p>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import ZxIcon from '../components/ZxIcon.vue';
import ZxLinkOr from '../components/ZxLinkOr.vue';
import { facts, isConfigured, releaseState, siteConfig } from '../site.config';
import { trackDownloadClick } from '../analytics';
import { isEn, t } from '../i18n';

const status = () => releaseState();
const faq = computed(() => [
  { question: t('这是稳定正式版吗？', 'Is this a stable release?'), answer: t('当前是公开预览版，可下载试用。功能会继续迭代，真实账号与部分原生环境尚未完成验收；请先阅读 r10 更新说明中的验证范围和已知问题。', 'This is a downloadable public preview. Features continue to evolve; real-account and some native environments are not fully accepted. Read the validation scope and known issues in the r10 release notes first.') },
  { question: t('旧版本怎样更新到 r10？', 'How do I update to r10?'), answer: t('先备份原配置和数据目录，关闭旧客户端，再完整解压到新文件夹。R1 到 R10 共用同一个程序版本号，“已是最新版本”不能确认已装 r10，需手动下载。使用应用目录或自定义数据路径时保留 Data/.config_location 标记并核对原路径；本版不会自动替换或重启，也不沿用旧 Electron 更新机制。', 'Back up your configuration and data folder, close the older client, then extract the complete ZIP into a new folder. R1–R10 share one program version, so “up to date” cannot confirm r10; download it manually. Keep Data/.config_location and verify the original path when using application-folder or custom data storage. This build does not replace or restart itself, or use the former Electron updater.') },
  { question: t('需要付费才能开始吗？', 'Must I pay to start?'), answer: t('客户端预览包可免费下载。资讯、硬件信息和工具库无需配置 AI 即可查看；AI 功能使用你配置的接口或本地服务，模型、订阅和第三方软件的费用按各服务商规则处理。本地模型是否适合需看硬件。', 'The preview package is free to download. News, hardware information and the tool library need no AI setup. AI features use your configured API or local service; model, subscription and third-party software fees follow each provider’s terms. Local models require suitable hardware.') },
  { question: t('是否所有软件都能自动装好？', 'Can every tool install automatically?'), answer: t('确认方案后，受支持的软件会连续下载、安装和检测，已有软件优先复用。账号登录、购买、许可授权以及暂不支持的安装器会明确列为待办；网页打开或用户标记完成，不等于安装和连接已验证。', 'After you confirm a plan, supported software is downloaded, installed and checked in sequence, reusing existing tools first. Logins, purchases, licence authorization and unsupported installers remain explicit steps. Opening a page or marking a step complete does not verify installation or connection.') },
  { question: t('社区仍提示授权超时怎么办？', 'What if community authorization still times out?'), answer: t('先在新客户端从社区首页重新点击注册或登录，不继续旧授权页面。若出现“在浏览器重新登录”，从该入口重新开始；浏览器的登录只在该浏览器中生效。反馈时附 r10 版本、最后一步和脱敏截图，不要提交授权链接或密码。', 'Start registration or login again from the community home page in the new client instead of continuing an old authorization page. If “Restart login in browser” appears, restart there; that login applies only to that browser. Report r10, the last action and a sanitized screenshot, with no authorization links or passwords.') },
  { question: t('下载慢或打不开 GitHub 怎么办？', 'What if GitHub is slow or unreachable?'), answer: t('优先使用本页的官网下载按钮。官网镜像与 GitHub 原包具有相同的大小和 SHA-256；如果一处网络不通，可通过 GitHub 更新说明页使用备用下载。下载后按本页信息核对文件。源码 ZIP 是供开发者使用的代码，不是可运行的客户端。', 'Use the official download button on this page first. The site mirror has the same size and SHA-256 as the original GitHub package. If one route is unavailable, use the GitHub release notes page for the backup download, then check the file against the details here. The source ZIP is developer code, not the runnable client.') },
]);
const formatSize = (bytes: number | null) =>
  bytes === null ? '' : bytes >= 1024 ** 3
    ? `${(bytes / 1024 ** 3).toFixed(2)} GiB${isEn.value ? ` (${bytes.toLocaleString('en-US')} bytes)` : `（${bytes.toLocaleString('zh-CN')} 字节）`}`
    : `${(bytes / 1024 ** 2).toFixed(1)} MiB${isEn.value ? ` (${bytes.toLocaleString('en-US')} bytes)` : `（${bytes.toLocaleString('zh-CN')} 字节）`}`;
</script>
