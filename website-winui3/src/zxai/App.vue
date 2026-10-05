<template>
  <div class="zx-shell">
    <a class="zx-skip" href="#main">{{ t('跳到主要内容', 'Skip to main content') }}</a>
    <ZxHeader />
    <main id="main" class="zx-main" tabindex="-1">
      <RouterView v-slot="{ Component }">
        <Transition name="zx-page" mode="out-in">
          <component :is="Component" :key="$route.path" />
        </Transition>
      </RouterView>
    </main>
    <ZxFooter />
  </div>
</template>

<script setup lang="ts">
import ZxHeader from './components/ZxHeader.vue';
import ZxFooter from './components/ZxFooter.vue';
import { t } from './i18n';
</script>

<style scoped>
/* 页面切换：轻微淡入上移；prefers-reduced-motion 时由全局规则压掉时长 */
.zx-page-enter-active,
.zx-page-leave-active {
  transition: opacity 0.18s ease, transform 0.18s ease;
}

.zx-page-enter-from {
  opacity: 0;
  transform: translateY(6px);
}

.zx-page-leave-to {
  opacity: 0;
}
</style>
