using System.Text;
using TubaWinUi3.Models;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 游戏联机助手的纯逻辑回归：解析、邀请码、脚本生成、游戏档案。
/// 不依赖本机是否装了 Tailscale，也不会真的发起登录或联机。
/// </summary>
public class GameTunnelTests : IDisposable
{
    private readonly string _tempDir;

    public GameTunnelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TubaGameTunnel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        GameTunnelCatalog.DataDirOverride = _tempDir;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public void Dispose()
    {
        GameTunnelCatalog.DataDirOverride = null;
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ══════════════════ 后端卡在启动（真实故障现场） ══════════════════

    /// <summary>
    /// 开发机真实现场：浏览器里授权成功了，但本机连不上控制服务器（被代理/加速器拦截），
    /// 状态一直停在 NoState。界面绝不能把它显示成「未登录」，否则用户会反复点登录。
    /// </summary>
    [Fact]
    public void ParseStatusJson_NoStateIsStartingNotLoggedOut()
    {
        const string json = """
        {
          "BackendState": "NoState",
          "HaveNodeKey": true,
          "AuthURL": "",
          "Self": { "HostName": "LAPTOP-CE9T4R0L", "TailscaleIPs": null },
          "Health": [
            "Tailscale is starting. Please wait.",
            "You are logged out. The last login error was: fetch control key: Get \"https://controlplane.tailscale.com/key?v=142\": context canceled"
          ]
        }
        """;

        var status = TailscaleService.ParseStatusJson(json);

        Assert.NotNull(status);
        Assert.True(status!.IsStarting);
        Assert.False(status.IsLoggedIn);
        Assert.False(status.NeedsLogin);
        Assert.False(status.IsReady);
        Assert.True(status.HaveNodeKey);
        Assert.Equal("正在启动", status.DescribeState());
    }

    [Fact]
    public void ParseStatusJson_NeedsLoginIsTheOnlyLoggedOutState()
    {
        var needsLogin = TailscaleService.ParseStatusJson("""{"BackendState":"NeedsLogin"}""");
        Assert.NotNull(needsLogin);
        Assert.True(needsLogin!.NeedsLogin);
        Assert.False(needsLogin.IsStarting);

        var starting = TailscaleService.ParseStatusJson("""{"BackendState":"Starting","HaveNodeKey":true}""");
        Assert.NotNull(starting);
        Assert.True(starting!.IsStarting);
        Assert.False(starting.NeedsLogin);
    }

    /// <summary>
    /// warming-up 会抑制其它提醒，但解释故障的告警必须留下——否则用户只看到「正在启动」，
    /// 永远不知道是代理拦住了控制服务器。
    /// </summary>
    [Fact]
    public void DescribeHealthLines_KeepsExplanationWhileWarmingUp()
    {
        var notes = TailscaleService.DescribeHealthLines(
        [
            "Tailscale is starting. Please wait.",
            "You are logged out. The last login error was: fetch control key: Get \"https://controlplane.tailscale.com/key?v=142\": context canceled"
        ]);

        var only = Assert.Single(notes);
        Assert.Equal("fetch-control-key", only.Code);
        Assert.True(only.ImpactsConnectivity);
        Assert.Contains("代理", only.Advice);
    }

    [Fact]
    public void DescribeHealth_RegisterFailurePointsAtProxy()
    {
        var note = TailscaleService.DescribeHealth(
            "You are logged out. The last login error was: register request: Post \"https://controlplane.tailscale.com/machine/register\": context canceled");

        Assert.Equal("register-node", note.Code);
        Assert.True(note.ImpactsConnectivity);
        Assert.Contains("controlplane.tailscale.com", note.Advice);
    }

    [Fact]
    public void DescribeStartupHint_NamesProxyWhenPresent()
    {
        var withProxy = TailscaleService.DescribeStartupHint("127.0.0.1:58088");
        Assert.Contains("127.0.0.1:58088", withProxy);
        Assert.Contains("重启 Tailscale 服务", withProxy);

        var withoutProxy = TailscaleService.DescribeStartupHint(null, "proxy.local:8080");
        Assert.Contains("proxy.local:8080", withoutProxy);

        var generic = TailscaleService.DescribeStartupHint(null);
        Assert.Contains("controlplane.tailscale.com", generic);
    }

    // ══════════════════ 真实数据：status --json ══════════════════

    /// <summary>开发机真实输出（未登录状态，Health 里带两条告警）。</summary>
    private const string RealNeedsLoginJson = """
    {
      "Version": "1.102.4-t3caf7d9e7-g084ee3b64",
      "TUN": true,
      "BackendState": "NeedsLogin",
      "AuthURL": "https://login.tailscale.com/a/1cbef3bf01be15",
      "TailscaleIPs": null,
      "Self": {
        "ID": "",
        "HostName": "LAPTOP-CE9T4R0L",
        "DNSName": "",
        "OS": "windows",
        "UserID": 0,
        "TailscaleIPs": null,
        "Online": false
      },
      "Health": [
        "Unable to connect to the Tailscale coordination server to synchronize the state of your tailnet. Peer reachability might degrade over time.",
        "You are logged out. The last login error was: register request: Post \"https://controlplane.tailscale.com/machine/register\": context canceled"
      ],
      "MagicDNSSuffix": "",
      "CurrentTailnet": null,
      "Peer": null,
      "User": null
    }
    """;

    [Fact]
    public void ParseStatusJson_ReadsRealNeedsLoginOutput()
    {
        var status = TailscaleService.ParseStatusJson(RealNeedsLoginJson);

        Assert.NotNull(status);
        Assert.Equal("NeedsLogin", status!.BackendState);
        Assert.True(status.NeedsLogin);
        Assert.False(status.IsLoggedIn);
        Assert.False(status.IsReady);
        Assert.Equal("未登录", status.DescribeState());
        Assert.Equal("LAPTOP-CE9T4R0L", status.HostName);
        Assert.Equal("https://login.tailscale.com/a/1cbef3bf01be15", status.AuthUrl);
        Assert.Null(status.Ipv4);
        Assert.Equal(2, status.Health.Count);
    }

    [Fact]
    public void ParseStatusJson_ReadsRunningWithIpsAndUser()
    {
        const string json = """
        {
          "Version": "1.102.4",
          "BackendState": "Running",
          "Self": {
            "HostName": "desktop-abc",
            "DNSName": "desktop-abc.my-tailnet.ts.net.",
            "UserID": 3124156728162092,
            "TailscaleIPs": ["100.101.102.103", "fd7a:115c:a1e0::1"]
          },
          "User": {
            "3124156728162092": { "ID": 3124156728162092, "LoginName": "alice@example.com", "DisplayName": "Alice" }
          },
          "MagicDNSSuffix": "my-tailnet.ts.net",
          "CurrentTailnet": { "Name": "example.com", "MagicDNSSuffix": "my-tailnet.ts.net" },
          "Health": null,
          "Peer": {}
        }
        """;

        var status = TailscaleService.ParseStatusJson(json);

        Assert.NotNull(status);
        Assert.True(status!.IsReady);
        Assert.Equal("已就绪", status.DescribeState());
        Assert.Equal("100.101.102.103", status.Ipv4);
        Assert.Equal("fd7a:115c:a1e0::1", status.Ipv6);
        Assert.Equal("desktop-abc.my-tailnet.ts.net", status.DnsName);
        Assert.Equal("alice@example.com", status.LoginName);
        Assert.Equal("example.com", status.TailnetName);
        Assert.Empty(status.Health);
    }

    [Fact]
    public void ParseStatusJson_StoppedIsLoggedInButNotReady()
    {
        var status = TailscaleService.ParseStatusJson("""{"BackendState":"Stopped","Self":{"TailscaleIPs":["100.64.0.9"]}}""");

        Assert.NotNull(status);
        Assert.True(status!.IsLoggedIn);
        Assert.False(status.IsReady);
        Assert.Equal("已登录 · 未连接", status.DescribeState());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"Version":"1.2.3"}""")]
    public void ParseStatusJson_RejectsGarbage(string? json)
        => Assert.Null(TailscaleService.ParseStatusJson(json));

    [Fact]
    public void ParseStatusJson_ToleratesMissingUserMap()
    {
        var status = TailscaleService.ParseStatusJson("""{"BackendState":"Running","Self":{"TailscaleIPs":["100.64.0.1"]}}""");
        Assert.NotNull(status);
        Assert.Null(status!.LoginName);
        Assert.Equal("100.64.0.1", status.Ipv4);
    }

    // ══════════════════ Health 告警分级 ══════════════════

    [Fact]
    public void DescribeHealth_NotInMapPollIsReminderNotFailure()
    {
        var note = TailscaleService.DescribeHealth(
            "Unable to connect to the Tailscale coordination server to synchronize the state of your tailnet. Peer reachability might degrade over time.");

        Assert.Equal("not-in-map-poll", note.Code);
        Assert.False(note.ImpactsConnectivity);
        Assert.Contains("协调服务器", note.Text);
        // 界面不能出现英文原文
        Assert.DoesNotContain("Unable to connect", note.Describe());
    }

    [Fact]
    public void DescribeHealth_WarmingUpIsTransient()
    {
        var note = TailscaleService.DescribeHealth("Tailscale is starting. Please wait.");
        Assert.Equal("warming-up", note.Code);
        Assert.True(note.Transient);
        Assert.False(note.ImpactsConnectivity);
    }

    [Theory]
    [InlineData("Tailscale could not connect to any relay server. Check your Internet connection.", "no-derp-home")]
    [InlineData("Tailscale cannot connect because the network is down. Check your Internet connection.", "network-status")]
    [InlineData("Tailscale couldn't listen for incoming UDP connections.", "no-udp4-bind")]
    [InlineData("Tailscale could not establish an encrypted connection with 'controlplane.tailscale.com': tls: bad certificate", "tls-connection-failed")]
    [InlineData("Tailscale could not connect to the 'tok' relay server. Your Internet connection might be down.", "no-derp-connection")]
    public void DescribeHealth_FlagsConnectivityImpacting(string raw, string expectedCode)
    {
        var note = TailscaleService.DescribeHealth(raw);
        Assert.Equal(expectedCode, note.Code);
        Assert.True(note.ImpactsConnectivity);
        Assert.False(note.Transient);
    }

    [Theory]
    [InlineData("Tailscale hasn't received a network map from the coordination server in 5m0s.", "map-response-timeout")]
    [InlineData("The coordination server is reporting a health issue: NODE_KEY_EXPIRED", "control-health")]
    [InlineData("Tailscale hasn't heard from the 'tok' relay server in 10s.", "derp-timed-out")]
    [InlineData("You are logged out. The last login error was: x", "login-state")]
    [InlineData("this device needs machine authorization", "machine-auth")]
    [InlineData("Tailscale is stopped.", "want-running-false")]
    [InlineData("A security update from version 1.0 to 1.1 is available.", "security-update-available")]
    public void DescribeHealth_CoversCommonWarnings(string raw, string expectedCode)
        => Assert.Equal(expectedCode, TailscaleService.DescribeHealth(raw).Code);

    [Fact]
    public void DescribeHealth_UnknownTextPassesThrough()
    {
        var note = TailscaleService.DescribeHealth("something nobody has seen before");
        Assert.Equal("unknown", note.Code);
        Assert.Contains("something nobody has seen before", note.Describe());
        Assert.False(note.ImpactsConnectivity);
    }

    [Fact]
    public void DescribeHealthLines_WarmingUpSuppressesOthers()
    {
        var notes = TailscaleService.DescribeHealthLines(
        [
            "Unable to connect to the Tailscale coordination server to synchronize the state of your tailnet.",
            "Tailscale is starting. Please wait."
        ]);

        var only = Assert.Single(notes);
        Assert.Equal("warming-up", only.Code);
        Assert.True(only.Transient);
    }

    [Fact]
    public void DescribeHealthLines_KeepsEveryNoteOtherwise()
    {
        var notes = TailscaleService.DescribeHealthLines(["Tailscale is stopped.", "Tailscale couldn't listen for incoming UDP connections."]);
        Assert.Equal(2, notes.Count);
        Assert.True(TailscaleService.HasBlockingHealth(notes));
    }

    [Fact]
    public void DescribeHealthLines_EmptyInputYieldsNothing()
    {
        Assert.Empty(TailscaleService.DescribeHealthLines(null));
        Assert.Empty(TailscaleService.DescribeHealthLines([]));
        Assert.Empty(TailscaleService.DescribeHealthLines(["  "]));
        Assert.False(TailscaleService.HasBlockingHealth(null));
    }

    // ══════════════════ netcheck（开发机真实输出） ══════════════════

    private const string RealNetCheck = """
        2026/09/14 22:37:47 No DERP map from tailscaled; using default.
        2026/09/14 22:37:47 attempting to fetch a DERPMap from https://controlplane.tailscale.com
        2026/09/14 22:37:48 portmap: monitor: gateway and self IP changed: gw=172.27.0.1 self=172.27.110.235

        Report:
                * Time: 2026-09-14 22:37:53.3621299+08:00
                * UDP: true
                * IPv4: yes, 183.221.22.239:38290
                * IPv6: yes, [2409:8762:318:90:9385::1b]:56924
                * MappingVariesByDestIP: true
                * PortMapping: 
                * CaptivePortal: false
                * Nearest DERP: Hong Kong
                * DERP latency:
                        - hkg: 67.6ms  (Hong Kong)
                        - sin: 102.7ms (Singapore)
                        - tok: 105.2ms (Tokyo)
                        - lax: 229ms   (Los Angeles)
        """;

    [Fact]
    public void ParseNetCheck_ReadsRealOutput()
    {
        var report = TailscaleService.ParseNetCheck(RealNetCheck);

        Assert.NotNull(report);
        Assert.True(report!.Udp);
        Assert.True(report.HasIpv4);
        Assert.True(report.HasIpv6);
        Assert.True(report.MappingVariesByDestIp);
        Assert.False(report.HasPortMapping);
        Assert.Equal("Hong Kong", report.NearestDerp);
        Assert.Equal(67.6, report.NearestDerpMs!.Value, 1);
        Assert.Equal(4, report.DerpLatency.Count);
        // 无小数点的 "229ms" 也要解析出来
        Assert.Equal(229, report.DerpLatency.Single(d => d.Code == "lax").Ms, 1);
        Assert.False(report.LikelyDirect);
        Assert.Contains("难穿透 NAT", report.Verdict);
        Assert.Contains("UDP 可用", report.Detail());
    }

    [Fact]
    public void ParseNetCheck_GoodNetworkPredictsDirect()
    {
        const string good = """
            Report:
            	* UDP: true
            	* IPv4: yes, 1.2.3.4:5
            	* IPv6: no, but OS has support
            	* MappingVariesByDestIP: false
            	* PortMapping: UPnP, NAT-PMP, PCP
            	* Nearest DERP: Hong Kong
            	* DERP latency:
            		- hkg: 12.5ms  (Hong Kong)
            """;

        var report = TailscaleService.ParseNetCheck(good);

        Assert.NotNull(report);
        Assert.True(report!.HasPortMapping);
        Assert.True(report.LikelyDirect);
        Assert.Contains("直连", report.Verdict);
    }

    [Fact]
    public void ParseNetCheck_NoUdpMeansRelayOnly()
    {
        var report = TailscaleService.ParseNetCheck("Report:\n\t* UDP: false\n\t* IPv4: yes, 1.2.3.4:5\n\t* MappingVariesByDestIP: false\n");

        Assert.NotNull(report);
        Assert.False(report!.LikelyDirect);
        Assert.Contains("UDP", report.Verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no report here at all")]
    [InlineData("Report:\n\t* CaptivePortal: false\n")]
    public void ParseNetCheck_RejectsGarbage(string? output)
        => Assert.Null(TailscaleService.ParseNetCheck(output));

    // ══════════════════ ping ══════════════════

    [Fact]
    public void ParsePing_DetectsDirectPath()
    {
        var result = TailscaleService.ParsePing("pong from desktop-friend (100.101.102.103) via 203.0.113.7:41641 in 12.4ms");

        Assert.True(result.Ok);
        Assert.True(result.Direct);
        Assert.Equal("100.101.102.103", result.PeerIp);
        Assert.Contains("直连", result.Summary);
    }

    [Fact]
    public void ParsePing_DetectsRelayPath()
    {
        var result = TailscaleService.ParsePing("pong from desktop-friend (100.101.102.103) via DERP(tok) in 92ms");

        Assert.True(result.Ok);
        Assert.False(result.Direct);
        Assert.Contains("中继", result.Summary);
        Assert.Contains("tok", result.Summary);
    }

    /// <summary>开发机真实输出：未登录时 ping 会把登录地址打出来，不能当成「连不上」以外的结论。</summary>
    [Fact]
    public void ParsePing_ExplainsLoggedOut()
    {
        var result = TailscaleService.ParsePing("\nLogged out.\nLog in at: https://login.tailscale.com/a/1cbef3bf01be15\n");

        Assert.False(result.Ok);
        Assert.Contains("未登录", result.Summary);
    }

    [Theory]
    [InlineData("no matching peer", "不在你的网络里")]
    [InlineData("ping timed out waiting for pong", "没有响应")]
    public void ParsePing_ExplainsFailures(string output, string fragment)
    {
        var result = TailscaleService.ParsePing(output);
        Assert.False(result.Ok);
        Assert.Contains(fragment, result.Summary);
    }

    /// <summary>
    /// 「no matching peer」是纯地址路径的判据：这个地址根本不在本机 tailnet 里。
    /// 必须能和「在同一网络但对方没响应」区分开，否则界面没法给出正确的补救建议。
    /// </summary>
    [Fact]
    public void ParsePing_MarksPeerNotInTailnet()
    {
        var notInTailnet = TailscaleService.ParsePing("no matching peer");
        Assert.True(notInTailnet.PeerNotInTailnet);
        Assert.False(notInTailnet.Ok);

        var offline = TailscaleService.ParsePing("ping timed out waiting for pong");
        Assert.False(offline.PeerNotInTailnet);

        var direct = TailscaleService.ParsePing("pong from laptop (100.80.223.82) in 12ms");
        Assert.True(direct.Ok);
        Assert.False(direct.PeerNotInTailnet);
    }

    [Fact]
    public void ParsePing_IgnoresLogNoise()
    {
        var noisy = TailscaleService.ParsePing("2026/09/14 22:12:12 tshttpproxy: using proxy \"http://127.0.0.1:1/\"\nping never got a reply");
        Assert.False(noisy.Ok);
        Assert.DoesNotContain("tshttpproxy", noisy.Summary);
    }

    // ══════════════════ 登录地址 / 版本 / 密钥 ══════════════════

    [Fact]
    public void ExtractAuthUrl_ReadsRealLoginOutput()
    {
        const string output = "To authenticate, visit:\n\n\thttps://login.tailscale.com/a/2f1c9d8e7a6b\n";
        Assert.Equal("https://login.tailscale.com/a/2f1c9d8e7a6b", TailscaleService.ExtractAuthUrl(output));
        Assert.Equal("https://login.tailscale.com/a/1cbef3bf01be15", TailscaleService.ExtractAuthUrl("Log in at: https://login.tailscale.com/a/1cbef3bf01be15"));
        Assert.Null(TailscaleService.ExtractAuthUrl("see https://tailscale.com/download"));
        Assert.Null(TailscaleService.ExtractAuthUrl(""));
    }

    [Fact]
    public void ParseVersionText_TakesVersionFromRealOutput()
    {
        const string real = """
            1.102.4
              tailscale commit: 3caf7d9e7dcaba589cfc58beda596929733e4fea
              long version: 1.102.4-t3caf7d9e7-g084ee3b64
              go version: go1.26.6 (tailscale/go 7275f792d4)
            """;
        Assert.Equal("1.102.4", TailscaleService.ParseVersionText(real));
        Assert.Equal("1.102.4", TailscaleService.ParseVersionText("\n\n1.102.4\n"));
        Assert.Null(TailscaleService.ParseVersionText("   "));
    }

    [Fact]
    public void ParsePackagesVersion_PrefersVersionField()
    {
        const string real = """
        {"Version":"1.102.4","Tarballs":{"amd64":"tailscale_1.102.4_amd64.tgz"},
         "MSIs":{"amd64":"tailscale-setup-1.102.4-amd64.msi","arm64":"tailscale-setup-1.102.4-arm64.msi","x86":"tailscale-setup-1.102.4-x86.msi"},
         "MSIsVersion":"1.102.4"}
        """;
        Assert.Equal("1.102.4", TailscaleService.ParsePackagesVersion(real));
        Assert.Equal("1.102.4", TailscaleService.ParsePackagesVersion("""{"MSIsVersion":"1.102.4"}"""));
        Assert.Null(TailscaleService.ParsePackagesVersion("{}"));
        Assert.Null(TailscaleService.ParsePackagesVersion(null));
    }

    [Fact]
    public void InstallerUrl_UsesArchSuffix()
    {
        Assert.Equal(
            "https://pkgs.tailscale.com/stable/tailscale-setup-1.102.4-amd64.msi",
            TailscaleService.InstallerUrl("1.102.4", "amd64"));
        Assert.EndsWith($"tailscale-setup-1.102.4-{TailscaleService.ArchSuffix}.msi",
            TailscaleService.InstallerUrl("1.102.4", TailscaleService.ArchSuffix));
    }

    [Theory]
    [InlineData("tskey-auth-k1234567890CNTRL", "tskey-auth-k1234567890CNTRL")]
    [InlineData("  tskey-auth-abcdefghijklmnop  ", "tskey-auth-abcdefghijklmnop")]
    [InlineData("tskey-api-abcdefghijklmnop", null)]
    [InlineData("random-string", null)]
    [InlineData(null, null)]
    public void NormalizeAuthKey_Validates(string? input, string? expected)
        => Assert.Equal(expected, TailscaleService.NormalizeAuthKey(input));

    [Theory]
    [InlineData("tskey-api-kAbc123456789012345", "tskey-api-kAbc123456789012345")]
    [InlineData(" tskey-api-abcdefghijklmnop ", "tskey-api-abcdefghijklmnop")]
    [InlineData("tskey-auth-abcdefghijklmnop", null)]
    [InlineData(null, null)]
    public void NormalizeApiKey_Validates(string? input, string? expected)
        => Assert.Equal(expected, TailscaleApiService.NormalizeApiKey(input));

    [Fact]
    public void DescribeKeyKindMismatch_DetectsAuthKeyInApiField()
    {
        Assert.NotNull(TailscaleApiService.DescribeKeyKindMismatch("tskey-auth-abcdefghijklmnop"));
        Assert.Null(TailscaleApiService.DescribeKeyKindMismatch("tskey-api-abcdefghijklmnop"));
        Assert.Null(TailscaleApiService.DescribeKeyKindMismatch(""));
    }

    [Fact]
    public void IsUsableInstaller_ChecksMagicAndSize()
    {
        var small = Path.Combine(_tempDir, "small.msi");
        File.WriteAllBytes(small, [0xD0, 0xCF, 0x11, 0xE0]);
        Assert.False(TailscaleService.IsUsableInstaller(small));

        var garbage = Path.Combine(_tempDir, "garbage.msi");
        using (var fs = File.Create(garbage))
        {
            fs.Write("PK"u8);
            fs.SetLength(6L * 1024 * 1024);
        }
        Assert.False(TailscaleService.IsUsableInstaller(garbage));

        var compound = Path.Combine(_tempDir, "real.msi");
        using (var fs = File.Create(compound))
        {
            fs.Write([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
            fs.SetLength(6L * 1024 * 1024);
        }
        Assert.True(TailscaleService.IsUsableInstaller(compound));

        Assert.False(TailscaleService.IsUsableInstaller(Path.Combine(_tempDir, "missing.msi")));
    }

    [Fact]
    public void FirewallRuleName_IsStablePerPortAndProtocol()
    {
        var name = TailscaleService.FirewallRuleName(25565, GameTunnelProtocol.Tcp);
        Assert.Contains("TCP", name);
        Assert.Contains("25565", name);
        Assert.NotEqual(name, TailscaleService.FirewallRuleName(25566, GameTunnelProtocol.Tcp));
        Assert.NotEqual(name, TailscaleService.FirewallRuleName(25565, GameTunnelProtocol.Udp));
    }

    [Fact]
    public void IsTailnetAddress_OnlyAcceptsCgnatRange()
    {
        Assert.True(TailscaleService.IsTailnetAddress("100.101.102.103"));
        Assert.False(TailscaleService.IsTailnetAddress("192.168.1.10"));
        Assert.False(TailscaleService.IsTailnetAddress(""));
    }

    // ══════════════════ API ══════════════════

    [Fact]
    public void BuildCreateKeyBody_HasExpectedCapabilities()
    {
        var body = TailscaleApiService.BuildCreateKeyBody(true, true, true, 3600, "Tuba Tunnel - Java 25565");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var create = doc.RootElement.GetProperty("capabilities").GetProperty("devices").GetProperty("create");

        Assert.True(create.GetProperty("reusable").GetBoolean());
        Assert.True(create.GetProperty("ephemeral").GetBoolean());
        Assert.True(create.GetProperty("preauthorized").GetBoolean());
        // 未指定标签时不下发 tags（ACL 未定义标签会 400）
        Assert.False(create.TryGetProperty("tags", out _));
        Assert.Equal(3600, doc.RootElement.GetProperty("expirySeconds").GetInt32());
        Assert.Equal("Tuba Tunnel - Java 25565", doc.RootElement.GetProperty("description").GetString());
    }

    /// <summary>
    /// 真实故障：描述里带中文/中文标点时服务端返回 400
    /// 「keys: description had invalid characters」，邀请码永远生成不出来。
    /// </summary>
    [Fact]
    public void BuildCreateKeyBody_StripsCharactersTailscaleRejects()
    {
        var body = TailscaleApiService.BuildCreateKeyBody(true, false, true, 7200, "图吧工具箱 · 我的世界（Java 版）");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var description = doc.RootElement.GetProperty("description").GetString()!;

        Assert.Equal("Java", description);
        Assert.Matches("^[A-Za-z0-9 _-]*$", description);
    }

    [Theory]
    [InlineData("图吧工具箱 · 我的世界（Java 版）", "Java")]
    [InlineData("泰拉瑞亚", "")]
    [InlineData("Tuba Tunnel - Java 25565", "Tuba Tunnel - Java 25565")]
    [InlineData("a.b:c/d!e", "a b c d e")]
    [InlineData("  前后留空  ", "")]
    [InlineData("深岩银河  Dedicated  Server", "Dedicated Server")]
    public void SanitizeKeyDescription_KeepsOnlyAllowedCharacters(string raw, string expected)
    {
        var result = TailscaleApiService.SanitizeKeyDescription(raw);
        Assert.Equal(expected, result);
        Assert.Matches("^[A-Za-z0-9 _-]*$", result);
        Assert.DoesNotContain("  ", result);
    }

    [Fact]
    public void SanitizeKeyDescription_EmptyStaysEmptyAndIsOmitted()
    {
        Assert.Equal("", TailscaleApiService.SanitizeKeyDescription("泰拉瑞亚"));
        Assert.Equal("", TailscaleApiService.SanitizeKeyDescription("   "));
        Assert.Equal("", TailscaleApiService.SanitizeKeyDescription(null));

        // 全中文游戏名清洗后为空 → 请求体里干脆不带 description（而不是留个空串）
        var body = TailscaleApiService.BuildCreateKeyBody(true, false, true, 7200, "泰拉瑞亚");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("description", out _));
    }

    [Fact]
    public void SanitizeKeyDescription_IsLengthCapped()
    {
        var result = TailscaleApiService.SanitizeKeyDescription(new string('x', 200));
        Assert.True(result.Length <= 60);
    }

    [Fact]
    public void BuildCreateKeyBody_IncludesTagsWhenGiven()
    {
        var body = TailscaleApiService.BuildCreateKeyBody(true, false, false, 600, null, ["tag:game"]);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var tags = doc.RootElement.GetProperty("capabilities").GetProperty("devices").GetProperty("create").GetProperty("tags");
        Assert.Equal("tag:game", tags[0].GetString());
        Assert.False(doc.RootElement.TryGetProperty("description", out _));
    }

    [Fact]
    public void ParseAuthKeyResponse_ReadsKeyAndExpiry()
    {
        const string json = """
        {"id":"k123","key":"tskey-auth-kAbc1234567890xyz","created":"2026-09-14T00:00:00Z","expires":"2026-09-14T01:00:00Z"}
        """;
        var result = TailscaleApiService.ParseAuthKeyResponse(json);

        Assert.True(result.Ok);
        Assert.Equal("tskey-auth-kAbc1234567890xyz", result.Key);
        Assert.Equal("k123", result.KeyId);
        Assert.Equal(2026, result.Expires!.Value.Year);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"id":"k1"}""")]
    [InlineData("""{"key":"tskey-api-notanauthkey12345"}""")]
    public void ParseAuthKeyResponse_RejectsBadPayloads(string json)
    {
        var result = TailscaleApiService.ParseAuthKeyResponse(json);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void DescribeApiError_ExplainsCommonCodes()
    {
        Assert.Contains("令牌", TailscaleApiService.DescribeApiError(401, ""));
        Assert.Contains("权限", TailscaleApiService.DescribeApiError(403, ""));
        Assert.Contains("频繁", TailscaleApiService.DescribeApiError(429, ""));
        Assert.Contains("500", TailscaleApiService.DescribeApiError(500, ""));
    }

    [Fact]
    public void ParseDeviceList_ReadsDevices()
    {
        const string json = """
        {"devices":[
          {"id":"n1","name":"host.my-tailnet.ts.net","hostname":"host","os":"windows","addresses":["100.101.102.103","fd7a::1"],"online":true},
          {"id":"n2","name":"phone.my-tailnet.ts.net","hostname":"phone","os":"android","addresses":[],"lastSeen":"2026-09-13T10:00:00Z"}
        ]}
        """;
        var rows = TailscaleApiService.ParseDeviceList(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal("host", rows[0].HostName);
        Assert.Equal("100.101.102.103", rows[0].Ipv4);
        Assert.True(rows[0].Online);
        Assert.Null(rows[1].Ipv4);
        Assert.False(rows[1].Online);
        Assert.NotNull(rows[1].LastSeen);
    }

    [Fact]
    public void ParseDeviceList_TolerantOfBadPayload()
    {
        Assert.Empty(TailscaleApiService.ParseDeviceList(null));
        Assert.Empty(TailscaleApiService.ParseDeviceList("{}"));
        Assert.Empty(TailscaleApiService.ParseDeviceList("nope"));
    }

    // ══════════════════ Peer 解析 ══════════════════

    [Fact]
    public void ParsePeers_ReadsPeersWithOwnerAndConnectionKind()
    {
        const string json = """
        {
          "BackendState": "Running",
          "User": { "3124156728162092": { "ID": 3124156728162092, "LoginName": "luolan233@outlook.com" } },
          "Peer": {
            "nodekey:aaa": {
              "HostName": "DESKTOP-FRIEND",
              "DNSName": "desktop-friend.tailnet.ts.net.",
              "TailscaleIPs": ["100.101.102.103", "fd7a:115c:a1e0::1"],
              "OS": "windows", "UserID": 3124156728162092, "Online": true,
              "CurAddr": "203.0.113.7:41641", "Relay": "tok"
            },
            "nodekey:bbb": {
              "HostName": "phone",
              "DNSName": "phone.tailnet.ts.net.",
              "TailscaleIPs": ["100.101.102.104"],
              "OS": "android", "Online": true, "Relay": "hkg"
            }
          }
        }
        """;

        var rows = TailscaleService.ParsePeers(json);

        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal("DESKTOP-FRIEND", first.HostName);
        Assert.Equal("100.101.102.103", first.Ipv4);
        Assert.True(first.Online);
        Assert.True(first.IsDirect);
        Assert.Equal("luolan233@outlook.com", first.OwnerLogin);
        Assert.Contains("直连", first.Describe());

        // 没有 CurAddr 的在线设备走中继
        Assert.False(rows[1].IsDirect);
        Assert.Equal("relay", rows[1].ConnectionKindForTest());
        Assert.Contains("中继", rows[1].Describe());
    }

    [Fact]
    public void ParsePeers_TolerantOfMissingBackend()
    {
        Assert.Empty(TailscaleService.ParsePeers(null));
        Assert.Empty(TailscaleService.ParsePeers("not json"));
        Assert.Empty(TailscaleService.ParsePeers("""{"BackendState":"NoState"}"""));
        Assert.Empty(TailscaleService.ParsePeers("""{"Peer":{"a":{}}}"""));
    }

    // ══════════════════ 内置游戏档案 ══════════════════

    [Fact]
    public void Presets_AreConsistent()
    {
        var presets = GameTunnelCatalog.Presets;

        Assert.NotEmpty(presets);
        Assert.Equal(presets.Count, presets.Select(p => p.Id).Distinct().Count());
        Assert.Equal(presets.Count, presets.Select(p => p.Name).Distinct().Count());

        foreach (var preset in presets)
        {
            Assert.True(GameTunnelCatalog.IsValidPort(preset.DefaultPort), $"{preset.Id} 端口非法");
            Assert.NotEmpty(preset.HostSteps);
            Assert.NotEmpty(preset.GuestSteps);
            Assert.NotEmpty(preset.Glyph);
            Assert.False(string.IsNullOrWhiteSpace(preset.Tagline), $"{preset.Id} 缺少一句话说明");
            Assert.Contains("{host}", string.Join('\n', preset.GuestSteps));
        }
    }

    [Fact]
    public void Presets_CoverMinecraftAndTerraria()
    {
        Assert.Equal(25565, GameTunnelCatalog.FindPreset("minecraft-java")!.DefaultPort);
        Assert.Equal(7777, GameTunnelCatalog.FindPreset("terraria")!.DefaultPort);
        Assert.Equal(GameTunnelProtocol.Udp, GameTunnelCatalog.FindPreset("minecraft-bedrock")!.Protocol);
        Assert.Null(GameTunnelCatalog.FindPreset("nope"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(25565, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void IsValidPort_Validates(int port, bool expected)
        => Assert.Equal(expected, GameTunnelCatalog.IsValidPort(port));

    [Fact]
    public void ProtocolExtensions_DescribeTransports()
    {
        Assert.True(GameTunnelProtocol.Tcp.UsesTcp());
        Assert.False(GameTunnelProtocol.Tcp.UsesUdp());
        Assert.True(GameTunnelProtocol.TcpAndUdp.UsesTcp());
        Assert.True(GameTunnelProtocol.TcpAndUdp.UsesUdp());
        Assert.Equal("TCP + UDP", GameTunnelProtocol.TcpAndUdp.Describe());
    }

    // ══════════════════ 本地存储 ══════════════════

    [Fact]
    public void CustomGames_RoundTrip()
    {
        Assert.Empty(GameTunnelCatalog.LoadCustomGames());

        GameTunnelCatalog.SaveCustomGames(
        [
            new CustomGame { Id = "custom-1", Name = "测试游戏", Port = 9000, Protocol = GameTunnelProtocol.Udp }
        ]);

        var loaded = GameTunnelCatalog.LoadCustomGames();
        var game = Assert.Single(loaded);
        Assert.Equal("测试游戏", game.Name);
        Assert.Equal(9000, game.Port);
        Assert.Equal(GameTunnelProtocol.Udp, game.Protocol);
        Assert.Equal("测试游戏", GameTunnelCatalog.FindCustomGame("custom-1")!.Name);
    }

    [Fact]
    public void CustomGames_DropsInvalidEntries()
    {
        GameTunnelCatalog.SaveCustomGames(
        [
            new CustomGame { Id = "ok", Name = "合法", Port = 1234 },
            new CustomGame { Id = "bad", Name = "", Port = 1234 },
            new CustomGame { Id = "bad2", Name = "端口越界", Port = 99999 }
        ]);

        Assert.Single(GameTunnelCatalog.LoadCustomGames());
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var settings = GameTunnelCatalog.LoadSettings();
        Assert.Null(settings.ApiToken);
        Assert.True(settings.AutoFirewall);

        settings.ApiToken = "tskey-api-abcdefghijklmnop";
        settings.LastPresetId = "terraria";
        settings.LastPort = 7777;
        GameTunnelCatalog.SaveSettings(settings);

        var reloaded = GameTunnelCatalog.LoadSettings();
        Assert.Equal("tskey-api-abcdefghijklmnop", reloaded.ApiToken);
        Assert.Equal("terraria", reloaded.LastPresetId);
        Assert.Equal(7777, reloaded.LastPort);
    }

    // ══════════════════ 邀请码 ══════════════════

    [Fact]
    public void InviteCode_RoundTrips()
    {
        var info = new InviteInfo
        {
            Host = "100.101.102.103",
            Port = 25565,
            Game = "我的世界（Java 版）",
            AuthKey = "tskey-auth-kAbc1234567890xyz",
            HostName = "房主的电脑",
            Protocol = GameTunnelProtocol.Udp,
            ExpiresAt = 1_800_000_000
        };

        var code = GameTunnelInvite.Encode(info);
        Assert.StartsWith(GameTunnelInvite.CodePrefix, code);

        var decoded = GameTunnelInvite.Decode(code);

        Assert.NotNull(decoded);
        Assert.Equal(info.Host, decoded!.Host);
        Assert.Equal(info.Port, decoded.Port);
        Assert.Equal(info.Game, decoded.Game);
        Assert.Equal(info.AuthKey, decoded.AuthKey);
        Assert.Equal(info.HostName, decoded.HostName);
        Assert.Equal(GameTunnelProtocol.Udp, decoded.Protocol);
        Assert.Equal(info.ExpiresAt, decoded.ExpiresAt);
        Assert.Equal("100.101.102.103:25565", decoded.Address);
    }

    [Fact]
    public void InviteCode_DecodesFromFullInviteMessage()
    {
        var info = new InviteInfo { Host = "100.1.2.3", Port = 7777, Game = "泰拉瑞亚", AuthKey = "tskey-auth-abcdefghijklmnop" };
        var text = GameTunnelInvite.BuildInviteText(info, GameTunnelInvite.Encode(info), hasScript: true);

        var decoded = GameTunnelInvite.Decode(text);

        Assert.NotNull(decoded);
        Assert.Equal("100.1.2.3", decoded!.Host);
        Assert.Equal(7777, decoded.Port);
    }

    [Theory]
    [InlineData("100.101.102.103:25565")]
    [InlineData("  100.101.102.103:25565  ")]
    [InlineData("100.101.102.103：25565")]
    public void InviteCode_AcceptsPlainAddress(string text)
    {
        var decoded = GameTunnelInvite.Decode(text);
        Assert.NotNull(decoded);
        Assert.Equal("100.101.102.103", decoded!.Host);
        Assert.Equal(25565, decoded.Port);
        Assert.False(decoded.HasAuthKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("随便一段话")]
    [InlineData("TBG1:not-base64!!!")]
    [InlineData("100.101.102.103:99999")]
    public void InviteCode_RejectsGarbage(string text)
        => Assert.Null(GameTunnelInvite.Decode(text));

    [Fact]
    public void InviteCode_WithoutAuthKeyStillEncodes()
    {
        var code = GameTunnelInvite.Encode(new InviteInfo { Host = "100.1.2.3", Port = 25565 });
        var decoded = GameTunnelInvite.Decode(code);
        Assert.NotNull(decoded);
        Assert.False(decoded!.HasAuthKey);
        // 不含密钥时载荷里不应出现任何 tskey 字样
        Assert.DoesNotContain("tskey", Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(code[5..]))), StringComparison.OrdinalIgnoreCase);
    }

    private static string PadBase64(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };
    }

    [Fact]
    public void BuildInviteText_MentionsAddressAndCode()
    {
        var info = new InviteInfo
        {
            Host = "100.1.2.3",
            Port = 25565,
            Game = "我的世界",
            AuthKey = "tskey-auth-kAbc1234567890xyz"
        };
        var code = GameTunnelInvite.Encode(info);

        var text = GameTunnelInvite.BuildInviteText(info, code, hasScript: true);

        Assert.Contains("100.1.2.3:25565", text);
        Assert.Contains(code, text);
        Assert.Contains("一键加入联机.cmd", text);
        Assert.Contains("我的世界", text);
    }

    [Fact]
    public void BuildInviteText_WithoutAuthKeyTellsFriendTheyAreAlreadyIn()
    {
        var info = new InviteInfo { Host = "100.1.2.3", Port = 25565 };
        var text = GameTunnelInvite.BuildInviteText(info, null, hasScript: false);

        Assert.Contains("直接打开游戏", text);
        Assert.DoesNotContain("tskey", text);
    }

    // ══════════════════ 一键加入脚本 ══════════════════

    [Fact]
    public void BuildScript_ContainsInstallAndJoinFlow()
    {
        var script = GameTunnelInvite.BuildScript(new InviteInfo
        {
            Host = "100.101.102.103",
            Port = 25565,
            Game = "我的世界（Java 版）",
            AuthKey = "tskey-auth-kAbc1234567890xyz"
        });

        Assert.Contains("set \"HOSTADDR=100.101.102.103\"", script);
        Assert.Contains("set \"PORT=25565\"", script);
        Assert.Contains("set \"AUTHKEY=tskey-auth-kAbc1234567890xyz\"", script);
        Assert.Contains("set \"GAME=我的世界（Java 版）\"", script);

        // 自提权
        Assert.Contains("net session >nul 2>&1", script);
        Assert.Contains("Start-Process -FilePath '%~f0' -Verb RunAs", script);

        // 按对方架构选安装包
        Assert.Contains("set \"ARCH=amd64\"", script);
        Assert.Contains("if /i \"%PROCESSOR_ARCHITECTURE%\"==\"ARM64\" set \"ARCH=arm64\"", script);
        Assert.Contains("tailscale-setup-latest-%ARCH%.msi", script);

        // 静默安装 + 加入网络
        Assert.Contains("msiexec /i \"%MSI%\" /qn /norestart", script);
        Assert.Contains("up --auth-key=%AUTHKEY% --accept-dns=false --unattended", script);

        // 托盘客户端必须常驻：没有它，tailscale up 一退出守护进程就会断开，
        // 表现为「入网看起来成功、状态又弹回未连接」
        Assert.DoesNotContain("TS_NOLAUNCH", script);
        Assert.Contains("tasklist /FI \"IMAGENAME eq tailscale-ipn.exe\"", script);
        Assert.Contains("explorer.exe \"%TSIPN%\"", script);

        // 连接测试与最终提示
        Assert.Contains("ping --c 2 --timeout 5s %HOSTADDR%", script);
        Assert.Contains("%HOSTADDR%:%PORT%", script);

        // GBK 脚本不能用 chcp 切码页
        Assert.DoesNotContain("chcp", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildScript_WithoutAuthKeyFallsBackToManualLogin()
    {
        var script = GameTunnelInvite.BuildScript(new InviteInfo { Host = "100.64.0.5", Port = 7777, Game = "泰拉瑞亚" });

        Assert.Contains("set \"AUTHKEY=\"", script);
        Assert.Contains("goto :manual_login", script);
        Assert.Contains("Log in", script);
        // 无密钥时脚本里不该残留任何密钥
        Assert.DoesNotContain("tskey-", script);
    }

    [Fact]
    public void BuildScript_RejectsMalformedAuthKey()
    {
        var script = GameTunnelInvite.BuildScript(new InviteInfo
        {
            Host = "100.64.0.5",
            Port = 25565,
            AuthKey = "tskey-api-wrongkindabcdefgh"
        });

        Assert.Contains("set \"AUTHKEY=\"", script);
        Assert.DoesNotContain("wrongkind", script);
    }

    [Theory]
    [InlineData("我的世界（Java 版）", "我的世界（Java 版）-一键加入联机.cmd")]
    [InlineData("a/b:c*d", "a_b_c_d-一键加入联机.cmd")]
    [InlineData("", "游戏-一键加入联机.cmd")]
    public void ScriptFileName_Sanitizes(string input, string expected)
        => Assert.Equal(expected, GameTunnelInvite.ScriptFileName(input));

    [Fact]
    public void WriteScript_UsesGbkWithoutBom()
    {
        var info = new InviteInfo { Host = "100.101.102.103", Port = 25565, Game = "我的世界" };
        var path = GameTunnelInvite.WriteScriptToDataDir(info);

        Assert.True(File.Exists(path));
        Assert.Contains("100.101.102.103", File.ReadAllText(path));

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var gbkMarker = Encoding.GetEncoding(936).GetBytes("一键加入联机");
        Assert.True(ContainsSequence(bytes, gbkMarker));
    }

    /// <summary>
    /// 批处理必须是 CRLF：脚本正文来自源码里的多行字符串，源码存成 LF 时正文就是 LF，
    /// 这时 cmd.exe 会把命令行拆成碎片执行（「'o' 不是内部或外部命令」「系统找不到指定的路径」）。
    /// </summary>
    [Fact]
    public void WriteScript_UsesCrlfLineEndings()
    {
        var info = new InviteInfo { Host = "100.101.102.103", Port = 25565, Game = "我的世界" };
        var path = GameTunnelInvite.WriteScriptToDataDir(info);

        var bytes = File.ReadAllBytes(path);
        Assert.Contains((byte)13, bytes);

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 10) continue;
            Assert.True(i > 0 && bytes[i - 1] == 13, $"第 {i} 字节是裸 LF——cmd.exe 会拆碎命令行");
        }
    }

    [Fact]
    public void ToCrlf_NormalizesMixedLineEndings()
    {
        Assert.Equal("a\r\nb\r\nc", GameTunnelInvite.ToCrlf("a\nb\r\nc"));
        Assert.Equal("a\r\nb", GameTunnelInvite.ToCrlf("a\rb"));
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}

/// <summary>API 凭据输入与错误提示（用户实测踩过的坑：粘贴不进去、400 只显示状态码）。</summary>
public class TailscaleApiCredentialTests
{
    [Theory]
    [InlineData("tskey-api-kAbc1234567890abcdefghij", "tskey-api-kAbc1234567890abcdefghij")]
    [InlineData("   tskey-api-kAbc1234567890abcdefghij  ", "tskey-api-kAbc1234567890abcdefghij")]
    [InlineData("Tailscale API key: tskey-api-kAbc1234567890abcdefghij\n", "tskey-api-kAbc1234567890abcdefghij")]
    [InlineData("tskey-api-kAbc1234567890abcdefghij，这是我的令牌", "tskey-api-kAbc1234567890abcdefghij")]
    public void ExtractKey_PullsTokenOutOfNoisyClipboardText(string clipboard, string expected)
    {
        Assert.Equal(expected, TailscaleApiService.ExtractKey(clipboard, "tskey-api-"));
    }

    [Fact]
    public void ExtractKey_RejectsWrongKindAndPartialText()
    {
        // 授权密钥里不能抠出 API 令牌
        Assert.Null(TailscaleApiService.ExtractKey("tskey-auth-kAbc1234567890abcdefghij", "tskey-api-"));
        // 控制台页面上被截断显示的令牌（带省略号）不能当完整令牌用
        Assert.Null(TailscaleApiService.ExtractKey("tskey-api-kAbc1234…", "tskey-api-"));
        Assert.Null(TailscaleApiService.ExtractKey(null, "tskey-api-"));
        Assert.Null(TailscaleApiService.ExtractKey("这里没有密钥", "tskey-api-"));
    }

    [Fact]
    public void ExtractKey_FindsAuthKeyInsideInviteText()
    {
        const string clipboard = "欢迎联机！授权密钥 tskey-auth-kZzzz9876543210abcdefghij 有效期 2 小时";
        Assert.Equal("tskey-auth-kZzzz9876543210abcdefghij", TailscaleApiService.ExtractKey(clipboard, "tskey-auth-"));
    }

    [Fact]
    public void DescribeApiError_AlwaysCarriesServerMessage()
    {
        // 400 曾经只显示「请求失败（HTTP 400）」，用户完全不知道为什么被拒
        var message = TailscaleApiService.DescribeApiError(400, """{"message":"requested tags [tag:foo] are invalid or not permitted"}""");
        Assert.Contains("tag:foo", message);
        Assert.Contains("标签", message);

        var unauthorized = TailscaleApiService.DescribeApiError(401, """{"message":"API token invalid"}""");
        Assert.Contains("API token invalid", unauthorized);
        Assert.Contains("重新生成", unauthorized);

        var forbidden = TailscaleApiService.DescribeApiError(403, """{"message":"insufficient permissions"}""");
        Assert.Contains("insufficient permissions", forbidden);
        Assert.Contains("Owner", forbidden);
    }

    [Fact]
    public void ExtractServerMessage_HandlesNonJsonAndHtml()
    {
        Assert.Equal("plain failure", TailscaleApiService.ExtractServerMessage("plain failure"));
        Assert.Null(TailscaleApiService.ExtractServerMessage("<html><body>502 Bad Gateway</body></html>"));
        Assert.Null(TailscaleApiService.ExtractServerMessage(null));

        var message = TailscaleApiService.DescribeApiError(500, "<html>oops</html>");
        Assert.Contains("Tailscale 服务端出错", message);
        Assert.DoesNotContain("<html>", message);
    }

    [Fact]
    public void DescribeApiError_400WithoutServerMessageStillGivesCauses()
    {
        var message = TailscaleApiService.DescribeApiError(400, "");
        Assert.Contains("标签", message);
        Assert.Contains("90 天", message);
    }
}

/// <summary>
/// 两种邀请方式的口径：邀请码/脚本 = 直接加入（预授权，无需任何人同意）；
/// 纯地址 = 前提是双方已在同一个 Tailscale 网络。
/// 这套说法在房主侧、朋友侧、教程、邀请文案里必须一致。
/// </summary>
public class GameTunnelInviteCopyTests
{
    [Fact]
    public void DirectPathsSayNoApprovalNeeded()
    {
        Assert.Contains("不需要你点批准", GameTunnelInviteCopy.InviteCodeDirect);
        Assert.Contains("不需要任何人同意", GameTunnelInviteCopy.ScriptDirect);
    }

    [Fact]
    public void AddressOnlyPreconditionStatesTheNetworkRequirement()
    {
        Assert.Contains("同一个 Tailscale 网络", GameTunnelInviteCopy.AddressOnlyPrecondition);
        // 不能把它写得像邀请码一样即点即用
        Assert.DoesNotContain("直接加入你的网络", GameTunnelInviteCopy.AddressOnlyPrecondition);
        // 必须给替代方案
        Assert.Contains("邀请码", GameTunnelInviteCopy.AddressOnlyPrecondition);
    }

    [Fact]
    public void AddressOnlyRemedyPointsBackToInviteCode()
    {
        Assert.Contains("不在同一个 Tailscale 网络", GameTunnelInviteCopy.AddressOnlyRemedy);
        Assert.Contains("邀请码", GameTunnelInviteCopy.AddressOnlyRemedy);
    }

    [Fact]
    public void InviteText_WithKeySaysNoApprovalNeeded()
    {
        var text = GameTunnelInvite.BuildInviteText(
            new InviteInfo { Host = "100.80.223.82", Port = 25565, Game = "我的世界", AuthKey = "tskey-auth-kAbc1234567890xyz" },
            inviteCode: "TBG1:abc",
            hasScript: true);

        Assert.Contains("不用等我批准", text);
        Assert.Contains("不需要任何人同意", text);
        Assert.DoesNotContain("已经和我处在同一个 Tailscale 网络里", text);
    }

    [Fact]
    public void InviteText_AddressOnlyWarnsAboutThePrecondition()
    {
        var text = GameTunnelInvite.BuildInviteText(
            new InviteInfo { Host = "100.80.223.82", Port = 7777, Game = "泰拉瑞亚" },
            inviteCode: null,
            hasScript: false);

        Assert.Contains("同一个 Tailscale 网络", text);
        Assert.Contains("光有地址是连不上的", text);
        Assert.DoesNotContain("直接加入", text);
    }
}

/// <summary>游戏 Logo：素材来源、镜像备份、缓存命名、拿不到时的兜底。</summary>
public class GameLogoServiceTests
{
    [Fact]
    public void EveryPresetHasAnOfficialLogoUrl()
    {
        foreach (var preset in GameTunnelCatalog.Presets)
        {
            Assert.NotEmpty(preset.LogoUrls);
            foreach (var url in preset.LogoUrls)
            {
                Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), $"{preset.Id} 的 Logo 地址无效：{url}");
                Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
            }
        }
    }

    [Theory]
    [InlineData("terraria", 105600)]
    [InlineData("tmodloader", 1281930)]
    [InlineData("stardew", 413150)]
    [InlineData("dst", 322330)]
    [InlineData("valheim", 892970)]
    [InlineData("palworld", 1623730)]
    [InlineData("starbound", 211820)]
    [InlineData("factorio", 427520)]
    public void SteamPresetsPointAtTheirOwnAppId(string id, int appId)
    {
        var preset = GameTunnelCatalog.FindPreset(id);
        Assert.NotNull(preset);
        Assert.Contains(preset!.LogoUrls, url => url.Contains($"/steam/apps/{appId}/logo.png", StringComparison.Ordinal));
    }

    [Fact]
    public void MinecraftPresetsUseTheOfficialSiteLogo()
    {
        foreach (var id in new[] { "minecraft-java", "minecraft-bedrock" })
        {
            var preset = GameTunnelCatalog.FindPreset(id);
            Assert.NotNull(preset);
            Assert.All(preset!.LogoUrls, url => Assert.StartsWith("https://www.minecraft.net/", url));
        }
    }

    [Fact]
    public void SteamLogosShipAMirrorForMainlandNetworks()
    {
        var preset = GameTunnelCatalog.FindPreset("terraria");
        Assert.NotNull(preset);
        Assert.True(preset!.LogoUrls.Count >= 2, "Steam Logo 要有两个 CDN 互为备份");
        Assert.Contains(preset.LogoUrls, url => url.Contains("akamai.steamstatic.com", StringComparison.Ordinal));
    }

    [Fact]
    public void CachePathFollowsSourceExtensionAndSanitizesId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tubalogo");

        Assert.Equal(
            Path.Combine(dir, "dst.png"),
            GameLogoService.CachePath(dir, "dst", "https://cdn.cloudflare.steamstatic.com/steam/apps/322330/logo.png"));

        Assert.Equal(
            Path.Combine(dir, "minecraft-java.svg"),
            GameLogoService.CachePath(dir, "minecraft-java", "https://www.minecraft.net/content/dam/minecraftnet/games/minecraft/logos/logo-minecraft.svg"));

        Assert.Equal(
            Path.Combine(dir, "custom_1.png"),
            GameLogoService.CachePath(dir, "custom:1", "https://example.com/a/logo.png"));
    }

    [Fact]
    public void CustomGamesHaveNoLogoAndKeepTheGlyph()
    {
        var card = TunnelGameCard.FromCustom(new CustomGame { Id = "custom-1", Name = "自建服", Port = 25565 });

        Assert.Empty(card.LogoUrls);
        Assert.Null(card.Logo);
        Assert.Equal("\uE7FC", card.Glyph);
    }

    [Fact]
    public void CardFallsBackToGlyphUntilALogoArrives()
    {
        var card = TunnelGameCard.FromPreset(GameTunnelCatalog.FindPreset("terraria")!);

        Assert.Null(card.Logo);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, card.GlyphVisibility);
    }
}

