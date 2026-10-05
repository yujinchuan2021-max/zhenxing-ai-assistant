<template>
  <header class="zx-header">
    <div class="zx-container zx-header__inner">
      <RouterLink class="zx-brand" to="/" @click="closeMenu">
        <ZxStar class="zx-brand__star" />
        <span>{{ siteConfig.brand.name }}</span>
        <span class="zx-brand__ver" :title="t('官网版本', 'Site version')">{{ siteConfig.brand.version }}</span>
      </RouterLink>

      <nav
        id="zx-site-nav"
        class="zx-nav"
        :class="{ 'is-open': menuOpen }"
        :aria-label="t('站点导航', 'Site navigation')"
        @click="closeMenu">
        <RouterLink
          v-for="item in navItems"
          :key="item.path"
          class="zx-nav__link"
          :class="{ 'is-active': isActive(item.path) }"
          :to="item.path"
          :aria-current="isActive(item.path) ? 'page' : undefined">
          {{ item.label }}
        </RouterLink>
      </nav>

      <div class="zx-header__end">
        <ZxFontSwitch />
        <ZxLangSwitch />
        <ZxThemeSwitch />
        <RouterLink class="zx-btn zx-btn--primary zx-header__cta" to="/download">
          <ZxIcon name="download" />
          <span>{{ t('下载状态', 'Release status') }}</span>
        </RouterLink>
        <button
          type="button"
          class="zx-menu-btn"
          :aria-expanded="menuOpen"
          aria-controls="zx-site-nav"
          :aria-label="menuOpen ? t('关闭导航菜单', 'Close navigation menu') : t('打开导航菜单', 'Open navigation menu')"
          @click="toggleMenu">
          <ZxIcon :name="menuOpen ? 'close' : 'menu'" />
        </button>
      </div>
    </div>
  </header>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import ZxStar from './ZxStar.vue';
import ZxIcon from './ZxIcon.vue';
import ZxThemeSwitch from './ZxThemeSwitch.vue';
import ZxFontSwitch from './ZxFontSwitch.vue';
import ZxLangSwitch from './ZxLangSwitch.vue';
import { siteConfig } from '../site.config';
import { t } from '../i18n';

const navItems = computed(() => [
  { label: t('首页', 'Home'), path: '/' },
  { label: t('下载', 'Download'), path: '/download' },
  { label: t('文档', 'Docs'), path: '/docs' },
  { label: t('社区', 'Community'), path: '/community' },
  { label: t('关于', 'About'), path: '/about' },
]);

const route = useRoute();
const menuOpen = ref(false);

const isActive = (path: string) => (path === '/' ? route.path === '/' : route.path.startsWith(path));

function toggleMenu() {
  menuOpen.value = !menuOpen.value;
}

function closeMenu() {
  menuOpen.value = false;
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') closeMenu();
}

watch(() => route.fullPath, closeMenu);
watch(menuOpen, (open) => {
  if (typeof document === 'undefined') return;
  if (open) document.addEventListener('keydown', onKeydown);
  else document.removeEventListener('keydown', onKeydown);
});

onBeforeUnmount(() => {
  if (typeof document !== 'undefined') document.removeEventListener('keydown', onKeydown);
});
</script>
