<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">{{ t('产品与项目', 'Product and project') }}</p>
      <h1 class="zx-h1" style="font-size: clamp(30px, 3.6vw, 44px)">{{ t(`关于 ${siteConfig.brand.name}`, `About ${siteConfig.brand.name}`) }}</h1>
      <p>
        {{ t('一个', 'A ') }}<strong>{{ t('目标驱动', 'goal-driven') }}</strong>{{ t('的 Windows 桌面工具：说清你想做什么，它先澄清需求、在现成工具库里选出一条工具流，能自动的自动做完、不能自动的一步步带你做，最后把过程整理成一份可以复制给 AI Agent 的项目说明。', ' Windows desktop tool: say what you want to do and it clarifies the requirements, picks a workflow from the ready-made toolbox, automates what it can and walks you through the rest, then turns the process into a project brief you can paste into an AI agent.') }}
        {{ t(`硬件信息与内置排障工具来自上游开源项目「${siteConfig.upstream.name}」；第三方工具按需下载。`, `Hardware information and built-in troubleshooting tools come from the upstream project “${siteConfig.upstream.name}”; third-party tools download on demand.`) }}
      </p>
    </header>

    <div class="zx-page__body">
      <!-- 定位 -->
      <section class="zx-grid-2">
        <article class="zx-card">
          <div class="zx-card__icon"><ZxIcon name="sparkle" /></div>
          <h3>{{ t('它是什么', 'What it is') }}</h3>
          <p>
            {{ t('一个围绕目标搭工具流的 Windows 应用：问清关键条件、对比方案、确认后准备已支持的软件，再交接给你选定的工具或外部 Agent。客户端共用 AI 设置；密钥存于本机，联网模型请求和可关闭的工具流分享按下方数据说明处理。', 'A Windows app that prepares goal-based workflows: clarify conditions, compare plans, confirm supported software preparation, then hand off to your chosen tools or external agent. Client features share AI settings; keys are stored locally, while online model requests and disableable workflow sharing follow the data notice below.') }}
          </p>
        </article>
        <article class="zx-card">
          <div class="zx-card__icon"><ZxIcon name="info" /></div>
          <h3>{{ t('当前版本与适用范围', 'Current version and scope') }}</h3>
          <p>
            {{ t('枕星帮助你准备环境并继续使用工具，实际开发或创作由所选工具与 Agent 完成。模型账号、会员和费用按所选服务商规则处理。当前 0.1.1 为公开预览版，下载、源码和已知问题说明已在本项目 GitHub 发布。', '枕星图吧AI助手 prepares the environment and helps you continue with your tools; your chosen tools and agent perform the actual development or creative work. Model accounts, subscriptions and fees follow each provider’s rules. The current 0.1.1 public preview, source and known issues are published on this project’s GitHub.') }}
          </p>
        </article>
      </section>

      <section id="scope">
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('当前版本，怎样理解', 'Understand the current preview') }}</h2>
        <div class="zx-grid-2"><article class="zx-card"><h3>{{ t('能力与适用条件', 'Capabilities and requirements') }}</h3><p>{{ t('目标工作台、应用管理、技能、硬件和电脑工具按各自条件提供。工具准备完成后，账号、首次运行和实际效果仍需确认。', 'Goal workflows, app management, skills, hardware and utilities each have requirements. After preparation, accounts, first runs and actual results still need confirmation.') }}</p><RouterLink to="/features" class="zx-capability__link">{{ t('查看逐项功能说明', 'Explore every capability') }} →</RouterLink></article><article class="zx-card"><h3>{{ t('公开预览与发行记录', 'Public preview and release record') }}</h3><p>{{ t('0.1.1 当前提供 Windows x64 完整便携包。源码后续变化不会自动进入现有下载包；公开包的更新内容、验证范围和已知问题以对应发行记录为准。', '0.1.1 provides a complete Windows x64 portable package. Later source changes do not automatically enter that package. Its changes, validation scope and known issues are recorded in the corresponding release.') }}</p><a :href="siteConfig.links.releaseNotes ?? undefined" target="_blank" rel="noopener noreferrer" class="zx-capability__link">{{ t('查看发行记录', 'Read the release record') }} →</a></article></div>
      </section>

      <!-- 数据与统计 -->
      <section id="data">
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('数据与统计', 'Data and analytics') }}</h2>
        <div class="zx-grid-2">
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="shield" /></div>
            <h3>{{ dataNotice().siteTitle }}</h3>
            <p>{{ dataNotice().site }}</p>
          </article>
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="info" /></div>
            <h3>{{ dataNotice().clientStatus }}</h3>
            <p>{{ dataNotice().client }}</p>
          </article>
        </div>
        <div class="zx-kv" style="margin-top: 20px">
          <div class="zx-kv__row">
            <span class="zx-kv__key">{{ t('统计脚本', 'Analytics script') }}</span>
            <span class="zx-kv__val">
              <template v-if="analyticsConfigured()">
                {{ t('已配置：', 'Configured: ') }}{{ siteConfig.analytics.umami.scriptUrl }}{{ t('（开源 Umami，自托管）', ' (open-source Umami, self-hosted)') }}
              </template>
              <template v-else>
                {{ t('未配置 · 当前页面不加载任何统计脚本（脚本地址与网站 ID 都是可配置项，不内置任何管理凭据）', 'Not configured · this page loads no analytics script (the script URL and website ID are configuration options, and no admin credentials are ever embedded)') }}
              </template>
            </span>
          </div>
          <div class="zx-kv__row">
            <span class="zx-kv__key">{{ t('下载量口径', 'Download counting') }}</span>
            <span class="zx-kv__val">{{ t('页面按钮只标记一次点击事件；真实下载量以', 'The button only marks a single click event; real download counts are verified from ') }}{{ t(siteConfig.analytics.downloadCountSource, siteConfig.analytics.downloadCountSourceEn) }}{{ t('核对为准。', '.') }}</span>
          </div>
          <div class="zx-kv__row">
            <span class="zx-kv__key">{{ t('模型与密钥', 'Model and keys') }}</span>
            <span class="zx-kv__val">
              {{ t('统一 AI 设置供客户端内助手、技能制作与可选资讯整理共用。外部 Codex、Claude Code、Cursor 等仍需自己的可用账号或模型接入。模型请求发送给所选服务，兼容接口和本地模型须实际测试连接。社区另有论坛账号，不需要把模型 Key 提交给论坛。', 'Assistant chat, skill creation and optional news organisation inside this client share unified AI settings. External tools such as Codex, Claude Code and Cursor still need their own usable accounts or model connections. Requests go to the selected service; compatible APIs and local models need an actual connection test. The forum has a separate account and does not require your model key.') }}
            </span>
          </div>
        </div>
      </section>

      <!-- 与上游的关系 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('与上游的关系', 'Relationship with upstream') }}</h2>
        <div class="zx-card">
          <p>
            {{ t(`${siteConfig.brand.name}是「${siteConfig.upstream.name}」（TubaWinUi3）的衍生改造，`, `${siteConfig.brand.name} is a derivative of “${siteConfig.upstream.name}” (TubaWinUi3),`) }}
            {{ t('由本项目的维护者独立开发，', 'developed independently by this project’s maintainer; ') }}<strong>{{ t('并非上游官方发布，也不代表上游作者的任何承诺', 'it is not an upstream official release and implies no promise from the upstream author') }}</strong>{{ t('。', '.') }}
            {{ t(`上游以 ${siteConfig.upstream.license} 许可开源，本项目在保留其许可与署名要求的前提下继续演进：`, `Upstream is open-sourced under ${siteConfig.upstream.license}; this project continues from it while keeping its licence and attribution requirements:`) }}
            {{ t('上游的作者与贡献者保留各自权利，本项目的改动独立署名。', 'the upstream author and contributors keep their respective rights, and this project’s changes are attributed separately.') }}
          </p>
          <div class="zx-kv" style="margin-top: 20px">
            <div class="zx-kv__row"><span class="zx-kv__key">{{ t('上游项目', 'Upstream project') }}</span><span class="zx-kv__val">{{ siteConfig.upstream.name }}（TubaWinUi3 / 图吧工具箱CE）</span></div>
            <div class="zx-kv__row"><span class="zx-kv__key">{{ t('上游作者', 'Upstream author') }}</span><span class="zx-kv__val">{{ upstreamAuthors() }}</span></div>
            <div class="zx-kv__row">
              <span class="zx-kv__key">{{ t('上游仓库', 'Upstream repository') }}</span>
              <span class="zx-kv__val">
                <a :href="siteConfig.upstream.repo" target="_blank" rel="noopener noreferrer">github.com/luolangaga/tubatools</a>
              </span>
            </div>
            <div class="zx-kv__row">
              <span class="zx-kv__key">{{ t('上游官网', 'Upstream website') }}</span>
              <span class="zx-kv__val">
                <a :href="siteConfig.upstream.site" target="_blank" rel="noopener noreferrer">tubawinui3.cn</a>
                {{ t('（外部链接，非本站）', '(external link, not this site)') }}
              </span>
            </div>
            <div class="zx-kv__row">
              <span class="zx-kv__key">{{ t('开源许可', 'Open-source licence') }}</span>
              <span class="zx-kv__val">
                <a :href="siteConfig.upstream.licenseUrl" target="_blank" rel="noopener noreferrer">{{ siteConfig.upstream.license }}</a>
                {{ t('（沿用上游许可）', '(inherits the upstream licence)') }}
              </span>
            </div>
          </div>
        </div>
      </section>

      <!-- 本项目做了什么 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('本项目在上游基础上的改造', 'What this project changes on top of upstream') }}</h2>
        <ul class="zx-list zx-list--dense">
          <li><strong>{{ t('目标工作台', 'Goal workbench') }}</strong>{{ t('：围绕目标组织关键问题、方案比较、准备清单与工具交接，帮助用户看清当前步骤。', ': organises focused questions, plan comparison, preparation checklists and tool handoff around the goal, with a clear current step.') }}</li>
          <li><strong>{{ t('界面重塑', 'Interface rework') }}</strong>{{ t('：AI 页面与相关设置支持深色、浅色与跟随系统；动态会话、菜单和网页主题仍持续验证。', ': AI pages and settings support dark, light and system themes; dynamic conversations, menus and embedded pages remain under validation.') }}</li>
          <li><strong>{{ t('界面字体', 'Interface fonts') }}</strong>{{ t('：站点正文提供 更纱黑体 / Noto Sans SC / HarmonyOS Sans SC 三种选择（默认更纱黑体，顶栏可切换），字体文件与许可随站点附带，选择只记在访问者本机浏览器。', ': the site body offers Sarasa UI SC / Noto Sans SC / HarmonyOS Sans SC (Sarasa by default, switchable in the header); font files and licences ship with the site, and the choice is stored only in the visitor’s browser.') }}</li>
          <li><strong>{{ t('工具获取与管理', 'Tool acquisition and management') }}</strong>{{ t('：区分内置页面、按需下载与厂商获取流程；本机软件状态与云端目录分开呈现。', ': distinguishes built-in pages, on-demand downloads and vendor acquisition, with local software state separate from the cloud catalogue.') }}</li>
          <li><strong>{{ t('工程与验收', 'Engineering and acceptance') }}</strong>{{ t('：数据根隔离、启动守卫与回归测试体系，优先保证“测出来的结果可复现”。', ': isolated data roots, startup guards and a regression-test system, with priority on reproducible results.') }}</li>
        </ul>
        <p class="zx-small" style="margin-top: 14px">
          {{ t('具体能力以当前 0.1.1 客户端界面和更新说明为准；本次测试范围不等于所有功能或真实账号已完成验收。', 'Use the current 0.1.1 UI and release notes as the reference. The tests for this update do not constitute acceptance of every feature or real-account environment.') }}
        </p>
      </section>

      <!-- 本站说明 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('关于本站', 'About this site') }}</h2>
        <div class="zx-grid-2">
          <article class="zx-card">
            <h3>{{ t('站点口径', 'Editorial stance') }}</h3>
            <p>
              {{ t('全站不写无法验证的数字与评价：没有评分、没有用户量、没有“即将上线”的时间承诺；已确定的入口直接给出真实链接，未确定的入口以“未配置 / 未发布”如实标注（不生成占位链接）。', 'The site states no unverifiable numbers or reviews: no ratings, no user counts, no “coming soon” dates; settled entries get real links, unsettled ones are marked honestly as “not configured / not released” (no placeholder links are generated).') }}
            </p>
          </article>
          <article class="zx-card">
            <h3>{{ t('隐私与主题', 'Privacy and theming') }}</h3>
            <p>
              {{ t('本站使用自托管 Umami 统计页面访问与下载入口点击；主题和字体选择保存在你的浏览器本地。', 'This site uses self-hosted Umami to count page views and download-entry clicks; theme and font choices are stored locally in your browser.') }}
            </p>
          </article>
        </div>
      </section>

      <!-- 字体与许可署名 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('本站字体与许可', 'Fonts and licences on this site') }}</h2>
        <p class="zx-small" style="margin-bottom: 14px">
          {{ t('站点正文字体三选一，默认更纱黑体，可在顶栏切换；选择只记在访问者本机浏览器，不写入站点配置、不做账号绑定或跨端同步。', 'The body font is one of three, Sarasa UI SC by default, switchable in the header; the choice is stored only in the visitor’s browser — not in site configuration, not tied to an account and not synced across devices.') }}
        </p>
        <ul class="zx-list zx-list--dense">
          <li v-for="font in FONT_OPTIONS" :key="font.id">
            <strong>{{ font.order }} {{ t(font.label, font.labelEn) }}</strong><template v-if="font.id === activeFont.id">{{ t('（当前使用）', ' (in use)') }}</template>{{ t('：', ': ') }}
            {{ t(font.note, font.noteEn) }}{{ t('。许可：', '. Licence: ') }}
            <a :href="font.licenseHref" target="_blank" rel="noopener noreferrer">{{ t(font.license, font.licenseEn) }}</a>
            ·
            <a :href="font.sourceHref" target="_blank" rel="noopener noreferrer">{{ t('字体来源', 'Font source') }}</a>
          </li>
        </ul>
        <p class="zx-small" style="margin-top: 12px">
          {{ t('「HarmonyOS Sans」字体由华为提供，依 HarmonyOS Sans 字体许可协议使用：', 'The “HarmonyOS Sans” font is provided by Huawei and used under the HarmonyOS Sans font licence: ') }}<strong>{{ t('该协议不是开源许可', 'that licence is not an open-source licence') }}</strong>{{ t('，协议要求在软件中以显著方式声明使用了 HarmonyOS Sans 字体、且不得对字体做任何修改——因此本站附带的是官方原文件（未子集化、未转换格式），并随站点保留许可原文。', '; it requires prominent notice that HarmonyOS Sans is used and forbids any modification of the font — so this site ships the official files unchanged (no subsetting, no format conversion) and keeps the licence text alongside them.') }}
        </p>
      </section>

      <!-- 致谢 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('致谢', 'Credits') }}</h2>
        <ul class="zx-list zx-list--dense">
          <li>
            <strong>{{ siteConfig.upstream.name }}</strong> {{ t('与其作者、贡献者：', 'and its author and contributors: ') }}
            <a :href="siteConfig.upstream.repo" target="_blank" rel="noopener noreferrer">github.com/luolangaga/tubatools</a>
            （{{ siteConfig.upstream.license }}）。
          </li>
          <li>
            <strong>LibreHardwareMonitor</strong>{{ t('、', ', ') }}<strong>{{ t('DirectX 相关组件', 'DirectX-related components') }}</strong>{{ t('与其它第三方库：随客户端发布时一并列出许可与来源。', ' and other third-party libraries: licences and sources are listed together at client release time.') }}
          </li>
        </ul>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import ZxIcon from '../components/ZxIcon.vue';
import { siteConfig, dataNoticeCopy, upstreamAuthors } from '../site.config';
import { analyticsConfigured } from '../analytics';
import { FONT_OPTIONS, activeFont } from '../fonts';
import { t } from '../i18n';

// 数据说明与发布状态：与首页共用同一来源，且随界面语言切换
const dataNotice = () => dataNoticeCopy();
</script>
