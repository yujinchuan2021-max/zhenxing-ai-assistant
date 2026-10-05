# 贡献指南

欢迎为枕星图吧AI助手提供问题反馈、文档或代码。当前公开仓库是 [yujinchuan2021-max/zhenxing-ai-assistant](https://github.com/yujinchuan2021-max/zhenxing-ai-assistant)，当前版本为 WinUI 3 / .NET 10 预览版。本版基于 [图吧工具箱 CE](https://github.com/luolangaga/tubatools) 独立开发，本版特有问题和补丁请提交到本仓库。

## 反馈问题

先查看 [Issues](https://github.com/yujinchuan2021-max/zhenxing-ai-assistant/issues) 是否已有相同问题，再使用 [Bug 反馈模板](.github/ISSUE_TEMPLATE/bug_report.yml)。请提供版本、系统、最后一次操作、复现步骤、实际结果与预期结果；硬件或驱动问题再补充相应环境。

截图和日志请脱敏。不要公开 API Key、密码、访问令牌、完整授权链接、用户会话或私人文件。安全问题按 [SECURITY.md](SECURITY.md) 处理，不在公开问题中发布利用细节。

## 代码与构建

当前主客户端是 `TubaWinUi3.WinUI3/TubaWinUi3.csproj`，不要用仓库根目录的泛用构建命令代替指定项目的构建。开发环境、显式依赖恢复、轻量构建、测试及发布见 [docs/build.md](docs/build.md)。项目目标为 `net10.0-windows10.0.26100.0`，主客户端最低平台声明为 Windows build 19041；本次预览包交付 Windows x64。

| 目录 | 用途 |
| --- | --- |
| `TubaWinUi3.WinUI3/` | WinUI 主客户端、资源与元数据。 |
| `TubaWinUI3.BackEnd/` | 独立的系统功能后端；发布时恢复依赖并组装。 |
| `TubaWinUi3.Tests/` | 逻辑测试及测试资产。 |
| `TubaWinUi3.XamlHostRunner/` | 独立原生 XAML / WebView2 验证宿主。 |
| `TubaWinUi3.Compatible/` | .NET Framework 4.8 兼容实现，独立工具链。 |
| `website-winui3/` | 官网前端源码，使用独立的前端构建流程。 |
| `zxai-ai-news/`、`zxai-toolflow-server/` | 独立 Python 服务；模块源码不等于生产部署配置。 |

改动应落在实际编译的项目中。遵循相邻代码的约定，保留原有署名，不借局部改动重构无关模块。界面已有中文与英文资源，新增或修改文案时检查 `Strings/` 及对应本地化路径，不再假设应用只有硬编码中文。涉及主题的改动应检查浅色、深色、悬停、选中、禁用及弹出层。

## 验证与数据隔离

根据改动选择必要的构建和测试，并在 PR 中写清实际执行的命令、通过或失败结果、未验证范围。单次编译通过不能代替发行包、原生进程退出或真实账号流程验收。

- 普通验证使用合成数据与隔离目录，避免执行真实安装、卸载、账户授权或付费模型调用。
- `ZXAI_DATA_ROOT` 应设置为独立的绝对路径，不能指向日常客户端数据。原生宿主与相关测试的前置构建和运行方式见构建文档。
- dsh 假服务测试使用 `TestAssets/fake-dsh.cjs`；需要 Node 的用例使用明确的测试输入。不要为测试提交个人运行环境或真实模型密钥。
- 真实 dsh 集成用例为显式选择的单独范围，不作为普通 PR 的默认要求。
- 原生宿主的功能断言和进程退出结果都应保留。不要用跳过、吞异常或强制退出来隐藏失败。当前 r10 验证环境的退出限制见 [更新说明](docs/releases/r10.md)。

## 补充工具、技能和第三方资源

新增来源时，请提供原始项目地址、版本或提交、用途、许可文件与必要署名。需随发行包分发的第三方二进制，还应说明对应版本的再分发依据、下载来源、实际字节数和 SHA-256。不要将“GitHub 上能下载”作为可随包再分发的依据。

外部技能内容也需要保留来源和许可。不要把陌生来源的指令、脚本或示例密钥作为可信执行配置直接启用。工具或技能目录变更应说明客户端显示、下载、安装及更新行为的影响。

## 提交 Pull Request

从本仓库创建分支或 Fork，向本仓库提交 PR，说明解决的问题、最终行为及验证范围。使用 [PR 模板](.github/pull_request_template.md)。提交信息可用中文，建议使用 `fix:`、`feat:`、`docs:`、`test:` 等前缀。

请勿提交 `bin/`、`obj/`、`node_modules/`、`runtime/`、完整第三方工具库、个人配置、数据库、私密日志、签名材料或凭证。API Key 使用 Windows 当前用户的数据保护机制保存，但加密配置仍属于私有数据，不应进入 Git。

当前源码使用根目录 [GPL-3.0](LICENSE)。贡献当前项目代码时，请明确你有权按本项目许可提供这些内容；引入第三方代码或素材时保留其来源、许可证及声明。历史版本和独立第三方资源的许可边界见 [NOTICE](NOTICE)。
