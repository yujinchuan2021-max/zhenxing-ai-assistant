using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Controls.AgentChat;

internal enum ToolFlowGoalAction { OpenSource, ConfirmManual, ContinueAutomatic, CheckAgain, Handoff, OpenTool, OpenAgentConsole, OpenLocation }

/// <summary>A goal and one actionable step. Rendering never opens, installs or confirms anything.</summary>
internal sealed class ToolFlowGoalControl : Grid
{
    private readonly TextBlock _goal = new() { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stage = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _feedback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed };
    private readonly Button _primary = new() { Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _confirm = new() { Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _problem = new() { Padding = new Thickness(8, 5, 8, 5), FontSize = 12 };
    private readonly Button _return = new() { Padding = new Thickness(8, 5, 8, 5), FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly StackPanel _actions = new() { Spacing = 7, Orientation = Orientation.Horizontal };
    private readonly Border _currentSurface = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
        Padding = new Thickness(14) };
    private readonly StackPanel _problemChoices = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly StackPanel _confirmation = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Button _saveConfirmation = new() { Padding = new Thickness(12, 7, 12, 7), IsEnabled = false };
    private readonly HashSet<string> _checkedManualIds = new(StringComparer.Ordinal);
    private readonly List<TextBlock> _detailTexts = [];
    private readonly List<TextBlock> _confirmationTexts = [];
    private readonly Expander _later = NewExpander();
    private readonly Expander _ready = NewExpander();
    private readonly Expander _evidence = NewExpander();
    private readonly Expander _usage = NewExpander();
    private readonly Button _handoff = new() { Padding = new Thickness(8, 5, 8, 5), FontSize = 12 };
    private readonly ThemeRefreshScope _theme;
    private ToolFlowGoalGuide? _guide;
    private bool _busy;
    private string? _problemChoice;
    private string? _optionalStepId;
    private bool _confirmationShown;
    private ToolFlowGoalStep? _handoffStep;
    private ToolFlowGoalStep? ActiveStep => _guide?.LaterSteps.FirstOrDefault(step => step.IsOptional && step.Id == _optionalStepId)
        ?? _guide?.CurrentStep;
    internal string? CurrentStepId => ActiveStep?.Id;
    internal event Action<ToolFlowGoalStep, ToolFlowGoalAction>? ActionRequested;

    internal ToolFlowGoalControl()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _theme = ToolFlowThemeResources.Attach(this);
        ToolFlowThemeResources.Bind(_theme, this, BackgroundProperty, ToolFlowThemeResources.Canvas);
        ToolFlowThemeResources.Bind(_theme, _primary, Button.BackgroundProperty, ToolFlowThemeResources.Accent);
        ToolFlowThemeResources.Bind(_theme, _primary, Button.ForegroundProperty, ToolFlowThemeResources.OnAccent);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(_goal);
        heading.Children.Add(_stage);
        Children.Add(heading);
        var content = new StackPanel { Spacing = 12 };
        var current = _currentSurface;
        ToolFlowThemeResources.Bind(_theme, current, Border.BackgroundProperty, ToolFlowThemeResources.Surface);
        ToolFlowThemeResources.Bind(_theme, current, Border.BorderBrushProperty, ToolFlowThemeResources.Stroke);
        var body = new StackPanel { Spacing = 9 };
        body.Children.Add(_title);
        body.Children.Add(_hint);
        _actions.Children.Add(_primary);
        _actions.Children.Add(_confirm);
        body.Children.Add(_actions);
        body.Children.Add(_confirmation);
        body.Children.Add(_problem);
        body.Children.Add(_return);
        body.Children.Add(_problemChoices);
        body.Children.Add(_feedback);
        current.Child = body;
        content.Children.Add(current);
        content.Children.Add(_later);
        content.Children.Add(_ready);
        content.Children.Add(_evidence);
        content.Children.Add(_usage);
        _usage.Content = _handoff;
        var scroller = new ScrollViewer { Content = content, MaxHeight = 450,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroller, 1);
        Children.Add(scroller);
        foreach (var text in new[] { _goal, _stage, _title, _hint, _feedback })
            ToolFlowThemeResources.BindText(_theme, text, text == _stage || text == _feedback
                ? ToolFlowThemeResources.SecondaryText : ToolFlowThemeResources.PrimaryText);
        _primary.Click += (_, _) =>
        {
            if (ActiveStep is { } step)
            {
                if (!step.HasAutomaticItems && step.AccessEntry is { } entry)
                    Request(step, entry.IsGui ? ToolFlowGoalAction.OpenTool : ToolFlowGoalAction.OpenAgentConsole);
                else if (!step.HasAutomaticItems && step.SourceUrl is null) BeginManualConfirmation();
                else Request(step, step.HasAutomaticItems ? ToolFlowGoalAction.ContinueAutomatic : ToolFlowGoalAction.OpenSource);
            }
            else if (_handoffStep is { } ready)
                Request(ready, _guide?.Delivery is { } delivery ? DeliveryAction(delivery.Action) : ToolFlowGoalAction.Handoff);
        };
        _handoff.Click += (_, _) => { if (_handoffStep is { } ready) Request(ready, ToolFlowGoalAction.Handoff); };
        _confirm.Click += (_, _) => BeginManualConfirmation();
        _saveConfirmation.Click += (_, _) =>
        {
            if (_saveConfirmation.IsEnabled && ActiveStep is { } step) Request(step, ToolFlowGoalAction.ConfirmManual);
        };
        _return.Click += (_, _) =>
        {
            if (_busy) return;
            _optionalStepId = _problemChoice = null;
            ResetConfirmation();
            _problemChoices.Visibility = _feedback.Visibility = Visibility.Collapsed;
            ApplyLocalization();
        };
        _problem.Click += (_, _) =>
        {
            if (_busy) return;
            _problemChoices.Visibility = _problemChoices.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        };
        SizeChanged += (_, e) => _actions.Orientation = e.NewSize.Width < 480 ? Orientation.Vertical : Orientation.Horizontal;
        ToolFlowThemeResources.Refresh(_theme);
    }

    internal void Update(ToolFlowGoalGuide guide, bool busy = false)
    {
        var previousStepId = ActiveStep?.Id;
        _guide = guide;
        if (previousStepId != ActiveStep?.Id)
        {
            ResetConfirmation();
            _problemChoice = null;
            _problemChoices.Visibility = _feedback.Visibility = Visibility.Collapsed;
        }
        if (!guide.LaterSteps.Any(step => step.IsOptional && step.Id == _optionalStepId)) _optionalStepId = null;
        _busy = busy;
        ApplyLocalization();
    }

    internal void ApplyLocalization()
    {
        if (_guide is not { } guide) return;
        _detailTexts.Clear();
        _goal.Text = L("AiGoal_YourGoal", "你的目标：") + guide.Goal;
        _stage.Text = guide.Stage + (string.IsNullOrEmpty(guide.Summary) ? "" : " · " + guide.Summary);
        var step = ActiveStep;
        var delivery = guide.Delivery;
        _handoffStep = guide.ReadySteps.FirstOrDefault(candidate => candidate.Rows.Any(row => row.ItemId == delivery?.ItemId))
            ?? guide.ReadySteps.FirstOrDefault(candidate => !candidate.IsOptional);
        _title.Text = step is null ? delivery?.Title ?? L("AiDelivery_FallbackTitle", "下一步：核对使用入口")
            : (step.IsOptional ? L("AiGoal_Optional", "可选") + " · " : "") + step.Title;
        _hint.Text = step?.Hint ?? delivery?.Hint ?? L("AiDelivery_FallbackHint", "准备记录已保留，查看使用说明继续。");
        _primary.Content = step is null ? delivery?.ActionLabel ?? L("AiDelivery_ViewUsage", "查看使用与交接说明")
            : step.HasAutomaticItems ? step.IsOptional ? step.ActionLabel
                : L("AiGoal_ContinueAllInstallations", "继续安装全部待办")
            : step.AccessEntry is not null ? step.ActionLabel
            : step.SourceUrl is not null ? L("AiGoal_OpenWebsite", "打开网站")
            : step.CanConfirmManual ? L("AiGoal_ConfirmManual", "我已按提示完成") : step.ActionLabel;
        _primary.IsEnabled = !_busy && (step is not null && step.State != ToolFlowGoalStepState.Running &&
            (step.HasAutomaticItems || step.AccessEntry is not null || step.SourceUrl is not null || step.CanConfirmManual) ||
            step is null && _handoffStep is not null && (delivery is null || delivery.Action != ToolFlowDeliveryAction.None));
        _confirm.Content = L("AiGoal_ConfirmManual", "我已按提示完成");
        _confirm.Visibility = step is { CanConfirmManual: true } && (step.SourceUrl is not null || step.AccessEntry is not null)
            ? Visibility.Visible : Visibility.Collapsed;
        _confirm.IsEnabled = !_busy && step?.CanConfirmManual == true;
        _problem.Content = L("AiGoal_Problem", "这一步遇到问题");
        _problem.Visibility = step is not null && !step.HasAutomaticItems ? Visibility.Visible : Visibility.Collapsed;
        _problem.IsEnabled = !_busy;
        _return.Content = L("AiGoal_ReturnCurrent", "返回当前步骤");
        _return.Visibility = _optionalStepId is null ? Visibility.Collapsed : Visibility.Visible;
        _return.IsEnabled = !_busy;
        BuildProblemChoices();
        BuildConfirmation();
        _later.Header = L("AiGoal_LaterDetails", "后续步骤与可选工具") + " (" + guide.LaterSteps.Count + ")";
        _later.Visibility = guide.LaterSteps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _later.Content = BuildDetails(guide.LaterSteps, evidence: false);
        _ready.Header = L("AiGoal_ReadyDetails", "已具备与已确认") + " (" + guide.ReadySteps.Count + ")";
        _ready.Visibility = guide.ReadySteps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ready.Content = BuildDetails(guide.ReadySteps, evidence: false);
        _evidence.Header = L("AiGoal_EvidenceDetails", "查看清单与检测记录");
        _evidence.Content = BuildDetails((step is null ? [] : new[] { step }).Concat(guide.LaterSteps).Concat(guide.ReadySteps), evidence: true);
        _evidence.Visibility = step is not null || guide.LaterSteps.Count > 0 || guide.ReadySteps.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        _usage.Header = L("AiDelivery_UsageDetails", "使用与交接说明");
        _usage.Visibility = step is null && _handoffStep is not null && delivery is
            { Action: ToolFlowDeliveryAction.OpenTool or ToolFlowDeliveryAction.OpenAgentConsole or
                ToolFlowDeliveryAction.OpenLocation or ToolFlowDeliveryAction.OpenSource }
            ? Visibility.Visible : Visibility.Collapsed;
        _handoff.Content = L("AiDelivery_ViewUsage", "查看使用与交接说明");
        _handoff.IsEnabled = !_busy;
        AutomationProperties.SetName(this, _goal.Text + ". " + _stage.Text + ". " + _title.Text);
        ToolFlowThemeResources.Refresh(_theme);
    }

    private StackPanel BuildDetails(IEnumerable<ToolFlowGoalStep> steps, bool evidence)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var step in steps)
        {
            var row = new StackPanel { Spacing = 3 };
            row.Children.Add(ThemedText(step.Title + (step.IsOptional ? " · " + L("AiGoal_Optional", "可选") : ""), 13));
            if (!evidence)
                row.Children.Add(ThemedText(step.State == ToolFlowGoalStepState.UserConfirmed
                    ? L("AiGoal_SelfConfirmed", "你已确认；应用未验证账号或实际运行。") : step.Hint, 12, secondary: true));
            else foreach (var item in step.Rows)
                row.Children.Add(ThemedText(item.Name + " · " + ToolFlowResume.Label(item.State) + "\n" + item.StatusLine,
                    12, secondary: true));
            if (!evidence && step.IsOptional && step.State is not ToolFlowGoalStepState.Ready and not ToolFlowGoalStepState.UserConfirmed)
            {
                var prepare = new Button { Content = L("AiGoal_OptionalAction", "我需要这一项"), FontSize = 12, IsEnabled = !_busy };
                prepare.Click += (_, _) =>
                {
                    if (_busy || _guide?.LaterSteps.Contains(step) != true) return;
                    _optionalStepId = step.Id;
                    ResetConfirmation();
                    _problemChoice = null;
                    _problemChoices.Visibility = _feedback.Visibility = Visibility.Collapsed;
                    ApplyLocalization();
                };
                row.Children.Add(prepare);
            }
            panel.Children.Add(row);
        }
        return panel;
    }

    private void BuildProblemChoices()
    {
        _problemChoices.Children.Clear();
        foreach (var choice in new[] { ("site", "AiGoal_SiteBlocked", "网站打不开"),
            ("login", "AiGoal_NotSignedIn", "还没登录或没有权限"), ("uncertain", "AiGoal_NotSure", "不确定怎么做") })
        {
            var button = new Button { Content = L(choice.Item2, choice.Item3), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                IsEnabled = !_busy };
            button.Click += (_, _) =>
            {
                if (_busy) return;
                _problemChoice = choice.Item1;
                RefreshFeedback();
            };
            _problemChoices.Children.Add(button);
        }
        var check = new Button { Content = L("AiGoal_CheckAgain", "重新检查本机工具"), IsEnabled = !_busy, FontSize = 12 };
        check.Click += (_, _) => { if (ActiveStep is { } step) Request(step, ToolFlowGoalAction.CheckAgain); };
        _problemChoices.Children.Add(check);
        RefreshFeedback();
    }

    private void RefreshFeedback()
    {
        _feedback.Text = _problemChoice switch
        {
            "site" => L("AiGoal_SiteBlockedHint", "先检查网络，再打开网站。打不开时请回到对话选择可访问的替代服务；本步骤仍保留。"),
            "login" => L("AiGoal_NotSignedInHint", "打开网站完成登录，确认账号有方案需要的权限。没有权限时可回到对话更换方案，无需先购买。"),
            "uncertain" => ActiveStep?.Hint ?? "",
            _ => "",
        };
        _feedback.Visibility = string.IsNullOrEmpty(_feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BeginManualConfirmation()
    {
        if (_busy || ActiveStep?.CanConfirmManual != true) return;
        _confirmationShown = true;
        BuildConfirmation();
    }

    private void ResetConfirmation()
    {
        _confirmationShown = false;
        _checkedManualIds.Clear();
        _confirmation.Visibility = Visibility.Collapsed;
        _saveConfirmation.IsEnabled = false;
    }

    private static IReadOnlyList<ToolFlowResumeItemRow> ManualRows(ToolFlowGoalStep? step) => step?.Rows
        .Where(row => !row.CanContinueAutomatically && row.State is ToolFlowResumeItemState.NeedsUserAssist or ToolFlowResumeItemState.Failed)
        .ToArray() ?? [];

    private void BuildConfirmation()
    {
        _confirmationTexts.Clear();
        _confirmation.Children.Clear();
        _confirmation.Visibility = _confirmationShown && ActiveStep?.CanConfirmManual == true ? Visibility.Visible : Visibility.Collapsed;
        if (_confirmation.Visibility != Visibility.Visible) { _saveConfirmation.IsEnabled = false; return; }
        var rows = ManualRows(ActiveStep);
        _checkedManualIds.RemoveWhere(id => !rows.Any(row => row.ItemId == id));
        _confirmation.Children.Add(ThemedText(L("AiGoal_ConfirmNotice", "请勾选你已完成的事项；这里只保存你的声明，不代表应用验证通过。"),
            12, secondary: true, confirmation: true));
        foreach (var row in rows)
        {
            var label = new StackPanel { Spacing = 3 };
            label.Children.Add(ThemedText(row.Name, 13, confirmation: true));
            label.Children.Add(ThemedText(string.IsNullOrWhiteSpace(row.ManualHint)
                ? L("AiGoal_ConfirmFallback", "按选定方案完成这一项的配置和必要使用条件。") : row.ManualHint,
                12, secondary: true, confirmation: true));
            var check = new CheckBox { Content = label, IsChecked = _checkedManualIds.Contains(row.ItemId), IsEnabled = !_busy,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            check.Checked += (_, _) => { if (!_busy) { _checkedManualIds.Add(row.ItemId); RefreshConfirmationEnabled(); } };
            check.Unchecked += (_, _) => { if (!_busy) { _checkedManualIds.Remove(row.ItemId); RefreshConfirmationEnabled(); } };
            _confirmation.Children.Add(check);
        }
        _saveConfirmation.Content = L("AiGoal_SaveConfirmation", "保存我的确认");
        _confirmation.Children.Add(_saveConfirmation);
        RefreshConfirmationEnabled();
        ToolFlowThemeResources.Refresh(_theme);
    }

    private void RefreshConfirmationEnabled() => _saveConfirmation.IsEnabled = !_busy && _confirmationShown &&
        ActiveStep?.CanConfirmManual == true && ManualRows(ActiveStep) is { Count: > 0 } rows &&
        rows.All(row => _checkedManualIds.Contains(row.ItemId));

    private void Request(ToolFlowGoalStep step, ToolFlowGoalAction action)
    {
        if (_busy || _guide is null) return;
        if (ActiveStep is null && action is ToolFlowGoalAction.Handoff or ToolFlowGoalAction.OpenTool
            or ToolFlowGoalAction.OpenAgentConsole or ToolFlowGoalAction.OpenLocation or ToolFlowGoalAction.OpenSource)
        {
            if (_guide.CurrentStep is not null || !ReferenceEquals(step, _handoffStep)) return;
            if (action != ToolFlowGoalAction.Handoff && (_guide.Delivery is not { } delivery ||
                DeliveryAction(delivery.Action) != action)) return;
        }
        else if (!ReferenceEquals(step, ActiveStep)) return;
        if (ActiveStep is not null && (action is ToolFlowGoalAction.OpenTool or ToolFlowGoalAction.OpenAgentConsole) &&
            (step.AccessEntry is not { } local || (local.IsGui ? ToolFlowGoalAction.OpenTool : ToolFlowGoalAction.OpenAgentConsole) != action)) return;
        if (action == ToolFlowGoalAction.ConfirmManual && (!step.CanConfirmManual || !_confirmationShown ||
            !ManualRows(step).All(row => _checkedManualIds.Contains(row.ItemId)))) return;
        ActionRequested?.Invoke(step, action);
    }

    private static ToolFlowGoalAction DeliveryAction(ToolFlowDeliveryAction action) => action switch
    {
        ToolFlowDeliveryAction.OpenTool => ToolFlowGoalAction.OpenTool,
        ToolFlowDeliveryAction.OpenAgentConsole => ToolFlowGoalAction.OpenAgentConsole,
        ToolFlowDeliveryAction.OpenLocation => ToolFlowGoalAction.OpenLocation,
        ToolFlowDeliveryAction.OpenSource => ToolFlowGoalAction.OpenSource,
        _ => ToolFlowGoalAction.Handoff,
    };

    private static Expander NewExpander() => new() { HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private TextBlock ThemedText(string text, int size, bool secondary = false, bool confirmation = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        (confirmation ? _confirmationTexts : _detailTexts).Add(label);
        ToolFlowThemeResources.BindText(_theme, label,
            secondary ? ToolFlowThemeResources.SecondaryText : ToolFlowThemeResources.PrimaryText);
        return label;
    }
    private static string L(string key, string fallback) => ToolFlowGoalText.Get(key, fallback);
}
