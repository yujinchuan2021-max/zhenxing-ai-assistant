using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Real workbench/detail controls; synthetic plan and probe, no product page or external actions.</summary>
internal static class LocalToolReadinessCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("LocalReadiness_GodotTraeReadyAndDeferredModel_NativeWorkbench", ReadyWorkbench),
    ];

    private static async Task ReadyWorkbench()
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        const string deferredHint = "可后续配置，先用 Trae 内置模型；后续按所选服务说明填写 API Key。";
        var selection = new ToolFlowSelection
        {
            FlowId = "native-local-readiness", SubmissionId = "native-local-readiness-submission",
            Origin = ToolFlowOrigin.Assistant, SelectedAtUtc = DateTimeOffset.Parse("2026-10-05T02:00:00Z"),
            FlowName = "Godot + Trae", ProjectGoal = "制作一款 Windows 2D 游戏", GoalDescription = "Synthetic fixture",
            FlowText = "先复用 Godot 和 Trae，国内 API 可后续配置。", UploadEnabledAtSelection = false, Conversation = [],
            Items =
            [
                new() { ItemId = "engine", Name = "Godot Engine（已安装，直接复用）", Kind = "2D游戏引擎", SourceUrl = "https://godotengine.org/" },
                new() { ItemId = "agent", Name = "Trae", Kind = "AI 编程 Agent", InstallTargetKey = "trae", SourceUrl = "https://www.trae.ai/" },
                new() { ItemId = "model", Name = "自有国内模型 API 接入（可后续配置，先用 Trae 内置模型）",
                    Kind = "模型接入（人工配置）", SourceUrl = "https://platform.deepseek.com/", ManualHint = deferredHint },
            ],
        };
        var before = JsonSerializer.Serialize(selection);
        var preparationService = new ToolFlowPreparationService(["godot", "trae"], (key, _) =>
            Task.FromResult<InstalledToolEvidence?>(new(key, key == "godot" ? "Godot" : "Trae",
                @"C:\FixtureTools\" + key + @"\" + key + ".exe")));
        var preparation = await preparationService.PrepareAsync(selection.Items);
        var view = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection), preparation);
        ToolFlowToolAccessEntry[] access =
        [
            new("godot", "Godot", @"C:\FixtureTools\godot\godot.exe", @"C:\FixtureTools\godot", true),
            new("trae", "Trae", @"C:\FixtureTools\trae\trae.exe", @"C:\FixtureTools\trae", true),
        ];
        var guide = ToolFlowGoalGuide.Create(view, accessEntries: access);
        Assert.Null(guide.CurrentStep);
        Assert.Equal("trae", guide.Delivery!.AccessEntry!.TargetKey);
        Assert.Equal(ToolFlowDeliveryAction.OpenTool, guide.Delivery.Action);
        Assert.Equal(2, guide.ReadySteps.Count);
        var deferred = Assert.Single(guide.LaterSteps);
        Assert.Equal("model", Assert.Single(deferred.Rows).ItemId);
        Assert.True(deferred.IsOptional);
        Assert.Equal(ToolFlowGoalStepState.NeedsUserAssist, deferred.State);

        // The user's other saved plan has a required model item with no custom
        // hint. Installation evidence may provide an entry, not complete access.
        var requiredSelection = selection with
        {
            Items = selection.Items.Select(item => item.ItemId == "model" ? item with
            {
                Name = "自有国内模型 API 接入（DeepSeek/通义/智谱/Kimi 等 OpenAI 兼容）",
                Kind = "模型接入（人工配置）", ManualHint = null,
            } : item).ToList(),
        };
        var requiredBefore = JsonSerializer.Serialize(requiredSelection);
        var requiredPreparation = await preparationService.PrepareAsync(requiredSelection.Items);
        var requiredView = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(requiredSelection), requiredPreparation);
        var requiredGuide = ToolFlowGoalGuide.Create(requiredView, accessEntries: access);
        var requiredStep = Assert.IsType<ToolFlowGoalStep>(requiredGuide.CurrentStep);
        Assert.Equal("model", Assert.Single(requiredStep.Rows).ItemId);
        Assert.Equal(ToolFlowGoalStepState.NeedsUserAssist, requiredStep.State);
        Assert.False(requiredStep.IsOptional);
        Assert.Equal("trae", requiredStep.AccessEntry!.TargetKey);
        Assert.Null(requiredStep.SourceUrl);
        Assert.Equal(ToolFlowDeliveryAction.None, requiredGuide.Delivery!.Action);

        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1240, 950));
            var previousTheme = root.RequestedTheme;
            root.RequestedTheme = ElementTheme.Light;
            var preview = new Grid { Padding = new Thickness(16), ColumnSpacing = 24, RequestedTheme = ElementTheme.Light };
            preview.ColumnDefinitions.Add(new() { Width = new GridLength(500) });
            preview.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var task = new ToolFlowTaskControl { VerticalAlignment = VerticalAlignment.Top };
            var details = new ToolFlowGoalControl { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
            Grid.SetColumn(details, 1);
            preview.Children.Add(task);
            preview.Children.Add(details);
            var handoffs = 0;
            var primaryRequests = 0;
            var goalRequests = new List<ToolFlowGoalAction>();
            task.OpenHandoff += () => handoffs++;
            task.PrimaryRequested += () => primaryRequests++;
            details.ActionRequested += (_, action) => goalRequests.Add(action);
            task.Update(guide, selection.FlowName);
            task.ToolAccess.Update(access, busy: false);
            details.Update(guide);
            root.Children.Add(preview);
            try
            {
                await Settle(root);
                preview.Background = ThemeResourceResolver.ResolveBrush(task, ToolFlowThemeResources.Canvas);
                Assert.Equal(ElementTheme.Light, task.ActualTheme);
                Assert.Equal(new[] { "godot", "trae" }, task.ToolAccess.Entries.Select(entry => entry.TargetKey));
                var handoff = Field<Button>(task, "_handoff");
                Assert.Equal(Visibility.Visible, handoff.Visibility);
                Assert.True(handoff.IsEnabled && handoff.ActualWidth > 0 && handoff.ActualHeight > 0);
                Assert.Equal(guide.Delivery.ActionLabel, handoff.Content);
                Assert.Equal(Visibility.Collapsed, Field<Button>(task, "_details").Visibility);
                Assert.True(VisualTreeHelper.GetChildrenCount(handoff) > 0);
                Assert.Contains(Nodes(task.ToolAccess).OfType<TextBlock>(), text => text.Text == "Godot");
                Assert.Contains(Nodes(task.ToolAccess).OfType<TextBlock>(), text => text.Text == "Trae");
                var later = Field<Expander>(details, "_later");
                Assert.Equal(Visibility.Visible, later.Visibility);
                Assert.False(later.IsExpanded);
                Assert.Equal(0, handoffs + primaryRequests + goalRequests.Count);

                if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                {
                    await GoalGuideCases.SaveControlPreviewAsync(preview, "local-tool-readiness-light.png");
                    AssertPreview("local-tool-readiness-light.png");
                }

                // Collapsed template content has not inherited layout yet. The
                // localized action title may use the selected Agent's name, while
                // the saved model identity and deferred instructions remain intact.
                later.IsExpanded = true;
                await Settle(root);
                var laterText = Nodes(Assert.IsAssignableFrom<DependencyObject>(later.Content)).OfType<TextBlock>().ToArray();
                Assert.Contains(laterText, text => text.ActualHeight > 0 && text.Text.Contains(deferred.Title, StringComparison.Ordinal));
                Assert.Contains(laterText, text => text.ActualHeight > 0 && text.Text.Contains(deferredHint, StringComparison.Ordinal));
                if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                {
                    await GoalGuideCases.SaveControlPreviewAsync(preview, "local-tool-readiness-details-light.png");
                    AssertPreview("local-tool-readiness-details-light.png");
                }
                later.IsExpanded = false;
                await Settle(root);

                // Invoke real native buttons, but subscribe only to in-memory request observers.
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(handoff)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                await Settle(root);
                Assert.Equal(1, handoffs);
                Assert.Equal(0, primaryRequests);
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(Field<Button>(details, "_primary"))
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                await Settle(root);
                Assert.Equal(ToolFlowGoalAction.OpenTool, Assert.Single(goalRequests));
                Assert.Equal(before, JsonSerializer.Serialize(selection));

                task.Update(requiredGuide, requiredSelection.FlowName);
                details.Update(requiredGuide);
                await Settle(root);
                var setupButton = Field<Button>(task, "_details");
                Assert.Equal(Visibility.Visible, setupButton.Visibility);
                Assert.True(setupButton.IsEnabled && setupButton.ActualHeight > 0);
                Assert.Equal(requiredStep.ActionLabel, setupButton.Content);
                Assert.Equal(Visibility.Collapsed, handoff.Visibility);
                Assert.Equal(requiredStep.Id, details.CurrentStepId);
                var goalSetupButton = Field<Button>(details, "_primary");
                Assert.Equal(requiredStep.ActionLabel, goalSetupButton.Content);
                Assert.True(goalSetupButton.IsEnabled && goalSetupButton.ActualHeight > 0);
                Assert.Equal(new[] { "godot", "trae" }, task.ToolAccess.Entries.Select(entry => entry.TargetKey));
                if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                {
                    await GoalGuideCases.SaveControlPreviewAsync(preview, "local-tool-readiness-required-model-light.png");
                    AssertPreview("local-tool-readiness-required-model-light.png");
                }
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(setupButton)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(goalSetupButton)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                await Settle(root);
                Assert.Equal(1, primaryRequests);
                Assert.Equal(1, handoffs); // Required setup must not use the completed-plan handoff.
                Assert.Equal(2, goalRequests.Count);
                Assert.All(goalRequests, action => Assert.Equal(ToolFlowGoalAction.OpenTool, action));
                Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, requiredView.Rows.Single(row => row.ItemId == "model").State);
                Assert.DoesNotContain(requiredGuide.ReadySteps, step => step.Rows.Any(row => row.ItemId == "model"));
                Assert.Equal(requiredBefore, JsonSerializer.Serialize(requiredSelection));
                Console.WriteLine("LOCAL_READINESS_WORKBENCH|ready=godot,trae|primary=local-agent|deferred-api=collapsed|external-actions=0");
                Console.WriteLine("LOCAL_READINESS_REQUIRED_MODEL|primary=local-trae|website=none|model-still-unverified=true|external-actions=0");
            }
            finally { root.Children.Remove(preview); root.RequestedTheme = previousTheme; await Settle(root); }
        });
    }

    private static void AssertPreview(string fileName)
    {
        var path = Path.Combine(DataRoots.EffectiveTestRoot!, fileName);
        Assert.True(File.Exists(path) && new FileInfo(path).Length > 100, "Expected native control preview: " + path);
    }
    private static T Field<T>(object instance, string name) => Assert.IsType<T>(instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance));
    private static IEnumerable<DependencyObject> Nodes(DependencyObject element)
    {
        yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in Nodes(VisualTreeHelper.GetChild(element, index))) yield return child;
    }
    private static async Task Settle(Grid root)
    {
        await Task.Delay(180);
        root.UpdateLayout();
        await Task.Delay(90);
        root.UpdateLayout();
    }
}
