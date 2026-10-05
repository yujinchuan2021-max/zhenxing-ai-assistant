using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TubaWinUi3.Models;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Services;

/// <summary>First-party benchmark API. Public boards contain moderated client submissions, not verified measurements.</summary>
public static class BenchmarkCloudService
{
    private static readonly HttpClient Api = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly object CacheLock = new();
    private static readonly SemaphoreSlim UploadLock = new(1, 1);
    private static List<BenchmarkReportEntry>? _cache;
    private static DateTimeOffset _cacheTime;
    private static readonly ConcurrentDictionary<string, int> TotalPages = new();
    private static List<LatencyImageInfo>? _images;
    public static string CurrentSourceName => PerfTexts.T("枕星跑分服务");
    private static BenchmarkIdentityStore Identity => new(ConfigManager.GetDataDir());
    private static Uri Endpoint(string path) => new(ToolFlowUploadService.OfficialEndpoint, path);
    private static string CachePath => Path.Combine(ConfigManager.GetDataDir(), "zxai-benchmark-cache-v1.json");

    public sealed class LatencyImageInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string CpuName { get; init; } = "";
        public string Author { get; init; } = "";
        public string Path { get; init; } = "";
        public string Sha { get; init; } = "";
        public string RawUrl => Endpoint(Path).AbsoluteUri;
    }

    private sealed record ReportList(List<BenchmarkReportEntry> Reports);
    private sealed record ImageList(List<LatencyImageInfo> Images);
    private sealed record CacheDocument(string Source, List<BenchmarkReportEntry> Reports);

    public static void InvalidateCache()
    {
        lock (CacheLock) { _cache = null; _images = null; _cacheTime = DateTimeOffset.MinValue; }
        TotalPages.Clear();
    }

    public static void SaveToCache(List<BenchmarkReportEntry> reports)
    {
        lock (CacheLock) { _cache = reports.ToList(); _cacheTime = DateTimeOffset.UtcNow; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheDocument(ToolFlowUploadService.OfficialEndpoint.AbsoluteUri, reports), BenchmarkProtocol.Json));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static List<BenchmarkReportEntry> LoadLocalCacheOnly()
    {
        try
        {
            var cache = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(CachePath), BenchmarkProtocol.Json);
            return cache?.Source == ToolFlowUploadService.OfficialEndpoint.AbsoluteUri ? cache.Reports : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path, string? owner = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, Endpoint(path));
        if (owner is not null) request.Headers.Add("X-Benchmark-Token", owner);
        request.Headers.UserAgent.ParseAdd("ZhenxingAI-Benchmark/0.1");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, BenchmarkProtocol.Json), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<T> JsonAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(PerfTexts.T("枕星跑分服务请求失败，请稍后重试。") + $" (HTTP {(int)response.StatusCode})");
            if (response.Content.Headers.ContentLength is > 2 * 1024 * 1024) throw new InvalidDataException("Benchmark response too large.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Benchmark response too large.");
                output.Write(buffer, 0, count);
            }
            return JsonSerializer.Deserialize<T>(output.ToArray(), BenchmarkProtocol.Json) ?? throw new InvalidDataException("Invalid benchmark response.");
        }
    }

    public static async Task<BenchmarkUploadReceipt> UploadReportAsync(PerformanceBenchmarkResult result,
        IProgress<string>? progress, CancellationToken ct, string? latencyImagePath = null)
        => await UploadAsync(result, "benchmark", latencyImagePath, progress, ct);

    public static async Task<BenchmarkUploadReceipt> UploadLatencyImageOnlyAsync(string cpuName, string latencyImagePath,
        IProgress<string>? progress, CancellationToken ct)
        => await UploadAsync(new PerformanceBenchmarkResult { CpuName = cpuName, TestTime = File.GetLastWriteTimeUtc(latencyImagePath) },
            "latency", latencyImagePath, progress, ct);

    private static async Task<BenchmarkUploadReceipt> UploadAsync(PerformanceBenchmarkResult result, string kind,
        string? pngPath, IProgress<string>? progress, CancellationToken ct)
    {
        await UploadLock.WaitAsync(ct);
        try
        {
            byte[]? image = null;
            if (pngPath is not null)
            {
                if (new FileInfo(pngPath).Length > 2 * 1024 * 1024) throw new InvalidDataException(PerfTexts.T("热力图超过 2MB，无法上传。"));
                image = await File.ReadAllBytesAsync(pngPath, ct);
            }
            var body = BenchmarkProtocol.Create(result, kind, image,
                UpdateService.CurrentVersion.ToString(3), RuntimeInformation.OSDescription);
            progress?.Report(PerfTexts.T("正在提交到枕星后台..."));
            var receipt = await JsonAsync<BenchmarkUploadReceipt>(Request(HttpMethod.Post, "v1/benchmarks", Identity.GetOrCreate(), body), ct);
            if (receipt.ReportId != body.ReportId || receipt.Status is not ("pending" or "accepted" or "rejected" or "withdrawn"))
                throw new InvalidDataException("Benchmark receipt differs from submitted report.");
            InvalidateCache();
            return receipt;
        }
        finally { UploadLock.Release(); }
    }

    public static string ReceiptText(BenchmarkUploadReceipt receipt, bool heatmapOnly = false) => receipt.Status switch
    {
        "accepted" when heatmapOnly => PerfTexts.T("热力图已公开，可在核间延迟查询查看。"),
        "pending" when heatmapOnly => PerfTexts.T("热力图已提交到枕星后台，审核采纳后可在核间延迟查询查看。"),
        "accepted" => PerfTexts.T("报告已公开，可在枕星排行榜查看。"),
        "rejected" => PerfTexts.T("这份报告已被后台退回，可在我的记录查看。"),
        "withdrawn" => PerfTexts.T("这份报告已撤回，不会出现在排行榜。"),
        _ => PerfTexts.T("已提交到枕星后台，审核采纳后进入排行榜。")
    };

    public static async Task<List<BenchmarkReportEntry>> GetAllReportsAsync(CancellationToken ct)
    {
        lock (CacheLock) if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(1)) return _cache.ToList();
        var reports = new List<BenchmarkReportEntry>();
        for (int page = 0; page < 2000; page++)
        {
            var batch = await JsonAsync<BenchmarkLeaderboardPage>(Request(HttpMethod.Get, "v1/benchmarks/reports?page=" + page), ct);
            reports.AddRange(batch.Entries.Select(e => e.ToReportEntry()));
            if (page + 1 >= batch.TotalPages) { SaveToCache(reports); return reports; }
        }
        throw new InvalidDataException("Benchmark report list exceeded page limit.");
    }

    public static async Task<BenchmarkLeaderboardPage?> GetLeaderboardPageAsync(string sortBy, int page, CancellationToken ct,
        string? cpuFilter = null, string? gpuFilter = null)
    {
        var path = $"v1/benchmarks/leaderboard?sortBy={Uri.EscapeDataString(sortBy)}&page={page}";
        if (!string.IsNullOrWhiteSpace(cpuFilter)) path += "&cpu=" + Uri.EscapeDataString(cpuFilter.Trim());
        if (!string.IsNullOrWhiteSpace(gpuFilter)) path += "&gpu=" + Uri.EscapeDataString(gpuFilter.Trim());
        var batch = await JsonAsync<BenchmarkLeaderboardPage>(Request(HttpMethod.Get, path), ct);
        if (batch.SortBy != sortBy || batch.Page != page || batch.PageSize != 50 || batch.TotalPages < 0)
            throw new InvalidDataException("Invalid benchmark page.");
        TotalPages[sortBy] = batch.TotalPages;
        return batch;
    }

    public static bool HasMorePages(string sortBy, int currentPage) => TotalPages.TryGetValue(sortBy, out var total) && currentPage + 1 < total;

    public static async Task<List<BenchmarkLeaderboardEntry>> GetLeaderboardAsync(string sortBy, string? cpuFilter = null,
        string? gpuFilter = null, CancellationToken ct = default)
    {
        var entries = new List<BenchmarkLeaderboardEntry>();
        for (var page = 0; page < 2000; page++)
        {
            var batch = (await GetLeaderboardPageAsync(sortBy, page, ct, cpuFilter, gpuFilter))!;
            entries.AddRange(batch.Entries.Select(e => new BenchmarkLeaderboardEntry { Rank = e.Rank, Report = e.ToReportEntry() }));
            if (page + 1 >= batch.TotalPages) return entries;
        }
        throw new InvalidDataException("Benchmark leaderboard exceeded page limit.");
    }

    public static async Task<BenchmarkReportEntry?> GetReportDetailAsync(BenchmarkReportEntry report, CancellationToken ct = default)
        => await JsonAsync<BenchmarkReportEntry>(Request(HttpMethod.Get, "v1/benchmarks/" + Guid.Parse(report.Id).ToString("D"), report.Status == "accepted" ? null : Identity.Read()), ct);

    public static async Task<List<BenchmarkReportEntry>> GetMyReportsAsync(CancellationToken ct)
    {
        var owner = Identity.Read();
        return owner is null ? [] : (await JsonAsync<ReportList>(Request(HttpMethod.Get, "v1/benchmarks/mine", owner), ct)).Reports;
    }

    public static async Task DeleteReportAsync(BenchmarkReportEntry entry, IProgress<string>? progress, CancellationToken ct)
    {
        var owner = Identity.Read() ?? throw new InvalidOperationException(PerfTexts.T("本机没有这份报告的撤回凭据。"));
        progress?.Report(PerfTexts.T("正在撤回报告..."));
        using var request = Request(HttpMethod.Delete, "v1/benchmarks/" + Guid.Parse(entry.Id).ToString("D"), owner);
        using var receipt = await JsonAsync<JsonDocument>(request, ct);
        if (receipt.RootElement.GetProperty("status").GetString() != "withdrawn") throw new InvalidDataException("Invalid withdrawal receipt.");
        InvalidateCache();
    }

    public static async Task<List<LatencyImageInfo>> GetLatencyImagesAsync(CancellationToken ct, bool refresh = false)
    {
        lock (CacheLock) if (!refresh && _images is not null) return _images.ToList();
        var list = (await JsonAsync<ImageList>(Request(HttpMethod.Get, "v1/benchmarks/heatmaps"), ct)).Images;
        foreach (var image in list)
        {
            if (!Guid.TryParseExact(image.Id, "D", out _) || image.Path != $"v1/benchmarks/{image.Id}/heatmap.png" ||
                !Regex.IsMatch(image.Sha, "^[0-9a-f]{64}$")) throw new InvalidDataException("Invalid heatmap metadata.");
        }
        lock (CacheLock) _images = list;
        return list.ToList();
    }

    public static async Task<byte[]> GetLatencyImageBytesAsync(LatencyImageInfo info, CancellationToken ct)
    {
        if (!Guid.TryParseExact(info.Id, "D", out _) || info.Path != $"v1/benchmarks/{info.Id}/heatmap.png" ||
            !Regex.IsMatch(info.Sha, "^[0-9a-f]{64}$")) throw new InvalidDataException("Invalid heatmap metadata.");
        using var request = Request(HttpMethod.Get, info.Path);
        using var response = await Api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Heatmap too large.");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != info.Sha) throw new InvalidDataException("Heatmap hash differs.");
        return bytes;
    }

    private static int Score(BenchmarkReportEntry r, string sortBy) => sortBy switch
    {
        "office" => r.OfficeScore, "cpu" => r.CpuMultiCoreScore, "gpu" => r.GpuRenderScore,
        "memory" => r.MemoryCapacityScore, "disk" => r.DiskSeqReadScore, "browser" => r.BrowserTotalScore,
        "win" => r.WinFinalScore, _ => r.GamingScore
    };

    public static List<BenchmarkLeaderboardEntry> ComputeLeaderboard(List<BenchmarkReportEntry> reports,
        string sortBy, string? cpuFilter = null, string? gpuFilter = null)
        => reports.Where(r => (sortBy is not ("gaming" or "office") || CompleteComposite(r)) && Score(r, sortBy) > 0 && (string.IsNullOrWhiteSpace(cpuFilter) || r.CpuName.Contains(cpuFilter, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(gpuFilter) || r.GpuName.Contains(gpuFilter, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => Score(r, sortBy)).ThenBy(r => r.SubmittedAt).ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select((r, i) => new BenchmarkLeaderboardEntry { Rank = i + 1, SortBy = sortBy, Report = r }).ToList();

    private static bool CompleteComposite(BenchmarkReportEntry r) => r.CpuSingleCoreScore > 0 && r.CpuMultiCoreScore > 0
        && r.GpuRenderScore > 0 && r.MemoryCapacityScore > 0 && r.DiskSeqReadScore > 0 && r.DiskSeqWriteScore > 0
        && r.Disk4KReadScore > 0 && r.Disk4KWriteScore > 0 && r.BrowserTotalScore > 0;

    public static List<BenchmarkLeaderboardEntry> ComputeSameHardwareLeaderboard(List<BenchmarkReportEntry> reports, string cpuName, string gpuName)
    {
        static string Key(string value) => Regex.Replace(Regex.Replace(value.Replace("(R)", "").Replace("(TM)", ""),
            @"\b(CPU|Processor|Graphics|GPU)\b", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(cpuName) || string.IsNullOrWhiteSpace(gpuName)) return [];
        return ComputeLeaderboard(reports.Where(r => string.Equals(Key(r.CpuName), Key(cpuName), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Key(r.GpuName), Key(gpuName), StringComparison.OrdinalIgnoreCase)).ToList(), "gaming");
    }
}
