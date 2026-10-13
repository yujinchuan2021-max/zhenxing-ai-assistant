/** User-facing capability map. Entries describe the 0.1.1 UI, not universal acceptance. */
import { computed } from 'vue';
import { t } from './i18n';

export const featureGroups = computed(() => [
  { id: 'assistant', icon: 'sparkle', title: t('AI 助手与目标工作台', 'AI assistant and goal workbench'), outcome: t('从想法走到可执行的下一步', 'Turn an idea into a practical next step'), body: t('澄清影响方案的条件，对比工具组合，确认准备清单，保留当前步骤与项目说明。', 'Clarify the conditions, compare tool combinations, confirm a preparation checklist and keep the current step and project brief.'), items: [t('目标、关键问题与可点击选项', 'Goal, focused questions and selectable answers'), t('方案对比、确认清单与继续处理', 'Plan comparison, confirmation checklist and continuation'), t('打开工具、CLI 说明与外部 Agent 交接', 'App launch actions, CLI instructions and external-agent handoff')], condition: t('需要可用的 AI 接口。登录、付费与实际创作仍在所选服务或工具中完成。', 'Requires a working AI endpoint. Sign-in, payment and actual creative work take place in the selected service or tool.'), href: '/docs#workflow' },
  { id: 'apps', icon: 'download', title: t('工具库与应用中心', 'Tool library and App Center'), outcome: t('找到工具，再看清本机状态', 'Find a tool and see its local state'), body: t('浏览云端目录，按需获取第三方工具，复用已有软件。应用中心管理本机软件与已经发起的任务。', 'Browse the cloud catalogue, acquire third-party tools on demand and reuse installed software. App Center manages local apps and requested tasks.'), items: [t('受支持工具连续下载、安装和检测', 'Sequential download, installation and detection for supported tools'), t('有效主入口、打开位置与桌面快捷方式', 'Valid launch entries, folder access and desktop shortcuts'), t('失败重试、入口缺失与实际任务状态', 'Retry, missing-entry recovery and actual task status')], condition: t('安装方式按条目区分；商店、驱动、账号与暂不支持的安装器有各自步骤。', 'Each entry has its own acquisition method. Store apps, drivers, accounts and unsupported installers need their respective steps.'), href: '/docs#apps' },
  { id: 'skills', icon: 'book', title: t('技能库与技能制作', 'Skill library and creation'), outcome: t('把常用做法整理成可复用技能', 'Make repeatable methods into reusable skills'), body: t('按分类与用途搜索完整目录，瀑布流随滚动加载；查看适用条件，修改或制作技能，先试用再投稿。', 'Search the full catalogue by category and purpose, with a masonry view that loads on scroll. Check requirements, edit or create a skill, try it locally and optionally submit it.'), items: [t('分类、全库搜索与加载状态', 'Categories, full-catalogue search and loadability'), t('导入、编辑、AI 引导制作与本机试用', 'Import, editing, AI-guided creation and local trials'), t('配套要求、来源与审核状态', 'Companion requirements, source and review status')], condition: t('可加载、GitHub 热度与审核通过不等于运行效果已验证。', 'Loadability, GitHub popularity and review approval do not establish runtime effectiveness.'), href: '/docs#skills' },
  { id: 'hardware', icon: 'chip', title: t('硬件信息与检测', 'Hardware information and checks'), outcome: t('看配置、查状态、记录问题', 'Inspect configuration, check status and record issues'), body: t('汇总型号与关键硬件信息，结合监控、验机、屏幕、键盘与磁盘工具排查。', 'Review model and hardware information, then use monitoring, device checks, screen, keyboard and disk tools to investigate.'), items: [t('硬件概览、详细信息与刷新', 'Hardware overview, details and refresh'), t('性能测试、压力测试与监控', 'Benchmarks, stress tests and monitoring'), t('磁盘健康、电池分析与验机向导', 'Disk health, battery analysis and device-check guides')], condition: t('传感器与检测项取决于硬件、驱动和权限；硬件名称显示修改不会改变物理配置。', 'Sensors and checks depend on hardware, drivers and permissions. Editing displayed hardware names does not change physical components.'), href: '/docs#utilities' },
  { id: 'system', icon: 'system', title: t('系统维护', 'System maintenance'), outcome: t('集中处理日常维护操作', 'Handle routine maintenance in one place'), body: t('清理、启动项、右键菜单、时间同步和运行环境等工具集中提供入口。', 'Access cleaning, startup items, context menus, time sync and runtime tools together.'), items: [t('垃圾清理、启动项与右键菜单', 'Junk cleaning, startup items and context menus'), t('时间同步与运行库补全', 'Time sync and runtime repair'), t('镜像下载、实验功能与系统设置', 'OS image downloads, experimental features and system settings')], condition: t('写入系统设置和驱动操作可能需要授权或重启。先确认具体改动范围。', 'System-setting and driver changes may need authorization or a restart. Review the scope of the action first.'), href: '/docs#utilities' },
  { id: 'network', icon: 'monitor', title: t('网络诊断与分享', 'Network diagnostics and sharing'), outcome: t('定位连接问题，分享局域网文件', 'Investigate connections and share LAN files'), body: t('从网速、流量、端口和网络配置入手检查问题，按需开启局域网分享。', 'Inspect speed, traffic, ports and network configuration, and enable LAN sharing when needed.'), items: [t('测速、流量监控与端口占用', 'Speed tests, traffic monitoring and port usage'), t('Hosts、Wi-Fi 与网络配置', 'Hosts, Wi-Fi and network settings'), t('局域网文件分享', 'LAN file sharing')], condition: t('网卡调度不保证带宽叠加。文件分享是无登录鉴权的未加密 HTTP 服务，仅在可信局域网开放所需目录，用完停止服务。', 'Adapter scheduling does not guarantee combined bandwidth. File sharing uses unencrypted HTTP without login authentication: expose intended folders only on a trusted LAN and stop the service after use.'), href: '/docs#utilities' },
  { id: 'files', icon: 'cube', title: t('文件与格式转换', 'Files and format conversion'), outcome: t('把文件转成需要的格式', 'Convert files to the format you need'), body: t('在一个转换界面处理文档、图片、音视频与 PDF，查看参数和每项结果。', 'Process documents, images, audio, video and PDF in one conversion interface, with settings and per-item results.'), items: [t('文档、图片与音视频转换', 'Document, image, audio and video conversion'), t('批量处理与目标格式参数', 'Batch processing and target-format settings'), t('OCR 与 PDF 相关工具', 'OCR and PDF tools')], condition: t('任务可能需要 FFmpeg、ImageMagick 或 OfficeCLI 等组件；部分旧文档格式需要本机 Office 或 WPS。', 'Tasks may need components such as FFmpeg, ImageMagick or OfficeCLI. Some legacy document formats require local Office or WPS.'), href: '/docs#utilities' },
  { id: 'games', icon: 'monitor', title: t('游戏辅助', 'Game utilities'), outcome: t('观察运行状态，处理联机与环境', 'Inspect runtime state and manage networking and dependencies'), body: t('提供游戏监控、记录、联机助手、运行库与显示辅助入口。', 'Access game monitoring, records, networking, runtime repair and display assistance.'), items: [t('监控覆盖层与记录查看', 'Monitoring overlays and record viewing'), t('游戏联机助手与运行库修复', 'Game-networking helper and runtime repair'), t('屏幕显示辅助与 GPU 压力测试', 'Display assistance and GPU stress tests')], condition: t('游戏、驱动与网络环境存在差异；联机可能需要外部组件和网络授权。', 'Compatibility varies by game, driver and network. Networking may need external components and authorization.'), href: '/docs#utilities' },
  { id: 'news', icon: 'book', title: t('枕星AI资讯', 'AI news'), outcome: t('从来源更新了解 AI 动态', 'Follow AI developments through source updates'), body: t('阅读来源、原文和更新时间；配置客户端 AI 后可做个人整理。', 'Read sources, original articles and update times; configured client AI can produce a personal view.'), items: [t('原文入口与来源时间', 'Original-source links and dates'), t('服务端定时更新与失败缓存', 'Scheduled server updates and cache retention on failure'), t('可选 AI 摘要与整理', 'Optional AI summaries and organisation')], condition: t('阅读资讯无需配置模型。AI 摘要是辅助内容，重要信息应核对原文。', 'Reading news needs no model configuration. AI summaries are supplementary; check important claims against the original.'), href: '/ai-news/', external: true },
  { id: 'community', icon: 'users', title: t('枕星AI社区', 'Community'), outcome: t('交流方案，把排障经验留下来', 'Share plans and retain troubleshooting experience'), body: t('围绕目标、技能、工具使用和问题反馈交流，也可通过 GitHub 参与项目。', 'Discuss goals, skills, tools and issues, or contribute to the project on GitHub.'), items: [t('目标方案、教程与技能分享', 'Plans, tutorials and skill sharing'), t('问题反馈与可复现记录', 'Issue reports and reproducible records'), t('开源贡献与来源说明', 'Open-source contributions and attribution')], condition: t('社区使用独立论坛账号；不要公开密码、模型 Key 或授权链接。', 'The forum has separate accounts. Keep passwords, model keys and authorization URLs out of public posts.'), href: '/community' },
]);

