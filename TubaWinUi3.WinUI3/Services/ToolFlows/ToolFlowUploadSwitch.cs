namespace TubaWinUi3.Services.ToolFlows;

/// <summary>
/// 分享开关的作废动作（settable 于设置页调用；可注入数据根以便测试）。
/// 语义（fail-closed）：作废一切旧待发数据——选定记录的上报资格 + 计数批次与计数；
/// 内部同时立内存级撤销门闩（<see cref="ToolFlowUploadLatch"/>），因此即使磁盘清理失败，
/// 本次运行内也不会再发送旧数据。
/// 设置页在<b>两个时机</b>调用：
/// ① 关闭开关时——尽力清理，失败仅提示（本次运行内由门闩阻止发送）；
/// ② <b>重新开启前</b>——跨进程兜底：先重试作废上次清理失败的残留，
///   返回 false 时调用方必须拒绝开启、保持开关关闭（"拒绝重新开启直到旧记录确实作废"）。
/// </summary>
public static class ToolFlowUploadSwitch
{
    /// <summary>
    /// 作废旧待发数据并持久化。返回 false = 磁盘清理未全部成功
    /// （内存门闩已立；调用方应保持/收回开关为关闭，并在下次开启前再次尝试）。
    /// </summary>
    public static bool TryVoidPendingUploads(string? dataRoot = null)
    {
        var cleared = true;
        try { cleared &= new ToolFlowSelectionStore(dataRoot).InvalidatePendingUploads(); }
        catch { cleared = false; }
        try { cleared &= new DownloadMetricsStore(dataRoot).ClearPending(); }
        catch { cleared = false; }
        return cleared;
    }
}
