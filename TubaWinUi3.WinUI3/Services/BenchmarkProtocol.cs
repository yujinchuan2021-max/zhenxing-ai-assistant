using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TubaWinUi3.Models;

namespace TubaWinUi3.Services;

internal sealed record BenchmarkSubmission(int SchemaVersion, string ReportId, string Kind,
    string ClientVersion, string ScoreVersion, string TestTime, string DurationMode,
    object Report, string? HeatmapBase64);

public sealed record BenchmarkUploadReceipt(string ReportId, bool Created, string Status);

internal static class BenchmarkProtocol
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static BenchmarkSubmission Create(PerformanceBenchmarkResult r, string kind, byte[]? png,
        string clientVersion, string osName)
    {
        if (png is { Length: > 2 * 1024 * 1024 }) throw new InvalidDataException(PerfTexts.T("热力图超过 2MB，无法上传。"));
        var report = new
        {
            cpuName = Clean(r.CpuName, 400), gpuName = Clean(r.GpuName, 400), osName = Clean(osName, 300),
            motherboardName = Clean(r.MotherboardName, 800), memoryInfo = Clean(r.MemoryInfo, 4000),
            diskInfo = Clean(r.DiskInfo, 4000), displayInfo = Clean(r.DisplayInfo, 2000),
            cpuSingleCoreScore = r.Cpu.SingleCoreScore, cpuMultiCoreScore = r.Cpu.MultiCoreScore,
            gpuRenderScore = r.Gpu.RenderScore, memoryCapacityScore = r.Memory.CapacityScore,
            diskSeqReadScore = r.Disk.SeqReadScore, diskSeqWriteScore = r.Disk.SeqWriteScore,
            disk4KReadScore = r.Disk.Random4KReadScore, disk4KWriteScore = r.Disk.Random4KWriteScore,
            browserTotalScore = r.Browser.TotalScore, winFinalScore = r.Win.FinalScore
        };
        var payload = new BenchmarkSubmission(1, "", kind, clientVersion, "zxai-performance/1",
            r.TestTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            Clean(r.DurationMode, 80), report, png is null ? null : Convert.ToBase64String(png));
        // Deterministic for the same frozen test, so a lost receipt/retry never creates a duplicate row.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json)));
        return payload with { ReportId = new Guid(digest.AsSpan(0, 16)).ToString("D") };
    }

    private static string Clean(string value, int maximum)
    {
        var cleaned = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= maximum ? cleaned : cleaned[..maximum];
    }
}

/// <summary>Private ownership capability. Created only on explicit upload; no GitHub or model credential is used.</summary>
internal sealed class BenchmarkIdentityStore(string dataRoot)
{
    private static readonly object Sync = new();
    private readonly string _path = Path.Combine(dataRoot, "BenchmarkUploads", "owner.dpapi");
    internal string? Read()
    {
        lock (Sync)
        {
            if (!File.Exists(_path)) return null;
            var value = Encoding.ASCII.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser));
            if (value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException(PerfTexts.T("本机跑分投稿凭据损坏，请保留文件并联系开发者。"));
            return value;
        }
    }

    internal string GetOrCreate()
    {
        lock (Sync)
        {
            var existing = Read();
            if (existing is not null) return existing;
            var value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var encrypted = ProtectedData.Protect(Encoding.ASCII.GetBytes(value), null, DataProtectionScope.CurrentUser);
            // Publish only a fully flushed credential. Never replace another process's ownership.
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(encrypted);
                    output.Flush(true);
                }
                try { File.Move(temporary, _path, overwrite: false); }
                catch (IOException) when (File.Exists(_path)) { return Read()!; }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return value;
        }
    }
}
