<template>
  <div class="zx-home">
    <!-- ============================ 首屏 ============================ -->
    <section class="zx-hero zx-hero--tight">
      <div class="zx-container zx-hero__inner zx-home-hero">
        <div class="zx-home-hero__copy">
        <p class="zx-eyebrow zx-rise zx-rise--1">{{ t('Windows · AI 目标工作台与电脑工具', 'Windows · AI workflows and computer tools') }}</p>
        <h1 class="zx-h1 zx-hero__title zx-rise zx-rise--1">{{ t('把想法变成', 'Turn your idea into') }}<br /><span class="zx-home-hero__accent">{{ t('下一步行动。', 'a practical next step.') }}</span></h1>

        <p class="zx-lead zx-hero__lead zx-rise zx-rise--2">
          {{ t('枕星帮你问清需求、比较方案、准备支持的工具。开始创作，也能随时检查硬件、管理应用和处理电脑问题。', 'Clarify your goal, compare plans and prepare supported tools with 枕星图吧AI助手. Start creating, inspect hardware, manage apps and handle computer issues in one place.') }}
        </p>

        <div class="zx-btn-row zx-rise zx-rise--3">
          <RouterLink class="zx-btn zx-btn--primary" to="/download">
            <ZxIcon name="download" />
            <span>{{ t('下载 0.1.1 预览版', 'Download 0.1.1 preview') }}</span>
          </RouterLink>
          <RouterLink class="zx-btn" to="/features">
            <span>{{ t('查看全部功能', 'Explore features') }}</span>
            <ZxIcon name="arrow" style="width: 16px; height: 16px" />
          </RouterLink>
        </div>

        <p class="zx-hero__state zx-rise zx-rise--3">
          <span class="zx-dot" aria-hidden="true"></span>
          <RouterLink v-if="release().released" to="/download">{{ t('0.1.1 公开预览版 · Windows x64', '0.1.1 public preview · Windows x64') }} · {{ siteConfig.releaseArtifact.publishedAt }}</RouterLink>
          <template v-else>{{ t('开发中 · 暂未开放下载', 'In development · download not open yet') }}</template>
        </p>

        <div class="zx-home-hero__links zx-rise zx-rise--3"><RouterLink to="/docs">{{ t('第一次使用', 'First use') }} →</RouterLink><a :href="siteConfig.links.repo ?? undefined" target="_blank" rel="noopener noreferrer">GitHub ↗</a></div>
        </div>
        <div class="zx-workflow-preview zx-rise zx-rise--4" :aria-label="t('使用流程示意', 'Illustrative workflow')">
          <div class="zx-workflow-preview__top"><ZxIcon name="sparkle" /><span>{{ t('从目标开始', 'Start with a goal') }}</span><span class="zx-workflow-preview__flag">{{ t('使用流程示意', 'Illustrative workflow') }}</span></div>
          <p class="zx-workflow-preview__goal">{{ t('我想做一个 Windows 2D 游戏原型', 'I want to make a Windows 2D game prototype') }}</p>
          <ol class="zx-workflow-preview__steps"><li v-for="(step, index) in heroSteps" :key="step.title"><span class="zx-workflow-preview__number">{{ String(index + 1).padStart(2, '0') }}</span><div><h2>{{ step.title }}</h2><p>{{ step.text }}</p></div><ZxIcon name="arrow" /></li></ol>
          <p class="zx-workflow-preview__note">{{ t('确认后准备支持的软件。账号、授权与实际作品由你在所选工具中完成。', 'Prepare supported software after confirmation. Complete accounts, authorization and actual work in your selected tools.') }}</p>
          <a href="#demo" class="zx-workflow-preview__more">{{ t('逐步了解怎么用', 'Explore the steps') }} ↓</a>
        </div>
      </div>
    </section>

    <!-- ==================== 四状态可点演示 ==================== -->
    <section id="demo" class="zx-section">
      <div class="zx-container">
        <div class="zx-section-head">
          <p class="zx-eyebrow">{{ t('工作原理', 'How it works') }}</p>
          <h2 class="zx-h2">{{ t('明确目标，再准备下一步', 'Clarify the goal, then prepare the next step') }}</h2>
          <p class="zx-lead">{{ t('从说出目标到打开工具，查看每一步的用途和适用条件。以下为使用流程示意，实际操作以客户端为准。', 'See the purpose and requirements of each step, from describing a goal to opening tools. This illustrates the workflow; the client is the reference for actual interaction.') }}</p>
        </div>

        <div class="zx-demo">
          <div class="zx-demo__tabs" role="tablist" :aria-label="t('工具流四步演示', 'Four-step workflow demo')" @keydown="onTabKeydown">
            <button
              v-for="(step, index) in demoSteps"
              :key="step.id"
              :ref="(el) => setTabRef(el as HTMLButtonElement | null, index)"
              type="button"
              role="tab"
              class="zx-demo__tab"
              :class="{ 'is-active': active === index }"
              :id="`zx-tab-${step.id}`"
              :aria-selected="active === index"
              :aria-controls="`zx-panel-${step.id}`"
              :tabindex="active === index ? 0 : -1"
              @click="selectTab(index)">
              <span class="zx-demo__tab-index" aria-hidden="true">{{ String(index + 1).padStart(2, '0') }}</span>
              <span class="zx-demo__tab-label">{{ step.tab }}</span>
            </button>
          </div>

          <div class="zx-demo__stage">
            <Transition name="zx-swap" mode="out-in">
              <article
                :key="current.id"
                class="zx-demo__panel"
                role="tabpanel"
                :id="`zx-panel-${current.id}`"
                :aria-labelledby="`zx-tab-${current.id}`"
                tabindex="0">
                <div class="zx-demo__meta">
                  <span class="zx-tag" :class="`zx-tag--${current.status}`">{{ statusLabel(current.status) }}</span>
                  <span class="zx-demo__flag">{{ t('示意 · 非应用截图', 'Illustrative · not an app screenshot') }}</span>
                </div>

                <h3 class="zx-demo__title">{{ current.title }}</h3>
                <p class="zx-demo__desc">{{ current.desc }}</p>

                <!-- ① 描述目标 -->
                <div v-if="current.id === 'goal'" class="zx-mock">
                  <p class="zx-mock__bubble zx-mock__bubble--me">{{ t('我想做一款 Windows 2D 小游戏。', 'I want to make a small 2D game for Windows.') }}</p>
                  <p class="zx-mock__bubble zx-mock__bubble--ai">
                    {{ t('先选预算：顶级付费、低成本模型、本地模型，或让我推荐。一次只确认一个影响方案的问题。', 'First choose a budget: premium models, low-cost models, local models, or help me choose. One question that affects the plan at a time.') }}
                  </p>
                  <p class="zx-mock__hint">{{ t('预览版已有可点击的澄清选项；本地模型还要核对硬件，海外服务还要核对可用地区、账号和网络。', 'The preview has clickable question options. Local models also need suitable hardware; overseas services need an available region, account and network.') }}</p>
                </div>

                <!-- ② 比较 / 选择工具流 -->
                <div v-else-if="current.id === 'pick'" class="zx-mock">
                  <ul class="zx-mock__list">
                    <li class="zx-mock__item">
                      <span class="zx-mock__name">{{ t('轻量 · Agent + 低成本 API + 游戏引擎', 'Light · agent + low-cost API + game engine') }}</span>
                      <span class="zx-mock__why">{{ t('先跑通小原型，辅助素材按需要加', 'Start with a small prototype; add assets only as needed') }}</span>
                    </li>
                    <li class="zx-mock__item">
                      <span class="zx-mock__name">{{ t('中量 · 编程 Agent + 订阅或 API + 引擎', 'Medium · coding agent + subscription or API + engine') }}</span>
                      <span class="zx-mock__why">{{ t('兼顾 AI 能力、成本与现有账号', 'Balance AI capability, cost and existing accounts') }}</span>
                    </li>
                    <li class="zx-mock__item">
                      <span class="zx-mock__name">{{ t('重量 · 高能力 Agent + 商业引擎 + 必要辅助工具', 'Full · capable agent + commercial engine + needed tools') }}</span>
                      <span class="zx-mock__why">{{ t('适合更复杂目标，先核对硬件与许可成本', 'For complex goals; check hardware and licence cost first') }}</span>
                    </li>
                  </ul>
                  <p class="zx-mock__hint">{{ t('示例只演示「候选 + 理由 + 代价」的对比结构；实际选型随目标变化，最后由你拍板。', 'The example only shows the “options + reasons + trade-offs” structure; the actual choice depends on your goal and you make the final call.') }}</p>
                </div>

                <!-- ③ 配置与教程 -->
                <div v-else-if="current.id === 'setup'" class="zx-mock">
                  <ul class="zx-mock__list">
                    <li class="zx-mock__item">
                      <span class="zx-mock__chip zx-mock__chip--auto">{{ t('自动', 'Auto') }}</span>
                      <span class="zx-mock__name">{{ t('检测已有软件并复用', 'Detect and reuse installed software') }}</span>
                    </li>
                    <li class="zx-mock__item">
                      <span class="zx-mock__chip zx-mock__chip--auto">{{ t('自动', 'Auto') }}</span>
                      <span class="zx-mock__name">{{ t('支持软件：下载 → 安装 → 检测', 'Supported tools: download → install → check') }}</span>
                    </li>
                    <li class="zx-mock__item">
                      <span class="zx-mock__chip zx-mock__chip--manual">{{ t('需你操作', 'Your step') }}</span>
                      <span class="zx-mock__name">{{ t('账号登录、API Key 或会员购买', 'Login, API key or subscription purchase') }}</span>
                    </li>
                    <li class="zx-mock__item">
                      <span class="zx-mock__chip zx-mock__chip--doc">{{ t('教程', 'Guide') }}</span>
                      <span class="zx-mock__name">{{ t('桌面入口与命令行打开方法', 'Desktop entries and CLI opening instructions') }}</span>
                    </li>
                  </ul>
                  <p class="zx-mock__hint">{{ t('确认后连续准备整份已支持清单；未知安装器与账号付费等事项仍明确列出。桌面软件须有有效入口才能创建图标。', 'Confirmation prepares the entire supported checklist. Unknown installers and personal account or payment steps remain explicit. Desktop shortcuts require a valid app entry.') }}</p>
                </div>

                <!-- ④ 交给 AI Agent -->
                <div v-else class="zx-mock">
                  <pre class="zx-mock__code">{{ t(`# 项目说明（可复制）
目标    Windows 2D 小游戏原型
工具流  编程 Agent + API + 游戏引擎
已准备  软件入口与项目说明
待办    模型登录、真实连接与首次运行
环境    Win10 22H2 / x64 / 32GB`, `# Project brief (copy-pasteable)
Goal     A Windows 2D game prototype
Workflow Coding agent + API + game engine
Prepared Tool entries and project brief
To do    Model login, connection and first run
Env      Win10 22H2 / x64 / 32GB`) }}</pre>
                  <p class="zx-mock__hint">{{ t('枕星帮你搭好工具流；实际创作交给所选工具或外部 Agent。可复制项目说明，支持的编程 Agent 可在你选择的项目文件夹中启动。', '枕星图吧AI助手 prepares the workflow; the chosen tools or external agent perform the creative work. Copy the brief, or start a supported coding agent in your chosen project folder.') }}</p>
                </div>

                <p class="zx-demo__note zx-small">{{ current.note }}</p>
              </article>
            </Transition>
          </div>
        </div>
      </div>
    </section>

    <section class="zx-section zx-section--tight">
      <div class="zx-container">
        <div class="zx-section-head"><p class="zx-eyebrow">{{ t('不止一段对话', 'More than a conversation') }}</p><h2 class="zx-h2">{{ t('日常工具与 AI 工作流，在一个工作台', 'Everyday tools and AI workflows, in one workbench') }}</h2><p class="zx-lead">{{ t('用目标找到合适方案，也能直接查硬件、找工具和管理本机软件。按用途查看每项能力。', 'Find a plan for your goal, or directly inspect hardware, discover tools and manage local software. Explore each capability by purpose.') }}</p></div>
        <div class="zx-grid-3"><article v-for="feature in homeFeatures" :key="feature.id" class="zx-card zx-card--lift zx-home-feature"><div class="zx-card__icon"><ZxIcon :name="feature.icon" /></div><h3>{{ feature.title }}</h3><p>{{ feature.body }}</p><RouterLink :to="`/features#${feature.id}`">{{ t('查看用途与条件', 'See purpose and requirements') }} →</RouterLink></article></div>
        <div class="zx-start-panel"><div><h2 class="zx-h3">{{ t('先打开工具，再开始做', 'Open your tools and start') }}</h2><p>{{ t('下载完整便携包。硬件、工具库与资讯可以先看，AI 功能按需配置。', 'Download the complete portable package. Explore hardware, the tool library and news first, then configure AI when needed.') }}</p></div><RouterLink class="zx-btn zx-btn--primary" to="/download"><ZxIcon name="download" />{{ t('下载 0.1.1 预览版', 'Download 0.1.1 preview') }}</RouterLink></div>
        <p class="zx-note" style="margin-top: 22px">{{ t('工具流分享当前默认开启，可在设置中关闭。开启时会向官方服务发送该会话的可见对话与准备结果；模型请求另发往你选择的服务。', 'Workflow sharing is currently on by default and can be disabled in settings. When enabled, it sends this session’s visible conversation and preparation results to the official service; model requests separately go to your selected provider.') }} <RouterLink to="/about#data">{{ t('了解数据处理', 'Read the data notice') }} →</RouterLink></p>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import ZxIcon from '../components/ZxIcon.vue';
