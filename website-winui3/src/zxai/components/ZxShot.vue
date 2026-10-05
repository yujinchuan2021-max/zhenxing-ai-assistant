<template>
  <figure class="zx-shot zx-shot-pair">
    <div class="zx-shot--dark">
      <img
        :src="dark"
        :alt="`${alt}${t('（深色主题）', ' (dark theme)')}`"
        :width="width"
        :height="height"
        loading="lazy"
        decoding="async" />
    </div>
    <div v-if="light" class="zx-shot--light">
      <img
        :src="light"
        :alt="`${alt}${t('（浅色主题）', ' (light theme)')}`"
        :width="width"
        :height="height"
        loading="lazy"
        decoding="async" />
    </div>
    <figcaption v-if="caption" class="zx-shot__caption">{{ caption }}</figcaption>
  </figure>
</template>

<script setup lang="ts">
/**
 * 应用截图展示框：深/浅两套真实截图按当前主题切换。
 * - 两张图都在 DOM 里，由 CSS（html[data-theme=…]）决定显示哪张，避免 JS 切图闪动；
 * - 只提供 dark 时两套主题都显示该图（例如当前只有深色版截图的页面）。
 */
import { t } from '../i18n';

withDefaults(
  defineProps<{
    dark: string;
    /** 浅色版截图；缺省时两套主题都用 dark 图 */
    light?: string;
    alt: string;
    caption?: string;
    width?: number;
    height?: number;
  }>(),
  { light: '', caption: '', width: 2030, height: 1092 },
);
</script>
