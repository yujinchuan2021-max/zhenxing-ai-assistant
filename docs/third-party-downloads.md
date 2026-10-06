# 第三方工具下载、许可与源码

枕星下载目录使用固定版本和校验值。厂商原始 EXE 保持原样，下载后检查大小、SHA-256 和实际启动器架构；安装器启动与软件安装完成分别显示。程序、驱动、账号和订阅的实际要求以相应产品为准。

## 官网安装器来源

| 工具与版本 | 上游来源 | 许可及配套源码 |
| --- | --- | --- |
| UniGetUI 2026.3.0，x64 / ARM64 | [Devolutions 官方发行](https://github.com/Devolutions/UniGetUI/releases/tag/v2026.3.0) | [MIT 许可证](https://zhenxingai.com/downloads/installers/unigetui-license.txt)。原始 x86 安装启动器与产品安装目标架构分开记录。 |
| OptimizerDuck 2.28.1，x64 | [维护者发行](https://github.com/itsfatduck/optimizerDuck/releases/tag/v2.28.1) | [GPL 许可证](https://zhenxingai.com/downloads/installers/optimizer-license.txt)、[第三方通知](https://zhenxingai.com/downloads/installers/optimizer-third-party-notices.txt)、[2.28.1 对应源码 ZIP](https://zhenxingai.com/downloads/installers/optimizer-source-2.28.1.zip)。此文件是单文件工具，启动不表示系统优化已经执行。 |
| PawnIO 2.2.0，x64 | [维护者项目](https://github.com/namazso/PawnIO) | [许可说明](https://zhenxingai.com/downloads/installers/pawnio-license-readme.txt)、[2.2.0 对应源码 ZIP](https://zhenxingai.com/downloads/installers/pawnio-source-2.2.0.zip)。属于驱动安装流程，需要系统授权和实际驱动状态检查。 |
| Sandboxie-Plus 1.18.5，x64 / ARM64 | [官方发行](https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.5) | [Plus 许可](https://zhenxingai.com/downloads/installers/sandboxie-license-plus.txt)、[Classic 许可](https://zhenxingai.com/downloads/installers/sandboxie-license-classic.txt)、[1.18.5 对应源码 ZIP](https://zhenxingai.com/downloads/installers/sandboxie-source-1.18.5.zip)。原始安装文件未修改，付费功能遵循上游许可。 |
| PowerToys 0.101.2362.0，x64 / ARM64 | [微软官方发行](https://github.com/microsoft/PowerToys/releases/tag/v0.101.2362.0)；[枕星下载页](https://zhenxingai.com/tools/download/tool-powertoys) | [MIT 许可](https://zhenxingai.com/downloads/installers/powertoys/0.101.2362.0/powertoys-license.txt)、[第三方告知](https://zhenxingai.com/downloads/installers/powertoys/0.101.2362.0/powertoys-third-party-notices.md)、[同版本源码](https://zhenxingai.com/downloads/installers/powertoys/0.101.2362.0/powertoys-source-0.101.2362.0.zip)、[UTF.Unknown 2.6.0 对应源码](https://zhenxingai.com/downloads/installers/powertoys/0.101.2362.0/utf-unknown-source-2.6.0.zip)和[源码获取说明](https://zhenxingai.com/downloads/installers/powertoys/0.101.2362.0/powertoys-source-availability.txt)。原厂微软签名保留；缺少 WebView2 时安装器仍可能联网取得依赖。 |

客户端获取安装文件及上表配套资料时，可以直接访问官网；上游链接供核对出处。安装器目录单独维护，不将这些 EXE 当作可直接解压部署的便携 ZIP。

## 已上线的新增便携工具

| 工具 | 版本与随包资料 | 使用方式 |
| --- | --- | --- |
| Display Driver Uninstaller（DDU） | 18.1.5.7，保留 MIT 许可证及厂商原始主程序名。目录明确显示该版本，不冒充其他新版。 | 下载只准备软件；驱动清理需在工具中选择并执行。 |
| y-cruncher | 0.8.7.9547b，原厂完整 ZIP，保留 Read Me、Libraries.txt 与 CPU 后端。 | CLI 工具，在控制台中使用。 |
| LibreHardwareMonitor | 0.9.6，保留 MPL 与完整第三方通知，随包附对应 LibreHardwareMonitor 及 PawnIO.Modules 源码归档。 | 需要可用的 .NET Framework；部分传感器另需管理员权限及 PawnIO。 |
| Victoria | 5.37，保持原厂完整 ZIP 及所有随包文件，保留 [HDD.by 原作者官网](https://hdd.by)归属与支持链接。 | 真实入口为 x86，作者支持 Windows x86/x64，ARM64 未验证。客户端可以下载校验并解压；驱动不自动安装。 |

没有可靠下载文件、没有确定产品身份或需要原厂专门获取流程的条目，不显示一键安装成功。工具来源与上游许可是分别核对的事项；当前目录没有声明独立备用存储或大陆 CDN。
