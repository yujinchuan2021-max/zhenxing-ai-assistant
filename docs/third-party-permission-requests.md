# 第三方工具原样分发许可询问草稿

状态：仅供项目维护者审核、补齐署名后使用。没有发送邮件、私信或表单，也没有联系任何作者。以下是请求许可的提案，不表示对方已授权。

适用条目：Dism++、FanControl、HiBit Uninstaller、Geek Uninstaller、HDDScan、SeaTools。这些项目的判断不同：FanControl 有明确的事先同意要求；Dism++ 的原样镜像适用条件待确认；其余项目没有确认到适用于本项目的镜像授权。没有把“免费使用”等同于“允许镜像”。

## 共用邮件正文（英文，补入对应工具的特定段落）

Subject: Permission request to host unchanged official [Tool name] downloads for Zhenxing Tuba AI Assistant

Dear [author or publisher],

We maintain 枕星图吧AI助手 (Zhenxing Tuba AI Assistant), an independent Windows assistant that helps users find and prepare software for their tasks. The project source is available at https://github.com/yujinchuan2021-max/zhenxing-ai-assistant and the website is https://zhenxingai.com/.

Some of our users in mainland China cannot reliably reach the current download host. We would like your written permission to host and distribute the exact, unchanged official [Tool name] package through our own website and client. We would keep the publisher’s file intact and verify its size and SHA256 before publication and download. We are asking for permission; we are not treating the tool’s free download or an existing third-party copy as a redistribution licence.

[Insert the tool-specific paragraph below, including the proposed version and original file.]

Our proposed conditions are:

- No modification, repacking, extra advertisements, unrelated software, licence keys or activation bypasses. The original documentation, notices, signatures and licence terms would remain intact.
- Clear identification of you as the author/publisher, with a visible link to your official project and download page. We would describe ourselves as an independent distributor and would not imply endorsement.
- No fee for downloading your package. End-user eligibility, permitted uses and any paid licence requirements would remain governed by your terms. Please tell us if distribution through an independently operated software client requires separate commercial permission.
- User confirmation of your licence and any installer permissions. Downloading a file or opening its installer would not be reported as a successful installation.
- Publication only of official packages within the scope you approve. For updates, we would use an official feed or release channel you permit, preserve the original file and checksums, and verify it before replacing a download.
- Removal or suspension of our copy if you withdraw permission, identify a problem, or ask us to stop distributing a version, subject to any terms you specify.

Could you please confirm whether this proposed distribution is permitted, which versions and package formats it covers, and whether future official releases may be mirrored under the same conditions? Please also specify any required attribution wording, download-page links, permitted use of the product name or icon, geographic restrictions, and update or withdrawal procedure. If you prefer distribution only through your own infrastructure, an approved reliable download URL or integration method would also help.

We will not publish a mirrored package until the applicable permission and conditions are clear. Thank you for reviewing this request.

Kind regards,
[Maintainer’s name]
Maintainer, 枕星图吧AI助手 / Zhenxing Tuba AI Assistant
[Contact details supplied by the maintainer]

## Dism++：补入正文的特定段落

We are asking specifically about an unchanged copy of `Dism++10.1.1002.1B.zip` from your official release tagged `v10.1.1002.2`, as well as later official versions if you approve. The verified original is 3,767,974 bytes with SHA256 `5bbab96d60704854efd8246a7d9371688b9102261544827fc8884126d70bcb3b`. We would preserve all three official x86, x64 and ARM64 entry points, configuration, language resources, and the original user agreement. We would not publish only a detached executable or silently alter the agreement.

We read the Simplified Chinese agreement inside `Config/Languages/zh-Hans.zip`. Section 一/5 includes the qualification “以非法的方式”, and section 四/3 concerns prior consent for displaying, using or otherwise handling the related program. We do not want to assume that these statements either grant mirror rights or prohibit every unchanged original ZIP mirror. Please confirm whether the proposed unchanged distribution is permitted, whether a software-directory entry with the program name and an official link needs separate approval, and how the personal-use restriction applies to end users and to the operator of this mirror.

中文补充说明：请作者确认“原厂原样 ZIP 镜像”的适用许可，不将一/5 的限定语删除，也不预先声称项目取得了许可。中文回函如仅允许某版本、个人用途或特定入口，必须逐项保留这些条件。

官方来源：https://www.chuyu.me/zh-Hans/ 和 https://github.com/Chuyu-Team/Dism-Multi-language/releases/tag/v10.1.1002.2 。

## FanControl：补入正文的特定段落

Your application’s `LICENSE` requires the Licensor’s prior consent for third-party distribution. We are requesting that consent for exact original FanControl `V282` packages, including the official setup and the complete ZIP matching the required .NET version, and for future official releases only if you explicitly include them. We recognise that FanControl itself is proprietary and licensed for personal, non-commercial use; the open-source licence of a hardware-monitoring backend does not replace your application licence.

