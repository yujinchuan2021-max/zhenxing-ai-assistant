<template>
  <footer class="zx-footer">
    <div class="zx-container">
      <div class="zx-footer__grid">
        <div>
          <div class="zx-footer__brand">
            <ZxStar />
            <span>{{ siteConfig.brand.name }}</span>
            <span class="zx-brand__ver">{{ siteConfig.brand.version }}</span>
          </div>
          <p class="zx-small" style="margin-top: 12px; max-width: 34em">
            {{ brand().tagline }}。<br />
            {{ t('Windows 目标工作台与工具箱，基于上游开源项目「', 'A Windows goal workbench and toolbox, derived from the upstream project “') }}{{ siteConfig.upstream.name }}{{ t('」持续开发。', '”.') }}
          </p>
          <p style="margin-top: 14px">
            <span class="zx-badge"><span class="zx-dot zx-dot--idle"></span>{{ status().label }}</span>
          </p>
        </div>

        <div class="zx-footer__col">
          <h4>{{ t('产品', 'Product') }}</h4>
          <ul>
            <li><RouterLink to="/">{{ t('首页', 'Home') }}</RouterLink></li>
            <li><RouterLink to="/download">{{ t('下载 r10 预览版', 'Download r10 preview') }}</RouterLink></li>
            <li><RouterLink to="/docs">{{ t('文档', 'Docs') }}</RouterLink></li>
            <li><a href="/ai-news/">{{ t('枕星AI资讯', 'AI News') }}</a></li>
            <li><RouterLink to="/community">{{ t('社区', 'Community') }}</RouterLink></li>
          </ul>
        </div>

        <div class="zx-footer__col">
          <h4>{{ t('项目', 'Project') }}</h4>
          <ul>
            <li><RouterLink to="/about">{{ t('关于本项目', 'About this project') }}</RouterLink></li>
            <li><a :href="siteConfig.links.repo ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('枕星图吧AI助手 GitHub', '枕星图吧AI助手 on GitHub') }}</a></li>
            <li><a :href="siteConfig.links.releaseNotes ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('r10 更新说明', 'r10 release notes') }}</a></li>
            <li><a :href="siteConfig.links.issues ?? undefined" target="_blank" rel="noopener noreferrer">{{ t('反馈问题', 'Report an issue') }}</a></li>
            <li>
              <a :href="siteConfig.upstream.repo" target="_blank" rel="noopener noreferrer">
                {{ t('上游仓库（', 'Upstream repository (') }}{{ siteConfig.upstream.name }}{{ t('）', ')') }}
              </a>
            </li>
            <li>
              <a :href="siteConfig.upstream.site" target="_blank" rel="noopener noreferrer">
                {{ t('上游官网（非本站）', 'Upstream website (not this site)') }}
              </a>
            </li>
            <li>
              <a :href="siteConfig.upstream.licenseUrl" target="_blank" rel="noopener noreferrer">
                {{ t('上游许可：', 'Upstream license: ') }}{{ siteConfig.upstream.license }}
              </a>
            </li>
          </ul>
        </div>

        <div class="zx-footer__col">
          <h4>{{ t('本站', 'This site') }}</h4>
          <ul>
            <li><span>{{ t('无广告位、无会员收费设计', 'No ad slots, no paid-membership design') }}</span></li>
            <li>
              <span v-if="analyticsConfigured()">{{ t('统计：自托管 Umami（无 Cookie），只统计访问与下载按钮点击', 'Analytics: self-hosted Umami (no cookies), counting only page views and download-button clicks') }}</span>
              <span v-else>{{ t('统计未配置：当前不加载任何统计脚本', 'Analytics not configured: no analytics script is loaded') }}</span>
            </li>
            <li><span>{{ t('主题跟随系统，可手动切换', 'Theme follows your system and can be switched manually') }}</span></li>
            <li><span>{{ t('字体：更纱黑体 / Noto Sans SC / HarmonyOS Sans SC，顶栏可切换', 'Fonts: Sarasa UI SC / Noto Sans SC / HarmonyOS Sans SC, switchable in the header') }}</span></li>
            <li><span>{{ t('界面语言：中文 / English，顶栏可切换', 'Interface language: 中文 / English, switchable in the header') }}</span></li>
          </ul>
        </div>
      </div>

      <div class="zx-footer__note">
        <p>
          © 2026 {{ siteConfig.brand.name }} · {{ t(`本站是${siteConfig.brand.name}（上游「${siteConfig.upstream.name}」的衍生改造）的项目介绍站点，`, `This site presents ${siteConfig.brand.name}, a derivative of the upstream project “${siteConfig.upstream.name}”, and `) }}<strong>{{ t('并非上游官方站点', 'is not the upstream official site') }}</strong>{{ t('；上游项目与其作者、贡献者保留各自权利。', '; the upstream project, its author and contributors keep their respective rights.') }}
        </p>
        <p>
          {{ t('上游项目「', 'The upstream project “') }}{{ siteConfig.upstream.name }}{{ t('」由 ', '” is maintained by ') }}{{ upstreamAuthors() }}{{ t(' 维护，以 ', ' and open-sourced under ') }}{{ siteConfig.upstream.license }}{{ t(' 许可开源；', ';') }}
          <template v-if="ourLinks.length">
            {{ t('本项目的正式入口：', 'this project’s official entries: ') }}<template v-for="(item, index) in ourLinks" :key="item.label"
              ><span v-if="index"> · </span
              ><a
                :href="item.href"
                target="_blank"
                rel="noopener noreferrer"
                @click="item.download && trackDownloadClick('footer-entry')"
                >{{ item.label }}</a
              ></template
            >{{ t('。版本、启动方法和校验值见 ', '. Version, launch instructions and checksums are on the ') }}<RouterLink to="/download">{{ t('下载页', 'download page') }}</RouterLink>{{ t('。', '.') }}
          </template>
          <template v-else>
            {{ t('本项目的下载、仓库与社区入口尚未配置，知悉状态请见', 'This project’s download, repository and community entries are not configured yet; for the current status see the') }}
            <RouterLink to="/download">{{ t('下载状态', 'release status') }}</RouterLink>{{ t(' 页。', ' page.') }}
          </template>
        </p>
      </div>
    </div>
  </footer>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import ZxStar from './ZxStar.vue';
import { brandCopy, isConfigured, releaseState, siteConfig, upstreamAuthors } from '../site.config';
import { analyticsConfigured, trackDownloadClick } from '../analytics';
import { t } from '../i18n';

// 发布状态与品牌文案都随语言切换：用 computed 读取，切换后页脚自动重渲染
const status = () => releaseState();
const brand = () => brandCopy();

const rawLinks = computed<{ href: string | null; label: string; download?: boolean }[]>(() => [
  { href: status().released ? siteConfig.links.download : null, label: t('下载 x64 便携版', 'Download the x64 portable build'), download: true },
  { href: siteConfig.links.releaseNotes, label: t('更新说明', 'Release notes') },
  { href: siteConfig.links.repo, label: t('代码仓库', 'Code repository') },
  { href: siteConfig.links.docs, label: t('文档', 'Docs') },
  { href: siteConfig.links.community, label: t('社区', 'Community') },
]);

const ourLinks = computed(() =>
  rawLinks.value.filter((item): item is { href: string; label: string; download?: boolean } => isConfigured(item.href)),
);
</script>
