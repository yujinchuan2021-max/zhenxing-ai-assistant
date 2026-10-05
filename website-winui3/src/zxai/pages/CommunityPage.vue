<template>
  <div class="zx-page zx-container">
    <header class="zx-page__head">
      <p class="zx-eyebrow">{{ t('一起用好 AI 与工具', 'Make AI and tools useful together') }}</p>
      <h1 class="zx-h1" style="font-size: clamp(30px, 3.6vw, 44px)">{{ t('枕星AI社区', '枕星AI社区') }}</h1>
      <p>
        {{ t(`${siteConfig.brand.name}的社区是「围绕工具与 AI 工作流的交流」：能用、能问、能分享，而不是又一个下载站。`, `The ${siteConfig.brand.name} community is a place to talk about tools and AI workflows: usable, open to questions and to sharing — not another download site.`) }}
      </p>
    </header>

    <div class="zx-page__body">
      <section class="zx-status">
        <span class="zx-status__icon"><ZxIcon name="info" /></span>
        <div>
          <h2>{{ copy().statusTitle }}</h2>
          <p>{{ copy().statusBody }}</p>
        </div>
      </section>

      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ copy().sectionTitle }}</h2>
        <div class="zx-grid-2">
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="chat" /></div>
            <h3>{{ t('讨论与问答', 'Discussion and Q&A') }}</h3>
            <p>{{ t('装机、验机、排障的真实经验：一个现象、一段命令、一个结果，比“玄学”更有用。', 'Real experience with builds, checks and troubleshooting: a symptom, a command, a result — far more useful than folklore.') }}</p>
          </article>
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="sparkle" /></div>
            <h3>{{ t('技能与方案分享', 'Skills and solution sharing') }}</h3>
            <p>{{ t('分享目标、工具组合、适用环境与实际结果。技能要注明引擎、配套文件和验证状态；客户端可先本机试用，再由用户提交后台审核。', 'Share the goal, tool combination, environment and actual result. Skills should state their engine, companion files and validation status; try them locally before choosing to submit for review.') }}</p>
          </article>
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="info" /></div>
            <h3>{{ t('问题反馈与排障', 'Feedback and troubleshooting') }}</h3>
            <p>
              {{ t('客户端问题、工具兼容性、硬件怪毛病，按“现象—环境—复现步骤”整理后发邮件到', 'For client issues, tool compatibility or odd hardware behaviour, write up “symptom — environment — steps to reproduce” and email') }}
              {{ feedbackEmail }}{{ t('，我们会按这个顺序排查。', '; we work through it in that order.') }}
            </p>
          </article>
          <article class="zx-card">
            <div class="zx-card__icon"><ZxIcon name="terminal" /></div>
            <h3>{{ t('贡献代码与工具', 'Contribute code and tools') }}</h3>
            <p>{{ t('当前 WinUI 客户端源码已在 GitHub 公开，欢迎提交 Issue、修复建议与 PR。贡献请说明目标、实际变化和验证结果；工具及技能收录会核对来源、许可和适用条件。', 'The current WinUI client source is public on GitHub. Issues, fixes and pull requests are welcome. Explain the goal, actual change and validation results; tool and skill entries are reviewed for origin, licence and requirements.') }}</p>
          </article>
        </div>

        <p class="zx-note" style="margin-top: 18px">{{ t('第一次来先读欢迎帖与社区指引。当前客户端为 0.1.1 公开预览版；反馈时请写明版本、系统、最后一次操作和实际结果，截图隐藏密码与 Key。', 'Start with the welcome topic and community guidelines. The current client is the 0.1.1 public preview. For feedback, include the version, system, last action and actual result, with passwords and keys hidden in screenshots.') }}</p>
        <p><a href="https://community.zhenxingai.com/t/14" target="_blank" rel="noopener noreferrer">{{ t('欢迎与产品现状 →', 'Welcome and product status →') }}</a> · <a href="https://community.zhenxingai.com/t/15" target="_blank" rel="noopener noreferrer">{{ t('社区指引 →', 'Community guidelines →') }}</a></p>

        <div class="zx-entry-list" style="margin-top: 18px">
          <div class="zx-entry">
            <span class="zx-entry__label">{{ t('公开问题反馈', 'Public issue report') }}</span>
            <ZxLinkOr name="issues" :href="siteConfig.links.issues" :text="t('在 GitHub 提交问题', 'Report an issue on GitHub')" :fallback="t('暂不可用', 'Unavailable')" />
          </div>
          <div class="zx-entry">
            <span class="zx-entry__label">{{ t('社区平台', 'Community platform') }}</span>
            <ZxLinkOr
              name="community"
              :href="siteConfig.links.community"
              :text="t('进入枕星社区', 'Open 枕星AI社区')"
              :fallback="t('Discourse（自托管）· 尚未开通', 'Discourse (self-hosted) · not open yet')"
            />
          </div>
          <div class="zx-entry">
            <span class="zx-entry__label">{{ t('邮件反馈', 'Email feedback') }}</span>
            <ZxLinkOr
              name="feedback"
              :href="siteConfig.links.feedback"
              :text="t('发邮件反馈', 'Send an email')"
              :fallback="t('未配置 · 邮箱待定', 'Not configured · address pending')"
              :external="false"
            />
          </div>
          <div class="zx-entry">
            <span class="zx-entry__label">{{ t('代码仓库', 'Code repository') }}</span>
            <ZxLinkOr
              name="repo"
              :href="siteConfig.links.repo"
              :text="t('打开代码仓库', 'Open the repository')"
              :fallback="t('未配置 · 开源上线后公布', 'Not configured · announced when open-sourced')"
            />
          </div>
        </div>
      </section>

      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('官方 QQ 群', 'Official QQ groups') }}</h2>
        <p><a href="https://qm.qq.com/q/Jj4BxOHP6q" target="_blank" rel="noopener noreferrer">枕星图吧AI助手1群 · 521755240</a></p>
        <p><a href="https://qm.qq.com/q/91tGCq04Y8" target="_blank" rel="noopener noreferrer">枕星图吧AI助手2群 · 263271197</a></p>
        <p class="zx-small">{{ t('群聊用于交流；需要长期留存的复现步骤、教程和反馈请发在论坛。', 'Use groups for discussion; post reproducible issues, tutorials and feedback on the forum for a lasting record.') }}</p>
      </section>

      <section>
        <h2 class="zx-h3" style="margin-bottom: 14px">{{ t('一句话原则', 'The one-line principle') }}</h2>
        <p class="zx-note">
          {{ t('不卖版位、不做付费墙，也没有“社区特权”可买；论坛账号就是 Discourse 自己的账号，注册与数据都在我们自托管的实例里。社区只服务两件事：把工具用好，把经验留下来。', 'No paid slots, no paywall and no “community privileges” for sale; the forum account is just a Discourse account, and both registration and data live on our self-hosted instance. The community serves two things only: using the tools well, and keeping the experience around.') }}
        </p>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import ZxIcon from '../components/ZxIcon.vue';
import ZxLinkOr from '../components/ZxLinkOr.vue';
import { communityCopy, feedbackEmail, siteConfig } from '../site.config';
import { t } from '../i18n';

// 文案随 links.community 是否配置切换（与 SEO 描述共用同一来源，见 site.config.ts）；也随界面语言切换
const copy = () => communityCopy();
</script>
