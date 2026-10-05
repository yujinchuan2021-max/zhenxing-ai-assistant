<template>
  <div class="zx-lang" role="radiogroup" :aria-label="t('界面语言', 'Language')" @keydown="onKeydown">
    <button
      v-for="option in LANG_OPTIONS"
      :key="option.id"
      ref="buttons"
      type="button"
      class="zx-lang__btn"
      role="radio"
      :lang="option.id === 'en' ? 'en' : 'zh-CN'"
      :aria-checked="lang === option.id"
      :title="option.title"
      :tabindex="lang === option.id ? 0 : -1"
      @click="select(option.id)">
      <span aria-hidden="true">{{ option.label }}</span>
      <span class="zx-visually-hidden">{{ option.title }}</span>
    </button>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { LANG_OPTIONS, lang, setLang, t, type Lang } from '../i18n';

const buttons = ref<HTMLButtonElement[]>([]);

function select(next: Lang) {
  setLang(next);
  const index = LANG_OPTIONS.findIndex((option) => option.id === next);
  buttons.value[index]?.focus();
}

/** 单选组语义的标准键盘行为：左右方向键切换并聚焦（与主题切换一致） */
function onKeydown(event: KeyboardEvent) {
  const keys = ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown'];
  if (!keys.includes(event.key)) return;
  event.preventDefault();
  const current = LANG_OPTIONS.findIndex((option) => option.id === lang.value);
  const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : -1;
  const next = (current + step + LANG_OPTIONS.length) % LANG_OPTIONS.length;
  select(LANG_OPTIONS[next].id);
}
</script>
