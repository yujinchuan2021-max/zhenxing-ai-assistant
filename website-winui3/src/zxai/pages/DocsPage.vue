<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">{{ t('从这里开始', 'Start here') }}</p>
      <h1 class="zx-h1" style="font-size: clamp(30px, 3.6vw, 44px)">{{ t('新手指南', 'Getting started') }}</h1>
      <p>{{ docsCopy().intro }}</p>
    </header>
    <div class="zx-page__body">
      <section class="zx-status">
        <span class="zx-status__icon"><ZxIcon name="info" /></span>
        <div><h2>{{ t('客户端仍为私有预览，尚未公开发布', 'The client remains a private preview, not a public release') }}</h2>
          <p>{{ t('已有受邀预览包的用户可以按以下流程试用。官网不提供尚未发布的安装包；正式版本会在下载页列出版本、文件大小与校验值。', 'Invited preview users can try the steps below. This site does not offer an unreleased installer; a public release will list its version, size and checksum on the download page.') }}</p>
          <RouterLink to="/download">{{ t('查看下载状态 →', 'Check release status →') }}</RouterLink></div>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('先走完这四步', 'Follow these four steps') }}</h2>
        <div class="zx-grid-2">
          <article v-for="step in steps" :key="step.title" class="zx-card"><h3>{{ step.title }}</h3><p>{{ step.body }}</p></article>
        </div>
        <p class="zx-note" style="margin-top: 18px">{{ t('枕星负责搭建整体工具流。真正写代码、生成音乐或制作素材，由你选定的工具或外部 Agent 完成。只需要 AI 生成音乐时，不默认再安排编曲软件。', 'Zhenxing prepares the overall workflow. Your chosen tools or external agent write code, generate music or make assets. A request for AI-generated music does not automatically add a music-production suite.') }}</p>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('工具库与应用中心各做什么', 'Tool library and App Center') }}</h2>
        <div class="zx-grid-2">
          <article class="zx-card"><h3>{{ t('工具库：发现和获取', 'Tool library: discover and acquire') }}</h3><p>{{ t('查看完整目录。支持的便携包点击安装后自动下载、校验、解压并登记；没有匹配安装包的条目只提供来源页面与获取方式，打开网站不算安装完成。下载失败可重试，已有软件优先复用。', 'Browse the complete catalogue. Supported portable packages download, verify, extract and register automatically after installation is requested. Entries without a matching package offer a source or acquisition page; opening a website is not a completed installation. Retry failed downloads and reuse existing software first.') }}</p></article>
          <article class="zx-card"><h3>{{ t('应用中心：管理本机结果', 'App Center: manage local results') }}</h3><p>{{ t('只显示已检测或已管理的本机软件，以及你已发起的下载任务。未下载的云端目录不会混进来。可查看入口、打开位置；失败、待处理和受管理但入口缺失的项目会保留处理入口。', 'Shows software detected or managed locally and downloads you requested. Unrequested cloud entries stay out. View opening actions or locations; failed, pending and managed entries with a missing executable keep recovery actions.') }}</p></article>
        </div>
      </section>
      <section id="skills">
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('技能：先看适用条件，再试用', 'Skills: check requirements before trying') }}</h2>
        <p>{{ t('从会话里的技能菜单进入官方技能库，按分类、搜索和查看条件找技能。目录保留全部内容，具体数量以客户端当前同步结果为准。', 'Open the official skill library from the conversation’s skill menu. Browse by category, search and readiness. The full catalogue is retained; the current synced client view is the reference for its size.') }}</p>
        <ul class="zx-list zx-list--dense">
          <li>{{ t('可加载只表示当前文本结构能加载；需要配套文件或指定引擎的技能应先看详情。没有运行评测时显示待评测，GitHub 星数只代表热度。', 'Loadable means the text structure can be loaded. Check details for companion files or a required engine. Without runtime evaluation the skill remains unevaluated; GitHub stars indicate popularity only.') }}</li>
          <li>{{ t('修改技能或制作技能时，可用已配置的 AI 逐步生成草稿；先在本机保存和试用，再由你决定是否提交后台审核。提交审核不等于已公开，也不代表实测通过。', 'Edit a skill or create a draft with your configured AI. Save and try it locally, then decide whether to submit for review. Submission does not mean publication or a verified runtime result.') }}</li>
        </ul>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('资讯与交流', 'News and community') }}</h2>
        <p>{{ t('枕星AI资讯由服务器每天北京时间 09:00 自动更新，无需保持客户端打开；失败时保留有效缓存。没配置 AI 也能阅读来源更新与原文，客户端配置 AI 后可做个人整理。摘要是辅助阅读，关键信息请核对原文。', 'The server updates Zhenxing AI News daily at 09:00 Beijing time; the client does not need to remain open. Valid cached content remains on failure. Read sources and originals without AI setup, or use configured client AI for a personal view. Verify important summary claims against the source.') }}</p>
        <p><a href="/ai-news/">{{ t('枕星AI资讯 →', 'Zhenxing AI News →') }}</a> · <a :href="siteConfig.links.community ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('枕星AI社区 →', 'Zhenxing AI Community →') }}</a></p>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('遇到失败时怎么继续', 'When a step fails') }}</h2>
        <p>{{ t('先看失败的是下载、安装、入口还是账号连接。只重试对应步骤；正在进行的任务不要重复发起。软件装好但打不开时查看会话入口或应用中心，不需要重新描述整个目标。反馈时提供预览包名称、系统、最后一步和截图，隐藏密码与 Key。', 'Identify whether the failure is in downloading, installation, an app entry or an account connection. Retry the relevant step and avoid duplicating an active task. If an installed tool will not open, use the conversation actions or App Center rather than restating the whole goal. For feedback, include the preview name, system, last action and a screenshot, with passwords and keys hidden.') }}</p>
        <p>{{ t('工具流分享当前默认开启，可在设置中关闭。开启时会发送相关可见对话和准备结果到官方服务；模型请求另发往你选择的 AI 服务。', 'Workflow sharing is currently on by default and can be disabled in settings. When enabled it sends related visible conversation and preparation results to the official service; model requests separately go to your chosen AI provider.') }} <RouterLink to="/about">{{ t('查看数据说明 →', 'Read the data notice →') }}</RouterLink></p>
      </section>
      <section><h2 class="zx-h3" style="margin-bottom: 14px">{{ t('硬件工具参考', 'Hardware-tool reference') }}</h2><p>{{ t('上游图吧工具箱CE文档可帮助了解部分硬件工具；它的下载、商店和社区入口属于上游，客户端 AI 与工具流操作请以本指南和当前预览界面为准。', 'Upstream documentation helps explain some hardware tools. Its downloads, store and community belong to upstream; use this guide and the current preview UI for Zhenxing AI and workflow actions.') }}</p><a :href="siteConfig.upstream.docs" target="_blank" rel="noopener noreferrer">{{ t('打开上游参考（外部链接）↗', 'Open upstream reference (external link) ↗') }}</a></section>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import ZxIcon from '../components/ZxIcon.vue';
