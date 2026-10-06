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
        <div><h2>{{ t('0.1.1 公开预览版已提供下载', 'The 0.1.1 public preview is available') }}</h2>
          <p>{{ t('关闭旧客户端，将完整 ZIP 解压到新文件夹，运行根目录的「枕星图吧AI助手.exe」。这是 Windows x64 便携版；请保留整个目录，不要只复制 EXE。', 'Close the older client, extract the complete ZIP into a new folder and run “枕星图吧AI助手.exe” in its root. This is a Windows x64 portable build; keep the entire folder, not just the EXE.') }}</p>
          <RouterLink to="/download">{{ t('下载与首次启动说明 →', 'Download and first-launch instructions →') }}</RouterLink></div>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('先走完这四步', 'Follow these four steps') }}</h2>
        <div class="zx-grid-2">
          <article v-for="step in steps" :key="step.title" class="zx-card"><h3>{{ step.title }}</h3><p>{{ step.body }}</p></article>
        </div>
        <p class="zx-note" style="margin-top: 18px">{{ t('枕星负责搭建整体工具流。真正写代码、生成音乐或制作素材，由你选定的工具或外部 Agent 完成。只需要 AI 生成音乐时，不默认再安排编曲软件。', '枕星图吧AI助手 prepares the overall workflow. Your chosen tools or external agent write code, generate music or make assets. A request for AI-generated music does not automatically add a music-production suite.') }}</p>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('从旧版更新到 0.1.1', 'Update an older client to 0.1.1') }}</h2>
        <p>{{ t('从旧 0.1.0 或此前的 0.1.1 更新时，先导出配置并备份实际数据目录，从官网下载并核对完整 ZIP 的大小与 SHA-256，然后关闭旧版、解压到新文件夹并运行新启动器。检测与下载不代表已完成升级，本版不会自动替换、解压或重启。', 'When updating from 0.1.0 or an earlier 0.1.1 build, export configuration and back up the actual data folder first. Download the complete ZIP from the official site and check its size and SHA-256, then close the old client, extract into a new folder and run its launcher. Detection or download does not complete an upgrade; this build does not replace, extract or restart itself.') }}</p>
        <p>{{ t('同一 Windows 用户默认继续读取原数据位置。应用目录保存用户需保留实际程序目录下的 Data 与 .config_location 标记，便携包中通常为 src\\Data；自定义位置还要核对原路径，相对路径需保留目录结构。确认设置和会话可见后再处理旧文件夹；自行添加的工具与第三方 Agent 配置也应单独备份。', 'The same Windows user continues to read the default data location. For application-folder storage keep Data and the .config_location marker in the actual program folder, usually src\\Data in the portable package. For custom storage verify the original path and preserve folder structure for relative paths. Check that settings and chats are visible before removing the old folder; separately back up added tools and third-party agent configuration.') }}</p>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('工具库与应用中心各做什么', 'Tool library and App Center') }}</h2>
        <div class="zx-grid-2">
          <article class="zx-card"><h3>{{ t('工具库：发现和获取', 'Tool library: discover and acquire') }}</h3><p>{{ t('查看自有云端目录。支持的便携包点击安装后连续下载，核对大小、SHA-256、ZIP 内容和指定主程序入口，通过后解压并登记。没有匹配包的条目提供获取说明，部分安装器仍需按厂商流程操作。失败可重试并保留原工具；已有软件优先复用。', 'Browse the owned cloud catalogue. Supported portable packages download in sequence and pass file-size, SHA-256, ZIP-content and designated launch-entry checks before extraction and registration. Entries without a matching package provide acquisition instructions; some installers still require the vendor’s process. Retry failures while preserving an existing tool, and reuse installed software first.') }}</p></article>
          <article class="zx-card"><h3>{{ t('应用中心：管理本机结果', 'App Center: manage local results') }}</h3><p>{{ t('显示已检测或已管理的本机软件，以及已发起的下载任务。未下载的云端目录不会混进来。安装状态以指定主入口和验证结果为依据，目录非空、辅助 EXE 或打开来源网站不算安装成功。失败、待处理和入口缺失的项目保留处理入口。', 'Shows software detected or managed locally and downloads you requested. Unrequested cloud entries stay out. Installation status depends on the designated launch entry and validation results; a nonempty folder, helper EXE or opened source page does not prove installation. Failed, pending and missing-entry items retain recovery actions.') }}</p></article>
        </div>
      </section>
      <section id="third-party-licences">
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('第三方工具：许可与源码', 'Third-party tools: licences and source') }}</h2>
        <p>{{ t('这些入口由官网直接提供，无需先访问 GitHub。安装器保留原厂文件，具体许可与使用条件请阅读相应说明。安装过程中需要的授权或人工步骤仍以厂商流程为准，客户端按检测和安装结果显示状态。', 'These files are served directly by the official site; visiting GitHub first is not required. Installers retain the original vendor files. Read the relevant licence and usage terms. Required authorization or personal installation steps follow the vendor’s process, and the client reports the detected installation result.') }}</p>
        <p style="margin-top: 14px"><RouterLink to="/tools/download">{{ t('从枕星下载原厂工具文件 →', 'Download original vendor files from 枕星 →') }}</RouterLink>{{ t('。选择匹配的 Windows 版本，下载后按厂商说明安装或运行。', '. Choose the matching Windows version, then follow the vendor’s installation or launch instructions.') }}</p>
        <p style="margin-top: 14px"><RouterLink to="/tools/acquire">{{ t('其他工具的获取与使用步骤 →', 'Acquisition and usage steps for other tools →') }}</RouterLink>{{ t('。驱动先匹配设备和系统，MemTest86 使用启动 U 盘，在线工具无需安装，HDR 校准通过 Microsoft Store 获取。旧版皮肤编辑器不适用于当前客户端，外观请在设置中调整。', '. Match drivers to the device and system, use a bootable USB drive for MemTest86, open online tools without installation, and acquire HDR Calibration through Microsoft Store. The legacy skin editor does not support this client; use Appearance in Settings.') }}</p>
        <ul class="zx-list zx-list--dense" style="margin-top: 14px">
          <li>Optimizer · <a href="https://zhenxingai.com/downloads/installers/optimizer-license.txt">{{ t('许可', 'Licence') }}</a> · <a href="https://zhenxingai.com/downloads/installers/optimizer-source-2.28.1.zip">{{ t('对应源码 2.28.1', 'Corresponding source 2.28.1') }}</a></li>
          <li>PawnIO · <a href="https://zhenxingai.com/downloads/installers/pawnio-license-readme.txt">{{ t('许可说明', 'Licence notice') }}</a> · <a href="https://zhenxingai.com/downloads/installers/pawnio-source-2.2.0.zip">{{ t('对应源码 2.2.0', 'Corresponding source 2.2.0') }}</a></li>
          <li>Sandboxie · <a href="https://zhenxingai.com/downloads/installers/sandboxie-license-plus.txt">{{ t('Plus 许可', 'Plus licence') }}</a> · <a href="https://zhenxingai.com/downloads/installers/sandboxie-license-classic.txt">{{ t('Classic 许可', 'Classic licence') }}</a> · <a href="https://zhenxingai.com/downloads/installers/sandboxie-source-1.18.5.zip">{{ t('对应源码 1.18.5', 'Corresponding source 1.18.5') }}</a></li>
          <li>UniGetUI · <a href="https://zhenxingai.com/downloads/installers/unigetui-license.txt">{{ t('许可', 'Licence') }}</a></li>
        </ul>
        <p class="zx-note" style="margin-top: 14px">{{ t('LHM、DDU 与 y-cruncher 的许可文件及适用的对应源码随工具包提供。各工具保留自身许可，不以客户端开源许可替代厂商许可。', 'Licence files and applicable corresponding source for LHM, DDU and y-cruncher accompany their tool packages. Each tool retains its own terms; the client’s open-source licence does not replace the vendor’s licence.') }}</p>
        <p class="zx-note" style="margin-top: 14px">{{ t('Victoria 由 HDD.by 发布并保留原作者归属；产品介绍与支持信息请查看', 'Victoria is published by HDD.by and retains the original author attribution. For product information and support, visit ') }} <a href="https://hdd.by" target="_blank" rel="noopener noreferrer">{{ t('Victoria 原作者官网', 'Victoria author’s official site') }}</a>{{ t('。', '.') }}</p>
      </section>
      <section id="skills">
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('技能：先看适用条件，再试用', 'Skills: check requirements before trying') }}</h2>
        <p>{{ t('从左侧“官方技能库”或会话技能菜单进入。卡片采用瀑布流，先预加载内容，向下滚动继续加载；搜索与分类作用于完整目录。具体数量以客户端同步结果为准，当前屏幕显示数量不是全库总数。', 'Open Official skill library in the sidebar or the conversation’s skill menu. A masonry view preloads content and loads more as you scroll; search and category filters cover the full catalogue. The synced catalogue is the reference for its size; the visible card count is not the full library count.') }}</p>
        <ul class="zx-list zx-list--dense">
          <li>{{ t('可加载只表示当前文本结构能加载；需要配套文件或指定引擎的技能应先看详情。没有运行评测时显示待评测，GitHub 星数只代表热度。', 'Loadable means the text structure can be loaded. Check details for companion files or a required engine. Without runtime evaluation the skill remains unevaluated; GitHub stars indicate popularity only.') }}</li>
          <li>{{ t('修改技能或制作技能时，可用已配置的 AI 逐步生成草稿；先在本机保存和试用，再由你决定是否提交后台审核。提交审核不等于已公开，也不代表实测通过。', 'Edit a skill or create a draft with your configured AI. Save and try it locally, then decide whether to submit for review. Submission does not mean publication or a verified runtime result.') }}</li>
        </ul>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('资讯与交流', 'News and community') }}</h2>
        <p>{{ t('枕星AI资讯由服务器每天北京时间 09:00 自动更新，无需保持客户端打开；失败时保留有效缓存。没配置 AI 也能阅读来源更新与原文，客户端配置 AI 后可做个人整理。摘要是辅助阅读，关键信息请核对原文。', 'The server updates 枕星AI资讯 daily at 09:00 Beijing time; the client does not need to remain open. Valid cached content remains on failure. Read sources and originals without AI setup, or use configured client AI for a personal view. Verify important summary claims against the source.') }}</p>
        <p><a href="/ai-news/">{{ t('枕星AI资讯 →', '枕星AI资讯 →') }}</a> · <a :href="siteConfig.links.community ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('枕星AI社区 →', '枕星AI社区 →') }}</a></p>
      </section>
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('遇到失败时怎么继续', 'When a step fails') }}</h2>
        <p>{{ t('先看失败的是下载、安装、入口还是账号连接。只重试对应步骤；正在进行的任务不要重复发起。软件装好但打不开时查看会话入口或应用中心，不需要重新描述整个目标。反馈时提供预览包名称、系统、最后一步和截图，隐藏密码与 Key。', 'Identify whether the failure is in downloading, installation, an app entry or an account connection. Retry the relevant step and avoid duplicating an active task. If an installed tool will not open, use the conversation actions or App Center rather than restating the whole goal. For feedback, include the preview name, system, last action and a screenshot, with passwords and keys hidden.') }}</p>
        <p>{{ t('工具流分享当前默认开启，可在设置中关闭。开启时会发送该会话的完整可见对话（不含隐藏思考过程）和准备结果到官方服务；模型请求另发往你选择的 AI 服务。', 'Workflow sharing is currently on by default and can be disabled in settings. When enabled it sends the full visible conversation in that session (excluding hidden reasoning) and preparation results to the official service; model requests separately go to your chosen AI provider.') }} <RouterLink to="/about">{{ t('查看数据说明 →', 'Read the data notice →') }}</RouterLink></p>
        <p style="margin-top: 14px"><a :href="siteConfig.links.issues ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('反馈问题 →', 'Report an issue →') }}</a> · <a :href="siteConfig.links.releaseNotes ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('查看 0.1.1 已知问题与验证范围 →', 'Read 0.1.1 known issues and validation scope →') }}</a></p>
      </section>
      <section><h2 class="zx-h3" style="margin-bottom: 14px">{{ t('硬件工具参考', 'Hardware-tool reference') }}</h2><p>{{ t('上游图吧工具箱CE文档可帮助了解部分硬件工具；它的下载、商店和社区入口属于上游，客户端 AI 与工具流操作请以本指南和当前预览界面为准。', 'Upstream documentation helps explain some hardware tools. Its downloads, store and community belong to upstream; use this guide and the current preview UI for 枕星图吧AI助手 AI and workflow actions.') }}</p><a :href="siteConfig.upstream.docs" target="_blank" rel="noopener noreferrer">{{ t('打开上游参考（外部链接）↗', 'Open upstream reference (external link) ↗') }}</a></section>
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
  { title: t('3 · 选方案，确认准备', '3 · Choose a plan and confirm preparation'), body: t('按目标、预算、硬件与服务条件比较方案，适合的桌面 Agent 优先。核对目标与清单后确认：已有软件复用，已支持的软件按依赖顺序连续安装；登录、会员、授权及暂不支持的安装器明确列为待办。', 'Compare plans by goal, budget, hardware and service access, preferring a suitable desktop agent. Confirm the goal and checklist: reuse installed tools, prepare supported software in dependency order and keep login, subscription, licence and unsupported-installer steps explicit.') },
  { title: t('4 · 打开工具开始做', '4 · Open the tools and start'), body: t('桌面软件在受支持且入口验证成功时尝试创建桌面图标，也可在会话或应用中心打开。CLI 显示打开方法；支持的编程 Agent 由你选择项目文件夹后启动。把项目说明交给外部 Agent，再验证首次运行与实际效果。', 'For supported desktop apps with a verified launch entry, the client attempts to create a shortcut; you can also open them from the conversation or App Center. CLI entries show opening instructions; supported coding agents start after you select a project folder. Hand off the project brief, then verify the first run and actual result.') },
]);
</script>
