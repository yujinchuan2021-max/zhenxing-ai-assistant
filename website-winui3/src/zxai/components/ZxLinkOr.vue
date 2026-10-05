<script setup lang="ts">
/**
 * 入口渲染的统一开关：**已配置 → 真链接；未配置 → 诚实空态**。
 *
 * `href` 一律来自 `site.config.ts` 的 `links.*`：
 * - 未配置（null / 空串）时**绝不**生成占位链接，只显示调用方给的 fallback 文案；
 * - external（默认 true）新窗口打开并标注「外部链接」；本站内部地址传 `false`。
 *
 * 两个分支都带 `data-zx-link="<name>"`（对应 config 的字段名），
 * 便于 `scripts/check-links.mjs` 按「本该是链接的入口」精确断言（而不是按 .zx-entry 猜）。
 * 视觉上沿用 `.zx-entry__state`（空态）与同位置的 `--link` 修饰（已配置），占位一致不会跳版。
 *
 * **埋点纪律**：真链接分支会 emit `activate`；空态分支是纯 `<span>`、**不产生任何事件**。
 * 调用方要埋点一律绑 `@activate`——**别在组件上挂 `@click`**：Vue 的属性下落会把它同时挂到
 * 空态 `<span>` 上，点「未配置」的占位也会触发（曾实际发生：下载页把 `download-click` 记到了空态占位上）。
 */
import { isConfigured } from '../site.config';
import { t } from '../i18n';

const emit = defineEmits<{ (e: 'activate'): void }>();

const props = withDefaults(
  defineProps<{
    /** 对应 siteConfig.links 的字段名，如 download / repo / docs / community / feedback */
    name: string;
    href?: string | null;
    /** 已配置时显示的链接文案 */
    text: string;
    /** 未配置时显示的诚实空态文案 */
    fallback: string;
    external?: boolean;
  }>(),
  { href: null, external: true },
);
</script>

<template>
  <a
    v-if="isConfigured(props.href)"
    :data-zx-link="props.name"
    class="zx-entry__state zx-entry__state--link"
    :href="props.href"
    :target="props.external ? '_blank' : undefined"
    :rel="props.external ? 'noopener noreferrer' : undefined"
    @click="emit('activate')"
  >
    {{ props.text }}<span v-if="props.external" class="zx-small">{{ t('（外部链接）', ' (external link)') }}</span>
  </a>
  <span v-else :data-zx-link="props.name" class="zx-entry__state">{{ props.fallback }}</span>
</template>
