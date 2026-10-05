<#
.SYNOPSIS
    遗留 Store 发布入口已停用。
.DESCRIPTION
    上游 CE 的商店账户、证书及目录配置不属于枕星发行。
    枕星在配置自己的 Partner Center 包身份与签名后再启用 MSIX 构建。
#>
throw '此旧 Store 构建入口已停用：枕星尚未配置独立商店身份与签名。请使用便携发行链。'
