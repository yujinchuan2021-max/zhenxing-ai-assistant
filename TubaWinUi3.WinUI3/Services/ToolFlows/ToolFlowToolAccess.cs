using System.Diagnostics;
using System.IO.Enumeration;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>Ephemeral local access evidence; never persisted, uploaded or copied into an Agent handoff.</summary>
internal sealed record ToolFlowToolAccessEntry(string TargetKey, string Name, string? ExecutablePath,
    string? DirectoryPath, bool IsGui);

/// <summary>Local-only instructions: fixed arguments and the verified executable, never an AI-generated shell command.</summary>
internal sealed record ToolFlowCliLaunchInstruction(string Command, string Guidance);

internal static class ToolFlowToolAccess
{
    // Positive CLI metadata: a desktop target missing a GUI label must not become a console command.
    private static readonly IReadOnlyDictionary<string, string> CliArguments =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = "", ["opencode"] = "", ["claude-code"] = "",
            ["python"] = "--version", ["node"] = "--version", ["git"] = "--version",
            ["dotnet"] = "--info", ["cmake"] = "--version", ["ollama"] = "--help",
            ["wsl"] = "--help", ["neovim"] = "", ["langflow"] = "--help",
            ["open-webui"] = "--help", ["amp"] = "--help", ["ffmpeg"] = "-version",
            ["vllm"] = "--help", ["inspect-ai"] = "--help", ["ragas"] = "--help",
            ["helm"] = "--help", ["deepeval"] = "--help",
        };

    internal static bool TryGetCliLaunchInstruction(ToolFlowToolAccessEntry entry,
        out ToolFlowCliLaunchInstruction instruction)
        => TryGetCliLaunchInstruction(entry, File.Exists, Directory.Exists, out instruction);

    /// <summary>Pure seam for tests. Quoting prevents apostrophes or shell metacharacters in local paths from becoming code.</summary>
    internal static bool TryGetCliLaunchInstruction(ToolFlowToolAccessEntry entry,
        Func<string, bool> fileExists, Func<string, bool> directoryExists,
        out ToolFlowCliLaunchInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        instruction = null!;
        if (entry.IsGui || !CliArguments.TryGetValue(entry.TargetKey, out var arguments)) return false;
        var verified = ResolveCandidate(entry.TargetKey, entry.ExecutablePath, fileExists, directoryExists);
        if (verified is null || verified.IsGui || verified.ExecutablePath is null
            || !SamePath(entry.ExecutablePath, verified.ExecutablePath)
            || !SamePath(entry.DirectoryPath, verified.DirectoryPath)) return false;
        var command = "& '" + verified.ExecutablePath.Replace("'", "''", StringComparison.Ordinal) + "'";
        if (arguments.Length > 0) command += " " + arguments;
        var guidance = IsInteractiveAgent(verified)
            ? Text("AiTask_CliAgentGuidance", "打开 PowerShell，先进入你的项目文件夹，再粘贴命令；首次启动按提示登录或配置模型。",
                "Open PowerShell in your project folder and paste the command. On first launch, follow the sign-in or model setup prompts.")
            : Text("AiTask_CliToolGuidance", "打开 PowerShell，粘贴命令即可使用或查看工具信息。",
                "Open PowerShell and paste the command to use the tool or view its information.");
        instruction = new(command, guidance);
        return true;
    }

    internal static async Task<ToolFlowCliLaunchInstruction> RevalidateCliLaunchInstructionAsync(
        ToolFlowToolAccessEntry entry, CancellationToken cancellationToken = default)
    {
        var current = await RevalidateAsync(entry, requireGui: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return TryGetCliLaunchInstruction(current, out var instruction) ? instruction : throw Unavailable();
    }

    // Only these fixed, interactive Agent executables may open a console. SDKs and arbitrary scripts stay directory-only.
    internal static bool IsInteractiveAgent(ToolFlowToolAccessEntry entry) =>
        !entry.IsGui && (entry.TargetKey.ToLowerInvariant() is "codex" or "opencode" or "claude-code") &&
        entry.ExecutablePath is { } executable &&
        Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        SystemInstaller.TryGetToolAccessMetadata(entry.TargetKey, out var metadata) &&
        metadata.ExecutableNames.Any(name => name.Equals(Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase));

    /// <summary>The user chooses a project directory. No generated command, prompt or credential is passed to the Agent.</summary>
    internal static async Task OpenInteractiveAgentAsync(ToolFlowToolAccessEntry entry, string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var current = await RevalidateAsync(entry, requireGui: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var start = CreateInteractiveAgentStartInfo(current, projectDirectory, Directory.Exists);
        try
        {
            using var process = Process.Start(start);
            if (process is null) throw Unavailable();
        }
        catch { throw Unavailable(); }
    }

    internal static ProcessStartInfo CreateInteractiveAgentStartInfo(ToolFlowToolAccessEntry entry,
        string projectDirectory, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (!IsInteractiveAgent(entry) || NormalizeLocalPath(projectDirectory) is not { } directory ||
            !directoryExists(directory)) throw Unavailable();
        return new ProcessStartInfo
        {
            FileName = entry.ExecutablePath!, WorkingDirectory = directory,
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal,
        };
    }

    internal static ToolFlowItem AccessItem(ToolFlowPreparationItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.State == ToolFlowPreparationState.AlreadyInstalled &&
            ToolFlowItemSemantics.TryGetReadOnlyTarget(row.Item) is { } detectedTarget &&
            detectedTarget.Equals(row.ExistingTool?.TargetKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(row.ExistingTool?.ExecutablePath))
            return row.Item with { InstallTargetKey = detectedTarget };
        // This local view bridges legacy snapshots; the saved item and installation target stay unchanged.
        if (row.State == ToolFlowPreparationState.AlreadyInstalled
            && string.Equals(row.ExistingTool?.TargetKey, "ffmpeg", StringComparison.OrdinalIgnoreCase)
            && ToolFlowInstalledToolProbe.IsFfmpegExecutablePath(row.ExistingTool?.ExecutablePath))
            return row.Item with { InstallTargetKey = "ffmpeg" };
        // A replacement keeps the proposal's identity; only its local access target changes.
        return row.State == ToolFlowPreparationState.ReusedInstalledTool
            && row.Requirement.Kind == ToolFlowRequirementKind.ArchiveExtraction
            && row.Item.InstallTargetKey?.Trim().Equals("7zip", StringComparison.OrdinalIgnoreCase) == true
            && row.ExistingTool?.TargetKey == "winrar"
            ? row.Item with { InstallTargetKey = "winrar" }
            : row.Item;
    }

    internal static Task<ToolFlowToolAccessEntry?> ResolveAsync(ToolFlowItem item,
        CancellationToken cancellationToken = default)
        => ResolveAsync(item, ToolFlowInstalledToolProbe.ProbeAsync, File.Exists, Directory.Exists, cancellationToken);

    /// <summary>FFmpeg's read-only lookup and filesystem can be injected without opening real configuration or binaries.</summary>
    internal static async Task<ToolFlowToolAccessEntry?> ResolveAsync(ToolFlowItem item,
        Func<string, CancellationToken, Task<InstalledToolEvidence?>> probe,
        Func<string, bool> fileExists, Func<string, bool> directoryExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        cancellationToken.ThrowIfCancellationRequested();
        // A saved desktop proposal must not expose a CLI entry simply because that CLI is installed.
        if (ToolFlowAgentVariantPolicy.GetMismatchReason(item) is not null ||
            ToolFlowItemSemantics.NeedsUserSetup(item) || ToolFlowItemSemantics.HasConflictingTarget(item)) return null;
        if (string.Equals(item.InstallTargetKey?.Trim(), "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var evidence = await probe("ffmpeg", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return string.Equals(evidence?.TargetKey, "ffmpeg", StringComparison.OrdinalIgnoreCase)
                    ? ResolveCandidate("ffmpeg", evidence?.ExecutablePath, fileExists, directoryExists) : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
        if (!SystemInstaller.TryGetToolAccessMetadata(item.InstallTargetKey, out var metadata)) return null;
        try
        {
            var candidate = await SystemInstaller.ResolveToolAccessPathAsync(metadata.TargetKey, cancellationToken);
            return ResolveCandidate(metadata.TargetKey, candidate, fileExists, directoryExists);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Pure validation seam: supplied paths must come from the fixed-target system lookup or verified FFmpeg probe.</summary>
    internal static ToolFlowToolAccessEntry? ResolveCandidate(string? targetKey, string? candidatePath,
        Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        if (NormalizeLocalPath(candidatePath) is not { } candidate) return null;
        if (string.Equals(targetKey?.Trim(), "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (!ToolFlowInstalledToolProbe.IsFfmpegExecutablePath(candidate) || !fileExists(candidate)) return null;
            var directory = Path.GetDirectoryName(candidate);
            return directory is not null && directoryExists(directory)
                ? new("ffmpeg", "FFmpeg", candidate, directory, IsGui: false) : null;
        }
        if (!SystemInstaller.TryGetToolAccessMetadata(targetKey, out var metadata)) return null;
        if (fileExists(candidate))
        {
            if (!metadata.ExecutableNames.Any(pattern =>
                FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(candidate), ignoreCase: true))) return null;
            var directory = Path.GetDirectoryName(candidate);
            if (directory is null || !directoryExists(directory)) return null;
            return new(metadata.TargetKey, metadata.Name, candidate, directory, metadata.IsGui);
        }
        return directoryExists(candidate)
            ? new(metadata.TargetKey, metadata.Name, null, candidate, metadata.IsGui) : null;
    }

    internal static async Task OpenToolAsync(ToolFlowToolAccessEntry entry,
        CancellationToken cancellationToken = default)
    {
        var current = await RevalidateAsync(entry, requireGui: true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try { ToolProcessLauncher.Launch(current.ExecutablePath!, current.DirectoryPath); }
        catch { throw Unavailable(); }
    }

    internal static async Task OpenLocationAsync(ToolFlowToolAccessEntry entry,
        CancellationToken cancellationToken = default)
    {
        var current = await RevalidateAsync(entry, requireGui: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo { FileName = explorer, UseShellExecute = true };
            start.ArgumentList.Add(current.DirectoryPath!);
            Process.Start(start);
        }
        catch { throw Unavailable(); }
    }

    internal static async Task<string> CreateShortcutAsync(ToolFlowToolAccessEntry entry,
        CancellationToken cancellationToken = default)
    {
        var current = await RevalidateAsync(entry, requireGui: true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() => WindowsSearchIndexService.CreateVerifiedDesktopShortcut(
                current.Name, current.ExecutablePath!), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Unavailable(); }
    }

    internal static Task<ToolFlowToolAccessEntry> RevalidateAsync(ToolFlowToolAccessEntry entry,
        bool requireGui, CancellationToken cancellationToken)
        => RevalidateAsync(entry, requireGui, ToolFlowInstalledToolProbe.ProbeAsync, File.Exists,
            Directory.Exists, cancellationToken);

    /// <summary>Fresh FFmpeg metadata and filesystem seams for action validation, without executing any action.</summary>
    internal static async Task<ToolFlowToolAccessEntry> RevalidateAsync(ToolFlowToolAccessEntry entry,
        bool requireGui, Func<string, CancellationToken, Task<InstalledToolEvidence?>> probe,
        Func<string, bool> fileExists, Func<string, bool> directoryExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        // Neither caller-provided GUI flags nor executable availability can make FFmpeg a desktop tool.
        if (requireGui && string.Equals(entry.TargetKey?.Trim(), "ffmpeg", StringComparison.OrdinalIgnoreCase))
            throw Unavailable();
        var current = await ResolveAsync(new ToolFlowItem
        {
            ItemId = "access-check", Name = "", Kind = "software", InstallTargetKey = entry.TargetKey,
        }, probe, fileExists, directoryExists, cancellationToken);
        return ValidateActionTarget(entry, current, requireGui, fileExists, directoryExists);
    }

    /// <summary>Checks fresh discovery against the displayed entry; stale or caller-supplied paths cannot execute.</summary>
    internal static ToolFlowToolAccessEntry ValidateActionTarget(ToolFlowToolAccessEntry requested,
        ToolFlowToolAccessEntry? current, bool requireGui, Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        if (current is null) throw Unavailable();
        var verified = ResolveCandidate(current.TargetKey, current.ExecutablePath ?? current.DirectoryPath,
            fileExists, directoryExists);
        if (verified is null || !string.Equals(requested.TargetKey, verified.TargetKey, StringComparison.OrdinalIgnoreCase)
            || requested.IsGui != verified.IsGui || !SamePath(requested.ExecutablePath, verified.ExecutablePath)
            || !SamePath(requested.DirectoryPath, verified.DirectoryPath)
            || requireGui && (!verified.IsGui || verified.ExecutablePath is null)) throw Unavailable();
        return verified;
    }

    private static bool SamePath(string? first, string? second) => first is null && second is null ||
        NormalizeLocalPath(first) is { } normalized && string.Equals(normalized,
            NormalizeLocalPath(second), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl) || path.Contains('"') || path.Contains('%')) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
    }

    private static InvalidOperationException Unavailable() => new(LocalizationService.L("AiTask_ToolUnavailable",
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage
            ? "The tool entry is unavailable or has changed. Check the installed tools again."
            : "工具入口暂不可用或已变化，请重新检测已安装工具后再试。"));

    private static string Text(string key, string chinese, string english) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? english : chinese);
}
