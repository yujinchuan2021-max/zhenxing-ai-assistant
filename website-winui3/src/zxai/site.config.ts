/**
 * 枕星图吧AI助手 官网 · 站点配置
 *
 * 这里集中管理「我们自己的」对外入口。尚未确定的地址一律留 null：
 * 页面会显示诚实的「未配置 / 未发布」状态，而不是指向别人的下载页。
 * 上线前把对应字段填上即可，无需改页面代码。
 *
 * 注意：`upstream` 一节是上游开源项目（图吧工具箱CE）自己的入口，
 * 只用于来源署名，绝不当作本项目的正式下载/仓库入口使用。
 *
 * 双语：本文件里的中文串是文案来源；`brandCopy()` / `facts()` /
 * `releaseState()` / `communityCopy()` / `docsCopy()` / `dataNoticeCopy()` 等
 * 按 `<html data-lang>`（见 i18n.ts）返回对应语言的文案，页面与 SEO 描述共用同一来源。
 */

import { lang } from './i18n';

export interface SiteLinks {
  /**
   * 本项目正式域名（用于 canonical / og:url / og:image 绝对化），未确定则留 null。
   * 注意：填了之后 canonical 与 og:url 才会输出（未配置时宁缺毋滥）。
   */
  siteUrl: string | null;
  /**
   * 本项目自己的代码仓库（开源后填写）。
   * 页面用 `isConfigured()` 判断：有值就渲染真链接，没值就渲染诚实空态。
   */
  repo: string | null;
  /** 本项目正式下载入口（发布后填写） */
  download: string | null;
  /** 与发布包对应的本项目源码 ZIP；实际归档并验收后填写，不影响便携版发布状态。 */
  sourceArchive: string | null;
  /** 枕星版文档站（整理完成后填写） */
  docs: string | null;
  /** 社区入口：平台已定为自托管 Discourse，2026-09-24 已上线 → 见下方 communityUrl */
  community: string | null;
  /** 反馈渠道：先用邮箱（2026-09-24 用户确定）。页面文案只说「邮件反馈」，不提 GitHub Issues */
  feedback: string | null;
}

/** 本项目首个公开包：单一 Windows x64 便携 ZIP。数值在包验收后填写。 */
export interface ReleaseArtifact {
  version: string | null;
  sizeBytes: number | null;
  sha256: string | null;
}

export interface UpstreamInfo {
  name: string;
  repo: string;
  site: string;
  docs: string;
  license: string;
  licenseUrl: string;
  authors: string;
}

/**
 * 反馈邮箱（2026-09-24 用户确定：反馈与建议先用这个邮箱）。
 * 站点只把它当「邮件反馈」用（mailto 链接），不提 GitHub Issues；要换邮箱只改这一处。
 */
export const feedbackEmail = 'yujinchuan2021@gmail.com';

/**
 * 社区地址（2026-09-24 用户确认已上线：论坛首页在公网正常显示、管理员账号已激活）。
 * `links.community` 直接指向它；页面文案、页脚入口列表与 SEO 描述都会自动切到「已开通」分支。
 * 若社区下线，把 `links.community` 改回 null 即回到如实空态（文案与断言无需改动）。
 */
export const communityUrl = 'https://community.zhenxingai.com';