import { releaseState, siteConfig } from '../site.config';
import { featureGroups } from '../features';
import { statusLabel } from '../progress';
import { t } from '../i18n';

const release = () => releaseState();
const homeFeatures = computed(() => featureGroups.value.filter(group => ['assistant', 'apps', 'skills', 'hardware', 'news', 'community'].includes(group.id)));
const heroSteps = computed(() => [
  { title: t('说清目标', 'Describe the goal'), text: t('确认平台、预算与可用服务', 'Clarify platform, budget and service access') },
  { title: t('选择方案', 'Choose a plan'), text: t('对比工具、成本与适用条件', 'Compare tools, cost and requirements') },
  { title: t('确认准备', 'Confirm preparation'), text: t('复用已有软件，获取支持的工具', 'Reuse installed software and acquire supported tools') },
  { title: t('打开工具', 'Open the tools'), text: t('带着项目说明，开始实际使用', 'Take the project brief and begin actual work') },
]);

type DemoStatus = 'done' | 'partial' | 'planned';

const demoSteps = computed<{
  id: string;
  tab: string;
  title: string;
  desc: string;
  note: string;
  status: DemoStatus;
}[]>(() => [
  {
    id: 'goal',
    tab: t('描述目标', 'Describe the goal'),
    title: t('用一句话说清想做什么', 'Say what you want in one sentence'),
    desc: t('不用先懂工具、也不用先列软件：「我想做一款 Windows 2D 小游戏」就够开始了。', 'You do not need to know or list the tools first: “I want to make a small Windows 2D game” is enough to start.'),
    note: t('现状：预览版已有目标条与可点击的澄清选项。网站可达并不等于 API、账号或会员可用。', 'Status: the preview has a goal summary and clickable choices. A reachable website does not prove API, account or subscription availability.'),
    status: 'done',
  },
  {
    id: 'pick',
    tab: t('比较并选择工具流', 'Compare and pick a workflow'),
    title: t('候选、理由、代价，摆在一起挑', 'Options, reasons and trade-offs, side by side'),
    desc: t('按目标、预算、硬件和服务可用条件选组合：哪些必装、哪些可选。适合的桌面 AI Agent 优先，只有确实需要时才安排命令行工具。', 'Choose a combination for the goal, budget, hardware and service access: what is required and what is optional. Prefer a suitable desktop AI agent; add command-line tools only when needed.'),
    note: t('现状：预览版已有方案卡片。按目标选型，辅助工具只在需要时加入。', 'Status: plan cards are in the preview. Choose for the goal and add auxiliary tools only when needed.'),
    status: 'done',
  },
  {
    id: 'setup',
    tab: t('配置与教程', 'Setup and guides'),
    title: t('能自动的自动做，不能自动的带你做', 'Automate what can be automated, guide what cannot'),
    desc: t('安装、配置与检查尽量替你做完；注册、登录、授权这类必须你来的步骤，明确告诉你为什么、在哪做、怎么验证。', 'Installation, configuration and checks are done for you as much as possible; for steps that must be yours — sign-ups, logins, authorisations — it tells you why, where and how to verify.'),
    note: t('现状：整份自动安装限已支持的软件与安装方式；失败保留结果，不把用户确认当作连接验证。', 'Status: checklist automation covers supported tools and installation methods. Failures retain results; user confirmation is not connection verification.'),
    status: 'partial',
  },
  {
    id: 'handoff',
    tab: t('生成 Agent 项目说明', 'Generate the agent brief'),
    title: t('把过程交给 AI Agent', 'Hand the process to an AI agent'),
    desc: t('收尾时生成一份可复制的说明：目标、已选工具流、已完成与待办、关键环境信息。', 'At the end it produces a copy-pasteable brief: the goal, the chosen workflow, what is done and what is left, and key environment details.'),
    note: t('现状：预览版有项目说明与工具入口。页面内容仍是示意，实际安装、登录和效果需本机验证。', 'Status: project briefs and tool entries are in the preview. This page is illustrative; actual installation, login and results require local verification.'),
    status: 'partial',
  },
]);

const active = ref(0);
const current = computed(() => demoSteps.value[active.value]);

const tabEls = ref<HTMLButtonElement[]>([]);
function setTabRef(el: HTMLButtonElement | null, index: number) {
  if (el) tabEls.value[index] = el;
}

function selectTab(index: number) {
  active.value = index;
}

/** 键盘操作：左右方向键切换，Home/End 跳到首尾（标准 tablist 行为） */
function onTabKeydown(event: KeyboardEvent) {
  const last = demoSteps.value.length - 1;
  let next: number | null = null;
  if (event.key === 'ArrowRight') next = active.value === last ? 0 : active.value + 1;
  else if (event.key === 'ArrowLeft') next = active.value === 0 ? last : active.value - 1;
  else if (event.key === 'Home') next = 0;
  else if (event.key === 'End') next = last;
  if (next === null) return;
  event.preventDefault();
  selectTab(next);
  tabEls.value[next]?.focus();
}
</script>
