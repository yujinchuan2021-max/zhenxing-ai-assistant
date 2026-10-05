using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Controls;

namespace TubaWinUi3.Pages;

public sealed partial class SkillLibraryPage : Page
{
    private readonly ComboBox _categories = new() { MinWidth = 130 };
    private readonly ComboBox _readiness = new() { MinWidth = 170 };
    private readonly TextBox _search = new() { MinWidth = 180, MaxLength = 100 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _inventory = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _refresh = new();
    private readonly Button _retry = new() { Visibility = Visibility.Collapsed };
    private readonly Func<int, CancellationToken, Task<SkillCatalog>> _fetch;
    private SkillCatalogLoader _catalogue = new();
    private SkillCatalogueReadinessCounts _counts = new(0, 0, 0);
    private bool _loadFailed;
    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private SkillCatalogItem[] _items = [];
    private CancellationTokenSource? _stop, _renderStop, _loadStop;
    private bool _loading, _dialogOpen, _mounted, _localizing, _loadingAllPages, _fullIndexRequested, _renderPending;
    private const int DisplayPageSize = 100;
    private int _displayLimit = DisplayPageSize * 2;
    private readonly ObservableCollection<SkillLibraryCard> _shown = [];
    private SkillLibraryCard[] _filteredRows = [];
    private long _epoch, _renderVersion;
    private int _matchCount;
    private static string L(string key, string fallback) => LocalizationService.L("SkillHub_" + key, fallback);
    private SkillCatalogueReadiness SelectedReadiness => _readiness.SelectedItem is ComboBoxItem { Tag: SkillCatalogueReadiness mode }
        ? mode : SkillCatalogueReadiness.All;
    private bool HasFilter => _search.Text.Trim().Length > 0 || SelectedReadiness != SkillCatalogueReadiness.All ||
        ((_categories.SelectedItem as ComboBoxItem)?.Tag as string ?? "all") != "all";

    public SkillLibraryPage() : this(null) { }

    // The native regression host supplies an in-memory transport, never the live service.
    internal SkillLibraryPage(Func<int, CancellationToken, Task<SkillCatalog>>? fetch)
    {
        _fetch = fetch ?? ((page, token) => SkillHubClient.Official().CatalogueAsync(token, page));
        InitializeComponent();
        Cards.Layout = new SkillLibraryMasonryLayout
        {
            MinimumColumnWidth = 300, ColumnSpacing = 14, RowSpacing = 14,
            ItemHeightProvider = (item, width) => ((SkillLibraryCard)item).MeasureHeight(width),
        };
        Cards.ItemsSource = _shown;
        Header.Children.Add(_title); Header.Children.Add(_hint);
        var filters = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
        filters.Children.Add(_categories); filters.Children.Add(_readiness); filters.Children.Add(_search); filters.Children.Add(_refresh);
        var summary = new StackPanel { Spacing = 8 };
        summary.Children.Add(_status); summary.Children.Add(_inventory); summary.Children.Add(_retry);
        Header.Children.Add(filters); Header.Children.Add(summary);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Root.SizeChanged += (_, e) =>
        {
            filters.Orientation = e.NewSize.Width < 720 ? Orientation.Vertical : Orientation.Horizontal;
        };
        CardScroll.ViewChanged += async (_, _) => await LoadNearBottomAsync();
        _categories.SelectionChanged += (_, _) => { if (!_localizing) FilterChanged(); };
        _readiness.SelectionChanged += (_, _) => { if (!_localizing) FilterChanged(); };
        _search.TextChanged += (_, _) => { if (!_localizing) FilterChanged(delay: 180); };
        _refresh.Click += async (_, _) => await LoadAsync(refresh: true);
        _retry.Click += async (_, _) => await LoadAsync(allPages: HasFilter);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_mounted) return;
        _mounted = true;
        _epoch++;
        _stop = new();
        _loading = false; _loadingAllPages = false;
        _items = _catalogue.Items;
        _counts = SkillCatalogueReadinessFilter.Count(_items);
        LocalizationService.LanguageChanged += LanguageChanged;
        Localize();
        if (_catalogue.LoadedPages < 2 || HasFilter) await LoadAsync(initial: true, allPages: HasFilter);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _mounted = false;
        _epoch++;
        _renderVersion++;
        _stop?.Cancel(); _stop?.Dispose(); _stop = null;
        _loadStop?.Cancel();
        _fullIndexRequested = false;
        CancelRender();
        LocalizationService.LanguageChanged -= LanguageChanged;
    }

