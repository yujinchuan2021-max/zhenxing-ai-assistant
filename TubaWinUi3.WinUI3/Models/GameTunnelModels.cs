namespace TubaWinUi3.Models;

/// <summary>游戏服务器使用的传输层协议。</summary>
public enum GameTunnelProtocol
{
    Tcp,
    Udp,
    TcpAndUdp
}

public static class GameTunnelProtocolExtensions
{
    public static bool UsesTcp(this GameTunnelProtocol protocol)
        => protocol is GameTunnelProtocol.Tcp or GameTunnelProtocol.TcpAndUdp;

    public static bool UsesUdp(this GameTunnelProtocol protocol)
        => protocol is GameTunnelProtocol.Udp or GameTunnelProtocol.TcpAndUdp;

    public static string Describe(this GameTunnelProtocol protocol) => protocol switch
    {
        GameTunnelProtocol.Tcp => "TCP",
        GameTunnelProtocol.Udp => "UDP",
        _ => "TCP + UDP"
    };
}

/// <summary>内置游戏档案：端口、协议、主机/朋友两侧的分步教程。</summary>
public sealed class GamePreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>拿不到真实 Logo 时的兜底图标字形（Segoe Fluent Icons）。</summary>
    public required string Glyph { get; init; }

    /// <summary>官方 Logo 素材的候选地址，按顺序尝试（第一个通就用）。</summary>
    public IReadOnlyList<string> LogoUrls { get; init; } = [];

    public required int DefaultPort { get; init; }
    public required GameTunnelProtocol Protocol { get; init; }
    public required string Tagline { get; init; }

    /// <summary>主机在游戏里需要先完成的事（一句话）。</summary>
    public required string HostAction { get; init; }

    public required IReadOnlyList<string> HostSteps { get; init; }
    public required IReadOnlyList<string> GuestSteps { get; init; }

    /// <summary>朋友在游戏内的连接位置说明，如「多人游戏 → 通过 IP 加入」。</summary>
    public required string GuestEntryPoint { get; init; }

    public string? Note { get; init; }
}

/// <summary>用户自己添加的游戏（与内置档案同构，可编辑删除）。</summary>
public sealed class CustomGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public GameTunnelProtocol Protocol { get; set; } = GameTunnelProtocol.Tcp;

    /// <summary>给朋友看的补充说明（可空）。</summary>
    public string? Note { get; set; }
}

/// <summary>邀请码载荷：一段可以被复制到聊天软件里发出去的紧凑字符串。</summary>
public sealed class InviteInfo
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Game { get; set; } = "";
    public GameTunnelProtocol Protocol { get; set; } = GameTunnelProtocol.Tcp;

    /// <summary>主机 tailnet 的一次性/短期授权密钥；为空表示「对方已有同一网络」。</summary>
    public string? AuthKey { get; set; }

    /// <summary>主机设备名（展示用）。</summary>
    public string? HostName { get; set; }

    /// <summary>邀请码失效时间（Unix 秒），0 表示不限。</summary>
    public long ExpiresAt { get; set; }

    public bool HasAuthKey => !string.IsNullOrWhiteSpace(AuthKey);

    public DateTimeOffset? ExpiresAtLocal => ExpiresAt > 0
        ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAt).ToLocalTime()
        : null;

    public string Address => $"{Host}:{Port}";
}

/// <summary>tailscale status --json 的解析结果。</summary>
public sealed class TailscaleStatus
{
    public string BackendState { get; init; } = "";
    public string? Version { get; init; }
    public string? AuthUrl { get; init; }
    public string? Ipv4 { get; init; }
    public string? Ipv6 { get; init; }
    public string? HostName { get; init; }
    public string? DnsName { get; init; }
    public string? LoginName { get; init; }
    public string? TailnetName { get; init; }
    public IReadOnlyList<string> Health { get; init; } = [];

    /// <summary>tailnet 里的其它设备（与状态同一次 status --json 解析出来）。</summary>
    public IReadOnlyList<TailscalePeer> Peers { get; init; } = [];

    /// <summary>本机是否已经写过节点密钥（有过登录记录）。</summary>
    public bool HaveNodeKey { get; init; }

