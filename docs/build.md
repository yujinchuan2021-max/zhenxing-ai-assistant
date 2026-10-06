# 构建与验证

本仓库的当前客户端位于 `TubaWinUi3.WinUI3/`，采用 WinUI 3 / .NET 10。下列命令在 **Windows x64** 上从仓库根目录执行。源码构建、完整便携包组装与云端服务部署是三个独立步骤。

当前客户端源码为 **0.1.1 的 2026-10-07 下载可靠性修订**，公开版本保持 `0.1.1`，程序和更新清单内部版本为 `0.1.1.1`。本轮构建、公开附件与云端部署分别核对，见[下载修订说明](releases/0.1.1-revision-20261007-downloads.md)。原 `v0.1.1-preview` 标签与历史包保留，核对构建时同时查看源码提交、文件名和包内 `BUILD-INFO.json`；仅增加云端工具文件和网页不需要再修改客户端版本。

## 开发环境

- Windows 10 2004（build 19041）或更新版本；开发和界面验证建议使用 Windows 11。
- .NET SDK **10.0.401**：当前主客户端使用的工具链版本。
- Windows SDK **10.0.26100**。项目目标为 `net10.0-windows10.0.26100.0`，NuGet 引用的 SDK BuildTools 版本见主项目文件。
- PowerShell、可访问 NuGet 的网络环境；打包和独立服务需要 Python。
- WebView2 Evergreen Runtime：资讯、社区及部分内置网页功能需要。
- 原生启动器单独编译时需要 Zig；直接编译主客户端无需先编译启动器。

建议先运行 `dotnet --info` 确认实际使用的 SDK。`TubaWinUi3.Compatible/` 是另一套 .NET Framework 4.8 兼容实现，不作为当前 0.1.1 WinUI 客户端的主构建目标。

## 轻量源码构建

公开源码不包含完整第三方工具二进制集合，构建时明确排除它们：

```powershell
dotnet restore .\TubaWinUi3.WinUI3\TubaWinUi3.csproj -r win-x64 -p:Platform=x64 -p:ExcludeToolsFromPublish=true
dotnet build .\TubaWinUi3.WinUI3\TubaWinUi3.csproj -c Release -r win-x64 -p:Platform=x64 -p:ExcludeToolsFromPublish=true --no-restore
```

此构建可以检查主客户端代码和 XAML，但不等于完整发行包。排除第三方工具时，普通 `build` 不执行开发后端的复制目标；需要主动拦截等后端能力或便携分发时，使用下方 `publish`。

启动主客户端会根据运行模式请求管理员权限，并可能产生正常的功能初始化和网络行为。不要把启动主程序当作无副作用的自动化测试。

## 逻辑测试与界面验证

```powershell
dotnet restore .\TubaWinUi3.Tests\TubaWinUi3.Tests.csproj -p:Platform=x64 -p:ExcludeToolsFromPublish=true
dotnet test .\TubaWinUi3.Tests\TubaWinUi3.Tests.csproj -c Release -p:Platform=x64 -p:ExcludeToolsFromPublish=true --no-restore --filter "FullyQualifiedName~CommunityAppearanceTests|FullyQualifiedName~CommunitySiteTests|FullyQualifiedName~CommunityAuthenticationTests"
```

上述筛选对应历史 r10 的社区相关逻辑验证，并非本次 2026-10-06 修订或全项目测试结果。其他测试应按变更范围选择；不要用真实账号、真实 API Key 或真实安装卸载来代替隔离验证。

历史 r10 在 2026 年 10 月 5 日从当时整理的公开源码目录重新还原依赖并构建了主客户端、后端和测试项目，以上 207 项社区相关测试全部通过（0 失败、0 跳过）。这是 r10 的历史结果；构建仍有现有空值和分析器警告，不代表本次 2026-10-06 修订的全部验证结果。