import { docsCopy, siteConfig } from '../site.config';
import { t } from '../i18n';
const steps = computed(() => [
  { title: t('1 · 一次配置客户端 AI', '1 · Configure client AI once'), body: t('在 AI 设置统一选择接口、模型与 Agent，保存并测试连接。客户端内助手、技能制作和可选资讯整理共用这份配置。外部 Codex、Claude Code、Cursor 等仍需自己的可用账号或模型接入；本地模型还需合适硬件和已运行的服务。', 'Select the endpoint, model and agent in AI settings, save and test the connection. Assistant chat, skill creation and optional news organisation inside this client reuse it. External tools such as Codex, Claude Code and Cursor still need their own usable accounts or model connections; local models also need suitable hardware and a running service.') },
  { title: t('2 · 说目标，点击选项', '2 · State the goal and choose options'), body: t('例如“做一个 Windows 2D 游戏原型”或“用 AI 生成一首歌”。回答影响方案的预算、平台或服务条件即可，能确定的不用反复输入；网页能打开不等于账号与 API 已验证。', 'For example, “make a Windows 2D game prototype” or “generate a song with AI”. Answer only the budget, platform or service questions that affect the plan. A reachable site is not proof of account or API access.') },
  { title: t('3 · 选方案，确认准备', '3 · Choose a plan and confirm preparation'), body: t('比较轻量、中量、重量方案，核对目标与清单后确认。已装复用，已支持的软件整份按依赖顺序连续安装；未知软件、登录、会员和授权会明确列出。不要把“我已完成”当作客户端验证成功。', 'Compare light, medium and full plans and confirm the goal and checklist. Reuse installed tools and prepare supported software in dependency order. Unknown software, logins, subscriptions and licences stay explicit. “I have completed this” is not a verified client result.') },
  { title: t('4 · 打开工具开始做', '4 · Open the tools and start'), body: t('有有效入口的桌面软件会创建桌面图标，也可在会话或应用中心打开。CLI 显示打开方法；支持的编程 Agent 由你选择项目文件夹后启动。把项目说明交给外部 Agent，再验证首次运行与实际效果。', 'Desktop apps with valid entries receive shortcuts and can also open from the conversation or App Center. CLI entries show opening instructions; supported coding agents start after you select a project folder. Hand off the project brief, then verify the first run and actual result.') },
]);
</script>
