namespace TubaWinUi3.Services.Ai;

/// <summary>
/// 【主审修复·第六轮】页面级会话写入租约（可测的归属管理器；生产路径由 AiAgentPage 使用）。
///
/// 不变量：
/// - 页面在任一时刻**最多持有一个**会话 ID 的所有权（不双持）；
/// - 会话**替换路径必须成对**：先保存旧会话（调用方在持有旧归属时落盘）→ Replace/ReleaseCurrent；
/// - **关闭为终态**：Close() 之后不可重获、不可写盘（迟到回调 / Send finally 一律拒绝），
///   防止"关窗时释放 → 迟到回调 TryAcquire 重获 → 幽灵占用直到重启"（主审第六轮缺陷 1）；
/// - 数据根在构造时绑定，Acquire/Release 始终使用同一值。
/// </summary>
public sealed class ConversationLease
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly Guid _token = Guid.NewGuid();
    private string? _current;
    private bool _closed;

    public ConversationLease(string dataRoot) => _root = dataRoot;

    /// <summary>是否已关闭（终态）。</summary>
    public bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>当前持有的会话 ID（null=未持有/新会话尚未落盘）。</summary>
    public string? CurrentId { get { lock (_gate) return _current; } }

    /// <summary>写盘守卫：本页面是否有权写该会话。
    /// 已关闭 → 永远 false（终态不可重获）；
    /// 已持有该 ID → 校验登记后 true；
    /// 无当前 → 取得（新会话首次落盘）；
    /// 持有**其他** ID → false（替换路径必须成对处理，拒绝双持/覆盖）。</summary>
    public bool EnsureOwned(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return false;
        lock (_gate)
        {
            if (_closed) return false;
            if (string.Equals(_current, conversationId, StringComparison.Ordinal))
            {
                if (ConversationOwnershipRegistry.IsOwnedBy(_root, conversationId, _token)) return true;
                // 自身状态与登记不一致（异常路径）：无人持有则恢复，否则拒写。
                return ConversationOwnershipRegistry.TryAcquire(_root, conversationId, _token);
            }
            if (_current is not null) return false;
            if (!ConversationOwnershipRegistry.TryAcquire(_root, conversationId, _token)) return false;
            _current = conversationId;
            return true;
        }
    }

    /// <summary>会话替换（历史恢复等）：取得新会话所有权并释放旧归属（成对）。
    /// 返回 false = 新会话被其他窗口持有或已关闭（当前状态不变）。</summary>
    public bool Replace(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return false;
        lock (_gate)
        {
            if (_closed) return false;
            if (string.Equals(_current, conversationId, StringComparison.Ordinal)) return true;
            if (!ConversationOwnershipRegistry.TryAcquire(_root, conversationId, _token)) return false;
            if (_current is not null)
                ConversationOwnershipRegistry.Release(_root, _current, _token);   // 释放旧（成对）
            _current = conversationId;
            return true;
        }
    }

    /// <summary>能否取得某会话（不改变状态）——供"打开历史"前预检。</summary>
    public bool CanTake(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return true;
        lock (_gate)
        {
            if (_closed) return false;
            if (string.Equals(_current, conversationId, StringComparison.Ordinal)) return true;
            return !ConversationOwnershipRegistry.IsHeldByOther(_root, conversationId, _token);
        }
    }

    /// <summary>释放当前归属（新对话 / 替换中段）；幂等。</summary>
    public void ReleaseCurrent()
    {
        lock (_gate)
        {
            if (_current is null) return;
            ConversationOwnershipRegistry.Release(_root, _current, _token);
            _current = null;
        }
    }

    /// <summary>真实关闭：释放并封死（终态——之后 EnsureOwned/Replace 一律拒绝）；幂等。</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (_current is not null)
            {
                ConversationOwnershipRegistry.Release(_root, _current, _token);
                _current = null;
            }
        }
    }

    /// <summary>仅供诊断：所有者令牌。</summary>
    public Guid Token => _token;
}