2026-10-05 首次发布 0.1.1 时，102 项版本身份、更新通道、更新检查和错误报告相关逻辑测试全部通过（0 失败、0 跳过），对应筛选命令为：

```powershell
dotnet test .\TubaWinUi3.Tests\TubaWinUi3.Tests.csproj -c Release -p:Platform=x64 -p:ExcludeToolsFromPublish=true --no-restore --filter "FullyQualifiedName~DistributionIdentityTests|FullyQualifiedName~UpdateServiceTests|FullyQualifiedName~PreviewUpdateChannelTests|FullyQualifiedName~ErrorReportTests"
```

2026-10-05 原包当时还完成了 8 项离线运行时检查和独立审包；公开范围与结果见 [0.1.1 验证记录](releases/0.1.1-validation.json) 和 [首次发行说明](releases/0.1.1-preview.md)。这些历史检查不代表本次 2026-10-06 修订、全项目测试、所有用户流程、真实账号登录或完整原生环境均已验收。

2026-10-06 修订的配置修改器与型号目录按以下范围运行测试，版本身份测试应使用本次生成的主程序集：

```powershell
dotnet test .\TubaWinUi3.Tests\TubaWinUi3.Tests.csproj -c Release -p:Platform=x64 -p:ExcludeToolsFromPublish=true --no-restore --filter "FullyQualifiedName~HardwareDisplayEditorTests|FullyQualifiedName~HardwareModelCatalogTests|FullyQualifiedName~HardwareSpooferServiceTests|FullyQualifiedName~PreviewUpdateChannelTests|FullyQualifiedName~DistributionIdentityTests"
```

本次筛选的 98 项逻辑测试全部通过（0 失败、0 跳过），包括名称服务 23 项、型号目录 11 项、旧服务只读检查 3 项及版本与更新身份 61 项。名称服务的测试注入模拟硬件后端，并使用独立临时数据目录，不对真实注册表或 PnP 设备执行写入。

本次还完成配置修改器 8 项原生检查（浅色、深色各 4 项，0 失败，两个进程退出码均为 `0`）及 8 项离线运行时检查。原生用例使用编译后的共享页面和合成设备，不包含真实硬件写入、管理员启动或无关页面验收；版本标签回到 `0.1.1` 后页面功能代码未变。检查范围和结果见 [本次修订验证记录](releases/0.1.1-revision-20261006-validation.json) 与 [本次修订说明](releases/0.1.1-revision-20261006.md)。

`TubaWinUi3.XamlHostRunner/` 是单独的原生 XAML/WebView2 验证宿主，可运行指定用例：

```powershell
dotnet restore .\TubaWinUi3.XamlHostRunner\TubaWinUi3.XamlHostRunner.csproj -r win-x64 -p:Platform=x64 -p:ExcludeToolsFromPublish=true
dotnet build .\TubaWinUi3.XamlHostRunner\TubaWinUi3.XamlHostRunner.csproj -c Release -r win-x64 -p:Platform=x64 -p:ExcludeToolsFromPublish=true --no-restore
```

用例名称与执行参数见宿主的 `Program.cs` 和对应 `*Cases.cs`。设置独立的 `ZXAI_DATA_ROOT` 与临时目录后再执行，避免触碰使用中的客户端数据。社区授权用例拦截网页请求，在内存里提供合成页面、表单和 Cookie，不执行真实账号授权。

历史 r10 的两个原生社区授权功能用例通过；验证宿主退出时，当前验证机的微信输入法 `wetype_tip.dll` 仍触发 `0xc0000409`。应同时记录功能断言和进程退出结果，不能把断言通过当成完整原生验收通过。

## 自包含发布

```powershell
dotnet publish .\TubaWinUi3.WinUI3\TubaWinUi3.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:ExcludeToolsFromPublish=true -o .\artifacts\publish-x64
```

主项目的发布目标会单独对 `TubaWinUI3.BackEnd/` 执行 `Restore;Publish`，把托管自包含后端放入 `backend/`，避免与主应用的 WindowsDesktop 运行库混在一起。它不依赖预先存在的 `obj/project.assets.json`。

