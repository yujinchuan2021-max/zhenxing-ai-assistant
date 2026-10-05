using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Models;
using TubaWinUi3.Services;
using Windows.UI;

namespace TubaWinUi3.Pages;

public sealed partial class CommunityToolsPage : Page, ILocalizablePage
{
    private static string L(string key, string fallback) => LocalizationService.L(key, fallback);

    private List<CommunityTool> _allTools = [];
    private List<CommunityTool> _filteredTools = [];
    private string? _currentCategory;
    private string? _currentSearch;
    private CancellationTokenSource? _loadCts;

    public CommunityToolsPage()
    {
        InitializeComponent();

        CategoryFilter.SelectionChanged += CategoryFilter_SelectionChanged;
        Loaded += CommunityToolsPage_Loaded;
    }

    private bool _sourceReady;
    private bool _loadFailed;   // 真实失败状态：语言切换只翻译失败文案，不清成数量/空态
    private bool _isLoading;    // 真实加载状态：按当前请求 cts 归属维护（旧请求不得清除）

    private async void CommunityToolsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 先落标题/副标题/工具提示（不依赖网络；加载完成后再更新为带计数版本）。
        PageHeader.Title = LocalizationService.L("CommunityTools_Title", MiscTexts.T("社区"));
        PageHeader.Subtitle = L("CommunityTools_SubtitlePlain", MiscTexts.T("来自社区贡献的工具插件，下载安装即可使用。"));
        ToolTipService.SetToolTip(RefreshButton, L("CommunityTools_RefreshTooltip", MiscTexts.T("刷新")));