type ToolRow = [name: string, nameEn: string, purpose: string, purposeEn: string];
const hardware: ToolRow[] = [
  ['性能测试', 'Performance benchmark', '运行性能测试并查看结果。', 'Run benchmarks and inspect results.'],
  ['一键三烤', 'Combined stress test', '按所选项目进行压力测试。', 'Stress-test selected components.'],
  ['毒蘑菇测试', 'GPU stress test', '运行 GPU 分形图形负载，结果不构成显卡质量认证。', 'Run a GPU fractal workload; results do not certify GPU quality.'],
  ['快速验机', 'Quick device check', '按步骤检查新电脑与外设。', 'Follow checks for a new computer and peripherals.'],
  ['磁盘健康', 'Disk health', '查看受支持磁盘的健康信息。', 'View health data for supported disks.'],
  ['电池消耗分析', 'Battery analysis', '查看可用的电池使用记录。', 'Review available battery usage records.'],
  ['键盘测试', 'Keyboard test', '检查按键响应。', 'Check key responses.'],
  ['屏幕测试', 'Screen test', '使用测试图案检查显示效果。', 'Inspect the display using test patterns.'],
  ['CPU 天梯图', 'CPU rankings', '查阅处理器性能参考。', 'Browse CPU performance references.'],
  ['GPU 天梯图', 'GPU rankings', '查阅显卡性能参考。', 'Browse GPU performance references.'],
  ['核间延迟查询', 'Core-latency reference', '查阅已提供的核间延迟图。', 'Browse available core-latency maps.'],
  ['跑分排行', 'Benchmark rankings', '查看已提供的社区测试记录。', 'View available community benchmark records.'],
];
const system: ToolRow[] = [
  ['垃圾清理', 'Junk cleaner', '先分析，再确认清理范围。', 'Analyse first, then confirm what to clean.'],
  ['启动项管理', 'Startup manager', '查看和处理系统启动项。', 'Inspect and manage startup entries.'],
  ['右键菜单管理', 'Context-menu manager', '查看和调整右键菜单项。', 'Inspect and adjust context-menu entries.'],
  ['时间同步', 'Time sync', '检测时间偏差并校时。', 'Check clock offset and synchronise time.'],
  ['.NET 环境补全', '.NET environment', '检测并获取所需 .NET 组件。', 'Detect and acquire required .NET components.'],
  ['Windows 镜像', 'Windows image download', '查询并获取可用的系统镜像。', 'Find and obtain available Windows images.'],
  ['Windows 隐藏功能', 'Windows experimental features', '查看和调整实验性功能开关。', 'Inspect and adjust experimental feature flags.'],
  ['配置修改器', 'Configuration editor', '修改受支持的硬件名称显示，并保留恢复入口。', 'Edit supported displayed hardware names with recovery actions.'],
  ['后台节流省电神器', 'Background efficiency', '使用 Windows 11 EcoQoS 调整受支持进程的后台效率状态。', 'Use Windows 11 EcoQoS to adjust supported background processes.'],
  ['流氓软件的克星', 'Unwanted-software cleanup', '检查可疑残留并按项目处理。', 'Inspect suspicious remnants and handle individual items.'],
  ['恶意软件拦截器', 'Certificate blocker', '管理 Windows 不信任证书设置，属于证书管理工具。', 'Manage Windows certificate-distrust settings; this is a certificate-management tool.'],
  ['恶意软件沙盒', 'Software sandbox', '获取 Sandboxie 并查看使用指南；隔离效果取决于配置与程序。', 'Acquire Sandboxie and read its guide; isolation depends on configuration and the program.'],
  ['OptimizerDuck 优化鸭', 'OptimizerDuck', '提供系统设置调整入口。', 'Access Windows-setting adjustments.'],
];
const network: ToolRow[] = [
  ['网速测试', 'Speed test', '测量当前线路的网络速度。', 'Measure the current connection speed.'],
  ['流量监控器', 'Traffic monitor', '查看网络适配器流量。', 'Inspect network-adapter traffic.'],
  ['端口占用', 'Port viewer', '查看端口与关联进程。', 'Inspect ports and associated processes.'],
  ['Hosts 编辑', 'Hosts editor', '管理本机 Hosts 规则。', 'Manage local Hosts rules.'],
  ['Wi-Fi 密码', 'Wi-Fi information', '查看本机已保存的 Wi-Fi 信息。', 'View locally saved Wi-Fi information.'],
  ['网络优化', 'Network settings', '检测并调整可用网络参数。', 'Inspect and adjust available network parameters.'],
  ['网络调度器', 'Network scheduler', '提供多网卡配置入口，不保证带宽叠加。', 'Access multi-adapter configuration, without a guarantee of combined bandwidth.'],
  ['局域网文件分享', 'LAN file sharing', '在可信局域网通过无登录鉴权的 HTTP 分享文件，用完停止服务。', 'Share files over HTTP without login authentication on a trusted LAN; stop the service after use.'],
];
const games: ToolRow[] = [
  ['游戏监控', 'Game monitor', '配置并使用游戏监控覆盖层。', 'Configure and use a game-monitoring overlay.'],
  ['记录查看', 'Game-monitor records', '读取已保存的监控记录。', 'Read saved monitoring records.'],
  ['游戏联机助手', 'Game-networking helper', '按步骤准备受支持的游戏联机环境。', 'Prepare supported game-networking environments.'],
  ['运行库修复', 'Runtime repair', '检测并补充所需运行库。', 'Detect and obtain required runtimes.'],
  ['游戏防晕 3D', 'Game display markers', '提供可配置的视觉标记，没有医学疗效保证。', 'Provide configurable visual markers, without a medical-effect guarantee.'],
];
const practical: ToolRow[] = [
  ['AI 助手', 'AI assistant', '围绕目标比较方案并准备工具。', 'Compare plans and prepare tools around a goal.'],
  ['格式转换', 'Format conversion', '转换受支持的文档、图片、音视频与 PDF。', 'Convert supported documents, images, media and PDFs.'],
  ['电脑使用教程', 'Computer tutorials', '查阅电脑操作与使用指南。', 'Read computer-operation guides.'],
  ['常用官网', 'Official websites', '按用途打开相关网站。', 'Open relevant websites by purpose.'],
  ['服务网点查询', 'Service-centre lookup', '查看品牌服务与支持入口。', 'Find brand service and support entries.'],
  ['正版软件商店', 'Software-package store', '通过 WinGet 等受支持渠道获取软件，具体许可由厂商决定。', 'Acquire software through supported channels such as WinGet; vendors set their licences.'],
  ['UniGetUI 包管理器', 'UniGetUI', '打开或获取外部软件包管理器。', 'Open or acquire the external package manager.'],
  ['电子文盲测试', 'Computer-literacy quiz', '完成电脑基础知识测验。', 'Take a computer-basics quiz.'],
];
export const toolGroups = computed(() => [
  { id: 'hardware-tools', title: t('硬件与检测', 'Hardware and checks'), note: t('检测结果取决于本机硬件与驱动；排行属于参考资料，不能代替本机检测。', 'Results depend on local hardware and drivers. Rankings are references, not checks of this machine.'), rows: hardware },
  { id: 'system-tools', title: t('系统维护', 'System maintenance'), note: t('部分入口依赖外部组件；系统设置、证书和驱动操作应核对范围，并按提示授权。', 'Some entries depend on external components. Review the scope of setting, certificate and driver operations and follow authorization prompts.'), rows: system },
  { id: 'network-tools', title: t('网络', 'Network'), note: t('按实际网络环境使用。变更连接设置前保留原配置，分享目录由你选择。', 'Use within your network environment. Keep original settings before changing connectivity; choose the folders to share.'), rows: network },
  { id: 'game-tools', title: t('游戏辅助', 'Game utilities'), note: t('覆盖层、联机和运行库支持受游戏、系统、网络及组件条件影响。', 'Overlay, networking and runtime support depend on games, Windows, the network and components.'), rows: games },
  { id: 'practical-tools', title: t('实用工具与参考', 'Utilities and references'), note: t('网页与外部应用入口有独立使用条件；并非所有条目都随客户端内置运行。', 'Websites and external apps have their own requirements. Not every entry runs as a built-in client feature.'), rows: practical },
].map(group => ({ ...group, rows: group.rows.map(([name, nameEn, purpose, purposeEn]) => ({ name: t(name, nameEn), purpose: t(purpose, purposeEn) })) })));
