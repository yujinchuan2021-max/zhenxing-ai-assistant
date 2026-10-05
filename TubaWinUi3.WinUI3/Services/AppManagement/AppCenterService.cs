using System.IO;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>应用中心条目：一件"软件内安装/可管理"的东西（public：XAML/x:Bind 数据源）。</summary>
public sealed class AppCenterItem
{
    // 注意：XamlTypeInfo 生成代码要求普通 set 且不能 required（会 CS8852/CS9035），勿改回
    public string Section { get; set; } = "";          // sandbox / env / ai / core
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public string Detail { get; set; } = "";
    public string CloudId { get; set; } = "";
    public string StatusKey { get; set; } = "installed";
    public string StatusLabel { get; set; } = "";
    public string EntryPath { get; set; } = "";
    public bool AllowLaunch { get; set; }
    public bool CanOpenTool { get; set; }
    public bool CanCreateShortcut { get; set; }
    public bool CanRemoveManaged { get; set; }
    public bool IsBusy { get; set; }
    public string PrimaryLabel { get; set; } = "";
    public bool PrimaryEnabled { get; set; }
    public string PrimaryAction { get; set; } = "None";
    public bool SecondaryEnabled => !IsBusy;
    public bool ShowOpen => CanOpenTool && PrimaryAction != nameof(AppCenterPrimaryAction.Open);
    public bool ShowLocation => CanOpenFolder && PrimaryAction != nameof(AppCenterPrimaryAction.OpenLocation);
    public bool CanOpenFolder { get; set; }
    /// <summary>登记记录 Id（「移除记录」按钮用；沙箱目录扫描项为空）。</summary>
    public string RecordId { get; set; } = "";
    /// <summary>是否显示「移除记录」按钮（非沙箱的登记条目）。</summary>
    public bool ShowRemove { get; set; }
    /// <summary>是否显示「卸载」按钮（沙箱目录内 or winget 系统安装条目）。</summary>
    public bool CanUninstall { get; set; }
    internal bool ManagedSandbox { get; set; }
    internal RegisteredPackageInspection? PackageInspection { get; set; }
    public bool ShowPackageCheck => !string.IsNullOrWhiteSpace(WingetId)
        && PrimaryAction != nameof(AppCenterPrimaryAction.CheckInstallation);
    public bool ShowSystemApps => !string.IsNullOrWhiteSpace(WingetId) &&
        (!CanOpenFolder || PackageInspection is { CanUninstall: false });
    /// <summary>winget 包 Id（系统安装条目，卸载走 winget uninstall）。</summary>
    public string WingetId { get; set; } = "";
    /// <summary>卸载动作令牌：sandbox|路径 或 winget|包Id（页面按此分流）。</summary>
    public string UninstallToken { get; set; } = "";
    /// <summary>可用新版本号（空=无更新）。</summary>
    public string UpgradeAvailable { get; set; } = "";
    public bool HasUpgrade { get; set; }
    public bool CanUpgrade => HasUpgrade && !string.IsNullOrWhiteSpace(WingetId);
    public string UpgradeLabel => CanUpgrade && !string.IsNullOrWhiteSpace(UpgradeAvailable)
        ? MiscTexts.TSub($"升级到 v{UpgradeAvailable}") : MiscTexts.T("升级");
    public string UpgradeToken => string.IsNullOrEmpty(WingetId) ? "" : "winget|" + WingetId;
}

/// <summary>
/// 本机应用聚合：登记、环境探测和随包内核；云端工具由页面单独投影，网络失败不影响本机清单。
/// ① 沙箱安装（%LocalAppData%\ToolboxCore\Environments\*，本应用管理的安装）
/// ② 本机开发环境（探测）
/// ③ AI 工具（探测）
/// ④ 工具箱内核（安装目录 Tools 统计）+ 本应用自身。
/// </summary>
internal static class AppCenterService
{
    /// <summary>
    /// 【GUI 隔离】沙箱环境根：隔离模式下整体落在隔离根下（ToolboxCore\Environments）；
    /// 生产模式 = %LocalAppData%\ToolboxCore\Environments。用属性（非 readonly 字段）以便每次访问实时取值、含测试钩子。
    /// </summary>
    public static string EnvironmentsRoot =>
        DataRoots.EffectiveTestRoot is { } tr
            ? Path.Combine(tr, "ToolboxCore", "Environments")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolboxCore", "Environments");

    /// <summary>winget 升级缓存（{包Id: 新版本}），由 RefreshUpgradeCacheAsync 填充，GetItemsAsync 读它渲染。</summary>
    private static Dictionary<string, string> _upgradeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object UpgradeGate = new();

