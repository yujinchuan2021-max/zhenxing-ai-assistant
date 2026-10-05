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
            {{ t('枕星帮助你准备环境并继续使用工具，实际开发或创作由所选工具与 Agent 完成。模型账号、会员和费用按所选服务商规则处理。当前 V0.1 · r10 为公开预览版，下载、源码和已知问题说明已在本项目 GitHub 发布。', '枕星图吧AI助手 prepares the environment and helps you continue with your tools; your chosen tools and agent perform the actual development or creative work. Model accounts, subscriptions and fees follow each provider’s rules. The current V0.1 · r10 public preview, source and known issues are published on this project’s GitHub.') }}
          </p>
        </article>
      </section>

      <!-- 开发进度（详细；首页只保留演示与三组价值） -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('开发进度（详细）', 'Development progress (detailed)') }}</h2>
        <p v-if="release().released" class="zx-small" style="margin-bottom: 16px">
          {{ t('Windows x64 r10 公开预览版已开放下载；下列功能以当前预览为准，需进一步验证的范围另列。', 'The Windows x64 r10 public preview is available to download. Features below describe this preview; areas needing further validation are listed separately.') }}
        </p>
        <p v-else class="zx-small" style="margin-bottom: 16px">
          {{ t('当前下载信息不完整，请到本项目 GitHub 发布页核对版本、文件与校验值。', 'Download information is incomplete. Check the version, file and checksum on this project’s GitHub release page.') }}
        </p>
        <div class="zx-grid-2">
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="check" /></div>
            <h3>{{ release().released ? t('当前公开预览版提供', 'In the current public preview') : t('开发版已接入', 'In the development build') }}</h3>
            <ul class="zx-list zx-list--dense">
              <li v-for="item in availableNow" :key="item">{{ item }}</li>
            </ul>
          </article>
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="sparkle" /></div>
            <h3>{{ t('稳定版前继续验证', 'Further validation before a stable release') }}</h3>
            <ul class="zx-list zx-list--dense">
              <li v-for="item in inProgress" :key="item">{{ item }}</li>
            </ul>
          </article>
        </div>

        <h3 class="zx-h3" style="margin: 28px 0 14px">{{ t('工具流：完整链路', 'The workflow: the full chain') }}</h3>
        <ol class="zx-steps">
          <li v-for="(step, index) in flowSteps" :key="step.title" class="zx-step">
            <span class="zx-step__index" aria-hidden="true">{{ String(index + 1).padStart(2, '0') }}</span>
            <div class="zx-step__body">
              <h4 class="zx-step__title">
                {{ step.title }}
                <span class="zx-tag" :class="`zx-tag--${step.status}`">{{ statusLabel(step.status) }}</span>
              </h4>
              <p>{{ step.text }}</p>
              <p v-if="step.examples" class="zx-step__examples">{{ step.examples }}</p>
            </div>
          </li>
        </ol>
        <p class="zx-note" style="margin-top: 20px">
          <strong>{{ t('关于「选择什么工具」', 'On “which tools to choose”') }}</strong>{{ t('：选型随目标变化，没有标准答案——助手负责把候选、理由与代价摆出来对比，', ': the right choice depends on the goal and there is no standard answer — the assistant lays out the options, the reasons and the trade-offs, and ') }}<strong>{{ t('最终由你拍板', 'you make the final call') }}</strong>{{ t('；页面里出现的任何产品名都只是示例，不构成固定推荐。', '; any product name on this site is an example, not a fixed recommendation.') }}
        </p>
      </section>

      <!-- 数据与统计 -->
      <section>
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
          <li><strong>{{ t('AI 助手内核', 'AI assistant core') }}</strong>{{ t('：接入 DeepSeek Harness（dsh）作为会话核心，保留自研引擎作为可回退方案。', ': DeepSeek Harness (dsh) is wired in as the conversation core, with the in-house engine kept as a fallback.') }}</li>
          <li><strong>{{ t('界面重塑', 'Interface rework') }}</strong>{{ t('：AI 页面与相关设置支持深色、浅色与跟随系统；动态会话、菜单和网页主题仍持续验证。', ': AI pages and settings support dark, light and system themes; dynamic conversations, menus and embedded pages remain under validation.') }}</li>
          <li><strong>{{ t('界面字体', 'Interface fonts') }}</strong>{{ t('：站点正文提供 更纱黑体 / Noto Sans SC / HarmonyOS Sans SC 三种选择（默认更纱黑体，顶栏可切换），字体文件与许可随站点附带，选择只记在访问者本机浏览器。', ': the site body offers Sarasa UI SC / Noto Sans SC / HarmonyOS Sans SC (Sarasa by default, switchable in the header); font files and licences ship with the site, and the choice is stored only in the visitor’s browser.') }}</li>
          <li><strong>{{ t('工具与合规', 'Tools and compliance') }}</strong>{{ t('：扩充内置工具（格式转换、游戏联机助手、时间同步、垃圾清理等），对高风险工具执行合规替换并在卡片上标注。', ': built-in tools were expanded (format conversion, game networking helper, time sync, junk cleaning and more); high-risk tools were replaced on compliance grounds and are marked on their cards.') }}</li>
          <li><strong>{{ t('工程与验收', 'Engineering and acceptance') }}</strong>{{ t('：数据根隔离、启动守卫与回归测试体系，优先保证“测出来的结果可复现”。', ': isolated data roots, startup guards and a regression-test system, with priority on reproducible results.') }}</li>
        </ul>
        <p class="zx-small" style="margin-top: 14px">
          {{ t('具体能力以当前 r10 客户端界面和更新说明为准；本次测试范围不等于所有功能或真实账号已完成验收。', 'Use the current r10 UI and release notes as the reference. The tests for this update do not constitute acceptance of every feature or real-account environment.') }}
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
import { siteConfig, dataNoticeCopy, releaseState, upstreamAuthors } from '../site.config';
import { analyticsConfigured } from '../analytics';
import { availableNow, flowSteps, inProgress, statusLabel } from '../progress';
import { FONT_OPTIONS, activeFont } from '../fonts';
import { t } from '../i18n';

// 数据说明与发布状态：与首页共用同一来源，且随界面语言切换
const dataNotice = () => dataNoticeCopy();
const release = () => releaseState();
</script>
