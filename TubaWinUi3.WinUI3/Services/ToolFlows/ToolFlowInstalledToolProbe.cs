using System.Diagnostics;
using System.ComponentModel;
using System.Text.RegularExpressions;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>Candidate locations only: these paths never authorize installation, execution or registration.</summary>
internal sealed record ToolFlowInstalledToolPaths(string? ManagedFfmpegPath,
    IReadOnlyList<string> PathDirectories, string? WinGetLinksDirectory);

internal sealed record ToolFlowFfmpegFileInfo(string? ProductName, string? FileDescription,
    string? OriginalFilename, string? Version);

/// <summary>Read-only capabilities outside the automatic installation list. No command or target file is started.</summary>
internal static class ToolFlowInstalledToolProbe
{
    internal static Task<InstalledToolEvidence?> ProbeAsync(string targetKey, CancellationToken ct)
        => targetKey.Trim().Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)
            ? Task.Run(() => ProbeFfmpeg(CurrentPaths(), File.Exists, ReadFileInfo, ct), ct)
            : SystemInstaller.ProbeInstalledToolAsync(targetKey, ct);

    private static ToolFlowInstalledToolPaths CurrentPaths()
    {
        // The managed converter supplies its existing fixed architecture-specific path.
        // Isolation never falls back to real PATH or external user locations.
        if (DataRoots.EffectiveTestRoot is { } testRoot)
            return new(FfmpegService.FfmpegPath, [], Path.Combine(testRoot, "ExternalTools", "WinGetLinks"));
        var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links");
        var directories = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(scope => (Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(Path.PathSeparator))
            .ToArray();
        return new(FfmpegService.FfmpegPath, directories, links);
    }

    /// <summary>Pure path expansion; only the fixed FFmpeg executable names are accepted.</summary>
    internal static IReadOnlyList<string> FfmpegCandidates(ToolFlowInstalledToolPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var candidates = new List<string>();
        if (IsFfmpegExecutablePath(paths.ManagedFfmpegPath)) candidates.Add(paths.ManagedFfmpegPath!);
        AddDirectory(paths.WinGetLinksDirectory);
        foreach (var directory in paths.PathDirectories) AddDirectory(directory);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        void AddDirectory(string? value)
        {
            var directory = (value ?? "").Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) return;
            candidates.Add(Path.Combine(directory, "ffmpeg.exe"));
        }
    }

    internal static bool IsFfmpegExecutablePath(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
           && Regex.IsMatch(Path.GetFileName(path), @"\Affmpeg(?:-(?:x64|x86|arm64))?\.exe\z",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Fake roots and file metadata can be injected without touching the OS or executing a binary.</summary>
    internal static InstalledToolEvidence? ProbeFfmpeg(ToolFlowInstalledToolPaths paths,
        Func<string, bool> fileExists, Func<string, ToolFlowFfmpegFileInfo> readFileInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(readFileInfo);
        foreach (var candidate in FfmpegCandidates(paths))
        {
            ct.ThrowIfCancellationRequested();
            if (!fileExists(candidate)) continue;
            ToolFlowFfmpegFileInfo info;
            try { info = readFileInfo(candidate); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (Win32Exception) { continue; }
            ct.ThrowIfCancellationRequested();
            if (!HasFfmpegIdentity(info)) continue;
            return new("ffmpeg", "FFmpeg", candidate, info.Version);
        }
        return null;
    }

    private static bool HasFfmpegIdentity(ToolFlowFfmpegFileInfo info)
    {
        // A renamed ffprobe or unrelated executable does not provide FFmpeg's encoder capability.
        if (!string.IsNullOrWhiteSpace(info.OriginalFilename)
            && !info.OriginalFilename.Trim().Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return MatchesIdentity(info.ProductName) || MatchesIdentity(info.FileDescription);

        static bool MatchesIdentity(string? text) => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text.Trim(), @"\A(?:Jellyfin\s+)?FFmpeg(?:\s|\z)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static ToolFlowFfmpegFileInfo ReadFileInfo(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new(info.ProductName, info.FileDescription, info.OriginalFilename,
            info.ProductVersion ?? info.FileVersion);
    }
}
