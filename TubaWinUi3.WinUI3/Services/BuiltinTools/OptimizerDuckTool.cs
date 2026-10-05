using System.Diagnostics;
using Microsoft.Win32;
using TubaWinUi3.Models;

namespace TubaWinUi3.Services;

public sealed class OptimizerDuckTool : IBuiltinTool
{
    public string Id => "optimizer-duck";
    public string Name => MiscTexts.T("OptimizerDuck 优化鸭");
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => MiscTexts.T("开源的 Windows 系统优化工具，支持系统清理、性能优化、隐私保护等功能。");
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE945";
    public string Category => "系统工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    private const string Repo = "itsfatduck/optimizerDuck";
    private const string ProjectUrl = "https://github.com/itsfatduck/optimizerDuck";

    private static string PortableDir => Path.Combine(ToolCatalog.ToolsRoot, "系统工具", MiscTexts.T("优化鸭"));

    /// <summary>
    /// 【GUI 隔离】安装/下载目标（写操作）：隔离态 = Tools 树内路径映射到可写根
    /// （ZXAI_DATA_ROOT\Tools\系统工具\优化鸭）；无法映射（树外路径）→ null（fail-closed，
    /// 绝不把写操作引向只读的随包 Tools）。生产态恒等 PortableDir，语义不变。测试可见。
    /// </summary>
    internal static string? ResolveInstallDir()
        => ToolCatalog.TryResolveDownloadTarget(PortableDir, out var resolved) ? resolved : null;

    public async Task ExecuteAsync(BuiltinToolContext context)
    {
        var exe = FindInstalledExe();
        if (exe is not null)
        {
            try { Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true }); return; }
            catch { }
        }

        var arch = GitHubReleaseService.GetCurrentArch();
        var destDir = ResolveInstallDir();
        if (destDir is null)
        {
            context.OnProgress?.Invoke(MiscTexts.T("隔离模式下无法解析可写安装目标，已取消下载（不写入只读的随包 Tools）。"));
            return;
        }

        if (context.ConfirmDownload is not null)
        {
            var confirmed = await context.ConfirmDownload(Name, MiscTexts.T("当前仅提供 x64 版本，ARM64 设备可能需要通过兼容层运行"), "");
            if (!confirmed) return;
        }

        DownloadQueueService.EnqueueWithResolver(
            displayName: MiscTexts.T("OptimizerDuck 优化鸭"),
            urlResolver: async ct =>
            {
                var release = await GitHubReleaseService.FetchLatestReleaseAsync(Repo, ct);
                if (release is null)
                    throw new InvalidOperationException(MiscTexts.T("无法从 GitHub 获取版本信息，请检查网络连接后重试。"));

                var asset = GitHubReleaseService.FindBestAsset(release.Assets, arch, AssetMatchStrategy.OptimizerDuck);
                if (asset is null)
                    throw new InvalidOperationException(MiscTexts.TSub($"当前架构 {arch} 没有匹配的下载文件。版本：{release.TagName}"));

                var proxyResults = await GitHubReleaseService.TestProxiesAsync(asset.OriginalUrl, 8, ct);
                var bestUrl = GitHubReleaseService.GetBestUrl(proxyResults, asset.OriginalUrl);

                return new ResolvedDownloadUrl(bestUrl, asset.Name, asset.Size);
            },
            destinationPath: destDir,
            postProcessor: new InstallerLaunchProcessor(),
            description: MiscTexts.T("当前仅提供 x64 版本，ARM64 设备可能需要通过兼容层运行"),
            glyph: Glyph);

        context.OnProgress?.Invoke(MiscTexts.T("已加入下载队列，请在下载中心查看进度。"));
    }

    private static string? FindInstalledExe()
    {
        try
        {
            // 【GUI 隔离】先查可写安装目标（隔离态安装落点），再查随包目录（只读可见/可启动）。
            var writable = ResolveInstallDir();
            if (writable is not null && Directory.Exists(writable))
            {
                var exe = FindMainExe(writable);
                if (exe is not null) return exe;
            }
            if (!string.Equals(writable, PortableDir, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(PortableDir))
            {
                var exe = FindMainExe(PortableDir);
                if (exe is not null) return exe;
            }
        }
        catch { }

        try
        {
            var keys = new[]
            {
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
            };
            foreach (var key in keys)
            {
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(sub);
                    var name = subKey?.GetValue("DisplayName") as string;
                    if (name is not null && IsOptimizerDuck(name))
                    {
                        var loc = subKey?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc))
                        {
                            loc = loc.TrimEnd('\\');
                            if (Directory.Exists(loc))
                            {
                                var exe = FindMainExe(loc);
                                if (exe is not null) return exe;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        try
        {
            var programDirs = new[] { @"C:\Program Files", @"C:\Program Files (x86)" };
            foreach (var d in programDirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var sub in Directory.GetDirectories(d))
                {
                    if (IsOptimizerDuck(Path.GetFileName(sub)))
                    {
                        var exe = FindMainExe(sub);
                        if (exe is not null) return exe;
                    }
                }
            }
        }
        catch { }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localDirs = new[] { Path.Combine(localAppData, "Programs") };
            foreach (var d in localDirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var sub in Directory.GetDirectories(d))
                {
                    if (IsOptimizerDuck(Path.GetFileName(sub)))
                    {
                        var exe = FindMainExe(sub);
                        if (exe is not null) return exe;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static bool IsOptimizerDuck(string name) =>
        name.Contains("optimizerDuck", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("OptimizerDuck", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("优化鸭", StringComparison.OrdinalIgnoreCase);

    private static string? FindMainExe(string dir)
    {
        var candidates = new[] { "optimizerDuck.exe", "OptimizerDuck.exe" };
        foreach (var c in candidates)
        {
            var p = Path.Combine(dir, c);
            if (File.Exists(p)) return p;
        }
        foreach (var f in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(f);
            if (name.Contains("optimizerDuck", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("OptimizerDuck", StringComparison.OrdinalIgnoreCase))
                return f;
        }
        return null;
    }
}
