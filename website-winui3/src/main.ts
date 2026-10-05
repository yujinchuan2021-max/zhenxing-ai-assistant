import { createApp, watch } from 'vue';
import App from './zxai/App.vue';
import router from './zxai/router';
import { initTheme } from './zxai/theme';
import { initFont } from './zxai/fonts';
import { initLang, lang } from './zxai/i18n';
import { applyPageSeo } from './zxai/seo';
import { initAnalytics } from './zxai/analytics';
import './zxai/styles/zxai.css';

/**
 * 枕星图吧AI助手 官网入口
 * —— 站点实现位于 src/zxai/（上游站点代码保留在 src/site/ 供对照，不参与本构建）。
 */

// 主题先于首帧渲染落地（index.html 内联脚本已设过一次，这里补上监听与主题色 meta）
initTheme();

// 正文字体选择同理：内联脚本已写过一次 <html data-font>，这里补状态与后续切换
initFont();

// 界面语言（V0.1）：内联脚本已写过一次 <html lang>，这里补状态与后续切换
initLang();

// 站点统计（可配置，默认关闭）：未配置脚本地址/网站 ID 时不加载任何统计脚本
initAnalytics();

const app = createApp(App);
app.use(router);
app.mount('#app');

// 语言切换不触发路由，但标题/描述/og:locale/JSON-LD 需要跟着语言重放一次
watch(lang, () => {
  const name = router.currentRoute.value.name;
  applyPageSeo(typeof name === 'string' ? name : 'home');
});
