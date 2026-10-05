using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Real native task-control checks in the empty STA test host. State cases are
/// detached; the explicit attached case uses only the owned offscreen Window.
/// Synthetic plans and access entries never probe, persist, install or open a tool.
/// </summary>
internal static class TaskCases
{
    public static (string Name, Func<Task> Body)[] All() =>
    [
        ("Task_RunningStopStates", () => { RunningStopStates(); return Task.CompletedTask; }),
        ("Task_IdleBusyRestoredStates", () => { IdleBusyRestoredStates(); return Task.CompletedTask; }),
        ("Task_GuidePendingDelivery_TwoThemes", () => { GuidePendingDelivery(); return Task.CompletedTask; }),
        ("Task_GuidePreparedEntries_TwoThemes", () => { GuidePreparedEntries(); return Task.CompletedTask; }),
        ("Task_GuideAttachedDelivery_TwoThemes", GuideAttachedDelivery),
    ];

    private static void RunningStopStates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var control = new ToolFlowTaskControl();
        var details = Field<Button>(control, "_details");
        var handoff = Field<Button>(control, "_handoff");
        var stop = Field<Button>(control, "_stop");
        var raisedActions = 0;
        control.OpenDetails += () => raisedActions++;
        control.OpenHandoff += () => raisedActions++;
        control.StopRequested += () => raisedActions++;
        var running = ToolFlowTaskPresentation.Create(Demo(), running: true,
            activeStep: "Checking the current tool", knownTargets: ["godot"]);

        control.Update(running, busy: true);
        Assert.Equal(Visibility.Collapsed, details.Visibility);
        Assert.False(details.IsEnabled);
        Assert.Equal(Visibility.Collapsed, handoff.Visibility);
        Assert.False(handoff.IsEnabled);
        Assert.Equal(Visibility.Visible, stop.Visibility);
        Assert.True(stop.IsEnabled);

        control.Update(running, busy: true, stopping: true);
        Assert.Equal(Visibility.Visible, stop.Visibility);
        Assert.False(stop.IsEnabled); // a second stop cannot be requested
        Assert.False(details.IsEnabled);
        Assert.False(handoff.IsEnabled);

