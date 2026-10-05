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
  /** 当前公开预览版的完整下载文件 */
  download: string | null;
  /** 当前版本更新说明与反馈入口 */
  releaseNotes: string | null;
  issues: string | null;
  /** 与发布包对应的本项目源码 ZIP；实际归档并验收后填写，不影响便携版发布状态。 */
  sourceArchive: string | null;
  /** 枕星版文档站（整理完成后填写） */
  docs: string | null;
  /** 社区入口：平台已定为自托管 Discourse，2026-09-24 已上线 → 见下方 communityUrl */
  community: string | null;
  /** 邮件反馈入口；公开问题也可使用 issues */
  feedback: string | null;
}

/** 当前公开预览包：单一 Windows x64 便携 ZIP。 */
export interface ReleaseArtifact {
  version: string | null;
  versionEn: string | null;
  sizeBytes: number | null;
  sha256: string | null;
  publishedAt: string | null;
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
 * 邮件可用于反馈不适合公开的信息；公开问题请使用 GitHub Issues。
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
    shortName: '枕星图吧AI助手',
    /** 顶栏与页脚标明当前公开预览；产品版本与预览修订号同时保留。 */
    version: 'V0.1 · r10',
    /** 首页 <title> 的主口径 */
    title: '枕星图吧AI助手 —— 带你把 AI 用起来',
    tagline: '说出你的想法，带你把 AI 用起来',
    description:
      '从一句目标开始：比较方案、复用已有软件、确认后准备支持的工具，再交给所选工具或 AI Agent。统一 AI 配置，技能制作与官方技能库，枕星AI资讯和社区。V0.1 · r10 Windows x64 公开预览版已提供下载。',
  },

  links: {
    // 正式域名已定（2026-09-23）：canonical / og:url / og:image 自动绝对化。
    // 反馈渠道已定（2026-09-24）：先用邮箱，页面按「邮件反馈」呈现（真链接，不是空态）。
    // 社区已上线（2026-09-24，用户确认论坛首页在公网正常显示、管理员已激活）：见 communityUrl。
    // r10 公开预览使用 GitHub 手动下载；不代表客户端自动更新已接入 GitHub。
    siteUrl: 'https://zhenxingai.com',
    repo: 'https://github.com/yujinchuan2021-max/zhenxing-ai-assistant',
    download: 'https://github.com/yujinchuan2021-max/zhenxing-ai-assistant/releases/download/v0.1.0-preview-r10/ZhenxingAI-v0.1-workbench-preview-20261005-r10.zip',
    sourceArchive: 'https://github.com/yujinchuan2021-max/zhenxing-ai-assistant/archive/refs/tags/v0.1.0-preview-r10.zip',
    releaseNotes: 'https://github.com/yujinchuan2021-max/zhenxing-ai-assistant/releases/tag/v0.1.0-preview-r10',
    issues: 'https://github.com/yujinchuan2021-max/zhenxing-ai-assistant/issues',
    docs: 'https://zhenxingai.com/docs',
    community: communityUrl,
    feedback: `mailto:${feedbackEmail}`,
  } as SiteLinks,

  releaseArtifact: {
    version: 'V0.1 · r10 公开预览版',
    versionEn: 'V0.1 · r10 public preview',
    sizeBytes: 431771859,
    sha256: 'df0d2acd53f7b30faff24fd12125fb8f17676e53175629a9d381fcbac7ad7ca9',
    publishedAt: '2026-10-05',
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
    downloadCountSource: '文件托管方的下载记录（当前为 GitHub）',
    downloadCountSourceEn: 'the file host’s download records (currently GitHub)',
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
    released: true,
    label: 'V0.1 · r10 公开预览版 · 已开放下载',
    note: '2026 年 10 月 5 日发布 Windows x64 便携版。请手动下载并完整解压到新文件夹；本版本为公开预览，真实账号登录和部分原生环境仍待复测。',
  },
} as const;

/**
 * 品牌文案随界面语言切换，中英版本同时维护能力和发布边界。
 */
