namespace TubaWinUi3.Services.Ai;

/// <summary>
/// 【主审修复·2026-09-22】会话写入归属登记（进程内单一所有者）。
///
/// 背景（主审 14:59 复现）：主窗口与独立卡片窗口各自载入同一条历史会话（同 ID）后，
/// 两实例均可按 ID 全量落盘——未编辑的旧副本（如未发送就关闭的独立窗口）会把
/// 另一窗口的新记录整体覆盖。
///
/// 规则：
/// - 同一「数据根 + 会话 ID」在同一时刻只能由一个所有者（ownerToken，页面实例级 GUID）编辑/落盘；
/// - 页面临时导航离开（设置↔主页等）**不**释放；
/// - 真关闭（Unload）与切换会话时释放，按 token 判定、幂等（非持有者调用无效）；
/// - 不同数据根（ZXAI_DATA_ROOT）互不影响。
/// </summary>
public static class ConversationOwnershipRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Guid> Owners = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string dataRoot, string conversationId)
        => (dataRoot ?? string.Empty) + "\u0001" + conversationId;

    /// <summary>尝试取得会话所有权：成功、或本 token 已持有（重入）返回 true；被其他实例持有时 false。</summary>
    public static bool TryAcquire(string dataRoot, string conversationId, Guid token)
    {
        if (string.IsNullOrEmpty(conversationId)) return true;   // 无名会话（未落盘）无冲突
        lock (Gate)
        {
            var key = Key(dataRoot, conversationId);
            if (Owners.TryGetValue(key, out var holder))
                return holder == token;
            Owners[key] = token;
            return true;
        }
    }

    /// <summary>本 token 当前是否持有该会话所有权。</summary>
    public static bool IsOwnedBy(string dataRoot, string conversationId, Guid token)
    {
        if (string.IsNullOrEmpty(conversationId)) return false;
        lock (Gate)
            return Owners.TryGetValue(Key(dataRoot, conversationId), out var holder) && holder == token;
    }

    /// <summary>是否被其他所有者持有（本 token 已持有不算冲突）。</summary>
    public static bool IsHeldByOther(string dataRoot, string conversationId, Guid token)
    {
        if (string.IsNullOrEmpty(conversationId)) return false;
        lock (Gate)
            return Owners.TryGetValue(Key(dataRoot, conversationId), out var holder) && holder != token;
    }

    /// <summary>释放所有权（仅持有者本人有效；非持有者调用无效果——幂等）。</summary>
    public static void Release(string dataRoot, string conversationId, Guid token)
    {
        if (string.IsNullOrEmpty(conversationId)) return;
        lock (Gate)
        {
            var key = Key(dataRoot, conversationId);
            if (Owners.TryGetValue(key, out var holder) && holder == token)
                Owners.Remove(key);
        }
    }
}
