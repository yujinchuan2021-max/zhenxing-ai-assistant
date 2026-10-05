using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using TubaWinUi3.Services.ToolFlows;
using Downloader;
using Microsoft.UI.Dispatching;
using Microsoft.Toolkit.Uwp.Notifications;
using TubaWinUi3.Models;

namespace TubaWinUi3.Services;

/// <summary>
/// 下载队列：底层引擎为 Downloader 库（github.com/bezzad/Downloader，MIT）。
/// 每个文件按 ChunkCount 分块并行下载；暂停/失败/退出应用后半成品
/// （目标文件名 + .download 侧车文件，内嵌分块元数据）保留在磁盘上，
/// 再次启动（Resume/Retry 或跨会话）时由 Downloader 自动断点续传。
/// </summary>
public static class DownloadQueueService
{
    private const int MaxConcurrentDownloads = 2;
    private const int ChunkCount = 8;                 // 单文件分块数（服务器不支持 Range 时自动退化为单连接）
    private const int ParallelChunkCount = 4;         // 单文件同时活动的分块连接数
    private const int ProgressThrottleMs = 300;
    private const string PartialSuffix = ".tubadl";             // 旧版手写引擎的半成品后缀，仅做兼容清理
    private const string DownloaderPartialSuffix = ".download"; // Downloader 半成品侧车文件
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private static readonly SemaphoreSlim _semaphore = new(MaxConcurrentDownloads);
    private static readonly ObservableCollection<DownloadItem> _queue = [];
    private static readonly Dictionary<string, Task> _activeTasks = [];
    private static readonly Dictionary<string, int> _multiFileAutoRetries = [];
    private const int MaxMultiFileAutoRetries = 6;
    private static int _pendingCount;
    private static DispatcherQueue? _dispatcherQueue;
#pragma warning disable CS0414
    private static bool _dirty;
#pragma warning restore CS0414
    private static readonly object _saveLock = new();

    public static void Initialize(DispatcherQueue dq)
    {
        _dispatcherQueue = dq;
        PostProcessorRegistry.RegisterDefaults();
        _ = Task.Run(() => LoadQueue());
    }

    public static ObservableCollection<DownloadItem> Queue => _queue;
    public static event Action? QueueChanged;

    public static int PendingCount => _pendingCount;

    /// <summary>
    /// 【GUI 隔离·恢复链】最近一次 <see cref="RestorePersistedQueue"/> 中被显式拒绝恢复的条目
    /// （"显示名 → 原目标"）。隔离态专用诊断：目标无法安全解析（树外/空路径）时拒绝入队；
    /// 生产态恒为空（路径原样、不拒绝任何条目）。
    /// </summary>
    internal static IReadOnlyList<string> LastRestoreRefusals { get; private set; } = [];

    public static DownloadItem Enqueue(
        string displayName,
        string downloadUrl,
        string destinationPath,
        IDownloadPostProcessor? postProcessor = null,
        string? description = null,
        string? glyph = null,
        object? tag = null)
    {
        var item = DownloadItem.CreateDirect(displayName, downloadUrl, destinationPath,
            postProcessor, description, glyph, tag);
        AddAndStart(item);
        return item;
    }

    public static DownloadItem EnqueueWithResolver(
        string displayName,
        Func<CancellationToken, Task<ResolvedDownloadUrl>> urlResolver,
        string destinationPath,
        IDownloadPostProcessor? postProcessor = null,
        string? description = null,
        string? glyph = null,
        object? tag = null,
        string? fallbackUrl = null)
    {
        var item = DownloadItem.CreateWithResolver(displayName, urlResolver, destinationPath,
            postProcessor, description, glyph, tag, fallbackUrl);
        AddAndStart(item);
        return item;
    }

    public static DownloadItem EnqueueMultiFile(
        string displayName,
        Func<CancellationToken, Task<List<ResolvedDownloadUrl>>> multiFileResolver,
        string destinationPath,
        IDownloadPostProcessor? postProcessor = null,
        string? description = null,
        string? glyph = null,
        object? tag = null)
    {
        var item = DownloadItem.CreateMultiFile(displayName, multiFileResolver, destinationPath,
            postProcessor, description, glyph, tag);
        AddAndStart(item);
        return item;
    }

    public static void Pause(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null) return;
        if (item.State is not (DownloadItemState.Downloading or DownloadItemState.Queued)) return;

