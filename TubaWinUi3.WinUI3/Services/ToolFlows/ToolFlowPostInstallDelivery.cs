using System.Text.Json.Serialization;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.ToolFlows;

public enum ToolFlowPostInstallDeliveryKind
{
    DesktopShortcutReady,
    CliReady,
    Unavailable,
    Failed,
}

/// <summary>Usage delivery is separate from installation success. Local paths and commands never enter upload payloads.</summary>
public sealed record ToolFlowPostInstallDeliveryResult(
    string ItemId, string Name, ToolFlowPostInstallDeliveryKind Kind, string Message)
{
    public string? TargetKey { get; init; }
    [JsonIgnore] public string? ShortcutPath { get; init; }
    [JsonIgnore] public string? CliCommand { get; init; }
    [JsonIgnore] public string? CliGuidance { get; init; }
}

/// <summary>Completes a verified installation with a desktop icon or safe CLI instructions, without starting the tool.</summary>
internal sealed class ToolFlowPostInstallDelivery
{
    private readonly Func<ToolFlowItem, CancellationToken, Task<ToolFlowToolAccessEntry?>> _resolver;
    private readonly Func<ToolFlowToolAccessEntry, CancellationToken, Task<string>> _shortcutCreator;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;

    internal ToolFlowPostInstallDelivery(
        Func<ToolFlowItem, CancellationToken, Task<ToolFlowToolAccessEntry?>>? resolver = null,
        Func<ToolFlowToolAccessEntry, CancellationToken, Task<string>>? shortcutCreator = null,
        Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
    {
        _resolver = resolver ?? ToolFlowToolAccess.ResolveAsync;
        _shortcutCreator = shortcutCreator ?? ToolFlowToolAccess.CreateShortcutAsync;
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    internal async Task<ToolFlowPostInstallDeliveryResult> DeliverAsync(ToolFlowItem item,
        ToolFlowInstallItemResult installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        var targetKey = installation.Status == ToolFlowInstallItemStatus.ReusedInstalledTool
            ? installation.ExistingToolTargetKey : item.InstallTargetKey;
        targetKey = targetKey?.Trim().ToLowerInvariant();
        ToolFlowPostInstallDeliveryResult Result(ToolFlowPostInstallDeliveryKind kind, string message,
            string? name = null) => new(item.ItemId, name ?? item.Name, kind, message) { TargetKey = targetKey };

        if (installation.ItemId != item.ItemId || installation.Status is not
            (ToolFlowInstallItemStatus.Installed or ToolFlowInstallItemStatus.AlreadyInstalled or
             ToolFlowInstallItemStatus.ReusedInstalledTool))
            return Result(ToolFlowPostInstallDeliveryKind.Unavailable,
                Text("AiTask_DeliveryNotReady", "本项尚未就绪，完成后再提供打开入口。", "This item is not ready yet. Its usage entry will be available when complete."));

        try
        {
            if (string.IsNullOrEmpty(targetKey) || targetKey != "ffmpeg"
                && !SystemInstaller.TryGetToolAccessMetadata(targetKey, out _)) return MissingEntry();
            var resolved = await _resolver(item with { InstallTargetKey = targetKey }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved is null || !string.Equals(resolved.TargetKey, targetKey, StringComparison.OrdinalIgnoreCase))
                return MissingEntry();
            ToolFlowToolAccessEntry entry;
            try
            {
                entry = ToolFlowToolAccess.ValidateActionTarget(resolved, resolved, requireGui: false,
                    _fileExists, _directoryExists);
            }
            catch (InvalidOperationException) { return MissingEntry(); }
            if (entry.ExecutablePath is null) return MissingEntry();
            if (entry.IsGui)
            {
                // The existing helper revalidates discovery, reuses matching links and preserves unrelated desktop items.
                var shortcut = await _shortcutCreator(entry, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExistingShortcut(shortcut)) return ShortcutFailed(entry.Name);
                return Result(ToolFlowPostInstallDeliveryKind.DesktopShortcutReady,
                    Text("AiTask_DeliveryDesktopReady", "已就绪，桌面图标已创建。", "Ready. A desktop shortcut is available."), entry.Name)
                    with { ShortcutPath = shortcut };
            }
            if (ToolFlowToolAccess.TryGetCliLaunchInstruction(entry, _fileExists, _directoryExists, out var cli))
                return Result(ToolFlowPostInstallDeliveryKind.CliReady,
                    Text("AiTask_DeliveryCliReady", "已就绪，复制启动命令到 PowerShell 即可。", "Ready. Copy the launch command into PowerShell."), entry.Name)
                    with { CliCommand = cli.Command, CliGuidance = cli.Guidance };
            return MissingEntry();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return Result(ToolFlowPostInstallDeliveryKind.Failed,
                Text("AiTask_DeliveryFailed", "软件已安装，打开入口暂未完成；可重新检测或重试创建图标。",
                    "The software is installed, but its usage entry is incomplete. Check it again or retry creating the shortcut."));
        }

        ToolFlowPostInstallDeliveryResult MissingEntry() => Result(ToolFlowPostInstallDeliveryKind.Unavailable,
            Text("AiTask_DeliveryMissingEntry", "软件已安装，暂未找到可验证的启动入口；请重新检测。",
                "The software is installed, but a verified launch entry was not found. Check the installed tool again."));
        ToolFlowPostInstallDeliveryResult ShortcutFailed(string name) => Result(ToolFlowPostInstallDeliveryKind.Failed,
            Text("AiTask_DeliveryShortcutFailed", "软件已安装，桌面图标未创建；可重试创建图标。",
                "The software is installed, but the desktop shortcut was not created. You can retry."), name);
    }

    private bool IsExistingShortcut(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            && !path.StartsWith(@"\\", StringComparison.Ordinal) && path.IndexOf(':', 2) < 0
            && !path.Any(char.IsControl) && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            && _fileExists(path);

    private static string Text(string key, string chinese, string english) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? english : chinese);
}