    private void FilterChanged(int delay = 0)
    {
        _displayLimit = DisplayPageSize * 2;
        _fullIndexRequested = HasFilter;
        if (!HasFilter && _loadingAllPages) _loadStop?.Cancel();
        ScheduleRender(delay);
        if (HasFilter) _ = LoadAsync(allPages: true);
    }

    private async Task LoadNearBottomAsync()
    {
        if (!_mounted || _loading || _renderPending || _loadFailed || _shown.Count < 2 ||
            CardScroll.ViewportHeight <= 0 || CardScroll.VerticalOffset + CardScroll.ViewportHeight < CardScroll.ExtentHeight - 220) return;
        if (_shown.Count < _filteredRows.Length)
        {
            _displayLimit += DisplayPageSize;
            AppendVisibleRows();
            UpdateSummary();
        }
        else if (!_catalogue.IsComplete)
        {
            _displayLimit += DisplayPageSize;
            await LoadAsync(allPages: HasFilter);
        }
    }

    private void LanguageChanged()
    {
        var epoch = _epoch;
        DispatcherQueue.TryEnqueue(() => { if (_mounted && epoch == _epoch) Localize(); });
    }

    private void Localize()
    {
        _localizing = true;
        try
        {
            _title.Text = L("Library", "官方技能库");
            _hint.Text = L("ReadinessHint", "按用途和加载条件筛选。可加载不代表已验证效果；GitHub 热度仅供参考。");
            AutomationProperties.SetName(_readiness, L("ReadinessFilter", "查看条件"));
            _search.PlaceholderText = L("Search", "搜索技能、用途或作者"); _refresh.Content = L("Refresh", "刷新");
            _retry.Content = L("Retry", "继续读取");
            string selected = (_categories.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            var readiness = SelectedReadiness;
            _categories.Items.Clear();
            foreach (var key in new[] { "all" }.Concat(SkillHubDocument.Categories))
            {
                var item = new ComboBoxItem { Tag = key, Content = Category(key) };
                _categories.Items.Add(item); if (key == selected) _categories.SelectedItem = item;
            }
            _readiness.Items.Clear();
            foreach (var mode in Enum.GetValues<SkillCatalogueReadiness>())
            {
                var item = new ComboBoxItem { Tag = mode, Content = mode switch {
                    SkillCatalogueReadiness.Loadable => L("ReadinessLoadable", "可直接加载"),
                    SkillCatalogueReadiness.Reference => L("ReadinessReference", "需要配套／参考"),
                    _ => L("ReadinessAll", "全部目录") } };
                _readiness.Items.Add(item); if (mode == readiness) _readiness.SelectedItem = item;
            }
        }
        finally { _localizing = false; }
        ScheduleRender();
    }

    private static string Category(string key) => L("Category_" + key, key switch {
        "all" => "全部分类", "development" => "开发", "design" => "设计", "writing" => "写作与营销", "office" => "办公", "data" => "数据与研究", "game" => "游戏", _ => "其他" });

    private async Task LoadAsync(bool refresh = false, bool initial = false, bool allPages = false)
    {
        if (allPages) _fullIndexRequested = true;
        if (_loading || !_mounted || _stop is null) return;
        if (refresh)
        {
            _catalogue = new(); _items = []; _counts = new(0, 0, 0);
            _displayLimit = DisplayPageSize * 2;
            initial = true; allPages = HasFilter;
            _shown.Clear(); _filteredRows = [];
            CardScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        if (_catalogue.IsComplete) { ScheduleRender(); return; }
        _loading = true; _loadFailed = false; _refresh.IsEnabled = _retry.IsEnabled = false;
        _loadingAllPages = allPages; _fullIndexRequested = false;
        using var loadStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _loadStop = loadStop;
        long epoch = _epoch; var token = loadStop.Token; var catalogue = _catalogue;
        int? pageLimit = allPages ? null : initial ? Math.Max(1, 2 - catalogue.LoadedPages) : 1;
        UpdateSummary();
        try
        {
            // Validation, metadata snapshots and filtering must not hold the XAML
            // thread. Progress only queues the latest immutable snapshot back to it.
            await Task.Run(() => catalogue.LoadAsync(_fetch, progress =>
            {
                var counts = SkillCatalogueReadinessFilter.Count(progress.Items);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_mounted || epoch != _epoch || token.IsCancellationRequested) return;
                    _items = progress.Items;
                    _counts = counts;
                    UpdateSummary();
                    ScheduleRender(delay: progress.IsComplete ? 0 : 60);
                });
            }, token, pageLimit), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || epoch != _epoch) { }
        catch (Exception) { if (_mounted && epoch == _epoch) _loadFailed = true; }
        finally
        {
            if (_mounted && epoch == _epoch)
            {
                _loading = false; _loadingAllPages = false;
                _items = catalogue.Items; // Also retain pages received immediately before cancellation.
                _counts = SkillCatalogueReadinessFilter.Count(_items);
                _refresh.IsEnabled = _retry.IsEnabled = true;
                UpdateSummary();
                ScheduleRender();
                if (_fullIndexRequested && HasFilter && !_catalogue.IsComplete && !_loadFailed)
                    await LoadAsync(allPages: true);
            }
            if (ReferenceEquals(_loadStop, loadStop)) _loadStop = null;
        }
    }