export const siteConfig = {
  brand: {
    name: '枕星图吧AI助手',
    shortName: '枕星图吧',
    /**
     * 官网版本标识（2026-09-25 用户指定）：顶栏与页脚显示 `V0.1`。
     * 与 `releaseArtifact.version`（发布包版本，仍为 null）不是一件事：**不代表已发布**。
     */
    version: 'V0.1',
    /** 首页 <title> 的主口径 */
    title: '枕星图吧AI助手 —— 带你把 AI 用起来',
    tagline: '说出你的想法，带你把 AI 用起来',
    description:
      '围绕目标比较方案，确认后准备已支持的软件，再交接给所选工具或 AI Agent。客户端共用 AI 设置，第三方工具按需下载；外部 Agent 仍需自己的账号或模型接入。客户端仍为私有预览，尚未公开发布。',
  },

  links: {
    // 正式域名已定（2026-09-23）：canonical / og:url / og:image 自动绝对化。
    // 反馈渠道已定（2026-09-24）：先用邮箱，页面按「邮件反馈」呈现（真链接，不是空态）。
    // 社区已上线（2026-09-24，用户确认论坛首页在公网正常显示、管理员已激活）：见 communityUrl。
    // 客户端只有私有预览，未公开发布；下载/源码仍留空，本站 /docs 提供新手指南。
    siteUrl: 'https://zhenxingai.com',
    repo: null,
    download: null,
    sourceArchive: null,
    docs: 'https://zhenxingai.com/docs',
    community: communityUrl,
    feedback: `mailto:${feedbackEmail}`,
  } as SiteLinks,

  releaseArtifact: {
    version: null,
    sizeBytes: null,
    sha256: null,
  } as ReleaseArtifact,

  /**
   * 站点统计（可配置，默认关闭）：自托管开源 Umami（MIT 许可）。
   *
   * - `scriptUrl` / `websiteId` 任一为空 → **不加载任何统计脚本**，页面与按钮照常工作；
   * - 这里只放公开的脚本地址与网站 ID，**不存放、不需要任何管理凭据**；
   * - 下载按钮只上报一次「点击」事件：真实下载量以服务端日志核对为准，不靠前端计数。
   */
  analytics: {
    umami: {
      /**
       * 自托管 Umami 的脚本地址（2026-09-24 已定）：公开统计入口 `https://stats.zhenxingai.com/script.js`。
       * DNS/TLS 与线上可用性由 Codex 负责；站点侧只负责按配置注入，不探测、不写任何接口。
       */
      scriptUrl: 'https://stats.zhenxingai.com/script.js' as string | null,
      /**
       * Umami 网站 ID（**公开追踪标识，不是管理凭据**；官方就是把它写在页面里的）。
       * 管理口令、API 令牌、数据库地址一律不写进站点源码。
       */
      websiteId: 'c17a4f8e-d8dd-4f61-8838-9e8c7dc8ff0b' as string | null,
      /**
       * 只统计正式域名（交给 tracker 的 `data-domains`，逗号分隔、不带协议、不带端口）：
       * 本地预览（127.0.0.1 / localhost）与任何测试域名都不会进入统计。
       */
      domains: 'zhenxingai.com,www.zhenxingai.com',
      /** 丢弃 URL 查询串（`?…`），避免带参数的测试链接被当成不同页面 */
      excludeSearch: true,
    },
    /** 下载量口径（写死在文案里，避免页面各说各话）；英文侧同源 */
    downloadCountSource: '服务端日志',
    downloadCountSourceEn: 'server logs',
    /** 点击事件名（下载按钮统一用它） */
    downloadEventName: 'download-click',
  },

  upstream: {
    name: '图吧工具箱CE',
    // 上游 GitHub 仓库的规范地址是 tubatools（tubatool 会被 301 重定向）
    repo: 'https://github.com/luolangaga/tubatools',
    site: 'https://tubawinui3.cn/',
    docs: 'https://tubawinui3.cn/guide/getting-started',
    license: 'GPL-3.0',
    licenseUrl: 'https://www.gnu.org/licenses/gpl-3.0.html',
    authors: '罗澜嘎嘎（luolangaga）与贡献者',
  } as UpstreamInfo,

  /** 可在页面文案中引用的、来自代码仓库的既有事实（勿写无法验证的统计） */
  facts: {
    /** Catalogue size changes; this is a conservative description, not a release counter. */
    externalTools: '百余款',
    /** Built-in tools remain separate from on-demand third-party packages. */
    builtinTools: '40 余款',
    minOs: 'Windows 10 2004（build 19041）或更高',
    archs: '当前预览：x64',
    runtime: '自包含运行，无需额外安装 .NET',
  },

  status: {
    /** 包、版本、大小和哈希确认后，才置 true 并填 links.download。 */
    released: false,
    label: '开发中 · 尚未公开发布',
    note: 'Windows x64 私有预览正在验证中；尚无公开稳定包。正式发布后才提供下载地址、版本与校验信息。社区入口已开放。',
  },
} as const;

/**
 * 品牌文案随界面语言切换，中英版本同时维护能力和发布边界。
 */
export const brandCopy = () =>
  lang.value === 'en'
    ? {
        title: 'Zhenxing Tuba AI Assistant — get AI working for you',
        tagline: 'Say what you want to do — we get AI working for you',
        description:
          'Compare goal-based plans, confirm preparation of supported software, then hand off to your chosen tools or AI agent. Client features share AI settings; external agents need their own accounts or model connections. Tools download on demand. The client remains a private preview, not a public release.',
      }
    : {
        title: siteConfig.brand.title,
        tagline: siteConfig.brand.tagline,
        description: siteConfig.brand.description,
      };

/** 可在页面文案中引用的事实（英文侧同样只写能核实的量级，不编造统计） */
export const facts = () =>
  lang.value === 'en'
    ? {
        externalTools: '100+',
        builtinTools: '40+',
        minOs: 'Windows 10 2004 (build 19041) or later',
        archs: 'current preview: x64',
        runtime: 'self-contained, no separate .NET install required',
      }
    : { ...siteConfig.facts };

/** 上游作者署名（中文原文见 siteConfig.upstream.authors） */
export const upstreamAuthors = () =>
  lang.value === 'en' ? 'luolangaga and contributors' : siteConfig.upstream.authors;

/** 已配置的「我们自己的」链接（无则返回 null） */
export const configuredLinks = (): SiteLinks => ({ ...siteConfig.links });

