/**
 * 站点 SEO 更新：标题 / 描述 / og / canonical / JSON-LD
 *
 * 原则：
 * - 文案全部是枕星版事实，不沿用上游的宣传口径；
 * - 本项目正式域名未配置时，**不写 canonical 与 og:url**（宁缺毋滥）；
 * - og:image / twitter:image 在域名配置后自动升级为绝对地址（未配置时保持站点内相对路径）；
 * - 页面描述随配置切换（社区/文档/下载），文案来源统一在 `site.config.ts`，避免页面与描述口径不一致；
 * - 不编造评分、下载量、用户评价等结构化数据。
 *
 * 双语（V0.1）：标题与描述随界面语言切换（`og:locale` 同步 `zh_CN` / `en_US`）。
 * 语言变化本身不会触发路由，所以 main.ts 监听 `lang` 后重放一次 `applyPageSeo`。
 */

import {
  brandCopy,
  communityCopy,
  docsCopy,
  downloadSeoDescription,
  facts,
  isConfigured,
  releaseState,
  siteConfig,
} from './site.config';
import { isEn } from './i18n';

export interface PageSeo {
  title: string;
  description: string;
  /** 路由路径，如 '/'、'/download' */
  path: string;
  jsonLd?: Record<string, unknown>;
}

/**
 * 按当前语言构建各页 SEO（每次读取都重新构建：语言切换后由 main.ts 重放）。
 * 中文侧文案与站点默认渲染完全一致（检查脚本按默认中文断言）。
 */
export const buildPageSeoMap = (): Record<string, PageSeo> => {
  const brand = brandCopy();
  const brandName = siteConfig.brand.name;
  const en = isEn.value;
  const release = releaseState();
  const factSet = facts();

  return {
    home: {
      title: brand.title,
      description: brand.description,
      path: '/',
      jsonLd: {
        '@context': 'https://schema.org',
        '@type': 'SoftwareApplication',
        name: brandName,
        description: brand.description,
        applicationCategory: 'UtilitiesApplication',
        operatingSystem: factSet.minOs,
        softwareRequirements: `${factSet.minOs}；${release.released ? 'x64' : factSet.archs}`,
        softwareVersion: release.released ? (en ? siteConfig.releaseArtifact.versionEn : siteConfig.releaseArtifact.version) : undefined,
        datePublished: release.released ? siteConfig.releaseArtifact.publishedAt : undefined,
        license: siteConfig.upstream.licenseUrl,
        isAccessibleForFree: true,
        offers: { '@type': 'Offer', price: '0', priceCurrency: 'CNY' },
        // 不写评分或下载量；下载地址只有在整包信息齐备且发布开关开启后才输出。
        downloadUrl: release.released ? siteConfig.links.download : undefined,
        releaseNotes: release.released
          ? siteConfig.links.releaseNotes
          : en
            ? 'A derivative of the upstream open-source project “图吧工具箱CE” (GPL-3.0); currently in development.'
            : '基于上游开源项目「图吧工具箱CE」（GPL-3.0）的衍生改造，当前处于开发阶段。',
        isBasedOn: siteConfig.upstream.repo,
      },
    },
    download: {
      title: en ? `Download 0.1.1 preview — ${brandName}` : `下载 0.1.1 公开预览版 —— ${brandName}`,
      // 描述随 releaseState() 切换（与下载页文案同一来源）
      description: downloadSeoDescription(),
      path: '/download',
    },
    'tool-downloads': {
      title: en ? `Vendor tool downloads — ${brandName}` : `原厂工具下载 —— ${brandName}`,
      description: en
        ? 'Download available original vendor files directly from 枕星. Choose the correct Windows architecture, check file size and SHA-256, and follow the vendor’s installation requirements.'
        : '从枕星直接下载已提供的原厂工具文件，按 Windows 架构选择版本、核对文件大小和 SHA-256，再按厂商要求完成安装。',
      path: '/tools/download',
    },
    docs: {
      title: en ? `Getting started — ${brandName}` : `新手指南 —— ${brandName}`,
      // 描述随 links.docs 切换：未配置说明「整理中」，配置后不再说未建立
      description: docsCopy().seo,
      path: '/docs',
    },
    community: {
      title: en ? `Community — ${brandName}` : `社区 —— ${brandName}`,
      // 描述随 links.community 切换：未配置才写「尚未开通、地址尚未配置」
      description: communityCopy().seo,
      path: '/community',
    },
    about: {
      title: en ? `About — ${brandName}` : `关于 —— ${brandName}`,
      description: en
        ? 'About the 枕星图吧AI助手: what it is, how it relates to the upstream open-source project “图吧工具箱CE”, licensing and credits.'
        : '了解枕星图吧AI助手：产品定位、与上游开源项目「图吧工具箱CE」的关系与署名、开源方向与致谢。',
      path: '/about',
    },
  };
};

