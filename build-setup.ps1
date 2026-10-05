<#
.SYNOPSIS
    遗留安装包自动构建入口已停用。
.DESCRIPTION
    枕星 V0.1 当前受支持的交付形态是 x64 自包含便携包。
    旧脚本直接 dotnet publish，没有组装私有 Node/dsh 运行时，
    输出目录也与当前 Inno 文件输入不一致，不能用于正式枕星安装包。
    installer*.iss 已设置独立枕星身份，但安装包完整构建和实机验收仍待交付。
    使用 scripts/make-staging.py 与已验收的 candidate 组装链生成便携包。
#>
throw '旧安装包构建入口已停用。当前仅支持已验收的 x64 便携发行链；Inno 安装包构建与验收尚未交付。'