项目的 `CompleteAppPublishLayout` 目标补齐 PRI、编译后的 XAML 和依赖组件载荷。分发时保留发布目录整体；只复制 EXE 或 DLL 会缺失界面资源及依赖。自包含 .NET 发布不等于捆绑 WebView2 Evergreen Runtime。

## 私有 Node / dsh 运行时

AI 引擎与 .NET 客户端分开。需要复现 0.1.1 随包的 dsh 引擎时，先准备合法取得的 **Node v22.23.2** 与 **`@deepseek-ai/dsh` 0.1.5-rc.2**。dsh 目录需要包含 `lib/bin.js`、`package.json` 和安装完整的运行依赖，不是只有源码文件。

下面的输入路径是示例，替换成自己准备的专用目录：

```powershell
powershell -NoProfile -File .\scripts\prepare-dsh-runtime.ps1 -NodeExe C:\runtime-input\node.exe -DshSource C:\runtime-input\dsh -RuntimeDir .\TubaWinUi3.WinUI3\runtime
New-Item -ItemType Directory -Path .\artifacts -Force | Out-Null
python .\scripts\verify-dsh-runtime.py .\TubaWinUi3.WinUI3\runtime --report .\artifacts\runtime-smoke.json
```

组装脚本不下载 Node，缺少输入或版本不匹配时会失败；版本改变需要重新验证。不要使用个人环境、已有会话目录或包含凭证的目录作为运行时来源。运行时存在时，主项目会把它纳入输出；随后重新执行发布。公开 Git 源码不包含 `runtime/`、`node_modules/` 或第三方工具库。

## 启动器与发行目录

直接运行发布目录中的 `TubaWinUi3.exe` 可用于开发验证。面向用户的 0.1.1 便携包另有根目录启动器，布局为：

```text
枕星图吧AI助手.exe
License.txt
PrivacyPolicy.txt
PrivacyPolicy.html
src/
  TubaWinUi3.exe
  TubaWinUi3.pri
  backend/
  runtime/
  ...其他发布资源与依赖
```

启动器源码位于 `Launcher/`，可单独编译：

```powershell
.\Launcher\build.ps1 -Arch x64 -ZigPath C:\toolchains\zig\zig.exe
```

输出文件名由该脚本指定，组装便携包时再命名为 `枕星图吧AI助手.exe`。不要把启动器和 `src/` 分开。

`scripts/make-staging.py`、`scripts/assemble-cloud-preview.py` 包含维护者发行目录及证据管理约定，并非任意检出后即可运行的通用打包入口。调用前审阅路径限制、显式指定 SDK/Node/dsh 输入，并校验实际发布资源、内容锁和离线运行时结果。不要使用脚本默认的个人环境路径，也不要仅凭旧清单声称重新构建验证通过。

## 独立服务与官网

这些模块不会随着 `dotnet build` 自动部署：

| 目录 | 环境与说明 |
| --- | --- |
| `zxai-ai-news/` | Python 3.10+，资讯采集、可选服务器编辑和门户；默认北京时间 09:00 日更，部署和配置见模块 README。 |
| `zxai-toolflow-server/` | Python 3.11+，工具流、技能修订、跑分和工具目录相关接口；权限与公网路由需独立配置，见模块 README。 |
| `website-winui3/` | Vue / Vite 官网；按 `package.json` 的 Node 环境约束，执行 `npm ci`、`npm run build`。 |
| `file-transfer-web/`、`cloudflare-worker/` | 文件传输网页与信令服务；分别使用其前端和 Worker 工具链。 |
| `benchmark-worker/`、`rating-worker/` | 独立 Worker 模块，按各自配置进行开发或部署。 |

仓库里的服务源码不包含生产数据库、登录配置、服务器凭证或私人部署材料；下载源码不会自动获得一套已部署后台。