        _sourceReady = false;
        SourceSelector.SelectedIndex = CommunityToolService.CurrentSource == CommunityDataSource.GitCode ? 0 : 1;
        _sourceReady = true;
        await LoadToolsAsync();
    }

    private async void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_sourceReady) return;
        var newSource = SourceSelector.SelectedIndex == 1 ? CommunityDataSource.GitHub : CommunityDataSource.GitCode;
        if (newSource == CommunityToolService.CurrentSource) return;
        CommunityToolService.CurrentSource = newSource;
        CommunityToolService.InvalidateCache();
        await LoadToolsAsync();
    }

    private async Task LoadToolsAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        _loadFailed = false;
        _isLoading = true;
        LoadingProgress.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        ToolsGrid.Visibility = Visibility.Collapsed;

        try
        {
            var tools = await CommunityToolService.GetPluginsAsync(ct: cts.Token);

            if (cts.Token.IsCancellationRequested) return;

            _allTools = tools;

            foreach (var tool in _allTools)
            {
                tool.InstallStatus = CommunityToolService.CheckInstallStatus(tool);
                tool.LocalPath = CommunityToolService.GetLocalPath(tool);

                // 已安装的工具直接用本地元数据填充，不需要加载 plugin.json
                if (tool.InstallStatus == CommunityToolInstallStatus.Installed)
                {
                    ApplyLocalMetadata(tool);
                }
            }

            UpdateCategoryFilter();
            ApplyFilter();

            PageHeader.Title = LocalizationService.L("CommunityTools_Title", MiscTexts.T("社区"));
            PageHeader.Subtitle = string.Format(L("CommunityTools_SubtitleCount", MiscTexts.T("来自社区贡献的工具插件，下载安装即可使用。共 {0} 个")), _allTools.Count);
            StatusText.Text = _allTools.Count > 0 ? string.Format(L("CommunityTools_CountStatus", MiscTexts.T("共 {0} 个社区工具")), _allTools.Count) : L("CommunityTools_EmptyNone", MiscTexts.T("暂无社区工具"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 旧请求失败不得覆盖当前请求（归属检查与 finally 一致；取消场景不弹错）。
            if (!ReferenceEquals(_loadCts, cts) || cts.IsCancellationRequested) return;
            _loadFailed = true;
            ShowStatus(L("CommunityTools_LoadFailed", MiscTexts.T("加载失败")), ex.Message, InfoBarSeverity.Error);
            StatusText.Text = L("CommunityTools_LoadFailed", MiscTexts.T("加载失败"));

            var errDialog = new ContentDialog
            {
                Title = L("CommunityTools_LoadFailedTitle", MiscTexts.T("加载社区工具失败")),
                CloseButtonText = L("Common_OK", "确定"),
                XamlRoot = XamlRoot,
                RequestedTheme = ThemeService.CurrentElementTheme
            };
            errDialog.Resources["ContentDialogMaxWidth"] = 560;
            errDialog.Content = new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock
                {
                    Text = ex.InnerException?.Message ?? ex.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    IsTextSelectionEnabled = true
                }
            };
            await errDialog.ShowAsync();
        }
        finally
        {
            // 只有当前请求才能清除加载态；被替换的旧请求不得影响新请求的显示。
            if (ReferenceEquals(_loadCts, cts))
            {
                _isLoading = false;
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    public void ApplyLocalization()
    {
        // 语言切换：仅刷新显示与筛选标签；保留真实加载/失败状态、来源与筛选搜索，不重新下载目录。
        PageHeader.Title = LocalizationService.L("CommunityTools_Title", MiscTexts.T("社区"));
        ToolTipService.SetToolTip(RefreshButton, L("CommunityTools_RefreshTooltip", MiscTexts.T("刷新")));

        if (_loadFailed)
        {
            // 失败优先：错误对话框 await 期间 _isLoading 仍为 true，失败文案必须能刷新。
            StatusText.Text = L("CommunityTools_LoadFailed", MiscTexts.T("加载失败"));
            return;
        }

        if (_isLoading)
        {
            // 真实加载中：只刷新标题/提示，保留 LoadingProgress 与列表可见性，不重绘结果。
            PageHeader.Subtitle = L("CommunityTools_SubtitlePlain", MiscTexts.T("来自社区贡献的工具插件，下载安装即可使用。"));
            return;
        }

        // 已加载：按真实数据重绘（筛选/搜索由既有字段保持，ApplyFilter 不重置它们）。
        PageHeader.Subtitle = _allTools.Count > 0
            ? string.Format(L("CommunityTools_SubtitleCount", MiscTexts.T("来自社区贡献的工具插件，下载安装即可使用。共 {0} 个")), _allTools.Count)
            : L("CommunityTools_SubtitlePlain", MiscTexts.T("来自社区贡献的工具插件，下载安装即可使用。"));
        StatusText.Text = _allTools.Count > 0
            ? string.Format(L("CommunityTools_CountStatus", MiscTexts.T("共 {0} 个社区工具")), _allTools.Count)
            : L("CommunityTools_EmptyNone", MiscTexts.T("暂无社区工具"));
        UpdateCategoryFilter();
        ApplyFilter();
    }

    private void UpdateCategoryFilter()
    {
        var prevSelection = _currentCategory;
        CategoryFilter.SelectionChanged -= CategoryFilter_SelectionChanged;
        CategoryFilter.Items.Clear();
        CategoryFilter.Items.Add(new ComboBoxItem { Content = L("CommunityTools_AllCategories", "全部分类"), Tag = null });

        var categories = _allTools.Select(t => t.Category).Distinct().OrderBy(c => c).ToList();
        foreach (var cat in categories)
        {
            CategoryFilter.Items.Add(new ComboBoxItem { Content = MiscTexts.T(cat), Tag = cat });
        }

        if (prevSelection is not null && categories.Contains(prevSelection))
        {
            CategoryFilter.SelectedIndex = categories.IndexOf(prevSelection) + 1;
        }
        else
        {
            CategoryFilter.SelectedIndex = 0;
        }

        _currentCategory = CategoryFilter.SelectedIndex == 0 ? null : (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        CategoryFilter.SelectionChanged += CategoryFilter_SelectionChanged;
    }

    private void ApplyFilter()
    {
        _filteredTools = _allTools;

        if (_currentCategory is not null)
        {
            _filteredTools = _filteredTools.Where(t => t.Category == _currentCategory).ToList();
        }

        if (!string.IsNullOrWhiteSpace(_currentSearch))
        {
            var q = _currentSearch.Trim().ToLowerInvariant();
            _filteredTools = _filteredTools.Where(t =>
                t.Name.ToLowerInvariant().Contains(q) ||
                (t.Description?.ToLowerInvariant().Contains(q) == true) ||
                t.Tags.Any(tag => tag.ToLowerInvariant().Contains(q))
            ).ToList();
        }

        ToolsGrid.ItemsSource = _filteredTools;
        ToolsGrid.Visibility = _filteredTools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _filteredTools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_filteredTools.Count == 0 && _allTools.Count > 0)
        {
            EmptyStateText.Text = L("CommunityTools_NoMatch", MiscTexts.T("没有匹配的社区工具"));
            EmptySubmitLink.Visibility = Visibility.Collapsed;
        }
        else if (_allTools.Count == 0)
        {
            EmptyStateText.Text = L("CommunityTools_BeFirst", MiscTexts.T("成为第一个贡献者！"));
            EmptySubmitLink.Visibility = Visibility.Visible;
        }
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryFilter.SelectedIndex <= 0)
            _currentCategory = null;
        else
            _currentCategory = (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string;

        ApplyFilter();
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _currentSearch = sender.Text;
        ApplyFilter();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _currentSearch = args.QueryText;
        ApplyFilter();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        CommunityToolService.InvalidateCache();
        await LoadToolsAsync();
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowSubmitDialogAsync();
    }

    private async void ToolsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CommunityTool tool) return;

        // 已安装的工具直接打开，不需要加载详情
        if (tool.InstallStatus == CommunityToolInstallStatus.Installed)
        {
            CommunityToolService.LaunchPlugin(tool);
            return;
        }

        var detail = await LoadToolDetailAsync(tool);
        if (detail is not null)
            ShowToolDetailAsync(detail);
    }

    private async Task<CommunityTool?> LoadToolDetailAsync(CommunityTool tool)
    {
        LoadingProgress.Visibility = Visibility.Visible;

        try
        {
            var detail = await CommunityToolService.LoadToolDetailAsync(tool);
            if (detail is not null)
            {
                detail.InstallStatus = CommunityToolService.CheckInstallStatus(detail);
                detail.LocalPath = CommunityToolService.GetLocalPath(detail);
                if (GitHubAuthService.IsLoggedIn && !string.IsNullOrWhiteSpace(detail.Author))
                {
                    try
                    {
                        var user = await GitHubAuthService.GetCurrentUserAsync();
                        detail.IsAuthor = user is not null && string.Equals(user.Login, detail.Author, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { }
                }
            }
            return detail;
        }
        catch
        {
            return null;
        }
        finally
        {
            LoadingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static void ApplyLocalMetadata(CommunityTool tool)
    {
        // 【GUI 隔离】本地匹配同时看可写根（隔离态安装落点）与随包只读根：隔离安装的工具卡片
        // 必须能取到本地元数据/图标；随包 Tools 保持可见。
        var roots = new List<string>();
        var writable = ToolCatalog.WritableToolsRoot;
        if (!string.IsNullOrWhiteSpace(writable)) roots.Add(writable);
        var bundled = ToolCatalog.ToolsRoot;
        if (!string.IsNullOrWhiteSpace(bundled) &&
            !roots.Contains(bundled, StringComparer.OrdinalIgnoreCase)) roots.Add(bundled);
        if (roots.Count == 0) return;

        var localItems = ToolCatalog.GetTools(tool.Category);
        ToolItem? match = null;

        foreach (var item in localItems)
        {
            if (roots.Any(r => item.Path.StartsWith(
                    Path.Combine(r, tool.Category, tool.Id), StringComparison.OrdinalIgnoreCase)))
            {
                match = item;
                break;
            }
        }

        if (match is null) return;

        tool.Name = match.Name;
        tool.Description = match.Description;
        tool.Publisher = match.Publisher;
        tool.Version = match.Version;
        if (match.Tags.Count > 0) tool.Tags = match.Tags;

        var iconPath = ToolIconService.GetCachedIconPath(match.Path);
        if (!string.IsNullOrWhiteSpace(iconPath))
            tool.IconPath = iconPath;
    }

    private void ToolsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ToolsGrid.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
        {
            var padding = 56;
            wrapGrid.ItemWidth = Math.Max(220, (e.NewSize.Width - padding) / Math.Max(1, (int)((e.NewSize.Width - padding) / 280)));
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not CommunityTool tool) return;

        if (tool.InstallStatus == CommunityToolInstallStatus.Installed)
        {
            CommunityToolService.LaunchPlugin(tool);
            return;
        }

        // 卡片上的工具只是列表摘要（没有 plugin.json 里的下载源/作者信息），
        // 先加载详情再安装；详情已缓存时这一步是纯内存操作。
        var detail = await LoadToolDetailAsync(tool);
        await InstallToolAsync(detail ?? tool);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not CommunityTool tool) return;

        await DeleteToolAsync(tool);
    }

    private async Task DeleteToolAsync(CommunityTool tool)
    {
        var loggedIn = await GitHubAuthService.EnsureAuthenticatedAsync(XamlRoot);
        if (!loggedIn) return;

        var user = await GitHubAuthService.GetCurrentUserAsync();
        if (user is null || !string.Equals(user.Login, tool.Author, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync(L("CommunityTools_CannotDelete", MiscTexts.T("无法删除")), L("CommunityTools_OnlyOwn", MiscTexts.T("只能删除自己。")));
            return;
        }

        var confirmDialog = new ContentDialog
        {
            Title = string.Format(L("CommunityTools_DeleteToolTitle", MiscTexts.T("删除工具「{0}」")), tool.Name),
            PrimaryButtonText = L("Common_ConfirmDelete", MiscTexts.T("确认删除")),
            CloseButtonText = L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        confirmDialog.Resources["ContentDialogMaxWidth"] = 480;

        var confirmStack = new StackPanel { Spacing = 12 };

        var warningBorder = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Color.FromArgb(25, 255, 68, 68)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 68, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.Format(L("CommunityTools_DeleteConfirm", MiscTexts.T("确定要删除「{0}」吗？")), tool.Name),
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold
                    },
                    new TextBlock
                    {
                        Text = L("CommunityTools_DeleteExplain", MiscTexts.T("此操作将创建一个删除 Pull Request，审核通过后工具将从社区中移除。已安装的用户不受影响。")),
                        FontSize = 13,
                        Opacity = 0.8,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        confirmStack.Children.Add(warningBorder);

        confirmDialog.Content = confirmStack;

        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var progressDialog = new ContentDialog
        {
            Title = string.Format(L("CommunityTools_DeletingTitle", MiscTexts.T("正在删除「{0}」")), tool.Name),
            CloseButtonText = L("Common_Cancel", "取消"),
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        progressDialog.Resources["ContentDialogMaxWidth"] = 480;

        var progressText = new TextBlock
        {
            Text = L("CommunityTools_Preparing", MiscTexts.T("准备中...")),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        var progressBar = new ProgressBar { IsIndeterminate = true };

        progressDialog.Content = new StackPanel
        {
            Spacing = 8,
            Children = { progressText, progressBar }
        };

        var progressCts = new CancellationTokenSource();
        var deleteTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        progressDialog.CloseButtonClick += (s, e) =>
        {
            progressCts.Cancel();
            deleteTcs.TrySetResult(null);
        };

        var progress = new Progress<string>(msg =>
        {
            DispatcherQueue.TryEnqueue(() => { progressText.Text = msg; });
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var prUrl = await CommunityToolService.DeletePluginAsync(tool, progress, progressCts.Token);
                deleteTcs.TrySetResult(prUrl);
            }
            catch (OperationCanceledException)
            {
                deleteTcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                deleteTcs.TrySetException(ex);
            }
        }, progressCts.Token);

        _ = Task.Run(async () =>
        {
            try
            {
                var prUrl = await deleteTcs.Task;
                DispatcherQueue.TryEnqueue(() =>
                {
                    progressDialog.Hide();

                    if (prUrl is not null)
                    {
                        _ = ShowDeleteResultAsync(prUrl);
                    }
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    progressDialog.Hide();
                    _ = ShowDeleteErrorAsync(ex);
                });
            }
        });

        await progressDialog.ShowAsync();
    }

    private async Task ShowDeleteResultAsync(string prUrl)
    {
        var dialog = new ContentDialog
        {
            Title = L("CommunityTools_DeleteSubmitted", MiscTexts.T("删除请求已提交")),
            CloseButtonText = L("Common_OK", "确定"),
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        dialog.Resources["ContentDialogMaxWidth"] = 480;

        var stack = new StackPanel { Spacing = 12 };

        stack.Children.Add(new TextBlock
        {
            Text = L("CommunityTools_DeleteSubmittedBody", MiscTexts.T("删除 Pull Request 已创建，审核通过后工具将从社区中移除。")),
            TextWrapping = TextWrapping.Wrap
        });

        try
        {
            stack.Children.Add(new HyperlinkButton
            {
                Content = L("CommunityTools_ViewPr", MiscTexts.T("查看 Pull Request")),
                NavigateUri = new Uri(prUrl)
            });
        }
        catch { }

        dialog.Content = stack;
        await dialog.ShowAsync();

        CommunityToolService.InvalidateCache();
        await LoadToolsAsync();
    }

    private async Task ShowDeleteErrorAsync(Exception ex)
    {
        var dialog = new ContentDialog
        {
            Title = L("CommunityTools_DeleteFailed", MiscTexts.T("删除失败")),
            CloseButtonText = L("Common_OK", "确定"),
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        dialog.Resources["ContentDialogMaxWidth"] = 560;
        dialog.Content = new ScrollViewer
        {
            MaxHeight = 300,
            Content = new TextBlock
            {
                Text = ex.InnerException?.Message ?? ex.Message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                IsTextSelectionEnabled = true
            }
        };
        await dialog.ShowAsync();
    }

    private async Task InstallToolAsync(CommunityTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.DownloadUrl) && string.IsNullOrWhiteSpace(tool.File))
        {
            // 兜底：摘要对象缺少下载源时再试一次加载详情（正常入口已加载，命中缓存）
            var detail = await CommunityToolService.LoadToolDetailAsync(tool);
            if (detail is not null) tool = detail;
        }

        if (string.IsNullOrWhiteSpace(tool.DownloadUrl) && string.IsNullOrWhiteSpace(tool.File))
        {
            ShowStatus(L("CommunityTools_CannotDownload", MiscTexts.T("无法下载")), L("CommunityTools_NoDownloadSource", MiscTexts.T("该工具没有提供下载源")), InfoBarSeverity.Warning);
            return;
        }

        var authorName = tool.Author ?? L("Common_UnknownUser", MiscTexts.T("未知用户"));
        var versionText = tool.Version ?? L("Common_Unknown", MiscTexts.T("未知"));

        var confirmDialog = new ContentDialog
        {
            Title = string.Format(L("CommunityTools_DownloadTitle", MiscTexts.T("下载 {0}")), tool.Name),
            PrimaryButtonText = string.Format(L("CommunityTools_TrustDownload", MiscTexts.T("我信任 {0}，开始下载")), authorName),
            CloseButtonText = L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        confirmDialog.Resources["ContentDialogMaxWidth"] = 480;

        var confirmStack = new StackPanel { Spacing = 12 };

        var infoBorder = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = tool.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                    new TextBlock { Text = tool.Description ?? L("CommunityTools_NoDescription", MiscTexts.T("无描述")), FontSize = 13, Opacity = 0.7, TextWrapping = TextWrapping.Wrap }
                }
            }
        };
        confirmStack.Children.Add(infoBorder);
        ((StackPanel)infoBorder.Child).Children.Add(new TextBlock
        {
            Text = string.Format(L("CommunityTools_MetaLine", MiscTexts.T("分类：{0}  ·  版本：{1}  ·  提交者：{2}")), tool.Category, versionText, authorName),
            FontSize = 12,
            Opacity = 0.6
        });

        var warningIcon = new FontIcon { Glyph = "\uE7BA", FontSize = 14 };
        warningIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 68, 68));

        var warningText = new TextBlock
        {
            Text = string.Format(L("CommunityTools_RiskNotice", MiscTexts.T("社区包无法保证其安全性，枕星图吧AI助手不对社区包负责，但会尽量避免违规工具。如果你信任 {0} 可以开始下载。")), authorName),
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var warningStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        warningStack.Children.Add(warningIcon);
        warningStack.Children.Add(warningText);

        var warningBorder = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Color.FromArgb(25, 255, 68, 68)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 68, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = warningStack
        };
        confirmStack.Children.Add(warningBorder);

        var downloadWindow = new GitHubDownloadWindow(tool);
        downloadWindow.DownloadSucceeded += () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                tool.InstallStatus = CommunityToolInstallStatus.Installed;
                tool.LocalPath = CommunityToolService.GetLocalPath(tool);
                CommunityToolService.InvalidateCache();
                _ = LoadToolsAsync();
            });
        };
        downloadWindow.Activate();
    }

    private async void ShowToolDetailAsync(CommunityTool tool)
    {
        var dialog = new ContentDialog
        {
            Title = tool.Name,
            CloseButtonText = L("Common_Close", "关闭"),
            PrimaryButtonText = tool.InstallStatus == CommunityToolInstallStatus.Installed ? L("CommunityTools_Open", "打开") : (tool.CanInstall ? tool.LaunchButtonText : L("Common_Close", "关闭")),
            SecondaryButtonText = tool.CanDelete ? L("CommunityTools_Delete", MiscTexts.T("删除")) : null,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        dialog.Resources["ContentDialogMaxWidth"] = 500;

        var stack = new StackPanel { Spacing = 12 };

        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            stack.Children.Add(new TextBlock { Text = tool.Description, TextWrapping = TextWrapping.Wrap });
        }

        var infoGrid = new Grid
        {
            RowSpacing = 6,
            ColumnSpacing = 12
        };
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = new (string Label, string Value)[]
        {
            (L("CommunityTools_LabelCategory", MiscTexts.T("分类")), tool.Category),
            (L("CommunityTools_LabelVersion", MiscTexts.T("版本")), tool.Version ?? L("Common_Unknown", MiscTexts.T("未知"))),
            (L("CommunityTools_LabelPublisher", MiscTexts.T("发布者")), tool.Publisher ?? L("Common_Unknown", MiscTexts.T("未知"))),
            (L("CommunityTools_LabelSubmitter", MiscTexts.T("提交者")), tool.Author ?? L("Common_Unknown", MiscTexts.T("未知"))),
            (L("CommunityTools_LabelTags", MiscTexts.T("标签")), tool.TagsText),
            (L("CommunityTools_LabelStatus", MiscTexts.T("状态")), tool.InstallStatusText),
            (L("CommunityTools_LabelHomepage", MiscTexts.T("官网")), tool.Homepage ?? L("Common_None", MiscTexts.T("无"))),
        };

        for (var i = 0; i < rows.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(rows[i].Value) || rows[i].Value == L("Common_Unknown", MiscTexts.T("未知"))) continue;
            var rowDef = new RowDefinition { Height = GridLength.Auto };
            infoGrid.RowDefinitions.Add(rowDef);

            var label = new TextBlock
            {
                Text = rows[i].Label,
                FontSize = 13,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            infoGrid.Children.Add(label);

            if (rows[i].Label == L("CommunityTools_LabelHomepage", MiscTexts.T("官网")) && rows[i].Value != "无")
            {
                var link = new HyperlinkButton
                {
                    Content = new TextBlock { Text = rows[i].Value, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 },
                    NavigateUri = Uri.TryCreate(rows[i].Value, UriKind.Absolute, out var uri) ? uri : null,
                    Padding = new Thickness(0)
                };
                Grid.SetRow(link, i);
                Grid.SetColumn(link, 1);
                infoGrid.Children.Add(link);
            }
            else
            {
                var val = new TextBlock
                {
                    Text = rows[i].Value,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(val, i);
                Grid.SetColumn(val, 1);
                infoGrid.Children.Add(val);
            }
        }

        stack.Children.Add(infoGrid);

        if (!string.IsNullOrWhiteSpace(tool.Homepage))
        {
            try
            {
                stack.Children.Add(new HyperlinkButton
                {
                    Content = L("CommunityTools_ViewHomepage", MiscTexts.T("查看项目主页")),
                    NavigateUri = new Uri(tool.Homepage)
                });
            }
            catch { }
        }

        dialog.Content = stack;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (tool.InstallStatus == CommunityToolInstallStatus.Installed)
                CommunityToolService.LaunchPlugin(tool);
            else if (tool.CanInstall)
                await InstallToolAsync(tool);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await DeleteToolAsync(tool);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    private async Task ShowSubmitDialogAsync()
    {
        var loggedIn = await GitHubAuthService.EnsureAuthenticatedAsync(XamlRoot);
        if (!loggedIn) return;

        var user = await GitHubAuthService.GetCurrentUserAsync();

        var methodRadio = new RadioButtons
        {
            Header = L("CommunityTools_UploadMethod", MiscTexts.T("上传方式")),
            ItemsSource = new[] { L("CommunityTools_UploadZip", "上传压缩包"), L("CommunityTools_UploadLink", "提供下载链接") },
            SelectedIndex = 0
        };

        string? packagePath = null;
        IReadOnlyList<ImportableExecutable>? executables = null;
        FileInfo? fileInfo = null;

        var primaryComboBox = new ComboBox
        {
            Header = L("CommunityTools_MainProgram", MiscTexts.T("主程序")),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var nameBox = new TextBox
        {
            Header = L("CommunityTools_ToolName", MiscTexts.T("工具名称")),
            PlaceholderText = L("CommunityTools_ToolNamePh", MiscTexts.T("例如 CPU-Z"))
        };

        var variantsList = new ListView
        {
            Header = L("CommunityTools_MultiArch", MiscTexts.T("多架构文件（可选）")),
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 150
        };

        var packageInfo = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = L("CommunityTools_NoFile", MiscTexts.T("未选择文件")), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                    new TextBlock { Text = "", FontSize = 12, Opacity = 0.6 }
                }
            }
        };

        var packagePickButton = new Button
        {
            Content = L("CommunityTools_ChooseZip", MiscTexts.T("选择压缩包")),
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Visible
        };

        var downloadUrlBox = new TextBox
        {
            Header = L("CommunityTools_LinkHeader", MiscTexts.T("下载链接")),
            PlaceholderText = L("CommunityTools_LinkPh", MiscTexts.T("例如 https://example.com/tool.zip 或 gh:owner/repo"))
        };

        var downloadFilterBox = new TextBox
        {
            Header = L("CommunityTools_FilterHeader", MiscTexts.T("下载筛选（可选）")),
            PlaceholderText = L("CommunityTools_FilterPh", MiscTexts.T("例如 *.exe 用于 gh: 链接"))
        };

        var verifyButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { FontSize = 12, Glyph = "\uE72C" },
                    new TextBlock { Text = L("CommunityTools_VerifyDownload", MiscTexts.T("验证并下载")) }
                }
            },
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(6)
        };

        var verifyProgress = new ProgressBar
        {
            IsIndeterminate = true,
            Visibility = Visibility.Collapsed
        };

        var verifyStatusText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.68,
            Visibility = Visibility.Collapsed
        };

        var downloadUrlTip = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Color.FromArgb(25, 96, 165, 250)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 96, 165, 250)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE946", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250)) },
                    new TextBlock
                    {
                        Text = L("CommunityTools_VerifyHint", MiscTexts.T("输入下载链接后点击「验证并下载」，下载完成后可查看并选择 exe 文件。")),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        var downloadedFileInfo = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = L("CommunityTools_NotDownloaded", MiscTexts.T("未下载")), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                    new TextBlock { Text = "", FontSize = 12, Opacity = 0.6 }
                }
            }
        };

        string? downloadedPackagePath = null;

        var zipSection = new StackPanel
        {
            Spacing = 8,
            Visibility = Visibility.Visible,
            Children = { packagePickButton, packageInfo }
        };

        var urlSection = new StackPanel
        {
            Spacing = 8,
            Visibility = Visibility.Collapsed,
            Children = { downloadUrlTip, downloadUrlBox, downloadFilterBox, verifyButton, verifyProgress, verifyStatusText, downloadedFileInfo }
        };

        methodRadio.SelectionChanged += (_, _) =>
        {
            var isZip = methodRadio.SelectedIndex == 0;
            zipSection.Visibility = isZip ? Visibility.Visible : Visibility.Collapsed;
            urlSection.Visibility = isZip ? Visibility.Collapsed : Visibility.Visible;
        };

        verifyButton.Click += async (_, _) =>
        {
            var url = downloadUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                verifyStatusText.Visibility = Visibility.Visible;
                verifyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 68, 68));
                verifyStatusText.Text = L("CommunityTools_EnterLink", MiscTexts.T("请输入下载链接"));
                return;
            }

            verifyButton.IsEnabled = false;
            verifyProgress.Visibility = Visibility.Visible;
            verifyProgress.IsIndeterminate = true;
            verifyStatusText.Visibility = Visibility.Visible;
            verifyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 68, 68));
            verifyStatusText.Text = L("CommunityTools_Resolving", MiscTexts.T("正在解析下载链接..."));
            downloadedFileInfo.Visibility = Visibility.Collapsed;

            try
            {
                var filter = downloadFilterBox.Text.Trim();
                string resolvedUrl;
                string resolvedFileName;
                long resolvedSize;

                if (url.StartsWith("gh:", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var downloadInfo = await ToolDownloaderService.ResolveDownloadUrlAsync(url, string.IsNullOrWhiteSpace(filter) ? null : filter);
                    if (downloadInfo is null)
                    {
                        verifyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 68, 68));
                        verifyStatusText.Text = L("CommunityTools_ResolveFailed", MiscTexts.T("无法解析链接，请检查链接是否正确"));
                        return;
                    }
                    resolvedUrl = downloadInfo.DownloadUrl;
                    resolvedFileName = downloadInfo.FileName;
                    resolvedSize = downloadInfo.Size;
                }
                else
                {
                    resolvedUrl = url;
                    resolvedFileName = Path.GetFileName(new Uri(url).AbsolutePath);
                    if (string.IsNullOrWhiteSpace(resolvedFileName) || resolvedFileName.Contains('?'))
                        resolvedFileName = "download";
                    resolvedSize = 0;
                }

                verifyStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                verifyStatusText.Text = string.Format(L("CommunityTools_Downloading", MiscTexts.T("正在下载 {0}...")), resolvedFileName);
                verifyProgress.IsIndeterminate = false;
                verifyProgress.Value = 0;

                var tempDir = Path.Combine(Path.GetTempPath(), $"TubaCommunityVerify_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                var progress = new Progress<ToolDownloadProgress>(p =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (p.Percentage > 0) verifyProgress.Value = p.Percentage;
                        verifyStatusText.Text = string.Format(L("CommunityTools_DownloadingProgress", MiscTexts.T("正在下载... {0:F0}%  {1}")), p.Percentage, ToolDownloaderService.FormatSpeed(p.SpeedMbps));
                    });
                });

                var filePath = await ToolDownloaderService.DownloadToFileAsync(resolvedUrl, tempDir, resolvedFileName, progress);

                verifyStatusText.Text = L("CommunityTools_Checking", MiscTexts.T("正在检查文件..."));

                var isArchive = filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                filePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

                if (isArchive)
                {
                    var extractDir = Path.Combine(tempDir, "extracted");
                    Directory.CreateDirectory(extractDir);
                    await ToolDownloaderService.ExtractArchiveAsync(filePath, extractDir);
                    var exes = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                        .Select(f => new ImportableExecutable(f.Substring(extractDir.Length + 1).Replace('\\', '/')))
                        .OrderBy(e => e.EntryPath, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    if (exes.Count == 0)
                    {
                        verifyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 68, 68));
                        verifyStatusText.Text = L("CommunityTools_NoExeInZip", MiscTexts.T("下载的压缩包里没有 .exe 文件"));
                        return;
                    }

                    downloadedPackagePath = filePath;
                    executables = exes;
                    fileInfo = null;

                    primaryComboBox.ItemsSource = exes;
                    primaryComboBox.SelectedIndex = 0;
                    variantsList.ItemsSource = exes;
                    nameBox.Text = Path.GetFileNameWithoutExtension(exes[0].FileName);

                    var innerStack = (StackPanel)downloadedFileInfo.Child;
                    ((TextBlock)innerStack.Children[0]).Text = resolvedFileName;
                    ((TextBlock)innerStack.Children[1]).Text = string.Format(L("CommunityTools_ExeCount", MiscTexts.T("{0} 个可执行文件")), exes.Count);
                    downloadedFileInfo.Visibility = Visibility.Visible;

                    verifyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128));
                    verifyStatusText.Text = string.Format(L("CommunityTools_DownloadDoneCount", MiscTexts.T("下载完成，发现 {0} 个可执行文件")), exes.Count);
                }
                else
                {
                    downloadedPackagePath = filePath;
                    packagePath = filePath;
                    executables = [new ImportableExecutable(resolvedFileName)];
                    fileInfo = new FileInfo(filePath);

                    primaryComboBox.ItemsSource = executables;
                    primaryComboBox.SelectedIndex = 0;
                    variantsList.ItemsSource = executables;
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                        nameBox.Text = Path.GetFileNameWithoutExtension(resolvedFileName);

                    var innerStack = (StackPanel)downloadedFileInfo.Child;
                    ((TextBlock)innerStack.Children[0]).Text = resolvedFileName;
                    ((TextBlock)innerStack.Children[1]).Text = FormatSize(new FileInfo(filePath).Length);
                    downloadedFileInfo.Visibility = Visibility.Visible;

                    verifyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128));
                    verifyStatusText.Text = L("CommunityTools_DownloadDone", MiscTexts.T("下载完成"));
                }
            }
            catch (Exception ex)
            {
                verifyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 68, 68));
                verifyStatusText.Text = string.Format(L("CommunityTools_VerifyFailed", MiscTexts.T("验证失败: {0}")), ex.InnerException?.Message ?? ex.Message);
            }
            finally
            {
                verifyButton.IsEnabled = true;
                verifyProgress.Visibility = Visibility.Collapsed;
            }
        };

        packagePickButton.Click += (s, e) =>
        {
            var pkgOfn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow),
                lpstrFilter = L("CommunityTools_ZipFilterName", MiscTexts.T("压缩包")) + "\0*.zip\0" + L("CommunityTools_FilterAllFiles", MiscTexts.T("所有文件")) + "\0*.*\0\0",
                lpstrFile = new string(new char[1024]),
                nMaxFile = 1024,
                lpstrTitle = L("CommunityTools_ZipDialogTitle", MiscTexts.T("选择工具压缩包")),
                Flags = OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR,
                nFilterIndex = 1
            };

            if (!GetOpenFileName(ref pkgOfn)) return;

            var picked = pkgOfn.lpstrFile.TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(picked)) return;

            var fi = new FileInfo(picked);
            if (fi.Length > CommunityToolService.MaxUploadSizeBytes)
            {
                _ = ShowMessageAsync(L("CommunityTools_FileTooLargeTitle", MiscTexts.T("文件过大")), string.Format(L("CommunityTools_FileTooLargeBody", MiscTexts.T("压缩包大小不能超过 {0} MB。\n当前文件：{1}")), CommunityToolService.MaxUploadSizeBytes / 1024 / 1024, FormatSize(fi.Length)));
                return;
            }

            var exes = CustomToolPackageService.GetExecutables(picked);
            if (exes.Count == 0)
            {
                _ = ShowMessageAsync(L("CommunityTools_NoExeFileTitle", MiscTexts.T("未找到可执行文件")), L("CommunityTools_NeedExeInZip", MiscTexts.T("压缩包里需要至少包含一个 .exe 文件。")));
                return;
            }

            packagePath = picked;
            executables = exes;
            fileInfo = fi;

            var innerStack = (StackPanel)packageInfo.Child;
            ((TextBlock)innerStack.Children[0]).Text = Path.GetFileName(picked);
            ((TextBlock)innerStack.Children[1]).Text = string.Format(L("CommunityTools_UploadPreview", MiscTexts.T("{0}  ·  {1} 个可执行文件")), FormatSize(fi.Length), exes.Count);
            packageInfo.Visibility = Visibility.Visible;

            primaryComboBox.ItemsSource = exes;
            primaryComboBox.SelectedIndex = 0;
            variantsList.ItemsSource = exes;
            nameBox.Text = Path.GetFileNameWithoutExtension(exes[0].FileName);
        };

        var categoryComboBox = new ComboBox
        {
            Header = L("CommunityTools_FormCategoryHeader", MiscTexts.T("分类")),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var existingCategories = ToolCatalog.GetCategories();
        var standardCategories = new[] { "处理器工具", "显卡工具", "内存工具", "硬盘工具", "显示器工具", "声卡工具", "网卡工具", "外设工具", "综合工具", "系统工具", "游戏工具", "其他工具" };
        var allCategories = existingCategories.Concat(standardCategories).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(c => c).ToList();
        foreach (var cat in allCategories)
            categoryComboBox.Items.Add(new ComboBoxItem { Content = MiscTexts.T(cat), Tag = cat });
        categoryComboBox.SelectedIndex = 0;

        var descBox = new TextBox
        {
            Header = L("CommunityTools_Intro", MiscTexts.T("简介")),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            PlaceholderText = L("CommunityTools_IntroPh", MiscTexts.T("输入工具用途、特点或注意事项"))
        };

        var publisherBox = new TextBox
        {
            Header = L("CommunityTools_Author", MiscTexts.T("作者/发布者")),
            PlaceholderText = L("CommunityTools_Optional", MiscTexts.T("可选"))
        };

        var tagsBox = new TextBox
        {
            Header = L("CommunityTools_FormTagsHeader", MiscTexts.T("标签")),
            PlaceholderText = L("CommunityTools_TagsPh", MiscTexts.T("用逗号分隔，例如 CPU, 跑分, 稳定性测试"))
        };

        var launchTargetBox = new TextBox
        {
            Header = L("CommunityTools_LaunchTarget", MiscTexts.T("启动目标")),
            PlaceholderText = L("CommunityTools_LaunchTargetPh", MiscTexts.T("例如 cpuz.exe（可选，默认使用主程序）"))
        };

        var homepageBox = new TextBox
        {
            Header = L("CommunityTools_OfficialSite", MiscTexts.T("官方网站")),
            PlaceholderText = L("CommunityTools_HomepagePh", MiscTexts.T("https://...（可选）"))
        };

        var versionBox = new TextBox
        {
            Header = L("CommunityTools_VersionNumberHeader", MiscTexts.T("版本号")),
            PlaceholderText = L("CommunityTools_VersionPh", MiscTexts.T("例如 2.09（可选，默认 1.0）"))
        };

        string? iconFilePath = null;
        var iconPreview = new Border
        {
            Width = 48,
            Height = 48,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Child = new FontIcon { Glyph = "\uE8B7", FontSize = 24, Opacity = 0.5 }
        };
        var iconText = new TextBlock
        {
            Text = L("CommunityTools_NoIcon", MiscTexts.T("未选择图标")),
            Opacity = 0.6,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconPickButton = new Button
        {
            Content = L("CommunityTools_ChooseIcon", MiscTexts.T("选择图标")),
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6)
        };
        iconPickButton.Click += (s, e) =>
        {
            var iconOfn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow),
                lpstrFilter = L("CommunityTools_IconFilterName", MiscTexts.T("图标文件")) + "\0*.png;*.ico;*.jpg;*.bmp\0" + L("CommunityTools_FilterAllFiles", MiscTexts.T("所有文件")) + "\0*.*\0\0",
                lpstrFile = new string(new char[1024]),
                nMaxFile = 1024,
                lpstrTitle = L("CommunityTools_IconDialogTitle", MiscTexts.T("选择工具图标")),
                Flags = OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR,
                nFilterIndex = 1
            };
            if (GetOpenFileName(ref iconOfn))
            {
                var picked = iconOfn.lpstrFile.TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(picked) && File.Exists(picked))
                {
                    iconFilePath = picked;
                    iconText.Text = Path.GetFileName(picked);
                    try
                    {
                        iconPreview.Child = new Image
                        {
                            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(picked)),
                            Stretch = Stretch.Uniform
                        };
                    }
                    catch { }
                }
            }
        };

        var loginInfo = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uEC61", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128)) },
                    new TextBlock { Text = string.Format(L("CommunityTools_LoggedIn", MiscTexts.T("已登录：{0}")), user?.Login ?? L("Common_Unknown", MiscTexts.T("未知"))), FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };

        var content = new ScrollViewer
        {
            MaxHeight = 620,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    loginInfo,
                    methodRadio,
                    zipSection,
                    urlSection,
                    nameBox,
                    categoryComboBox,
                    new TextBlock { Text = L("CommunityTools_MainProgramLabel", MiscTexts.T("主程序")), Opacity = 0.68, FontSize = 12 },
                    primaryComboBox,
                    variantsList,
                    new TextBlock { Text = L("CommunityTools_IconOptional", MiscTexts.T("工具图标（可选）")), Opacity = 0.68, FontSize = 12 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { iconPreview, iconText, iconPickButton } },
                    descBox,
                    publisherBox,
                    tagsBox,
                    launchTargetBox,
                    homepageBox,
                    versionBox
                }
            }
        };

        var dialog = new ContentDialog
        {
            Title = L("CommunityTools_SubmitTitle", MiscTexts.T("提交社区工具")),
            Content = content,
            PrimaryButtonText = L("CommunityTools_PreviewSubmit", MiscTexts.T("预览并提交")),
            CloseButtonText = L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        dialog.Resources["ContentDialogMaxWidth"] = 560;

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary) return;

        var isZipMode = methodRadio.SelectedIndex == 0;

        if (isZipMode && (string.IsNullOrWhiteSpace(packagePath) || executables is null || executables.Count == 0))
        {
            await ShowMessageAsync(L("CommunityTools_NeedZipTitle", MiscTexts.T("请选择压缩包")), L("CommunityTools_NeedZipBody", MiscTexts.T("上传压缩包模式下需要选择一个包含 exe 的压缩包。")));
            return;
        }

        if (!isZipMode && string.IsNullOrWhiteSpace(downloadUrlBox.Text))
        {
            await ShowMessageAsync(L("CommunityTools_NeedLinkTitle", MiscTexts.T("请填写下载链接")), L("CommunityTools_NeedLinkBody", MiscTexts.T("下载链接模式下需要提供下载地址。")));
            return;
        }

        if (!isZipMode && string.IsNullOrWhiteSpace(downloadedPackagePath))
        {
            await ShowMessageAsync(L("CommunityTools_VerifyFirstTitle", MiscTexts.T("请先验证链接")), L("CommunityTools_VerifyFirstBody", MiscTexts.T("请点击「验证并下载」确认链接可用后再提交。")));
            return;
        }

        if (string.IsNullOrWhiteSpace(nameBox.Text))
        {
            await ShowMessageAsync(L("CommunityTools_NeedNameTitle", MiscTexts.T("请填写工具名称")), L("CommunityTools_NeedNameBody", MiscTexts.T("工具名称是必填项。")));
            return;
        }

        var category = (categoryComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "其他工具";

        ImportableExecutable? primary = primaryComboBox.SelectedItem as ImportableExecutable;
        if (isZipMode && primary is null)
        {
            await ShowMessageAsync(L("CommunityTools_NeedMainTitle", MiscTexts.T("请选择主程序")), L("CommunityTools_NeedMainBody", MiscTexts.T("需要指定一个 exe 作为打开工具时运行的主程序。")));
            return;
        }

        var launchTarget = string.IsNullOrWhiteSpace(launchTargetBox.Text)
            ? primary?.FileName ?? ""
            : launchTargetBox.Text;

        var selectedVariants = variantsList.SelectedItems
            .OfType<ImportableExecutable>()
            .Select(item => new { item.EntryPath, Arch = GuessArch(item.EntryPath) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Arch))
            .ToList();

        var effectiveDownloadUrl = isZipMode ? null : downloadUrlBox.Text.Trim();
        var effectiveDownloadFilter = isZipMode ? null : downloadFilterBox.Text.Trim();

        var pluginJson = BuildPluginJson(
            nameBox.Text, descBox.Text, category,
            tagsBox.Text, packagePath is not null ? Path.GetFileName(packagePath) : null,
            launchTarget, publisherBox.Text, homepageBox.Text,
            versionBox.Text, user?.Login ?? "",
            selectedVariants.Select(v => (v.EntryPath, v.Arch)).ToList(),
            iconFilePath is not null ? Path.GetFileName(iconFilePath) : null,
            effectiveDownloadUrl,
            string.IsNullOrWhiteSpace(effectiveDownloadFilter) ? null : effectiveDownloadFilter);

        var previewDialog = new ContentDialog
        {
            Title = L("CommunityTools_ConfirmSubmit", MiscTexts.T("确认提交")),
            PrimaryButtonText = L("CommunityTools_Submit", MiscTexts.T("提交")),
            CloseButtonText = L("CommunityTools_BackToEdit", MiscTexts.T("返回修改")),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        previewDialog.Resources["ContentDialogMaxWidth"] = 560;

        var previewStack = new StackPanel { Spacing = 12 };

        var previewJsonBlock = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer
            {
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Text = pluginJson
                }
            }
        };

        var submitTip = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Color.FromArgb(25, 96, 165, 250)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 96, 165, 250)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE946", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250)) },
                    new TextBlock
                    {
                        Text = L("CommunityTools_SubmitHint", MiscTexts.T("提交后将创建 Pull Request，审核通过后即可在社区中展示。")),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        previewStack.Children.Add(previewJsonBlock);
        previewStack.Children.Add(submitTip);

        previewDialog.Content = previewStack;

        var previewResult = await previewDialog.ShowAsync();
        if (previewResult != ContentDialogResult.Primary) return;

        var submitWindow = new CommunitySubmitWindow(
            nameBox.Text, descBox.Text, category,
            tagsBox.Text, isZipMode ? packagePath : null, launchTarget,
            publisherBox.Text, homepageBox.Text, versionBox.Text,
            iconFilePath,
            effectiveDownloadUrl,
            string.IsNullOrWhiteSpace(effectiveDownloadFilter) ? null : effectiveDownloadFilter);
        submitWindow.SubmitSucceeded += () =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                CommunityToolService.InvalidateCache();
                await LoadToolsAsync();
            });
        };
        submitWindow.Activate();
    }

    private static string GuessArch(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            return "ARM64";
        if (name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("64", StringComparison.OrdinalIgnoreCase))
            return "x64";
        if (name.Contains("x86", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("32", StringComparison.OrdinalIgnoreCase))
            return "x86";
        return "";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{(double)bytes / (1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{(double)bytes / (1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{(double)bytes / (1L << 10):F1} KB";
        return $"{bytes} B";
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = L("Common_OK", "确定"),
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme
        };
        await dialog.ShowAsync();
    }

    private static string BuildPluginJson(
        string name, string description, string category, string tags,
        string? fileName, string launchTarget,
        string publisher, string homepage, string version, string author,
        List<(string EntryPath, string Arch)> archVariants,
        string? iconFileName = null,
        string? downloadUrl = null,
        string? downloadFilter = null)
    {
        var toolId = CommunityToolService.GenerateToolId(name);
        var tagList = tags.Split(',', '，', ';', '；')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var plugin = new Dictionary<string, object?>
        {
            ["id"] = toolId,
            ["name"] = name,
            ["version"] = string.IsNullOrWhiteSpace(version) ? "1.0" : version,
            ["description"] = description,
            ["category"] = category,
            ["tags"] = tagList,
            ["launchTarget"] = launchTarget,
            ["author"] = author,
            ["submittedAt"] = DateTimeOffset.UtcNow.ToString("o")
        };

        if (!string.IsNullOrWhiteSpace(publisher)) plugin["publisher"] = publisher;
        if (!string.IsNullOrWhiteSpace(homepage)) plugin["homepage"] = homepage;
        if (!string.IsNullOrWhiteSpace(iconFileName)) plugin["icon"] = iconFileName;
        if (!string.IsNullOrWhiteSpace(downloadUrl)) plugin["downloadUrl"] = downloadUrl;
        if (!string.IsNullOrWhiteSpace(downloadFilter)) plugin["downloadFilter"] = downloadFilter;
        if (!string.IsNullOrWhiteSpace(fileName)) plugin["file"] = fileName;

        if (archVariants.Count > 0)
        {
            plugin["archVariants"] = archVariants.Select(v => new Dictionary<string, object?>
            {
                ["file"] = v.EntryPath.Replace('\\', '/').TrimStart('/'),
                ["arch"] = v.Arch
            }).ToList();
        }

        return JsonSerializer.Serialize(plugin, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private DispatcherTimer? _statusBarTimer;

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;

        _statusBarTimer?.Stop();
        _statusBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusBarTimer.Tick += (s, e) =>
        {
            StatusBar.IsOpen = false;
            ((DispatcherTimer)s!).Stop();
        };
        _statusBarTimer.Start();
    }
}