        control.Update(ToolFlowTaskPresentation.Create(Demo(), knownTargets: ["godot"]));
        Assert.Equal(Visibility.Collapsed, stop.Visibility);
        Assert.False(stop.IsEnabled);
        Assert.Equal(Visibility.Visible, details.Visibility);
        Assert.True(details.IsEnabled);
        Assert.Equal(Visibility.Visible, handoff.Visibility);
        Assert.True(handoff.IsEnabled);
        Assert.Equal(0, raisedActions); // state changes themselves never start an action
    }

    private static void IdleBusyRestoredStates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var languageField = typeof(LocalizationService).GetField("_currentLanguage",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Localization fallback field missing.");
        var originalLanguage = languageField.GetValue(null);
        try
        {
            // In-memory fallback only: no settings write or language broadcast.
            languageField.SetValue(null, LocalizationService.ChineseLanguage);
            var control = new ToolFlowTaskControl();
            var details = Field<Button>(control, "_details");
            var handoff = Field<Button>(control, "_handoff");
            var goal = Field<TextBlock>(control, "_goal");
            var selection = Demo();
            var idle = ToolFlowTaskPresentation.Create(selection, knownTargets: ["godot"]);
            control.Update(idle);
            Assert.True(details.IsEnabled);
            Assert.True(handoff.IsEnabled);
            Assert.StartsWith("当前目标：", goal.Text);
            Assert.Equal(2, control.CurrentStageIndex);
            Assert.Equal(2, control.RecordedStageCount);

            control.Update(idle, selectionConfirmed: false);
            Assert.Equal(0, control.RecordedStageCount);
            Assert.False(details.IsEnabled);
            Assert.False(handoff.IsEnabled);

            control.Update(idle, busy: true);
            Assert.Equal(Visibility.Visible, details.Visibility);
            Assert.Equal(Visibility.Visible, handoff.Visibility);
            Assert.False(details.IsEnabled);
            Assert.False(handoff.IsEnabled); // a dialog/request already in progress blocks duplicates

            control.Update(idle, resumed: true);
            Assert.Same(details, Field<Button>(control, "_details"));
            Assert.Same(handoff, Field<Button>(control, "_handoff"));
            Assert.True(details.IsEnabled);
            Assert.True(handoff.IsEnabled);
            Assert.StartsWith("恢复目标：", goal.Text);
            Assert.EndsWith(selection.ProjectGoal, goal.Text);
            Assert.Contains(goal.Text, AutomationProperties.GetName(control));

            var result = new ToolFlowInstallResult(
                [new(selection.Items[0].ItemId, selection.Items[0].Name,
                    ToolFlowInstallItemStatus.AlreadyInstalled, "Fake checked result")]);
            var checkedPlan = ToolFlowTaskPresentation.Create(selection, result, knownTargets: ["godot"]);
            control.Update(checkedPlan, resumed: true);
            // A legacy summary alone cannot prove the final use/account step.
            Assert.Equal(2, control.CurrentStageIndex);
            Assert.Equal(2, control.RecordedStageCount);
            Assert.True(details.IsEnabled);
            Assert.Equal("查看清单", details.Content); // verified plan changes the action without disabling review
            Assert.True(handoff.IsEnabled);
        }
        finally { languageField.SetValue(null, originalLanguage); }
    }

    private static void GuidePendingDelivery()
    {
        RequireIsolatedData();
        var language = LanguageField();
        var previous = language.GetValue(null);
        try
        {
            language.SetValue(null, LocalizationService.ChineseLanguage);
            foreach (var themeKey in new[] { "Light", "Dark" })
            {
                using var theme = new XamlThemeCases.ThemeTestEnvironment(themeKey);
                var selection = Demo() with { Items = [Demo().Items[0], new()
                    { ItemId = "model", Name = "Agent 模型接入", Kind = "model", ManualHint = "先连接模型" }] };
                ToolFlowResumeItemRow[] rows = [Row(selection.Items[0], ToolFlowResumeItemState.InstalledOrDetected),
                    Row(selection.Items[1], ToolFlowResumeItemState.NeedsUserAssist)];
                var guide = ToolFlowGoalGuide.Create(new(selection, rows, [], 1, 0, 0, 1));
                Assert.NotNull(guide.CurrentStep);
                Assert.Equal(ToolFlowDeliveryAction.None, guide.Delivery!.Action);
                Assert.Single(guide.ReadySteps); // An installed dependency cannot bypass the remaining setup.
                var control = new ToolFlowTaskControl();
                var actions = 0;
                control.OpenDetails += () => actions++;
                control.OpenHandoff += () => actions++;
                control.StopRequested += () => actions++;
                control.Update(guide, selection.FlowName);
                var details = Field<Button>(control, "_details");
                var delivery = Field<Button>(control, "_handoff");
                Assert.Equal("核对这一步", details.Content);
                Assert.Equal(2, control.CurrentStageIndex);
                Assert.Equal(2, control.RecordedStageCount);
                Assert.Equal(Visibility.Visible, details.Visibility);
                Assert.True(details.IsEnabled);
                Assert.Equal(Visibility.Collapsed, delivery.Visibility);
                Assert.False(delivery.IsEnabled);
                Assert.Contains(guide.CurrentStep.Title, Field<TextBlock>(control, "_next").Text);
                Assert.Contains(guide.Summary, Field<TextBlock>(control, "_summary").Text);
                AssertTaskPalette(control, themeKey);

                control.Update(guide, selection.FlowName, busy: true);
                Assert.False(details.IsEnabled);
                Assert.False(delivery.IsEnabled);
                control.Update(guide, selection.FlowName, running: true, busy: true);
                Assert.Equal(Visibility.Collapsed, details.Visibility);
                Assert.Equal(Visibility.Collapsed, delivery.Visibility);
                Assert.True(Field<Button>(control, "_stop").IsEnabled);
                control.Update(guide, selection.FlowName, running: true, busy: true, stopping: true);
                Assert.False(Field<Button>(control, "_stop").IsEnabled);

                var empty = ToolFlowGoalGuide.Create(new(selection with { Items = [] }, [], [], 0, 0, 0, 0));
                control.Update(empty, selection.FlowName);
                Assert.False(delivery.IsEnabled);
                Assert.Contains(empty.Summary, Field<TextBlock>(control, "_summary").Text);
                Assert.Equal(0, actions); // All updates are display-only.
            }
        }
        finally { language.SetValue(null, previous); }
    }

    private static void GuidePreparedEntries()
    {
        RequireIsolatedData();
        var language = LanguageField();
        var previous = language.GetValue(null);
        try
        {
            language.SetValue(null, LocalizationService.ChineseLanguage);
            foreach (var themeKey in new[] { "Light", "Dark" })
            {
                using var theme = new XamlThemeCases.ThemeTestEnvironment(themeKey);
                var control = new ToolFlowTaskControl();
                var actions = 0;
                control.OpenDetails += () => actions++;
                control.OpenHandoff += () => actions++;
                foreach (var (guide, action, label) in PreparedGuides())
                {
                    Assert.Null(guide.CurrentStep);
                    Assert.Equal(action, guide.Delivery!.Action);
                    control.Update(guide, "Synthetic saved plan");
                    var delivery = Field<Button>(control, "_handoff");
                    var overview = Field<Button>(control, "_overview");
                    Assert.Equal(label, delivery.Content);
                    Assert.Equal(Visibility.Visible, delivery.Visibility);
                    Assert.True(delivery.IsEnabled);
                    Assert.Equal(3, control.CurrentStageIndex);
                    Assert.Equal(3, control.RecordedStageCount); // Start using stays current, never goal-complete.
                    Assert.Equal(Visibility.Collapsed, Field<Button>(control, "_details").Visibility);
                    Assert.Equal(Visibility.Visible, overview.Visibility);
                    Assert.True(overview.IsEnabled);
                    Assert.Contains(guide.Delivery.Hint, Field<TextBlock>(control, "_next").Text);
                    Assert.Contains(guide.Summary, Field<TextBlock>(control, "_summary").Text);
                    if (action == ToolFlowDeliveryAction.OpenSource)
                    {
                        Assert.Contains("你确认", Field<TextBlock>(control, "_summary").Text);
                        Assert.Contains("实际使用尚未验证", Field<TextBlock>(control, "_summary").Text);
                        Assert.DoesNotContain("验证通过", Field<TextBlock>(control, "_summary").Text);
                        Assert.DoesNotContain("连接成功", Field<TextBlock>(control, "_next").Text);
                    }
                    AssertTaskPalette(control, themeKey);
                    control.Update(guide, "Synthetic saved plan", busy: true);
                    Assert.False(delivery.IsEnabled);
                    Assert.False(overview.IsEnabled);
                    control.Update(guide, "Synthetic saved plan", resumed: true);
                    Assert.True(delivery.IsEnabled);
                    Assert.StartsWith("恢复目标：", Field<TextBlock>(control, "_goal").Text);
                    Assert.Equal(label, delivery.Content);
                }
                Assert.Equal(0, actions); // Restoring every route performs no launch or self-confirmation.
            }
        }
        finally { language.SetValue(null, previous); }
    }

    private static async Task GuideAttachedDelivery()
    {
        RequireIsolatedData();
        var language = LanguageField();
        var previous = language.GetValue(null);
        var previousOverride = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        try
        {
            language.SetValue(null, LocalizationService.ChineseLanguage);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            foreach (var themeKey in new[] { "Light", "Dark" })
            {
                await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
                {
                    var previousTheme = root.RequestedTheme;
                    try
                    {
                        root.RequestedTheme = themeKey == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
                        foreach (var (guide, action, label) in PreparedGuides())
                        {
                            Assert.Equal(action, guide.Delivery!.Action);
                            root.Children.Clear();
                            var control = new ToolFlowTaskControl { Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
                                VerticalAlignment = VerticalAlignment.Top, RequestedTheme = root.RequestedTheme };
                            var actions = 0;
                            control.OpenHandoff += () => actions++;
                            control.ToolAccess.ActionRequested += (_, _) => actions++;
                            control.Update(guide, "Synthetic saved plan");
                            if (guide.Delivery.AccessEntry is { } entry)
                                control.ToolAccess.Update([entry], busy: false);
                            var loaded = new TaskCompletionSource();
                            control.Loaded += (_, _) => loaded.TrySetResult();
                            root.Children.Add(control);
                            Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(6000)) == loaded.Task,
                                "The isolated delivery control did not attach.");
                            await SettleAsync(root);
                            Assert.Equal(root.RequestedTheme, control.ActualTheme);
                            Assert.InRange(control.ActualWidth, 319, 321);
                            Assert.True(control.ActualHeight > 0);
                            Assert.Equal(Orientation.Vertical, Field<StackPanel>(control, "_actions").Orientation);
                            var delivery = Field<Button>(control, "_handoff");
                            Assert.Equal(label, delivery.Content);
                            Assert.True(delivery.ActualHeight > 0);
                            Assert.InRange(delivery.ActualWidth, 1, control.ActualWidth);
                            Assert.True(VisualTreeHelper.GetChildrenCount(delivery) > 0);
                            AssertTaskPalette(control, themeKey);
                            if (guide.Delivery.AccessEntry is not null)
                            {
                                Assert.Equal(control.ActualTheme, control.ToolAccess.ActualTheme);
                                AssertToolAccessPalette(control);
                            }
                            if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                                await GoalGuideCases.SaveControlPreviewAsync(control, "task-guide-320-" +
                                    themeKey.ToLowerInvariant() + "-" + action.ToString().ToLowerInvariant() + ".png");
                            Assert.Equal(0, actions); // Native loading and templates do not start a product action.
                            ((IInvokeProvider)new ButtonAutomationPeer(delivery).GetPattern(PatternInterface.Invoke)).Invoke();
                            await SettleAsync(root);
                            Assert.Equal(1, actions); // Only the synthetic callback is registered; no executable is opened.
                            control.Update(guide, "Synthetic saved plan", busy: true);
                            if (guide.Delivery.AccessEntry is { } busyEntry)
                                control.ToolAccess.Update([busyEntry], busy: true);
                            await SettleAsync(root);
                            Assert.False(delivery.IsEnabled);
                            Assert.False(Field<Button>(control, "_overview").IsEnabled);
                            Assert.Equal(1, actions);
                        }
                    }
                    finally { root.RequestedTheme = previousTheme; }
                });
            }
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousOverride;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
            language.SetValue(null, previous);
        }
    }

    private static (ToolFlowGoalGuide Guide, ToolFlowDeliveryAction Action, string Label)[] PreparedGuides() =>
    [
        (PreparedGuide(new() { ItemId = "music", Name = "音乐生成服务", Kind = "service", SourceUrl = "https://example.com/music" },
            ToolFlowResumeItemState.UserReportedDone), ToolFlowDeliveryAction.OpenSource, "打开所选服务"),
        (PreparedGuide(new() { ItemId = "engine", Name = "Godot", Kind = "software", InstallTargetKey = "godot" },
            ToolFlowResumeItemState.InstalledOrDetected, new("godot", "Godot", @"C:\synthetic\godot.exe", @"C:\synthetic", true)),
            ToolFlowDeliveryAction.OpenTool, "打开所选工具"),
        (PreparedGuide(new() { ItemId = "agent", Name = "Codex", Kind = "agent", InstallTargetKey = "codex" },
            ToolFlowResumeItemState.InstalledOrDetected, new("codex", "Codex", @"C:\synthetic\codex.exe", @"C:\synthetic", false)),
            ToolFlowDeliveryAction.OpenAgentConsole, "打开所选 Agent"),
        (PreparedGuide(new() { ItemId = "runtime", Name = "Node.js", Kind = "software", InstallTargetKey = "node" },
            ToolFlowResumeItemState.InstalledOrDetected, new("node", "Node.js", @"C:\synthetic\node.exe", @"C:\synthetic", false)),
            ToolFlowDeliveryAction.OpenLocation, "打开工具位置"),
    ];

    private static ToolFlowGoalGuide PreparedGuide(ToolFlowItem item, ToolFlowResumeItemState state,
        ToolFlowToolAccessEntry? access = null)
    {
        var selection = Demo() with { Items = [item] };
        var view = new ToolFlowResumeView(selection, [Row(item, state)], [],
            state == ToolFlowResumeItemState.InstalledOrDetected ? 1 : 0,
            state == ToolFlowResumeItemState.UserReportedDone ? 1 : 0, 0, 0);
        return ToolFlowGoalGuide.Create(view, accessEntries: access is null ? [] : [access]);
    }

    private static ToolFlowResumeItemRow Row(ToolFlowItem item, ToolFlowResumeItemState state) => new()
    {
        ItemId = item.ItemId, Name = item.Name, Kind = item.Kind, State = state,
        StatusLine = state == ToolFlowResumeItemState.UserReportedDone
            ? "用户自报完成；应用未验证账号或实际使用。" : "Synthetic preparation evidence only.",
        ManualHint = item.ManualHint,
    };

    private static void AssertTaskPalette(ToolFlowTaskControl control, string themeKey)
    {
        var surface = Assert.IsType<Border>(Assert.Single(control.Children));
        ToolFlowPaletteAssertions.Readable(surface.Background, Field<TextBlock>(control, "_goal").Foreground,
            Field<TextBlock>(control, "_summary").Foreground, themeKey);
        ToolFlowPaletteAssertions.Solid(surface.BorderBrush);
        foreach (var name in new[] { "_plan", "_summary", "_next" })
        {
            var text = Field<TextBlock>(control, name);
            Assert.Equal(1d, text.Opacity);
            ToolFlowPaletteAssertions.ContrastAtLeast(surface.Background, text.Foreground, 4.5);
        }
        var currentSurface = Field<Border>(control, "_currentSurface");
        ToolFlowPaletteAssertions.ContrastAtLeast(currentSurface.Background, Field<TextBlock>(control, "_summary").Foreground, 4.5);
        ToolFlowPaletteAssertions.ContrastAtLeast(currentSurface.Background, Field<TextBlock>(control, "_next").Foreground, 4.5);
    }

    private static void AssertToolAccessPalette(ToolFlowTaskControl control)
    {
        var rows = typeof(ToolFlowToolAccessControl).GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(control.ToolAccess) as StackPanel ?? throw new InvalidOperationException("Tool access rows missing.");
        var row = Assert.IsType<StackPanel>(Assert.Single(rows.Children));
        var name = Assert.IsType<TextBlock>(row.Children[0]);
        var surface = Assert.IsType<Border>(Assert.Single(control.Children));
        Assert.Equal(1d, name.Opacity);
        ToolFlowPaletteAssertions.ContrastAtLeast(surface.Background, name.Foreground, 4.5);
        Assert.Equal(ToolFlowPaletteAssertions.Solid(Field<TextBlock>(control, "_goal").Foreground).Color,
            ToolFlowPaletteAssertions.Solid(name.Foreground).Color);
    }

    private static FieldInfo LanguageField() => typeof(LocalizationService).GetField("_currentLanguage",
        BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Localization fallback field missing.");

    private static async Task SettleAsync(Grid root)
    {
        await Task.Delay(120);
        root.UpdateLayout();
    }

    private static ToolFlowSelection Demo() => new()
    {
        FlowId = "task-demo-flow", SubmissionId = "task-demo-submission",
        Origin = ToolFlowOrigin.User, SelectedAtUtc = DateTimeOffset.UtcNow,
        ProjectGoal = "Build a small demo game", GoalDescription = "Build a small demo game",
        FlowName = "Demo preparation plan", FlowText = "Demo only",
        UploadEnabledAtSelection = false, Conversation = [],
        Items = [new() { ItemId = "demo-godot", Name = "Godot", Kind = "software", InstallTargetKey = "godot" }],
    };

    private static T Field<T>(ToolFlowTaskControl control, string name) where T : class =>
        typeof(ToolFlowTaskControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(control) as T ?? throw new InvalidOperationException("Task control field missing: " + name);

    private static void RequireIsolatedData()
    {
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(declared), "Task cases require a fresh ZXAI_DATA_ROOT.");
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
    }
}
