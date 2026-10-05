using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Controls.AgentChat;

/// <summary>INPC 基类。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>一条 Agent 步骤的 UI 视图模型（绑定步骤链列表行）。</summary>
public sealed class StepRowVm : ObservableObject
{
    private AgentStep _step;
    private WeakReference<FrameworkElement>? _themeHost;

    /// <summary>解析失败时的兜底缓存（键 → 最近一次成功解析的画刷）。</summary>
    private static readonly Dictionary<string, Brush> LastResolved = [];

    public StepRowVm(AgentStep step)
    {
        _step = step;
    }

    /// <summary>【A06】底层步骤数据（展示记录快照用）。</summary>
    public AgentStep Step => _step;

    public string ToolGlyph => _step.Glyph;
    public string DisplayName => _step.DisplayName;
    public string Summary => _step.Summary;
    public string StatusText => _step.StatusText;
    public string CallId => _step.CallId ?? "";

    public bool IsRunning => _step.Status == AgentStepStatus.Running;
    public bool IsWaiting => _step.Status == AgentStepStatus.AwaitingConfirmation;
    public bool IsFailed => _step.Status == AgentStepStatus.Failed;
    public bool IsDone => _step.Status is AgentStepStatus.Success or AgentStepStatus.Failed or AgentStepStatus.Rejected or AgentStepStatus.Cancelled;

    public Visibility IsRunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsWaitingVisibility => IsWaiting ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsDoneVisibility => IsDone ? Visibility.Visible : Visibility.Collapsed;

    public string StatusGlyph => _step.Status switch
    {
        AgentStepStatus.AwaitingConfirmation => "\uE823", // StatusCircleQuestionMark
        AgentStepStatus.Success => "\uE73E",              // CheckMark
        AgentStepStatus.Failed => "\uE783",               // ErrorBadge
        AgentStepStatus.Rejected => "\uE711",             // Cancel
        AgentStepStatus.Cancelled => "\uE711",
        _ => ""
    };

    public Brush? StatusBrush => ResolveStatusBrush();

    public string DurationText => _step.Duration is { } d ? $"{d.TotalSeconds:F0}s" : "";

    public string? ResultPreview
    {
        get
        {
            var text = _step.Status == AgentStepStatus.Failed
                ? _step.Error ?? _step.Result
                : _step.Result;
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.Length > 200) text = text[..200] + "…";
            return text;
        }
    }

    /// <summary>【UI 改版】主题变化后重算状态色（StatusBrush 是计算属性，需要显式通知绑定）。</summary>
    public void RefreshTheme() => OnPropertyChanged(nameof(StatusBrush));

    /// <summary>步骤状态更新（同一实例）。</summary>
    public void Update(AgentStep step)
    {
        _step = step;
        OnPropertyChanged(null); // 刷新全部绑定
    }

    /// <summary>
    /// 【主题统一】注入主题上下文（步骤行所在控件）。状态色按该元素的【实际主题】解析，
    /// 而不是 Application.Current.Resources 的"当前解析值"——后者在页面/容器局部强制
    /// Light/Dark（如聊天页 Assistant* 令牌）时会取错主题。
    /// </summary>
    public void AttachThemeHost(FrameworkElement? host)
        => _themeHost = host is null ? null : new WeakReference<FrameworkElement>(host);

    /// <summary>当前主题上下文的实际主题（供诊断/测试观察解析依据）。</summary>
    public ElementTheme ThemeContext =>
        ThemeHost is { } host ? host.ActualTheme : ElementTheme.Default;

    /// <summary>【可测纯函数】步骤状态 → 状态色资源键。</summary>
    internal static string StatusBrushKeyFor(AgentStepStatus status) => status switch
    {
        AgentStepStatus.Success => "SystemFillColorSuccessBrush",
        AgentStepStatus.Failed => "SystemFillColorCriticalBrush",
        AgentStepStatus.Rejected => "TextFillColorSecondaryBrush",
        AgentStepStatus.AwaitingConfirmation => "AccentTextFillColorPrimaryBrush",
        _ => "AccentTextFillColorPrimaryBrush"
    };

    private FrameworkElement? ThemeHost
        => _themeHost is not null && _themeHost.TryGetTarget(out var host) ? host : null;

    private Brush? ResolveStatusBrush()
    {
        var key = StatusBrushKeyFor(_step.Status);
        try
        {
            // 主键缺失时回退强调色（资源字典异常也不会让绑定求值抛异常导致崩溃）
            var brush = ThemeResourceResolver.ResolveBrush(ThemeHost, key, "AccentFillColorDefaultBrush");
            if (brush is not null)
            {
                lock (LastResolved)
                    LastResolved[key] = brush;
                return brush;
            }

            // 极端兜底：解析失败时沿用最近一次成功解析的画刷（避免 Foreground 变成 null 后不可见）
            lock (LastResolved)
                return LastResolved.TryGetValue(key, out var cached) ? cached : null;
        }
        catch
        {
            lock (LastResolved)
                return LastResolved.TryGetValue(key, out var cached) ? cached : null;
        }
    }
}

/// <summary>
/// 一轮 Agent 步骤链的 UI 视图模型：运行中实时展开，完成后自动折叠为摘要。
/// </summary>
public sealed class RunVm : ObservableObject
{
    public ObservableCollection<StepRowVm> Steps { get; } = [];

    private bool _isRunning;
    /// <summary>整链是否仍在执行（翻转为 false 时控件自动折叠）。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    private string _summaryText = MiscTexts.T("正在执行…");
    public string SummaryText
    {
        get => _summaryText;
        set => Set(ref _summaryText, value);
    }

    public void AddStep(StepRowVm vm)
    {
        Steps.Add(vm);
        SummaryText = MiscTexts.TSub($"正在执行… 第 {Steps.Count} 步");
    }

    /// <summary>按工具调用 ID 查找步骤行（状态更新用）。</summary>
    public StepRowVm? FindByCallId(string callId)
    {
        if (string.IsNullOrEmpty(callId)) return null;
        return Steps.FirstOrDefault(s => s.CallId == callId);
    }

    /// <summary>整链完成：更新摘要并标记结束（触发折叠动画）。</summary>
    public void Complete(AgentStepGroupSummary summary)
    {
        SummaryText = summary.ToDisplayText();
        IsRunning = false;
    }
}
