using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TubaWinUi3.Services;
using TubaWinUi3.Services.AppManagement;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Pages;

/// <summary>Local software stays visible independently of the cloud catalog. Tool operations belong to the service.</summary>
public sealed partial class AppCenterPage : Page
{
    public ObservableCollection<AppCenterItem> CloudItems { get; } = [];
    public ObservableCollection<AppCenterItem> SandboxItems { get; } = [];
    public ObservableCollection<AppCenterItem> EnvItems { get; } = [];
    public ObservableCollection<AppCenterItem> AiItems { get; } = [];
    public ObservableCollection<AppCenterItem> CoreItems { get; } = [];
    private List<AppCenterItem> _localItems = [];
    private List<AppCenterItem> _cloudSnapshot = [];
    private readonly HashSet<string> _busyKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pageStop;
    private ContentDialog? _shownDialog;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _cloudTimer;
    private int _cloudUpdateQueued;
    private long _epoch;
    private bool _active, _refreshing, _dialogOpen, _registering;

    public AppCenterPage()
    {
        InitializeComponent();
        _cloudTimer = DispatcherQueue.CreateTimer();
        _cloudTimer.Interval = TimeSpan.FromMilliseconds(200); _cloudTimer.IsRepeating = false;
        _cloudTimer.Tick += (_, _) =>
        {
            Interlocked.Exchange(ref _cloudUpdateQueued, 0);
            if (!_active) return;
            try { Render(); }
            catch (Exception ex) { Debug.WriteLine($"[AppCenter] cloud projection: {ex}"); Feedback("工具状态暂未刷新，请重试。", _epoch); }
        };
        Unloaded += (_, _) => Deactivate();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Deactivate();
        _active = true; _epoch++; _pageStop = new();
        RegisterButton.IsEnabled = !_registering;
        RefreshButton.IsEnabled = true; LoadingRing.IsActive = false;
        CloudToolService.Changed += CloudChanged;
        try { CloudToolService.Start(); Render(); await RefreshAllAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[AppCenter] navigation: {ex}"); Feedback("暂时无法读取全部工具；已找到的本机软件保留，请刷新重试。", _epoch); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Deactivate();
        base.OnNavigatedFrom(e);
    }

    private void Deactivate()
    {
        _active = false; _epoch++;
        CloudToolService.Changed -= CloudChanged;
        _cloudTimer.Stop(); Interlocked.Exchange(ref _cloudUpdateQueued, 0);
        _pageStop?.Cancel(); _pageStop?.Dispose(); _pageStop = null;
        _refreshing = false;
        try { _shownDialog?.Hide(); } catch { }
    }

    private bool Current(long epoch) => _active && epoch == _epoch && _pageStop is not null;
    private void CloudChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _cloudUpdateQueued, 1) != 0) return;
        long epoch = _epoch;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (!Current(epoch)) return;
            _cloudTimer.Start();
        })) Interlocked.Exchange(ref _cloudUpdateQueued, 0);
    }

    private async Task RefreshAllAsync()
    {
        if (!_active || _refreshing || _pageStop is null) return;
        _refreshing = true;
        long epoch = _epoch; var token = _pageStop.Token;
        RefreshButton.IsEnabled = false; LoadingRing.IsActive = true;
        try
        {
            // Start independently. A failed cloud request must not discard the local snapshot.
            var localTask = ReloadLocalAsync(epoch, token);
            var cloudTask = RefreshCloudAsync(epoch, token);
            await Task.WhenAll(localTask, cloudTask);
            if (!Current(epoch)) return;
            try
            {
                if (await AppCenterService.RefreshUpgradeCacheAsync(token) && Current(epoch))
                    await ReloadLocalAsync(epoch, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Debug.WriteLine($"[AppCenter] upgrade check: {ex}"); }
        }
        finally
        {
            if (Current(epoch)) { _refreshing = false; RefreshButton.IsEnabled = true; LoadingRing.IsActive = false; }
        }
    }

    private async Task RefreshCloudAsync(long epoch, CancellationToken token)
    {
        try
        {
            await CloudToolService.RefreshAsync(token);
            if (!Current(epoch)) return;
            Render();
            if (CloudToolService.LastRefreshError is not null)
                Feedback("云端目录暂时无法更新，已安装的软件仍可打开；稍后点刷新重试。", epoch);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppCenter] cloud refresh: {ex}");
            Feedback("云端目录暂时无法更新，已安装的软件仍可打开；稍后点刷新重试。", epoch);
        }
    }

    private async Task ReloadLocalAsync(long epoch, CancellationToken token)
    {
        try
        {
            var items = await AppCenterService.GetItemsAsync(token);
            if (!Current(epoch)) return;
            _localItems = items; Render();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppCenter] local refresh: {ex}");
            Feedback("本机软件检测未完成，已有清单保留；请点刷新重试。", epoch);
        }
    }

    private List<AppCenterItem> CloudSnapshot()
    {
        var definitions = CloudToolService.GetCatalog().ToDictionary(t => t.Id, StringComparer.Ordinal);
        var architecture = UpdateService.CurrentArchitecture;
        return CloudToolService.GetStates()
            .Select(state => (State: state, Entry: AppCenterActionPresentation.ResolveLocalEntry(
                state.EntryPath, true, File.Exists, Directory.Exists)))
            .Where(snapshot => AppCenterInventoryPolicy.ShouldIncludeCloudTool(
                snapshot.State, snapshot.Entry.Executable is not null))
            .Select(snapshot =>
        {
            var state = snapshot.State;
            var entry = snapshot.Entry;
            definitions.TryGetValue(state.Id, out var definition);
            bool canDownload = definition is not null && CloudToolValidation.SelectPackage(definition, architecture) is not null;
            bool website = definition is not null && InternalBrowserLink.TryGetWebUri(definition.Homepage, out _);
            var status = Enum.TryParse<AppCenterToolStatus>(state.Status.ToString(), out var parsed) ? parsed : AppCenterToolStatus.Unsupported;
            var view = AppCenterActionPresentation.Cloud(status, entry.Executable is not null,
                state.IsManaged, !string.IsNullOrEmpty(state.AvailableVersion) && state.AvailableVersion != state.Version,
                state.PendingUpdate, state.Progress, canDownload, website);
            var item = new AppCenterItem
            {
                Section = "cloud", CloudId = state.Id, Name = state.Name, Glyph = "\uE8F1",
                Version = string.IsNullOrWhiteSpace(state.Version) ? definition?.Version ?? "" : state.Version,
                Path = state.EntryPath ?? "", EntryPath = entry.Executable ?? "", AllowLaunch = true,
                Detail = (definition?.Description ?? "") + (state.Error is { Length: > 0 } ? " · " + state.Error : ""),
                StatusKey = view.StatusKey == "download" ? "attention" : view.StatusKey,
                StatusLabel = MiscTexts.T(view.StatusLabel),
                PrimaryAction = view.PrimaryAction.ToString(), PrimaryLabel = MiscTexts.T(view.PrimaryLabel),
                PrimaryEnabled = view.PrimaryEnabled, CanOpenTool = entry.Executable is not null && !state.IsBusy,
                CanOpenFolder = view.CanOpenLocation, CanCreateShortcut = view.CanCreateShortcut,
                CanRemoveManaged = view.CanRemoveManaged, IsBusy = state.IsBusy,
            };
            ApplyBusy(item);
            return item;
        }).OrderBy(item => definitions.TryGetValue(item.CloudId, out var definition) ? definition.Order : int.MaxValue)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void Render()
    {
        if (!_active || SearchBox is null || StatusFilter is null || SummaryText is null) return;
        string filter = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all", search = SearchBox.Text;
        List<AppCenterItem> cloud;
        try { cloud = CloudSnapshot(); _cloudSnapshot = cloud; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppCenter] cached cloud snapshot: {ex}");
            cloud = _cloudSnapshot;
            Feedback("云端工具状态暂不可用，本机软件清单保留；请刷新重试。", _epoch);
        }
        foreach (var item in _localItems) { AppCenterService.RefreshLocalEntry(item); item.IsBusy = false; ApplyBusy(item); }
        bool Matches(AppCenterItem item) => AppCenterActionPresentation.Matches(item, filter, search);
        Fill(CloudItems, cloud.Where(Matches));
        Fill(SandboxItems, _localItems.Where(i => i.Section == "sandbox" && Matches(i)));
        Fill(EnvItems, _localItems.Where(i => i.Section == "env" && Matches(i)));
        Fill(AiItems, _localItems.Where(i => i.Section == "ai" && Matches(i)));
        Fill(CoreItems, _localItems.Where(i => i.Section == "core" && Matches(i)));
        CloudSection.Visibility = cloud.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CloudEmpty.Visibility = CloudItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SandboxEmpty.Visibility = SandboxItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnvEmpty.Visibility = EnvItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AiEmpty.Visibility = AiItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = MiscTexts.TSub($"本机清单 {cloud.Count + _localItems.Count} 项 · 当前显示 {CloudItems.Count + SandboxItems.Count + EnvItems.Count + AiItems.Count + CoreItems.Count} 项");
    }

    private static void Fill(ObservableCollection<AppCenterItem> target, IEnumerable<AppCenterItem> source)
    {
        target.Clear(); foreach (var item in source) target.Add(item);
    }
    private static string Key(AppCenterItem item) => item.CloudId.Length > 0 ? "cloud:" + item.CloudId
        : item.RecordId.Length > 0 ? "record:" + item.RecordId : "path:" + item.Path;
    private void ApplyBusy(AppCenterItem item)
    {
        if (!_busyKeys.Contains(Key(item))) return;
        item.IsBusy = true; item.PrimaryEnabled = false; item.PrimaryLabel = MiscTexts.T("处理中");
    }
    private void Feedback(string message, long epoch)
    {
        if (!Current(epoch)) return;
        FeedbackText.Text = MiscTexts.T(message); FeedbackText.Visibility = Visibility.Visible;
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        try { Render(); } catch (Exception ex) { Debug.WriteLine($"[AppCenter] filter: {ex}"); }
    }
    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Toolbar.Orientation = e.NewSize.Width < 760 ? Orientation.Vertical : Orientation.Horizontal;
        SearchBox.Width = Toolbar.Orientation == Orientation.Vertical ? double.NaN : 260;
    }
    private void CardActions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is StackPanel panel) panel.Orientation = e.NewSize.Width < 760 ? Orientation.Vertical : Orientation.Horizontal;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        long epoch = _epoch;
        try { await RefreshAllAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[AppCenter] refresh: {ex}"); Feedback("刷新未完成，已有清单保留，请稍后重试。", epoch); }
    }

    private async Task RunItemAsync(AppCenterItem item, Func<Task> action)
    {
        if (!_active || !item.SecondaryEnabled || !_busyKeys.Add(Key(item))) return;
        long epoch = _epoch;
        try { Render(); await action(); }
        catch (Exception ex) { Debug.WriteLine($"[AppCenter] action: {ex}"); Feedback("操作未完成，请刷新软件入口后重试；原有软件与登记记录保留。", epoch); }
        finally
        {
            _busyKeys.Remove(Key(item));
            if (Current(epoch))
            {
                try { Render(); } catch (Exception ex) { Debug.WriteLine($"[AppCenter] render after action: {ex}"); }
            }
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, long epoch)
    {
        if (!Current(epoch) || _dialogOpen || XamlRoot is null) return ContentDialogResult.None;
        _dialogOpen = true; _shownDialog = dialog; dialog.XamlRoot = XamlRoot; dialog.RequestedTheme = ThemeService.CurrentElementTheme;
        try { return await dialog.ShowAsync(); }
        finally { if (_shownDialog == dialog) _shownDialog = null; _dialogOpen = false; }
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCenterItem item } || !item.PrimaryEnabled) return;
        await RunItemAsync(item, async () =>
        {
            long epoch = _epoch;
            if (!Enum.TryParse<AppCenterPrimaryAction>(item.PrimaryAction, out var action)) return;
            if (action == AppCenterPrimaryAction.Open) { OpenEntry(item); return; }
            if (action == AppCenterPrimaryAction.OpenLocation) { OpenLocation(item); return; }
            if (action == AppCenterPrimaryAction.CheckInstallation) { await CheckInstallationAsync(item, epoch); return; }
            if (action == AppCenterPrimaryAction.OpenWebsite)
            {
                var definition = CloudToolService.GetCatalog().FirstOrDefault(t => t.Id == item.CloudId);
                if (definition is not null && InternalBrowserLink.TryGetWebUri(definition.Homepage, out var uri)) BrowserPage.Open(uri.AbsoluteUri);
                return;
            }
            if (item.CloudId.Length > 0)
            {
                var result = action == AppCenterPrimaryAction.Update || action == AppCenterPrimaryAction.Retry && CloudToolService.GetInstalledEntryPath(item.CloudId) is not null
                    ? await CloudToolService.UpdateAsync(item.CloudId, CancellationToken.None)
                    : await CloudToolService.InstallAsync(item.CloudId, CancellationToken.None);
                Feedback(result.Message, epoch);
            }
            else if (action == AppCenterPrimaryAction.Update) await UpgradeLocalAsync(item, epoch);
        });
    }

    private async void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppCenterItem item }) await RunItemAsync(item, () => { OpenEntry(item); return Task.CompletedTask; });
    }
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppCenterItem item }) await RunItemAsync(item, () => { OpenLocation(item); return Task.CompletedTask; });
    }
    private async void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCenterItem item }) return;
        await RunItemAsync(item, async () =>
        {
            long epoch = _epoch;
            var entry = ValidateEntry(item, requireExecutable: true);
            await Task.Run(() => WindowsSearchIndexService.CreateVerifiedDesktopShortcut(item.Name, entry.Executable!));
            Feedback("桌面图标已创建；同名的其他图标会保留。", epoch);
        });
    }

    private (string? Executable, string? Directory) ValidateEntry(AppCenterItem item, bool requireExecutable)
    {
        string? path;
        if (item.CloudId.Length > 0) path = CloudToolService.GetInstalledEntryPath(item.CloudId);
        else
        {
            if (!_localItems.Contains(item)) throw new InvalidOperationException("The local snapshot changed.");
            path = item.Path;
            if (item.RecordId.Length > 0)
            {
                var record = SoftwareRegistry.Load().FirstOrDefault(r => r.Id == item.RecordId);
                if (record is null || AppCenterActionPresentation.NormalizeLocalPath(record.Path) != AppCenterActionPresentation.NormalizeLocalPath(path))
                    throw new InvalidOperationException("The registered entry changed.");
            }
        }
        var current = AppCenterActionPresentation.ResolveLocalEntry(path, item.AllowLaunch, File.Exists, Directory.Exists);
        if (current.Directory is null || requireExecutable && (current.Executable is null ||
            !string.Equals(current.Executable, item.EntryPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The program entry is unavailable or has changed.");
        return current;
    }
    private void OpenEntry(AppCenterItem item)
    {
        var entry = ValidateEntry(item, requireExecutable: true);
        ToolProcessLauncher.Launch(entry.Executable!, entry.Directory);
    }
    private void OpenLocation(AppCenterItem item)
    {
        var entry = ValidateEntry(item, requireExecutable: false);
        var start = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), UseShellExecute = true };
        start.ArgumentList.Add(entry.Directory!); Process.Start(start);
    }

    private async Task UpgradeLocalAsync(AppCenterItem item, long epoch)
    {
        if (!ValidWingetId(item.WingetId)) throw new InvalidOperationException("Invalid package identity.");
        var dialog = new ContentDialog { Title = MiscTexts.T("升级软件"), Content = MiscTexts.TSub($"将通过 winget 升级 {item.Name}。可能出现系统权限或安装窗口。"),
            PrimaryButtonText = MiscTexts.T("升级"), CloseButtonText = MiscTexts.T("取消"), DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog, epoch) != ContentDialogResult.Primary || !Current(epoch)) return;
        VerifyRegisteredPackage(item);
        Feedback(await SystemInstaller.UpgradeAsync(item.WingetId, CancellationToken.None), epoch);
        if (Current(epoch) && _pageStop is not null) await ReloadLocalAsync(epoch, _pageStop.Token);
    }
    private static bool ValidWingetId(string id) => Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,150}\z", RegexOptions.CultureInvariant);
    private static void VerifyRegisteredPackage(AppCenterItem item)
    {
        if (!ValidWingetId(item.WingetId) || !SoftwareRegistry.Load().Any(r => r.Id == item.RecordId && r.WingetId == item.WingetId))
            throw new InvalidOperationException("The package record changed.");
    }

    private async void RemoveCloud_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCenterItem item } || !item.CanRemoveManaged) return;
        await RunItemAsync(item, async () =>
        {
            long epoch = _epoch;
            if (!CloudToolService.IsManaged(item.CloudId)) throw new InvalidOperationException("This tool is not managed here.");
            var dialog = new ContentDialog { Title = MiscTexts.T("移除下载包"), Content = MiscTexts.TSub($"将删除 {item.Name} 的下载副本及其目录内的配置和文件。已有更新备份会保留。"),
                PrimaryButtonText = MiscTexts.T("移除"), CloseButtonText = MiscTexts.T("取消"), DefaultButton = ContentDialogButton.Close };
            if (await ShowDialogAsync(dialog, epoch) != ContentDialogResult.Primary || !Current(epoch)) return;
            var result = await CloudToolService.RemoveAsync(item.CloudId, CancellationToken.None); Feedback(result.Message, epoch);
        });
    }
    private async void RemoveRecord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCenterItem item } || !item.ShowRemove) return;
        await RunItemAsync(item, async () =>
        {
            long epoch = _epoch;
            var dialog = new ContentDialog { Title = MiscTexts.T("移除登记记录"), Content = MiscTexts.TSub($"只移除 {item.Name} 的登记记录，不会卸载软件或删除文件。"),
                PrimaryButtonText = MiscTexts.T("移除记录"), CloseButtonText = MiscTexts.T("取消"), DefaultButton = ContentDialogButton.Close };
            if (await ShowDialogAsync(dialog, epoch) != ContentDialogResult.Primary || !Current(epoch)) return;
            if (!SoftwareRegistry.Remove(item.RecordId)) throw new InvalidOperationException("The record is unavailable.");
            Feedback("登记记录已移除，软件文件保留。", epoch);
            if (_pageStop is not null) await ReloadLocalAsync(epoch, _pageStop.Token);
        });
    }

    private async Task CheckInstallationAsync(AppCenterItem item, long epoch)
    {
        VerifyRegisteredPackage(item);
        if (_pageStop is null) return;
        var result = await SystemInstaller.InspectRegisteredPackageAsync(item.WingetId, _pageStop.Token);
        if (!Current(epoch)) return;
        ApplyPackageInspection(item, result);
        Feedback(result.Message, epoch);
    }

    private void ApplyPackageInspection(AppCenterItem item, RegisteredPackageInspection inspection)
    {
        // A background refresh may have replaced the displayed objects. Only update the same registration identity.
        foreach (var current in _localItems.Where(row => row.RecordId == item.RecordId &&
                     row.WingetId == item.WingetId && row.Path == item.Path))
            current.PackageInspection = inspection;
    }

    private async void CheckInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCenterItem item } || string.IsNullOrWhiteSpace(item.WingetId)) return;
        await RunItemAsync(item, () => CheckInstallationAsync(item, _epoch));
    }

    private async void SystemApps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCenterItem item } || !item.ShowSystemApps) return;
        await RunItemAsync(item, async () =>
        {
            long epoch = _epoch;
            var opened = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures"));
            Feedback(opened ? "已打开 Windows 应用设置，请核对软件名称与安装状态。" : "Windows 应用设置未打开，请从系统设置进入已安装的应用。", epoch);
        });
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCenterItem item } || !item.CanUninstall) return;
        await RunItemAsync(item, async () =>
        {
            long epoch = _epoch;
            bool winget = item.WingetId.Length > 0;
            var dialog = new ContentDialog { Title = MiscTexts.T("卸载软件"),
                Content = MiscTexts.TSub(winget ? $"将通过 winget 卸载 {item.Name}。可能出现系统权限窗口。"
                    : $"将删除应用管理环境内的 {item.Name} 及其中全部文件。请先保存内容并关闭相关工具。"),
                PrimaryButtonText = MiscTexts.T("卸载"), CloseButtonText = MiscTexts.T("取消"), DefaultButton = ContentDialogButton.Close };
            if (await ShowDialogAsync(dialog, epoch) != ContentDialogResult.Primary || !Current(epoch)) return;
            string report;
            RegisteredPackageInspection? inspection = null;
            if (winget)
            {
                VerifyRegisteredPackage(item);
                var result = await SystemInstaller.UninstallRegisteredPackageAsync(item.WingetId, CancellationToken.None);
                report = result.Message;
                inspection = result.Inspection;
            }
            else
            {
                if (!AppCenterService.CanRemoveSandboxDirectory(item.Path)) throw new InvalidOperationException("This directory is not a managed environment.");
                report = await Task.Run(() => SandboxInstaller.Uninstall(item.Path));
            }
            Feedback(report, epoch);
            if (Current(epoch) && _pageStop is not null)
            {
                await ReloadLocalAsync(epoch, _pageStop.Token);
                if (Current(epoch) && inspection is not null) ApplyPackageInspection(item, inspection);
            }
        });
    }

    private async void RegisterSoftware_Click(object sender, RoutedEventArgs e)
    {
        if (!_active || _registering) return;
        _registering = true; RegisterButton.IsEnabled = false; long epoch = _epoch;
        try
        {
            var dialog = new ContentDialog { Title = MiscTexts.T("登记已安装的软件"),
                Content = MiscTexts.T("选择程序文件可直接打开并创建桌面图标；选择文件夹只提供打开位置，不会猜测启动程序。"),
                PrimaryButtonText = MiscTexts.T("选择程序文件"), SecondaryButtonText = MiscTexts.T("选择文件夹"),
                CloseButtonText = MiscTexts.T("取消"), DefaultButton = ContentDialogButton.Close };
            var choice = await ShowDialogAsync(dialog, epoch);
            if (!Current(epoch) || choice == ContentDialogResult.None) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            string? path = null, name = null;
            if (choice == ContentDialogResult.Primary)
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".exe"); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSingleFileAsync(); path = file?.Path; name = file?.DisplayName;
            }
            else
            {
                var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var folder = await picker.PickSingleFolderAsync(); path = folder?.Path; name = folder?.Name;
            }
            if (!Current(epoch) || path is null) return;
            var entry = AppCenterActionPresentation.ResolveLocalEntry(path, choice == ContentDialogResult.Primary, File.Exists, Directory.Exists);
            if (entry.Directory is null || choice == ContentDialogResult.Primary && entry.Executable is null)
                throw new InvalidOperationException("The selected local entry is not available.");
            SoftwareRegistry.Add(name ?? Path.GetFileNameWithoutExtension(path), path, source: "manual", note: MiscTexts.T("手动登记"));
            Feedback("登记已保存；程序文件可以直接打开，文件夹登记可打开位置。", epoch);
            if (_pageStop is not null) await ReloadLocalAsync(epoch, _pageStop.Token);
        }
        catch (Exception ex) { Debug.WriteLine($"[AppCenter] register: {ex}"); Feedback("登记未完成，请重新选择本机程序或文件夹。", epoch); }
        finally { _registering = false; if (Current(epoch)) RegisterButton.IsEnabled = true; }
    }
}
