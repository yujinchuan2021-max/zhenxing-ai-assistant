<template>
  <div class="zx-font">
    <button
      ref="trigger"
      type="button"
      class="zx-font__btn"
      aria-haspopup="true"
      :aria-expanded="open"
      :title="t(`正文字体：${activeLabel}`, `Body font: ${activeLabel}`)"
      :aria-label="t(`正文字体：${activeLabel}（点击切换）`, `Body font: ${activeLabel} (click to switch)`)"
      @click="toggle">
      <ZxIcon name="type" />
      <span class="zx-font__label">{{ activeLabel }}</span>
    </button>

    <div v-if="open" class="zx-font__panel" role="radiogroup" :aria-label="t('正文字体', 'Body font')" @keydown="onKeydown">
      <p class="zx-font__head">{{ t('正文字体', 'Body font') }}</p>
      <button
        v-for="option in FONT_OPTIONS"
        :key="option.id"
        type="button"
        role="radio"
        class="zx-font__opt"
        :aria-checked="option.id === fontChoice"
        :tabindex="option.id === fontChoice ? 0 : -1"
        @click="choose(option.id)">
        <span class="zx-font__idx" aria-hidden="true">{{ option.order }}</span>
        <span class="zx-font__text">
          <span class="zx-font__name">{{ t(option.label, option.labelEn) }}</span>
          <span class="zx-font__meta">
            {{ option.family }} · {{ option.openSource ? option.license : t('非开源许可', 'not an open-source licence') }}
          </span>
        </span>
        <ZxIcon v-if="option.id === fontChoice" name="check" class="zx-font__check" />
      </button>
      <p class="zx-font__foot">
        {{ t('选择只记在本机浏览器 ·', 'Saved in this browser only ·') }}
        <RouterLink to="/about" @click="close">{{ t('字体许可署名', 'font licences and attribution') }}</RouterLink>
      </p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import ZxIcon from './ZxIcon.vue';
import { FONT_OPTIONS, activeFont, fontChoice, setFontChoice, type FontId } from '../fonts';
import { t } from '../i18n';

const open = ref(false);
const trigger = ref<HTMLButtonElement | null>(null);
const optionButtons = ref<HTMLButtonElement[]>([]);

// 顶栏按钮上的字体名随语言切换（中文界面显示「更纱黑体」，英文界面显示 Sarasa UI SC）
const activeLabel = computed(() => t(activeFont.value.label, activeFont.value.labelEn));

const currentIndex = computed(() => FONT_OPTIONS.findIndex((option) => option.id === fontChoice.value));

function toggle() {
  open.value = !open.value;
}

function close() {
  open.value = false;
}

function choose(id: FontId) {
  setFontChoice(id);
}

function onDocumentPointerDown(event: PointerEvent) {
  const target = event.target as HTMLElement | null;
  if (!target) return;
  if (target.closest('.zx-font')) return;
  close();
}

function onDocumentKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    close();
    trigger.value?.focus();
  }
}

/** 面板内的单选组键盘行为：上下/左右方向键切换并聚焦（与主题切换一致） */
function onKeydown(event: KeyboardEvent) {
  const keys = ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown'];
  if (!keys.includes(event.key)) return;
  event.preventDefault();
  const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : -1;
  const next = (currentIndex.value + step + FONT_OPTIONS.length) % FONT_OPTIONS.length;
  choose(FONT_OPTIONS[next].id);
  optionButtons.value[next]?.focus();
}

watch(open, (isOpen) => {
  if (typeof document === 'undefined') return;
  if (isOpen) {
    document.addEventListener('pointerdown', onDocumentPointerDown, true);
    document.addEventListener('keydown', onDocumentKeydown, true);
  } else {
    document.removeEventListener('pointerdown', onDocumentPointerDown, true);
    document.removeEventListener('keydown', onDocumentKeydown, true);
  }
});

// 打开后把焦点送到当前选项，键盘用户不用先 Tab 一遍
watch(open, async (isOpen) => {
  if (!isOpen) return;
  await Promise.resolve();
  optionButtons.value[currentIndex.value]?.focus();
});
</script>
