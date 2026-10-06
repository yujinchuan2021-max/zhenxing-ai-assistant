import { createRouter, createWebHistory } from 'vue-router';
import type { RouteRecordRaw } from 'vue-router';
import { applyPageSeo } from './seo';

/**
 * 枕星图吧AI助手 官网路由
 *
 * 只保留本站自己的栏目：首页 / 下载状态 / 文档 / 社区 / 关于。
 * 上游官网的栏目路径（/why、/ranking、/latency、/mushroom、/download/thanks、
 * /guide/* 等）由 catch-all 回落到首页——本站没有这些栏目，也不冒充上游站点。
 */
const routes: RouteRecordRaw[] = [
  { path: '/', name: 'home', component: () => import('./pages/HomePage.vue') },
  { path: '/download', name: 'download', component: () => import('./pages/DownloadPage.vue') },
  { path: '/tools/download/:id?', name: 'tool-downloads', component: () => import('./pages/ToolDownloadsPage.vue') },
  { path: '/tools/acquire/:id?', name: 'tool-acquisition', component: () => import('./pages/ToolAcquisitionPage.vue') },
  { path: '/docs', name: 'docs', component: () => import('./pages/DocsPage.vue') },
  { path: '/community', name: 'community', component: () => import('./pages/CommunityPage.vue') },
  { path: '/about', name: 'about', component: () => import('./pages/AboutPage.vue') },
  { path: '/:pathMatch(.*)*', redirect: '/' },
];

const prefersReducedMotion = () =>
  typeof window !== 'undefined' &&
  typeof window.matchMedia === 'function' &&
  window.matchMedia('(prefers-reduced-motion: reduce)').matches;

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
  scrollBehavior(to, _from, savedPosition) {
    if (to.hash) {
      return {
        el: to.hash,
        behavior: prefersReducedMotion() ? 'auto' : 'smooth',
      };
    }
    if (savedPosition) return savedPosition;
    return { top: 0 };
  },
});

router.afterEach((to) => {
  applyPageSeo(typeof to.name === 'string' ? to.name : 'home');
});

export default router;
