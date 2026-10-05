# 枕星图吧AI助手官网

本模块提供 [枕星图吧AI助手官网](https://zhenxingai.com/) 的页面实现。当前产品为 **0.1.1 公开预览版**：面向 Windows 的 AI 助手与电脑工具工作台，以“明确目标 → 选择方案 → 准备工具 → 开始使用”组织介绍、下载和使用指南。

[当前客户端下载](https://github.com/yujinchuan2021-max/zhenxing-ai-assistant/releases/tag/v0.1.1-preview) · [公开源码](https://github.com/yujinchuan2021-max/zhenxing-ai-assistant) · [新版使用指南](../docs/getting-started.md) · [0.1.1 更新与验证范围](../docs/releases/0.1.1-preview.md)

当前产品站入口是 `src/main.ts` → `src/zxai/App.vue`；`src/site/` 与上游文档保留作来源参考，与枕星当前首页入口不同。界面基于 Vue 3 与 WinUIonWeb，支持中文、English、浅色、深色与跟随系统。

## 本地构建

安装 `package.json` 要求的 Node.js 和依赖后，在本目录执行：

```powershell
npm ci
npm run build
```

构建包含 TypeScript 检查与 Vite 打包，产物为 `dist/`。构建通过之后仍需检查页面、路由、浅深色主题、下载链接与线上内容。README 的更新本身不证明网页已部署。

## 对外内容来源

- `src/zxai/site.config.ts`：域名、正式入口、发布状态、下载校验信息与数据说明。
- `src/zxai/progress.ts`：目标工具流能力与验证边界；“预览版已接入”不等于公开发布或用户目标已完成。
- `src/zxai/pages/`：首页四步示意、新手指南、下载状态、社区和关于。
- `index.html`：无 JavaScript 的介绍与静态元信息，应与页面文案同步。

对外版本统一为 **0.1.1 公开预览版**，公开下载链接、大小和 SHA-256 以 GitHub 发行资产为准。旧 Electron 标签只用于历史追溯，不能当成当前客户端或其更新通道。技能数量以实时目录为准，不写固定营销数字；可加载、审核通过和 GitHub 热度都不等于实测有效。

“工具已准备”不等于用户目标已经完成，外部 Agent 仍需自己的账号或兼容接入。社区授权的逻辑和隔离功能测试已通过，真实账号注册与登录未验证，原生验证宿主退出异常须继续如实说明。

## 服务分工与发布

官网由 Nginx 提供静态文件，历史部署使用 `/var/www/zxai/releases/<版本>`，校验后原子切换 `/var/www/zxai/current`，旧目录保留供回滚。History 路由需要 SPA fallback。

`/ai-news/` 是独立自有资讯服务的门户，不应被官网发布覆盖。服务默认每天北京时间 09:00 自动采集，失败保留有效缓存；无需客户端或 Codex 常开。`/api/ai-news/`、`/api/toolflows/`、`/downloads/` 同样属于既有服务路由，本次官网文案发布不改服务、数据库、模型环境或下载通道。

社区是 [枕星AI社区](https://community.zhenxingai.com/) 的自托管 Discourse，账号和授权由社区处理。客户端浏览器、官网、资讯服务和社区各有自己的验证范围，修改一处文案不代表其他服务配置也已更新。

## 来源与署名

本项目是图吧工具箱CE的衍生改造，保留 GPL-3.0 与来源署名。Web 控件来源于 [WinUIonWeb](https://github.com/Furry-Xiyi/WinUIonWeb)（GPL-3.0），设计参考 [DevToys](https://devtoys.app)（MIT）；并非上游官方站点。

旧浏览器检查脚本中的 `community` 模式曾约定文档入口为空，与当前本站新手指南已不同。复用时应先核对其断言与当前页面是否一致，不能把历史检查记录当成新版线上验收结果。