/// <summary>status --json 顺带解析出设备列表（主页「同一虚拟网络」那行用它，不额外起进程）。</summary>
public class TailscaleStatusPeersTests
{
    [Fact]
    public void ParseStatusJson_CarriesPeersFromTheSamePayload()
    {
        const string json = """
        {
          "BackendState": "Running",
          "Self": { "HostName": "my-laptop", "TailscaleIPs": ["100.64.0.1"], "UserID": 1 },
          "User": { "1": { "LoginName": "me@example.com" } },
          "Peer": {
            "nodekey:aaa": {
              "HostName": "DESKTOP-A",
              "DNSName": "desktop-a.tailnet.ts.net.",
              "TailscaleIPs": ["100.64.0.2"],
              "Online": true,
              "CurAddr": "203.0.113.5:41641",
              "OS": "windows",
              "UserID": 1
            },
            "nodekey:bbb": {
              "HostName": "iphone",
              "DNSName": "iphone.tailnet.ts.net.",
              "TailscaleIPs": ["100.64.0.3"],
              "Online": false,
              "Relay": "hkg",
              "UserID": 1
            }
          }
        }
        """;

        var status = TailscaleService.ParseStatusJson(json);

        Assert.NotNull(status);
        Assert.True(status!.IsReady);
        Assert.Equal(2, status.Peers.Count);

        // 在线的排在前面
        var desktop = status.Peers[0];
        Assert.Equal("DESKTOP-A", desktop.HostName);
        Assert.Equal("100.64.0.2", desktop.Ipv4);
        Assert.True(desktop.Online);
        Assert.True(desktop.IsDirect);

        var phone = status.Peers[1];
        Assert.Equal("iphone", phone.HostName);
        Assert.False(phone.Online);
        Assert.False(phone.IsDirect);
        Assert.Equal("离线", phone.Describe());
    }

    [Fact]
    public void ParseStatusJson_WithoutPeersIsEmptyNotFailing()
    {
        var status = TailscaleService.ParseStatusJson("""{"BackendState":"Stopped"}""");

        Assert.NotNull(status);
        Assert.Empty(status!.Peers);
    }
}

internal static class PeerTestExtensions
{
    /// <summary>测试辅助：把连接方式归一化成 relay / direct / offline。</summary>
    public static string ConnectionKindForTest(this TailscalePeer peer)
        => !peer.Online ? "offline" : peer.IsDirect ? "direct" : "relay";
}
