namespace TubaWinUi3.Services.AppManagement;

internal enum AppCenterPrimaryAction { None, Download, Update, Open, Retry, OpenLocation, OpenWebsite, CheckInstallation }
internal enum AppCenterToolStatus { NotInstalled, Downloading, Installing, Installed, Updating, PendingUpdate, Removing, Failed, Unsupported }

internal sealed record AppCenterActionView(string StatusKey, string StatusLabel, string PrimaryLabel,
    AppCenterPrimaryAction PrimaryAction, bool PrimaryEnabled, bool CanOpenLocation,
    bool CanCreateShortcut, bool CanRemoveManaged);

/// <summary>Pure presentation and path checks. No probing, downloads, process launches or user-data reads.</summary>
internal static class AppCenterActionPresentation
{
    internal static AppCenterActionView Cloud(AppCenterToolStatus status, bool hasEntry, bool isManaged,
        bool hasUpdate, bool pendingUpdate, double progress = 0, bool canDownload = true, bool hasHomepage = false)
    {
        bool busy = status is AppCenterToolStatus.Downloading or AppCenterToolStatus.Installing
            or AppCenterToolStatus.Updating or AppCenterToolStatus.Removing;
        string percent = double.IsFinite(progress) ? $" {Math.Clamp(progress, 0, 100):0}%" : "";
        if (busy) return new("busy", status switch
        {
            AppCenterToolStatus.Downloading => "正在下载" + percent,
            AppCenterToolStatus.Installing => "正在安装",
            AppCenterToolStatus.Updating => "正在更新" + percent,
            _ => "正在移除",
        }, "处理中", AppCenterPrimaryAction.None, false, false, false, false);
        if (pendingUpdate || status == AppCenterToolStatus.PendingUpdate)
            return new("update", "运行中，退出工具后再更新", "等待工具退出", AppCenterPrimaryAction.None,
                false, hasEntry, hasEntry, false);
        if (!canDownload || status == AppCenterToolStatus.Unsupported)
            return hasEntry
                ? new("installed", "已安装，自动下载暂不可用", "打开", AppCenterPrimaryAction.Open,
                    true, true, true, isManaged)
                : new("download", "需查看来源获取，暂不可自动下载", hasHomepage ? "查看来源" : "暂不可自动下载",
                    hasHomepage ? AppCenterPrimaryAction.OpenWebsite : AppCenterPrimaryAction.None,
                    hasHomepage, false, false, isManaged);
        if (status == AppCenterToolStatus.Failed)
            return new(hasEntry ? "update" : "download", hasEntry ? "更新未完成，原版本可用" : "准备未完成",
                "重试", AppCenterPrimaryAction.Retry, true, hasEntry, hasEntry, isManaged);
        if (hasEntry && hasUpdate)
            return new("update", "有可用更新", "更新", AppCenterPrimaryAction.Update, true, true, true, isManaged);
        if (hasEntry)
            return new("installed", "已安装", "打开", AppCenterPrimaryAction.Open, true, true, true, isManaged);
        // A persisted Installed state cannot substitute for a real entry file.
        return new("download", status == AppCenterToolStatus.Installed ? "入口缺失，需要重新下载" : "待下载",
            "下载", AppCenterPrimaryAction.Download, true, false, false, isManaged);
    }

    internal static bool Matches(AppCenterItem item, string category, string search)
        => (category == "all" || item.StatusKey == category) && (string.IsNullOrWhiteSpace(search)
            || (item.Name + " " + item.Version + " " + item.Detail + " " + item.StatusLabel)
                .Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0
            || path.Any(char.IsControl) || path.Contains('"') || path.Contains('%')) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    internal static (string? Executable, string? Directory) ResolveLocalEntry(string? path, bool allowLaunch,
        Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        if (NormalizeLocalPath(path) is not { } normalized) return (null, null);
        if (directoryExists(normalized)) return (null, normalized);
        if (!fileExists(normalized)) return (null, null);
        var directory = Path.GetDirectoryName(normalized);
        if (directory is null || !directoryExists(directory)) return (null, null);
        return (allowLaunch && Path.GetExtension(normalized).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized : null, directory);
    }

    internal static bool IsSandboxChild(string? path, string root)
    {
        var target = NormalizeLocalPath(path);
        var parent = NormalizeLocalPath(root);
        return target is not null && parent is not null
            && target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
