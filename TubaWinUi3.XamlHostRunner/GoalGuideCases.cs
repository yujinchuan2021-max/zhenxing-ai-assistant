using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;
using Xunit;
using Windows.Graphics.Imaging;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Native state and attached-template checks with synthetic workflow evidence.
/// Only the empty STA TestApp and its owned offscreen Window are used; these cases
/// never construct a product page, probe installations, save a plan, or invoke a product action.
/// </summary>
internal static class GoalGuideCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("GoalGuide_CurrentManualAndDeferredSections", () => { CurrentManualAndDeferredSections(); return Task.CompletedTask; }),
        ("GoalGuide_BusyAndLanguagePreserveCurrentStep", () => { BusyAndLanguagePreserveCurrentStep(); return Task.CompletedTask; }),
        ("GoalGuide_AdvanceKeepsSelfReportedEvidence", () => { AdvanceKeepsSelfReportedEvidence(); return Task.CompletedTask; }),
        ("GoalGuide_AutomaticFailureAndCompletionStates", () => { AutomaticFailureAndCompletionStates(); return Task.CompletedTask; }),
        ("GoalGuide_TaskSummarySharesCurrentStep", () => { TaskSummarySharesCurrentStep(); return Task.CompletedTask; }),
        ("GoalGuide_OptionalFocusAndReturn", OptionalFocusAndReturn),
        ("GoalGuide_ManualConfirmationRequiresEveryMember", ManualConfirmationRequiresEveryMember),
        ("GoalGuide_NarrowAttachedTemplates", NarrowAttachedTemplates),
        ("GoalGuide_PaletteMatchesPageSource", () => { PaletteMatchesPageSource(); return Task.CompletedTask; }),
        ("GoalGuide_RealLocalPalette_TwoThemes", RealLocalPalette),
    ];

    private static void PaletteMatchesPageSource()
    {
        RequireIsolatedData();
        var source = Environment.GetEnvironmentVariable("ZXAI_THEME_SOURCE_XAML");
        Assert.False(string.IsNullOrWhiteSpace(source), "Declare ZXAI_THEME_SOURCE_XAML for the page palette source contract.");
        Assert.True(Path.IsPathFullyQualified(source!) && File.Exists(source), "The declared page XAML source must exist.");
        var document = XDocument.Load(source!);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var control = new ToolFlowGoalControl();
        var chain = ThemeResourceResolver.BuildChain(control);
        (string Local, string Page)[] keys =
        [
            (ToolFlowThemeResources.Canvas, "AssistantCanvasBrush"),
            (ToolFlowThemeResources.Surface, "AssistantSoftFillBrush"),
            (ToolFlowThemeResources.Stroke, "AssistantSeparatorBrush"),
            (ToolFlowThemeResources.PrimaryText, "AssistantPrimaryTextBrush"),
            (ToolFlowThemeResources.SecondaryText, "AssistantSecondaryTextBrush"),
            (ToolFlowThemeResources.Accent, "AssistantAccentBrush"),
            (ToolFlowThemeResources.OnAccent, "AssistantAccentForegroundBrush"),
        ];
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var dictionary = Assert.Single(document.Descendants().Where(element =>
                element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(x + "Key") == theme));
            foreach (var (local, page) in keys)
            {
                var declaration = Assert.Single(dictionary.Elements().Where(element => (string?)element.Attribute(x + "Key") == page));
                var hex = ((string?)declaration.Attribute("Color") ?? "").TrimStart('#');
                Assert.True(hex.Length is 6 or 8, "The page palette must use an explicit RGB or ARGB color.");
                var expected = Convert.ToUInt32(hex, 16) | (hex.Length == 6 ? 0xFF000000u : 0u);
                var actual = ToolFlowPaletteAssertions.Solid(ThemeResourceResolver.ResolveBrush(chain, theme, local));
                Assert.Equal(expected, (uint)actual.Color.A << 24 | (uint)actual.Color.R << 16 |
                    (uint)actual.Color.G << 8 | actual.Color.B);
            }
        }
    }

    private static async Task RealLocalPalette()
    {
        RequireIsolatedData();
        var previousOverride = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        try
        {
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
            {
                var previousTheme = root.RequestedTheme;
                var previousBackground = root.Background;
                try
                {
                    var control = new ToolFlowGoalControl { Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top };
                    control.Update(ManualGuide());
                    var raised = 0;
                    control.ActionRequested += (_, _) => raised++;
                    var loaded = new TaskCompletionSource();
                    control.Loaded += (_, _) => loaded.TrySetResult();
                    root.Children.Clear();
                    root.Children.Add(control);
                    Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(6000)) == loaded.Task,
                        "The isolated palette control did not attach.");
                    foreach (var requested in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                    {
                        root.RequestedTheme = control.RequestedTheme = requested;
                        await SettleAsync(root);
                        Assert.Equal(requested, control.ActualTheme);
                        root.Background = ThemeResourceResolver.ResolveBrush(control, ToolFlowThemeResources.Canvas);
                        foreach (var section in Sections(control)) section.IsExpanded = true;
                        await SettleAsync(root);
                        Assert.All(Flatten(control).OfType<TextBlock>(), text => Assert.Equal(requested, text.ActualTheme));
                        AssertRealPalette(control, requested == ElementTheme.Dark ? "Dark" : "Light");
                        Assert.True(VisualTreeHelper.GetChildrenCount(Field<Button>(control, "_primary")) > 0);

                        // Newly created confirmation/detail labels must share the palette too.
                        var begin = typeof(ToolFlowGoalControl).GetMethod("BeginManualConfirmation", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        begin.Invoke(control, null);
                        await SettleAsync(root);
                        Assert.All(Flatten(control).OfType<TextBlock>(), text => Assert.Equal(requested, text.ActualTheme));
                        AssertRealPalette(control, requested == ElementTheme.Dark ? "Dark" : "Light");
                        Assert.Equal(0, raised); // Display updates do not confirm or open a service.
                        if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                            await SaveControlPreviewAsync(control, "goal-guide-320-" +
                                (requested == ElementTheme.Dark ? "dark" : "light") + "-local.png");
                    }
                }
                finally { root.RequestedTheme = previousTheme; root.Background = previousBackground; }
            });
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousOverride;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
        }
    }

    private static void AssertRealPalette(ToolFlowGoalControl control, string theme)
    {
        var current = Assert.IsType<Border>(Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(control.Children[1]).Content).Children[0]);
        var primary = Field<TextBlock>(control, "_goal");
        var secondary = Field<TextBlock>(control, "_stage");
        ToolFlowPaletteAssertions.Readable(control.Background, primary.Foreground, secondary.Foreground, theme);
        ToolFlowPaletteAssertions.Readable(current.Background, Field<TextBlock>(control, "_title").Foreground,
            Field<TextBlock>(control, "_feedback").Foreground, theme);
        foreach (var text in Flatten(control).OfType<TextBlock>())
        {
            Assert.Equal(1d, text.Opacity);
            // Collapsed native templates have not inherited the visible theme yet.
            // The real-palette case expands every section before these assertions.
            if (text.ActualTheme == control.ActualTheme)
                ToolFlowPaletteAssertions.ContrastAtLeast(current.Background, text.Foreground, 4.5);
        }
        var button = Field<Button>(control, "_primary");
        ToolFlowPaletteAssertions.ContrastAtLeast(button.Background, button.Foreground, 4.5);
        ToolFlowPaletteAssertions.Solid(current.BorderBrush);
    }

    private static void CurrentManualAndDeferredSections()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var control = new ToolFlowGoalControl();
        var actions = 0;
        control.ActionRequested += (_, _) => actions++;
        var guide = ManualGuide();
        control.Update(guide);

        Assert.Equal("account", control.CurrentStepId);
        Assert.Contains(guide.Goal, Field<TextBlock>(control, "_goal").Text);
        Assert.Contains(guide.Stage, Field<TextBlock>(control, "_stage").Text);
        Assert.Equal(guide.CurrentStep!.Title, Field<TextBlock>(control, "_title").Text);
        Assert.Contains(guide.CurrentStep.Hint, Field<TextBlock>(control, "_hint").Text);
        Assert.DoesNotContain("OPTIONAL", Field<TextBlock>(control, "_title").Text);
        Assert.DoesNotContain("READY", Field<TextBlock>(control, "_title").Text);

        var primary = Field<Button>(control, "_primary");
        var confirm = Field<Button>(control, "_confirm");
        Assert.Equal(Visibility.Visible, primary.Visibility);
        Assert.True(primary.IsEnabled);
        Assert.Equal(Visibility.Visible, confirm.Visibility);
        Assert.True(confirm.IsEnabled); // the current website step has a distinct self-report confirmation
        Assert.All(Sections(control), section => Assert.False(section.IsExpanded));
        Assert.Contains(guide.Goal, AutomationProperties.GetName(control));
        Assert.Equal(0, actions); // Showing a restored guide does not perform its action.
    }

    private static void BusyAndLanguagePreserveCurrentStep()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var language = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Localization fallback field missing.");
        var previous = language.GetValue(null);
        try
        {
            // In-memory language only: no Localizer initialization, preference write,
            // or broadcast to product services.
            language.SetValue(null, LocalizationService.ChineseLanguage);
            var guide = ManualGuide();
            var control = new ToolFlowGoalControl();
            var actions = 0;
            control.ActionRequested += (_, _) => actions++;
            control.Update(guide);
            var primary = Field<Button>(control, "_primary");
            var confirm = Field<Button>(control, "_confirm");
            var later = Field<Expander>(control, "_later");
            var ready = Field<Expander>(control, "_ready");
            var evidence = Field<Expander>(control, "_evidence");
            var chinesePrimary = primary.Content;
            var chineseEvidenceHeader = evidence.Header;
            var problems = Field<StackPanel>(control, "_problemChoices");
            // Seed local help state without invoking a workflow action or changing persisted evidence.
            PrivateField("_problemChoice").SetValue(control, "site");
            problems.Visibility = Visibility.Visible;
            control.ApplyLocalization();
            var chineseFeedback = Field<TextBlock>(control, "_feedback").Text;
            later.IsExpanded = true;
            evidence.IsExpanded = true;

            control.Update(guide, busy: true);
            Assert.False(primary.IsEnabled);
            Assert.False(confirm.IsEnabled);
            Assert.All(Flatten(control).OfType<Button>(), button => Assert.False(button.IsEnabled));
            language.SetValue(null, LocalizationService.EnglishLanguage);
            control.ApplyLocalization();

            Assert.Same(primary, Field<Button>(control, "_primary"));
            Assert.Same(confirm, Field<Button>(control, "_confirm"));
            Assert.Same(later, Field<Expander>(control, "_later"));
            Assert.Same(ready, Field<Expander>(control, "_ready"));
            Assert.Same(evidence, Field<Expander>(control, "_evidence"));
            Assert.Equal("account", control.CurrentStepId);
            Assert.True(later.IsExpanded);
            Assert.False(ready.IsExpanded);
            Assert.True(evidence.IsExpanded);
            Assert.False(primary.IsEnabled);
            Assert.False(confirm.IsEnabled);
            Assert.NotEqual(chinesePrimary, primary.Content);
            Assert.NotEqual(chineseEvidenceHeader, evidence.Header);
            Assert.Equal("site", PrivateField("_problemChoice").GetValue(control));
            Assert.Equal(Visibility.Visible, problems.Visibility);
            Assert.Equal(Visibility.Visible, Field<TextBlock>(control, "_feedback").Visibility);
            Assert.NotEqual(chineseFeedback, Field<TextBlock>(control, "_feedback").Text);

            control.Update(guide, busy: false);
            Assert.True(primary.IsEnabled);
            Assert.True(confirm.IsEnabled);
            Assert.True(later.IsExpanded);
            Assert.True(evidence.IsExpanded);
            Assert.Equal("account", control.CurrentStepId);
            Assert.Equal(0, actions); // Refreshing and unlocking do not confirm the user step.
        }
        finally { language.SetValue(null, previous); }
    }

    private static void AdvanceKeepsSelfReportedEvidence()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var initial = ManualGuide();
        var control = new ToolFlowGoalControl();
        var actions = 0;
        control.ActionRequested += (_, _) => actions++;
        control.Update(initial);
        PrivateField("_problemChoice").SetValue(control, "site");
        Field<StackPanel>(control, "_problemChoices").Visibility = Visibility.Visible;
        var primary = Field<Button>(control, "_primary");
        var confirmedRow = Row("account", "Account", ToolFlowResumeItemState.UserReportedDone,
            "USER REPORTED ONLY; installation, login and project completion were not verified.");
        var confirmed = Step("account", "Account", ToolFlowGoalStepState.UserConfirmed, [confirmedRow]);
        var next = Step("model-setup", "CURRENT MODEL SETUP", ToolFlowGoalStepState.NeedsUserAssist,
            [Row("model-setup", "Model setup", ToolFlowResumeItemState.NeedsUserAssist, "Configure the model in the external service.")]);
        var advanced = initial with
        {
            Stage = "Configure the model",
            CurrentStep = next,
            ReadySteps = [.. initial.ReadySteps, confirmed],
        };
        control.Update(advanced);

        Assert.Same(primary, Field<Button>(control, "_primary"));
        Assert.Equal("model-setup", control.CurrentStepId);
        Assert.Equal(next.Title, Field<TextBlock>(control, "_title").Text);
        Assert.Equal(Visibility.Visible, primary.Visibility);
        Assert.True(primary.IsEnabled);
        Assert.Equal(Visibility.Collapsed, Field<Button>(control, "_confirm").Visibility);
        Assert.Null(PrivateField("_problemChoice").GetValue(control));
        Assert.Equal(Visibility.Collapsed, Field<StackPanel>(control, "_problemChoices").Visibility);
        Assert.Contains(confirmedRow.StatusLine, string.Join("\n", Texts(Field<Expander>(control, "_evidence"))));
        Assert.Contains("Account", string.Join("\n", Texts(Field<Expander>(control, "_ready"))));
        Assert.Equal(0, actions); // Updating persisted self-report evidence never produces verification.
    }

    private static void AutomaticFailureAndCompletionStates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var automatic = Step("engine", "CURRENT ENGINE", ToolFlowGoalStepState.PendingAutomatic,
            [Row("engine", "Godot", ToolFlowResumeItemState.PendingAutomatic, "Engine is still missing.", automatic: true)], automatic: true);
        var guide = new ToolFlowGoalGuide("Create a small game", "Prepare the engine", "0 verified items",
            automatic, [], [], true);
        var control = new ToolFlowGoalControl();
        var actions = 0;
        control.ActionRequested += (_, _) => actions++;
        control.Update(guide);
        var primary = Field<Button>(control, "_primary");
        var confirm = Field<Button>(control, "_confirm");
        Assert.True(primary.IsEnabled);
        Assert.Equal(Visibility.Collapsed, confirm.Visibility);

        var failed = automatic with { State = ToolFlowGoalStepState.Failed };
        control.Update(guide with { CurrentStep = failed, Stage = "Check the unconfirmed engine" });
        Assert.True(primary.IsEnabled);
        Assert.Equal("engine", control.CurrentStepId);
        Assert.Equal(Visibility.Collapsed, confirm.Visibility);

        control.Update(guide with { CurrentStep = automatic with { State = ToolFlowGoalStepState.Running } }, busy: true);
        Assert.False(primary.IsEnabled);
        Assert.False(confirm.IsEnabled);

        var completed = guide with
        {
            CurrentStep = null, Stage = "Review the use steps", HasPendingAutomatic = false,
            ReadySteps = [automatic with { State = ToolFlowGoalStepState.Ready }],
        };
        control.Update(completed);
        Assert.Null(control.CurrentStepId);
        Assert.Equal(Visibility.Visible, primary.Visibility);
        Assert.True(primary.IsEnabled); // readiness still offers the next use/handoff steps
        Assert.Equal(Visibility.Collapsed, confirm.Visibility);
        Assert.Equal(0, actions);
    }

    private static async Task NarrowAttachedTemplates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        try
        {
            await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
            {
                // A stock control baseline distinguishes missing WinUI resources
                // from a composition regression in the goal control itself.
                var stock = new Expander { Header = "Native baseline", Content = new TextBlock { Text = "Fake evidence" }, IsExpanded = true };
                root.Children.Add(stock);
                await SettleAsync(root);
                Assert.True(stock.ActualHeight > 0);
                Assert.True(VisualTreeHelper.GetChildrenCount(stock) > 0);
                root.Children.Clear();

                var guide = ManualGuide();
                var control = new ToolFlowGoalControl { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
                var actions = 0;
                control.ActionRequested += (_, _) => actions++;
                control.Update(guide); // product restore populates before attaching the control
                var loaded = new TaskCompletionSource();
                control.Loaded += (_, _) => loaded.TrySetResult();
                root.Children.Add(new ContentControl { Content = control, HorizontalContentAlignment = HorizontalAlignment.Left });
                Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(6000)) == loaded.Task, "Goal guide did not attach to the native Window.");
                await SettleAsync(root);

                Assert.True(control.IsLoaded);
                Assert.InRange(control.ActualWidth, 319, 321);
                Assert.True(control.ActualHeight > 0);
                var primary = Field<Button>(control, "_primary");
                Assert.True(primary.ActualHeight > 0);
                Assert.True(VisualTreeHelper.GetChildrenCount(primary) > 0);
                Assert.InRange(primary.ActualWidth, 1, control.ActualWidth);
                Assert.All(Sections(control), section =>
                {
                    Assert.False(section.IsExpanded);
                    Assert.True(section.ActualHeight > 0);
                    Assert.True(VisualTreeHelper.GetChildrenCount(section) > 0);
                });
                if (string.Equals(Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW"), "1", StringComparison.Ordinal))
                    await SaveStockThemePreviewsAsync(control, root, review: false);

                await InvokeAsync(Field<Button>(control, "_confirm"));
                await SettleAsync(root);
                var confirmation = Field<StackPanel>(control, "_confirmation");
                Assert.Equal(Visibility.Visible, confirmation.Visibility);
                var checkbox = Assert.Single(Flatten(confirmation).OfType<CheckBox>());
                Assert.True(checkbox.ActualHeight > 0);
                Assert.True(VisualTreeHelper.GetChildrenCount(checkbox) > 0);
                Assert.InRange(checkbox.ActualWidth, 1, control.ActualWidth);
                Assert.False(Field<Button>(control, "_saveConfirmation").IsEnabled);
                if (string.Equals(Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW"), "1", StringComparison.Ordinal))
                    await SaveStockThemePreviewsAsync(control, root, review: true);

                var evidence = Field<Expander>(control, "_evidence");
                evidence.IsExpanded = true;
                await SettleAsync(root);
                Assert.True(evidence.IsExpanded);
                control.Update(guide, busy: true);
                control.ApplyLocalization();
                await SettleAsync(root);
                Assert.True(evidence.IsExpanded);
                Assert.Equal("account", control.CurrentStepId);
                Assert.False(primary.IsEnabled);
                Assert.All(Flatten(control).OfType<Button>(), button => Assert.False(button.IsEnabled));
                Assert.All(Flatten(confirmation).OfType<CheckBox>(), check => Assert.False(check.IsEnabled));
                Assert.Equal(0, actions); // native template attachment and expansion execute nothing
            });
        }
        finally { Application.Current.Resources.MergedDictionaries.Remove(resources); }
    }

    private static async Task OptionalFocusAndReturn()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var language = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Localization fallback field missing.");
        var previous = language.GetValue(null);
        try
        {
            language.SetValue(null, LocalizationService.ChineseLanguage);
            var guide = ManualGuide();
            var control = new ToolFlowGoalControl { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            var actions = 0;
            control.ActionRequested += (_, _) => actions++;
            control.Update(guide);
            await WithGoalWindowAsync(control, async root =>
            {
                var later = Field<Expander>(control, "_later");
                later.IsExpanded = true;
                await SettleAsync(root);
                var optional = Assert.Single(Flatten(later).OfType<Button>());
                await InvokeAsync(optional);
                await SettleAsync(root);
                Assert.Equal("optional-editor", control.CurrentStepId);
                Assert.Contains(guide.LaterSteps[0].Title, Field<TextBlock>(control, "_title").Text);
                Assert.Contains(guide.Goal, Field<TextBlock>(control, "_goal").Text);
                Assert.Equal(0, actions); // choosing optional work changes local focus only
                var back = Field<Button>(control, "_return");
                Assert.Equal(Visibility.Visible, back.Visibility);
                Assert.True(back.IsEnabled);
                var chineseBack = back.Content;

                control.Update(guide, busy: true);
                language.SetValue(null, LocalizationService.EnglishLanguage);
                control.ApplyLocalization();
                Assert.Equal("optional-editor", control.CurrentStepId);
                Assert.True(later.IsExpanded);
                Assert.False(back.IsEnabled);
                Assert.False(Field<Button>(control, "_primary").IsEnabled);
                Assert.All(Flatten(control).OfType<Button>(), button => Assert.False(button.IsEnabled));
                Assert.NotEqual(chineseBack, back.Content);
                Assert.Equal(0, actions);

                control.Update(guide);
                await InvokeAsync(back);
                Assert.Equal("account", control.CurrentStepId);
                Assert.Equal(guide.CurrentStep!.Title, Field<TextBlock>(control, "_title").Text);
                Assert.Equal(Visibility.Collapsed, back.Visibility);
                Assert.Equal(0, actions);

                optional = Assert.Single(Flatten(later).OfType<Button>());
                await InvokeAsync(optional);
                Assert.Equal("optional-editor", control.CurrentStepId);
                control.Update(guide with { LaterSteps = [] });
                Assert.Equal("account", control.CurrentStepId); // removing an optional row restores the required current step
                Assert.Equal(Visibility.Collapsed, back.Visibility);
                Assert.Equal(0, actions);
            });
        }
        finally { language.SetValue(null, previous); }
    }

    private static async Task ManualConfirmationRequiresEveryMember()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var language = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Localization fallback field missing.");
        var previous = language.GetValue(null);
        try
        {
            language.SetValue(null, LocalizationService.ChineseLanguage);
            var account = Row("account", "Service account", ToolFlowResumeItemState.NeedsUserAssist, "Still needs account access")
                with { ManualHint = "FULL ACCOUNT HINT: Open the selected service, sign in, and verify that this account can access the requested feature.\nKeep this second line visible for review." };
            var permission = Row("permission", "Service permissions", ToolFlowResumeItemState.Failed, "Access has not been confirmed")
                with { ManualHint = "FULL PERMISSION HINT: Check the required access and subscription terms before deciding whether this workflow can be used." };
            var ready = Row("ready", "Already available engine", ToolFlowResumeItemState.InstalledOrDetected, "Detected earlier");
            var step = Step("service-access", "CURRENT SERVICE ACCESS", ToolFlowGoalStepState.NeedsUserAssist,
                [account, permission, ready], source: "https://example.test/service");
            var guide = new ToolFlowGoalGuide("Make a short demo", "Review service access", "Two user tasks", step, [], [], false);
            var control = new ToolFlowGoalControl { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            var actions = new List<(string Id, ToolFlowGoalAction Action)>();
            control.ActionRequested += (requested, action) =>
            {
                actions.Add((requested.Id, action));
                control.Update(guide, busy: true); // emulate the page's immediate in-flight lock, using no product page
            };
            control.Update(guide);
            await WithGoalWindowAsync(control, async root =>
            {
                RequestFakeConfirmation(control, step);
                Assert.Empty(actions); // even a direct request cannot skip the confirmation review
                await InvokeAsync(Field<Button>(control, "_confirm"));
                await SettleAsync(root);
                Assert.Empty(actions);
                var confirmation = Field<StackPanel>(control, "_confirmation");
                var save = Field<Button>(control, "_saveConfirmation");
                Assert.Equal(Visibility.Visible, confirmation.Visibility);
                var checks = Flatten(confirmation).OfType<CheckBox>().ToArray();
                Assert.Equal(2, checks.Length); // satisfied evidence is not requested again
                Assert.All(checks, check => Assert.False(check.IsChecked == true));
                Assert.Contains(account.ManualHint, string.Join("\n", Texts(confirmation)));
                Assert.Contains(permission.ManualHint, string.Join("\n", Texts(confirmation)));
                Assert.False(save.IsEnabled);

                checks[0].IsChecked = true;
                Assert.False(save.IsEnabled);
                RequestFakeConfirmation(control, step);
                Assert.Empty(actions); // one checked member is insufficient for the grouped step
                control.Update(guide, busy: true);
                language.SetValue(null, LocalizationService.EnglishLanguage);
                control.ApplyLocalization();
                checks = Flatten(confirmation).OfType<CheckBox>().ToArray();
                Assert.True(checks[0].IsChecked == true);
                Assert.False(checks[1].IsChecked == true);
                Assert.All(checks, check => Assert.False(check.IsEnabled));
                Assert.False(save.IsEnabled);
                Assert.Equal("service-access", control.CurrentStepId);
                Assert.Equal(Visibility.Visible, confirmation.Visibility);
                Assert.Empty(actions);

                control.Update(guide);
                checks = Flatten(confirmation).OfType<CheckBox>().ToArray();
                Assert.True(checks[0].IsChecked == true);
                checks[1].IsChecked = true;
                Assert.True(save.IsEnabled);
                await InvokeAsync(save);
                Assert.Equal(("service-access", ToolFlowGoalAction.ConfirmManual), Assert.Single(actions));
                Assert.False(save.IsEnabled);
                RequestFakeConfirmation(control, step);
                Assert.Single(actions); // a second request is blocked during the in-flight lock

                var next = Step("next", "NEXT REQUIRED STEP", ToolFlowGoalStepState.NeedsUserAssist,
                    [Row("next", "Next configuration", ToolFlowResumeItemState.NeedsUserAssist, "Still needs configuration")]);
                control.Update(guide with { CurrentStep = next });
                Assert.Equal("next", control.CurrentStepId);
                Assert.Equal(Visibility.Collapsed, confirmation.Visibility);
                Assert.Empty(Field<HashSet<string>>(control, "_checkedManualIds"));
                Assert.False(save.IsEnabled);
                await InvokeAsync(Field<Button>(control, "_primary"));
                Assert.Equal(Visibility.Visible, confirmation.Visibility); // a manual step without a link uses the same review
                Assert.False(Assert.Single(Flatten(confirmation).OfType<CheckBox>()).IsChecked == true);
                Assert.False(save.IsEnabled);
                Assert.Single(actions);
            });
        }
        finally { language.SetValue(null, previous); }
    }

    private static async Task WithGoalWindowAsync(ToolFlowGoalControl control, Func<Grid, Task> verify)
    {
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        try
        {
            await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
            {
                var loaded = new TaskCompletionSource();
                control.Loaded += (_, _) => loaded.TrySetResult();
                root.Children.Add(new ContentControl { Content = control, HorizontalContentAlignment = HorizontalAlignment.Left });
                Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(6000)) == loaded.Task, "Goal guide did not attach to the native Window.");
                await SettleAsync(root);
                await verify(root);
            });
        }
        finally { Application.Current.Resources.MergedDictionaries.Remove(resources); }
    }

    private static async Task InvokeAsync(Button button)
    {
        Assert.True(button.IsEnabled, "The synthetic native button must be enabled before invoking it.");
        var provider = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
        provider.Invoke();
        await Task.Yield();
        await Task.Delay(40);
    }

    private static void RequestFakeConfirmation(ToolFlowGoalControl control, ToolFlowGoalStep step)
    {
        var request = typeof(ToolFlowGoalControl).GetMethod("Request", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Goal control request guard missing.");
        request.Invoke(control, [step, ToolFlowGoalAction.ConfirmManual]);
    }

    private static async Task SaveStockThemePreviewsAsync(ToolFlowGoalControl control, Grid root, bool review)
    {
        var previousThemeOverride = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var previousRootTheme = root.RequestedTheme;
        var previousControlTheme = control.RequestedTheme;
        var previousRootBackground = root.Background;
        var previousControlBackground = control.Background;
        var scope = Field<ThemeRefreshScope>(control, "_theme");
        try
        {
            // The assertion palette intentionally uses distinguishable test colors.
            // Preview the local palette plus native templates, without product App
            // resources or fake test dictionaries. The root background is a test canvas.
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            foreach (var requested in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = requested;
                control.RequestedTheme = requested;
                await SettleAsync(root);
                var background = ThemeResourceResolver.ResolveBrush(control, ToolFlowThemeResources.Canvas)
                    ?? throw new InvalidOperationException("The local test-canvas background could not be resolved.");
                root.Background = background;
                control.Background = background; // RenderTargetBitmap captures this subtree, including its own background.
                scope.RefreshAll();
                await SettleAsync(root);
                AssertRealPalette(control, requested == ElementTheme.Dark ? "Dark" : "Light");
                var themeName = requested == ElementTheme.Dark ? "dark" : "light";
                var fileName = "goal-guide-320-" + themeName + (review ? "-review" : "") + ".png";
                await SaveControlPreviewAsync(control, fileName);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("GOAL_PREVIEW_UNAVAILABLE|" + ex.GetType().Name + "|" + ex.Message.Replace("\r", " ").Replace("\n", " "));
            Console.Out.Flush();
        }
        finally
        {
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousThemeOverride;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
            root.RequestedTheme = previousRootTheme;
            control.RequestedTheme = previousControlTheme;
            root.Background = previousRootBackground;
            control.Background = previousControlBackground;
            scope.RefreshAll();
            await SettleAsync(root);
            scope.RefreshAll();
        }
    }

    internal static async Task SaveControlPreviewAsync(FrameworkElement control, string fileName)
    {
        try
        {
            // Render only this synthetic XAML subtree. No desktop, product page,
            // browser, user configuration, or other window is captured.
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(control);
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                throw new InvalidOperationException("The offscreen control produced no preview pixels.");
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            buffer.CopyTo(pixels);
            var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")!;
            var path = Path.Combine(Path.GetFullPath(declared), fileName);
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            Console.WriteLine("GOAL_PREVIEW|" + path);
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            // Preview is additional evidence. A platform limitation must not
            // weaken or replace the native state/template assertions above.
            Console.WriteLine("GOAL_PREVIEW_UNAVAILABLE|" + ex.GetType().Name + "|" + ex.Message.Replace("\r", " ").Replace("\n", " "));
            Console.Out.Flush();
        }
    }

    private static void TaskSummarySharesCurrentStep()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var guide = ManualGuide();
        var task = new ToolFlowTaskControl();
        var actions = 0;
        task.OpenDetails += () => actions++;
        task.OpenHandoff += () => actions++;
        task.StopRequested += () => actions++;
        task.Update(guide, "Saved preparation plan");
        var details = TaskField<Button>(task, "_details");
        var handoff = TaskField<Button>(task, "_handoff");
        var overview = TaskField<Button>(task, "_overview");
        Assert.Contains(guide.Goal, TaskField<TextBlock>(task, "_goal").Text);
        Assert.Contains(guide.Stage, TaskField<TextBlock>(task, "_summary").Text);
        Assert.Contains(guide.CurrentStep!.Title, TaskField<TextBlock>(task, "_next").Text);
        Assert.Equal(Visibility.Visible, details.Visibility);
        Assert.True(details.IsEnabled);
        Assert.Equal(Visibility.Collapsed, handoff.Visibility);
        // The workbench opens this step's source directly. Its separate review
        // action is still available to record manual confirmation explicitly.
        Assert.Equal(Visibility.Visible, overview.Visibility);
        Assert.True(overview.IsEnabled);
        Assert.NotEqual(details.Content, overview.Content);

        task.Update(guide, "Saved preparation plan", busy: true, resumed: true);
        Assert.False(details.IsEnabled);
        Assert.False(overview.IsEnabled);
        Assert.Same(details, TaskField<Button>(task, "_details"));
        Assert.Contains(guide.Goal, TaskField<TextBlock>(task, "_goal").Text);
        Assert.Equal(0, actions); // Restoring the top summary neither opens details nor executes the step.
    }

    private static ToolFlowGoalGuide ManualGuide()
    {
        var current = Step("account", "CURRENT ACCOUNT STEP", ToolFlowGoalStepState.NeedsUserAssist,
            [Row("account", "Account", ToolFlowResumeItemState.NeedsUserAssist, "Open the service and confirm account access.")],
            source: "https://example.test/account");
        var later = Step("optional-editor", "LATER OPTIONAL EDITOR", ToolFlowGoalStepState.NeedsUserAssist,
            [Row("optional-editor", "Optional editor", ToolFlowResumeItemState.NeedsUserAssist, "Optional, after the first playable game.")], optional: true);
        var ready = Step("engine", "READY ENGINE", ToolFlowGoalStepState.Ready,
            [Row("engine", "Godot", ToolFlowResumeItemState.InstalledOrDetected, "Detected engine; no additional installation needed.")]);
        return new("Create my first playable 2D game", "Confirm service access", "1 detected tool; 1 current step",
            current, [later], [ready], false);
    }

    private static ToolFlowGoalStep Step(string id, string title, ToolFlowGoalStepState state,
        IReadOnlyList<ToolFlowResumeItemRow> rows, string? source = null, bool automatic = false, bool optional = false)
        => new(id, title, "Follow the current step's instructions only.", rows, "Complete the current step", source, state,
            automatic, state == ToolFlowGoalStepState.NeedsUserAssist, optional);

    private static ToolFlowResumeItemRow Row(string id, string name, ToolFlowResumeItemState state, string status, bool automatic = false)
        => new() { ItemId = id, Name = name, Kind = "service", State = state, StatusLine = status, CanContinueAutomatically = automatic };

    private static Expander[] Sections(ToolFlowGoalControl control) =>
        [Field<Expander>(control, "_later"), Field<Expander>(control, "_ready"), Field<Expander>(control, "_evidence")];

    private static T Field<T>(ToolFlowGoalControl control, string name) where T : class
        => PrivateField(name).GetValue(control) as T
            ?? throw new InvalidOperationException("Goal control field missing: " + name);

    private static FieldInfo PrivateField(string name)
        => typeof(ToolFlowGoalControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Goal control field missing: " + name);

    private static T TaskField<T>(ToolFlowTaskControl control, string name) where T : class
        => typeof(ToolFlowTaskControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(control) as T
            ?? throw new InvalidOperationException("Task control field missing: " + name);

    private static string[] Texts(DependencyObject root) => Flatten(root).OfType<TextBlock>().Select(text => text.Text).ToArray();

    private static IReadOnlyList<DependencyObject> Flatten(DependencyObject root)
    {
        var nodes = new List<DependencyObject>();
        void Walk(DependencyObject node)
        {
            nodes.Add(node);
            switch (node)
            {
                case Panel panel: foreach (var child in panel.Children) Walk(child); break;
                case Border border when border.Child is { } child: Walk(child); break;
                case ScrollViewer scroller when scroller.Content is DependencyObject scrolledContent: Walk(scrolledContent); break;
                case Expander expander:
                    if (expander.Header is DependencyObject header) Walk(header);
                    if (expander.Content is DependencyObject content) Walk(content);
                    break;
                case ContentControl contentControl when contentControl.Content is DependencyObject childContent: Walk(childContent); break;
            }
        }
        Walk(root);
        return nodes;
    }

    private static async Task SettleAsync(Grid root)
    {
        await Task.Delay(150);
        root.UpdateLayout();
    }

    private static void RequireIsolatedData()
    {
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(declared), "Goal-guide cases require a fresh ZXAI_DATA_ROOT.");
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
    }
}

/// <summary>Checks assigned real brushes by luminance and contrast, independently of palette constants.</summary>
internal static class ToolFlowPaletteAssertions
{
    internal static SolidColorBrush Solid(Brush? brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal((byte)255, solid.Color.A);
        Assert.Equal(1d, solid.Opacity);
        return solid;
    }

    internal static void Readable(Brush? surface, Brush? primary, Brush? secondary, string theme)
    {
        var background = Luminance(Solid(surface));
        var foreground = Luminance(Solid(primary));
        if (theme == "Light")
        {
            Assert.True(background > .7, "Light mode must have a light card/canvas.");
            Assert.True(foreground < .1, "Light mode must have dark primary text.");
        }
        else
        {
            Assert.True(background < .1, "Dark mode must have a dark card/canvas.");
            Assert.True(foreground > .7, "Dark mode must have light primary text.");
        }
        ContrastAtLeast(surface, primary, 4.5);
        ContrastAtLeast(surface, secondary, 4.5);
    }

    internal static void ContrastAtLeast(Brush? surface, Brush? foreground, double minimum)
    {
        var a = Luminance(Solid(surface));
        var b = Luminance(Solid(foreground));
        var contrast = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Assert.True(contrast >= minimum, $"Text contrast {contrast:F2}:1 is below {minimum}:1.");
    }

    private static double Luminance(SolidColorBrush brush)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        var color = brush.Color;
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
