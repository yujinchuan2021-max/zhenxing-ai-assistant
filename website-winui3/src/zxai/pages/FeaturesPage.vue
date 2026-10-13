<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">{{ t('功能与使用场景', 'Features and use cases') }}</p>
      <h1 class="zx-h1">{{ t('每项能力，都有清楚的用途', 'A clear purpose for every capability') }}</h1>
      <p>{{ t('从 AI 目标工作台到日常电脑工具，按你要做的事情查看功能、入口和适用条件。这里介绍 0.1.1 公开预览版；完整工具目录以客户端同步结果为准。', 'Explore features, entry points and requirements by the task you want to do, from AI workflows to everyday computer tools. This page describes the 0.1.1 public preview; the synced client catalogue is the reference for available tools.') }}</p>
      <div class="zx-btn-row" style="margin-top: 24px"><RouterLink class="zx-btn zx-btn--primary" to="/download"><ZxIcon name="download" />{{ t('下载预览版', 'Download preview') }}</RouterLink><RouterLink class="zx-btn" to="/docs">{{ t('阅读使用指南', 'Read the guide') }}</RouterLink></div>
    </header>
    <nav class="zx-section-nav" :aria-label="t('功能目录', 'Feature contents')"><a v-for="group in featureGroups" :key="group.id" :href="`#${group.id}`">{{ group.title }}</a><a href="#tools">{{ t('工具逐项说明', 'Individual tool guide') }}</a></nav>
    <div class="zx-page__body">
      <div class="zx-feature-grid">
        <article v-for="group in featureGroups" :id="group.id" :key="group.id" class="zx-card zx-capability">
          <div class="zx-capability__head"><div class="zx-card__icon"><ZxIcon :name="group.icon" /></div><span class="zx-capability__label">{{ group.title }}</span></div>
          <h2 class="zx-h3">{{ group.outcome }}</h2><p>{{ group.body }}</p>
          <ul class="zx-list zx-list--dense"><li v-for="item in group.items" :key="item">{{ item }}</li></ul>
          <p class="zx-capability__condition"><strong>{{ t('使用条件', 'Requirements') }}</strong>{{ group.condition }}</p>
          <a v-if="group.external" :href="group.href" class="zx-capability__link">{{ t('打开入口', 'Open') }} →</a><RouterLink v-else :to="group.href" class="zx-capability__link">{{ t('查看使用说明', 'Read instructions') }} →</RouterLink>
        </article>
      </div>
      <section id="tools">
        <h2 class="zx-h2">{{ t('客户端工具，逐项了解', 'Explore individual client tools') }}</h2>
        <p class="zx-lead" style="margin-top: 16px">{{ t('按分类展开用途说明。以下是客户端提供的工具入口，包含原生页面、外部组件与资料查询；“有入口”不代表已安装所有组件或支持所有设备。', 'Expand a category for individual purposes. These client entries include native pages, external components and reference lookups. An entry does not mean all components are installed or all devices are supported.') }}</p>
        <div class="zx-tool-map"><details v-for="group in toolGroups" :key="group.id" class="zx-tool-group"><summary><span>{{ group.title }}</span><span class="zx-small">{{ group.rows.length }} {{ t('项说明', 'entries') }}</span></summary><p>{{ group.note }}</p><div class="zx-tool-rows"><div v-for="row in group.rows" :key="row.name" class="zx-tool-row"><h3>{{ row.name }}</h3><p>{{ row.purpose }}</p></div></div></details></div>
      </section>
      <section class="zx-grid-2">
        <article class="zx-card"><h2 class="zx-h3">{{ t('第三方工具在哪里获取？', 'Where do I acquire third-party tools?') }}</h2><p>{{ t('客户端工具库按需提供获取入口。官网另外列出已发布原厂文件与其他获取流程，不把尚未安装的条目计入本机软件。', 'The client tool library offers acquisition on demand. The site separately lists published vendor files and other acquisition steps; uninstalled entries do not count as local software.') }}</p><div class="zx-linkrow" style="margin-top: 16px"><RouterLink to="/tools/download">{{ t('原厂文件下载', 'Vendor downloads') }} →</RouterLink><RouterLink to="/tools/acquire">{{ t('其他获取方式', 'Other methods') }} →</RouterLink></div></article>
        <article class="zx-card"><h2 class="zx-h3">{{ t('怎样判断是否已经完成？', 'How do I know a task is complete?') }}</h2><p>{{ t('看清下载、安装、入口、账号与实际结果各自的状态。工具准备完成后，在所选工具中进行首次运行与效果确认；公开预览的验证范围见发行记录。', 'Check the separate states of download, installation, launch entry, account and actual result. After preparation, perform a first run in the chosen tool and verify the output. See the release record for preview validation scope.') }}</p><a :href="siteConfig.links.releaseNotes ?? undefined" target="_blank" rel="noopener noreferrer" class="zx-capability__link">{{ t('0.1.1 更新与验证范围', '0.1.1 changes and validation scope') }} →</a></article>
      </section>
    </div>
  </div>
</template>
<script setup lang="ts">
import ZxIcon from '../components/ZxIcon.vue';
import { featureGroups, toolGroups } from '../features';
import { siteConfig } from '../site.config';
import { t } from '../i18n';
</script>
