<#
.SYNOPSIS
    枕星 MSIX/Store 发布入口暂未启用。
.DESCRIPTION
    原脚本绑定上游 CE 的商店身份与签名证书，不能用于枕星发行。
    枕星 V0.1 当前受支持的交付形态仅为 x64 自包含便携 ZIP。
    要支持 Store，先提供枕星在 Partner Center 注册的包身份及对应签名配置，
    再实现独立构建链；原脚本已保存于本地审计备份。
#>
param(
    [string]$Version = '',
    [string[]]$Archs = @('x64','arm64'),
    [switch]$FrameworkDependent,
    [switch]$SkipSign
)
throw '枕星尚未配置独立的 MSIX/Store 包身份和签名；禁止使用上游 CE 身份发布。请使用已验收的便携发行链。'
