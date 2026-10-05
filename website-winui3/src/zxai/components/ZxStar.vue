<template>
  <!-- 枕星品牌符号：四角星（本项目原创矢量绘制，随主题取 currentColor） -->
  <svg
    class="zx-star"
    viewBox="0 0 48 48"
    role="img"
    :aria-label="label"
    focusable="false">
    <path
      d="M24 1.5c0 11.9-10.6 22.5-22.5 22.5C13.4 24 24 34.6 24 46.5 24 34.6 34.6 24 46.5 24 34.6 24 24 13.4 24 1.5Z"
      fill="currentColor" />
  </svg>
</template>

<script setup lang="ts">
withDefaults(defineProps<{ label?: string }>(), {
  label: '枕星图吧AI助手',
});
</script>

<style scoped>
/* 用自定义属性给出默认尺寸：外部只需要设 --zx-star-size，
   不必和 scoped 选择器抢优先级（Vue 的 scoped 属性会抬高特异性）。 */
.zx-star {
  display: block;
  width: var(--zx-star-size, 1em);
  height: var(--zx-star-size, 1em);
  flex: none;
}
</style>
