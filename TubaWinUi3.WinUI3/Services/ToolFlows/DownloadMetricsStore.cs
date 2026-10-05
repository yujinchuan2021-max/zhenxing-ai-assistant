using System.Text;
using System.Text.Json;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>一条结构化计数：类别 + 工具名 + 次数。</summary>
public sealed class DownloadMetricEntry
{
    public string Kind { get; set; } = "";
    public string Tool { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// 下载按钮点击与真实文件下载请求的本地结构化计数（按类别 + 工具名聚合，账号级数据）。
/// 只做本地计数与持久化：不联网、不自动上报；是否累计/发送由分享开关决定
/// （见 <see cref="DownloadMetricsUploader"/>），与工具流共用官方接收入口。
/// <b>开关关闭期间不累计、不保留可供日后上传的计数</b>。
/// <b>文件格式 v2</b>：旧预览版（schema v1）遗留的计数与冻结批次在加载时整体作废
/// （见 <see cref="Load"/>），不会在升级后被上传。
/// 文件有界：条目达到上限后按类别合并到 "*"。全部公开方法静默兜底——统计失败绝不阻断下载/点击路径。
/// </summary>
public sealed class DownloadMetricsStore
{
    public const string ClickedKind = "download_clicked";
    public const string RequestedKind = "download_requested";
    public const string SucceededKind = "download_succeeded";
    public const string FailedKind = "download_failed";

    /// <summary>条目上限（kind×tool 组合）；超过后新组合合并进 tool="*"，保证文件有界。</summary>
    public const int MaxEntries = 400;

    private const int MaxCount = 1_000_000;
    private const int MaxToolLength = 120;

    private static readonly object DefaultGate = new();
    private static DownloadMetricsStore? _default;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>应用内默认实例（真实数据根）；测试请 new 注入假根。</summary>
    public static DownloadMetricsStore Default
    {
        get
        {
            lock (DefaultGate)
            {
                return _default ??= new DownloadMetricsStore();
            }
        }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Func<bool> _uploadEnabled;

    /// <param name="dataRoot">可注入假根；省略时使用客户端数据根。</param>
    /// <param name="uploadEnabled">分享开关读取：关闭期间不累计、不进入待上报批次（测试可注入）。</param>
    public DownloadMetricsStore(string? dataRoot = null, Func<bool>? uploadEnabled = null)
    {
        var root = Path.GetFullPath(dataRoot ?? ConfigManager.GetDataDir());
        _path = Path.Combine(root, "ToolFlows", "DownloadMetrics", "counters.json");
        _uploadEnabled = uploadEnabled ?? DefaultUploadEnabledGate;
    }

    // 默认资格与「选定工具流上报」共用开关；关闭期间不留下可补传的计数。
    private static bool DefaultUploadEnabledGate() => ToolFlowUploadClient.IsUploadEligibleNow();

    public void ReportButtonClicked(string? tool) => Increment(ClickedKind, tool);
    public void ReportRequestStarted(string? tool) => Increment(RequestedKind, tool);
    public void ReportRequestSucceeded(string? tool) => Increment(SucceededKind, tool);
    public void ReportRequestFailed(string? tool) => Increment(FailedKind, tool);

    /// <summary>当前计数快照（kind、tool 升序副本），供构造上传载荷。</summary>
    public IReadOnlyList<DownloadMetricEntry> Snapshot()
    {
        lock (_gate)
        {
            return Load().Entries
                .OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Tool, StringComparer.Ordinal)
                .Select(x => new DownloadMetricEntry { Kind = x.Kind, Tool = x.Tool, Count = x.Count })
                .ToArray();
        }
    }

    /// <summary>确认发送成功后，从本地计数中扣除这份快照（发送期间新增的计数保留）。</summary>
    public void ConfirmSent(IReadOnlyList<DownloadMetricEntry> sent)
    {
        try
        {
            lock (_gate)
            {
                var state = Load();
                foreach (var entry in sent)
                {
                    var match = state.Entries.FirstOrDefault(x => x.Kind == entry.Kind && x.Tool == entry.Tool);
                    if (match is not null) match.Count -= entry.Count;
                }
                state.Entries.RemoveAll(x => x.Count <= 0);
                Save(state);
            }
        }
        catch
        {
            // 扣除失败只影响下次重发（服务端按批次 ID 去重），不抛出。
        }
    }

    /// <summary>
    /// 取待发送批次：已有未确认的冻结批次时原样返回（网络失败重试复用同一批次与同一 ID）；
    /// 否则把当前计数固化成新批次：生成并持久化唯一批次 ID。没有待发计数时返回 null。
    /// 撤销前的冻结批次（磁盘清理失败残留）一律作废，且旧计数经内存门闩的作废快照从新批次中剔除。
    /// 批次未成功落盘（含清槽失败）时返回 null——不发未持久化的批次，避免重复计数。
    /// </summary>
    public MetricsBatch? TryBeginBatch()
    {
        lock (_gate)
        {
            var state = Load();
            if (state.PendingBatch is { Counts.Count: > 0 } frozen &&
                !string.IsNullOrWhiteSpace(frozen.BatchId))
            {
                if (!ToolFlowUploadLatch.IsRevoked(frozen.CreatedAtUtc))
                    return Clone(frozen);

                // 撤销时刻前的批次不得发送：清槽后按作废快照重算新批次。
                state.PendingBatch = null;
                if (!TrySave(state)) return null;
            }

            var counts = ToolFlowUploadLatch.AdjustForVoided(state.Entries)
                .OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Tool, StringComparer.Ordinal)
                .ToList();
            if (counts.Count == 0) return null;

            var batch = new MetricsBatch
            {
                BatchId = Guid.NewGuid().ToString("D"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Counts = counts,
            };
            state.PendingBatch = batch;
            return TrySave(state) ? Clone(batch) : null;
        }
    }

    /// <summary>
    /// 确认批次送达：扣除该批次自身的计数并清除批次槽（冻结后新增的计数保留，进入下一批）。
    /// </summary>
    public void CompleteBatch(MetricsBatch batch)
    {
        try
        {
            lock (_gate)
            {
                var state = Load();
                if (state.PendingBatch is not { } current || current.BatchId != batch.BatchId) return;
                foreach (var entry in batch.Counts)
                {
                    var match = state.Entries.FirstOrDefault(x => x.Kind == entry.Kind && x.Tool == entry.Tool);
                    if (match is not null) match.Count -= entry.Count;
                }
                state.Entries.RemoveAll(x => x.Count <= 0);
                state.PendingBatch = null;
                Save(state);
            }
        }
        catch
        {
            // 与 ConfirmSent 相同：失败只影响下次重发（服务端按批次 ID 去重），不抛出。
        }
    }

    /// <summary>
    /// 关闭分享开关时调用：先立内存级撤销门闩并登记作废计数快照（fail-closed，磁盘清理失败也不再发送旧计数），
    /// 再作废全部未发送计数（含已冻结的待发批次）。返回 false = 磁盘清理失败
    /// （旧计数已由内存门闩在本次运行内阻止发送；重新开启前会再次尝试作废，未完成时开关将保持关闭）。
    /// 重新开启后从当时的新事件重新累计。
    /// </summary>
    public bool ClearPending()
    {
        ToolFlowUploadLatch.RevokeAll();
        try
        {
            lock (_gate)
            {
                var state = Load();
                // 先登记作废快照：若下面的落盘失败，旧计数不会随新批次发送。
                ToolFlowUploadLatch.VoidCounts(state.Entries);
                state.Entries.Clear();
                state.PendingBatch = null;
                Save(state);
                // 磁盘清理成功：无需再作废任何计数。
                ToolFlowUploadLatch.VoidCounts([]);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySave(MetricsState state)
    {
        try
        {
            Save(state);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MetricsBatch Clone(MetricsBatch batch) => new()
    {
        BatchId = batch.BatchId,
        CreatedAtUtc = batch.CreatedAtUtc,
        Counts = batch.Counts
            .Select(x => new DownloadMetricEntry { Kind = x.Kind, Tool = x.Tool, Count = x.Count })
            .ToList(),
    };

    private void Increment(string kind, string? tool)
    {
        try
        {
            // 分享开关关闭期间不累计待上报计数：
            // 重新具备资格后从当时的新事件计起，不补传更早的点击。
            if (!_uploadEnabled()) return;
            lock (_gate)
            {
                var state = Load();
                var name = NormalizeTool(tool);
                var match = state.Entries.FirstOrDefault(x => x.Kind == kind && x.Tool == name);
                if (match is null && state.Entries.Count >= MaxEntries)
                {
                    name = "*";
                    match = state.Entries.FirstOrDefault(x => x.Kind == kind && x.Tool == name);
                }
                if (match is null)
                {
                    match = new DownloadMetricEntry { Kind = kind, Tool = name };
                    state.Entries.Add(match);
                }
                if (match.Count < MaxCount) match.Count++;
                Save(state);
            }
        }
        catch
        {
            // 统计失败必须静默：绝不阻断下载/点击路径。
        }
    }

    private static string NormalizeTool(string? tool)
    {
        var text = (tool ?? "").Trim();
        if (text.Length == 0) return "*";
        if (text.Length > MaxToolLength) text = text[..MaxToolLength];
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "*" : cleaned;
    }

    private MetricsState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var state = JsonSerializer.Deserialize<MetricsState>(File.ReadAllText(_path), JsonOptions);
                if (state is { SchemaVersion: 2, Entries: not null }) return state;

                // 旧预览版（schema v1 及更早）遗留文件：计数与冻结批次整体作废，绝不允许在
                // 升级/重开开关后被上传。一次性清空为 v2 空状态（best-effort：
                // 即使清理失败，本函数也只会返回空状态，旧内容不进入任何上传路径）。
                try { Save(new MetricsState()); } catch { }
            }
        }
        catch { }
        return new MetricsState();
    }

    private void Save(MetricsState state)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory,
            "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(state, JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public sealed class MetricsState
    {
        /// <summary>文件格式版本：2 = 当前。旧预览版 v1 文件在加载时整体作废（一次性清空）。</summary>
        public int SchemaVersion { get; set; } = 2;
        public List<DownloadMetricEntry> Entries { get; set; } = [];
        /// <summary>未确认送达的冻结批次（含唯一 ID 与冻结时的计数）；收到 2xx 后清空。</summary>
        public MetricsBatch? PendingBatch { get; set; }
    }

    /// <summary>一次待发送的计数批次：ID 每个新批次生成一次并持久化，失败重试时复用。</summary>
    public sealed class MetricsBatch
    {
        public string BatchId { get; set; } = "";
        /// <summary>批次创建（冻结）时刻：撤销时刻及之前的批次不得发送；旧文件缺省为 MinValue（按最旧处理）。</summary>
        public DateTimeOffset CreatedAtUtc { get; set; }
        public List<DownloadMetricEntry> Counts { get; set; } = [];
    }
}
