using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Models;
using TubaWinUi3.Services;
using static TubaWinUi3.Pages.GameTunnelUi;

namespace TubaWinUi3.Pages;

/// <summary>新手教程与常见问题（一次性讲清楚，主页只留入口）。</summary>
public sealed class GameTunnelGuideDialog
{
    private readonly ContentDialog _dialog;

    public GameTunnelGuideDialog(XamlRoot xamlRoot)
    {
        var body = new StackPanel { Spacing = 12, MinWidth = 520 };

        body.Children.Add(SectionHeader(GameTunnelTexts.T("三步开始")));
        body.Children.Add(Card(Section(
            Line(GameTunnelTexts.T("第 1 步 · 两台电脑各点一次「准备联机环境」"), 13),
            Line(GameTunnelTexts.T("会自动下载安装 Tailscale 并引导登录（浏览器里点一下授权）。只做一次，以后都不用再管。")),
            Line(GameTunnelTexts.T("第 2 步 · 主机在游戏里把游戏开起来"), 13),
            Line(GameTunnelTexts.T("比如泰拉瑞亚点「开服并开始游戏」、我的世界按 Esc 选「对局域网开放」。")),
            Line(GameTunnelTexts.T("第 3 步 · 主机点「我当主机，邀请朋友」，把邀请发给朋友"), 13),
            Line(GameTunnelTexts.T("朋友点「我加入朋友」粘贴邀请码即可；没装工具箱的朋友可以运行「一键加入脚本」。")))));

        body.Children.Add(SectionHeader(GameTunnelTexts.T("朋友那边的三种加入方式")));
        body.Children.Add(Card(Section(
            Line(GameTunnelTexts.T("① 邀请码：朋友也装了图吧工具箱 —— 粘贴即用。密钥自带预授权，他直接进你的网络，你和他都不需要点任何「同意」。")),
            Line(GameTunnelTexts.T("② 一键加入脚本：朋友什么都没装 —— 发给他的 .cmd 双击运行，自动装好并直接加入，同样不需要同意。")),
            Line(GameTunnelTexts.T("③ 只发地址：只有对方已经和你在同一个 Tailscale 网络里时才有效（同一账号、你共享过设备且他接受了，或本就在同一 tailnet）。")),
            Line(GameTunnelTexts.T("不在同一网络时，你的 100.x 地址在他那边根本不存在——这种邀请发过去也连不上，改用 ①② 即可。")))));

        body.Children.Add(SectionHeader(GameTunnelTexts.T("常见问题")));

        body.Children.Add(Expander(GameTunnelTexts.T("邀请码和「只发地址」有什么区别？为什么地址有时不好使？"),
            GameTunnelTexts.T("邀请码里带着一把短期密钥，朋友用它直接加入你的网络——不用你批准，也不用他登录 Tailscale，这是最省事也最不容易出错的。\n")
            + GameTunnelTexts.T("「只发地址」则要求对方已经和你在同一个 Tailscale 网络里。Tailscale 的不同网络之间是互相隔离的：不在同一个网络，你的 100.x 地址在他那边根本不存在，填了也连不上。\n")
            + GameTunnelTexts.T("要让对方进你的网络，只有三条路：① 你发邀请码 / 一键加入脚本（直接，无需同意）② 你在 Tailscale 控制台把设备共享给他、由他点「接受」（这一步需要同意）③ 你们本来就在同一账号 / 同一 tailnet。\n")
            + GameTunnelTexts.T("所以除非对方本来就在你的网络里，否则请用邀请码或脚本。")));

        body.Children.Add(Expander(GameTunnelTexts.T("玩起来会卡吗？延迟高不高？"),
            GameTunnelTexts.T("打洞成功时是两台电脑直接连线，延迟基本等于你们之间的网络延迟；打洞失败会自动走加密中继，延迟会高一些但仍然能玩。\n想确认自己属于哪种：主页右上角「网络检测」→ 填对方地址 → 开始测试，会直接告诉你「直连」还是「中继」，以及具体延迟。")));

        body.Children.Add(Expander(GameTunnelTexts.T("要开路由器端口 / 有公网 IP 吗？"),
            GameTunnelTexts.T("都不需要。Tailscale 会自己打洞，打不通就走中继，所以也不怕运营商不给公网 IP。\n这也是为什么它比传统内网穿透省事——不用改路由器、不用记公网 IP。")));

        body.Children.Add(Expander(GameTunnelTexts.T("它和「局域网文件分享」是一回事吗？"),
            GameTunnelTexts.T("不是。局域网文件分享只在同一个 Wi-Fi 下有用；游戏联机助手是跨互联网的虚拟局域网，朋友在天南海北也能连。")));

        body.Children.Add(Expander(GameTunnelTexts.T("朋友加入后，我的电脑会被别人看到吗？"),
            GameTunnelTexts.T("只有加入同一个网络（tailnet）的设备能互相访问，而且只有你邀请的设备。\n朋友那边只连到你的这台电脑；邀请密钥有有效期，过期后不能再用来加入。\n玩完想断掉：右键任务栏 Tailscale 图标选择 Disconnect，或者在控制台删掉对应设备。")));

        body.Children.Add(Expander(GameTunnelTexts.T("为什么有时候刷不出朋友的房间？"),
            GameTunnelTexts.T("取决于游戏怎么找主机：靠平台联机（例如 Steam）或用 UDP 广播的游戏通常能刷到；靠局域网组播 / 子网广播扫描的（例如我的世界）常常刷不到——这类不是没连上，只是发现包过不去。\n")
            + GameTunnelTexts.T("刷不到就直接用游戏里的「直接连接 / 通过 IP 加入」（不同游戏叫法不同：泰拉瑞亚是「通过 IP 加入」、我的世界是「直接连接」、帕鲁是「加入多人游戏（专用服务器）」），填对方的 100.x 地址和端口，这条路任何游戏都有效。\n")
            + GameTunnelTexts.T("另外注意：两台电脑本来就在同一个 Wi-Fi / 路由器下时，刷到的是真实局域网，跟 Tailscale 没有关系。")));

        body.Children.Add(Expander(GameTunnelTexts.T("朋友加入了，但游戏里连不上"),
            GameTunnelTexts.T("按顺序检查：\n① 主机的游戏还开着吗？本工具主页会显示「游戏正在监听端口」。\n② 朋友是真的加入成功了吗？让他看工具箱的「网络检测」里的状态。\n③ 游戏里是用「直接连接 / 通过 IP 加入」填的地址吗？（靠局域网扫描经常刷不出对方，手动填地址最稳。）\n④ 双方做一次「测试与对方的连接」，看看是连不通还是连得通但游戏不认（后者通常是版本不一致）。\n⑤ 个别安全软件会拦截虚拟网卡，让朋友临时关掉网络防护再试一次。")));

        body.Children.Add(Expander(GameTunnelTexts.T("会不会修改我的网络设置/DNS？"),
            GameTunnelTexts.T("不会改动你的上网方式。朋友用邀请码加入时本工具会显式关闭 DNS 接管（--accept-dns=false），只借用虚拟网卡通信。\n除此之外没有任何系统级改动，断开 Tailscale 就完全恢复原样。")));

        body.Children.Add(Expander(GameTunnelTexts.T("游戏端口怎么查？"),
            GameTunnelTexts.T("内置了常见游戏的端口预设，选游戏时会自动填好。自定义游戏时：泰拉瑞亚 7777、我的世界（Java）25565、基岩版 19132、帕鲁 8211……\n实在不知道就去游戏里开一次（建房 / 开服），通常在提示里会写端口号。")));

        body.Children.Add(SectionHeader(GameTunnelTexts.T("内置游戏端口速查")));
        var portLines = GameTunnelCatalog.Presets
            .Select(p => GameTunnelTexts.TSub($"{p.Name} · {p.DefaultPort}（{p.Protocol.Describe()}）"));
        body.Children.Add(Card(Section(portLines.Select(l => Line(l)).ToArray())));

        var revoke = new HyperlinkButton { Padding = new Thickness(0, 4, 0, 0), Content = GameTunnelTexts.T("打开 Tailscale 控制台查看设备") };
        revoke.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TailscaleService.MachinesUrl)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        };
        body.Children.Add(revoke);

        _dialog = new ContentDialog
        {
            Title = GameTunnelTexts.T("使用教程"),
            Content = new ScrollViewer
            {
                Content = body,
                MaxHeight = 470,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = GameTunnelTexts.T("知道了"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
    }

    public Task<ContentDialogResult> ShowAsync() => _dialog.ShowAsync().AsTask();

    private static Expander Expander(string header, string body) => new()
    {
        Header = header,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Subtle,
            Margin = new Thickness(0, 8, 0, 4)
        }
    };
}
