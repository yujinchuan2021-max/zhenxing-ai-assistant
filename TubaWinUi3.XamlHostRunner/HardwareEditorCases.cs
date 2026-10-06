using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Real compiled editor page with synthetic devices only. No hardware enumeration,
/// profile save, registry write, restore, driver or device restart is performed.
/// Requires only HardwareSpooferPage, ToolPageHeader and FluentTokens XBF payloads.
/// Functional assertions and the native host's process exit are separate evidence.
/// </summary>
internal static class HardwareEditorCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("HardwareEditor_SixCategoriesAndIndependentDrafts_Offline", IndependentDrafts),
        ("HardwareEditor_CatalogBrowseAndAutofill_Offline", CatalogAutofill),
        ("HardwareEditor_CategoryTilesResponsiveLayout_Offline", ResponsiveTiles),
        ("HardwareEditor_NativeThemeInteractionAndPreview_Offline", ThemeInteraction),
    ];

    private static async Task IndependentDrafts()
    {
        RequireResources();
        await WithPage(async (page, host, root) =>
        {
            var snapshot = Snapshot();
            var editors = Editors(page);
            Assert.Equal(Enum.GetValues<HardwareModelCategory>(), editors.Keys.ToArray());
            Assert.Equal(Enum.GetValues<HardwareModelCategory>(), CategoryButtons(page).Keys.ToArray());
            Assert.Equal(snapshot.Devices.Count, Preview(page).Children.Count);
            AssertFixtureActionsDisabled(page);
            foreach (var category in Enum.GetValues<HardwareModelCategory>())
            {
                Console.WriteLine("HARDWARE_EDITOR_DRAFT|category=" + category);
                await SelectCategory(page, category, root);
                var view = View(Editors(page)[category]);
                var devices = snapshot.Devices.Where(device => device.Category == category).ToArray();
                Assert.Equal(2, view.Picker.Items.Count);
                Assert.Equal(devices[0].CurrentName, view.Name.Text);
                Assert.Equal(devices[0].CurrentManufacturer, view.Manufacturer.Text);
                AssertOriginal(view, devices[0].OriginalName);

                Edit(view, "Draft A " + category, "Draft manufacturer A " + category);
                await AssertPreviewEventually(page, "Draft A " + category, devices[0].OriginalName, root);
                view.Picker.SelectedIndex = 1;
                await Settle(root, 30);
                Assert.Equal(devices[1].CurrentName, view.Name.Text);
                Assert.Equal(devices[1].CurrentManufacturer, view.Manufacturer.Text);
                AssertOriginal(view, devices[1].OriginalName);
                Edit(view, "Draft B " + category, "Draft manufacturer B " + category);
                await AssertPreviewEventually(page, "Draft B " + category, devices[1].OriginalName, root);

                view.Picker.SelectedIndex = 0;
                await Settle(root, 30);
                Assert.Equal("Draft A " + category, view.Name.Text);
                Assert.Equal("Draft manufacturer A " + category, view.Manufacturer.Text);
                AssertOriginal(view, devices[0].OriginalName);
                // Invoke the actual reset button; only the selected device resets.
                await Invoke(view.Reset);
                Assert.Equal(devices[0].OriginalName, view.Name.Text);
                Assert.Equal(devices[0].OriginalManufacturer, view.Manufacturer.Text);
                view.Picker.SelectedIndex = 1;
                await Settle(root, 30);
                Assert.Equal("Draft B " + category, view.Name.Text);
                Assert.Equal("Draft manufacturer B " + category, view.Manufacturer.Text);
                AssertPreview(page, "Draft B " + category, devices[1].OriginalName);
            }
            AssertFixtureActionsDisabled(page);
            Assert.Equal(12, Preview(page).Children.Count);
            // Rebuilding localized labels must preserve drafts and device identities.
            page.ApplyLocalization();
            await Settle(root);
            foreach (var category in Enum.GetValues<HardwareModelCategory>())
            {
                await SelectCategory(page, category, root);
                var view = View(Editors(page)[category]);
                view.Picker.SelectedIndex = 1;
                Assert.Equal("Draft B " + category, view.Name.Text);
            }
            AssertNoHardwareFiles();
            Console.WriteLine("HARDWARE_EDITOR|categories=6|synthetic-devices=12|drafts=independent|reset=selected-only|system-actions=disabled");
        });
        // A missing-device marker is detection text, never a target model.
        var placeholders = Enum.GetValues<HardwareModelCategory>().Select(category => new EditorDevice(category,
            "local/" + category, "未检测到设备", "", "", "", "Local synthetic placeholder", false)).ToArray();
        await WithPage(async (page, host, root) =>
        {
            foreach (var category in Enum.GetValues<HardwareModelCategory>())
            {
                await SelectCategory(page, category, root);
                var view = View(Editors(page)[category]);
                Assert.Equal("", view.Name.Text);
                Assert.Equal("", view.Manufacturer.Text);
                page.ChooseModelForFixture(category, HardwareModelCatalog.ForCategory(category)[0]);
                await Settle(root, 30);
                Assert.NotEmpty(view.Name.Text);
                Assert.NotEmpty(view.Manufacturer.Text);
                await Invoke(view.Reset);
                await Settle(root, 30);
                Assert.Equal("", view.Name.Text);
                Assert.Equal("", view.Manufacturer.Text);
                Assert.StartsWith("0", Element<TextBlock>(page, "DraftStatusText").Text);
            }
            AssertFixtureActionsDisabled(page);
            AssertNoHardwareFiles();
            Console.WriteLine("HARDWARE_EDITOR|local-placeholders=6|preset-then-undo=empty-target|draft-count=0|persistence=none");
        }, new(placeholders, Array.Empty<string>()));
    }

    private static async Task CatalogAutofill()
    {
        RequireResources();
        await WithPage(async (page, host, root) =>
        {
            foreach (var category in Enum.GetValues<HardwareModelCategory>())
            {
                await SelectCategory(page, category, root);
                var view = View(Editors(page)[category]);
                view.Picker.SelectedIndex = 1;
                view.Name.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await Settle(root);
                // A real native button invocation opens the real AutoSuggestBox.
                await Invoke(view.Browse);
                for (var attempt = 0; !view.Name.IsSuggestionListOpen && attempt < 20; ++attempt) await Settle(root, 50);
                Assert.True(view.Name.IsSuggestionListOpen, $"The native model browser did not open for {category}; loaded={view.Name.IsLoaded}; focus={view.Name.FocusState}");
                var suggestions = Assert.IsAssignableFrom<IEnumerable<HardwareModelPreset>>(view.Name.ItemsSource).ToArray();
                Assert.NotEmpty(suggestions);
                Assert.All(suggestions, preset => Assert.Equal(category, preset.Category));
                Assert.Equal(HardwareModelCatalog.ForCategory(category).Count, suggestions.Length);

                // This fixture-only entry invokes the exact Choose callback also
                // wired to SuggestionChosen and QuerySubmitted, without forged
                // WinRT event args or dispatching input to another application.
                var preset = suggestions[0];
                page.ChooseModelForFixture(category, preset);
                view.Name.IsSuggestionListOpen = false;
                await Settle(root, 30);
                Assert.Equal(preset.Name, view.Name.Text);
                Assert.Equal(preset.Manufacturer, view.Manufacturer.Text);
                AssertPreview(page, preset.Name, Snapshot().Devices.Single(device => device.Category == category && device.Id.EndsWith(":b", StringComparison.Ordinal)).OriginalName);
                view.Picker.SelectedIndex = 0;
                Assert.Equal(Snapshot().Devices.Single(device => device.Category == category && device.Id.EndsWith(":a", StringComparison.Ordinal)).CurrentName, view.Name.Text);
                view.Picker.SelectedIndex = 1;
                Assert.Equal(preset.Name, view.Name.Text);
                Assert.Equal(preset.Manufacturer, view.Manufacturer.Text);
            }
            AssertFixtureActionsDisabled(page);
            AssertNoHardwareFiles();
            Console.WriteLine("HARDWARE_EDITOR|preset-browse=native-invoke|preset-selection=shared-production-callback|manufacturer=autofilled|categories=6");
        });
    }

    private static async Task ThemeInteraction()
    {
        RequireResources();
        await WithPage(async (page, host, root) =>
        {
            var pageScroll = LogicalNodes(page).OfType<ScrollViewer>().Single();
            foreach (var theme in new[] { OppositeAppTheme, AppTheme, OppositeAppTheme })
            {
                await SelectCategory(page, HardwareModelCategory.Cpu, root);
                root.RequestedTheme = theme;
                host.RequestedTheme = theme;
                page.RequestedTheme = theme;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Settle(root, 320);
                // Native offscreen layout may defer its render walk. Ask the
                // platform to render the subtree before observing its theme.
                await new RenderTargetBitmap().RenderAsync(host);
                await Settle(root, 100);
                Console.WriteLine($"HARDWARE_EDITOR_THEME|expected={theme}|app={Application.Current.RequestedTheme}|root={root.RequestedTheme}/{root.ActualTheme}|host={host.RequestedTheme}/{host.ActualTheme}|page={page.RequestedTheme}/{page.ActualTheme}");
                Assert.Equal(theme, page.ActualTheme);
                foreach (var text in LogicalNodes(page).OfType<TextBlock>().Where(text => text.Visibility == Visibility.Visible && !string.IsNullOrEmpty(text.Text)))
                {
                    Assert.True(theme == text.ActualTheme, $"Visible text theme mismatch: expected={theme}; actual={text.ActualTheme}; text={text.Text}");
                    CheckText(text, host, 4.5, "body/" + text.Text);
                }
                foreach (var category in Enum.GetValues<HardwareModelCategory>())
                {
                    await SelectCategory(page, category, root);
                    foreach (var other in CategoryButtons(page))
                        CheckControlText(other.Value, host, 4.5, other.Key + "/summary/current-selection=" + category);
                    var tile = CategoryButtons(page)[category];
                    foreach (var state in new[] { "Normal", "PointerOver", "Normal" })
                    {
                        Assert.True(VisualStateManager.GoToState(tile, state, false));
                        await Settle(root, 30);
                        CheckControlText(tile, host, 4.5, category + "/summary/" + state);
                    }
                    var view = View(Editors(page)[category]);
                    view.Expander.IsExpanded = true;
                    view.Manufacturer.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    await Settle(root);
                    var inner = VisualNodes(view.Name).OfType<TextBox>().FirstOrDefault()
                        ?? throw new InvalidOperationException("AutoSuggestBox did not activate its native TextBox template.");
                    foreach (var control in new Control[] { view.Picker, inner, view.Manufacturer, view.Browse, view.Reset })
                    {
                        Assert.Equal(theme, control.ActualTheme);
                        foreach (var state in new[] { "Normal", "PointerOver", "Normal" })
                        {
                            Assert.True(VisualStateManager.GoToState(control, state, false), "Missing native state: " + control.GetType().Name + "/" + state);
                            await Settle(root, 30);
                            CheckControlText(control, host, 4.5, category + "/" + state);
                        }
                        control.IsEnabled = false;
                        await Settle(root, 30);
                        Assert.True(VisualStateManager.GoToState(control, "Disabled", false));
                        // Disabled stock controls are not subject to the 4.5:1
                        // active-text rule; still reject text lost on its canvas.
                        CheckControlText(control, host, 1.3, category + "/Disabled");
                        control.IsEnabled = true;
                        Assert.True(VisualStateManager.GoToState(control, "Normal", false));
                    }
                }
                AssertFixtureActionsDisabled(page);
                foreach (var buttonName in new[] { "ApplyButton", "RestoreButton", "RefreshButton" })
                    CheckControlText(Element<Button>(page, buttonName), host, 1.3, "fixture-footer/Disabled");
                await SelectCategory(page, HardwareModelCategory.Cpu, root);
                pageScroll.ChangeView(null, 0, null, disableAnimation: true);
                await Settle(root);
                if (PreviewEnabled)
                    await GoalGuideCases.SaveControlPreviewAsync(host, "hardware-editor-overview-" + theme.ToString().ToLowerInvariant() + ".png");
                pageScroll.ChangeView(null, pageScroll.ScrollableHeight, null, disableAnimation: true);
                await Settle(root);
                foreach (var text in Preview(page).Children.Cast<TextBlock>())
                {
                    Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0, "Preview text did not lay out.");
                    CheckText(text, host, 4.5, "preview/" + text.Text);
                }
                if (PreviewEnabled)
                    await GoalGuideCases.SaveControlPreviewAsync(host, "hardware-editor-preview-" + theme.ToString().ToLowerInvariant() + ".png");
            }
            AssertNoHardwareFiles();
            Console.WriteLine($"HARDWARE_EDITOR|app={Application.Current.RequestedTheme}|owner-theme-cycle=complete|body-preview-contrast>=4.5|native-hover-disabled=checked");
        });
    }

    private static async Task ResponsiveTiles()
    {
        RequireResources();
        await WithPage(async (page, host, root) =>
        {
            foreach (var (width, columns) in new[] { (1120d, 3), (780d, 2) })
            {
                page.Width = width;
                page.HorizontalAlignment = HorizontalAlignment.Center;
                await Settle(root);
                var panel = Element<Grid>(page, "CategoryCardsPanel");
                Assert.Equal(columns, panel.ColumnDefinitions.Count);
                Assert.Equal(6 / columns, panel.RowDefinitions.Count);
                Assert.Equal(6, CategoryButtons(page).Count);
                foreach (var (category, tile) in CategoryButtons(page))
                {
                    var origin = tile.TransformToVisual(panel).TransformPoint(new Windows.Foundation.Point(0, 0));
                    Assert.True(tile.ActualWidth > 0 && tile.ActualHeight > 0);
                    Assert.True(origin.X >= -.5 && origin.X + tile.ActualWidth <= panel.ActualWidth + .5,
                        "The responsive category tile escaped its panel: " + category);
                    await SelectCategory(page, category, root);
                }
                page.ApplyLocalization();
                await Settle(root, 320);
                var origins = CategoryButtons(page).Values.Select(tile => tile.TransformToVisual(panel)
                    .TransformPoint(new Windows.Foundation.Point(0, 0))).ToArray();
                Assert.Equal(6, origins.Select(point => (Math.Round(point.X), Math.Round(point.Y))).Distinct().Count());
                foreach (var tile in CategoryButtons(page).Values)
                    Assert.True(tile.ActualWidth > 0 && tile.ActualHeight > 0);
                await SelectCategory(page, HardwareModelCategory.Cpu, root);
                LogicalNodes(page).OfType<ScrollViewer>().Single().ChangeView(null, 0, null, true);
                await Settle(root);
                if (PreviewEnabled)
                    await GoalGuideCases.SaveControlPreviewAsync(host, "hardware-editor-responsive-" + (int)width + "-" + page.ActualTheme.ToString().ToLowerInvariant() + ".png");
            }
            AssertFixtureActionsDisabled(page);
            AssertNoHardwareFiles();
            Console.WriteLine("HARDWARE_EDITOR|summary-tiles=6|wide-columns=3|narrow-columns=2|visible-editor=selected-only");
        });
        if (PreviewEnabled) await DemoScreenshots();
    }

    private static async Task DemoScreenshots()
    {
        var names = new Dictionary<HardwareModelCategory, string>
        {
            [HardwareModelCategory.Cpu] = "AMD Ryzen 9 9950X3D",
            [HardwareModelCategory.Motherboard] = "MSI MAG X870 TOMAHAWK WIFI",
            [HardwareModelCategory.Gpu] = "NVIDIA GeForce RTX 5080",
            [HardwareModelCategory.Memory] = "Kingston FURY Beast DDR5",
            [HardwareModelCategory.Monitor] = "ASUS ROG Swift OLED PG27AQDM",
            [HardwareModelCategory.Disk] = "Samsung SSD 9100 PRO",
        };
        var devices = names.Select(pair =>
        {
            var preset = Assert.Single(HardwareModelCatalog.ForCategory(pair.Key).Where(model => model.Name == pair.Value));
            return new EditorDevice(pair.Key, "demo:" + pair.Key, "演示设备 " + HardwareSpooferPage.CategoryName(pair.Key),
                "演示厂商", preset.Name, preset.Manufacturer, "演示数据", pair.Key != HardwareModelCategory.Memory);
        }).ToArray();
        await WithPage(async (page, host, root) =>
        {
            var theme = AppTheme;
            root.RequestedTheme = host.RequestedTheme = page.RequestedTheme = theme;
            Element<TextBlock>(page, "BackupStatusText").Text = "演示数据 · 型号来自离线库 · 不读写真实硬件";
            await SelectCategory(page, HardwareModelCategory.Cpu, root);
            await Settle(root, 320);
            await new RenderTargetBitmap().RenderAsync(host);
            foreach (var tile in CategoryButtons(page)) CheckControlText(tile.Value, host, 4.5, "demo/" + tile.Key);
            AssertFixtureActionsDisabled(page);
            var scroll = LogicalNodes(page).OfType<ScrollViewer>().Single();
            await GoalGuideCases.SaveControlPreviewAsync(host, "hardware-editor-demo-overview-" + theme.ToString().ToLowerInvariant() + ".png");
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            await Settle(root, 320);
            await GoalGuideCases.SaveControlPreviewAsync(host, "hardware-editor-demo-preview-" + theme.ToString().ToLowerInvariant() + ".png");
            AssertNoHardwareFiles();
        }, new(devices, Array.Empty<string>()));
    }

    private static async Task WithPage(Func<HardwareSpooferPage, Grid, Grid, Task> body, HardwareEditorSnapshot? snapshot = null)
    {
        await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
        {
            var previousTheme = root.RequestedTheme;
            root.RequestedTheme = OppositeAppTheme;
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 1180, 1050));
            var host = Assert.IsType<Grid>(XamlReader.Load("""
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" />
                """));
            host.RequestedTheme = OppositeAppTheme;
            var page = new HardwareSpooferPage(snapshot ?? Snapshot()) { RequestedTheme = OppositeAppTheme };
            host.Children.Add(page);
            root.Children.Add(host);
            await Settle(root, 320);
            Assert.True(page.IsLoaded && page.ActualWidth > 0 && page.ActualHeight > 0);
            try { await body(page, host, root); }
            finally
            {
                // Native popup/focus callbacks can outlive removal of a page.
                // Close this fixture's owned popups before the next fixture.
                foreach (var editor in Editors(page).Values) View(editor).Name.IsSuggestionListOpen = false;
                await Settle(root, 100);
                root.Children.Remove(host);
                root.RequestedTheme = previousTheme;
                await Settle(root, 200);
            }
        });
    }

    private static HardwareEditorSnapshot Snapshot()
    {
        var devices = new List<EditorDevice>();
        foreach (var category in Enum.GetValues<HardwareModelCategory>())
        {
            foreach (var suffix in new[] { "a", "b" })
            {
                // GPU names deliberately match: identity must use the device ID.
                var original = category == HardwareModelCategory.Gpu ? "Same model GPU" : category + " original " + suffix;
                devices.Add(new(category, "fixture:" + category + ":" + suffix, original,
                    "Original manufacturer " + suffix, category + " saved " + suffix,
                    "Saved manufacturer " + suffix, "Synthetic fixture scope", category != HardwareModelCategory.Memory));
            }
        }
        return new(devices, Array.Empty<string>());
    }

    private static Dictionary<HardwareModelCategory, Expander> Editors(HardwareSpooferPage page)
        => Element<StackPanel>(page, "EditorsPanel").Children.Cast<Expander>()
            .ToDictionary(expander => Assert.IsType<HardwareModelCategory>(expander.Tag));

    private static Dictionary<HardwareModelCategory, Button> CategoryButtons(HardwareSpooferPage page)
        => Assert.IsAssignableFrom<Panel>(page.FindName("CategoryCardsPanel")).Children.OfType<Button>()
            .Where(button => button.Tag is HardwareModelCategory)
            .ToDictionary(button => Assert.IsType<HardwareModelCategory>(button.Tag));

    private static async Task SelectCategory(HardwareSpooferPage page, HardwareModelCategory category, Grid root)
    {
        var button = CategoryButtons(page)[category];
        // Cold native template activation completes after the first layout pass.
        // Wait for the actual tile to load, rather than the outer Page only.
        for (var attempt = 0; !button.IsLoaded && attempt < 20; ++attempt) await Settle(root, 100);
        await Invoke(button);
        await Settle(root, 40);
        foreach (var (itemCategory, expander) in Editors(page))
        {
            Assert.Equal(itemCategory == category ? Visibility.Visible : Visibility.Collapsed, expander.Visibility);
            if (itemCategory == category) Assert.True(expander.IsExpanded);
        }
    }

    private static EditorView View(Expander expander)
    {
        var body = Assert.IsType<StackPanel>(expander.Content);
        var buttons = body.Children.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);
        return new(expander, body, Assert.Single(body.Children.OfType<ComboBox>()),
            Assert.Single(body.Children.OfType<AutoSuggestBox>()), Assert.Single(body.Children.OfType<TextBox>()), buttons[0], buttons[1]);
    }

    private sealed record EditorView(Expander Expander, StackPanel Body, ComboBox Picker,
        AutoSuggestBox Name, TextBox Manufacturer, Button Browse, Button Reset);

    private static void Edit(EditorView view, string name, string manufacturer)
    {
        // Real TextBox.TextChanged commits both visible values through Store.
        view.Name.Text = name;
        view.Manufacturer.Text = manufacturer;
    }

    private static void AssertOriginal(EditorView view, string name)
        => Assert.Contains(view.Body.Children.OfType<TextBlock>(), text => text.Text.Contains(name, StringComparison.Ordinal));

    private static void AssertPreview(HardwareSpooferPage page, string name, string original)
        => Assert.Contains(Preview(page).Children.Cast<TextBlock>(), text => text.Text.Contains(name, StringComparison.Ordinal) && text.Text.Contains(original, StringComparison.Ordinal));

    private static async Task AssertPreviewEventually(HardwareSpooferPage page, string name, string original, Grid root)
    {
        // TextBox native events are deferred; observe their result before a
        // subsequent device switch, instead of depending on a fixed 30 ms.
        for (var attempt = 0; attempt < 20; ++attempt)
        {
            await Settle(root, 50);
            if (Preview(page).Children.Cast<TextBlock>().Any(text => text.Text.Contains(name, StringComparison.Ordinal) && text.Text.Contains(original, StringComparison.Ordinal))) return;
        }
        Assert.True(false, $"Preview did not settle for {name} / {original}: " + string.Join(" | ", Preview(page).Children.Cast<TextBlock>().Select(text => text.Text)));
    }

    private static StackPanel Preview(HardwareSpooferPage page) => Element<StackPanel>(page, "PreviewPanel");
    private static T Element<T>(HardwareSpooferPage page, string name) where T : FrameworkElement
        => Assert.IsType<T>(page.FindName(name));

    private static void AssertFixtureActionsDisabled(HardwareSpooferPage page)
    {
        foreach (var name in new[] { "ApplyButton", "RestoreButton", "RefreshButton" })
            Assert.False(Element<Button>(page, name).IsEnabled, "A hardware-mutating fixture action remained enabled: " + name);
        Assert.False(Element<CheckBox>(page, "SyncWindowsCheck").IsEnabled);
        Assert.False(Element<ProgressRing>(page, "LoadingRing").IsActive);
    }

    private static void RequireResources()
    {
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(declared), "Hardware fixtures require an isolated ZXAI_DATA_ROOT.");
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(ConfigManager.GetDataDir()), ignoreCase: true);
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        Assert.Null(ThemeResourceResolver.CurrentThemeKeyOverrideForTest);
        Assert.Null(ThemeResourceResolver.ExtraRootDictionariesForTest);
        var source = new Uri("ms-appx:///Styles/FluentTokens.xaml");
        if (!Application.Current.Resources.MergedDictionaries.Any(dictionary => dictionary.Source == source))
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
        AssertNoHardwareFiles();
    }

    private static void AssertNoHardwareFiles()
    {
        foreach (var name in new[] { "hardware-display-profile.json", "hardware-display-system-backup.json", "hardware_spoofer_backup.json" })
            Assert.False(File.Exists(Path.Combine(DataRoots.EffectiveTestRoot!, name)), "The synthetic page performed hardware profile persistence: " + name);
    }

    private static async Task Invoke(Button button)
    {
        Assert.True(button.IsEnabled && button.IsLoaded, $"Native invoke unavailable: category={button.Tag}; content={button.Content}; enabled={button.IsEnabled}; loaded={button.IsLoaded}");
        Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(30);
    }

    private static void CheckControlText(Control control, Grid host, double minimum, string label)
    {
        var texts = VisualNodes(control).OfType<TextBlock>()
            .Where(text => text.Visibility == Visibility.Visible && text.ActualWidth > 0 && text.ActualHeight > 0 && !string.IsNullOrEmpty(text.Text)).ToArray();
        if (control is Button or ComboBox) Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            // FontIcon's native template uses TextBlock for decorative glyphs;
            // graphical contrast is 3:1, while ordinary labels remain 4.5:1.
            var glyph = text.Text.All(character => char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.PrivateUse);
            CheckText(text, host, glyph ? Math.Min(minimum, 3) : minimum, label + "/" + text.Text);
        }
        if (control is TextBox textBox)
        {
            var content = VisualNodes(textBox).OfType<ScrollViewer>().FirstOrDefault();
            CheckContrast(Backdrop(content ?? (FrameworkElement)textBox, host), textBox.Foreground, minimum, label + "/input");
        }
    }

    private static void CheckText(TextBlock text, Grid host, double minimum, string label)
        => CheckContrast(Backdrop(text, host), text.Foreground, minimum, label);

    private static Color Backdrop(FrameworkElement element, Grid boundary)
    {
        var background = Assert.IsType<SolidColorBrush>(boundary.Background).Color;
        var brushes = new List<Brush>();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            var brush = parent switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                ContentPresenter presenter => presenter.Background,
                Control control => control.Background,
                _ => null,
            };
            if (brush is not null) brushes.Add(brush);
            if (ReferenceEquals(parent, boundary)) break;
        }
        for (var i = brushes.Count - 1; i >= 0; i--) background = Composite(brushes[i], background);
        return background;
    }

    private static Color Composite(Brush foreground, Color background)
    {
        var brush = Assert.IsType<SolidColorBrush>(foreground);
        var color = brush.Color;
        var alpha = color.A / 255d * brush.Opacity;
        byte Channel(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromArgb(255, Channel(color.R, background.R), Channel(color.G, background.G), Channel(color.B, background.B));
    }

    private static void CheckContrast(Color background, Brush foreground, double minimum, string label)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value) { var n = value / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var a = Luminance(background); var b = Luminance(Composite(foreground, background));
        var ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Assert.True(ratio >= minimum, $"{label}: {ratio:F2}:1 below {minimum}:1; background={background}; foreground={Assert.IsType<SolidColorBrush>(foreground).Color}");
    }

    private static IEnumerable<DependencyObject> VisualNodes(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualNodes(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static IEnumerable<DependencyObject> LogicalNodes(DependencyObject root)
    {
        yield return root;
        if (root is UIElement element && element.Visibility == Visibility.Collapsed) yield break;
        var children = root switch
        {
            Page page when page.Content is DependencyObject content => new[] { content },
            UserControl control when control.Content is DependencyObject content => new[] { content },
            Panel panel => panel.Children.Cast<DependencyObject>(),
            Border border when border.Child is { } child => new[] { child },
            ScrollViewer scroll when scroll.Content is DependencyObject content => new[] { content },
            Expander expander => (expander.IsExpanded ? new[] { expander.Header, expander.Content } : new[] { expander.Header }).OfType<DependencyObject>(),
            ContentControl control when control.Content is DependencyObject content => new[] { content },
            _ => Array.Empty<DependencyObject>(),
        };
        foreach (var child in children)
            foreach (var node in LogicalNodes(child)) yield return node;
    }

    private static Task Settle(Grid root, int delay = 150) => SettleCore(root, delay);
    private static async Task SettleCore(Grid root, int delay) { root.UpdateLayout(); await Task.Delay(delay); root.UpdateLayout(); await Task.Yield(); }
    private static ElementTheme AppTheme => Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
    private static ElementTheme OppositeAppTheme => AppTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
    private static bool PreviewEnabled => Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1";
}
