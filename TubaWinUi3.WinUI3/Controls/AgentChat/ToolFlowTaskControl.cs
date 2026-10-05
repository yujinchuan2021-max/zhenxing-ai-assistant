using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Controls.AgentChat;

/// <summary>
/// The selected goal's workbench. The page owns its viewport and every operation;
/// updating this view never starts an installation, opens a tool or confirms a task.
/// </summary>
internal sealed class ToolFlowTaskControl : Grid
{
    private readonly TextBlock _eyebrow = new() { FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _goal = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _plan = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap,
        MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _summary = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _currentLabel = new() { FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _next = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _useNotice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { IsIndeterminate = false, Height = 3,
        HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly Button _details = NewButton();
    private readonly Button _handoff = NewButton();
    private readonly Button _stop = NewButton();
    private readonly Button _overview = NewButton();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    // Retain the original wrappers used by weak theme bindings while this visual is alive.
    // Native Children alone do not guarantee their managed lifetime after collection/remount.
    private readonly Border _surface = new() { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
        Padding = new Thickness(24) };
    private readonly Border _currentSurface = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
        Padding = new Thickness(20) };
    private readonly Border _accentMarker = new() { Width = 3, CornerRadius = new CornerRadius(2),
        VerticalAlignment = VerticalAlignment.Stretch };
    private readonly ToolFlowWorkbenchStageControl _stages = new();
    private readonly ThemeRefreshScope _theme;
    private ToolFlowTaskPresentation? _presentation;
    private ToolFlowGoalGuide? _guide;
    private bool _busy;
    private bool _stopping;
    private bool _resumed;
    private bool _selectionConfirmed;

    /// <summary>The current-step action. Legacy hosts can continue handling OpenDetails.</summary>
    internal event Action? PrimaryRequested;
    internal event Action? OpenDetails;
    internal event Action? OpenHandoff;
    internal event Action? StopRequested;
    internal ToolFlowToolAccessControl ToolAccess { get; } = new();
    internal int CurrentStageIndex => _stages.CurrentStageIndex;
    internal int RecordedStageCount => _stages.RecordedStageCount;

    internal ToolFlowTaskControl()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _theme = ToolFlowThemeResources.Attach(this);
        foreach (var primary in new[] { _details, _handoff })
        {
            ToolFlowThemeResources.Bind(_theme, primary, Button.BackgroundProperty, ToolFlowThemeResources.Accent);
            ToolFlowThemeResources.Bind(_theme, primary, Button.ForegroundProperty, ToolFlowThemeResources.OnAccent);
        }
        ToolFlowThemeResources.Bind(_theme, _surface, Border.BackgroundProperty, ToolFlowThemeResources.Canvas);
        ToolFlowThemeResources.Bind(_theme, _surface, Border.BorderBrushProperty, ToolFlowThemeResources.Stroke);
        ToolFlowThemeResources.Bind(_theme, _currentSurface, Border.BackgroundProperty, ToolFlowThemeResources.Surface);
        ToolFlowThemeResources.Bind(_theme, _currentSurface, Border.BorderBrushProperty, ToolFlowThemeResources.Stroke);
        ToolFlowThemeResources.Bind(_theme, _accentMarker, Border.BackgroundProperty, ToolFlowThemeResources.Accent);
        ToolFlowThemeResources.Bind(_theme, _progress, ProgressBar.ForegroundProperty, ToolFlowThemeResources.Accent);
        foreach (var text in new[] { _eyebrow, _goal, _plan, _summary, _currentLabel, _next, _useNotice })
            ToolFlowThemeResources.BindText(_theme, text,
                text == _goal || text == _next ? ToolFlowThemeResources.PrimaryText : ToolFlowThemeResources.SecondaryText);

        Children.Add(_surface);
        var content = new StackPanel { Spacing = 20 };
        _surface.Child = content;
        var heading = new StackPanel { Spacing = 7 };
        heading.Children.Add(_eyebrow);
        heading.Children.Add(_goal);
        heading.Children.Add(_plan);
        content.Children.Add(heading);
        content.Children.Add(_stages);
        content.Children.Add(_currentSurface);

        var currentLayout = new Grid { ColumnSpacing = 14 };
        currentLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        currentLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        currentLayout.Children.Add(_accentMarker);
        var current = new StackPanel { Spacing = 12 };
        SetColumn(current, 1);
        currentLayout.Children.Add(current);
        _currentSurface.Child = currentLayout;
        current.Children.Add(_currentLabel);
        current.Children.Add(_next);
        current.Children.Add(_summary);
        current.Children.Add(_progress);
        _actions.Children.Add(_details);
        _actions.Children.Add(_handoff);
        _actions.Children.Add(_stop);
        _actions.Children.Add(_overview);
        current.Children.Add(_actions);
        current.Children.Add(_useNotice);
        content.Children.Add(ToolAccess);

        _details.Click += (_, _) =>
        {
            if (!_details.IsEnabled) return;
            if (PrimaryRequested is { } requested) requested();
            else OpenDetails?.Invoke();
        };
        _handoff.Click += (_, _) => { if (_handoff.IsEnabled) OpenHandoff?.Invoke(); };
        _stop.Click += (_, _) => { if (_stop.IsEnabled) StopRequested?.Invoke(); };
        _overview.Click += (_, _) => { if (_overview.IsEnabled) OpenDetails?.Invoke(); };
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        ApplyWidth(320);
        ToolFlowThemeResources.Refresh(_theme);
    }

    internal void Update(ToolFlowTaskPresentation presentation, bool busy = false, bool stopping = false,
        bool resumed = false, bool selectionConfirmed = true)
    {
        _presentation = presentation;
        _guide = null;
        _busy = busy;
        _stopping = stopping;
        _resumed = resumed;
        _selectionConfirmed = selectionConfirmed;
        ApplyLocalization();
    }

    internal void Update(ToolFlowGoalGuide guide, string plan, bool running = false, bool busy = false,
        bool stopping = false, bool resumed = false, bool selectionConfirmed = true)
    {
        _guide = guide;
        _presentation = new(guide.Goal, plan, guide.Stage + " · " + guide.Summary,
            guide.CurrentStep is { } step ? step.Title + "\n" + step.Hint
                : guide.Delivery?.Hint ?? L("AiGoal_UseHint", "软件准备记录已保留。接下来打开工具，核对账号和配置，再完成你的目标。"),
            guide.HasPendingAutomatic, running);
        _busy = busy;
        _stopping = stopping;
        _resumed = resumed;
        _selectionConfirmed = selectionConfirmed;
        ApplyLocalization();
    }

    internal void ApplyLocalization()
    {
        ToolAccess.ApplyLocalization();
        if (_presentation is not { } p) return;
        var confirmed = _selectionConfirmed && !string.IsNullOrWhiteSpace(p.Goal) && !string.IsNullOrWhiteSpace(p.Plan);
        _eyebrow.Text = Text("AiWorkbench_Title", "任务工作台", "Task workbench");
        _goal.Text = (_resumed ? L("AiTask_RestoredGoal", "恢复目标：") : L("AiTask_Goal", "当前目标：")) + p.Goal;
        ToolTipService.SetToolTip(_goal, p.Goal);
        _plan.Text = L("AiTask_Plan", "选定方案：") + p.Plan;
        ToolTipService.SetToolTip(_plan, p.Plan);
        _summary.Text = p.Summary;
        _next.Text = _stopping ? L("AiTask_Stopping", "等待当前工具完成检查，然后停止后续准备…") : p.NextStep;
        _currentLabel.Text = p.Running
            ? Text("AiWorkbench_Running", "正在连续准备", "Preparing your selected tools")
            : Text("AiWorkbench_CurrentStep", "当前一步", "Your next step");
        _progress.Visibility = p.Running ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = p.Running && !_stopping;
        _details.Content = p.CanPrepare ? L("AiTask_Continue", "继续准备") : L("AiTask_Details", "查看清单");
        _handoff.Content = L("AiTask_Handoff", "交给 AI Agent");
        _stop.Content = L("AiTask_Stop", "停止后续");
        _stop.Visibility = p.Running ? Visibility.Visible : Visibility.Collapsed;
        _stop.IsEnabled = p.Running && !_stopping;
        _details.Visibility = _handoff.Visibility = p.Running ? Visibility.Collapsed : Visibility.Visible;
        _details.IsEnabled = _handoff.IsEnabled = confirmed && !_busy && !p.Running;
        _overview.Visibility = Visibility.Collapsed;
        _overview.IsEnabled = confirmed && !_busy && !p.Running;
        _useNotice.Visibility = Visibility.Collapsed;

        // Only the guide contains readiness evidence. A legacy summary's CanPrepare=false
        // can also mean missing manual requirements, so it never advances the workbench.
        var canTry = false;
        if (_guide is { } guide)
        {
            var hasCurrent = guide.CurrentStep is not null;
            _details.Content = guide.CurrentStep is { } current
                ? current.HasAutomaticItems ? Text("AiWorkbench_ContinueTools", "继续准备工具", "Continue preparing tools")
                    : current.AccessEntry is not null ? current.ActionLabel
                    : current.SourceUrl is not null ? Text("AiWorkbench_OpenWebsite", "打开相关网站", "Open the website")
                    : Text("AiWorkbench_CheckStep", "核对这一步", "Check this step")
                : L("AiGoal_Overview", "查看详情");
            _handoff.Content = guide.Delivery?.ActionLabel ?? L("AiGoal_UseAction", "查看使用步骤");
            _details.Visibility = !p.Running && hasCurrent ? Visibility.Visible : Visibility.Collapsed;
            _handoff.Visibility = !p.Running && !hasCurrent ? Visibility.Visible : Visibility.Collapsed;
            _handoff.IsEnabled = confirmed && !_busy && !p.Running && guide.ReadySteps.Count > 0 &&
                guide.Delivery is { Action: not ToolFlowDeliveryAction.None };
            _overview.Content = guide.CurrentStep is { HasAutomaticItems: false } attention &&
                (attention.SourceUrl is not null || attention.AccessEntry is not null)
                ? Text("AiWorkbench_CheckStep", "核对这一步", "Check this step")
                : L("AiGoal_Overview", "查看详情");
            _overview.Visibility = !p.Running ? Visibility.Visible : Visibility.Collapsed;
            canTry = !p.Running && !hasCurrent && guide.ReadySteps.Any(step => !step.IsOptional) &&
                guide.Delivery is { Action: not ToolFlowDeliveryAction.None };
            if (canTry)
            {
                _currentLabel.Text = Text("AiWorkbench_StartUsing", "下一步 · 开始使用", "Next · start using your tools");
                _useNotice.Text = Text("AiWorkbench_UseBoundary", "准备有记录，目标还需要你在工具中实际完成。",
                    "Preparation is recorded. Complete and check your goal in the selected tool.");
                _useNotice.Visibility = Visibility.Visible;
            }
        }
        _stages.Update(confirmed, !string.IsNullOrWhiteSpace(p.Goal), canTry);
        AutomationProperties.SetName(this, _eyebrow.Text + ". " + _goal.Text + ". " + p.Summary + ". " + _next.Text);
        ToolFlowThemeResources.Refresh(_theme);
    }

    private void ApplyWidth(double width)
    {
        var narrow = width < 460;
        _actions.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        _surface.Padding = new Thickness(narrow ? 14 : 24);
        _currentSurface.Padding = new Thickness(narrow ? 14 : 20);
        _goal.FontSize = narrow ? 21 : 24;
        foreach (var button in new[] { _details, _handoff, _stop, _overview })
            button.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    private static Button NewButton() => new() { FontSize = 13, Padding = new Thickness(14, 9, 14, 9),
        HorizontalContentAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private static string Text(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
    private static string L(string key, string fallback) => ToolFlowGoalText.Get(key, fallback);
}
