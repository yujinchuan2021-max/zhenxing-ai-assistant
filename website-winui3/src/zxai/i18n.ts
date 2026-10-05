/**
 * 界面语言（中文 / English）—— 官网 V0.1
 *
 * 口径（与主题、正文字体一致）：
 * - 真值：`<html lang="zh-CN|en">`（index.html 内联脚本先写一次，避免首帧闪一下中文）；
 * - 持久化：`localStorage['zxai-lang']`，**只记在访问者本机浏览器**，不写站点配置、不做账号绑定或跨端同步；
 * - 首访默认中文：站点中文优先，不按浏览器语言自动切换（避免误判访客意图）；
 * - 页面文案用 `t('中文原文', 'English')` 内联双语：中文是原文来源，英文缺省时回落中文；
 * - 需要重跑的副作用（SEO 标题/描述/og:locale/JSON-LD）由 main.ts 监听 `lang` 重放，见 seo.ts。
 */

import { computed, ref } from 'vue';

export type Lang = 'zh' | 'en';

/** 首访（以及存储值无效时）使用的语言 */
export const DEFAULT_LANG: Lang = 'zh';

const STORAGE_KEY = 'zxai-lang';

const isLang = (value: unknown): value is Lang => value === 'zh' || value === 'en';

function readStoredLang(): Lang {
  if (typeof window === 'undefined') return DEFAULT_LANG;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (isLang(raw)) return raw;
  } catch {
    /* 隐私模式等场景下 localStorage 可能不可用 */
  }
  return DEFAULT_LANG;
}

export const lang = ref<Lang>(readStoredLang());
export const isEn = computed(() => lang.value === 'en');

/**
 * 内联双语：`t('中文原文', 'English')`。
 * 英文未翻译时显式省略第二个参数 → 回落中文（报告里按此口径统计未译处）。
 */
export function t(zh: string, en?: string): string {
  return isEn.value && en !== undefined ? en : zh;
}

function applyToDom(next: Lang) {
  if (typeof document === 'undefined') return;
  document.documentElement.lang = next === 'en' ? 'en' : 'zh-CN';
  document.documentElement.dataset.lang = next;
}

export function setLang(next: Lang) {
  if (!isLang(next) || next === lang.value) return;
  lang.value = next;
  try {
    window.localStorage.setItem(STORAGE_KEY, next);
  } catch {
    /* ignore */
  }
  applyToDom(next);
}

/** 应用启动时调用一次：把（存储里的或默认的）语言落到 <html lang> */
export function initLang() {
  applyToDom(lang.value);
}

/** 语言切换控件的两个选项（固定顺序：中文 → English） */
export const LANG_OPTIONS: { id: Lang; label: string; title: string }[] = [
  { id: 'zh', label: '中文', title: '简体中文' },
  { id: 'en', label: 'EN', title: 'English' },
];
