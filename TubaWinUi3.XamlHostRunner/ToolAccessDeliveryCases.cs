using System.Reflection;
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

/// <summary>Native delivery UI only. Synthetic files stay beneath the isolated data root and are never executed.</summary>
internal static class ToolAccessDeliveryCases
{
    public static (string Name, Func<Task> Body)[] All() => [.. Detached(), .. Attached()];

    // ResetForTest in ThemeTestEnvironment is global. Register these before
    // any case creates the shared Window and starts native template layout.
    internal static (string Name, Func<Task> Body)[] Detached() =>
    [
        ("ToolAccess_FirstReadyAndManualFold", () => { FirstReadyAndManualFold(); return Task.CompletedTask; }),
        ("ToolAccess_ShortcutEvidenceAndRetry", () => { ShortcutEvidenceAndRetry(); return Task.CompletedTask; }),
        ("ToolAccess_CliInstructionsAndUnknownTarget", () => { CliInstructionsAndUnknownTarget(); return Task.CompletedTask; }),
    ];

    internal static (string Name, Func<Task> Body)[] Attached() =>
    [
        ("ToolAccess_AttachedDelivery_TwoThemes", AttachedDelivery),
        ("ToolAccess_TaskGoalHostThemeAndRemount", TaskGoalHostThemeAndRemount),
    ];

