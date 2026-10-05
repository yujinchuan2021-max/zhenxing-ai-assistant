/**
 * 主题管理：浅色 / 深色 / 跟随系统
 *
 * - 真值来源：<html data-theme="light|dark">（CSS 只认这个属性）
 * - 首屏防闪烁：index.html 内联脚本在样式加载前先写一次 data-theme
 * - 持久化：localStorage['zxai-theme'] = 'system' | 'light' | 'dark'
 */

import { ref, computed } from 'vue';

export type ThemeMode = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

const STORAGE_KEY = 'zxai-theme';
const MODES: ThemeMode[] = ['system', 'light', 'dark'];

const media = typeof window !== 'undefined'
  ? window.matchMedia('(prefers-color-scheme: dark)')
  : null;

function readStoredMode(): ThemeMode {
  if (typeof window === 'undefined') return 'system';
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (raw && (MODES as string[]).includes(raw)) return raw as ThemeMode;
  } catch {
    /* 隐私模式等场景下 localStorage 可能不可用 */
  }
  return 'system';
}

export const themeMode = ref<ThemeMode>(readStoredMode());
export const systemPrefersDark = ref<boolean>(media?.matches ?? true);

export const resolvedTheme = computed<ResolvedTheme>(() => {
  if (themeMode.value === 'light') return 'light';
  if (themeMode.value === 'dark') return 'dark';
  return systemPrefersDark.value ? 'dark' : 'light';
});

function applyToDom(theme: ResolvedTheme, mode: ThemeMode) {
  if (typeof document === 'undefined') return;
  const root = document.documentElement;
  root.dataset.theme = theme;
  root.dataset.themeMode = mode;
  root.style.colorScheme = theme;

  // 移动端浏览器地址栏配色
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', theme === 'dark' ? '#141311' : '#FAF7F2');
}

export function setThemeMode(mode: ThemeMode) {
  if (!MODES.includes(mode)) return;
  themeMode.value = mode;
  try {
    window.localStorage.setItem(STORAGE_KEY, mode);
  } catch {
    /* ignore */
  }
  applyToDom(resolvedTheme.value, mode);
}

/** 应用启动时调用一次：绑定系统主题变化监听 */
export function initTheme() {
  applyToDom(resolvedTheme.value, themeMode.value);
  if (!media) return;
  const onChange = (event: MediaQueryListEvent) => {
    systemPrefersDark.value = event.matches;
    if (themeMode.value === 'system') applyToDom(resolvedTheme.value, 'system');
  };
  if (typeof media.addEventListener === 'function') {
    media.addEventListener('change', onChange);
  } else if (typeof (media as MediaQueryList).addListener === 'function') {
    (media as MediaQueryList).addListener(onChange);
  }
}