        item.Cts?.Cancel();
    }

    public static void Resume(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null) return;
        if (item.State != DownloadItemState.Paused) return;

        item.PrepareResume();
        item.SetState(DownloadItemState.Queued);
        StartItemAsync(item);
    }

    public static void Cancel(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null) return;

        item.Cts?.Cancel();
        if (item.State is DownloadItemState.Queued or DownloadItemState.Resolving)
        {
            _multiFileAutoRetries.Remove(itemId);
            DispatchState(item, DownloadItemState.Cancelled);
            DecrementPending();
            CleanupPartialFile(item);
            MarkDirty();
        }
        else if (item.State is DownloadItemState.Paused)
        {
            _multiFileAutoRetries.Remove(itemId);
            DispatchState(item, DownloadItemState.Cancelled);
            CleanupPartialFile(item);
            MarkDirty();
        }
    }

    public static void Retry(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null) return;
        if (item.State is not (DownloadItemState.Failed or DownloadItemState.Cancelled)) return;

        _multiFileAutoRetries.Remove(itemId);
        item.Reset();
        IncrementPending();
        StartItemAsync(item);
    }

    public static void Remove(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null) return;
        if (item.State is DownloadItemState.Downloading or DownloadItemState.Processing or DownloadItemState.Resolving)
        {
            item.Cts?.Cancel();
            return;
        }

        var wasPending = item.State is DownloadItemState.Queued or DownloadItemState.Resolving
            or DownloadItemState.Downloading or DownloadItemState.Processing or DownloadItemState.Paused;
        _queue.Remove(item);
        _multiFileAutoRetries.Remove(itemId);
        if (wasPending) DecrementPending();
        CleanupPartialFile(item);
        MarkDirty();
        QueueChanged?.Invoke();
    }

    public static void DeleteFile(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null) return;
        if (item.State is not DownloadItemState.Completed) return;

        try
        {
            // 【GUI 隔离·物理边界】删除执行前复查目标目录物理链；不可信 → 关闭该操作（保留条目，不触碰路径）
            if (!DataRoots.IsIsolationTargetPhysicallySafe(item.DestinationPath, out _)) return;

            var candidate = item.ResolvedFileName ?? SanitizeFileName(item.DisplayName);
            // 【GUI 隔离·恢复链·文件名校验】删除路径统一经严格解析：不合法（绝对路径/分隔符/..）→ 拒绝删除
            if (TryResolveSafeFilePath(item.DestinationPath, candidate, out _, out var filePath)
                && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch { }

        _queue.Remove(item);
        MarkDirty();
        QueueChanged?.Invoke();
    }

    public static void ClearCompleted()
    {
        var toRemove = _queue.Where(i =>
            i.State is DownloadItemState.Completed or DownloadItemState.Failed or DownloadItemState.Cancelled)
            .ToList();
        foreach (var item in toRemove)
            _queue.Remove(item);
        MarkDirty();
        QueueChanged?.Invoke();
    }

    public static void SaveQueue()
    {
        lock (_saveLock)
        {
            try
            {
                var entries = _queue.Select(ToEntry).ToList();
                var json = JsonSerializer.Serialize(entries, _jsonOpts);
                var path = ConfigManager.GetDownloadQueuePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                _dirty = false;
            }
            catch { }
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{(double)bytes / (1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{(double)bytes / (1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{(double)bytes / (1L << 10):F1} KB";
        return $"{bytes} B";
    }

    public static string FormatSpeed(double mbps)
    {
        if (mbps >= 1000) return $"{mbps / 1000:F2} Gbps";
        if (mbps >= 1) return $"{mbps:F2} Mbps";
        return $"{mbps * 1000:F0} Kbps";
    }

    public static string FormatTime(TimeSpan? time)
    {
        if (time is null || time.Value.TotalSeconds <= 0) return "--";
        var t = time.Value;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    private static void LoadQueue()
    {
        try { RestorePersistedQueue(ConfigManager.GetDownloadQueuePath()); }
        catch { }
    }

    /// <summary>
    /// 持久化下载队列恢复入口（启动时 Initialize 调用）。internal 供隔离测试直接调用：
    /// 测试传临时文件路径，绝不触碰真实 %LOCALAPPDATA% 队列文件。返回实际恢复（加入队列）的条目数。
    /// 【A15】独立发行：上游内核包任务一律不恢复——不回队、不续传、不解压替换（持久下载恢复入口闸门）。
    /// </summary>
    internal static int RestorePersistedQueue(string queueFilePath)
    {
        try
        {
            LastRestoreRefusals = [];
            var path = queueFilePath;
            if (!File.Exists(path)) return 0;

            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<DownloadQueueEntry>>(json);
            if (entries is null) return 0;

            var items = new List<DownloadItem>();
            var refusals = new List<string>();

            foreach (var entry in entries)
            {
                DownloadItem? item = null;
                var postProcessor = PostProcessorRegistry.Find(entry.PostProcessorKey);

                // 【A15】独立发行：上游内核包任务不从持久化队列恢复（后处理器识别 + 资产名兜底识别）
                if (ToolsBundleService.IsBlockedUpstreamBundleTask(postProcessor, entry.DirectUrl))
                    continue;

                if (!string.IsNullOrEmpty(entry.DirectUrl))
                {
                    // 【GUI 隔离·恢复链收口】恢复旧持久化条目时，下载目标必须先按现有隔离规则解析：
                    //  · 已在可写根内 → 原样；
                    //  · 随包 Tools 树内旧路径 → 映射到可写根（ZXAI_DATA_ROOT\Tools）；
                    //  · 树外/空路径 → 无法安全解析 → 显式拒绝恢复（不入队、不建目录、不续传），
                    //    防止旧随包 Tools 目标经「恢复 → 续传」在下载路径上被重新建目录/写入。
                    // 生产态（无隔离根）保持原有语义：路径原样、不拒绝任何条目。
                    var destinationPath = entry.DestinationPath;
                    if (DataRoots.EffectiveTestRoot is not null)
                    {
                        if (!ToolCatalog.TryResolveDownloadTarget(destinationPath, out var resolvedDestination))
                        {
                            refusals.Add($"{entry.DisplayName} → {destinationPath}");
                            continue;
                        }
                        destinationPath = resolvedDestination;
                        // 【GUI 隔离·物理边界】恢复入队前逐次校验目标目录物理链（隔离根→目标不得含 junction/symlink）
                        if (!DataRoots.IsIsolationTargetPhysicallySafe(destinationPath, out var physReason))
                        {
                            refusals.Add(MiscTexts.TSub($"{entry.DisplayName} → {destinationPath}（{physReason}）"));
                            continue;
                        }
                    }

                    item = DownloadItem.CreateDirect(
                        entry.DisplayName, entry.DirectUrl, destinationPath,
                        postProcessor, entry.Description, entry.Glyph);
                }

                if (item is null) continue;

                item.Id = entry.Id;
                item.ResolvedUrl = entry.ResolvedUrl;
                // 【GUI 隔离·恢复链·文件名校验】持久化 ResolvedFileName 只在“单个安全文件名”时接受；
                // 绝对路径 / 分隔符 / "."-".." / 尾点尾空格等不合法值一律不采纳（回落按 DisplayName 派生），
                // 防 清理（.tubadl）/ 续传落点 / 删除 三处路径经该字段越界。
                item.ResolvedFileName = TryValidateFileNameComponent(entry.ResolvedFileName, out var safeResolvedName)
                    ? safeResolvedName
                    : null;
                item.ResolvedSize = entry.ResolvedSize;

                if (entry.State == DownloadItemState.Paused)
                {
                    item.SetState(DownloadItemState.Paused);
                    item.ResumePosition = entry.BytesReceived;
                    if (entry.TotalBytes > 0)
                        item.SetProgress(new DownloadQueueProgress(entry.BytesReceived, entry.TotalBytes,
                            entry.TotalBytes > 0 ? (double)entry.BytesReceived / entry.TotalBytes * 100 : 0, 0, null));
                    IncrementPending();
                    CleanupLegacyPartialFile(item);
                }
                else if (entry.State == DownloadItemState.Completed)
                {
                    if (entry.CompletedAt.HasValue)
                        item.CompletedAt = entry.CompletedAt;
                    item.SetState(DownloadItemState.Completed);
                }
                else if (entry.State == DownloadItemState.Downloading
                    || entry.State == DownloadItemState.Queued
                    || entry.State == DownloadItemState.Resolving)
                {
                    item.SetState(DownloadItemState.Paused);
                    item.ResumePosition = entry.BytesReceived;
                    if (entry.TotalBytes > 0)
                        item.SetProgress(new DownloadQueueProgress(entry.BytesReceived, entry.TotalBytes,
                            entry.TotalBytes > 0 ? (double)entry.BytesReceived / entry.TotalBytes * 100 : 0, 0, null));
                    IncrementPending();
                    CleanupLegacyPartialFile(item);
                }
                else
                {
                    item.SetState(entry.State);
                    if (!string.IsNullOrEmpty(entry.ErrorMessage))
                        item.SetErrorMessage(entry.ErrorMessage);
                }

                items.Add(item);
            }

            LastRestoreRefusals = refusals;

            if (_dispatcherQueue is not null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var item in items)
                        _queue.Add(item);
                    QueueChanged?.Invoke();
                });
            }
            else
            {
                foreach (var item in items)
                    _queue.Add(item);
                QueueChanged?.Invoke();
            }

            return items.Count;
        }
        catch { return 0; }
    }

    /// <summary>旧版手写引擎的 .tubadl 半成品无法被 Downloader 续传，启动恢复时直接清理。</summary>
    private static void CleanupLegacyPartialFile(DownloadItem item)
    {
        try
        {
            // 【GUI 隔离·物理边界】清理执行前复查目标目录物理链；不可信 → 关闭该操作
            if (!DataRoots.IsIsolationTargetPhysicallySafe(item.DestinationPath, out _)) return;

            var candidate = item.ResolvedFileName ?? SanitizeFileName(item.DisplayName);
            // 【GUI 隔离·恢复链·文件名校验】清理目标统一经严格解析；不合法 → 拒绝清理（不触碰越界路径）
            if (!TryResolveSafeFilePath(item.DestinationPath, candidate, out _, out var targetPath)) return;
            var legacyPartial = targetPath + PartialSuffix;
            if (File.Exists(legacyPartial))
                File.Delete(legacyPartial);
        }
        catch { }
    }

    private static void MarkDirty()
    {
        _dirty = true;
        _dispatcherQueue?.TryEnqueue(SaveQueue);
    }

    private static DownloadQueueEntry ToEntry(DownloadItem item)
    {
        return new DownloadQueueEntry
        {
            Id = item.Id,
            DisplayName = item.DisplayName,
            Description = item.Description,
            Glyph = item.Glyph,
            DestinationPath = item.DestinationPath,
            DirectUrl = item.DirectUrl,
            State = item.State,
            ResolvedUrl = item.ResolvedUrl,
            ResolvedFileName = item.ResolvedFileName,
            ResolvedSize = item.ResolvedSize,
            BytesReceived = item.Progress?.BytesReceived ?? item.ResumePosition,
            TotalBytes = item.Progress?.TotalBytes ?? 0,
            PostProcessorKey = PostProcessorRegistry.GetKey(item.PostProcessor),
            ErrorMessage = item.ErrorMessage,
            CompletedAt = item.CompletedAt
        };
    }

    private static void AddAndStart(DownloadItem item)
    {
        // 【GUI 隔离】Tools 树内的下载/解压目标一律改道可写根（ZXAI_DATA_ROOT\Tools）：防止任何
        // 调用方（首页下载对话框、内置工具安装、工具更新链）把随包 Tools 当写入目标；
        // 生产态恒等返回；隔离态树外路径保持调用方语义（队列为通用组件）。
        if (ToolCatalog.TryResolveWritableToolsPath(item.DestinationPath, out var mappedDest))
            item.DestinationPath = mappedDest;

        // 【A15】独立发行：上游内核包任务一律不入队、不启动、不弹「已加入下载队列」提示。
        // 对话框（ToolsBundleDownloadDialog）经此入队，闸门关闭时这条真实路径在此被截断。
        if (ToolsBundleService.IsBlockedUpstreamBundleTask(item.PostProcessor, item.DirectUrl))
        {
            BlockUpstreamBundleItem(item, decrementPending: false);
            return;
        }

        item.Cts = new CancellationTokenSource();
        _queue.Insert(0, item);
        IncrementPending();
        MarkDirty();
        StartItemAsync(item);

        ShowToast(MiscTexts.T("已加入下载队列"), MiscTexts.TSub($"\"{item.DisplayName}\" 已开始下载"));
    }

    /// <summary>
    /// 【A15】上游内核包任务被闸门拦下时的统一收尾：标记为已取消并给出停用原因
    /// （不静默失败、不留在待办计数里、清理可能残留的半成品），保证零请求、零替换。
    /// </summary>
    private static void BlockUpstreamBundleItem(DownloadItem item, bool decrementPending)
    {
        DispatchState(item, DownloadItemState.Cancelled);
        DispatchError(item, ToolsBundleService.UpstreamBundleDisabledMessage);
        CleanupPartialFile(item);
        if (decrementPending) DecrementPending();
        MarkDirty();
    }

    private static async void StartItemAsync(DownloadItem item)
    {
        // 【A15】兜底闸门：上游内核包任务绝不出队执行——覆盖入队启动 / 暂停续传 / 失败重试 /
        // 跨会话恢复后的续传，闸门关闭时零请求、零替换（任何入口漏网也走不到网络层）。
        if (ToolsBundleService.IsBlockedUpstreamBundleTask(item.PostProcessor, item.DirectUrl))
        {
            BlockUpstreamBundleItem(item, decrementPending: true);
            return;
        }

        try
        {
            await _semaphore.WaitAsync(item.Cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OnDownloadPaused(item);
            return;
        }

        if (item.State is DownloadItemState.Cancelled)
        {
            _semaphore.Release();
            return;
        }

        var task = ProcessItemAsync(item);
        lock (_activeTasks)
            _activeTasks[item.Id] = task;
    }

    private static void OnDownloadPaused(DownloadItem item)
    {
        if (item.State is not (DownloadItemState.Downloading or DownloadItemState.Queued or DownloadItemState.Resolving))
            return;

        var pos = item.ResumePosition;
        if (item.Progress is not null && item.Progress.BytesReceived > pos)
            pos = item.Progress.BytesReceived;
        item.ResumePosition = pos;
        DispatchState(item, DownloadItemState.Paused);
        DecrementPending();
        MarkDirty();
    }

    private static async Task ProcessItemAsync(DownloadItem item)
    {
        var ct = item.Cts?.Token ?? CancellationToken.None;
        var newFilesThisPass = 0;
        try
        {
            if (item.MultiFileResolver is not null)
            {
                newFilesThisPass = await ProcessMultiFileAsync(item, ct);
                _multiFileAutoRetries.Remove(item.Id);
            }
            else
            {
                if (item.ResolvedUrl is null)
                {
                    DispatchState(item, DownloadItemState.Resolving);
                    var resolved = await ResolveUrlAsync(item, ct);
                    item.ResolvedUrl = resolved.Url;
                    item.ResolvedFileName = resolved.FileName;
                    item.ResolvedSize = resolved.Size;
                    MarkDirty();
                }

                ct.ThrowIfCancellationRequested();

                if (item.State != DownloadItemState.Downloading)
                    DispatchState(item, DownloadItemState.Downloading);
                var downloadedFile = await DownloadFileWithFallbackAsync(item, ct);

                ct.ThrowIfCancellationRequested();

                await RunPostProcessorAsync(item, downloadedFile, ct);
            }

            DispatchCompleted(item);
        }
        catch (OperationCanceledException)
        {
            OnDownloadPaused(item);
        }
        catch (Exception ex)
        {
            // 多文件任务（UUP 文件集等）：微软 CDN 直链有效期只有约 15 分钟，长任务中
            // 后续文件的链接可能已过期。与官方 aria2 脚本的做法一致：重新解析文件列表
            // 换新链接再跑一遍，已按大小校验完成的文件会自动跳过。本轮有进展时重新计数，
            // 避免同一处反复失败造成死循环。
            if (item.MultiFileResolver is not null)
            {
                if (newFilesThisPass > 0)
                    _multiFileAutoRetries.Remove(item.Id);

                if (CanAutoRetryMultiFile(item))
                {
                    var reason = ex.InnerException?.Message ?? ex.Message;
                    if (reason.Length > 120) reason = reason[..120] + "...";
                    DispatchProcessingStatus(item, MiscTexts.TSub($"下载中断（{reason}），正在刷新下载列表并自动重试..."));
                    await Task.Delay(3000).ConfigureAwait(false);
                    StartItemAsync(item);
                    return;
                }

                _multiFileAutoRetries.Remove(item.Id);
            }

            var errorMsg = ex.InnerException?.Message ?? ex.Message;
            DispatchError(item, errorMsg);
            DispatchState(item, DownloadItemState.Failed);
            DecrementPending();
            MarkDirty();

            ShowToast(MiscTexts.T("下载失败"), MiscTexts.TSub($"\"{item.DisplayName}\" 下载失败：{errorMsg}"));
        }
        finally
        {
            _semaphore.Release();
            lock (_activeTasks)
                _activeTasks.Remove(item.Id);
            QueueChanged?.Invoke();
        }
    }

    private static bool CanAutoRetryMultiFile(DownloadItem item)
    {
        lock (_activeTasks)
        {
            _multiFileAutoRetries.TryGetValue(item.Id, out var count);
            if (count >= MaxMultiFileAutoRetries) return false;
            _multiFileAutoRetries[item.Id] = count + 1;
            return true;
        }
    }

    /// <summary>逐个下载文件清单；返回本轮实际新下载的文件数（已存在且大小一致的文件直接跳过）。</summary>
    private static async Task<int> ProcessMultiFileAsync(DownloadItem item, CancellationToken ct)
    {
        DispatchState(item, DownloadItemState.Resolving);
        var files = await item.MultiFileResolver!(ct);

        if (files.Count == 0)
        {
            if (item.PostProcessor is not null)
            {
                DispatchState(item, DownloadItemState.Processing);
                DispatchProcessingStatus(item, item.PostProcessor.DisplayName);
                var progress = new Progress<string>(status => DispatchProcessingStatus(item, status));
                await item.PostProcessor.ExecuteAsync(item.DestinationPath, item.DestinationPath, progress, ct);
            }
            return 0;
        }

        DispatchState(item, DownloadItemState.Downloading);

        var newFiles = 0;
        long completedBytes = 0;   // 已完成文件的累计字节
        long knownTotal = 0;       // 已知文件的累计总字节

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];

            // 【GUI 隔离·物理边界】多文件落点比任务目录更深：先做相对路径规范化与字面范围校验（拒绝 rooted、":"、".." 段），
            // 再对**实际父目录**逐段复查物理链（可能到 DestinationPath 之下的 sub 目录）；任何一步不过 →
            // 在 CreateDirectory / File.Exists / DeleteLegacyPartial / 下载之前拒绝
            //（物理检查隔离态外恒过；字面范围校验两模式统一）。
            var localPath = TryResolveSafeMultiFilePath(item.DestinationPath, file.FileName);
            if (localPath is null
                || !DataRoots.IsIsolationTargetPhysicallySafe(Path.GetDirectoryName(localPath), out var physReasonLoop))
            {
                throw new InvalidOperationException(
                    MiscTexts.TSub($"下载落点不安全或隔离目标链不可信，已拒绝：{item.DestinationPath} | {file.FileName}"));
            }

            Directory.CreateDirectory(item.DestinationPath);

            var localDir = Path.GetDirectoryName(localPath);
            if (localDir is not null) Directory.CreateDirectory(localDir);

            // 大小一致且已存在的文件直接跳过：重试 / 恢复会话时不重复下载
            if (file.Size > 0 && File.Exists(localPath) && new FileInfo(localPath).Length == file.Size)
            {
                completedBytes += file.Size;
                knownTotal += file.Size;
                ReportAggregatedProgress(item, completedBytes, knownTotal, 0);
                continue;
            }

            DeleteLegacyPartial(localPath);

            long fileTotal = file.Size;
            await DownloadWithDownloaderAsync(item, file.Url, localPath, ct,
                onStarted: total => fileTotal = total > 0 ? total : file.Size,
                onProgress: e =>
                {
                    fileTotal = e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive : fileTotal;
                    ReportAggregatedProgress(item, completedBytes + e.ReceivedBytesSize,
                        knownTotal + fileTotal, e.BytesPerSecondSpeed);
                });

            var actualSize = File.Exists(localPath) ? new FileInfo(localPath).Length : fileTotal;
            completedBytes += actualSize;
            knownTotal += actualSize;
            newFiles++;
        }

        ReportAggregatedProgress(item, completedBytes, completedBytes, 0);

        ct.ThrowIfCancellationRequested();

        if (item.PostProcessor is not null)
        {
            DispatchState(item, DownloadItemState.Processing);
            DispatchProcessingStatus(item, item.PostProcessor.DisplayName);
            var progress = new Progress<string>(status => DispatchProcessingStatus(item, status));
            await item.PostProcessor.ExecuteAsync(item.DestinationPath, item.DestinationPath, progress, ct);
        }

        return newFiles;
    }

    private static void DeleteLegacyPartial(string finalPath)
    {
        try
        {
            var legacyPartial = finalPath + PartialSuffix;
            if (File.Exists(legacyPartial)) File.Delete(legacyPartial);
        }
        catch { }
    }

    private static async Task RunPostProcessorAsync(DownloadItem item, string downloadedFile, CancellationToken ct)
    {
        if (item.PostProcessor is null) return;
        DispatchState(item, DownloadItemState.Processing);
        DispatchProcessingStatus(item, item.PostProcessor.DisplayName);
        var progress = new Progress<string>(status => DispatchProcessingStatus(item, status));
        await item.PostProcessor.ExecuteAsync(downloadedFile, item.DestinationPath, progress, ct);
    }

    /// <summary>
    /// 优先使用主下载源（GitCode），失败时先自动重下一次（续传半成品），
    /// 仍失败则切换备用源（GitHub）重试，并校验 zip 完整性后再交给解压。
    /// </summary>
    private static async Task<string> DownloadFileWithFallbackAsync(DownloadItem item, CancellationToken ct)
    {
        var alternate = !string.IsNullOrEmpty(item.AlternateUrl) &&
                        !string.Equals(item.AlternateUrl, item.ResolvedUrl, StringComparison.OrdinalIgnoreCase)
            ? item.AlternateUrl
            : null;

        // 第 1 次：主源（默认 GitCode）
        try
        {
            return await DownloadAndValidateAsync(item, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception primaryEx)
        {
            // 第 2 次：主源重试一次（网络波动常见；半成品保留，由 Downloader 断点续传）
            DispatchProcessingStatus(item, MiscTexts.T("下载中断，正在自动重试..."));
            try
            {
                return await DownloadAndValidateAsync(item, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception retryEx) when (alternate is not null)
            {
                // 第 3 次：切换备用源（GitHub）
                DispatchProcessingStatus(item, MiscTexts.T("GitCode 下载失败，正在切换 GitHub 重试..."));
                item.ResolvedUrl = alternate;
                item.ResumePosition = 0;
                MarkDirty();
                try
                {
                    return await DownloadAndValidateAsync(item, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception altEx)
                {
                    throw new InvalidOperationException(
                        MiscTexts.TSub($"主源下载失败：{primaryEx.InnerException?.Message ?? primaryEx.Message}；重试失败：{retryEx.InnerException?.Message ?? retryEx.Message}；备用源失败：{altEx.InnerException?.Message ?? altEx.Message}"),
                        altEx);
                }
            }
        }
    }

    private static async Task<string> DownloadAndValidateAsync(DownloadItem item, CancellationToken ct)
    {
        // 【GUI 隔离·物理边界】执行前复查目标目录物理链；不可信 → 显式拒绝下载（fail-closed）
        if (!DataRoots.IsIsolationTargetPhysicallySafe(item.DestinationPath, out var physReason))
            throw new InvalidOperationException(MiscTexts.TSub($"隔离目标链不可信（{physReason}），已拒绝下载：{item.DestinationPath}"));

        // 【GUI 隔离·恢复链·文件名校验】下载落点统一经严格解析（同时覆盖运行时解析出的文件名）；
        // 不合法（绝对路径/分隔符/..）→ 显式拒绝下载，绝不越出映射后目录。
        var candidate = item.ResolvedFileName ?? SanitizeFileName(item.DisplayName);
        if (!TryResolveSafeFilePath(item.DestinationPath, candidate, out _, out var finalPath))
            throw new InvalidOperationException(MiscTexts.TSub($"下载文件名不安全，已拒绝：{candidate}"));

        // 已存在的半成品大小（含侧车元数据）用于恢复时初始进度展示
        var partialPath = finalPath + DownloaderPartialSuffix;
        var partialBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        await DownloadWithDownloaderAsync(item, item.ResolvedUrl!, finalPath, ct,
            onStarted: total =>
            {
                if (total > 0)
                {
                    item.ResolvedSize = total;
                    if (partialBytes > 0)
                        ReportAggregatedProgress(item, partialBytes, total, 0);
                }
            },
            onProgress: e => HandleSingleFileProgress(item, e));

        ValidateDownloadedFile(finalPath);
        return finalPath;
    }

    /// <summary>
    /// 用 Downloader 引擎把 <paramref name="url"/> 下载到 <paramref name="finalPath"/>。
    /// 成功正常返回；取消/暂停抛 <see cref="OperationCanceledException"/>；
    /// 失败抛完成事件携带的异常。半成品（finalPath.download）由 Downloader 自动管理，
    /// 存在且服务器文件大小未变时自动续传。
    /// </summary>
    private static async Task DownloadWithDownloaderAsync(DownloadItem item, string url, string finalPath,
        CancellationToken ct,
        Action<long>? onStarted = null,
        Action<DownloadProgressChangedEventArgs>? onProgress = null)
    {
        // 【GUI 隔离·物理边界】建目录/写入前最后一道（单文件与多文件链共用）：以**实际写入目录**
        //（finalPath 的父目录，多文件链可能比任务 DestinationPath 更深）复查物理链，不得含 junction/symlink
        var writeDir = Path.GetDirectoryName(finalPath) ?? item.DestinationPath;
        if (!DataRoots.IsIsolationTargetPhysicallySafe(writeDir, out var physReasonW))
            throw new InvalidOperationException(MiscTexts.TSub($"隔离目标链不可信（{physReasonW}），已拒绝写入：{writeDir}"));

        Directory.CreateDirectory(item.DestinationPath);
        var service = new DownloadService(CreateDownloadConfiguration());

        AsyncCompletedEventArgs? completion = null;
        service.DownloadFileCompleted += (_, e) => completion = e;
        if (onStarted is not null)
            service.DownloadStarted += (_, e) => onStarted(e.TotalBytesToReceive);
        if (onProgress is not null)
            service.DownloadProgressChanged += (_, e) => onProgress(e);

        // 【结构化统计】真实文件下载请求（含重试尝试）：开始/成功/失败分开计数；统计失败不得影响下载。
        var metricName = Path.GetFileName(finalPath);
        DownloadMetricsStore.Default.ReportRequestStarted(metricName);
        try
        {
            try
            {
                await service.DownloadFileTaskAsync(url, finalPath, ct).ConfigureAwait(false);
            }
            finally
            {
                await service.DisposeAsync().ConfigureAwait(false);
            }

            if (completion is null)
                throw new InvalidOperationException(MiscTexts.T("下载未返回完成状态"));
            if (completion.Cancelled)
                throw new OperationCanceledException();
            if (completion.Error is not null)
                throw completion.Error;

            DownloadMetricsStore.Default.ReportRequestSucceeded(metricName);
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消/暂停不是失败
        }
        catch
        {
            DownloadMetricsStore.Default.ReportRequestFailed(metricName);
            throw;
        }
    }

    /// <summary>
    /// 若是 zip，校验压缩包完整性（遍历并打开每个条目，验证本地文件头）。
    /// 损坏则删除并抛异常，触发自动重下（重下时 Downloader 会清掉损坏的完整文件）。
    /// </summary>
    private static void ValidateDownloadedFile(string filePath)
    {
        if (!filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(filePath)) return;

        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(filePath);
            foreach (var entry in archive.Entries)
            {
                using var s = entry.Open();
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(filePath); } catch { }
            throw new InvalidDataException(MiscTexts.TSub($"下载的压缩包已损坏（{ex.Message}）"), ex);
        }
    }

    private static DownloadConfiguration CreateDownloadConfiguration() => new()
    {
        BufferBlockSize = 64 * 1024,
        ChunkCount = ChunkCount,
        ParallelCount = ParallelChunkCount,
        ParallelDownload = true,
        MaxTryAgainOnFailure = 3,                       // 分块级自动重试（指数退避）
        MinimumSizeOfChunking = 1024 * 1024,            // 小于 1MB 不分块
        EnableAutoResumeDownload = true,                // 半成品内嵌分块元数据，跨会话续传
        ClearPackageOnCompletionWithFailure = false,    // 失败保留半成品，重试可续传
        FileExistPolicy = FileExistPolicy.Delete,       // 目标完整文件已存在则删除重下
        DownloadFileExtension = DownloaderPartialSuffix,
        HttpClientTimeout = 2 * 60 * 60 * 1000,         // 默认 100s 会误杀慢速大文件流
        MaximumMemoryBufferBytes = 64 * 1024 * 1024,
        // 下载链路同样走 IPv4 优先连接：CDN 域名若解析出不可达的 IPv6 地址，
        // 默认 handler 会顺序尝试并挂起整个超时周期（见 HttpClientFactory 说明）
        CustomHttpMessageHandlerFactory = CreateDownloadHandler,
        RequestConfiguration = new RequestConfiguration
        {
            UserAgent = "TubaWinUi3-DownloadQueue",
        }
    };

    private static SocketsHttpHandler CreateDownloadHandler()
        => HttpClientFactory.CreateIpv4PreferredHandler();

    private static void HandleSingleFileProgress(DownloadItem item, DownloadProgressChangedEventArgs e)
        => ReportAggregatedProgress(item, e.ReceivedBytesSize, e.TotalBytesToReceive, e.BytesPerSecondSpeed);

    private static void ReportAggregatedProgress(DownloadItem item, long received, long total, double bytesPerSecond)
    {
        var percentage = total > 0 ? Math.Min(received * 100.0 / total, 100) : 0;
        var speedMbps = bytesPerSecond * 8 / 1_000_000;
        var remaining = total > 0 && bytesPerSecond > 1
            ? TimeSpan.FromSeconds(Math.Max(0, total - received) / bytesPerSecond)
            : (TimeSpan?)null;

        item.ResumePosition = received;

        // Downloader 每个分块的每个数据块都会触发进度事件，直接刷 UI 会卡顿；
        // 按固定间隔节流，收尾阶段（>=99.9%）立即推送避免停在 99%。
        var now = Environment.TickCount64;
        if (percentage > 0 && percentage < 99.9 && now - item.LastProgressTick < ProgressThrottleMs)
            return;
        item.LastProgressTick = now;

        DispatchProgress(item, new DownloadQueueProgress(received, total, percentage, speedMbps, remaining));
    }

    private static void CleanupPartialFile(DownloadItem item)
    {
        if (item.State is not (DownloadItemState.Cancelled or DownloadItemState.Failed))
            return;

        // 【GUI 隔离·物理边界】清理执行前复查目标目录物理链；不可信 → 关闭该操作
        if (!DataRoots.IsIsolationTargetPhysicallySafe(item.DestinationPath, out _)) return;

        var candidate = item.ResolvedFileName ?? SanitizeFileName(item.DisplayName);
        // 【GUI 隔离·恢复链·文件名校验】半成品清理统一经严格解析；不合法 → 拒绝清理
        if (!TryResolveSafeFilePath(item.DestinationPath, candidate, out _, out var finalPath)) return;

        foreach (var partial in new[] { finalPath + PartialSuffix, finalPath + DownloaderPartialSuffix })
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    private static DownloadItem? FindItem(string itemId)
        => _queue.FirstOrDefault(i => i.Id == itemId);

    private static void ShowToast(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch
        {
            // Toast notifications may throw ArgumentException ("Value does not
            // fall within the expected range") in unpackaged mode when no AUMID
            // is registered. Swallow so the download flow is not broken.
        }
    }

    private static void DispatchState(DownloadItem item, DownloadItemState state)
    {
        if (_dispatcherQueue is not null)
            _dispatcherQueue.TryEnqueue(() => item.SetState(state));
        else
            item.SetState(state);
    }

    private static void DispatchProgress(DownloadItem item, DownloadQueueProgress progress)
    {
        if (_dispatcherQueue is not null)
            _dispatcherQueue.TryEnqueue(() => item.SetProgress(progress));
        else
            item.SetProgress(progress);
    }

    private static void DispatchProcessingStatus(DownloadItem item, string status)
    {
        if (_dispatcherQueue is not null)
            _dispatcherQueue.TryEnqueue(() => item.SetProcessingStatus(status));
        else
            item.SetProcessingStatus(status);
    }

    private static void DispatchCompleted(DownloadItem item)
    {
        if (_dispatcherQueue is not null)
            _dispatcherQueue.TryEnqueue(() =>
            {
                item.SetCompleted();
                DecrementPending();
                MarkDirty();

                ShowToast(MiscTexts.T("下载完成"), MiscTexts.TSub($"\"{item.DisplayName}\" 已下载完成"));
            });
        else
        {
            item.SetCompleted();
            DecrementPending();
            MarkDirty();

            ShowToast(MiscTexts.T("下载完成"), MiscTexts.TSub($"\"{item.DisplayName}\" 已下载完成"));
        }
    }

    private static void DispatchError(DownloadItem item, string message)
    {
        if (_dispatcherQueue is not null)
            _dispatcherQueue.TryEnqueue(() => item.SetErrorMessage(message));
        else
            item.SetErrorMessage(message);
    }

    private static async Task<ResolvedDownloadUrl> ResolveUrlAsync(DownloadItem item, CancellationToken ct)
    {
        if (item.DirectUrl is not null)
        {
            var fileName = Path.GetFileName(new Uri(item.DirectUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('?'))
                fileName = SanitizeFileName(item.DisplayName);
            return new ResolvedDownloadUrl(item.DirectUrl, fileName);
        }

        if (item.UrlResolver is not null)
            return await item.UrlResolver(ct);

        throw new InvalidOperationException("No download URL or resolver provided");
    }

    private static void IncrementPending()
    {
        Interlocked.Increment(ref _pendingCount);
        QueueChanged?.Invoke();
    }

    private static void DecrementPending()
    {
        Interlocked.Decrement(ref _pendingCount);
        QueueChanged?.Invoke();
    }

    /// <summary>
    /// 【GUI 隔离·恢复链·文件名校验】严格安全路径解析（下载 / 清理 / 删除 统一复用）：
    /// 只接受“单个文件名”，且规范化后严格位于目标目录之内。
    /// 拒绝：空/空白、rooted（绝对路径/盘符/UNC）、路径分隔符或冒号、"." 与 ".."、
    /// 尾点/尾空格（Windows 规范化歧义）、非法文件名字符（含控制字符）。
    /// 返回的 fullPath 必为 destinationDir 的直接子路径；不合法一律 false。
    /// </summary>
    private static bool TryResolveSafeFilePath(string? destinationDir, string? candidateName,
        out string fileName, out string fullPath)
    {
        fullPath = "";
        if (!TryValidateFileNameComponent(candidateName, out fileName)) return false;
        if (string.IsNullOrWhiteSpace(destinationDir)) return false;

        string dir;
        try { dir = Path.GetFullPath(destinationDir); } catch { return false; }

        var full = Path.Combine(dir, fileName);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(Path.GetFullPath(parent), dir, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = full;
        return true;
    }

    /// <summary>【GUI 隔离·恢复链·文件名校验】单个文件名的轻量校验（恢复边界使用；无目录上下文）。</summary>
    private static bool TryValidateFileNameComponent(string? candidateName, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrWhiteSpace(candidateName)) return false;
        var name = candidateName.Trim();
        if (name.Length == 0) return false;
        if (Path.IsPathRooted(name)) return false;
        if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return false;
        if (name is "." or "..") return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        fileName = name;
        return true;
    }

    /// <summary>
    /// 【GUI 隔离·物理边界】多文件落点的字面范围校验：file.FileName 可为相对子路径（如 sub\safe.zip），
    /// 先规范化（GetFullPath 消解 "." 与空段），要求结果**严格位于目标目录之内**；
    /// 拒绝：空、rooted（绝对路径/盘符/UNC）、":" 与 NUL、任何 ".." 段（不论是否实际越界）、规范化后越出目标目录。
    /// 返回 null 表示拒绝（调用方必须关闭该操作）。本函数只做字面范围校验，不触碰文件系统；
    /// 调用方拿到结果后仍须对 Path.GetDirectoryName(结果)（实际父目录，可能比 DestinationPath 更深）做逐段 reparse 物理检查。
    /// </summary>
    private static string? TryResolveSafeMultiFilePath(string destinationDir, string? relativeName)
    {
        if (string.IsNullOrWhiteSpace(destinationDir) || string.IsNullOrWhiteSpace(relativeName)) return null;
        var name = relativeName.Trim();
        if (Path.IsPathRooted(name)) return null;
        if (name.IndexOfAny(new[] { ':', '\0' }) >= 0) return null;
        foreach (var seg in name.Split(new[] { '\\', '/' }, StringSplitOptions.None))
            if (seg == "..") return null;   // 明确拒绝一切 ".." 段

        string dir, full;
        try
        {
            dir = Path.GetFullPath(destinationDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = Path.GetFullPath(Path.Combine(dir, name));
        }
        catch { return null; }
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;   // 规范化后仍须严格位于目标目录之下
        return full;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            if (!invalid.Contains(c)) result.Append(c);
        return result.Length == 0 ? "download" : result.ToString();
    }
}