export const isConfigured = (value: string | null | undefined): value is string =>
  typeof value === 'string' && value.trim().length > 0;

/**
 * 只有发布开关、下载地址和包的实际信息都齐全时才显示已发布。
 */
export const releaseState = (): { released: boolean; label: string; note: string } =>
  siteConfig.status.released &&
  isConfigured(siteConfig.links.download) &&
  isConfigured(siteConfig.releaseArtifact.version) &&
  Number.isSafeInteger(siteConfig.releaseArtifact.sizeBytes) &&
  (siteConfig.releaseArtifact.sizeBytes ?? 0) > 0 &&
  /^[a-f\d]{64}$/i.test(siteConfig.releaseArtifact.sha256 ?? '')
    ? lang.value === 'en'
      ? {
          released: true,
          label: 'Released',
          note: 'The Windows x64 portable ZIP is out — version, file size and SHA-256 are listed below.',
        }
      : {
          released: true,
          label: '已公开发布',
          note: 'Windows x64 便携版 ZIP 已发布，版本、文件大小和 SHA-256 校验值见下方。',
        }
    : lang.value === 'en'
      ? {
          released: false,
          label: 'In development · not yet released',
          note: 'The private Windows x64 preview is being validated. No public stable build is available. Versioned downloads and checksums will appear at public release; the community is already open.',
        }
      : {
          released: false,
          label: siteConfig.status.label,
          note: siteConfig.status.note,
        };

/**
 * 随配置自动切换的文案（**单一来源**，页面与 SEO 描述共用，避免出现「页面说未开通、描述说已开通」这类不一致）：
 * - 地址为空 → 保留如实空态（不生成占位链接、不编造计划）；
 * - 地址已配置 → 只陈述已确认的事实（社区/文档站已有地址），
 *   **不编造帖子数、活跃度、版本号或正式域名**。
 */
export const communityCopy = () =>
  isConfigured(siteConfig.links.community)
    ? lang.value === 'en'
      ? {
          configured: true as const,
          statusTitle: 'Status: the community is live (Discourse)',
          statusBody:
            'The community runs on the self-hosted open-source forum Discourse (GPL-2.0, official Docker deployment): tool tips, AI workflows, skill sharing and troubleshooting notes all live there. The entry link is under “Community platform” below. You can register a forum account yourself — accounts and data stay on our self-hosted instance.',
          sectionTitle: 'What is in the community',
          seo: 'The Zhenxing Tuba AI Assistant community runs on the self-hosted open-source forum Discourse (GPL-2.0): tool usage, AI workflows, skill sharing and troubleshooting.',
        }
      : {
          configured: true as const,
          statusTitle: '当前状态：社区已开通（Discourse）',
          statusBody:
            '社区运行在自托管的开源论坛 Discourse（GPL-2.0，官方 Docker 部署）上：工具经验、AI 工作流、技能分享与排障经验都在里面；入口见下方「社区平台」，进站后可自行注册论坛账号（账号与数据都在我们自托管的实例里）。',
          sectionTitle: '社区里有什么',
          seo: '枕星图吧AI助手的社区采用自托管开源论坛 Discourse（GPL-2.0）：围绕工具使用、AI 工作流、技能分享与排障经验交流。',
        }
    : lang.value === 'en'
      ? {
          configured: false as const,
          statusTitle: 'Status: platform chosen (Discourse), community not yet open',
          statusBody:
            'The community will run on the self-hosted open-source forum Discourse (GPL-2.0, official Docker deployment). An address is prepared, but the forum is not online yet and no entry is enabled on this site, so there is no clickable link here for now. We will not register a third-party community or use a placeholder link as an official entry until it is confirmed.',
          sectionTitle: 'Planned community content',
          seo: 'The Zhenxing Tuba AI Assistant community will run on the self-hosted open-source forum Discourse (GPL-2.0): tool usage, AI workflows, skill sharing and troubleshooting. Not yet open; the address is prepared but not enabled.',
        }
      : {
          configured: false as const,
          statusTitle: '当前状态：平台已定（Discourse），社区尚未开通',
          statusBody:
            '社区平台采用自托管的开源论坛 Discourse（GPL-2.0，官方 Docker 部署）；地址已经准备好，但论坛尚未上线、站点里也尚未启用入口，所以这里暂时没有可点的地址。确认上线之前，本站不会注册或链接任何第三方社区，也不会用占位链接冒充官方入口。',
          sectionTitle: '计划中的社区内容',
          seo: '枕星图吧AI助手的社区采用自托管开源论坛 Discourse（GPL-2.0）：围绕工具使用、AI 工作流、技能分享与排障经验交流。社区尚未开通、地址已准备但尚未启用。',
        };

