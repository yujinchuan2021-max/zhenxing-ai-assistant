/** Capability copy describes the current public preview, not a successful user run. */
import { computed } from 'vue';
import { isEn } from './i18n';
export type StepStatus = 'done' | 'partial' | 'planned';
export const statusLabel = (status: StepStatus) => isEn.value
  ? status === 'done' ? 'In the public preview' : status === 'partial' ? 'Supported with limits' : 'Needs further validation'
  : status === 'done' ? '预览版已接入' : status === 'partial' ? '支持范围内可用' : '继续验证中';
interface FlowStep { title: string; text: string; status: StepStatus; examples?: string }
const flowStepsZh: FlowStep[] = [
  { title: '说清目标', status: 'done', text: '说出最终想完成什么。助手只追问影响方案的关键条件，能选择的用选项回答，不必先列软件清单。' },
  { title: '对比方案', status: 'done', text: '按目标比较轻量、中量和重量方案，说明成本、AI 能力、硬件与服务可用条件；已有工具优先复用，适合的桌面 Agent 优先，命令行按需要安排。最终由你选择。' },
  { title: '确认后准备整份清单', status: 'partial', text: '确认后，已支持的软件按依赖顺序连续下载、安装和检测。未知软件、没有受支持安装方式的条目会明确列出，不猜测静默安装参数。' },
  { title: '完成必要的人工步骤', status: 'partial', text: '账号注册、登录、会员购买、API Key、许可授权等仍需你完成；“我已完成”表示用户确认，不表示客户端已验证服务可用。' },
  { title: '打开工具并交接目标', status: 'partial', text: '桌面软件在受支持且入口验证成功时尝试创建桌面图标，并在会话中提供打开入口。命令行工具说明如何打开；受支持的编程 Agent 可选择项目文件夹后启动。项目说明可复制给外部 Agent。' },
  { title: '管理与继续', status: 'done', text: '应用中心查看本机已检测或已管理的软件，以及你已发起的下载任务；全部云端工具仍在工具库。失败和待处理项保留，便于继续。' },
];
const flowStepsEn: FlowStep[] = [
  { title: 'Describe the goal', status: 'done', text: 'Say what you want to finish. The assistant asks only the questions that affect the plan and offers choices where possible; you do not need to list the software first.' },
  { title: 'Compare plans', status: 'done', text: 'Compare lightweight, medium and full plans by cost, AI capability, hardware and service access. Reuse existing tools and prefer a suitable desktop agent; use CLI tools when needed. You choose the plan.' },
  { title: 'Prepare the confirmed checklist', status: 'partial', text: 'After confirmation, supported software is downloaded, installed and checked in dependency order. Unknown software and unsupported installers remain clearly identified; silent-install flags are never guessed.' },
  { title: 'Finish necessary personal steps', status: 'partial', text: 'Registration, login, subscriptions, API keys and licences still require you. “I have completed this” is your confirmation, not a verified service connection.' },
  { title: 'Open tools and hand off the goal', status: 'partial', text: 'For supported desktop apps with a verified launch entry, the client attempts to create a desktop shortcut and offers conversation actions. CLI tools explain how to open them; supported coding agents can start in a selected project folder. A project brief can be copied to your external agent.' },
  { title: 'Manage and continue', status: 'done', text: 'App Center shows software detected or managed on this machine and downloads you requested. The complete cloud catalogue stays in the tool library. Failed and pending steps remain available to continue.' },
];
const availableNowZh = [
  '目标工具流：关键问题选项、方案卡片、确认清单与当前步骤；网站可达性是方案条件，网页能打开并不证明登录或 API 一定可用。',
  '准备环境：已装复用；支持的软件确认后整份连续安装；桌面入口与命令行打开方式；失败保留结果，按具体问题继续。',
  '工具库与应用中心：第三方工具按需下载，校验后安装受管理的便携包；没有可用安装包的条目只提供来源页面与获取方式。应用中心只展示本机软件与已发起任务。',
  '客户端 AI 全局配置：接口、模型与 Agent 在一个设置页配置，供客户端内助手、技能制作与可选资讯整理共用。外部编程 Agent 仍需自己的账号或模型接入。',
  '技能库：分类、全库搜索与瀑布流滚动加载；区分可加载、需要配套和参考内容。可修改或由已配置的 AI 引导制作，本机试用与提交审核由用户操作。热度、加载和审核状态与运行评测分别标明。',
  '枕星AI资讯与社区：资讯无需配置 AI 也可阅读来源更新与原文；服务器每天北京时间 09:00 自动更新，失败保留缓存。客户端配置 AI 后可做个人整理，论坛用于问答与经验分享。',
  '硬件与系统工具：硬件信息、实时监控、格式转换、时间同步等；第三方工具按需要获取，具体硬件支持以本机检测为准。',
];
const availableNowEn = [
  'Goal workflows: focused question choices, plan cards, a confirmation checklist and the current step. Site reachability is a planning condition, not proof that login or an API will work.',
  'Environment preparation: reuse installed tools; install the confirmed supported checklist in sequence; provide desktop entries and CLI opening instructions; keep results when a step fails.',
  'Tool library and App Center: download third-party tools on demand and verify managed portable packages. Entries without a supported package offer a source or acquisition page. App Center lists local software and requested tasks.',
  'Global client AI setup: configure the endpoint, model and agent together for chat, skill creation and optional news organisation inside this client. External coding agents still need their own accounts or model connections.',
  'Skill library: categories, full-catalogue search and a masonry view that loads more on scroll. Loadable, companion-required and reference entries are distinguished. Edit or create skills with configured AI, try them locally and submit for review. Popularity, loading, review and runtime evaluation are distinct states.',
  'News and community: read source updates and originals without AI configuration. The server updates daily at 09:00 Beijing time and keeps cached content on failure. Configured client AI can organise a personal view; the forum supports questions and shared experience.',
  'Hardware and system tools: hardware information, monitoring, format conversion and time sync; third-party tools are acquired as needed, and hardware support depends on this machine.',
];
const inProgressZh = [
  'r10 为公开预览：真实用户主流程、干净机器和旧版迁移仍继续验证；本次 GitHub 包需手动下载后完整解压到新文件夹。',
  '社区真实账号注册与登录尚未验证；原生验证宿主退出时的微信输入法模块异常已记录，完整退出验收未通过。',
  '更多软件的可靠安装、入口检测和兼容验证；未知安装器不承诺自动完成。',
  '账号、订阅、API 连接与生成结果的实际验证；不把用户自报完成当作实测。',
  '技能运行效果评测、失效维护与投稿反馈；资讯摘要持续抽查，原文与来源时间保留。',
];
const inProgressEn = [
  'r10 is a public preview. Real user journeys, clean-machine checks and migration continue to be validated. Download this GitHub package manually and extract it completely into a new folder.',
  'Real-account community registration and login remain unverified. A WeType input-method fault on native validation-host exit is recorded; full native exit acceptance did not pass.',
  'Reliable installation, entry detection and compatibility for more tools; unknown installers are not promised as automatic.',
  'Actual account, subscription, API and output checks; user-reported completion is not a verified result.',
  'Skill effectiveness evaluation, maintenance and submission feedback; continued news-summary sampling with source links and dates retained.',
];
export const flowSteps = computed(() => isEn.value ? flowStepsEn : flowStepsZh);
export const availableNow = computed(() => isEn.value ? availableNowEn : availableNowZh);
export const inProgress = computed(() => isEn.value ? inProgressEn : inProgressZh);
