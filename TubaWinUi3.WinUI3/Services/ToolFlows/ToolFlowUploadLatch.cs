namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// 关闭分享开关时的<strong>内存级撤销门闩</strong>（fail-closed）：
/// 撤销时刻一经记录，本次运行内就不再发送该时刻及之前产生的选定与计数批次——
/// 即使磁盘清理（记录作废 / 计数清空）失败也一样。
/// 跨进程兜底不由本类承担：每次重新开启分享前，设置页会先通过
/// <see cref="ToolFlowUploadSwitch.TryVoidPendingUploads"/> 重试作废旧记录，作废未完成时拒绝开启。
/// 发送紧前的资格核对（`TryStartSend`）在同一把锁内完成，确保旧请求
/// 不能跨出撤销边界（快速"关闭→重开"也不会被解释为对旧数据的追认）。
/// </summary>
public static class ToolFlowUploadLatch
{
    private static readonly object Gate = new();
    private static DateTimeOffset? _revokedBeforeUtc;
    private static Dictionary<(string Kind, string Tool), int>? _voidedCounts;

    /// <summary>最近一次撤销时刻；null = 本次运行尚未撤销过。</summary>
    public static DateTimeOffset? RevokedBeforeUtc
    {
        get
        {
            lock (Gate) return _revokedBeforeUtc;
        }
    }

    /// <summary>关闭开关时调用（可重复调用，以最近一次为准）：此后更早产生的数据一律不再发送。</summary>
    public static void RevokeAll()
    {
        lock (Gate) _revokedBeforeUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>该时刻产生的数据是否已被撤销（已被撤销的不得发送）。</summary>
    public static bool IsRevoked(DateTimeOffset createdAtUtc)
    {
        lock (Gate) return _revokedBeforeUtc is { } revokedAt && createdAtUtc <= revokedAt;
    }

    /// <summary>
    /// 登记一份"作废计数快照"（关闭开关时的当前计数）：磁盘清理失败时，
    /// 这些旧计数不得随重新开启后的新批次发送（见 <see cref="AdjustForVoided"/>）。
    /// 每次调用整体替换快照（以最近的关闭时刻为准）；清理成功后以空快照撤销登记。
    /// </summary>
    public static void VoidCounts(IEnumerable<DownloadMetricEntry> entries)
    {
        lock (Gate)
        {
            _voidedCounts = entries
                .Where(x => x.Count > 0)
                .GroupBy(x => (x.Kind, x.Tool))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        }
    }

    /// <summary>按已登记的作废快照下调计数（只影响拟发送视图，不改磁盘记录；非正条目剔除）。</summary>
    public static List<DownloadMetricEntry> AdjustForVoided(IEnumerable<DownloadMetricEntry> entries)
    {
        lock (Gate)
        {
            var voided = _voidedCounts;
            return entries
                .Select(x => new DownloadMetricEntry
                {
                    Kind = x.Kind,
                    Tool = x.Tool,
                    Count = x.Count - (voided is not null &&
                        voided.TryGetValue((x.Kind, x.Tool), out var amount) ? amount : 0),
                })
                .Where(x => x.Count > 0)
                .ToList();
        }
    }

    /// <summary>
    /// 发送紧前的原子资格核对（与撤销共享同一把锁）：确认开关开启且数据未被撤销后，
    /// 立即在锁内启动发送。返回 null = 不得发送（开关关闭或已跨撤销边界）；
    /// 否则返回已启动的发送任务，由调用方等待。
    /// 用途：消除"核对之后、发送之前发生关闭→重开"的竞态——撤销只可能发生在核对之前
    /// （阻止发送）或发送启动之后（请求已在途，不再收回），旧请求无法跨出撤销边界。
    /// </summary>
    public static Task<T>? TryStartSend<T>(DateTimeOffset createdAtUtc, Func<bool> enabled,
        Func<Task<T>> startSend)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(startSend);
        lock (Gate)
        {
            if (!enabled()) return null;
            if (_revokedBeforeUtc is { } revokedAt && createdAtUtc <= revokedAt) return null;
            return startSend();
        }
    }

    /// <summary>测试用：清除门闩状态（生产代码不调用）。</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _revokedBeforeUtc = null;
            _voidedCounts = null;
        }
    }
}