/** 新手指南与 SEO 使用同一口径；上游参考始终另行标注。 */
export const docsCopy = () =>
  isConfigured(siteConfig.links.docs)
    ? lang.value === 'en'
      ? {
          configured: true as const,
          intro:
            'Start with the four-step guide below: configure AI, describe the goal, confirm a plan, then open the prepared tools. It documents the private preview and its limits.',
          statusTitle: 'Getting started with the private preview',
          seo: 'Zhenxing Tuba AI Assistant getting started: global AI setup, goal workflows, supported installation, local App Center, skills and news. The client is not publicly released yet.',
        }
      : {
          configured: true as const,
          intro: '先看下面四步：配置 AI、说目标、确认方案、打开工具。这里说明私有预览的实际流程与限制。',
          statusTitle: '私有预览新手指南',
          seo: '枕星图吧AI助手新手指南：AI 全局配置、目标工具流、支持软件安装、本机应用中心、技能与资讯。客户端尚未公开发布。',
        }
    : lang.value === 'en'
      ? {
          configured: false as const,
          intro:
            'The Zhenxing docs site is not up yet: documentation will be organised and released together with the code repository. Until then this page lists trustworthy alternatives and makes clear what does not belong to this project.',
          statusTitle: 'Status: in preparation, docs site not established',
          seo: 'Zhenxing Tuba AI Assistant documentation is in preparation; for now you can refer to the upstream “图吧工具箱CE” documentation (external link, not this project’s docs site).',
        }
      : {
          configured: false as const,
          intro:
            '枕星版文档站还没有建立：文档会与代码仓库一起整理发布。在此之前，这里给出可以放心的替代路径，并说明哪些内容不属于本项目。',
          statusTitle: '当前状态：整理中，文档站未建立',
          seo: '枕星图吧AI助手文档整理中；现阶段可参考上游「图吧工具箱CE」的官方文档（外部链接，非本项目文档站）。',
        };

/** 下载页 SEO 描述：与页面共用 releaseState()，杜绝「页面说未发布、描述说已发布」 */
export const downloadSeoDescription = () =>
  releaseState().released
    ? lang.value === 'en'
      ? 'Download the Zhenxing Tuba AI Assistant Windows x64 portable ZIP; this page lists the version, file size and SHA-256.'
      : '下载枕星图吧AI助手 Windows x64 便携版 ZIP；本页提供版本、文件大小和 SHA-256 校验值。'
    : lang.value === 'en'
      ? 'The Zhenxing Tuba AI Assistant is still in development and not yet released; this page explains the release status, system requirements, and the official entries of the upstream open-source project “图吧工具箱CE” (not this project’s release).'
      : '枕星图吧AI助手当前处于开发阶段，尚未公开发布；本页说明发布状态、系统要求，以及上游开源项目「图吧工具箱CE」的官方入口（非本项目发布）。';

/**
 * 数据说明（**单一来源**：首页、下载页与「关于」页共用同一份文字）。
 *
 * 站点与客户端各自说明；客户端文案随发布状态切换。
 */
export const dataNoticeCopy = () => {
  const released = releaseState().released;
  if (lang.value === 'en') {
    return {
      siteTitle: 'This site (in effect now)',
      site: 'This site has no accounts, no ads and no tracking cookies. Analytics is configurable: the analytics script is loaded only when a self-hosted open-source Umami (MIT) script URL and website ID are configured, and it only counts page views and download-button clicks. The button fires a single “click” marker; real download counts are verified from server logs.',
      clientStatus: released ? 'Client (current public build)' : 'Client (not yet released)',
      client: 'The preview has an official workflow-sharing endpoint. Sharing is on by default and can be disabled in settings. When enabled, confirming a plan can send the goal, related visible conversation, selected tools and execution or download results to our server. Skill submissions and benchmark uploads are separate user actions. AI keys are stored locally; model requests go to your chosen provider, so conversations are not guaranteed to remain offline. Public-release behaviour will be documented with that build.',
    };
  }
  return {
    siteTitle: '本站（已生效）',
    site: '本站没有账号体系，不投放广告，不使用追踪 Cookie。统计是可配置项：只有配置了自托管开源 Umami（MIT）的脚本地址与网站 ID 之后，页面才会加载统计脚本，且只统计页面访问与下载按钮的点击；下载按钮只发一次「点击」标记，真实下载量以服务端日志核对为准。',
    clientStatus: released ? '客户端（当前公开版）' : '客户端（尚未公开发布）',
    client: '预览版已有官方工具流分享入口，默认开启，可在设置中关闭。开启时，确认方案会尝试发送目标、相关可见对话、所选工具及执行与下载结果到我们的服务器。技能投稿和性能测试上传是用户另行操作。AI 密钥存于本机，模型请求会发送到你选择的服务；不能将所有对话理解为只在本机处理。正式公开版的行为会随版本说明。',
  };
};
