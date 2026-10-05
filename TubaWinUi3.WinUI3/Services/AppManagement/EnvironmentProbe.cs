using System.Diagnostics;
using System.Text;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>环境/工具探测结果。</summary>
internal sealed record ProbeEntry(
    string Key, string Label, string Glyph, string Category,
    bool Found, string Version, string Path);

internal sealed record RuntimeFileInfo(string ProductName, string Version);

/// <summary>
/// 共享探测：常见开发环境与 AI 工具的安装探测（只读）。
/// 「应用中心」页面与 MCP manage_environment(verify) 共用同一套逻辑。
/// </summary>
internal static class EnvironmentProbe
{
    /// <summary>探测项定义：key、显示名、图标、分类（env=开发环境 / ai=AI 工具）、命令、版本参数。</summary>
    private static readonly (string Key, string Label, string Glyph, string Category, string Exe, string VerArgs)[] Defs =
    [
        ("node", "Node.js", "\uE943", "env", "node.exe", "--version"),
        ("python", "Python", "\uE756", "env", "python.exe", "--version"),
        ("git", "Git", "\uE8C8", "env", "git.exe", "--version"),
        ("dotnet", ".NET SDK", "\uE99A", "env", "dotnet.exe", "--version"),
        ("code", "Visual Studio Code", "\uE70F", "env", "code.cmd", "--version"),
        ("claude", "Claude Code CLI", "\uE99A", "ai", "claude.cmd", "--version"),
        ("codex", "Codex CLI", "\uE943", "ai", "codex.cmd", "--version"),
        ("opencode", "OpenCode CLI", "\uE756", "ai", "opencode.exe", "--version"),
        ("ollama", "Ollama", "\uE8F1", "ai", "ollama.exe", "--version"),
        ("uv", MiscTexts.T("uv（Python 包管理器）"), "\uE756", "ai", "uv.exe", "--version"),
        ("godot", "Godot", "\uE7FC", "ai", "godot.exe", "--version"),
        ("cursor", "Cursor", "\uE70F", "ai", "cursor.exe", "--version"),
    ];

    /// <summary>探测全部。Python/Node/Git 使用共享只读文件证据；其他项保留版本探测，失败不标为可用。</summary>
    public static Task<List<ProbeEntry>> ProbeAllAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            var list = new List<ProbeEntry>(Defs.Length);
            foreach (var (key, label, glyph, category, exe, verArgs) in Defs)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(ProbeOne(key, label, glyph, category, exe, verArgs));
            }
            return list;
        }, ct);

    /// <summary>探测单项（供 MCP 的 target 参数使用，也供委托调用）。</summary>
    public static ProbeEntry ProbeOne(string key)
    {
        var def = Defs.FirstOrDefault(d => d.Key == key);
        return def.Key is null
            ? new ProbeEntry(key, key, "\uE946", "env", false, "", "")
            : ProbeOne(def.Key, def.Label, def.Glyph, def.Category, def.Exe, def.VerArgs);
    }

    public static IEnumerable<string> KnownKeys => Defs.Select(d => d.Key);

    internal static bool UsesSharedRuntimeDiscovery(string? key)
        => (key ?? "").Trim().ToLowerInvariant() is "python" or "node" or "git";

    /// <summary>Pure runtime validity seam. Existence alone cannot turn a Store alias into Python.</summary>
    internal static bool TryValidateRuntimeCandidate(string key, string candidate,
        Func<string, bool> fileExists, Func<string, RuntimeFileInfo> readInfo, out string version)
    {
        version = "";
        key = key.Trim().ToLowerInvariant();
        if (!UsesSharedRuntimeDiscovery(key) || !Path.IsPathFullyQualified(candidate)) return false;
        var expectedFile = key switch { "node" => "node.exe", "git" => "git.exe", _ => "python.exe" };
        if (!Path.GetFileName(candidate).Equals(expectedFile, StringComparison.OrdinalIgnoreCase)) return false;
        if (key == "python" && Path.GetFileName(Path.GetDirectoryName(candidate))
            .Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileExists(candidate)) return false;
        RuntimeFileInfo info;
        try { info = readInfo(candidate); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        var product = new string(info.ProductName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var identity = key switch
        {
            "python" => product.StartsWith("python", StringComparison.Ordinal),
            "node" => product.StartsWith("nodejs", StringComparison.Ordinal),
            _ => product is "git" or "gitforwindows",
        };
        var actual = info.Version.Trim();
        if (!identity || !System.Text.RegularExpressions.Regex.IsMatch(actual,
            @"\A(?:v|Python\s+|git\s+version\s+)?\d+\.\d+(?:\.\d+)*(?:[-+.]\w[\w.+-]*)?\z",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return false;
        version = actual;
        return true;
    }

    internal static bool TryValidateRuntimeCandidate(string key, string candidate, out string version)
        => TryValidateRuntimeCandidate(key, candidate, File.Exists, ReadRuntimeFileInfo, out version);

    internal static string? FindRegisteredRuntimeCandidate(string key, IEnumerable<SoftRecord> records,
        Func<string, IEnumerable<string>> expandCandidates, Func<string, bool> fileExists,
        Func<string, RuntimeFileInfo> readInfo)
    {
        foreach (var record in records)
        {
            if (!Path.IsPathFullyQualified(record.Path ?? "")) continue;
            foreach (var candidate in expandCandidates(record.Path))
                if (TryValidateRuntimeCandidate(key, candidate, fileExists, readInfo, out _)) return candidate;
        }
        return null;
    }

    internal static string? FindRegisteredRuntimeCandidate(string key, IEnumerable<SoftRecord> records,
        Func<string, IEnumerable<string>> expandCandidates)
        => FindRegisteredRuntimeCandidate(key, records, expandCandidates, File.Exists, ReadRuntimeFileInfo);

    private static RuntimeFileInfo ReadRuntimeFileInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new(info.ProductName ?? "", info.ProductVersion ?? info.FileVersion ?? "");
        }
        catch (IOException) { return new("", ""); }
        catch (UnauthorizedAccessException) { return new("", ""); }
        catch (System.ComponentModel.Win32Exception) { return new("", ""); }
    }

    private static ProbeEntry ProbeOne(string key, string label, string glyph, string category, string exe, string verArgs)
    {
        try
        {
            if (UsesSharedRuntimeDiscovery(key))
            {
                // The application center and preparation use the same discovery and file evidence.
                // This branch never starts where.exe, Python, Node or Git.
                var existing = SystemInstaller.ProbeInstalledTool(key, CancellationToken.None);
                return existing is null
                    ? new ProbeEntry(key, label, glyph, category, false, "", "")
                    : new ProbeEntry(key, label, glyph, category, true, existing.Version ?? "", existing.ExecutablePath ?? "");
            }
            var whereOut = RunProcess("where.exe", exe, 8000);
            var path = whereOut.Split('\n')
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.Length > 2 && x.Contains(":\\"));
            if (path is null) return new ProbeEntry(key, label, glyph, category, false, "", "");

            var ver = RunProcess(path, verArgs, 8000).Trim()
                .Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            return new ProbeEntry(key, label, glyph, category, ver.Length > 0, ver, ver.Length > 0 ? path : "");
        }
        catch
        {
            return new ProbeEntry(key, label, glyph, category, false, "", "");
        }
    }

    private static string RunProcess(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return "";
            }
            return proc.ExitCode == 0 ? stdout : "";
        }
        catch
        {
            return "";
        }
    }
}