Please specify whether the setup, portable packages and any included dependencies or plugins may all be mirrored, or whether some must continue to come directly from their respective publishers. We would preserve PawnIO and .NET requirements and obtain any separate permissions needed for separately distributed components. We would not modify the application’s update mechanism, bypass its licence, or accept installer and driver prompts on a user’s behalf. An approved non-GitHub release feed or download URL would also be useful if you prefer not to permit mirroring.

官方来源：https://github.com/Rem0o/FanControl.Releases/blob/master/LICENSE 和 https://github.com/Rem0o/FanControl.Releases/releases/tag/V282 。

## HiBit Uninstaller：补入正文的特定段落

We request permission for both official HiBit Uninstaller `4.0.10` formats: `HiBitUninstaller-Portable-4.0.10.zip` and `HiBitUninstaller-setup-4.0.10.exe`. We would keep these as separate portable and installer choices. The verified original portable ZIP is 6,025,084 bytes, SHA256 `986b1e07de8037cda70e4d2cf01010e2660042f79f233bbac91846de94741646`; the setup is 4,533,003 bytes, SHA256 `ef4f82968d2ab81e574d8cddd433c66190947ba5d28ca83bec8034d124a007dc`.

Your website describes the software as free, but we did not find a statement explicitly covering a third-party mirror. Please confirm whether hosting those unchanged files and linking them from our software directory is allowed, what attribution you require, and whether future versions are covered. We would preserve the original package contents and your supported Windows versions rather than claim additional compatibility.

官方来源：https://www.hibitsoft.ir/Uninstaller.html 和 https://www.hibitsoft.ir/About.html 。

## Geek Uninstaller：补入正文的特定段落

We request permission for the unchanged Geek Uninstaller Free `1.5.5.185` archive distributed as `geek.zip` on your own website. The verified original is 3,210,637 bytes, SHA256 `4f52fa1ef943111f902cc02ab2ef9de5f6dde01ed3649a7cc6940aa28e78c61e`. We understand that the Free edition is for personal use only. We would make that condition visible and would not describe it as a free commercial licence.

Please confirm whether an independently operated website/client may mirror this Free package, and whether that distribution requires a separate agreement even when users receive the package without charge. We would not mirror PRO or Uninstall Tool, include licence keys, or confuse a separate product’s trial with Geek Uninstaller Free. Please also specify whether permission covers future Free releases and any required wording or official links.

官方来源：https://geekuninstaller.com/download 和 https://geekuninstaller.com/about 。

## HDDScan：补入正文的特定段落

We request permission for the exact original HDDScan `4.1` archive from `https://hddscan.com/download/HDDScan.zip`. The verified original is 3,833,107 bytes, SHA256 `8f392fc0c2dbb5b75848b7f791c105da28d5f1260e3d324b2f9ea9c72122657c`. We would retain `HDDScan.exe`, the complete `res` directory, `UserManual.pdf`, notices and the original licence display.

We read the embedded License Agreement and the archive’s PAD metadata. The former contains copyright and warranty disclaimers, while the `Distribution_Permissions` and `EULA` fields in the latter are empty. We are asking for an explicit mirror permission rather than interpreting those empty fields or the word “freeware” as consent. Please specify the permitted distribution scope, attribution and supported systems, including whether later official releases can use the same permission. We would not automatically run disk tests or write/erase operations for users.

官方来源：https://hddscan.com/ 和 https://hddscan.com/download.html 。

## SeaTools：补入正文的特定段落

We request permission for the current official SeaTools Windows installer, `SeaToolsWindowsInstaller.exe`, whose signed product version is `5.3.1`. The verified original from Seagate is 71,145,424 bytes, SHA256 `e01982752ab084bcbff0ea4de32cdd5bfd1afb268a87ce3870c5f834e18193b3`, with a valid `SEAGATE TECHNOLOGY LLC` signature. This request concerns that unchanged Windows installer; it does not assume that permission for SeaChest, libraries or an older SeaTools release covers SeaTools 5.3.1.

Please provide or identify the redistribution terms applicable to this exact product and confirm whether our proposed public mirror is permitted. Please also clarify the scope for future Windows updates, separately supplied bootable packages and any included dependencies. We would keep Windows installation and bootable-USB creation as distinct workflows and preserve the first-launch language and licence-confirmation process. We would not accept the licence on behalf of the user or perform drive tests, firmware changes or erase operations automatically.

官方来源：https://www.seagate.com/support/downloads/seatools/ 。

## 回函审核时必须记录的条件

- 回函主体是否确有权授权整个原包；仅某个库的授权不能覆盖整个应用。
- 授权是否包含公开网站、客户端入口、镜像服务器、地域、商业运营场景及免费/收费区别。
- 覆盖的精确产品、版次、原始格式、版本，以及未来更新是否自动包含。
- 必须保留的许可、版权、作者来源链接、产品名/图标使用条件，以及单独组件的条件。
- 原包原样分发、签名、完整性、撤回、到期和旧版保留的要求。
- 有限制的回函按其明确范围实施；未回复、仅确认收件或仅授权使用软件，不当作已取得镜像许可。
