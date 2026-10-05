<template>
  <div class="zx-theme" role="radiogroup" :aria-label="t('站点主题', 'Site theme')" @keydown="onKeydown">
    <button
      v-for="option in options"
      :key="option.mode"
      ref="buttons"
      type="button"
      class="zx-theme__btn"
      role="radio"
      :aria-checked="themeMode === option.mode"
      :title="t(option.title, option.titleEn)"
      :tabindex="themeMode === option.mode ? 0 : -1"
      @click="select(option.mode)">
      <span class="zx-visually-hidden">{{ t(option.title, option.titleEn) }}</span>
      <ZxIcon :name="option.icon" />
    </button>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import ZxIcon from './ZxIcon.vue';
import { themeMode, setThemeMode, type ThemeMode } from '../theme';
import { t } from '../i18n';

const options: { mode: ThemeMode; icon: string; title: string; titleEn: string }[] = [
  { mode: 'system', icon: 'system', title: '跟随系统', titleEn: 'Follow system' },
  { mode: 'light', icon: 'sun', title: '浅色主题', titleEn: 'Light theme' },
  { mode: 'dark', icon: 'moon', title: '深色主题', titleEn: 'Dark theme' },
];

const buttons = ref<HTMLButtonElement[]>([]);

function select(mode: ThemeMode) {
  setThemeMode(mode);
  const index = options.findIndex((option) => option.mode === mode);
  buttons.value[index]?.focus();
}

/** 单选组语义的标准键盘行为：左右方向键切换并聚焦 */
function onKeydown(event: KeyboardEvent) {
  const keys = ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown'];
  if (!keys.includes(event.key)) return;
  event.preventDefault();
  const current = options.findIndex((option) => option.mode === themeMode.value);
  const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : -1;
  const next = (current + step + options.length) % options.length;
  select(options[next].mode);
}
</script>
