<template>
  <div class="zx-home">
    <!-- ============================ 首屏 ============================ -->
    <section class="zx-hero zx-hero--tight">
      <div class="zx-container zx-hero__inner">
        <h1 class="zx-h1 zx-hero__title zx-rise zx-rise--1">{{ t('说出你的想法，枕星带你把 AI 用起来。', 'Say what you want to do — 枕星图吧AI助手 helps you get started.') }}</h1>

        <p class="zx-lead zx-hero__lead zx-rise zx-rise--2">
          {{ t('说目标、选方案、确认准备，再打开你的工具开始做。', 'Describe the goal, choose a plan, confirm preparation, then open your tools and get started.') }}
        </p>

        <div class="zx-btn-row zx-rise zx-rise--3">
          <RouterLink class="zx-btn zx-btn--primary" to="/download">
            <ZxIcon name="download" />
            <span>{{ t('下载 0.1.1 预览版', 'Download 0.1.1 preview') }}</span>
          </RouterLink>
          <a class="zx-btn" href="#demo">
            <span>{{ t('看看怎么用', 'See how it works') }}</span>
            <ZxIcon name="arrow" style="width: 16px; height: 16px" />
          </a>
        </div>

        <p class="zx-hero__state zx-rise zx-rise--3">
          <span class="zx-dot" aria-hidden="true"></span>
          <RouterLink v-if="release().released" to="/download">{{ t('0.1.1 公开预览版 · Windows x64', '0.1.1 public preview · Windows x64') }} · {{ siteConfig.releaseArtifact.publishedAt }}</RouterLink>
          <template v-else>{{ t('开发中 · 暂未开放下载', 'In development · download not open yet') }}</template>
        </p>

        <div class="zx-hero__shot zx-rise zx-rise--4">
          <ZxShot
            :dark="shotAiDark"
            :light="shotAiLight"
            :alt="t('枕星图吧AI助手 · AI 助手页面实拍', '枕星图吧AI助手 · screenshot of the AI assistant page')"
            :caption="t('应用实拍：AI 助手新对话页（界面示例，以当前 0.1.1 客户端为准）', 'App screenshot: the AI assistant’s new-chat page (an interface example; refer to the current 0.1.1 client)')" />
        </div>
      </div>
    </section>

    <!-- ==================== 四状态可点演示 ==================== -->
    <section id="demo" class="zx-section">
      <div class="zx-container">
        <div class="zx-section-head">
          <p class="zx-eyebrow">{{ t('工作原理', 'How it works') }}</p>
          <h2 class="zx-h2">{{ t('一条工具流，四步走完', 'One workflow, four steps') }}</h2>
          <p class="zx-lead">{{ t('点开每一步，看它做什么、现在能不能用。带「示意」标记的是交互示意，不是应用截图。', 'Open each step to see what it does and whether it works today. Anything marked “illustrative” is an interaction mock-up, not an app screenshot.') }}</p>
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

    <!-- ======================== 三组价值 ======================== -->
    <section class="zx-section zx-section--tight">
      <div class="zx-container">
        <div class="zx-grid-3">
          <article class="zx-card zx-card--lift">
            <div class="zx-card__icon"><ZxIcon name="sparkle" /></div>
            <h3>{{ t('AI 助手：围绕目标搭工具流', 'AI assistant: build a workflow for your goal') }}</h3>
            <p>
              {{ t('客户端内助手、技能制作和可选资讯整理共用 AI 设置。先问关键条件，再比较方案和准备环境；外部 Agent 的账号、登录和费用仍由你处理。', 'Chat, skill creation and optional news organisation inside this client share AI settings. Clarify the conditions, compare plans and prepare the environment; external agent accounts, logins and fees remain yours to handle.') }}
            </p>
            <p class="zx-card__foot zx-small">{{ t('目标工作台已提供 · 安装完成后可继续开始创作', 'Goal workbench available · continue creating after setup') }}</p>
          </article>
          <article class="zx-card zx-card--lift">
            <div class="zx-card__icon"><ZxIcon name="chip" /></div>
            <h3>{{ t('工具箱：随手的排障底座', 'Toolbox: a troubleshooting base within reach') }}</h3>
            <p>
              {{ t(`${facts().builtinTools}内置工具（垃圾清理、时间同步、格式转换、游戏联机、镜像下载…）`, `${facts().builtinTools} built-in tools (junk cleaning, time sync, format conversion, game networking, image download …)`) }}
              {{ t(`加${facts().externalTools}外部工具，来自上游「${siteConfig.upstream.name}」——`, `plus ${facts().externalTools} external tools from the upstream “${siteConfig.upstream.name}” —`) }}
              {{ t('云端工具目录按需下载，受支持的便携包通过内容和主入口校验后安装；应用中心显示本机软件与真实任务状态。', 'Download supported portable tools on demand from the cloud catalogue, with content and launch-entry validation before installation. App Center shows local software and actual task states.') }}
            </p>
            <p class="zx-card__foot zx-small">{{ t('预览版已提供 · 上游 GPL-3.0，保留署名', 'Available in the preview · upstream GPL-3.0, attribution kept') }}</p>
          </article>
          <article class="zx-card zx-card--lift">
            <div class="zx-card__icon"><ZxIcon name="users" /></div>
            <h3>{{ t('社区：经验留在能用的人手里', 'Community: experience stays with the people who use it') }}</h3>
            <p v-if="communityLive">
              {{ t('自托管开源论坛 Discourse（GPL-2.0），围绕工具流、技能分享与排障经验展开。', 'A self-hosted open-source Discourse forum (GPL-2.0) around workflows, skill sharing and troubleshooting.') }}
              <strong>{{ t('论坛已上线', 'The forum is live') }}</strong>{{ t('：可以进站注册、提问与分享经验。', ': register, ask questions and share what you know.') }}
            </p>
            <p v-else>
              {{ t('自托管开源论坛 Discourse（GPL-2.0），围绕工具流、技能分享与排障经验展开。', 'A self-hosted open-source Discourse forum (GPL-2.0) around workflows, skill sharing and troubleshooting.') }}
              {{ t('论坛尚未开通，地址已准备——确认上线后再挂入口，不提前放空链接。', 'The forum is not open yet; an address is prepared. The entry appears once it is confirmed — no empty links in advance.') }}
            </p>
            <p class="zx-card__foot zx-small">
              <template v-if="communityLive">
                {{ t('已上线 ·', 'Live ·') }}
                <a
                  :href="communityHref"
                  target="_blank"
                  rel="noopener noreferrer"
                  >{{ t('进入社区论坛', 'Open the community forum') }}<span class="zx-small">{{ t('（外部链接）', ' (external link)') }}</span></a
                >
              </template>
              <template v-else>{{ t('未开通 · 计划中', 'Not open yet · planned') }}</template>
            </p>
          </article>
        </div>

        <div class="zx-grid-2" style="margin-top: 18px">
          <article class="zx-card">
            <h3>{{ t('技能：按用途找，按状态试', 'Skills: find by purpose, try by readiness') }}</h3>
            <p>{{ t('官方技能库按分类和用途搜索，瀑布流随下拉继续加载。每项注明可加载、需要配套或仅供参考；你也可以修改或由 AI 引导制作技能，先本机试用，再提交审核。', 'Search the official skill library by category and purpose, with a masonry view that loads more as you scroll. Entries show whether they are loadable, need companion files or are reference material. Edit a skill or create one with AI guidance, try it locally, then submit it for review.') }}</p>
            <RouterLink to="/docs#skills">{{ t('看技能使用说明 →', 'Read the skill guide →') }}</RouterLink>
          </article>
          <article class="zx-card">
            <h3>{{ t('枕星AI资讯：无需配置也能读', '枕星AI资讯: read without setup') }}</h3>
            <p>{{ t('服务器每天北京时间 09:00 自动更新，失败保留缓存。没有 AI 配置也能读来源与原文；客户端配置 AI 后可做个人摘要与整理，AI 内容会标明。', 'The server updates daily at 09:00 Beijing time and keeps cached content on failure. Read sources and originals without AI setup; configured client AI can provide personal summaries, labelled as AI content.') }}</p>
            <a href="/ai-news/">{{ t('打开枕星AI资讯 →', 'Open 枕星AI资讯 →') }}</a>
          </article>
        </div>

        <p class="zx-close">
          <RouterLink to="/download">{{ t('下载、启动与本次更新', 'Download, launch and what changed') }}</RouterLink>
          <span aria-hidden="true">·</span>
          <RouterLink to="/about">{{ t('数据说明、开源署名与许可', 'Data notice, attribution and licenses') }}</RouterLink>
        </p>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import ZxIcon from '../components/ZxIcon.vue';
import ZxShot from '../components/ZxShot.vue';
import { facts, isConfigured, releaseState, siteConfig } from '../site.config';
import { statusLabel } from '../progress';
import { t } from '../i18n';
import shotAiDark from '../../assets/zxai/shots/ai-home-dark.webp';
import shotAiLight from '../../assets/zxai/shots/ai-home-light.webp';

/** 社区是否已上线：文案与入口链接随之切换（真值来源仍是 site.config.ts 的 links.community） */
const communityLive = isConfigured(siteConfig.links.community);
const communityHref = siteConfig.links.community ?? undefined;
const release = () => releaseState();

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