    private static void FirstReadyAndManualFold()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var control = new ToolFlowToolAccessControl();
        var entry = Gui();
        var actions = 0;
        control.ActionRequested += (_, _) => actions++;
        control.Update([], busy: false);
        Assert.Equal(Visibility.Collapsed, control.Visibility);
        control.Update([entry], busy: false);
        var expander = Field<Expander>(control, "_expander");
        Assert.True(expander.IsExpanded); // First ready entries are immediately discoverable.
        var row = Assert.Single(Field<StackPanel>(control, "_rows").Children);
        var buttons = Buttons(control).ToArray();
        control.Collapse();
        control.Update([entry], busy: true);
        control.ApplyLocalization();
        Assert.False(expander.IsExpanded);
        Assert.Same(row, Assert.Single(Field<StackPanel>(control, "_rows").Children));
        Assert.All(buttons, button => Assert.False(button.IsEnabled));
        control.Update([entry], busy: false);
        Assert.Same(row, Assert.Single(Field<StackPanel>(control, "_rows").Children));
        Assert.All(buttons, button => Assert.True(button.IsEnabled));
        control.Update([], busy: true);
        control.Update([entry], busy: false);
        Assert.False(expander.IsExpanded); // An empty intermediate probe does not reset manual folding.
        control.Reset();
        control.Update([entry], busy: false);
        Assert.True(expander.IsExpanded); // A new task has its own initial disclosure.
        Assert.Equal(0, actions);
    }

    private static void ShortcutEvidenceAndRetry()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        WithChinese(() =>
        {
            var control = new ToolFlowToolAccessControl();
            var entry = Gui();
            var ready = new ToolFlowPostInstallDeliveryResult("engine", "Godot",
                ToolFlowPostInstallDeliveryKind.DesktopShortcutReady, "Synthetic verified delivery")
                { TargetKey = "godot", ShortcutPath = @"C:\synthetic\Godot.lnk" };
            control.Update([entry], busy: false,
                deliveryByTarget: new Dictionary<string, ToolFlowPostInstallDeliveryResult> { ["godot"] = ready });
            Assert.Contains("桌面图标已创建", Texts(control).Select(x => x.Text).Single(x => x.Contains("桌面图标")));
            Assert.Contains(Buttons(control), x => Equals(x.Content, "打开软件"));
            Assert.DoesNotContain(Buttons(control), x => Equals(x.Content, "创建桌面图标"));

            var failed = ready with { Kind = ToolFlowPostInstallDeliveryKind.Failed, ShortcutPath = null };
            control.Update([entry], busy: false,
                deliveryByTarget: new Dictionary<string, ToolFlowPostInstallDeliveryResult> { ["godot"] = failed });
            Assert.Contains(Buttons(control), x => Equals(x.Content, "重试桌面图标"));
            Assert.Contains(Texts(control), x => x.Text.Contains("桌面图标未创建"));

            control.Update([entry], busy: false,
                deliveryByTarget: new Dictionary<string, ToolFlowPostInstallDeliveryResult>
                    { ["godot"] = ready with { TargetKey = "another-tool" } });
            Assert.DoesNotContain(Texts(control), x => x.Text.Contains("桌面图标已创建"));
            Assert.Contains(Buttons(control), x => Equals(x.Content, "创建桌面图标"));
        });
    }

    private static void CliInstructionsAndUnknownTarget()
    {
        using var files = new SyntheticFiles();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        WithChinese(() =>
        {
            var control = new ToolFlowToolAccessControl();
            var node = files.Entry("node", "Node.js", "node.exe");
            var codex = files.Entry("codex", "Codex", "codex.exe");
            control.Update([node, codex], busy: false);
            Assert.Contains(Texts(control), x => x.Text.Contains("PowerShell"));
            Assert.Contains(Texts(control), x => x.Text == "& '" + node.ExecutablePath!.Replace("'", "''") + "' --version");
            Assert.Equal(2, Buttons(control).Count(x => Equals(x.Content, "复制启动命令")));
            Assert.Single(Buttons(control).Where(x => Equals(x.Content, "打开所选 Agent")));
            Assert.DoesNotContain(Buttons(control), x => Equals(x.Content, "创建桌面图标"));

            control.Update([new("unknown-cli", "Unknown CLI", node.ExecutablePath, files.Directory, false)], busy: false);
            Assert.DoesNotContain(Buttons(control), x => Equals(x.Content, "复制启动命令"));
            Assert.DoesNotContain(Texts(control), x => x.Text.StartsWith("& '"));
            Assert.Contains(Texts(control), x => x.Text.Contains("打开位置查看说明"));
        });
    }

    private static async Task AttachedDelivery()
    {
        using var files = new SyntheticFiles();
        var language = LanguageField();
        var previousLanguage = language.GetValue(null);
        var previousOverride = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        try
        {
            language.SetValue(null, LocalizationService.ChineseLanguage);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
                {
                    var previousTheme = root.RequestedTheme;
                    try
                    {
                        Console.WriteLine("TOOL_ACCESS_STAGE|" + theme + "|attach"); Console.Out.Flush();
                        root.RequestedTheme = theme;
                        var control = new ToolFlowToolAccessControl
                        {
                            Width = 320, RequestedTheme = theme, HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                        };
                        var entry = files.Entry("node", "Node.js", "node.exe");
                        var actions = new List<ToolAccessAction>();
                        control.ActionRequested += (_, action) => actions.Add(action);
                        // Enough tools to exercise the retained fixed-height scroll area.
                        var entries = new[] { entry }.Concat(Enumerable.Range(1, 5)
                            .Select(i => new ToolFlowToolAccessEntry("unknown-" + i, "Synthetic tool " + i,
                                entry.ExecutablePath, files.Directory, false))).ToArray();
                        control.Update(entries, busy: false);
                        var loaded = new TaskCompletionSource();
                        control.Loaded += (_, _) => loaded.TrySetResult();
                        root.Children.Add(control);
                        Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(6000)) == loaded.Task);
                        await SettleAsync(root);
                        // The native Expander's composition transition may outlive
                        // layout. Capture its settled first reveal, including an
                        // opaque fixture canvas rather than white-viewer alpha.
                        await Task.Delay(350);
                        root.UpdateLayout();
                        control.Background = ThemeResourceResolver.ResolveBrush(control, ToolFlowThemeResources.Canvas);
                        var expander = Field<Expander>(control, "_expander");
                        Assert.True(expander.IsExpanded);
                        var scroll = Assert.IsType<ScrollViewer>(expander.Content);
                        Assert.Equal(230d, scroll.MaxHeight);
                        Assert.InRange(scroll.ActualHeight, 1, 231);
                        Console.WriteLine($"TOOL_ACCESS_VIEWPORT|{theme}|offset={scroll.VerticalOffset:F1}|viewport={scroll.ViewportHeight:F1}|extent={scroll.ExtentHeight:F1}");
                        Console.Out.Flush();
                        Assert.InRange(scroll.VerticalOffset, 0, .1);
                        Assert.Equal(theme, control.ActualTheme);
                        Assert.InRange(control.ActualWidth, 319, 321);
                        var firstRow = Assert.IsType<StackPanel>(Field<StackPanel>(control, "_rows").Children[0]);
                        var firstName = Assert.IsType<TextBlock>(firstRow.Children[0]);
                        var firstHint = Assert.IsType<TextBlock>(firstRow.Children[1]);
                        var nameY = firstName.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
                        var hintY = firstHint.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
                        Assert.InRange(nameY, 0, scroll.ViewportHeight - firstName.ActualHeight);
                        Assert.InRange(hintY, 0, scroll.ViewportHeight - firstHint.ActualHeight);
                        foreach (var row in Field<StackPanel>(control, "_rows").Children.Cast<StackPanel>())
                        {
                            Assert.Equal(Orientation.Vertical, row.Children.OfType<StackPanel>().Single().Orientation);
                            foreach (var text in row.Children.OfType<TextBlock>())
                            {
                                Console.WriteLine($"TOOL_ACCESS_LAYOUT|{theme}|row={row.ActualWidth:F1}x{row.ActualHeight:F1}|text={text.ActualWidth:F1}x{text.ActualHeight:F1}");
                                Console.Out.Flush();
                                Assert.True(text.ActualHeight > 0, "Tool name, guidance and command must receive native layout height.");
                                Assert.Equal(1d, text.Opacity);
                                ToolFlowPaletteAssertions.ContrastAtLeast(
                                    ThemeResourceResolver.ResolveBrush(control, ToolFlowThemeResources.Canvas), text.Foreground, 4.5);
                            }
                        }
                        if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                            await GoalGuideCases.SaveControlPreviewAsync(control,
                                "tool-access-320-" + (theme == ElementTheme.Dark ? "dark" : "light") + ".png");
                        var copy = Buttons(control).Single(x => Equals(x.Content, "复制启动命令"));
                        Assert.True(copy.ActualHeight > 0);
                        Assert.InRange(copy.ActualWidth, 1, control.ActualWidth);
                        Assert.Empty(actions);
                        Console.WriteLine("TOOL_ACCESS_STAGE|" + theme + "|invoke-copy"); Console.Out.Flush();
                        ((IInvokeProvider)new ButtonAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke();
                        await SettleAsync(root);
                        Console.WriteLine("TOOL_ACCESS_STAGE|" + theme + "|after-copy"); Console.Out.Flush();
                        Assert.Equal(ToolAccessAction.CopyCliCommand, Assert.Single(actions)); // A callback only; no clipboard or executable.
                        var originalRow = Field<StackPanel>(control, "_rows").Children[0];
                        control.Collapse();
                        control.Update(entries, busy: true);
                        await SettleAsync(root);
                        Console.WriteLine("TOOL_ACCESS_STAGE|" + theme + "|after-collapse"); Console.Out.Flush();
                        Assert.Same(originalRow, Field<StackPanel>(control, "_rows").Children[0]);
                        Assert.Same(copy, Buttons(control).Single(x => Equals(x.Content, "复制启动命令")));
                        Assert.False(copy.IsEnabled);
                        Assert.False(expander.IsExpanded);
                        Assert.Single(actions);
                    }
                    finally
                    {
                        Console.WriteLine("TOOL_ACCESS_STAGE|" + theme + "|restore-theme"); Console.Out.Flush();
                        root.RequestedTheme = previousTheme;
                        await SettleAsync(root);
                    }
                });
            }
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousOverride;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
            language.SetValue(null, previousLanguage);
        }
    }

    private static async Task TaskGoalHostThemeAndRemount()
    {
        RequireIsolatedData();
        Assert.Equal(ApplicationTheme.Dark, Application.Current.RequestedTheme);
        var language = LanguageField();
        var previousLanguage = language.GetValue(null);
        var previousOverride = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        try
        {
            language.SetValue(null, LocalizationService.ChineseLanguage);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
            {
                var previousTheme = root.RequestedTheme;
                try
                {
                    root.RequestedTheme = ElementTheme.Light;
                    var item = new ToolFlowItem { ItemId = "engine", Name = "Godot", Kind = "software", InstallTargetKey = "godot" };
                    var selection = new ToolFlowSelection
                    {
                        FlowId = "theme-demo", SubmissionId = "theme-submission", Origin = ToolFlowOrigin.User,
                        SelectedAtUtc = DateTimeOffset.UtcNow, ProjectGoal = "Synthetic game goal", GoalDescription = "Synthetic game goal",
                        FlowName = "Synthetic plan", FlowText = "Synthetic only", UploadEnabledAtSelection = false,
                        Items = [item], Conversation = [],
                    };
                    var view = new ToolFlowResumeView(selection, [new ToolFlowResumeItemRow
                        { ItemId = "engine", Name = "Godot", Kind = "software", State = ToolFlowResumeItemState.InstalledOrDetected,
                          StatusLine = "Synthetic evidence" }], [], 1, 0, 0, 0);
                    var guide = ToolFlowGoalGuide.Create(view, accessEntries: [Gui()]);
                    var task = new ToolFlowTaskControl { Width = 320, RequestedTheme = ElementTheme.Default };
                    var goal = new ToolFlowGoalControl { Width = 320, RequestedTheme = ElementTheme.Default };
                    task.Update(guide, selection.FlowName);
                    task.ToolAccess.Update([Gui()], busy: false);
                    goal.Update(guide);
                    var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                    host.Children.Add(task);
                    host.Children.Add(goal);
                    root.Children.Add(host);
                    await SettleAsync(root);
                    foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        root.RequestedTheme = theme; // No control recreation or localization refresh.
                        await SettleAsync(root);
                        Assert.Equal(theme, task.ActualTheme);
                        Assert.Equal(theme, goal.ActualTheme);
                        Assert.Equal(theme, task.ToolAccess.ActualTheme);
                        AssertTaskGoalPalette(task, goal, theme);
                    }
                    // A cached task is reattached under a different ancestor, then
                    // reopens under Light while the Application remains Dark.
                    root.Children.Clear();
                    await SettleAsync(root);
                    root.RequestedTheme = ElementTheme.Dark;
                    root.Children.Add(host);
                    await SettleAsync(root);
                    AssertTaskGoalPalette(task, goal, ElementTheme.Dark);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    root.RequestedTheme = ElementTheme.Light;
                    await SettleAsync(root);
                    AssertTaskGoalPalette(task, goal, ElementTheme.Light);
                    if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                    {
                        await GoalGuideCases.SaveControlPreviewAsync(task, "task-host-light-app-dark-remount.png");
                        await GoalGuideCases.SaveControlPreviewAsync(goal, "goal-host-light-app-dark-remount.png");
                    }
                }
                finally { root.RequestedTheme = previousTheme; }
            });
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousOverride;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
            language.SetValue(null, previousLanguage);
        }
    }

    private static void AssertTaskGoalPalette(ToolFlowTaskControl task, ToolFlowGoalControl goal, ElementTheme theme)
    {
        var themeKey = theme == ElementTheme.Dark ? "Dark" : "Light";
        var surface = Assert.IsType<Border>(Assert.Single(task.Children));
        Console.WriteLine($"TOOL_ACCESS_HOST_PALETTE|expected={theme}|actual={task.ActualTheme}|loaded={task.IsLoaded}|task={ToolFlowPaletteAssertions.Solid(surface.Background).Color}|text={ToolFlowPaletteAssertions.Solid(Field<TextBlock>(task, "_goal").Foreground).Color}");
        Console.Out.Flush();
        ToolFlowPaletteAssertions.Readable(surface.Background, Field<TextBlock>(task, "_goal").Foreground,
            Field<TextBlock>(task, "_summary").Foreground, themeKey);
        var primary = Field<Button>(task, "_handoff");
        ToolFlowPaletteAssertions.ContrastAtLeast(primary.Background, primary.Foreground, 4.5);
        Assert.Equal(ToolFlowPaletteAssertions.Solid(ThemeResourceResolver.ResolveBrush(task, ToolFlowThemeResources.Accent)).Color,
            ToolFlowPaletteAssertions.Solid(primary.Background).Color);
        var scroll = goal.Children.OfType<ScrollViewer>().Single();
        var current = Assert.IsType<StackPanel>(scroll.Content).Children.OfType<Border>().Single();
        ToolFlowPaletteAssertions.Readable(current.Background, Field<TextBlock>(goal, "_title").Foreground,
            Field<TextBlock>(goal, "_stage").Foreground, themeKey);
        var goalPrimary = Field<Button>(goal, "_primary");
        ToolFlowPaletteAssertions.ContrastAtLeast(goalPrimary.Background, goalPrimary.Foreground, 4.5);
        var row = Assert.IsType<StackPanel>(Assert.Single(Field<StackPanel>(task.ToolAccess, "_rows").Children));
        var name = Assert.IsType<TextBlock>(row.Children[0]);
        var hint = Assert.IsType<TextBlock>(row.Children[1]);
        var toolsScroll = Assert.IsType<ScrollViewer>(Field<Expander>(task.ToolAccess, "_expander").Content);
        Assert.True(name.ActualHeight > 0);
        Assert.True(hint.ActualHeight > 0);
        Assert.InRange(toolsScroll.VerticalOffset, 0, .1);
        Assert.InRange(name.TransformToVisual(toolsScroll).TransformPoint(new Windows.Foundation.Point()).Y,
            0, toolsScroll.ViewportHeight - name.ActualHeight);
        ToolFlowPaletteAssertions.ContrastAtLeast(surface.Background, name.Foreground, 4.5);
        ToolFlowPaletteAssertions.ContrastAtLeast(surface.Background, hint.Foreground, 4.5);
    }

    private static ToolFlowToolAccessEntry Gui() => new("godot", "Godot", @"C:\synthetic\godot.exe", @"C:\synthetic", true);
    private static IEnumerable<Button> Buttons(ToolFlowToolAccessControl control) => Field<StackPanel>(control, "_rows")
        .Children.Cast<StackPanel>().SelectMany(x => x.Children.OfType<StackPanel>()).SelectMany(x => x.Children.OfType<Button>());
    private static IEnumerable<TextBlock> Texts(ToolFlowToolAccessControl control) => Field<StackPanel>(control, "_rows")
        .Children.Cast<StackPanel>().SelectMany(x => x.Children.OfType<TextBlock>());
    private static T Field<T>(object control, string name) where T : class =>
        control.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(control) as T
        ?? throw new InvalidOperationException("Delivery control field missing: " + name);
    private static FieldInfo LanguageField() => typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Localization fallback field missing.");
    private static void WithChinese(Action body)
    {
        var language = LanguageField();
        var previous = language.GetValue(null);
        try { language.SetValue(null, LocalizationService.ChineseLanguage); body(); }
        finally { language.SetValue(null, previous); }
    }
    private static async Task SettleAsync(Grid root) { await Task.Delay(120); root.UpdateLayout(); }
    private static string RequireIsolatedData()
    {
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(declared), "Delivery cases require a fresh ZXAI_DATA_ROOT.");
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
        return Path.GetFullPath(declared!);
    }
    private sealed class SyntheticFiles : IDisposable
    {
        internal string Directory { get; } = Path.Combine(RequireIsolatedData(), "tool-delivery-" + Guid.NewGuid().ToString("N"));
        internal SyntheticFiles() => System.IO.Directory.CreateDirectory(Directory);
        internal ToolFlowToolAccessEntry Entry(string key, string name, string executable)
        {
            var path = Path.Combine(Directory, executable);
            File.WriteAllText(path, "Synthetic file; never executable.");
            return new(key, name, path, Directory, false);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
