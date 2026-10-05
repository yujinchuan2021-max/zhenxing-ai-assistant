using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace TubaWinUi3.Services;

/// <summary>
/// 【UI 改版·第一批】主题服务：真实实现 跟随系统 / 浅色 / 深色。
///  - 启动时读取 AppSettings 的保存值并恢复（不再无条件 Default）；
///  - 用户切换持久化（settings.json）+ 立即生效；
///  - "跟随系统"模式订阅真实系统主题变化（UISettings.ColorValuesChanged），
///    回调切回 UI 线程重新广播，标题栏与子窗口同步刷新；
///  - 非法旧值一律回退"跟随系统"。
/// </summary>
public static class ThemeService
{
    private static AppTheme _currentTheme = AppTheme.Default;
    private static UISettings? _uiSettings;
    private static bool _systemListenerHooked;

    /// <summary>
    /// 主题应用后触发（参数为解析后的 ElementTheme）。
    /// 子窗口/对话框宿主订阅此事件以实现主题实时跟随。
    /// </summary>
    public static event Action<ElementTheme>? ThemeChanged;

    public static AppTheme CurrentTheme => _currentTheme;

    public static ElementTheme CurrentElementTheme => _currentTheme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    internal const string ThemeSettingKey = "theme";

    /// <summary>【可测纯函数】字符串 → 主题枚举；system/空/非法旧值统一回退"跟随系统"。</summary>
    public static AppTheme ParseTheme(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.Default,
    };

    /// <summary>【可测纯函数】主题枚举 → 持久化字符串。</summary>
    public static string ThemeToString(AppTheme t) => t switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system",
    };

    /// <summary>启动时按保存值恢复（规格要求：不再无条件 Default）。</summary>
    public static void ApplySavedTheme()
    {
        AppTheme saved;
        try { saved = ParseTheme(AppSettings.Get(ThemeSettingKey)); }
        catch { saved = AppTheme.Default; }

        _currentTheme = saved;
        ApplyTheme(saved);
        HookSystemListener(saved);
    }

    /// <summary>用户切换主题：持久化 + 立即生效 + 系统监听订阅/退订。</summary>
    public static void SetTheme(AppTheme theme)
    {
        _currentTheme = theme;
        try
        {
            AppSettings.Set(ThemeSettingKey, ThemeToString(theme));
            AppSettings.Flush();   // 主题选择立即落盘（不等去抖，重启必须恢复）
        }
        catch { }

        ApplyTheme(theme);
        HookSystemListener(theme);
    }

    private static void ApplyTheme(AppTheme theme)
    {
        var window = App.MainWindow;
        if (window?.Content is not FrameworkElement root)
            return;

        var elementTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        root.RequestedTheme = elementTheme;

        if (window is MainWindow mw)
            mw.ApplyTitleBarTheme(elementTheme);

        ThemeChanged?.Invoke(elementTheme);
    }

    /// <summary>
    /// 【UI 改版】仅"跟随系统"需要订阅系统主题变化；切到固定浅/深时退订，
    /// 避免无用回调与重复刷新。
    /// </summary>
    private static void HookSystemListener(AppTheme theme)
    {
        if (theme != AppTheme.Default)
        {
            if (_systemListenerHooked && _uiSettings is not null)
            {
                try { _uiSettings.ColorValuesChanged -= OnSystemColorChanged; } catch { }
                _systemListenerHooked = false;
            }
            return;
        }

        if (_systemListenerHooked) return;
        try
        {
            _uiSettings ??= new UISettings();
            _uiSettings.ColorValuesChanged += OnSystemColorChanged;
            _systemListenerHooked = true;
        }
        catch { }
    }

    private static void OnSystemColorChanged(UISettings sender, object args)
    {
        // 系统主题变化在非 UI 线程回调：切回 UI 线程重新应用/广播（标题栏、子窗口、动态控件刷新）。
        var dq = App.MainWindow?.DispatcherQueue;
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            if (_currentTheme != AppTheme.Default) return;   // 期间用户已切固定主题
            ApplyTheme(AppTheme.Default);
        });
    }

    /// <summary>【测试】解除系统监听（避免测试宿主残留订阅）。</summary>
    internal static void UnhookSystemListenerForTest()
    {
        if (_systemListenerHooked && _uiSettings is not null)
        {
            try { _uiSettings.ColorValuesChanged -= OnSystemColorChanged; } catch { }
        }
        _systemListenerHooked = false;
    }
}

public enum AppTheme
{
    Default,
    Light,
    Dark
}