    private void UpdateSummary()
    {
        string count = string.Format(L("Count", "共 {0} 项 · 匹配 {1} 项 · 当前显示 {2} 项"),
            _items.Length, _matchCount, Math.Max(0, _shown.Count - 1));
        _inventory.Text = string.Format(L("ReadinessCounts", "已读取目录中：可加载 {0} 项 · 配套／参考 {1} 项"),
            _counts.Loadable, _counts.Reference);
        _status.Text = _loadFailed
            ? string.Format(L("PartialFailed", "读取中断，已保留 {0} 项。继续读取或刷新后可搜索全库。"), _items.Length)
            : !_catalogue.IsComplete
                ? HasFilter
                    ? string.Format(L("SearchingAll", "正在搜索全库，已读取 {0} 项…"), _items.Length)
                    : _loading
                        ? string.Format(L("LoadingPages", "正在加载技能，已读取 {0} 项…"), _items.Length)
                        : string.Format(L("BrowseMore", "已加载 {0} 项，下滑查看更多"), _items.Length)
                : count;
        _retry.Visibility = _loadFailed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelRender()
    {
        _renderStop?.Cancel();
        _renderStop?.Dispose();
        _renderStop = null;
    }

    private void ScheduleRender(int delay = 0)
    {
        if (!_mounted || _stop is null) return;
        CancelRender();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _renderStop = stop;
        _renderPending = true;
        _ = RenderAsync(++_renderVersion, _epoch, delay, stop.Token);
    }

    private async Task RenderAsync(long version, long epoch, int delay, CancellationToken token)
    {
        try
        {
            if (delay > 0) await Task.Delay(delay, token);
            token.ThrowIfCancellationRequested();
            var source = _items;
            string category = (_categories.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            string search = _search.Text.Trim();
            var readiness = SelectedReadiness;
            // Localized UI resource lookups stay on the UI thread, once per render.
            var categories = SkillHubDocument.Categories.ToDictionary(key => key, Category);
            string ready = L("TextReady", "可直接加载"), reference = L("ReadinessReference", "需要配套／参考");
            string noRating = L("NoRating", "暂无热度记录"), heat = L("RepositoryHeat", "仓库热度：");
            string unevaluated = L("Unevaluated", "待评测"), details = L("Details", "查看详情");
            var pinned = new SkillLibraryCard(new SkillCatalogItem { Id = AiAgentWorkflowSkill.Id,
                DisplayName = L("GoalName", "枕星目标助手"),
                Description = L("GoalDescription", "理解目标、复用已有工具，确认方案后准备环境，连接使用与排障。"), Kind = "builtin" },
                L("Pinned", "置顶 · 已内置"), "", "", details);
            var rows = await Task.Run(() =>
            {
                var matches = SkillCatalogueReadinessFilter.Filter(source, category, search, readiness, token);
                var result = new SkillLibraryCard[matches.Length + 1];
                result[0] = pinned;
                for (int index = 0; index < matches.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = matches[index];
                    var label = categories.GetValueOrDefault(item.Category ?? "", categories["other"]);
                    result[index + 1] = new(item, label + " · " + (item.CanInstall ? ready : reference),
                        item.Author + " · " + item.License,
                        heat + (item.StarDisplay ?? item.RepoStars?.ToString("N0") ?? noRating) + " · " + unevaluated, details);
                }
                return result;
            }, token);
            if (!_mounted || epoch != _epoch || version != _renderVersion || token.IsCancellationRequested) return;
            _matchCount = rows.Length - 1;
            _filteredRows = rows;
            bool prefixChanged = _shown.Take(Math.Min(_shown.Count, rows.Length))
                .Where((row, index) => !row.SameContent(rows[index])).Any();
            if (prefixChanged || _shown.Count > rows.Length)
            {
                _shown.Clear();
                CardScroll.ChangeView(null, 0, null, disableAnimation: true);
            }
            else _displayLimit = Math.Max(_displayLimit, _shown.Count - 1);
            AppendVisibleRows();
            UpdateSummary();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_mounted && epoch == _epoch && version == _renderVersion)
                _status.Text = L("DetailFailed", "暂时无法加载技能详情，请检查网络或已有技能文件后重试。");
        }
        finally { if (epoch == _epoch && version == _renderVersion) _renderPending = false; }
    }

    private void AppendVisibleRows()
    {
        var end = Math.Min(_filteredRows.Length, _displayLimit + 1);
        for (int index = _shown.Count; index < end; index++) _shown.Add(_filteredRows[index]);
    }

    private async void Details_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: SkillLibraryCard row }) await ShowDetailAsync(row.Item);
    }

    private async Task ShowDetailAsync(SkillCatalogItem row)
    {
        if (_dialogOpen || !IsLoaded || _stop is null) return;
        _dialogOpen = true; var epoch = _epoch;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        bool open = true;
        bool Current() => open && IsLoaded && epoch == _epoch && !stop.IsCancellationRequested;
        ContentDialog? dialog = null;
        try
        {
            var item = await SkillHubClient.Official().DetailAsync(row.Id, stop.Token);
            if (!Current()) return;
            var content = new StackPanel { Spacing = 12, Width = Math.Min(550, Math.Max(220, XamlRoot.Size.Width - 110)) };
            content.Children.Add(new TextBlock { Text = item.CanInstall
                ? L("LoadableBoundary", "可加载技能正文；运行效果与本机适配尚未验证。")
                : L("ReferenceBoundary", "参考条目：请查看来源与使用条件，当前不能直接加载。"),
                TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = item.Details, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = L("Evaluation", "收录状态：") + item.Evaluation, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            if (item.Requirements.Length > 0) content.Children.Add(new TextBlock { Text = L("Requirements", "使用条件：") + string.Join("；", item.Requirements), TextWrapping = TextWrapping.Wrap });
            foreach (var (caption, url) in new[] { (L("Source", "原作者与项目来源"), item.SourceUrl), (L("License", "许可说明"), item.LicenseUrl) })
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
                    content.Children.Add(new HyperlinkButton { Content = caption, NavigateUri = uri });
            if (item.CanInstall)
            {
                if (string.IsNullOrWhiteSpace(item.SystemPromptFragment) || string.IsNullOrWhiteSpace(item.LicenseText) ||
                    SkillHubDocument.Hash(item.SystemPromptFragment) != item.ContentSha256) throw new InvalidDataException("技能正文或许可无效。");
                content.Children.Add(new Expander { Header = L("ViewInstructions", "查看技能正文"), Content = new TextBlock { Text = item.SystemPromptFragment, TextWrapping = TextWrapping.Wrap } });
                content.Children.Add(new Expander { Header = L("ViewLicense", "查看原始许可"), Content = new TextBlock { Text = item.LicenseText, TextWrapping = TextWrapping.Wrap } });
            }
            dialog = new ContentDialog { Title = item.DisplayName, XamlRoot = XamlRoot, RequestedTheme = ThemeService.CurrentElementTheme,
                CloseButtonText = L("Close", "关闭"), PrimaryButtonText = item.CanInstall ? L("LoadSkill", "加载到本机") : "",
                Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(240, XamlRoot.Size.Height - 200),
                    HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            void Unload(object sender, RoutedEventArgs e) { stop.Cancel(); dialog.Hide(); }
            Unloaded += Unload;
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && Current())
                {
                    var draft = new SkillHubDraft("usr_catalog_" + SkillHubDocument.Hash(item.Id + item.ContentSha256)[..24],
                        item.DisplayName, item.Description, item.Category, item.SystemPromptFragment!, item.TriggerKeywords);
                    SkillHubLocalStore.SaveAndLoad(draft, item);
                    _status.Text = L("LibraryLoaded", "技能已加载，新会话默认启用；现有会话可在技能菜单勾选。");
                }
            }
            finally { Unloaded -= Unload; }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || epoch != _epoch || !IsLoaded) { }
        catch (Exception) { if (Current()) _status.Text = L("DetailFailed", "暂时无法加载技能详情，请检查网络或已有技能文件后重试。"); }
        finally { open = false; stop.Cancel(); _dialogOpen = false; }
    }
}

