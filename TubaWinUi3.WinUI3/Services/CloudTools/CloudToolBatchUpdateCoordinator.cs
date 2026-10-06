namespace TubaWinUi3.Services.CloudTools;

/// <summary>
/// One application-owned instance coordinates a batch across page lifetimes. The injected
/// updater must verify the candidate's version/SHA under its transaction gate and preserve
/// the old installation on failure; this component never touches installation files.
/// </summary>
public sealed class CloudToolBatchUpdateCoordinator
{
    private readonly object _sync = new();
    private readonly Func<CloudToolBatchUpdateCandidate, CancellationToken, Task<CloudToolOperationResult>> _update;
    private readonly Func<string, CloudToolState?> _getState;
    private readonly Func<string, bool> _hasValidEntry;
    private readonly Func<CloudToolBatchUpdateCandidate, Task<bool>>? _cancelPending;
    private readonly Func<CloudToolBatchUpdateCandidate, bool> _hasExpectedInstalledPackage;
    private IReadOnlyDictionary<string, CloudToolBatchUpdateCandidate> _candidates =
        new Dictionary<string, CloudToolBatchUpdateCandidate>(StringComparer.Ordinal);
    private CloudToolBatchUpdateSnapshot _snapshot = new(Guid.Empty, false, false, [], []);
    private Task<CloudToolBatchUpdateSnapshot>? _activeTask;
    private CancellationTokenSource? _cancellation;

    public CloudToolBatchUpdateCoordinator(
        Func<CloudToolBatchUpdateCandidate, CancellationToken, Task<CloudToolOperationResult>> updateAsync,
        Func<string, CloudToolState?> getState, Func<string, bool> hasValidInstalledEntry,
        Func<CloudToolBatchUpdateCandidate, Task<bool>>? cancelPendingAsync = null,
        Func<CloudToolBatchUpdateCandidate, bool>? hasExpectedInstalledPackage = null)
    {
        _update = updateAsync ?? throw new ArgumentNullException(nameof(updateAsync));
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
        _hasValidEntry = hasValidInstalledEntry ?? throw new ArgumentNullException(nameof(hasValidInstalledEntry));
        _cancelPending = cancelPendingAsync;
        // Production must supply receipt-backed version/SHA verification. This fallback
        // supports synthetic fixtures that expose only state, never a real file receipt.
        _hasExpectedInstalledPackage = hasExpectedInstalledPackage ?? (candidate =>
        {
            var state = _getState(candidate.Id);
            return state?.Id == candidate.Id && !state.HasUpdate &&
                   state.Version == candidate.TargetVersion && _hasValidEntry(candidate.Id);
        });
    }

    public event EventHandler? Changed;
    public bool IsRunning { get { lock (_sync) return _snapshot.IsRunning; } }

    // Existing manager change notifications can prompt the UI to re-read real download
    // progress. The getter does not initiate updates, persist files or notify observers.
    public CloudToolBatchUpdateSnapshot Snapshot
    {
        get
        {
            CloudToolBatchUpdateSnapshot snapshot;
            lock (_sync) snapshot = _snapshot;
            if (snapshot.Current is not { } current) return snapshot;
            try
            {
                var state = _getState(current.Id);
                if (state?.Id != current.Id) return snapshot;
                return snapshot with { Items = Array.AsReadOnly(snapshot.Items.Select(item =>
                    item.Id == current.Id ? item with { State = state } : item).ToArray()) };
            }
            catch { return snapshot; }
        }
    }

