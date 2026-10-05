using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>
/// 沙箱安装器（v0.2）：把便携版运行环境（Python / Node.js 绿色包）安装到受控目录
/// %LocalAppData%\ToolboxCore\Environments\ 下，全程留 journal（安装日志），
/// 安装完成后自动登记到「应用中心」。卸载 = 删目录 + 回放日志（绝不动系统目录）。
/// </summary>
internal static class SandboxInstaller
{
    public sealed record InstallTarget(string Id, string Name, string Version, string[] Urls, string? TopDirToStrip);

    private static readonly Dictionary<string, InstallTarget> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = new("python-3.12.8", "Python 3.12（沙箱版）", "3.12.8",
            [
                "https://mirrors.huaweicloud.com/python/3.12.8/python-3.12.8-embed-amd64.zip",
                "https://www.python.org/ftp/python/3.12.8/python-3.12.8-embed-amd64.zip",
            ],
            TopDirToStrip: null), // python embeddable 为平铺结构
        ["node"] = new("node-v22.20.0", "Node.js 22（沙箱版）", "22.20.0",
            [
                "https://registry.npmmirror.com/-/binary/node/v22.20.0/node-v22.20.0-win-x64.zip",
                "https://nodejs.org/dist/v22.20.0/node-v22.20.0-win-x64.zip",
            ],
            TopDirToStrip: "node-v22.20.0-win-x64"),
    };

    public static IEnumerable<string> KnownTargets => Targets.Keys;

    public static string DescribeTargets()
        => string.Join("、", Targets.Values.Select(t => $"{t.Id}（{t.Name}）"));

    /// <summary>安装一个目标。progress 回调用于日志；返回结果说明文本。</summary>
    public static async Task<string> InstallAsync(string targetKey, Action<string>? progress, CancellationToken ct)
    {
        if (!Targets.TryGetValue(targetKey ?? "", out var target))
            return $"未知安装目标：{targetKey}（支持：{DescribeTargets()}）";

        var root = Path.Combine(AppCenterService.EnvironmentsRoot, target.Id);
        var journalPath = Path.Combine(root, "journal.jsonl");
        Directory.CreateDirectory(root);

        void Journal(string action, string detail)
        {
            try
            {
                File.AppendAllText(journalPath,
                    JsonSerializer.Serialize(new { ts = DateTime.Now.ToString("O"), action, detail }) + "\n");
            }
            catch { }
            progress?.Invoke($"{action}: {detail}");
        }

        if (Directory.Exists(root) && File.Exists(Path.Combine(root, ".installed")))
            return MiscTexts.TSub($"{target.Name} 已存在（{root}）。如需重装，请先在「应用中心」卸载。");

        var zipPath = Path.Combine(root, ".download.zip");
        try
        {
            // 1) 下载（多源尝试）
            Journal("start", MiscTexts.TSub($"开始安装 {target.Name} → {root}"));
            long size = 0;
            Exception? lastError = null;
            foreach (var url in target.Urls)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Journal("download", url);
                    size = await DownloadAsync(url, zipPath, ct);
                    Journal("download-ok", $"{size / 1024 / 1024.0:F1} MB");
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Journal("download-retry", MiscTexts.TSub($"失败：{ex.Message}，尝试下一个源"));
                }
            }
            if (lastError is not null) throw new IOException(MiscTexts.TSub($"全部下载源失败：{lastError.Message}"));

            // 2) 校验：大小 + zip 完整性 + SHA-256 留档
            if (size < 1024 * 100) throw new IOException(MiscTexts.TSub($"文件过小（{size} 字节），疑似下载失败"));
            using (var za = ZipFile.OpenRead(zipPath))
            {
                if (za.Entries.Count == 0) throw new IOException(MiscTexts.T("压缩包为空"));
            }
            var sha = await ComputeSha256Async(zipPath, ct);
            Journal("verify", MiscTexts.TSub($"SHA-256 = {sha}（已留档）"));

            // 3) 解压
            Journal("extract", MiscTexts.T("解压到沙箱目录"));
            var tmpDir = Path.Combine(root, ".extracting");
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            ZipFile.ExtractToDirectory(zipPath, tmpDir);
            var sourceDir = tmpDir;
            if (target.TopDirToStrip is not null)
            {
                var stripped = Path.Combine(tmpDir, target.TopDirToStrip);
                if (Directory.Exists(stripped)) sourceDir = stripped;
            }
            foreach (var entry in Directory.GetFileSystemEntries(sourceDir))
            {
                var name = Path.GetFileName(entry);
                var dest = Path.Combine(root, name);
                if (Directory.Exists(entry)) CopyDir(entry, dest);
                else File.Copy(entry, dest, true);
            }
            Directory.Delete(tmpDir, true);

            // 4) 标记 + 登记 + 清理
            File.WriteAllText(Path.Combine(root, ".installed"), $"{target.Id} {DateTime.Now:O}");
            File.Delete(zipPath);
            Journal("done", MiscTexts.T("安装完成"));

            SoftwareRegistry.Add(target.Name, root, target.Version, source: "sandbox",
                note: MiscTexts.TSub($"沙箱安装 · {target.Id}"));

            return MiscTexts.TSub($"✅ {target.Name} 安装完成：{root}（已登记到应用中心，可随时卸载）");
        }
        catch (Exception ex)
        {
            Journal("failed", ex.Message);
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return MiscTexts.TSub($"❌ {target.Name} 安装失败：{ex.Message}（可看 {journalPath}）");
        }
    }

    /// <summary>卸载（仅限沙箱目录内）：删目录 + 返回回放报告。</summary>
    public static string Uninstall(string sandboxPath)
    {
        var full = Path.GetFullPath(sandboxPath).TrimEnd('\\', '/');
        var root = Path.GetFullPath(AppCenterService.EnvironmentsRoot).TrimEnd('\\', '/');
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return MiscTexts.T("拒绝卸载：该路径不在应用管理沙箱目录内。");
        }

        try
        {
            var entries = 0;
            long bytes = 0;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).Count();
                bytes = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { }

            Directory.Delete(full, true);
            SoftwareRegistry.Load()
                .Where(r => string.Equals((r.Path ?? "").TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(r => SoftwareRegistry.Remove(r.Id));

            return MiscTexts.TSub($"已卸载：删除 {entries} 个文件 / {bytes / 1024 / 1024.0:F1} MB（{full}）。残留说明：仅限沙箱目录内，系统目录未改动。");
        }
        catch (Exception ex)
        {
            return MiscTexts.TSub($"卸载失败：{ex.Message}（可能有文件被占用；关闭相关进程后重试）");
        }
    }

    private static async Task<long> DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZhenxingAI/{UpdateService.CurrentVersion.ToString(3)}");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await resp.Content.CopyToAsync(fs, ct);
        return fs.Length;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