const META_KEYS = [
  'description',
  'og:title',
  'og:description',
  'og:site_name',
  'og:url',
  'og:type',
  'og:locale',
  'og:image',
];

function setMeta(property: string, content: string) {
  let el = document.head.querySelector<HTMLMetaElement>(`meta[property="${property}"]`);
  if (!el) {
    el = document.createElement('meta');
    el.setAttribute('property', property);
    document.head.appendChild(el);
  }
  el.setAttribute('content', content);
}

function setNameMeta(name: string, content: string) {
  let el = document.head.querySelector<HTMLMetaElement>(`meta[name="${name}"]`);
  if (!el) {
    el = document.createElement('meta');
    el.setAttribute('name', name);
    document.head.appendChild(el);
  }
  el.setAttribute('content', content);
}

function removeMeta(property: string) {
  document.head.querySelector(`meta[property="${property}"]`)?.remove();
}

function setCanonical(href: string | null) {
  const selector = 'link[rel="canonical"]';
  const existing = document.head.querySelector<HTMLLinkElement>(selector);
  if (!href) {
    existing?.remove();
    return;
  }
  const el = existing ?? document.createElement('link');
  el.setAttribute('rel', 'canonical');
  el.setAttribute('href', href);
  if (!existing) document.head.appendChild(el);
}

function setJsonLd(data: Record<string, unknown> | undefined) {
  const id = 'zxai-jsonld';
  const prev = document.getElementById(id);
  if (!data) {
    prev?.remove();
    return;
  }
  const el = prev ?? document.createElement('script');
  el.id = id;
  el.setAttribute('type', 'application/ld+json');
  el.textContent = JSON.stringify(data);
  if (!prev) document.head.appendChild(el);
}

/** 社交分享图：站点内相对路径，域名配置后由下方逻辑升级为绝对地址 */
const OG_IMAGE_PATH = '/zxai/og.png';

export function applyPageSeo(routeName: string) {
  const map = buildPageSeoMap();
  const seo = map[routeName] ?? map.home;
  const siteUrl = siteConfig.links.siteUrl;
  const baseUrl = isConfigured(siteUrl) ? (siteUrl.endsWith('/') ? siteUrl : `${siteUrl}/`) : null;
  const absoluteUrl = baseUrl ? new URL(seo.path, baseUrl).href : null;
  // 多数社交平台要求 og:image 为绝对地址：域名定了就写绝对，未定则保持相对（不编造域名）
  const ogImage = baseUrl ? new URL(OG_IMAGE_PATH, baseUrl).href : OG_IMAGE_PATH;

  document.title = seo.title;
  setNameMeta('description', seo.description);
  setMeta('og:title', seo.title);
  setMeta('og:description', seo.description);
  setMeta('og:site_name', siteConfig.brand.name);
  setMeta('og:type', 'website');
  setMeta('og:locale', isEn.value ? 'en_US' : 'zh_CN');
  setMeta('og:image', ogImage);
  setNameMeta('twitter:image', ogImage);
  setNameMeta('twitter:title', seo.title);
  setNameMeta('twitter:description', seo.description);

  if (absoluteUrl) {
    setMeta('og:url', absoluteUrl);
    setCanonical(absoluteUrl);
  } else {
    // 域名未定：不输出会误导的 canonical / og:url
    removeMeta('og:url');
    setCanonical(null);
  }

  setJsonLd(seo.jsonLd);
}

export { META_KEYS };