    /// <summary>Repeats join the existing task; callers should not pass page-lifetime tokens.</summary>
    public Task<CloudToolBatchUpdateSnapshot> StartOrJoinAsync(CloudToolBatchUpdatePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        TaskCompletionSource<CloudToolBatchUpdateSnapshot> completion;
        CancellationTokenSource source;
        CloudToolBatchUpdateCandidate[] candidates;
        lock (_sync)
        {
            if (_activeTask is { IsCompleted: false }) return _activeTask;
            // Keep ownership/cancellation controls for deferred work until it is
            // applied or withdrawn; a new batch must not replace that task card.
            if (_snapshot.PendingUpdateCount != 0) return Task.FromResult(_snapshot);
            candidates = plan.Candidates.ToArray();
            if (candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
                throw new ArgumentException("批次中的工具标识重复。", nameof(plan));
            _candidates = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellation = source;
            _snapshot = new(Guid.NewGuid(), true, false,
                Array.AsReadOnly(candidates.Select(candidate => Item(candidate,
                    CloudToolBatchUpdateOutcome.Queued, "等待更新。")).ToArray()),
                Array.AsReadOnly(plan.Skipped.ToArray()));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeTask = completion.Task;
        }
        NotifyChanged();
        _ = Task.Run(() => ExecuteAsync(candidates, source, completion));
        return completion.Task;
    }

    /// <summary>Stops the active adapter and not-yet-started items; does not undo completed updates.</summary>
    public void Cancel()
    {
        CancellationTokenSource? source;
        lock (_sync)
        {
            if (!_snapshot.IsRunning) return;
            _snapshot = _snapshot with { CancellationRequested = true };
            source = _cancellation;
        }
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
        NotifyChanged();
    }

    /// <summary>Also withdraws this batch's own deferred requests; other owners are untouched.</summary>
    public Task<CloudToolBatchUpdateSnapshot> CancelAsync()
    {
        Task<CloudToolBatchUpdateSnapshot>? active;
        TaskCompletionSource<CloudToolBatchUpdateSnapshot>? cleanup = null;
        lock (_sync)
        {
            active = _activeTask is { IsCompleted: false } && _snapshot.IsRunning ? _activeTask : null;
            if (active is null)
            {
                if (_snapshot.PendingUpdateCount == 0) return Task.FromResult(_snapshot);
                _snapshot = _snapshot with { IsRunning = true, CancellationRequested = true };
                cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeTask = cleanup.Task;
            }
        }
        if (active is not null)
        {
            Cancel();
            return active;
        }
        NotifyChanged();
        _ = Task.Run(async () =>
        {
            await CleanupPendingAsync().ConfigureAwait(false);
            Finish(cleanup!, null);
        });
        return cleanup!.Task;
    }

    /// <summary>Reconciles earlier deferred results when the manager later applies or fails them.</summary>
    public void RefreshPendingOutcomes()
    {
        CloudToolBatchUpdateSnapshot observed;
        IReadOnlyDictionary<string, CloudToolBatchUpdateCandidate> candidates;
        lock (_sync)
        {
            // Clearing an owned intent produces intermediate manager events. The
            // cancellation adapter, not those intermediate events, reports its outcome.
            if (_snapshot.IsRunning && _snapshot.CancellationRequested) return;
            observed = _snapshot;
            candidates = _candidates;
        }
        foreach (var item in observed.Items.Where(item => item.Outcome == CloudToolBatchUpdateOutcome.PendingUpdate))
        {
            try
            {
                var state = _getState(item.Id);
                if (state?.Id != item.Id) continue;
                if (!candidates.TryGetValue(item.Id, out var candidate)) continue;
                CloudToolBatchUpdateItemResult updated;
                if (state.PendingUpdate || state.Status == CloudToolStatus.PendingUpdate || state.IsBusy)
                    updated = item with { State = state };
                else if (IsExpectedApplied(candidate, state))
                    updated = item with { Outcome = CloudToolBatchUpdateOutcome.Succeeded, State = state,
                        Message = "先前等待退出的更新已实际应用。" };
                else
                    updated = item with { Outcome = CloudToolBatchUpdateOutcome.Failed, State = state,
                        Message = state.Error ?? "待应用更新尚未完成；请核对当前版本后重试。" };
                lock (_sync)
                {
                    if (_snapshot.Id != observed.Id) return;
                    ReplaceItem(item.Id, old => old.Outcome == CloudToolBatchUpdateOutcome.PendingUpdate ? updated : old);
                }
            }
            catch { /* A temporary state-read failure does not erase a pending request. */ }
        }
        if (observed.PendingUpdateCount != 0) NotifyChanged();
    }

    private async Task ExecuteAsync(CloudToolBatchUpdateCandidate[] candidates, CancellationTokenSource source,
        TaskCompletionSource<CloudToolBatchUpdateSnapshot> completion)
    {
        try
        {
            foreach (var candidate in candidates)
            {
                CloudToolBatchUpdateItemResult result;
                try
                {
                    if (CancellationRequested(source)) result = Skipped(candidate, CloudToolBatchUpdateSkipReason.Cancelled);
                    else if (Recheck(candidate) is { } excluded) result = excluded;
                    else
                    {
                        SetItem(Item(candidate, CloudToolBatchUpdateOutcome.InProgress, "正在更新。", _getState(candidate.Id)));
                        if (CancellationRequested(source)) result = Skipped(candidate, CloudToolBatchUpdateSkipReason.Cancelled);
                        else
                        {
                            var operation = await _update(candidate, source.Token).ConfigureAwait(false);
                            result = Classify(candidate, operation, CancellationRequested(source));
                        }
                    }
                }
                catch (OperationCanceledException) when (CancellationRequested(source))
                { result = Skipped(candidate, CloudToolBatchUpdateSkipReason.Cancelled); }
                catch (Exception error)
                { result = Item(candidate, CloudToolBatchUpdateOutcome.Failed, error.Message); }
                SetItem(result);
            }
        }
        finally
        {
            bool cleanup;
            lock (_sync)
            {
                cleanup = (_snapshot.CancellationRequested || source.IsCancellationRequested) &&
                    _snapshot.PendingUpdateCount > 0;
                // End atomically with the cancellation decision, so a later cancellation
                // can start its own tracked cleanup rather than missing the final window.
                if (!cleanup) FinishLocked(completion, source);
            }
            if (cleanup)
            {
                await CleanupPendingAsync().ConfigureAwait(false);
                Finish(completion, source);
            }
            else
            {
                source.Dispose();
                NotifyChanged();
            }
        }
    }

    private bool CancellationRequested(CancellationTokenSource source)
    {
        lock (_sync) return _snapshot.CancellationRequested || source.IsCancellationRequested;
    }

    private async Task CleanupPendingAsync()
    {
        CloudToolBatchUpdateSnapshot observed;
        lock (_sync) observed = _snapshot;
        foreach (var pending in observed.Items.Where(item => item.Outcome == CloudToolBatchUpdateOutcome.PendingUpdate))
        {
            if (!_candidates.TryGetValue(pending.Id, out var candidate)) continue;
            CloudToolBatchUpdateItemResult result;
            try
            {
                var cancelled = _cancelPending is not null && await _cancelPending(candidate).ConfigureAwait(false);
                var state = _getState(candidate.Id);
                if (cancelled) result = Skipped(candidate, CloudToolBatchUpdateSkipReason.Cancelled, state);
                else if (IsExpectedApplied(candidate, state))
                    result = Item(candidate, CloudToolBatchUpdateOutcome.Succeeded,
                        "更新已实际应用，无需撤销；已完成的更新保留。", state);
                else if (state?.Id == candidate.Id && !state.PendingUpdate &&
                         state.Status != CloudToolStatus.PendingUpdate && !state.IsBusy)
                    result = Item(candidate, CloudToolBatchUpdateOutcome.Failed,
                        state.Error ?? "此待更新请求已失效，未确认应用；请核对当前版本后重新确认。", state);
                else result = pending with { State = state ?? pending.State,
                    Message = "未能撤销此批次的待更新请求；请核对后重试，未影响其他请求。" };
            }
            catch (Exception error)
            { result = pending with { Message = "撤销待更新请求失败：" + error.Message }; }
            SetItem(result);
        }
    }

    private void Finish(TaskCompletionSource<CloudToolBatchUpdateSnapshot> completion, CancellationTokenSource? source)
    {
        lock (_sync) FinishLocked(completion, source);
        source?.Dispose();
        NotifyChanged();
    }

    private void FinishLocked(TaskCompletionSource<CloudToolBatchUpdateSnapshot> completion, CancellationTokenSource? source)
    {
        _snapshot = _snapshot with { IsRunning = false,
            CancellationRequested = _snapshot.CancellationRequested || source?.IsCancellationRequested == true };
        _cancellation = null;
        completion.TrySetResult(_snapshot);
    }

    private CloudToolBatchUpdateItemResult? Recheck(CloudToolBatchUpdateCandidate candidate)
    {
        var state = _getState(candidate.Id);
        var reason = state?.Id != candidate.Id ? CloudToolBatchUpdateSkipReason.CatalogMissing
            : !state.IsManaged ? CloudToolBatchUpdateSkipReason.NotManaged
            : !state.IsInstalled ? CloudToolBatchUpdateSkipReason.NotInstalled
            : state.IsBusy ? CloudToolBatchUpdateSkipReason.Busy
            : state.PendingUpdate || state.Status == CloudToolStatus.PendingUpdate ? CloudToolBatchUpdateSkipReason.PendingUpdate
            : !_hasValidEntry(candidate.Id) ? CloudToolBatchUpdateSkipReason.MissingEntry
            : state.AvailableVersion != candidate.TargetVersion ? CloudToolBatchUpdateSkipReason.CatalogChanged
            : !CloudToolBatchUpdatePlanner.NeedsUpdate(state) ? CloudToolBatchUpdateSkipReason.AlreadyCurrent
            : CloudToolBatchUpdateSkipReason.None;
        return reason == CloudToolBatchUpdateSkipReason.None ? null : Skipped(candidate, reason, state);
    }

    private CloudToolBatchUpdateItemResult Classify(CloudToolBatchUpdateCandidate candidate,
        CloudToolOperationResult operation, bool cancellationRequested)
    {
        var state = operation.State ?? _getState(candidate.Id);
        if (state?.Id != candidate.Id)
            return Item(candidate, CloudToolBatchUpdateOutcome.Failed, "更新结果缺少此工具的有效状态。", state);
        if (state.PendingUpdate || state.Status == CloudToolStatus.PendingUpdate)
            return state.IsManaged && state.IsInstalled && _hasValidEntry(candidate.Id)
                ? Item(candidate, CloudToolBatchUpdateOutcome.PendingUpdate, operation.Message, state)
                : Item(candidate, CloudToolBatchUpdateOutcome.Failed, "待应用更新缺少原有的有效入口。", state);
        if (operation.Success)
            return IsExpectedApplied(candidate, state)
                ? Item(candidate, CloudToolBatchUpdateOutcome.Succeeded, operation.Message, state)
                : Item(candidate, CloudToolBatchUpdateOutcome.Failed, "更新未能确认已应用，不能计为成功。", state);
        return cancellationRequested ? Skipped(candidate, CloudToolBatchUpdateSkipReason.Cancelled, state)
            : Item(candidate, CloudToolBatchUpdateOutcome.Failed, operation.Message, state);
    }

    private bool IsExpectedApplied(CloudToolBatchUpdateCandidate candidate, CloudToolState? state) =>
        state?.Id == candidate.Id && state.IsManaged && state.IsInstalled && !state.PendingUpdate &&
        state.Status == CloudToolStatus.Installed && state.Version == candidate.TargetVersion &&
        _hasValidEntry(candidate.Id) && _hasExpectedInstalledPackage(candidate);

    private static CloudToolBatchUpdateItemResult Item(CloudToolBatchUpdateCandidate candidate,
        CloudToolBatchUpdateOutcome outcome, string message, CloudToolState? state = null) =>
        new(candidate.Id, candidate.Name, candidate.InstalledVersion, candidate.TargetVersion, outcome,
            Message: message, State: state);

    private static CloudToolBatchUpdateItemResult Skipped(CloudToolBatchUpdateCandidate candidate,
        CloudToolBatchUpdateSkipReason reason, CloudToolState? state = null) =>
        Item(candidate, CloudToolBatchUpdateOutcome.Skipped, CloudToolBatchUpdatePlanner.SkipMessage(reason), state)
            with { SkipReason = reason };

    private void SetItem(CloudToolBatchUpdateItemResult item)
    {
        lock (_sync) ReplaceItem(item.Id, _ => item);
        NotifyChanged();
    }

    private void ReplaceItem(string id, Func<CloudToolBatchUpdateItemResult, CloudToolBatchUpdateItemResult> update) =>
        _snapshot = _snapshot with { Items = Array.AsReadOnly(_snapshot.Items.Select(item =>
            item.Id == id ? update(item) : item).ToArray()) };

    private void NotifyChanged()
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((EventHandler)subscriber)(this, EventArgs.Empty); } catch { }
    }
}
