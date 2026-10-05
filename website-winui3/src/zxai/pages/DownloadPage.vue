<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">Download</p>
      <h1 class="zx-h1" style="font-size: clamp(30px, 3.6vw, 44px)">{{ t('下载与发布状态', 'Download and release status') }}</h1>
      <p>
        {{ t('这一页回答一个问题：现在能不能下载 ', 'This page answers one question: can you download ') }}{{ siteConfig.brand.name }}{{ t('？', ' right now?') }}<br />
        {{ t('我们只写真实状态——不会挂出任何未启用的下载按钮。', 'We only state the real status — no download button is ever shown unless it is actually enabled.') }}
      </p>
    </header>

    <div class="zx-page__body">
      <!-- 当前状态（文案由 releaseState() 统一决定：发布开关 + 下载入口一致才算已发布） -->
      <section class="zx-status">
        <span class="zx-status__icon"><ZxIcon name="info" /></span>
        <div>
          <h2>{{ t('当前状态：', 'Status: ') }}{{ status().label }}</h2>
          <p>{{ status().note }}</p>
        </div>
      </section>

      <!-- 入口清单：已配置的入口渲染真链接，未配置的渲染诚实空态（同一套判断，见 ZxLinkOr） -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('本项目的对外入口', 'Official entries of this project') }}</h2>
        <div class="zx-entry-list" style="margin-top: 0">
          <div class="zx-entry">
            <ZxIcon name="download" style="width: 18px; height: 18px; color: var(--zx-text-3)" />
            <span class="zx-entry__label">{{ t('Windows x64 便携版', 'Windows x64 portable build') }}</span>
            <ZxLinkOr
              name="download"
              :href="status().released ? siteConfig.links.download : null"
              :text="t('下载 ZIP 文件', 'Download the ZIP')"
              :fallback="t('尚未开放下载', 'Download not open yet')"
              :external="false"
              @activate="trackDownloadClick('download-page-entry')"
            />
          </div>
          <div v-if="status().released && isConfigured(siteConfig.links.sourceArchive)" class="zx-entry">
            <ZxIcon name="terminal" style="width: 18px; height: 18px; color: var(--zx-text-3)" />
            <span class="zx-entry__label">{{ t('对应版本源码', 'Source code for this version') }}</span>
            <ZxLinkOr
              name="sourceArchive"
              :href="siteConfig.links.sourceArchive"
              :text="t('下载源码 ZIP', 'Download the source ZIP')"
              fallback=""
              :external="false"
            />
          </div>
          <div class="zx-entry">
            <ZxIcon name="users" style="width: 18px; height: 18px; color: var(--zx-text-3)" />
            <span class="zx-entry__label">{{ t('社区入口', 'Community') }}</span>
            <ZxLinkOr
              name="community"
              :href="siteConfig.links.community"
              :text="t('进入枕星社区', 'Open the Zhenxing community')"
              :fallback="t('Discourse 论坛 · 地址已准备，尚未开通', 'Discourse forum · address prepared, not open yet')"
            />
          </div>
        </div>
        <div v-if="status().released" class="zx-kv" style="margin-top: 18px">
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('版本', 'Version') }}</span><span class="zx-kv__val">{{ siteConfig.releaseArtifact.version }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('文件大小', 'File size') }}</span><span class="zx-kv__val">{{ formatSize(siteConfig.releaseArtifact.sizeBytes) }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">SHA-256</span><code class="zx-kv__val" style="overflow-wrap: anywhere">{{ siteConfig.releaseArtifact.sha256 }}</code></div>
        </div>
        <p v-else class="zx-note" style="margin-top: 18px">{{ t('下载文件和校验信息确认后会在这里一起公布。', 'The download file and checksums will be published here together once confirmed.') }}</p>
        <p class="zx-note" style="margin-top: 12px">
          {{ t('统计口径：下载按钮只上报一次「点击」事件（统计是可配置项，未配置时页面不加载任何统计脚本），', 'Counting: the download button reports a single “click” event (analytics is configurable — with nothing configured the page loads no analytics script),') }}
          <strong>{{ t('点击不代表下载量', 'a click is not a download count') }}</strong>{{ t('——真实下载量以服务端日志核对为准。数据说明详见', ' — real download counts are verified from server logs. See the') }}
          <RouterLink to="/about">{{ t('「关于」页的数据与统计', 'data and analytics section on the About page') }}</RouterLink>{{ t('。', '.') }}
        </p>
      </section>

      <!-- 系统要求 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('系统要求（客户端）', 'System requirements (client)') }}</h2>
        <div class="zx-kv">
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('操作系统', 'OS') }}</span><span class="zx-kv__val">{{ facts().minOs }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('架构', 'Architecture') }}</span><span class="zx-kv__val">{{ status().released ? 'x64' : facts().archs }}</span></div>
          <div class="zx-kv__row"><span class="zx-kv__key">{{ t('运行时', 'Runtime') }}</span><span class="zx-kv__val">{{ facts().runtime }}</span></div>
          <div class="zx-kv__row">
            <span class="zx-kv__key">{{ t('AI 模型', 'AI model') }}</span>
            <span class="zx-kv__val">{{ t('在统一 AI 设置选择接口、模型与 Agent，云端服务使用你的账号或 Key；兼容接口与本地模型需实际测试连接，硬件也要适合。不使用 AI 仍可查看资讯、硬件和工具库。', 'Choose the endpoint, model and agent in unified AI settings. Cloud services use your account or key; compatible APIs and local models require connection tests and suitable hardware. News, hardware and the tool library can be viewed without AI.') }}</span>
          </div>
        </div>
      </section>

      <!-- 上游参考 -->
      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('想先试试上游原版？', 'Want to try the upstream original first?') }}</h2>
        <div class="zx-card">
          <p>
            {{ t('「', '“') }}{{ siteConfig.upstream.name }}{{ t(`」是独立的上游开源项目（${siteConfig.upstream.license}），`, `” is a separate upstream open-source project (${siteConfig.upstream.license}),`) }}
            {{ t('本项目是基于它做的衍生改造。上游有自己的官方发布渠道，下面两个链接', ' and this project is a derivative of it. Upstream has its own official release channels; the two links below') }}
            <strong>{{ t('属于上游', 'belong to upstream') }}</strong>{{ t('，', ',') }}
            <strong>{{ t('不是本项目的下载', 'not to this project') }}</strong>{{ t('：', ':') }}
          </p>
          <ul class="zx-list" style="margin-top: 16px">
            <li>
              {{ t('上游官网（含下载）：', 'Upstream website (with downloads): ') }}
              <a :href="siteConfig.upstream.site" target="_blank" rel="noopener noreferrer">tubawinui3.cn</a>
              <span class="zx-small">{{ t('（外部链接）', '(external link)') }}</span>
            </li>
            <li>
              {{ t('上游代码仓库：', 'Upstream code repository: ') }}
              <a :href="siteConfig.upstream.repo" target="_blank" rel="noopener noreferrer">github.com/luolangaga/tubatools</a>
              <span class="zx-small">{{ t('（外部链接）', '(external link)') }}</span>
            </li>
          </ul>
          <p class="zx-small" style="margin-top: 14px">
            {{ t('提醒：上游版本不等于本项目版本；上面的链接属于上游，不是枕星版的下载入口。', 'Note: the upstream build is not this project’s build; the links above belong to upstream, not to the Zhenxing release.') }}
          </p>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import ZxIcon from '../components/ZxIcon.vue';
import ZxLinkOr from '../components/ZxLinkOr.vue';
import { facts, isConfigured, releaseState, siteConfig } from '../site.config';
import { trackDownloadClick } from '../analytics';
import { isEn, t } from '../i18n';

const status = () => releaseState();
const formatSize = (bytes: number | null) =>
  bytes === null ? '' : bytes >= 1024 ** 3
    ? `${(bytes / 1024 ** 3).toFixed(2)} GiB${isEn.value ? ` (${bytes.toLocaleString('en-US')} bytes)` : `（${bytes.toLocaleString('zh-CN')} 字节）`}`
    : `${(bytes / 1024 ** 2).toFixed(1)} MiB${isEn.value ? ` (${bytes.toLocaleString('en-US')} bytes)` : `（${bytes.toLocaleString('zh-CN')} 字节）`}`;
</script>
