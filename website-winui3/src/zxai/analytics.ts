/**
 * 站点统计接线（可配置，默认关闭）
 *
 * - 口径（用户 2026-09-24）：**追踪地址或网站 ID 未配置时不加载任何脚本**；
 *   这里只读 `site.config.ts` 里的公开地址与网站 ID，**不内置任何管理凭据**，
 *   也不调用 Umami 的任何写入接口。
 * - 下载按钮只上报一次「点击」事件：真实下载量以**服务端日志**核对为准。
 * - 未配置时所有上报函数都是空操作，页面与按钮行为完全不受影响。
 *
 * 现状（2026-09-24）：公开配置已填（`https://stats.zhenxingai.com/script.js` + 公开 Website ID）。
 * 注入的 tracker 额外带两个属性：
 *   · `data-domains="zhenxingai.com,www.zhenxingai.com"` —— 本地预览与测试域名不进统计；
 *   · `data-exclude-search="true"` —— 丢查询串，带 ?… 的测试链接不另算页面。
 * DNS/TLS 与线上是否真正收到数据由 Codex 在服务器侧验证（站点侧不探测、不上报管理接口）。
 */

import { isConfigured, siteConfig } from './site.config';

/** 统计是否已配置（两个字段都要有值） */
export const analyticsConfigured = (): boolean =>
  isConfigured(siteConfig.analytics.umami.scriptUrl) && isConfigured(siteConfig.analytics.umami.websiteId);

interface UmamiGlobal {
  track?: (event: string, data?: Record<string, unknown>) => void;
}

function umami(): UmamiGlobal | null {
  if (typeof window === 'undefined') return null;
  const candidate = (window as unknown as { umami?: UmamiGlobal }).umami;
  return candidate && typeof candidate.track === 'function' ? candidate : null;
}

/**
 * 注入 Umami 脚本。返回是否真的注入了。
 * 已有同源脚本时不重复注入（避免热更新/多次调用叠脚本）。
 */
export function initAnalytics(): boolean {
  if (typeof document === 'undefined') return false;
  if (!analyticsConfigured()) return false;

  const scriptUrl = siteConfig.analytics.umami.scriptUrl as string;
  const websiteId = siteConfig.analytics.umami.websiteId as string;
  if (document.querySelector(`script[data-website-id="${websiteId}"]`)) return true;

  const script = document.createElement('script');
  script.async = true;
  script.defer = true;
  script.src = scriptUrl;
  script.dataset.websiteId = websiteId;
  // 只统计正式域名：本地预览 / 测试域名不进统计（空值则不写该属性）
  const domains = siteConfig.analytics.umami.domains;
  if (isConfigured(domains)) script.dataset.domains = domains;
  // 丢查询串：带 ?… 的测试链接不被当成不同页面
  if (siteConfig.analytics.umami.excludeSearch) script.dataset.excludeSearch = 'true';
  document.head.appendChild(script);
  return true;
}

/** 发送自定义事件；未配置或脚本未就绪时是空操作，返回是否真的上报 */
export function trackEvent(name: string, data?: Record<string, unknown>): boolean {
  const tracker = umami();
  if (!tracker?.track) return false;
  tracker.track(name, data);
  return true;
}

/**
 * 下载点击标记：只标记「有人点了这个入口」，**不代表下载量**。
 * 真实下载量以服务端日志核对（见 site.config.ts 的 downloadCountSource）。
 */
export function trackDownloadClick(where: string, extra?: Record<string, unknown>): boolean {
  return trackEvent(siteConfig.analytics.downloadEventName, { where, ...extra });
}
