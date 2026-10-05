/**
 * 正文字体选择：更纱黑体 / Noto Sans SC / HarmonyOS Sans SC（固定顺序 ①②③）
 *
 * - 真值来源：<html data-font="sarasa|noto|harmony">（CSS 只认这个属性）
 * - 首屏防闪烁：index.html 内联脚本在样式加载前先写一次 data-font
 * - 持久化：localStorage['zxai-font']，**只保存在访问者本机浏览器**——
 *   不写进站点配置、不做账号绑定、不做跨端同步
 * - 首次访问：更纱黑体（DEFAULT_FONT）
 * - 字体文件与许可位于 public/zxai/fonts/（生成脚本 scripts/prepare-fonts.py）
 */

import { computed, ref } from 'vue';

export type FontId = 'sarasa' | 'noto' | 'harmony';

export interface FontOption {
  id: FontId;
  /** 展示序号 ①②③（固定顺序：更纱 → Noto → 鸿蒙） */
  order: string;
  /** 顶栏与正文里说的名字 */
  label: string;
  /** 同一名字的英文写法（界面语言为 English 时使用） */
  labelEn: string;
  /** CSS @font-face 的家族名 */
  family: string;
  /** 许可名（用于署名文案） */
  license: string;
  /** 同一许可名的英文写法（界面语言为 English 时使用） */
  licenseEn: string;
  /** 随站点保留的许可原文 */
  licenseHref: string;
  /** 是否开源字体：鸿蒙的自订协议不是开源许可，页面不得称其开源 */
  openSource: boolean;
  /** 字体来源页 */
  sourceHref: string;
  /** 一句话说明（来源 / 许可要点） */
  note: string;
  /** 同一句话的英文版（界面语言为 English 时使用；中文仍是原文来源） */
  noteEn: string;
}

export const FONT_OPTIONS: readonly FontOption[] = [
  {
    id: 'sarasa',
    order: '①',
    label: '更纱黑体',
    labelEn: 'Sarasa UI SC',
    family: 'Sarasa UI SC',
    license: 'SIL OFL 1.1',
    licenseEn: 'SIL OFL 1.1',
    licenseHref: '/zxai/fonts/LICENSE-sarasa-gothic-OFL.txt',
    openSource: true,
    sourceHref: 'https://github.com/be5invis/Sarasa-Gothic/releases',
    note: '更纱黑体 Sarasa UI SC v1.0.41（Belleve Invis），SIL OFL 1.1，按站点用字做子集',
    noteEn: 'Sarasa UI SC v1.0.41 (Belleve Invis), SIL OFL 1.1, subset to the characters this site uses',
  },
  {
    id: 'noto',
    order: '②',
    label: 'Noto Sans SC',
    labelEn: 'Noto Sans SC',
    family: 'Noto Sans SC',
    license: 'SIL OFL 1.1',
    licenseEn: 'SIL OFL 1.1',
    licenseHref: '/zxai/fonts/LICENSE-noto-sans-sc-OFL.txt',
    openSource: true,
    sourceHref: 'https://fonts.google.com/noto',
    note: 'Noto Sans SC（Google / Adobe），SIL OFL 1.1，与桌面应用内置的中文回退字体同源，按站点用字做子集',
    noteEn: 'Noto Sans SC (Google / Adobe), SIL OFL 1.1, the same family as the desktop app’s built-in Chinese fallback, subset to the characters this site uses',
  },
  {
    id: 'harmony',
    order: '③',
    label: 'HarmonyOS Sans SC',
    labelEn: 'HarmonyOS Sans SC',
    family: 'HarmonyOS Sans SC',
    license: 'HarmonyOS Sans 字体许可协议（非开源）',
    licenseEn: 'HarmonyOS Sans font licence (not open source)',
    licenseHref: '/zxai/fonts/LICENSE-HarmonyOS-Sans.txt',
    openSource: false,
    sourceHref: 'https://developer.huawei.com/consumer/cn/design/resource',
    note: 'HarmonyOS Sans SC（华为），HarmonyOS Sans 字体许可协议；协议禁止修改，故按原文件随站点附带、未做子集或格式转换',
    noteEn: 'HarmonyOS Sans SC (Huawei), under the HarmonyOS Sans font licence; the licence forbids modification, so the official files ship unchanged — no subsetting, no format conversion',
  },
] as const;

/** 首次访问（以及存储值无效时）使用的字体 */
export const DEFAULT_FONT: FontId = 'sarasa';

const STORAGE_KEY = 'zxai-font';
const IDS = FONT_OPTIONS.map((option) => option.id);

function isFontId(value: unknown): value is FontId {
  return typeof value === 'string' && (IDS as string[]).includes(value);
}

function readStoredFont(): FontId {
  if (typeof window === 'undefined') return DEFAULT_FONT;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (isFontId(raw)) return raw;
  } catch {
    /* 隐私模式等场景下 localStorage 可能不可用 */
  }
  return DEFAULT_FONT;
}

export const fontChoice = ref<FontId>(readStoredFont());

export const activeFont = computed<FontOption>(
  () => FONT_OPTIONS.find((option) => option.id === fontChoice.value) ?? FONT_OPTIONS[0],
);

function applyToDom(id: FontId) {
  if (typeof document === 'undefined') return;
  document.documentElement.dataset.font = id;
}

export function setFontChoice(id: FontId) {
  if (!isFontId(id)) return;
  fontChoice.value = id;
  try {
    window.localStorage.setItem(STORAGE_KEY, id);
  } catch {
    /* ignore */
  }
  applyToDom(id);
}

/** 应用启动时调用一次：把（存储里的或默认的）选择落到 <html data-font> */
export function initFont() {
  applyToDom(fontChoice.value);
}
