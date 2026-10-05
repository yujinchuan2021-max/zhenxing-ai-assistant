using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.CloudTools;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Attaches the compiled AppCenterPage and real data templates to the empty TestApp.
/// No Frame navigation, refresh handler, tool action, model, network or environment probe runs.
/// Native template rows use synthetic in-memory state. The inventory projection case
/// uses only a GUID fixture beneath ZXAI_DATA_ROOT and a transport that rejects requests.
/// </summary>
internal static class AppCenterCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("AppCenter_NativeTemplates_Narrow_TwoThemes", () => NativePageAsync(360)),
        ("AppCenter_NativeTemplates_Wide_TwoThemes", () => NativePageAsync(1120)),
        ("AppCenter_RegisteredClaude_VerifiedUninstall_NativeTemplates_TwoThemes", () => NativePageAsync(1120, registeredPackagesOnly: true)),
        ("AppCenter_CloudSnapshot_OnlyLocalAndRequestedTasks_Offline", InventoryProjectionAsync),
    ];

    private static async Task NativePageAsync(double width, bool registeredPackagesOnly = false)
    {
        RequireIsolation();
        Assert.IsType<TestApp>(Application.Current);
        var previousResolver = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        AppCenterPage? page = null;
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
                    page = new AppCenterPage
                    {
                        Width = width, Height = 700, HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    var fixtures = registeredPackagesOnly ? RegisteredPackageRows() : MemoryRows();
                    foreach (var item in fixtures.Where(item => item.Section == "cloud")) page.CloudItems.Add(item);
                    foreach (var item in fixtures.Where(item => item.Section == "sandbox")) page.SandboxItems.Add(item);
                    // Production Render controls this from the filtered snapshot count;
                    // the template fixture bypasses navigation and Render deliberately.
                    Assert.IsType<StackPanel>(page.FindName("CloudSection")).Visibility = registeredPackagesOnly ? Visibility.Collapsed : Visibility.Visible;
                    Assert.IsType<TextBlock>(page.FindName("SandboxEmpty")).Visibility = Visibility.Collapsed;
                    AssertIdlePage(page);
                    var loaded = new TaskCompletionSource();
                    page.Loaded += (_, _) => loaded.TrySetResult();
                    root.Children.Add(page); // Grid attachment deliberately bypasses OnNavigatedTo.
                    Assert.Same(loaded.Task, await Task.WhenAny(loaded.Task, Task.Delay(6000)));
                    foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                    {
                        root.RequestedTheme = page.RequestedTheme = theme;
                        var canvas = theme == ElementTheme.Light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
                        root.Background = page.Background = new SolidColorBrush(canvas);
                        await SettleAsync(root);
                        AssertIdlePage(page);
                        Assert.Equal(theme, page.ActualTheme);
                        Assert.InRange(page.ActualWidth, width - 1, width + 1);
                        Assert.True(page.ActualHeight > 0);
                        AssertToolbar(page, width);
                        AssertCards(page, fixtures, theme, canvas, width);
                        if (registeredPackagesOnly) AssertRegisteredPackageActions(page, fixtures, verified: false);
                        if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                            await GoalGuideCases.SaveControlPreviewAsync(page, (registeredPackagesOnly ? "app-center-claude-unverified-" : "app-center-") + (int)width + "-" + theme.ToString().ToLowerInvariant() + ".png");
                        Console.WriteLine($"APP_CENTER_NATIVE|width={width}|theme={theme}|rows={fixtures.Length}|navigation=not-run|actions=not-run");
                    }
                    if (registeredPackagesOnly)
                    {
                        // Production Render replaces collection entries after a check. Rebind the same
                        // in-memory records to exercise their real OneTime x:Bind template refresh.
                        page.SandboxItems.Clear();
                        foreach (var item in fixtures)
                        {
                            item.PackageInspection = new(RegisteredPackageState.Installed, "已确认匹配已安装的软件包；启动入口仍待确认。");
                            AppCenterService.RefreshLocalEntry(item, _ => false, _ => false);
                            page.SandboxItems.Add(item);
                        }
                        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                        {
                            root.RequestedTheme = page.RequestedTheme = theme;
                            var canvas = theme == ElementTheme.Light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
                            root.Background = page.Background = new SolidColorBrush(canvas);
                            await SettleAsync(root);
                            AssertIdlePage(page);
                            AssertCards(page, fixtures, theme, canvas, width);
                            AssertRegisteredPackageActions(page, fixtures, verified: true);
                            if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                                await GoalGuideCases.SaveControlPreviewAsync(page, "app-center-claude-verified-" + (int)width + "-" + theme.ToString().ToLowerInvariant() + ".png");
                        }
                        Console.WriteLine("APP_CENTER_REGISTERED_PACKAGE|records=2|unverified-uninstall=hidden|verified-uninstall=visible|record-identity=retained|winget=not-run|actions=not-run");
                    }
                    root.Children.Remove(page);
                    await SettleAsync(root);
                    AssertIdlePage(page);
                }
                finally
                {
                    if (page is not null) root.Children.Remove(page);
                    root.RequestedTheme = previousTheme; root.Background = previousBackground;
                }
            });
        }
        finally
        {
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousResolver;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
        }
    }

    private static void AssertToolbar(AppCenterPage page, double width)
    {
        var toolbar = Assert.IsType<StackPanel>(page.FindName("Toolbar"));
        Assert.Equal(width < 760 ? Orientation.Vertical : Orientation.Horizontal, toolbar.Orientation);
        Assert.True(toolbar.ActualWidth > 0);
        foreach (var child in toolbar.Children.OfType<FrameworkElement>())
        {
            Assert.True(child.ActualHeight > 0);
            var x = child.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X;
            Assert.InRange(x, -1, page.ActualWidth);
            Assert.True(x + child.ActualWidth <= page.ActualWidth + 1, "A toolbar control overflows the page.");
        }
        Assert.Contains(Flatten(page).OfType<Button>(), b => b.Name == "RefreshButton");
        Assert.Contains(Flatten(page).OfType<Button>(), b => b.Name == "RegisterButton");
    }

    private static void AssertCards(AppCenterPage page, AppCenterItem[] fixtures, ElementTheme theme,
        Windows.UI.Color canvas, double width)
    {
        var buttons = Flatten(page).OfType<Button>().Where(button => button.Tag is AppCenterItem).ToArray();
        Assert.NotEmpty(buttons);
        foreach (var item in fixtures)
        {
            var row = buttons.Where(button => ReferenceEquals(button.Tag, item)).ToArray();
            Assert.NotEmpty(row);
            var primary = row[0];
            Assert.Equal(item.PrimaryLabel, primary.Content);
            Assert.Equal(item.PrimaryEnabled, primary.IsEnabled);
            Assert.Equal(Visibility.Visible, primary.Visibility);
            var actions = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(primary));
            Assert.Equal(width < 760 ? Orientation.Vertical : Orientation.Horizontal, actions.Orientation);
            var card = Ancestor<Border>(actions);
            Assert.NotNull(card);
            Assert.True(card!.ActualHeight > 0 && card.ActualWidth > 0);
            Assert.Equal(theme, card.ActualTheme);
            foreach (var button in row.Where(button => button.Visibility == Visibility.Visible))
            {
                Assert.True(button.ActualHeight > 0, "A visible action lost its native layout.");
                Assert.True(VisualTreeHelper.GetChildrenCount(button) > 0, "The native button template did not apply.");
                Assert.Equal(!item.IsBusy && (button == primary ? item.PrimaryEnabled : true), button.IsEnabled);
                var x = button.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X;
                Assert.InRange(x, -1, page.ActualWidth);
                Assert.True(x + button.ActualWidth <= page.ActualWidth + 1, "A card action overflows the page.");
            }
            var secondary = row.Skip(1).ToArray();
            AssertAction(secondary, "打开位置", item.ShowLocation);
            AssertAction(secondary, "打开当前版本", item.ShowOpen);
            AssertAction(secondary, "创建桌面图标", item.CanCreateShortcut);
            AssertAction(secondary, "移除下载包", item.CanRemoveManaged);
            AssertAction(secondary, "移除记录", item.ShowRemove);
            AssertAction(secondary, "检查安装状态", item.ShowPackageCheck);
            AssertAction(secondary, "Windows 应用设置", item.ShowSystemApps);
            AssertAction(secondary, "卸载", item.CanUninstall);
            var title = Assert.Single(Flatten(card).OfType<TextBlock>().Where(text => text.Text == item.Name));
            var status = Assert.Single(Flatten(card).OfType<TextBlock>().Where(text => text.Text == item.StatusLabel));
            Assert.Equal(theme, title.ActualTheme);
            AssertContrast(card.Background, title.Foreground, canvas, 4.5);
            AssertContrast(card.Background, status.Foreground, canvas, 4.5);
        }
        if (fixtures.Any(item => item.Section == "cloud"))
        {
            Assert.Contains(buttons, button => Equals(button.Content, "下载") && button.IsEnabled);
            Assert.Contains(buttons, button => Equals(button.Content, "更新") && button.IsEnabled);
            Assert.Contains(buttons, button => Equals(button.Content, "打开") && button.IsEnabled);
            Assert.Contains(buttons, button => Equals(button.Content, "重试") && button.IsEnabled);
            Assert.Contains(buttons, button => Equals(button.Content, "等待工具退出") && !button.IsEnabled);
        }
        Assert.DoesNotContain(buttons, button => Equals(button.Content, "查看来源") || Equals(button.Content, "暂不可自动下载"));
    }

    private static AppCenterItem[] RegisteredPackageRows()
    {
        AppCenterItem[] rows =
        [
            new() { Name = "Claude 桌面版", WingetId = "Anthropic.Claude" },
            new() { Name = "Claude Code", WingetId = "Anthropic.ClaudeCode",
                PackageInspection = new(RegisteredPackageState.Unmatched, "未找到匹配的软件包，不能据此确认已卸载；请到 Windows 应用设置检查。") },
        ];
        foreach (var item in rows)
        {
            item.Section = "sandbox";
            item.RecordId = "memory-" + item.WingetId;
            item.Path = "winget:" + item.WingetId;
            item.Glyph = "\uE8F1";
            item.Detail = "隔离验收用模拟登记记录；不运行检测、卸载或系统设置。";
            item.ShowRemove = item.AllowLaunch = true;
            AppCenterService.RefreshLocalEntry(item, _ => false, _ => false);
        }
        return rows;
    }

    private static void AssertRegisteredPackageActions(AppCenterPage page, AppCenterItem[] fixtures, bool verified)
    {
        Assert.Equal(2, page.SandboxItems.Count);
        foreach (var item in fixtures)
        {
            Assert.Equal("memory-" + item.WingetId, item.RecordId);
            Assert.Equal("winget:" + item.WingetId, item.Path);
            Assert.False(item.CanOpenTool);
            Assert.Equal(verified, item.CanUninstall);
            Assert.Equal(nameof(AppCenterPrimaryAction.CheckInstallation), item.PrimaryAction);
            var row = Flatten(page).OfType<Button>().Where(button => ReferenceEquals(button.Tag, item)).ToArray();
            var check = Assert.Single(row.Where(button => Equals(button.Content, "检查安装状态") && button.Visibility == Visibility.Visible));
            Assert.True(check.IsEnabled);
            AssertAction(row, "Windows 应用设置", visible: true);
            AssertAction(row, "移除记录", visible: true);
            AssertAction(row, "卸载", visible: verified);
        }
    }

    private static void AssertAction(Button[] row, string label, bool visible)
    {
        var button = Assert.Single(row.Where(button => Equals(button.Content, label)));
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
    }

    private static AppCenterItem[] MemoryRows() =>
    [
        Cloud("Memory managed missing entry", AppCenterToolStatus.Installed, false, true, false),
        Cloud("Memory update", AppCenterToolStatus.Installed, true, true, true),
        Cloud("Memory legacy open", AppCenterToolStatus.Installed, true, false, false),
        Cloud("Memory retry", AppCenterToolStatus.Failed, false, false, false),
        Cloud("Memory pending", AppCenterToolStatus.PendingUpdate, true, true, true, pending: true),
        Cloud("Memory busy", AppCenterToolStatus.Downloading, false, false, false),
        new() { Section = "sandbox", Name = "Memory folder registration", RecordId = "memory-folder", Detail = "Synthetic folder, never opened or scanned.",
            StatusKey = "installed", StatusLabel = "已登记文件夹", PrimaryAction = "OpenLocation", PrimaryLabel = "打开位置", PrimaryEnabled = true,
            CanOpenFolder = true, ShowRemove = true },
        new() { Section = "sandbox", Name = "Memory executable registration", RecordId = "memory-executable", Detail = "Synthetic executable, no file exists.",
            StatusKey = "installed", StatusLabel = "已登记程序", PrimaryAction = "Open", PrimaryLabel = "打开", PrimaryEnabled = true,
            CanOpenFolder = true, CanOpenTool = true, CanCreateShortcut = true, ShowRemove = true, CanUninstall = true },
        new() { Section = "sandbox", Name = "Memory missing entry", RecordId = "memory-missing", Detail = "Synthetic missing path, record is retained.",
            StatusKey = "missing", StatusLabel = "入口未找到，保留登记记录", PrimaryAction = "None", PrimaryLabel = "入口未找到", PrimaryEnabled = false, ShowRemove = true },
    ];

    private static AppCenterItem Cloud(string name, AppCenterToolStatus status, bool entry, bool managed, bool update,
        bool pending = false, bool package = true, bool source = false)
    {
        var view = AppCenterActionPresentation.Cloud(status, entry, managed, update, pending, 35, package, source);
        return new AppCenterItem
        {
            Section = "cloud", CloudId = name.Replace(' ', '-'), Name = name, Version = "synthetic 1.0", Glyph = "\uE8F1",
            Detail = "Synthetic state; this test never downloads, installs, probes paths or opens a tool.",
            StatusKey = view.StatusKey == "download" ? "attention" : view.StatusKey,
            StatusLabel = view.StatusLabel, PrimaryAction = view.PrimaryAction.ToString(),
            PrimaryLabel = view.PrimaryLabel, PrimaryEnabled = view.PrimaryEnabled,
            CanOpenTool = entry && status is not (AppCenterToolStatus.Downloading or AppCenterToolStatus.Installing or AppCenterToolStatus.Updating or AppCenterToolStatus.Removing),
            CanOpenFolder = view.CanOpenLocation, CanCreateShortcut = view.CanCreateShortcut, CanRemoveManaged = view.CanRemoveManaged,
            IsBusy = status is AppCenterToolStatus.Downloading or AppCenterToolStatus.Installing or AppCenterToolStatus.Updating or AppCenterToolStatus.Removing,
        };
    }

    private static async Task InventoryProjectionAsync()
    {
        RequireIsolation();
        Assert.IsType<TestApp>(Application.Current);
        string isolationRoot = Path.GetFullPath(DataRoots.EffectiveTestRoot!);
        string fixtureRoot = Path.Combine(isolationRoot, "app-center-inventory-" + Guid.NewGuid().ToString("N"));
        Assert.StartsWith(isolationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            Path.GetFullPath(fixtureRoot), StringComparison.OrdinalIgnoreCase);
        CloudToolValidation.CheckNoReparse(fixtureRoot);
        var previousManager = CloudToolService.OverrideForTests;
        var resources = new XamlControlsResources();
        Application.Current.Resources.MergedDictionaries.Add(resources);
        using var transport = new RejectNetworkTransport();
        using var http = new HttpClient(transport);
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            var catalog = new CloudToolCatalog
            {
                Revision = 1, PublishedAt = "2026-10-05T01:00:00Z", MinClientVersion = "0.1.0.0",
                Tools = [Packaged("unused-tool"), Packaged("requested-tool"), new()
                {
                    Id = "manual-tool", Name = "Synthetic manual source", Category = "系统工具", Version = "1.0",
                    Homepage = "https://example.invalid/", Packages = [],
                }],
            };
            string seed = Path.Combine(fixtureRoot, "seed.json");
            File.WriteAllText(seed, JsonSerializer.Serialize(catalog, CloudToolValidation.JsonOptions));
            var manager = new CloudToolManager(fixtureRoot, http,
                new Uri("https://zhenxingai.com/api/toolflows/v1/tools/catalog"), "x64", new Version(0, 1, 0, 0),
                seed, Path.Combine(fixtureRoot, "no-legacy-tools"), allowNetwork: false, isInUse: _ => false);
            CloudToolService.OverrideForTests = manager;
            var page = new AppCenterPage(); // No attachment or Frame navigation starts probes.
            AssertIdlePage(page);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(page.FindName("CloudSection")).Visibility);
            Assert.Equal(3, manager.GetCatalog().Count);
            Assert.All(manager.GetStates(), state =>
            {
                Assert.False(state.IsManaged);
                Assert.False(state.HasOperationActivity);
                Assert.True(state.Status is CloudToolStatus.NotInstalled or CloudToolStatus.Unsupported);
            });
            Assert.Empty(Snapshot(page));

            // The real manager records an initiated failure before any transport request.
            // Calling the page projection verifies the production policy wiring, without
            // clicking an action handler, navigating, refreshing or installing a payload.
            Assert.False((await manager.InstallAsync("requested-tool")).Success);
            var requested = Assert.Single(manager.GetStates().Where(state => state.HasOperationActivity));
            Assert.Equal("requested-tool", requested.Id);
            Assert.Equal(CloudToolStatus.Failed, requested.Status);
            var visible = Assert.Single(Snapshot(page));
            Assert.Equal("requested-tool", visible.CloudId);
            Assert.Equal("attention", visible.StatusKey);
            Assert.Equal(AppCenterPrimaryAction.Retry.ToString(), visible.PrimaryAction);
            Assert.True(visible.PrimaryEnabled);
            Assert.False(visible.IsBusy);
            Assert.False(visible.CanRemoveManaged);
            Assert.Equal(3, manager.GetCatalog().Count); // The tool library retains all source entries.
            Assert.Empty(page.CloudItems); // Projection alone has not run Render.
            AssertIdlePage(page);
            Assert.Equal(0, transport.Requests);
            Console.WriteLine("APP_CENTER_INVENTORY|catalog=3|initial=0|requested-failure=1|transport=0|navigation=not-run|actions=not-run");
        }
        finally
        {
            CloudToolService.OverrideForTests = previousManager;
            Application.Current.Resources.MergedDictionaries.Remove(resources);
            if (Directory.Exists(fixtureRoot))
            {
                CloudToolValidation.CheckNoReparse(fixtureRoot);
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }

        static CloudToolDefinition Packaged(string id) => new()
        {
            Id = id, Name = "Synthetic " + id, Category = "系统工具", Version = "1.0",
            Packages = [new() { Architecture = "x64", Url = "https://zhenxingai.com/downloads/tools/" + id + ".zip",
                SizeBytes = 2, Sha256 = new string('a', 64), EntryPoint = "app.exe" }],
        };
        static List<AppCenterItem> Snapshot(AppCenterPage page)
        {
            var method = typeof(AppCenterPage).GetMethod("CloudSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("App-center cloud projection is missing.");
            return Assert.IsType<List<AppCenterItem>>(method.Invoke(page, null));
        }
    }

    private sealed class RejectNetworkTransport : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("The app-center inventory fixture must never request a network resource.");
        }
    }

    private static void AssertIdlePage(AppCenterPage page)
    {
        Assert.False(Field<bool>(page, "_active"), "Frame navigation must not run in this fixture.");
        Assert.False(Field<bool>(page, "_refreshing"));
        Assert.Null(Field<object?>(page, "_pageStop"));
        Assert.Empty(Field<List<AppCenterItem>>(page, "_localItems"));
        var serviceLifetime = typeof(CloudToolService).GetField("_lifetime", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Null(serviceLifetime.GetValue(null));
    }
    private static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("App-center guard field is missing: " + name);
        return (T)field.GetValue(target)!;
    }

    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
    private static IEnumerable<DependencyObject> Flatten(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Flatten(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task SettleAsync(Grid root) { await Task.Delay(200); root.UpdateLayout(); await Task.Yield(); root.UpdateLayout(); }

    // Native stock cards may use translucent brushes. Composite them onto the test canvas
    // before checking contrast instead of pretending their alpha channel is always opaque.
    private static void AssertContrast(Brush? surface, Brush? text, Windows.UI.Color canvas, double minimum)
    {
        var bg = Assert.IsType<SolidColorBrush>(surface);
        var fg = Assert.IsType<SolidColorBrush>(text);
        static (double R, double G, double B) Composite(Windows.UI.Color color, double opacity, (double R, double G, double B) under)
        {
            double alpha = color.A / 255d * opacity;
            return (color.R / 255d * alpha + under.R * (1 - alpha), color.G / 255d * alpha + under.G * (1 - alpha),
                color.B / 255d * alpha + under.B * (1 - alpha));
        }
        static double Luminance((double R, double G, double B) color)
        {
            static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var baseColor = Composite(bg.Color, bg.Opacity, (canvas.R / 255d, canvas.G / 255d, canvas.B / 255d));
        var textColor = Composite(fg.Color, fg.Opacity, baseColor);
        double a = Luminance(baseColor), b = Luminance(textColor);
        double contrast = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        Assert.True(contrast >= minimum, $"Native app-center text contrast is {contrast:F2}, below {minimum}.");
    }

    private static void RequireIsolation()
    {
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(declared), "App-center native cases require a fresh ZXAI_DATA_ROOT.");
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(ConfigManager.GetDataDir()), ignoreCase: true);
    }
}