    /// <summary>刷新升级缓存（慢：跑 winget upgrade，数秒）。返回是否有变化（页面据此决定是否重绘）。</summary>
    public static async Task<bool> RefreshUpgradeCacheAsync(CancellationToken cancellationToken = default)
    {
        List<string> ids;
        try
        {
            ids = SoftwareRegistry.Load()
                .Where(r => !string.IsNullOrWhiteSpace(r.WingetId))
                .Select(r => r.WingetId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return false; }

        cancellationToken.ThrowIfCancellationRequested();
        var upgrades = await SystemInstaller.CheckUpgradesAsync(ids, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (UpgradeGate)
        {
            var changed = upgrades.Count != _upgradeCache.Count;
            if (!changed)
            {
                foreach (var kv in upgrades)
                    if (!_upgradeCache.TryGetValue(kv.Key, out var old) || old != kv.Value) { changed = true; break; }
            }
            _upgradeCache = upgrades;
            return changed;
        }
    }

    public static Task<List<AppCenterItem>> GetItemsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => ReadItemsAsync(cancellationToken), cancellationToken);

    private static async Task<List<AppCenterItem>> ReadItemsAsync(CancellationToken cancellationToken)
    {
        var items = new List<AppCenterItem>();
        Dictionary<string, string> upgrades;
        lock (UpgradeGate) upgrades = new(_upgradeCache, StringComparer.OrdinalIgnoreCase);

        // ① 已登记软件（AI 助手安装 / 手动登记）+ 沙箱目录里尚未登记的
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in SoftwareRegistry.Load().OrderByDescending(x => x.InstalledAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seenPaths.Add((r.Path ?? "").TrimEnd('\\', '/'));
            var sourceText = r.Source switch
            {
                "ai" => MiscTexts.T("AI 助手安装"),
                "sandbox" => MiscTexts.T("沙箱安装"),
                "system" => MiscTexts.T("系统安装（winget）"),
                _ => MiscTexts.T("手动登记"),
            };
            var detail = $"{sourceText} · {r.InstalledAt:yyyy-MM-dd}";
            if (!string.IsNullOrWhiteSpace(r.Note)) detail += $" · {r.Note}";
            var normPath = (r.Path ?? "").TrimEnd('\\', '/');
            var inSandbox = CanRemoveSandboxDirectory(normPath);
            var hasWinget = !string.IsNullOrWhiteSpace(r.WingetId); // 升级缓存见 RefreshUpgradeCacheAsync
            items.Add(new AppCenterItem
            {
                Section = "sandbox",
                Name = r.Name,
                Glyph = "\uE8F1",
                Version = r.Version,
                Path = r.Path,
                Detail = detail,
                AllowLaunch = true,
                RecordId = r.Id,
                ShowRemove = !inSandbox,
                CanUninstall = inSandbox,
                ManagedSandbox = inSandbox,
                WingetId = r.WingetId,
                UninstallToken = inSandbox ? "sandbox|" + normPath : (hasWinget ? "winget|" + r.WingetId : ""),
                HasUpgrade = hasWinget && upgrades.ContainsKey(r.WingetId),
                UpgradeAvailable = hasWinget && upgrades.TryGetValue(r.WingetId, out var uv) ? uv : "",
            });
        }
        try
        {
            if (Directory.Exists(EnvironmentsRoot))
            {
                foreach (var dir in Directory.GetDirectories(EnvironmentsRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    if (seenPaths.Contains(dir.TrimEnd('\\', '/'))) continue;
                    var name = Path.GetFileName(dir);
                    var size = DirSize(dir);
                    var last = Directory.GetLastWriteTime(dir);
                    items.Add(new AppCenterItem
                    {
                        Section = "sandbox",
                        Name = name,
                        Glyph = "\uE8F1",
                        Version = "",
                        Path = dir,
                        Detail = MiscTexts.TSub($"沙箱目录 · {FormatSize(size)} · 最近使用 {last:yyyy-MM-dd}"),
                        CanOpenFolder = true,
                        CanUninstall = true,
                        ManagedSandbox = true,
                        UninstallToken = "sandbox|" + dir,
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // ②③ 探测（开发环境 + AI 工具）
        var probes = await EnvironmentProbe.ProbeAllAsync(cancellationToken);
        foreach (var p in probes.Where(p => p.Found))
        {
            items.Add(new AppCenterItem
            {
                Section = p.Category == "ai" ? "ai" : "env",
                Name = p.Label,
                Glyph = p.Glyph,
                Version = p.Version,
                Path = p.Path,
                Detail = p.Path,
                AllowLaunch = p.Key is "godot" or "cursor",
            });
        }

        // ④ 工具箱内核（安装目录 Tools 统计）
        try
        {
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
            if (Directory.Exists(toolsDir))
            {
                var count = Directory.EnumerateFiles(toolsDir, "*", SearchOption.AllDirectories).Count();
                items.Add(new AppCenterItem
                {
                    Section = "core",
                    Name = MiscTexts.T("图吧工具箱内核"),
                    Glyph = "\uE90F",
                    Version = "",
                    Path = toolsDir,
                    Detail = MiscTexts.TSub($"{count} 个工具文件 · {FormatSize(DirSize(toolsDir))} · 已就位"),
                    CanOpenFolder = true,
                });
            }
        }
        catch { }

        // ④ 本应用自身
        try
        {
            var exe = Environment.ProcessPath ?? "";
            var ver = typeof(AppCenterService).Assembly.GetName().Version?.ToString() ?? "";
            items.Add(new AppCenterItem
            {
                Section = "core",
                Name = MiscTexts.T("枕星图吧AI助手（本应用）"),
                Glyph = "\uE99A",
                Version = ver,
                Path = exe,
                Detail = exe,
                CanOpenFolder = !string.IsNullOrEmpty(exe),
            });
        }
        catch { }

        foreach (var item in items) RefreshLocalEntry(item);
        return items;
    }

    internal static void RefreshLocalEntry(AppCenterItem item)
        => RefreshLocalEntry(item, File.Exists, Directory.Exists);

    internal static void RefreshLocalEntry(AppCenterItem item, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        var entry = AppCenterActionPresentation.ResolveLocalEntry(item.Path, item.AllowLaunch, fileExists, directoryExists);
        item.EntryPath = entry.Executable ?? "";
        item.CanOpenTool = entry.Executable is not null;
        item.CanCreateShortcut = item.CanOpenTool;
        item.CanOpenFolder = entry.Directory is not null;
        // A stored package ID grants no uninstall capability until an exact, current read-only check succeeds.
        item.CanUninstall = item.ManagedSandbox || !string.IsNullOrWhiteSpace(item.WingetId)
            && item.PackageInspection?.CanUninstall == true;
        item.StatusKey = item.CanUpgrade ? "update" : item.CanOpenFolder ? "installed" : "missing";
        item.StatusLabel = MiscTexts.T(item.CanUpgrade ? "有可用更新" : item.CanOpenFolder ? "已找到本机软件" : "入口未找到，保留登记记录");
        item.PrimaryAction = (item.CanUpgrade ? AppCenterPrimaryAction.Update
            : item.CanOpenTool ? AppCenterPrimaryAction.Open
            : item.CanOpenFolder ? AppCenterPrimaryAction.OpenLocation : AppCenterPrimaryAction.None).ToString();
        item.PrimaryLabel = item.CanUpgrade ? item.UpgradeLabel : MiscTexts.T(item.CanOpenTool ? "打开" : item.CanOpenFolder ? "打开位置" : "入口未找到");
        item.PrimaryEnabled = item.CanUpgrade || item.CanOpenFolder;
        if (!string.IsNullOrWhiteSpace(item.WingetId))
        {
            if (item.PackageInspection is { } inspection)
            {
                item.StatusLabel = inspection.Message;
                if (!inspection.CanUninstall) item.StatusKey = "missing";
            }
            if (!item.CanOpenFolder)
            {
                item.StatusKey = item.PackageInspection?.CanUninstall == true ? "installed" : "missing";
                item.StatusLabel = item.PackageInspection?.Message ?? RegisteredPackageOperations.T(
                    "入口未找到，不能据此确认已卸载；请检查安装状态。",
                    "The entry was not found; this does not prove removal. Check installation status.");
                item.PrimaryAction = nameof(AppCenterPrimaryAction.CheckInstallation);
                item.PrimaryLabel = RegisteredPackageOperations.T("检查安装状态", "Check installation");
                item.PrimaryEnabled = true;
            }
        }
    }

    internal static bool CanRemoveSandboxDirectory(string? path)
    {
        if (!AppCenterActionPresentation.IsSandboxChild(path, EnvironmentsRoot)
            || AppCenterActionPresentation.NormalizeLocalPath(path) is not { } full || !Directory.Exists(full)) return false;
        try
        {
            var root = Path.GetFullPath(EnvironmentsRoot).TrimEnd('\\', '/');
            for (var current = full; current.Length >= root.Length; current = Path.GetDirectoryName(current) ?? "")
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    /// <summary>x:Bind 用：bool → Visibility（「打开目录」按钮的显示控制）。</summary>
    public static Microsoft.UI.Xaml.Visibility BoolToVisibility(bool value)
        => value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static long DirSize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:F1} {units[u]}";
    }
}