    /// <summary>后端已连上控制服务器并可收发流量。</summary>
    public bool IsRunning => string.Equals(BackendState, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 后端还在启动（或卡在启动）——<b>既不是「未登录」也不可用</b>。
    /// NoState 尤其容易误判：浏览器里授权成功后如果本机连不上控制服务器取密钥，
    /// 状态会一直停在 NoState，界面绝不能因此显示「未登录」并把用户推去重复登录。
    /// </summary>
    public bool IsStarting => BackendState is "" or "NoState" or "Starting";

    /// <summary>本机已在某个 tailnet 中（无论当前是否正在跑）。</summary>
    public bool IsLoggedIn => BackendState is "Running" or "Stopped" or "Starting" or "NeedsMachineAuth";

    /// <summary>需要用户在浏览器里完成登录。</summary>
    public bool NeedsLogin => BackendState == "NeedsLogin";

    /// <summary>已登录且拿到了 Tailscale IP——可以开始联机。</summary>
    public bool IsReady => IsRunning && !string.IsNullOrWhiteSpace(Ipv4);

    public string DescribeState() => BackendState switch
    {
        "Running" => "已就绪",
        "Starting" => "正在启动",
        "Stopped" => "已登录 · 未连接",
        "NeedsLogin" => "未登录",
        "NeedsMachineAuth" => "等待管理员批准",
        "NoState" => "正在启动",
        "" => "未检测到",
        _ => BackendState
    };
}

/// <summary>tailnet 里的另一台设备。</summary>
public sealed class TailscalePeer
{
    public string HostName { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Ipv4 { get; init; }
    public bool Online { get; init; }

    /// <summary>有 CurAddr 表示打洞成功走了直连，否则流量经 DERP 中继。</summary>
    public bool IsDirect { get; init; }

    public string? Relay { get; init; }
    public string? OwnerLogin { get; init; }
    public string? Os { get; init; }

    public string Describe()
    {
        if (!Online) return "离线";
        return IsDirect ? "直连" : Relay is { Length: > 0 } ? $"中继 · {Relay}" : "已连接";
    }
}

/// <summary>tailscale netcheck 报告（判断能不能打洞直连）。</summary>
public sealed class TailscaleNetReport
{
    public bool Udp { get; init; }
    public bool HasIpv4 { get; init; }
    public bool HasIpv6 { get; init; }
    public bool MappingVariesByDestIp { get; init; }
    public bool HasPortMapping { get; init; }
    public string? NearestDerp { get; init; }
    public double? NearestDerpMs { get; init; }
    public IReadOnlyList<(string Code, double Ms)> DerpLatency { get; init; } = [];

    /// <summary>UDP 可用且 NAT 不难穿透，预计可以点对点直连。</summary>
    public bool LikelyDirect => Udp && !MappingVariesByDestIp;

    public string Verdict
    {
        get
        {
            if (!Udp) return "UDP 被封锁，只能走中继（延迟会偏高）";
            if (MappingVariesByDestIp) return "检测到难穿透 NAT，可能走中继";
            return "预计可以直连，延迟接近本机网络质量";
        }
    }

    public string Detail()
    {
        var parts = new List<string> { Udp ? "UDP 可用" : "UDP 不可用" };
        if (HasIpv4) parts.Add("有 IPv4");
        if (HasIpv6) parts.Add("有 IPv6");
        if (HasPortMapping) parts.Add("路由器支持端口映射");
        if (NearestDerp is { Length: > 0 }) parts.Add($"最近中继 {NearestDerp} {NearestDerpMs:F0}ms");
        return string.Join(" · ", parts);
    }
}

/// <summary>tailscale ping 的结果：直连还是中继、延迟多少。</summary>
public sealed class TailscalePingResult
{
    public bool Ok { get; init; }
    public bool Direct { get; init; }
    public string? PeerIp { get; init; }
    public double? LatencyMs { get; init; }
    public string Summary { get; init; } = "";

    /// <summary>
    /// 对方根本不在本机的 tailnet 里（tailscale ping 回 "no matching peer"）。
    /// 这是「纯地址」那条路的判据：地址不在本机网络里，光有地址永远连不上。
    /// </summary>
    public bool PeerNotInTailnet { get; init; }
}

/// <summary>一条 Health 告警的中文释义。</summary>
public sealed class TailscaleHealthNote
{
    public string Code { get; init; } = "unknown";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string? Advice { get; init; }

    /// <summary>官方标记为影响连通性的告警（否则只当提醒）。</summary>
    public bool ImpactsConnectivity { get; init; }

    /// <summary>临时状态（如正在启动），不应提示用户排障。</summary>
    public bool Transient { get; init; }

    public string Describe() => Advice is { Length: > 0 } ? $"{Text}（{Advice}）" : Text;
}
