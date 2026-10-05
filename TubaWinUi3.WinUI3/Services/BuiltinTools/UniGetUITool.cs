using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TubaWinUi3.Models;

namespace TubaWinUi3.Services;

public sealed class UniGetUITool : IBuiltinTool
{
    public string Id => "unigetui";
    public string Name => MiscTexts.T("UniGetUI 包管理器");
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => MiscTexts.T("开源的 Windows 包管理器 GUI，支持 winget/scoop/chocolatey/pip/npm 等多种包管理器。");
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE8F2";
    public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.InstantAction;

    private const string Repo = "Devolutions/UniGetUI";
    private const string ProjectUrl = "https://github.com/Devolutions/UniGetUI";

    public async Task ExecuteAsync(BuiltinToolContext context)
    {
        if (IsInstalled())
        {
            var exe = FindInstalledExe();
            if (exe is not null)
            {
                try { Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true }); return; }
                catch { }
            }
        }

        var arch = GitHubReleaseService.GetCurrentArch();
        var destDir = Path.Combine(Path.GetTempPath(), "TubaWinUi3_UniGetUI");

        if (context.ConfirmDownload is not null)
        {
            var confirmed = await context.ConfirmDownload(Name, MiscTexts.T("安装包较大（约 135MB），下载可能需要较长时间"), MiscTexts.T("约 135MB"));
            if (!confirmed) return;
        }

        DownloadQueueService.EnqueueWithResolver(
            displayName: MiscTexts.T("UniGetUI 包管理器"),
            urlResolver: async ct =>
            {
                var release = await GitHubReleaseService.FetchLatestReleaseAsync(Repo, ct);
                if (release is null)
                    throw new InvalidOperationException(MiscTexts.T("无法从 GitHub 获取版本信息，请检查网络连接后重试。"));

                var asset = GitHubReleaseService.FindBestAsset(release.Assets, arch, AssetMatchStrategy.UniGetUI);
                if (asset is null)
                    throw new InvalidOperationException(MiscTexts.TSub($"当前架构 {arch} 没有匹配的下载文件。版本：{release.TagName}"));

                var proxyResults = await GitHubReleaseService.TestProxiesAsync(asset.OriginalUrl, 8, ct);
                var bestUrl = GitHubReleaseService.GetBestUrl(proxyResults, asset.OriginalUrl);

                return new ResolvedDownloadUrl(bestUrl, asset.Name, asset.Size);
            },
            destinationPath: destDir,
            postProcessor: new InstallerLaunchProcessor(),
            description: MiscTexts.T("安装包较大（约 135MB），下载可能需要较长时间"),
            glyph: Glyph);

        context.OnProgress?.Invoke(MiscTexts.T("已加入下载队列，请在下载中心查看进度。"));
    }

    private static bool IsInstalled() => FindInstalledExe() is not null;

    private static string? FindInstalledExe()
    {
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
                    if (name is not null && IsUniGetUI(name))
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
            var programDirs = new[] { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Program Files (ARM)" };
            foreach (var d in programDirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var sub in Directory.GetDirectories(d))
                {
                    if (IsUniGetUI(Path.GetFileName(sub)))
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

    private static bool IsUniGetUI(string name) =>
        name.Contains("UniGetUI", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WingetUI", StringComparison.OrdinalIgnoreCase);

    private static string? FindMainExe(string dir)
    {
        var candidates = new[] { "UniGetUI.exe", "WingetUI.exe" };
        foreach (var c in candidates)
        {
            var p = Path.Combine(dir, c);
            if (File.Exists(p)) return p;
        }
        foreach (var f in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(f);
            if (name.Contains("UniGetUI", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WingetUI", StringComparison.OrdinalIgnoreCase))
                return f;
        }
        return null;
    }
}
