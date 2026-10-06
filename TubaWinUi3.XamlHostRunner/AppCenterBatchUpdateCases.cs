using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.CloudTools;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Native projections of one synthetic app-owned batch. Pages are attached directly,
/// never navigated: no software probe, updater click, vendor process or real HTTP runs.
/// Fixture-only MZ markers and receipts live under a fresh GUID within ZXAI_DATA_ROOT.
/// </summary>
internal static class AppCenterBatchUpdateCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("AppCenter_BatchUpdate_Narrow_TwoThemes", () => NativeBatchAsync(360)),
        ("AppCenter_BatchUpdate_Wide_TwoThemes", () => NativeBatchAsync(1120)),
    ];

    private static async Task NativeBatchAsync(double width)
    {
        RequireIsolation();
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        using var fixture = new Fixture();
        var previousManager = CloudToolService.OverrideForTests;
        var previousBatch = CloudToolService.BatchUpdatesOverrideForTests;
        var previousTheme = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentDictionary<string, CloudToolState>(
            fixture.Manager.GetStates().ToDictionary(state => state.Id, StringComparer.Ordinal));
        var batch = new CloudToolBatchUpdateCoordinator(async (candidate, token) =>
        {
            var state = states[candidate.Id];
            if (candidate.Id == "batch-alpha")
            {
                states[candidate.Id] = state with { Status = CloudToolStatus.Downloading, Progress = 45 };
                started.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                fixture.WriteReceipt(candidate.Id, candidate.TargetVersion, candidate.PackageSha256);
                var applied = state with { Version = candidate.TargetVersion, HasUpdate = false };
                states[candidate.Id] = applied;
                return new(true, "合成更新已经应用。", applied);
            }
            if (candidate.Id == "batch-beta")
            {
                var failed = state with { Status = CloudToolStatus.Failed, Error = "合成校验失败，原有工具保留。" };
                states[candidate.Id] = failed;
                return new(false, failed.Error!, failed);
            }
            fixture.SetPending(candidate.Id, true);
            var pending = state with { Status = CloudToolStatus.PendingUpdate, PendingUpdate = true };
            states[candidate.Id] = pending;
            return new(false, "合成工具正在运行，等待退出。", pending);
        }, id => states.GetValueOrDefault(id), _ => true, candidate =>
        {
            fixture.SetPending(candidate.Id, false);
            states[candidate.Id] = states[candidate.Id] with
            { Status = CloudToolStatus.Installed, PendingUpdate = false };
            return Task.FromResult(true);
        });
        Task<CloudToolBatchUpdateSnapshot>? task = null;
        try
        {
            CloudToolService.OverrideForTests = fixture.Manager;
            CloudToolService.BatchUpdatesOverrideForTests = batch;
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            var plan = CloudToolService.GetBatchUpdatePlan();
            Assert.Equal(3, plan.CandidateCount);
            Assert.Equal(6L * 1024 * 1024, plan.TotalSizeBytes);
            await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
            {
                var oldTheme = root.RequestedTheme;
                var oldBackground = root.Background;
                AppCenterPage? page = null;
                FrameworkElement? confirmation = null;
                try
                {
                    page = CreatePage(width, batch);
                    root.Children.Add(page);
                    await SettleAsync(root);
                    Render(page);
                    AssertCountIndependentOfFilter(page, 3);

                    confirmation = Invoke<FrameworkElement>(page, "CreateBatchConfirmationContent", plan);
                    confirmation.Width = Math.Min(600, width - 48);
                    confirmation.HorizontalAlignment = HorizontalAlignment.Left;
                    confirmation.VerticalAlignment = VerticalAlignment.Top;
                    root.Children.Remove(page);
                    root.Children.Add(confirmation);
                    foreach (var theme in Themes)
                    {
                        Paint(root, confirmation, theme);
                        await SettleAsync(root);
                        var summary = Named<TextBlock>(confirmation, "ConfirmationSummary");
                        Assert.Contains("3", summary.Text);
                        Assert.Contains(UpdateService.FormatSize(plan.TotalSizeBytes), summary.Text);
                        var rows = Named<ItemsControl>(confirmation, "ConfirmationItems").Items.OfType<string>().ToArray();
                        Assert.Equal(3, rows.Length);
                        Assert.All(plan.Candidates, candidate => Assert.Contains(rows, row =>
                            row.Contains(candidate.Name, StringComparison.Ordinal) &&
                            row.Contains(UpdateService.FormatSize(candidate.SizeBytes), StringComparison.Ordinal)));
                        Assert.Contains(rows, row => row.Contains(MiscTexts.TSub("2.0（同版本修订）"), StringComparison.Ordinal));
                        AssertTexts(root.Background, Flatten(confirmation).OfType<TextBlock>(), Canvas(theme));
                        AssertWithin(confirmation, confirmation);
                        await Preview(confirmation, $"app-center-batch-confirm-{(int)width}-{theme.ToString().ToLowerInvariant()}.png");
                    }
                    root.Children.Remove(confirmation);
                    confirmation = null;
                    root.Children.Add(page);

                    task = batch.StartOrJoinAsync(plan);
                    Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(5000)));
                    Guid taskId = batch.Snapshot.Id;
                    foreach (var theme in Themes)
                    {
                        Paint(root, page, theme);
                        Render(page);
                        await SettleAsync(root);
                        Assert.Equal(Visibility.Visible, Named<Border>(page, "BatchPanel").Visibility);
                        Assert.Contains("45%", Named<TextBlock>(page, "BatchCurrentText").Text);
                        Assert.Contains(states["batch-alpha"].Name, Named<TextBlock>(page, "BatchCurrentText").Text);
                        Assert.InRange(Named<ProgressBar>(page, "BatchProgressBar").Value, 14.9, 15.1);
                        Assert.Equal(Visibility.Visible, Named<Button>(page, "BatchCancelButton").Visibility);
                        Assert.True(Named<Button>(page, "BatchCancelButton").IsEnabled);
                        Assert.False(Named<Button>(page, "BatchUpdateButton").IsEnabled);
                        AssertPagePaletteAndBounds(page, theme);
                        AssertIdle(page);
                        await Preview(page, $"app-center-batch-running-{(int)width}-{theme.ToString().ToLowerInvariant()}.png");
                    }

                    // Removing a page invokes its real Deactivate/Unloaded guard, while
                    // the synthetic service-owned operation remains blocked on its gate.
                    root.Children.Remove(page);
                    await SettleAsync(root);
                    Assert.False(task.IsCompleted);
                    Assert.True(batch.IsRunning);
                    AssertIdle(page);
                    page = CreatePage(width, batch);
                    root.Children.Add(page);
                    Render(page);
                    await SettleAsync(root);
                    Assert.Equal(taskId, batch.Snapshot.Id);
                    Assert.Contains("45%", Named<TextBlock>(page, "BatchCurrentText").Text);
                    Assert.False(task.IsCompleted);
                    AssertIdle(page);

                    release.TrySetResult();
                    Assert.Same(task, await Task.WhenAny(task, Task.Delay(5000)));
                    var result = await task;
                    Assert.Equal(1, result.SucceededCount);
                    Assert.Equal(1, result.FailedCount);
                    Assert.Equal(1, result.PendingUpdateCount);
                    foreach (var theme in Themes)
                    {
                        Paint(root, page, theme);
                        Render(page);
                        await SettleAsync(root);
                        Assert.Equal(1, CloudToolService.GetBatchUpdatePlan().CandidateCount);
                        Assert.False(Named<Button>(page, "BatchUpdateButton").IsEnabled);
                        Assert.True(Named<Button>(page, "BatchCancelButton").IsEnabled);
                        Assert.Equal(MiscTexts.T("取消等待更新"), Named<Button>(page, "BatchCancelButton").Content);
                        Assert.Contains("1/3", Named<TextBlock>(page, "BatchSummaryText").Text);
                        Assert.InRange(Named<ProgressBar>(page, "BatchProgressBar").Value, 66.5, 66.8);
                        Assert.True(Named<Expander>(page, "BatchDetailsExpander").IsExpanded);
                        var details = Named<ItemsControl>(page, "BatchDetailsList").Items.OfType<string>().ToArray();
                        Assert.Contains(details, detail => detail.Contains(MiscTexts.T("更新失败"), StringComparison.Ordinal));
                        Assert.Contains(details, detail => detail.Contains(MiscTexts.T("等待工具退出，尚未应用"), StringComparison.Ordinal));
                        AssertPagePaletteAndBounds(page, theme);
                        AssertIdle(page);
                        await Preview(page, $"app-center-batch-partial-{(int)width}-{theme.ToString().ToLowerInvariant()}.png");
                    }

                    // Invoke the synthetic coordinator only, never the page action handler.
                    await batch.CancelAsync();
                    Render(page);
                    await SettleAsync(root);
                    Assert.Equal(0, batch.Snapshot.PendingUpdateCount);
                    Assert.Equal(1, batch.Snapshot.SucceededCount);
                    Assert.Equal(Visibility.Collapsed, Named<Button>(page, "BatchCancelButton").Visibility);
                    Assert.True(Named<Button>(page, "BatchUpdateButton").IsEnabled);
                    AssertCountIndependentOfFilter(page, 2);
                    Assert.Equal(0, fixture.Transport.Requests);
                    Console.WriteLine($"APP_CENTER_BATCH_NATIVE|width={width}|themes=Light,Dark,Light|confirmed=3|bytes={plan.TotalSizeBytes}|progress=45|success=1|failed=1|pending=1|recreated-page=same-task|pending-cancelled=1|http=0|navigation=not-run|vendor=not-run");
                }
                finally
                {
                    release.TrySetResult();
                    if (task is not null) await task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (page is not null) root.Children.Remove(page);
                    if (confirmation is not null) root.Children.Remove(confirmation);
                    root.RequestedTheme = oldTheme;
                    root.Background = oldBackground;
                }
            });
        }
        finally
        {
            CloudToolService.OverrideForTests = previousManager;
            CloudToolService.BatchUpdatesOverrideForTests = previousBatch;
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousTheme;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
        }
    }

    private static readonly ElementTheme[] Themes = [ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light];
    private static Windows.UI.Color Canvas(ElementTheme theme) => theme == ElementTheme.Light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
    private static void Paint(Grid root, FrameworkElement content, ElementTheme theme)
    {
        root.RequestedTheme = content.RequestedTheme = theme;
        root.Background = new SolidColorBrush(Canvas(theme));
        if (content is Page page) page.Background = new SolidColorBrush(Canvas(theme));
    }
    private static AppCenterPage CreatePage(double width, CloudToolBatchUpdateCoordinator batch)
    {
        var page = new AppCenterPage { Width = width, Height = 700,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        typeof(AppCenterPage).GetField("_batchUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, batch);
        AssertIdle(page);
        return page;
    }
    private static void Render(AppCenterPage page) => Invoke<object?>(page, "RenderBatchUpdate");
    private static T Invoke<T>(AppCenterPage page, string method, params object[] args) =>
        (T)typeof(AppCenterPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args)!;
    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement => Assert.IsAssignableFrom<T>(root.FindName(name));
    private static async Task SettleAsync(Grid root) { await Task.Delay(160); root.UpdateLayout(); await Task.Yield(); root.UpdateLayout(); }
    private static async Task Preview(FrameworkElement content, string fileName)
    {
        if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1") await GoalGuideCases.SaveControlPreviewAsync(content, fileName);
    }
    private static void AssertCountIndependentOfFilter(AppCenterPage page, int count)
    {
        Assert.Equal(0, page.CloudItems.Count);
        Named<TextBox>(page, "SearchBox").Text = "no-match-synthetic-filter";
        Named<ComboBox>(page, "StatusFilter").SelectedIndex = 5;
        Render(page);
        Assert.Equal(MiscTexts.TSub($"全部更新 ({count})"), Named<Button>(page, "BatchUpdateButton").Content);
        Named<TextBox>(page, "SearchBox").Text = "";
        Named<ComboBox>(page, "StatusFilter").SelectedIndex = 0;
    }
    private static void AssertIdle(AppCenterPage page)
    {
        Assert.False((bool)typeof(AppCenterPage).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
        Assert.Null(typeof(AppCenterPage).GetField("_pageStop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
        Assert.Null(typeof(CloudToolService).GetField("_lifetime", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
    }
    private static void AssertPagePaletteAndBounds(AppCenterPage page, ElementTheme theme)
    {
        var panel = Named<Border>(page, "BatchPanel");
        Assert.Equal(theme, page.ActualTheme);
        AssertTexts(panel.Background, [Named<TextBlock>(page, "BatchTitleText"), Named<TextBlock>(page, "BatchSummaryText"),
            Named<TextBlock>(page, "BatchCurrentText")], Canvas(theme));
        AssertTexts(page.Background, [Named<TextBlock>(page, "BatchScopeText")], Canvas(theme));
        AssertTexts(panel.Background, Flatten(Named<ItemsControl>(page, "BatchDetailsList")).OfType<TextBlock>(), Canvas(theme));
        foreach (string name in new[] { "BatchPanel", "BatchUpdateButton", "BatchCancelButton", "BatchScopeText", "BatchViewButton" })
            AssertWithin(Named<FrameworkElement>(page, name), page);
    }
    private static void AssertWithin(FrameworkElement element, FrameworkElement page)
    {
        var x = element.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).X;
        Assert.InRange(x, -1, page.ActualWidth + 1);
        Assert.True(x + element.ActualWidth <= page.ActualWidth + 1, "Batch control overflows horizontally: " + element.Name);
    }
    private static IEnumerable<DependencyObject> Flatten(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Flatten(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void AssertTexts(Brush? surface, IEnumerable<TextBlock> texts, Windows.UI.Color canvas)
    {
        var bg = Assert.IsType<SolidColorBrush>(surface);
        foreach (var text in texts.Where(text => !string.IsNullOrWhiteSpace(text.Text)))
        {
            var fg = Assert.IsType<SolidColorBrush>(text.Foreground);
            static (double R, double G, double B) Composite(Windows.UI.Color color, double opacity, (double R, double G, double B) under)
            {
                double alpha = color.A / 255d * opacity;
                return (color.R / 255d * alpha + under.R * (1 - alpha), color.G / 255d * alpha + under.G * (1 - alpha), color.B / 255d * alpha + under.B * (1 - alpha));
            }
            static double Luminance((double R, double G, double B) color)
            {
                static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            }
            var background = Composite(bg.Color, bg.Opacity, (canvas.R / 255d, canvas.G / 255d, canvas.B / 255d));
            var foreground = Composite(fg.Color, fg.Opacity, background);
            double a = Luminance(background), b = Luminance(foreground);
            Assert.True((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) >= 4.5, "Batch text contrast is insufficient: " + text.Text);
        }
    }
    private static void RequireIsolation()
    {
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(declared));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
        Assert.Equal(Path.GetFullPath(declared!), Path.GetFullPath(ConfigManager.GetDataDir()), ignoreCase: true);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(DataRoots.EffectiveTestRoot!, "app-center-batch-" + Guid.NewGuid().ToString("N"));
        internal RejectNetworkTransport Transport { get; } = new();
        private readonly HttpClient _http;
        private readonly CloudToolDefinition[] _tools;
        internal CloudToolManager Manager { get; }
        private static readonly byte[] Marker = "MZsynthetic-not-an-executable"u8.ToArray();
        internal Fixture()
        {
            CheckRoot();
            Directory.CreateDirectory(Root);
            _http = new(Transport);
            _tools = new[] { "alpha", "beta", "gamma" }.Select((name, index) => new CloudToolDefinition
            {
                Id = "batch-" + name, Name = "合成工具 " + name, Category = "系统工具", Version = "2.0",
                Packages = [new() { Architecture = "any", Url = "https://zhenxingai.com/downloads/tools/batch-" + name + ".zip",
                    SizeBytes = (index + 1L) * 1024 * 1024, Sha256 = new string('a', 64), EntryPoint = "app.exe" }],
            }).ToArray();
            foreach (var tool in _tools) WriteReceipt(tool.Id, tool.Id == "batch-beta" ? "2.0" : "1.0", new string('b', 64));
            string seed = Path.Combine(Root, "seed.json");
            File.WriteAllText(seed, JsonSerializer.Serialize(new CloudToolCatalog { Revision = 1,
                PublishedAt = "2026-10-07T01:00:00Z", MinClientVersion = "0.1.0", Tools = _tools }, CloudToolValidation.JsonOptions));
            Manager = new(Root, _http, CloudToolService.OwnEndpoint, UpdateService.CurrentArchitecture,
                new Version(0, 1, 1, 2), seed, Path.Combine(Root, "no-legacy-tools"), allowNetwork: false, isInUse: _ => false);
            Assert.Equal(3, Manager.GetStates().Count(state => state.IsManaged && state.HasUpdate));
        }
        internal void WriteReceipt(string id, string version, string sha)
        {
            var tool = _tools.Single(tool => tool.Id == id);
            var directory = Path.Combine(Root, "CloudTools", "Installed", id);
            CloudToolValidation.CheckNoReparse(directory);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "app.exe"), Marker);
            File.WriteAllText(Path.Combine(directory, CloudToolValidation.ReceiptFile), JsonSerializer.Serialize(new CloudToolReceipt
            {
                Id = id, Name = tool.Name, Version = version, Architecture = "any", Sha256 = sha, EntryPoint = "app.exe",
                PackageUrl = tool.Packages[0].Url, PackageSize = tool.Packages[0].SizeBytes,
                Files = new() { ["app.exe"] = Convert.ToHexString(SHA256.HashData(Marker)).ToLowerInvariant() },
            }, CloudToolValidation.JsonOptions));
        }
        internal void SetPending(string id, bool pending)
        {
            Assert.Contains(_tools, tool => tool.Id == id);
            string directory = Path.Combine(Root, "CloudTools", "Pending");
            CloudToolValidation.CheckNoReparse(directory);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, id + ".json");
            if (pending) File.WriteAllText(path, "{}");
            else if (File.Exists(path)) File.Delete(path);
        }
        private void CheckRoot()
        {
            Assert.StartsWith(Path.GetFullPath(DataRoots.EffectiveTestRoot!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            CloudToolValidation.CheckNoReparse(Root);
        }
        public void Dispose()
        {
            _http.Dispose();
            CheckRoot();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
    private sealed class RejectNetworkTransport : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("The native batch fixture must never request a network resource.");
        }
    }
}