export const brandCopy = () =>
  lang.value === 'en'
    ? {
        title: '枕星图吧AI助手 — get AI working for you',
        tagline: 'Say what you want to do — we get AI working for you',
        description:
          'Start with a goal: compare plans, reuse installed software, prepare supported tools after confirmation, then hand off to your chosen tools or AI agent. Unified AI setup, skill creation and a skill library, AI news and community. The V0.1 · r10 Windows x64 public preview is available to download.',
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
          label: 'V0.1 · r10 public preview · available to download',
          note: 'Windows x64 portable build, released on 5 October 2026. Download manually and extract the complete ZIP into a new folder. This is a public preview; real-account login and some native environments still need retesting.',
        }
      : {
          released: true,
          label: siteConfig.status.label,
          note: siteConfig.status.note,
        }
    : lang.value === 'en'
      ? {
          released: false,
          label: 'Download temporarily unavailable',
          note: 'A download is shown only when the file, version, size and checksum are all configured. Please check the GitHub releases page for the current status.',
        }
      : {
          released: false,
          label: '下载信息暂不可用',
          note: '完整文件、版本、大小与校验值确认后才显示下载入口；当前状态请查看 GitHub 发布页。',
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
          statusTitle: '枕星AI社区 is open',
          statusBody:
            'Ask questions, share your goals and workflows, exchange skills, and keep useful troubleshooting notes. Open the community below to register or log in. The forum uses a separate account; no model API key is needed.',
          sectionTitle: 'What is in the community',
          seo: 'The 枕星图吧AI助手 community runs on the self-hosted open-source forum Discourse (GPL-2.0): tool usage, AI workflows, skill sharing and troubleshooting.',
        }
      : {
          configured: true as const,
          statusTitle: '枕星AI社区已开通',
          statusBody:
            '交流工具经验，分享目标方案、技能与排障记录。点击下方入口，注册或登录后即可提问与交流。论坛使用独立账号，不需要提交模型 API Key。',
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
          seo: 'The 枕星图吧AI助手 community will run on the self-hosted open-source forum Discourse (GPL-2.0): tool usage, AI workflows, skill sharing and troubleshooting. Not yet open; the address is prepared but not enabled.',
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
            'Download and extract the complete r10 preview, then configure AI, describe your goal, confirm a plan and open the prepared tools. This guide covers first use, skills, news and recovery.',
          statusTitle: 'Getting started with the r10 public preview',
          seo: '枕星图吧AI助手 r10 getting started: download and first launch, global AI setup, goal workflows, supported installation, App Center, skills, news and troubleshooting.',
        }
      : {
          configured: true as const,
          intro: '先下载并完整解压 r10 预览包，再按四步开始：配置 AI、说目标、确认方案、打开工具。这里也提供技能、资讯与遇到问题时的操作说明。',
          statusTitle: 'r10 公开预览新手指南',
          seo: '枕星图吧AI助手 r10 新手指南：下载与首次启动、AI 全局配置、目标工作台、支持软件安装、应用中心、技能库、资讯与常见问题。',
        }
    : lang.value === 'en'
      ? {
          configured: false as const,
          intro:
            'The 枕星图吧AI助手 docs site is not up yet: documentation will be organised and released together with the code repository. Until then this page lists trustworthy alternatives and makes clear what does not belong to this project.',
          statusTitle: 'Status: in preparation, docs site not established',
          seo: '枕星图吧AI助手 documentation is in preparation; for now you can refer to the upstream “图吧工具箱CE” documentation (external link, not this project’s docs site).',
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
      ? 'Download the 枕星图吧AI助手 V0.1 · r10 public preview for Windows x64. Includes the complete portable ZIP, source, SHA-256, first-launch instructions and known limitations.'
      : '枕星图吧AI助手 V0.1 · r10 公开预览版已公开发布：Windows x64 完整便携 ZIP、对应源码、SHA-256、首次启动说明与已知限制。'
    : lang.value === 'en'
      ? 'The 枕星图吧AI助手 is still in development and not yet released; this page explains the release status, system requirements, and the official entries of the upstream open-source project “图吧工具箱CE” (not this project’s release).'
      : '枕星图吧AI助手下载信息暂不可用；请通过本项目 GitHub 发布页核对当前版本，本页也提供系统要求与新手指南。';

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
      site: 'This product site has no account system or ads. Self-hosted Umami analytics records page views and download-entry clicks without tracking cookies. Theme, font and language preferences stay in your browser. A click does not confirm a completed download; package download records are maintained by the file host, currently GitHub. The community uses its own forum account.',
      clientStatus: released ? 'Client (r10 public preview)' : 'Client (download unavailable)',
      client: 'Workflow sharing is on by default and can be disabled in settings. When enabled, confirming a plan can send the goal, the full visible conversation in that session (excluding hidden reasoning), selected tools and execution or download results to our server. Skill submissions and benchmark uploads are separate user actions. AI keys are stored locally; model requests go to your chosen provider. See the r10 release notes for this build’s validation scope and known limitations.',
    };
  }
  return {
    siteTitle: '本站（已生效）',
    site: '产品介绍站没有账号体系和广告。自托管 Umami 统计页面访问与下载入口点击，不使用追踪 Cookie；主题、字体与语言偏好保存在浏览器本地。点击不代表下载完成，文件下载记录由托管方提供，当前为 GitHub。社区另有独立论坛账号。',
    clientStatus: released ? '客户端（r10 公开预览版）' : '客户端（下载信息暂不可用）',
    client: '工具流分享默认开启，可在设置中关闭。开启时，确认方案会尝试发送目标、该会话的完整可见对话（不含隐藏思考过程）、所选工具及执行与下载结果到我们的服务器。技能投稿和性能测试上传是用户另行操作。AI 密钥存于本机，模型请求会发送到你选择的服务。当前版本的验证范围与已知限制见 r10 更新说明。',
  };
};
