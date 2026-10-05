using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TubaWinUi3.Models;

namespace TubaWinUi3.Services;

/// <summary>安装向导用的进度快照。</summary>
public sealed record InstallerProgress(string Stage, double Percent, string Detail);

public sealed record InstallResult(bool Ok, string Message);

public sealed record JoinResult(bool Ok, string Message);

/// <summary>
/// Tailscale 的全部业务能力：安装、登录、状态、加入网络、网络体检、防火墙放行。
/// 解析类方法全部是纯函数（internal 供单测），需要进程调用的部分集中在下方。
/// </summary>
public static class TailscaleService
{
    public const string OfficialBase = "https://pkgs.tailscale.com/stable";
    public const string PackagesIndexUrl = OfficialBase + "/?mode=json";
    public const string DownloadPageUrl = "https://tailscale.com/download/windows";

    /// <summary>管理控制台里唯一需要引导用户打开的两个页面（其它一律不引导）。</summary>
    public const string KeysUrl = "https://login.tailscale.com/admin/settings/keys";
    public const string MachinesUrl = "https://login.tailscale.com/admin/machines";

    public static bool IsInstalled => TailscaleCli.IsInstalled;

    public static string? ExePath => TailscaleCli.ExePath;

    /// <summary>MSI 资产名里的架构后缀（tailscale-setup-&lt;ver&gt;-&lt;arch&gt;.msi）。</summary>
    public static string ArchSuffix => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => "amd64"
    };

    /// <summary>ARM64 的 MSI 若拉取失败时回退用的架构（官方文档建议 ARM64 可用 x86 版）。</summary>
    public static string FallbackArchSuffix => ArchSuffix == "x86" ? "amd64" : "x86";

    public static string GetInstallerDir()
    {
        var dir = Path.Combine(GameTunnelCatalog.DataDir, "Installer");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string InstallerFileName(string version, string arch) => $"tailscale-setup-{version}-{arch}.msi";

    public static string InstallerUrl(string version, string arch)
        => $"{OfficialBase}/tailscale-setup-{version}-{arch}.msi";

    // ══════════════════════ 版本 ══════════════════════

    /// <summary>从官方包索引取最新稳定版号。</summary>
    public static string? ParsePackagesVersion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var key in new[] { "Version", "MSIsVersion", "ExesVersion" })
            {
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } version)
                {
                    return version.Trim();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    public static async Task<string?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = HttpClientFactory.CreateIpv4Preferred(TimeSpan.FromSeconds(20));
            var json = await client.GetStringAsync(PackagesIndexUrl, ct).ConfigureAwait(false);
            return ParsePackagesVersion(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取本机已安装客户端的版本号（tailscale version 的第一行）。</summary>
    public static string? ParseVersionText(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            // 版本行形如 1.102.4（后面可能跟 -t3caf7d9e7 一类的构建后缀）
            var token = trimmed.Split(' ', '\t')[0];
            return token.Length > 0 ? token : null;
        }
        return null;
    }

    public static async Task<string?> GetLocalVersionAsync(CancellationToken ct = default)
    {
        var result = await TailscaleCli.RunAsync(["version"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return ParseVersionText(result.Combined);
    }

    // ══════════════════════ 下载与安装 ══════════════════════

    /// <summary>MSI 是复合文档格式，用魔数 + 体积双重要求排除错误页/半截文件。</summary>
    public static bool IsUsableInstaller(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length < 5L * 1024 * 1024) return false;

            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) < 8) return false;
            return header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0
                   && header[4] == 0xA1 && header[5] == 0xB1 && header[6] == 0x1A && header[7] == 0xE1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下载指定版本的安装包（已存在且校验通过则直接复用）。
    /// 返回 MSI 的完整路径。
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        string version,
        IProgress<InstallerProgress>? progress,
        CancellationToken ct = default)
    {
        // 同一时刻只允许一个下载：重复触发（例如安装中途关掉向导再打开）会互相踩 .part 临时文件
        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DownloadInstallerCoreAsync(version, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static readonly SemaphoreSlim _downloadGate = new(1, 1);

    private static async Task<string> DownloadInstallerCoreAsync(
        string version,
        IProgress<InstallerProgress>? progress,
        CancellationToken ct)
    {
        var primaryArch = ArchSuffix;
        var target = Path.Combine(GetInstallerDir(), InstallerFileName(version, primaryArch));
        if (IsUsableInstaller(target))
        {
            progress?.Report(new InstallerProgress(GameTunnelTexts.T("下载"), 100, GameTunnelTexts.T("已存在可用安装包")));
            return target;
        }

        var candidates = new List<(string Url, string Path)>
        {
            (InstallerUrl(version, primaryArch), target)
        };
        if (FallbackArchSuffix != primaryArch)
        {
            candidates.Add((
                InstallerUrl(version, FallbackArchSuffix),
                Path.Combine(GetInstallerDir(), InstallerFileName(version, FallbackArchSuffix))));
        }

        Exception? lastError = null;
        foreach (var (url, path) in candidates)
        {
            try
            {
                if (IsUsableInstaller(path))
                {
                    progress?.Report(new InstallerProgress(GameTunnelTexts.T("下载"), 100, GameTunnelTexts.T("已存在可用安装包")));
                    return path;
                }

                await DownloadFileAsync(url, path, progress, ct).ConfigureAwait(false);
                if (IsUsableInstaller(path)) return path;

                TryDelete(path);
                lastError = new InvalidOperationException(GameTunnelTexts.T("下载到的文件不是有效的安装包"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(path);
            }
        }

        throw lastError ?? new InvalidOperationException(GameTunnelTexts.T("下载失败"));
    }

    private static async Task DownloadFileAsync(
        string url,
        string target,
        IProgress<InstallerProgress>? progress,
        CancellationToken ct)
    {
        var temp = target + ".part";
        TryDelete(temp);

        using var client = HttpClientFactory.CreateIpv4Preferred(TimeSpan.FromMinutes(10));
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        var received = 0L;
        var stopwatch = Stopwatch.StartNew();

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = File.Create(temp))
        {
            var buffer = new byte[81920];
            var lastReport = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (stopwatch.ElapsedMilliseconds - lastReport < 200) continue;
                lastReport = stopwatch.ElapsedMilliseconds;

                var percent = total > 0 ? received * 100.0 / total : 0;
                var speed = stopwatch.Elapsed.TotalSeconds > 0 ? received / stopwatch.Elapsed.TotalSeconds : 0;
                var detail = total > 0
                    ? $"{received / 1048576.0:F1} MB / {total / 1048576.0:F1} MB · {speed / 1048576.0:F1} MB/s"
                    : $"{received / 1048576.0:F1} MB · {speed / 1048576.0:F1} MB/s";
                progress?.Report(new InstallerProgress(GameTunnelTexts.T("下载"), percent, detail));
            }
        }

        File.Move(temp, target, overwrite: true);
        progress?.Report(new InstallerProgress(GameTunnelTexts.T("下载"), 100, GameTunnelTexts.T("下载完成")));
    }

    /// <summary>静默安装 MSI（本应用始终以管理员身份运行，无需再提权）。</summary>
    public static async Task<InstallResult> InstallAsync(string msiPath, CancellationToken ct = default)
    {
        if (!IsUsableInstaller(msiPath))
            return new InstallResult(false, GameTunnelTexts.T("安装包不可用，请重新下载"));

        // 刻意不传 TS_NOLAUNCH：安装器会按正常流程把托盘客户端拉起来，
        // 让 tailscaled 立刻有一个常驻客户端（否则它连上就会自己断开）。
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "/i", msiPath, "/qn", "/norestart" })
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new InstallResult(false, GameTunnelTexts.T("无法启动安装程序"));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(8));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            // 0 = 成功，3010 = 成功但需要重启
            if (process.ExitCode is 0 or 3010)
            {
                TailscaleCli.Invalidate();

                // 装上客户端只是第一步：必须把托盘程序拉起来当常驻客户端，
                // 否则守护进程会在没有任何客户端时立刻断开（详见 EnsureTrayRunningAsync）。
                await EnsureTrayRunningAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);

                return new InstallResult(true, GameTunnelTexts.T("安装完成"));
            }

            return new InstallResult(false, process.ExitCode switch
            {
                1602 => GameTunnelTexts.T("安装被取消"),
                1603 => GameTunnelTexts.T("安装过程中出错（可能已有其它版本正在安装）"),
                1618 => GameTunnelTexts.T("另一个安装程序正在运行，请稍后重试"),
                1638 => GameTunnelTexts.T("已安装更新版本的 Tailscale"),
                _ => GameTunnelTexts.TSub($"安装失败（错误码 {process.ExitCode}）")
            });
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, GameTunnelTexts.T("安装超时或被取消"));
        }
        catch (Exception ex)
        {
            return new InstallResult(false, ex.Message);
        }
    }

    /// <summary>安装完成后等待 tailscale.exe 出现（安装器收尾需要一点时间）。</summary>
    public static async Task<bool> WaitForCliAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            TailscaleCli.Invalidate();
            if (TailscaleCli.IsInstalled) return true;
            try { await Task.Delay(700, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ══════════════════════ 状态 ══════════════════════

    public static async Task<TailscaleStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        var result = await TailscaleCli.RunAsync(["status", "--json"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return ParseStatusJson(result.Combined);
    }

    public static TailscaleStatus? ParseStatusJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("BackendState", out var backend)) return null;

            string? ipv4 = null, ipv6 = null, hostName = null, dnsName = null, userId = null;
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                hostName = GetString(self, "HostName");
                dnsName = TrimTrailingDot(GetString(self, "DNSName"));
                if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ip in ips.EnumerateArray())
                    {
                        if (ip.ValueKind != JsonValueKind.String) continue;
                        var value = ip.GetString();
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        if (value.Contains(':') && ipv6 is null) ipv6 = value;
                        else if (!value.Contains(':') && ipv4 is null) ipv4 = value;
                    }
                }
                if (self.TryGetProperty("UserID", out var uid))
                {
                    userId = uid.ValueKind switch
                    {
                        JsonValueKind.Number => uid.GetInt64().ToString(),
                        JsonValueKind.String => uid.GetString(),
                        _ => null
                    };
                }
            }

            // 顶层也可能直接给 IP（老版本），兜底取一次
            if (ipv4 is null && root.TryGetProperty("TailscaleIPs", out var topIps) && topIps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ip in topIps.EnumerateArray())
                {
                    if (ip.ValueKind == JsonValueKind.String && ip.GetString() is { Length: > 0 } value && !value.Contains(':'))
                    {
                        ipv4 = value;
                        break;
                    }
                }
            }

            var loginName = LookupLoginName(root, userId);

            string? tailnetName = null;
            if (root.TryGetProperty("CurrentTailnet", out var tailnet) && tailnet.ValueKind == JsonValueKind.Object)
            {
                tailnetName = GetString(tailnet, "Name") ?? GetString(tailnet, "MagicDNSSuffix");
            }

            var health = new List<string>();
            if (root.TryGetProperty("Health", out var healthElement) && healthElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in healthElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                        health.Add(text);
                }
            }

            return new TailscaleStatus
            {
                BackendState = backend.ValueKind == JsonValueKind.String ? backend.GetString() ?? "" : "",
                Version = GetString(root, "Version"),
                AuthUrl = GetString(root, "AuthURL"),
                Ipv4 = ipv4,
                Ipv6 = ipv6,
                HostName = hostName,
                DnsName = dnsName,
                LoginName = loginName,
                TailnetName = tailnetName,
                Peers = ParsePeerMap(root),
                HaveNodeKey = root.TryGetProperty("HaveNodeKey", out var haveKey) && haveKey.ValueKind == JsonValueKind.True,
                Health = health
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? LookupLoginName(JsonElement root, string? userId)
    {
        try
        {
            if (userId is null) return null;
            if (!root.TryGetProperty("User", out var users) || users.ValueKind != JsonValueKind.Object) return null;
            if (!users.TryGetProperty(userId, out var user) || user.ValueKind != JsonValueKind.Object) return null;
            return GetString(user, "LoginName") ?? GetString(user, "DisplayName");
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        try
        {
            if (!element.TryGetProperty(property, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static string? TrimTrailingDot(string? value)
        => value is { Length: > 0 } && value.EndsWith('.') ? value[..^1] : value;

    /// <summary>取本机 Tailscale IPv4（未登录/未连接时返回 null）。</summary>
    public static async Task<string?> GetIpv4Async(CancellationToken ct = default)
    {
        var result = await TailscaleCli.RunAsync(["ip", "-4"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (result.Ok)
        {
            // 未登录时这一行是 "no current Tailscale IPs; state: NeedsLogin"，必须按真实地址校验
            var first = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (first is not null && System.Net.IPAddress.TryParse(first, out _)) return first;
        }

        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        return status?.Ipv4;
    }

    public static bool IsTailnetAddress(string? value)
        => value is { Length: > 0 } && value.StartsWith("100.", StringComparison.Ordinal);

    /// <summary>已登录但处于 Stopped 时重新连接（等价于托盘的「Connect」）。</summary>
    public static async Task<InstallResult> BringUpAsync(CancellationToken ct = default)
    {
        // 必须有常驻客户端，否则 up 一退出守护进程立刻又断开
        await EnsureTrayRunningAsync(null, ct).ConfigureAwait(false);

        var result = await TailscaleCli.RunAsync(["up"], TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        if (result.Ok) return new InstallResult(true, GameTunnelTexts.T("已连接"));

        var line = result.FirstLine(GameTunnelTexts.T("未能连接"));
        // up 若因需要登录而失败，交给登录流程处理
        return new InstallResult(false, line);
    }

    /// <summary>轮询等待进入 Running 且拿到 IP。</summary>
    public static async Task<TailscaleStatus?> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = await GetStatusAsync(ct).ConfigureAwait(false);
            if (status?.IsReady == true) return status;
            try { await Task.Delay(1200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    // ══════════════════════ 登录 ══════════════════════

    /// <summary>从登录输出里挑出设备授权地址（只认登录域名下的 /a/ 链接）。</summary>
    public static string? ExtractAuthUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var token in text.Split([' ', '\n', '\r', '\t', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim().TrimEnd('.', ',', ')', ']', '）');
            if (candidate.StartsWith("https://login.tailscale.com/", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("https://controlplane.tailscale.com/", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        return null;
    }

    // ══════════════════════ 托盘客户端（必须常驻） ══════════════════════

    /// <summary>托盘程序 tailscale-ipn.exe 是否在运行。</summary>
    public static bool IsTrayRunning
    {
        get
        {
            try
            {
                return Process.GetProcessesByName("tailscale-ipn").Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 确保托盘程序在运行。<b>它不是可有可无的界面</b>：tailscaled 需要有常驻客户端
    /// 才维持 WantRunning=true。没有常驻客户端时，守护进程只在 CLI 临时连上来的那几十毫秒里
    /// 尝试连接，CLI 一退出立刻 "disconnecting Tailscale" 并掐掉正在进行的登录——
    /// 典型的现场是「浏览器里授权成功了，客户端却永远卡在启动、状态反复退回未登录」。
    /// </summary>
    public static async Task<bool> EnsureTrayRunningAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (IsTrayRunning) return true;

        var tray = TailscaleCli.TrayPath;
        if (tray is null) return false;

        // 本应用以管理员身份运行，而 Tailscale 图形客户端必须跑在普通用户会话里；
        // 借 explorer 拉起可以自然降到中等完整性级别（直接 Process.Start 会带上管理员令牌，
        // 图形客户端会拒绝以管理员身份运行）。
        var started = false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{tray}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            started = true;
        }
        catch
        {
        }

        if (!started)
        {
            try
            {
                Process.Start(new ProcessStartInfo(tray) { UseShellExecute = true });
                started = true;
            }
            catch
            {
                return false;
            }
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (IsTrayRunning) return true;
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return IsTrayRunning;
            }
        }

        return IsTrayRunning;
    }

    /// <summary>
    /// 发起浏览器登录并等待完成。授权地址一旦出现就通过 <paramref name="onAuthUrl"/> 回传
    /// （tailscale 会自己打开浏览器，我们额外展示地址以防浏览器没弹出来）。
    /// <paramref name="onProgress"/> 每次轮询回传一句「当前状态」，用于让用户看到还在等什么。
    /// </summary>
    public static async Task<bool> LoginAsync(
        Action<string>? onAuthUrl,
        Action<string>? onProgress = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var exe = TailscaleCli.ExePath;
        if (exe is null) return false;

        var budget = timeout ?? TimeSpan.FromMinutes(5);

        // 先把托盘客户端拉起来当常驻客户端。否则登录只靠 tailscale login 这个短命进程撑着，
        // 它一退出守护进程就断开，浏览器里刚授权好的登录会被立刻取消。
        try { onProgress?.Invoke(GameTunnelTexts.T("正在准备 Tailscale 客户端…")); } catch { }
        await EnsureTrayRunningAsync(null, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("login");

        Process? process = null;
        try
        {
            process = Process.Start(psi);
        }
        catch
        {
            return false;
        }

        if (process is null) return false;

        var urlReported = false;
        void Scan(string? line)
        {
            if (urlReported || string.IsNullOrWhiteSpace(line)) return;
            var url = ExtractAuthUrl(line);
            if (url is null) return;
            urlReported = true;
            try { onAuthUrl?.Invoke(url); } catch { }
        }

        process.OutputDataReceived += (_, e) => Scan(e.Data);
        process.ErrorDataReceived += (_, e) => Scan(e.Data);

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var started = DateTime.UtcNow;
            var deadline = started + budget;
            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) break;

                var status = await GetStatusAsync(ct).ConfigureAwait(false);
                if (status?.IsReady == true)
                {
                    // 登录刚完成时状态还会抖一下：确认稳定再算成功，
                    // 否则界面会出现「闪一下登录成功又退回未登录」
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    if ((await GetStatusAsync(ct).ConfigureAwait(false))?.IsReady == true) return true;
                }

                // 有些版本登录地址只在状态里给出，进程未输出时兜底读一次
                if (!urlReported && status?.AuthUrl is { Length: > 0 } authUrl)
                {
                    urlReported = true;
                    try { onAuthUrl?.Invoke(authUrl); } catch { }
                }

                if (onProgress is not null)
                {
                    var waited = (int)(DateTime.UtcNow - started).TotalSeconds;
                    var state = status is null ? GameTunnelTexts.T("读取状态中") : status.DescribeState();
                    try { onProgress(GameTunnelTexts.TSub($"{state} · 已等待 {waited} 秒")); } catch { }
                    if (!urlReported && state == "未登录")
                    {
                        try { onProgress(GameTunnelTexts.TSub($"正在等待浏览器授权 · 已等待 {waited} 秒")); } catch { }
                    }
                }

                try { await Task.Delay(1200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            return (await GetStatusAsync(ct).ConfigureAwait(false))?.IsReady == true;
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            process.Dispose();
        }
    }

    public static async Task<bool> LogoutAsync(CancellationToken ct = default)
    {
        var result = await TailscaleCli.RunAsync(["logout"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return result.Ok;
    }

    /// <summary>用授权密钥把本机加入主机的 tailnet。会切换当前登录（调用方必须先征得用户同意）。</summary>
    public static async Task<JoinResult> JoinTailnetAsync(string authKey, CancellationToken ct = default)
    {
        var normalized = NormalizeAuthKey(authKey);
        if (normalized is null) return new JoinResult(false, GameTunnelTexts.T("授权密钥格式不对（应以 tskey-auth- 开头）"));

        // 没有常驻客户端时，up 一退出守护进程就会断开——必须先把托盘拉起来
        await EnsureTrayRunningAsync(null, ct).ConfigureAwait(false);

        // --accept-dns=false：不修改对方的 DNS 设置，连接地址一律用 IP，功能不受影响
        var result = await TailscaleCli.RunAsync(
            ["up", $"--auth-key={normalized}", "--accept-dns=false", "--unattended"],
            TimeSpan.FromSeconds(120),
            ct).ConfigureAwait(false);

        if (result.Ok) return new JoinResult(true, GameTunnelTexts.T("已加入联机网络"));

        var line = result.FirstLine(GameTunnelTexts.T("加入失败"));
        var friendly = line.Contains("invalid key", StringComparison.OrdinalIgnoreCase) ? GameTunnelTexts.T("授权密钥无效或已被使用") :
                       line.Contains("expired", StringComparison.OrdinalIgnoreCase) ? GameTunnelTexts.T("授权密钥已过期，请让对方重新生成") :
                       line.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? GameTunnelTexts.T("连接超时，请检查本机网络后重试") :
                       line;
        return new JoinResult(false, friendly);
    }

    /// <summary>校验并规范化授权密钥（tskey-auth-…）；不是授权密钥时返回 null。</summary>
    public static string? NormalizeAuthKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Trim('"', '\'', '，', ',', '。');
        return value.StartsWith("tskey-auth-", StringComparison.Ordinal) && value.Length > 20 ? value : null;
    }

    // ══════════════════════ 网络体检 ══════════════════════

    public static async Task<string> NetCheckAsync(CancellationToken ct = default)
    {
        var result = await TailscaleCli.RunAsync(["netcheck"], TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        return result.Combined;
    }

    public static TailscaleNetReport? ParseNetCheck(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        if (!output.Contains("Report:", StringComparison.OrdinalIgnoreCase)) return null;

        var udp = false;
        var hasIpv4 = false;
        var hasIpv6 = false;
        var varies = false;
        var portMapping = false;
        string? nearest = null;
        double? nearestMs = null;
        var latency = new List<(string Code, double Ms)>();

        var inReport = false;
        var inDerp = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Report:", StringComparison.OrdinalIgnoreCase))
            {
                inReport = true;
                continue;
            }
            if (!inReport) continue;

            if (line.StartsWith("* DERP latency", StringComparison.OrdinalIgnoreCase))
            {
                inDerp = true;
                continue;
            }

            if (inDerp)
            {
                // 形如 "- tok: 92.2ms  (Tokyo)"
                if (!line.StartsWith('-')) { inDerp = false; continue; }
                var body = line[1..].Trim();
                var colon = body.IndexOf(':');
                if (colon <= 0) continue;
                var code = body[..colon].Trim();
                var rest = body[(colon + 1)..].Trim();
                var msEnd = rest.IndexOf("ms", StringComparison.OrdinalIgnoreCase);
                if (msEnd <= 0) continue;
                if (!double.TryParse(rest[..msEnd].Trim(), out var ms)) continue;

                latency.Add((code, ms));
                if (nearestMs is null || ms < nearestMs)
                {
                    nearestMs = ms;
                    var open = rest.IndexOf('(');
                    var close = rest.LastIndexOf(')');
                    nearest = open >= 0 && close > open ? rest[(open + 1)..close].Trim() : code;
                }
                continue;
            }

            if (line.StartsWith("* UDP:", StringComparison.OrdinalIgnoreCase))
                udp = line.Contains("true", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("* IPv4:", StringComparison.OrdinalIgnoreCase))
                hasIpv4 = line.Contains("yes", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("* IPv6:", StringComparison.OrdinalIgnoreCase))
                hasIpv6 = line.Contains("yes", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("* MappingVariesByDestIP:", StringComparison.OrdinalIgnoreCase))
                varies = line.Contains("true", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("* PortMapping:", StringComparison.OrdinalIgnoreCase))
                portMapping = line[(line.IndexOf(':') + 1)..].Trim().Length > 0;
            else if (line.StartsWith("* Nearest DERP:", StringComparison.OrdinalIgnoreCase))
                nearest = line[(line.IndexOf(':') + 1)..].Trim();
        }

        if (!udp && !hasIpv4 && !hasIpv6 && latency.Count == 0) return null;

        return new TailscaleNetReport
        {
            Udp = udp,
            HasIpv4 = hasIpv4,
            HasIpv6 = hasIpv6,
            MappingVariesByDestIp = varies,
            HasPortMapping = portMapping,
            NearestDerp = nearest,
            NearestDerpMs = nearestMs,
            DerpLatency = latency
        };
    }

    public static async Task<string> PingAsync(string target, CancellationToken ct = default)
    {
        var result = await TailscaleCli.RunAsync(
            ["ping", "--c", "3", "--timeout", "5s", target],
            TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);
        return result.Combined;
    }

    public static TailscalePingResult ParsePing(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new TailscalePingResult { Ok = false, Summary = GameTunnelTexts.T("没有收到回应") };

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            // 只认结论行，跳过前置日志
            if (!line.StartsWith("pong from", StringComparison.OrdinalIgnoreCase)) continue;

            var peerIp = ExtractInParentheses(line);
            var direct = !line.Contains("via DERP", StringComparison.OrdinalIgnoreCase);
            double? latency = null;

            var msIndex = line.LastIndexOf(" in ", StringComparison.OrdinalIgnoreCase);
            if (msIndex > 0)
            {
                var tail = line[(msIndex + 4)..].Trim().TrimEnd('.', 'm', 's', ' ');
                if (double.TryParse(tail.TrimEnd('m', 's'), out var parsed)) latency = parsed;
            }

            var relay = "";
            if (!direct)
            {
                var open = line.IndexOf("DERP(", StringComparison.OrdinalIgnoreCase);
                var close = open >= 0 ? line.IndexOf(')', open) : -1;
                if (open >= 0 && close > open) relay = line[(open + 5)..close];
            }

            var summary = direct
                ? GameTunnelTexts.TSub($"直连成功 · {latency:F0}ms")
                : GameTunnelTexts.TSub($"中继成功（{relay}）· {latency:F0}ms");
            return new TailscalePingResult
            {
                Ok = true,
                Direct = direct,
                PeerIp = peerIp,
                LatencyMs = latency,
                Summary = summary
            };
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("20", StringComparison.Ordinal)) continue;
            if (line.Contains("no matching peer", StringComparison.OrdinalIgnoreCase))
                return new TailscalePingResult
                {
                    Ok = false,
                    PeerNotInTailnet = true,
                    Summary = GameTunnelTexts.T("这台设备不在你的网络里（对方还没加入，或地址填错了）")
                };
            if (line.Contains("logged out", StringComparison.OrdinalIgnoreCase))
                return new TailscalePingResult { Ok = false, Summary = GameTunnelTexts.T("本机 Tailscale 未登录，请先完成登录") };
            if (line.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return new TailscalePingResult { Ok = false, Summary = GameTunnelTexts.T("对方没有响应（对方可能没开机或没连上）") };
            if (line.Contains("not logged", StringComparison.OrdinalIgnoreCase))
                return new TailscalePingResult { Ok = false, Summary = GameTunnelTexts.T("本机 Tailscale 未登录") };
        }

        return new TailscalePingResult { Ok = false, Summary = GameTunnelTexts.T("连接失败") };
    }

    private static string? ExtractInParentheses(string line)
    {
        var open = line.IndexOf('(');
        var close = open >= 0 ? line.IndexOf(')', open) : -1;
        return open >= 0 && close > open ? line[(open + 1)..close].Trim() : null;
    }

    public static async Task<IReadOnlyList<TailscalePeer>> GetPeersAsync(CancellationToken ct = default)
    {
        var result = await TailscaleCli.RunAsync(["status", "--json"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return ParsePeers(result.Combined);
    }

    public static IReadOnlyList<TailscalePeer> ParsePeers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParsePeerMap(doc.RootElement);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>从已解析的 status JSON 里取设备列表——和状态共用同一次解析，不用再起一个进程。</summary>
    internal static IReadOnlyList<TailscalePeer> ParsePeerMap(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (!root.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object) return [];

            var users = new Dictionary<string, string>();
            if (root.TryGetProperty("User", out var userMap) && userMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var user in userMap.EnumerateObject())
                {
                    var login = GetString(user.Value, "LoginName") ?? GetString(user.Value, "DisplayName");
                    if (login is not null) users[user.Name] = login;
                }
            }

            var rows = new List<TailscalePeer>();
            foreach (var peer in peers.EnumerateObject())
            {
                var value = peer.Value;
                if (value.ValueKind != JsonValueKind.Object) continue;

                var hostName = GetString(value, "HostName");
                var dnsName = TrimTrailingDot(GetString(value, "DNSName"));
                if (hostName is null && dnsName is null) continue;

                string? ipv4 = null;
                if (value.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ip in ips.EnumerateArray())
                    {
                        if (ip.ValueKind == JsonValueKind.String && ip.GetString() is { Length: > 0 } candidate && !candidate.Contains(':'))
                        {
                            ipv4 = candidate;
                            break;
                        }
                    }
                }

                var userId = value.TryGetProperty("UserID", out var uid) && uid.ValueKind == JsonValueKind.Number
                    ? uid.GetInt64().ToString()
                    : null;

                var curAddr = GetString(value, "CurAddr");
                rows.Add(new TailscalePeer
                {
                    HostName = hostName ?? dnsName ?? "",
                    DisplayName = dnsName?.Split('.')[0],
                    Ipv4 = ipv4,
                    Online = value.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True,
                    IsDirect = !string.IsNullOrWhiteSpace(curAddr),
                    Relay = GetString(value, "Relay"),
                    Os = GetString(value, "OS"),
                    OwnerLogin = userId is not null && users.TryGetValue(userId, out var login) ? login : null
                });
            }

            return rows
                .OrderByDescending(p => p.Online)
                .ThenByDescending(p => p.IsDirect)
                .ThenBy(p => p.HostName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ══════════════════════ 健康告警的中文释义 ══════════════════════

    private sealed record HealthRule(
        string Code,
        string Match,
        string Title,
        string Text,
        string? Advice = null,
        bool ImpactsConnectivity = false,
        bool Transient = false);

    // 按官方 health/warnings.go 的告警文案匹配；ImpactsConnectivity 与官方标注保持一致，
    // 官方没标「影响连通性」的告警一律当提醒展示，避免把用户吓去重启/重装。
    private static readonly HealthRule[] _healthRules =
    [
        new("warming-up", "Tailscale is starting", GameTunnelTexts.T("正在启动"), GameTunnelTexts.T("Tailscale 正在启动，稍等几秒就会自动就绪。"), null, false, true),
        new("fetch-control-key", "fetch control key", GameTunnelTexts.T("拿不到控制密钥"), GameTunnelTexts.T("浏览器里可能已经授权成功，但本机没能从 Tailscale 服务器取回密钥。"),
            GameTunnelTexts.T("常见原因是代理 / 加速器 / 安全软件拦截了到 controlplane.tailscale.com 的连接；把它设为直连，或临时关闭代理后重启 Tailscale 服务。"), true),
        new("register-node", "machine/register", GameTunnelTexts.T("设备注册失败"), GameTunnelTexts.T("本机连不上 Tailscale 控制服务器，没能完成设备注册。"),
            GameTunnelTexts.T("通常是网络到 controlplane.tailscale.com 被拦截（代理、加速器、DNS 污染），把它设为直连后重试。"), true),
        new("not-in-map-poll", "Unable to connect to the Tailscale coordination server",
            GameTunnelTexts.T("状态同步暂时中断"), GameTunnelTexts.T("本机暂时连不上 Tailscale 协调服务器，正在自动重试。"),
            GameTunnelTexts.T("如果只是偶尔出现，对已经建立的连接没有影响；持续出现时检查网络或代理设置。")),
        new("login-state", "You are logged out", GameTunnelTexts.T("登录已失效"), GameTunnelTexts.T("本机已退出 Tailscale 登录。"),
            GameTunnelTexts.T("重新登录一次即可。")),
        new("machine-auth", "needs machine authorization", GameTunnelTexts.T("等待管理员批准"), GameTunnelTexts.T("这台设备需要在管理控制台批准后才能加入网络。"),
            GameTunnelTexts.T("让管理员在控制台 Machines 页面批准。")),
        new("tailnet-lock-locked-out", "locked out", GameTunnelTexts.T("被 Tailnet Lock 锁定"), GameTunnelTexts.T("这台设备未被信任签名，暂时无法通信。"),
            GameTunnelTexts.T("需要在管理员设备上执行 tailscale lock sign。")),
        new("tailnet-lock", "Tailnet Lock is enabled", GameTunnelTexts.T("Tailnet Lock 已启用"), GameTunnelTexts.T("该网络启用了 Tailnet Lock，新设备需要签名。"), null),
        new("no-udp4-bind", "couldn't listen for incoming UDP", GameTunnelTexts.T("UDP 端口不可用"), GameTunnelTexts.T("本机无法监听 UDP 端口，直连打洞会受影响。"),
            GameTunnelTexts.T("常见原因是安全软件拦截，或另一个程序占用了端口。"), true),
        new("no-derp-home", "could not connect to any relay server", GameTunnelTexts.T("中继服务器连不上"), GameTunnelTexts.T("无法连接任何中继服务器。"),
            GameTunnelTexts.T("检查网络或代理设置，中继不可用时连接会建立不起来。"), true),
        new("no-derp-connection", "could not connect to the", GameTunnelTexts.T("中继服务器连接失败"), GameTunnelTexts.T("无法连接指定的中继服务器。"),
            GameTunnelTexts.T("通常是网络或代理问题，稍后会自动重试。"), true),
        new("network-status", "network is down", GameTunnelTexts.T("本机网络断开"), GameTunnelTexts.T("系统报告网络已断开。"), GameTunnelTexts.T("检查网线 / Wi-Fi 连接。"), true),
        new("tls-connection-failed", "could not establish an encrypted connection", GameTunnelTexts.T("加密连接失败"), GameTunnelTexts.T("与控制服务器建立加密连接失败。"),
            GameTunnelTexts.T("可能有中间设备拦截了 TLS，检查代理或安全软件。"), true),
        new("derp-timed-out", "hasn't heard from the", GameTunnelTexts.T("中继暂时失去响应"), GameTunnelTexts.T("中继服务器暂时没有响应。"),
            GameTunnelTexts.T("通常是临时的，稍后会自动恢复。")),
        new("derp-region-error", "is reporting an issue", GameTunnelTexts.T("中继服务器报告异常"), GameTunnelTexts.T("中继服务器自报异常。"), GameTunnelTexts.T("等待自动切换。")),
        new("map-response-timeout", "hasn't received a network map", GameTunnelTexts.T("网络状态更新延迟"), GameTunnelTexts.T("一段时间没收到网络状态更新。"),
            GameTunnelTexts.T("对已建立的连接影响有限，持续出现时检查网络。")),
        new("control-health", "coordination server is reporting a health issue", GameTunnelTexts.T("控制服务器报告异常"), GameTunnelTexts.T("协调服务器报告了健康问题。"), null),
        new("magicsock-receive-func-error", "is not running", GameTunnelTexts.T("网络组件未运行"), GameTunnelTexts.T("内部网络组件没有运行。"), GameTunnelTexts.T("重启 Tailscale 或重启电脑后重试。")),
        new("want-running-false", "Tailscale is stopped", GameTunnelTexts.T("已停止连接"), GameTunnelTexts.T("Tailscale 当前处于断开状态。"), GameTunnelTexts.T("点「连接」即可恢复。")),
        new("security-update-available", "security update", GameTunnelTexts.T("有安全更新"), GameTunnelTexts.T("检测到安全性更新，建议尽快升级。"), GameTunnelTexts.T("在 Tailscale 托盘菜单里选择更新。")),
        new("update-available", "update from version", GameTunnelTexts.T("有新版本"), GameTunnelTexts.T("检测到新版本可用。"), GameTunnelTexts.T("在 Tailscale 托盘菜单里选择更新。")),
        new("is-using-unstable-version", "unstable version", GameTunnelTexts.T("正在使用非稳定版"), GameTunnelTexts.T("当前使用的是非稳定版本。"), null),
        new("local-log-config-error", "log is misconfigured", GameTunnelTexts.T("日志配置异常"), GameTunnelTexts.T("本地日志配置有误。"), null),
        new("ip-forwarding-off", "IP forwarding is disabled", GameTunnelTexts.T("未开启 IP 转发"), GameTunnelTexts.T("子网路由已启用但系统未开启 IP 转发。"), null),
    ];

    /// <summary>把一条英文健康告警翻成中文提示。</summary>
    public static TailscaleHealthNote DescribeHealth(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return new TailscaleHealthNote { Title = "", Text = "" };
        return DescribeHealthCore(text);
    }

    public static IReadOnlyList<TailscaleHealthNote> DescribeHealthLines(IReadOnlyList<string>? health)
    {
        if (health is null || health.Count == 0) return [];

        var notes = new List<TailscaleHealthNote>();
        foreach (var line in health)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            notes.Add(DescribeHealthCore(line.Trim()));
        }

        if (notes.Count == 0) return [];

        // 官方约定：warming-up 期间要抑制其它告警，否则会出现「正在启动」和「连不上服务器」互相矛盾的两条。
        // 但真正解释故障的告警（如「拿不到控制密钥」）必须留下——否则用户只能看到「正在启动」，
        // 永远不知道是被代理拦截了。
        if (notes.Any(n => n.Transient && n.Code == "warming-up"))
        {
            var important = notes.Where(n => n.ImpactsConnectivity && !n.Transient).ToList();
            if (important.Count > 0) return important;

            var warming = notes.First(n => n.Transient && n.Code == "warming-up");
            return [warming];
        }

        return notes;
    }

    private static TailscaleHealthNote DescribeHealthCore(string text)
    {
        foreach (var rule in _healthRules)
        {
            if (!text.Contains(rule.Match, StringComparison.OrdinalIgnoreCase)) continue;
            return new TailscaleHealthNote
            {
                Code = rule.Code,
                Title = rule.Title,
                Text = rule.Text,
                Advice = rule.Advice,
                ImpactsConnectivity = rule.ImpactsConnectivity,
                Transient = rule.Transient
            };
        }

        return new TailscaleHealthNote
        {
            Code = "unknown",
            Title = GameTunnelTexts.T("Tailscale 提示"),
            Text = text,
            ImpactsConnectivity = false,
            Transient = false
        };
    }

    /// <summary>
    /// 只有真正影响连通性的告警才值得在主页报警；其余（状态同步、版本提醒等）只在详情里展示。
    /// </summary>
    public static bool HasBlockingHealth(IReadOnlyList<TailscaleHealthNote>? notes)
        => notes?.Any(n => n.ImpactsConnectivity && !n.Transient) == true;

    // ══════════════════════ 启动卡住：诊断与自救 ══════════════════════

    /// <summary>
    /// 读取系统代理（WinINet）。Tailscale 的控制连接会跟随系统代理，而代理/加速器
    /// 常把 controlplane.tailscale.com 解析成不可直连的 fake-IP，表现为
    /// 「浏览器里授权成功，客户端却一直卡在启动」。
    /// </summary>
    public static string? GetSystemProxyDescription()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return null;

            var enabled = key.GetValue("ProxyEnable");
            if (enabled is not int flag || flag == 0) return null;

            return key.GetValue("ProxyServer") is string server && !string.IsNullOrWhiteSpace(server)
                ? server.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>机器级 WinHTTP 代理（部分加速器只写这里）。</summary>
    public static async Task<string?> GetMachineProxyAsync(CancellationToken ct = default)
    {
        var (ok, output) = await RunPowerShellAsync(
            "(netsh winhttp show proxy) -join \"`n\"",
            TimeSpan.FromSeconds(20),
            ct).ConfigureAwait(false);
        if (!ok || output.Length == 0) return null;

        // 输出随语言变化（"直接访问" / "Direct access"），只认形如 host:port 的令牌
        var match = System.Text.RegularExpressions.Regex.Match(output, @"((?:[A-Za-z0-9\-.]+|\[[0-9a-fA-F:]+\]):\d{2,5})");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>后端起不来 / 卡在启动时的标准自救动作。</summary>
    public static async Task<InstallResult> RestartServiceAsync(CancellationToken ct = default)
    {
        var (ok, output) = await RunPowerShellAsync(
            "Restart-Service -Name 'Tailscale' -Force -ErrorAction Stop; 'restarted'",
            TimeSpan.FromSeconds(90),
            ct).ConfigureAwait(false);

        if (ok) return new InstallResult(true, GameTunnelTexts.T("已重启 Tailscale 服务"));

        var detail = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (detail.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallResult(false, GameTunnelTexts.T("没有权限重启服务，请以管理员身份运行"));
        }

        if (detail.Contains("Cannot find", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("找不到", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallResult(false, GameTunnelTexts.T("找不到 Tailscale 服务，可能安装不完整，建议重新安装客户端"));
        }

        return new InstallResult(false, detail.Length > 0 ? GameTunnelTexts.TSub($"重启服务失败：{detail}") : GameTunnelTexts.T("重启服务失败"));
    }

    /// <summary>后端长时间停在「启动中」时给用户的一句话解释（有代理就点名代理）。</summary>
    public static string DescribeStartupHint(string? systemProxy, string? machineProxy = null)
    {
        var proxy = !string.IsNullOrWhiteSpace(systemProxy) ? systemProxy : machineProxy;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            return GameTunnelTexts.TSub($"检测到系统代理 {proxy}。代理 / 加速器经常把 controlplane.tailscale.com 解析到无法直连的地址，") +
                   GameTunnelTexts.T("Tailscale 就会一直连不上控制服务器。把它加入直连（绕过代理）规则，或临时关闭代理，") +
                   GameTunnelTexts.T("然后点「重启 Tailscale 服务」重试。");
        }

        return GameTunnelTexts.T("长时间停在这里，通常是本机到 controlplane.tailscale.com 的连接被拦截（代理、加速器、DNS 污染或安全软件）。") +
               GameTunnelTexts.T("先点「重启 Tailscale 服务」重试；仍然不行就检查上面这类软件。");
    }

    // ══════════════════════ 防火墙放行（仅限 Tailscale 网络） ══════════════════════

    public const string TailscaleAdapterName = "Tailscale";

    public static string FirewallRuleName(int port, GameTunnelProtocol protocol)
        => $"图吧工具箱·游戏联机 {protocol.Describe()} {port}";

    /// <summary>为端口放行入站（只在 Tailscale 虚拟网卡上生效，不会暴露到局域网/公网）。</summary>
    public static async Task<InstallResult> EnsureFirewallAsync(int port, GameTunnelProtocol protocol, CancellationToken ct = default)
    {
        if (!GameTunnelCatalog.IsValidPort(port)) return new InstallResult(false, GameTunnelTexts.T("端口不合法"));

        var script = new StringBuilder();
        foreach (var proto in Protocols(protocol))
        {
            var name = FirewallRuleName(port, proto == "TCP" ? GameTunnelProtocol.Tcp : GameTunnelProtocol.Udp);
            script.AppendLine($"$n = '{EscapeForPowerShell(name)}';");
            script.AppendLine("if (-not (Get-NetFirewallRule -DisplayName $n -ErrorAction SilentlyContinue)) {");
            script.AppendLine($"  New-NetFirewallRule -DisplayName $n -Direction Inbound -Action Allow -Protocol {proto} -LocalPort {port} -InterfaceAlias '{TailscaleAdapterName}' -Profile Any -ErrorAction Stop | Out-Null");
            script.AppendLine("  'created'");
            script.AppendLine("} else { 'exists' }");
        }

        var (ok, output) = await RunPowerShellAsync(script.ToString(), TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        if (!ok)
        {
            return new InstallResult(false, ExplainFirewallFailure(output));
        }

        return new InstallResult(true, output.Contains("created") ? GameTunnelTexts.T("已放行防火墙（仅 Tailscale 网络）") : GameTunnelTexts.T("防火墙规则已存在"));
    }

    /// <summary>清理本工具添加的防火墙规则。</summary>
    public static async Task<InstallResult> RemoveFirewallAsync(int port, GameTunnelProtocol protocol, CancellationToken ct = default)
    {
        var script = new StringBuilder();
        foreach (var proto in Protocols(protocol))
        {
            var name = FirewallRuleName(port, proto == "TCP" ? GameTunnelProtocol.Tcp : GameTunnelProtocol.Udp);
            script.AppendLine($"Remove-NetFirewallRule -DisplayName '{EscapeForPowerShell(name)}' -ErrorAction SilentlyContinue");
        }
        script.AppendLine("'done'");

        var (ok, output) = await RunPowerShellAsync(script.ToString(), TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        return new InstallResult(ok, ok ? GameTunnelTexts.T("已清理防火墙规则") : output);
    }

    /// <summary>列出本工具添加的规则（供诊断页展示）。</summary>
    public static async Task<string> ListFirewallRulesAsync(CancellationToken ct = default)
    {
        const string script = "Get-NetFirewallRule -DisplayName '图吧工具箱·游戏联机*' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DisplayName";
        var (_, output) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return output;
    }

    /// <summary>清理本工具添加过的全部放行规则。</summary>
    public static async Task<InstallResult> RemoveAllFirewallRulesAsync(CancellationToken ct = default)
    {
        const string script = "Remove-NetFirewallRule -DisplayName '图吧工具箱·游戏联机*' -ErrorAction SilentlyContinue; 'done'";
        var (ok, output) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        return new InstallResult(ok, ok ? GameTunnelTexts.T("已清理本工具添加的防火墙规则") : output);
    }

    private static IEnumerable<string> Protocols(GameTunnelProtocol protocol)
    {
        if (protocol.UsesTcp()) yield return "TCP";
        if (protocol.UsesUdp()) yield return "UDP";
    }

    private static string ExplainFirewallFailure(string output)
    {
        if (output.Contains("No MSFT_NetFirewallRule", StringComparison.OrdinalIgnoreCase)
            || output.Contains("术语", StringComparison.OrdinalIgnoreCase))
        {
            return GameTunnelTexts.T("系统不支持按网卡限定防火墙规则，请手动在 Windows 防火墙里放行该端口");
        }
        if (output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
        {
            return GameTunnelTexts.T("没有权限修改防火墙，请以管理员身份运行");
        }
        return GameTunnelTexts.T("防火墙放行失败（不影响已有规则）");
    }

    private static string EscapeForPowerShell(string value) => value.Replace("'", "''");

    private static async Task<(bool ok, string output)> RunPowerShellAsync(
        string script,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return (false, GameTunnelTexts.T("无法启动 PowerShell"));

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return (false, GameTunnelTexts.T("操作超时"));
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var ok = process.ExitCode == 0;
            return (ok, ok ? stdout : string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