// Plain presentation data; creating thousands of these does not create XAML
// controls. ItemsRepeater realizes and recycles only the current viewport's cards.
public sealed class SkillLibraryCard
{
    internal SkillCatalogItem Item { get; }
    public string DisplayName => Item.DisplayName;
    public string Description => Item.Description;
    public string Badge { get; }
    public string AuthorCredit { get; }
    public string Heat { get; }
    public string DetailsLabel { get; }
    public Visibility DetailsVisibility => Item.Kind == "builtin" ? Visibility.Collapsed : Visibility.Visible;
    internal SkillLibraryCard(SkillCatalogItem item, string badge, string authorCredit, string heat, string detailsLabel)
    {
        Item = item; Badge = badge; AuthorCredit = authorCredit; Heat = heat; DetailsLabel = detailsLabel;
    }

    internal bool SameContent(SkillLibraryCard other) => Item == other.Item && Badge == other.Badge &&
        AuthorCredit == other.AuthorCredit && Heat == other.Heat && DetailsLabel == other.DetailsLabel;

    public double MeasureHeight(double width)
    {
        // CPU-only estimates give the virtualizing layout positions before it
        // creates any controls. Actual realized content can expand these bounds.
        var contentWidth = Math.Max(40, width - 38);
        static int Lines(string? text, double available, double size, int cap)
        {
            int count = 1; double used = 0;
            foreach (var character in text ?? "")
            {
                if (character == '\r') continue;
                double next = character >= 0x2e80 ? size : size * .6;
                if (character == '\n' || used + next > available)
                {
                    if (++count >= cap) return cap;
                    used = character == '\n' ? 0 : next;
                }
                else used += next;
            }
            return count;
        }
        double height = 38 + Lines(DisplayName, contentWidth, 18, 2) * 25 +
            Lines(Description, contentWidth, 14, 3) * 21 + 17 + 16;
        return Item.Kind == "builtin" ? height : height + 2 * 17 + 34 + 3 * 8;
    }
}
