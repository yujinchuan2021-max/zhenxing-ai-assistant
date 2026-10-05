namespace TubaWinUi3.Services;

/// <summary>
/// 两种邀请方式的准确说法。全应用共用，避免各处口径不一致
/// （曾经把「只发地址」写得像和邀请码一样即点即用，误导用户）。
///
/// 事实依据：
/// · 邀请码里带的是 preauthorized 授权密钥 —— 朋友用它入网时连控制台的设备批准都跳过，
///   所以是「直接加入，不需要任何人同意」。
/// · 纯地址不是「需要同意」，而是「双方必须已经在同一个 Tailscale 网络里」；
///   不在同一网络时那个 100.x 地址在对方机器上根本不存在。
///   要进同一网络只能：① 对方发邀请码/脚本（直接）② 对方在控制台共享设备、朋友点接受（需要同意）
///   ③ 本来就在同一账号/同一 tailnet。
/// </summary>
public static partial class GameTunnelInviteCopy
{
    /// <summary>邀请码：直接加入，无需任何批准。</summary>
    public static string InviteCodeDirect => GameTunnelTexts.T("邀请码里带着一把短期密钥，朋友用它直接加入你的网络——不需要你点批准，也不需要他自己登录 Tailscale。");

    /// <summary>一键加入脚本：同样是直接加入。</summary>
    public static string ScriptDirect => GameTunnelTexts.T("朋友双击这个 .cmd，它会自动装好 Tailscale 并连上你——和邀请码一样是直接加入，不需要任何人同意。");

    /// <summary>只发地址的前提（对方侧）。</summary>
    public static string AddressOnlyPrecondition =>
        GameTunnelTexts.T("前提：对方已经和你在同一个 Tailscale 网络里（同一账号、你共享过设备且他接受了，或你们本来就在同一个 tailnet）。") +
        GameTunnelTexts.T("不在同一网络时，这个 100.x 地址在他那儿是不存在的——那种情况请用上面的邀请码或一键加入脚本，它们是直接加入、无需同意。");

    /// <summary>只发地址时，朋友侧要看到的说明。</summary>
    public static string AddressOnlyGuestNote =>
        GameTunnelTexts.T("这是纯地址、没有密钥：只有你已经和对方在同一个 Tailscale 网络里才有效。") +
        GameTunnelTexts.T("如果你没加入过对方的网络，请让他发「邀请码」或「一键加入脚本」——那条路是直接加入，不需要任何人同意。");

    /// <summary>纯地址路径验证失败时的补救建议。</summary>
    public static string AddressOnlyRemedy =>
        GameTunnelTexts.T("你和对方不在同一个 Tailscale 网络里。仅凭地址连不上——请让对方在工具箱里用「邀请码」或「一键加入脚本」邀请你，") +
        GameTunnelTexts.T("那种邀请会把你直接加进他的网络（他不用批准，你也不用登录自己的 Tailscale）。");
}
