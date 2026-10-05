# 枕星图吧AI助手官网

当前产品站入口是 `src/main.ts` → `src/zxai/App.vue`；`src/site/` 与上游文档仍保留作来源参考，不是枕星当前首页。界面基于 Vue 3 与 WinUIonWeb，支持中文、English、浅色、深色与跟随系统。

## 本地构建

使用已安装依赖执行 `npm run build`，包含 `vue-tsc --build` 与 Vite 构建，产物为 `dist/`。这只证明本地构建成功，不能代替浏览器视觉与线上验收。不要为了验收读取用户浏览器资料或客户端配置。

## 对外内容来源

- `src/zxai/site.config.ts`：域名、正式入口、发布状态、下载校验信息与数据说明。
- `src/zxai/progress.ts`：目标工具流能力与验证边界；“预览版已接入”不等于公开发布或用户目标已完成。
- `src/zxai/pages/`：首页四步示意、新手指南、下载状态、社区和关于。
- `index.html`：无 JavaScript 的介绍与静态元信息，应与页面文案同步。

客户端当前仍为私有预览。只有公开包完成审核，且下载地址、版本、大小、SHA-256 全部真实可用，才允许打开 `status.released`。不得把上游下载、私有测试包或待上传文件当成枕星正式发布。技能数量以实时目录为准，不写固定营销数字；可加载、审核通过和 GitHub 热度都不等于实测有效。

## 服务分工与发布

官网由 Nginx 提供静态文件，历史部署使用 `/var/www/zxai/releases/<版本>`，校验后原子切换 `/var/www/zxai/current`，旧目录保留供回滚。History 路由需要 SPA fallback。

`/ai-news/` 是独立自有资讯服务的门户，不应被官网发布覆盖。服务默认每天北京时间 09:00 自动采集，失败保留有效缓存；无需客户端或 Codex 常开。`/api/ai-news/`、`/api/toolflows/`、`/downloads/` 同样属于既有服务路由，本次官网文案发布不改服务、数据库、模型环境或下载通道。

社区是 `https://community.zhenxingai.com/` 的自托管 Discourse。本轮既有官方主题、默认英文欢迎和 General 分类的更新源与受限备份/回滚脚本在 `../zxai-docs/public-content-2026-10-05/`；编写脚本不代表执行发布。

## 来源与署名

本项目是图吧工具箱CE的衍生改造，保留 GPL-3.0 与来源署名。Web 控件来源于 [WinUIonWeb](https://github.com/Furry-Xiyi/WinUIonWeb)（GPL-3.0），设计参考 [DevToys](https://devtoys.app)（MIT）；并非上游官方站点。

旧浏览器检查脚本中的 `community` 模式曾约定文档入口为空，与当前本站新手指南已不同；本轮不把这些旧断言当作有效验收记录。复用前须先更新合同，并按允许的浏览器工具执行。
