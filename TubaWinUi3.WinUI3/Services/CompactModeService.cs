namespace TubaWinUi3.Services;

/// <summary>
/// 兼容收口（2026-09-25 上线前窄改）：本版只保留默认卡片布局，界面不再向用户提供「卡牌/简洁模式」选择；
/// 旧配置里即使存在 CompactModeEnabled=true，也统一按默认卡片布局显示（本类恒返回 false，不再读写该设置）。
/// 事件与方法签名保留给既有订阅方（HomePage / BuiltinToolsPage）编译兼容，实际不会再发起切换。
/// </summary>
public static class CompactModeService
{
    public static event Action<bool>? CompactModeChanged;

    public static bool IsCompactModeEnabled() => false;

    public static void SetCompactModeEnabled(bool enabled)
    {
        // 不再写入设置；值恒为默认卡片布局（false）。保留事件作为统一刷新入口。
        _ = enabled;
        CompactModeChanged?.Invoke(false);
    }
}
