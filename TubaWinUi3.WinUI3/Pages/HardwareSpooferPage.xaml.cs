using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

public sealed partial class HardwareSpooferPage : Page, ILocalizablePage
{
    private readonly HardwareDisplayEditorService _service;
    private readonly bool _fixture;
    private readonly Dictionary<string, EditorDevice> _draft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EditorDevice> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<HardwareModelCategory, Action<HardwareModelPreset>> _presetSelectors = [];
    private readonly Dictionary<HardwareModelCategory, Button> _categoryButtons = [];
    private readonly Dictionary<HardwareModelCategory, TextBlock> _categorySummaries = [];
    private HardwareModelCategory _activeCategory = HardwareModelCategory.Cpu;
    private HardwareEditorSnapshot? _snapshot;
    private bool _busy;
    private bool _active;
    private int _loadGeneration;

    public HardwareSpooferPage() : this(new HardwareDisplayEditorService(), null) { }

    // The native UI fixture supplies synthetic devices; it never queries or writes host hardware.
    internal HardwareSpooferPage(HardwareEditorSnapshot snapshot)
        : this(new HardwareDisplayEditorService(), snapshot) { }

    private HardwareSpooferPage(HardwareDisplayEditorService service, HardwareEditorSnapshot? fixture)
    {
        _service = service;
        _fixture = fixture is not null;
        InitializeComponent();
        ApplyLocalization();
        if (fixture is not null) { SetSnapshot(fixture); SetBusy(false); }
        Loaded += async (_, _) =>
        {
            _active = true;
            if (!_fixture) await ReloadAsync();
        };
        Unloaded += (_, _) => { _active = false; ++_loadGeneration; };
    }

    private static string T(string zh, string en) => LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh;

    internal static string CategoryName(HardwareModelCategory category) => category switch
    {
        HardwareModelCategory.Cpu => T("CPU 处理器", "CPU"),
        HardwareModelCategory.Motherboard => T("主板", "Motherboard"),
        HardwareModelCategory.Gpu => T("显卡", "Graphics card"),
        HardwareModelCategory.Memory => T("内存", "Memory"),
        HardwareModelCategory.Monitor => T("显示器", "Monitor"),
        _ => T("硬盘", "Disk"),
    };

    public void ApplyLocalization()
    {
        PageHeader.Title = T("配置修改器", "Config editor");
        PageHeader.Subtitle = T("六类硬件型号 · 修改前预览 · 原始名称可恢复", "Six hardware categories · Preview changes · Restore original names");
        WorkspaceTitleText.Text = T("换个名字，玩一套新配置", "A fresh look for your hardware");
        WorkspaceHintText.Text = T("先选一张硬件卡片，再挑型号。每台设备独立编辑，应用前可以看到完整预览。", "Pick a hardware card, then choose a model. Edit each device separately and preview the whole profile before applying.");
        ScopeExpander.Header = T("了解展示配置的生效范围", "Where these display names apply");
        CatalogStatusText.Text = T($"{HardwareModelCatalog.Models.Count} 个主流型号 · 支持品牌和型号搜索 · 数据核对：{HardwareModelCatalog.VerifiedOn:yyyy-MM-dd}",
            $"{HardwareModelCatalog.Models.Count} model presets · Search brands and models · Verified {HardwareModelCatalog.VerifiedOn:yyyy-MM-dd}");
        ScopeText.Text = T("这里修改的是展示名称。CPU、主板可写入注册表显示字段；显卡、显示器、硬盘可同步 Windows 设备友好名称。内存仅保存为本工具的本地展示配置。不会改变真实硬件、容量或性能；固件、WMI 与检测软件可能继续显示真实型号，重启或更新驱动也可能重置 Windows 名称。",
            "This edits display names. CPU and motherboard labels can be written to registry display fields; GPU, monitor and disk names can be synced to Windows device friendly names. Memory uses a local display profile in this tool. Actual hardware, capacity and performance stay unchanged. Firmware, WMI and diagnostic tools may still show real models, and Windows names may reset after reboot or driver updates.");
        SyncWindowsCheck.Content = T("同步 Windows 显示名称（需要管理员权限；内存始终仅本地展示）", "Sync Windows display names (administrator required; memory remains local)");
        ApplyButton.Content = T("应用修改", "Apply changes");
        RestoreButton.Content = T("一键恢复", "Restore originals");
        RefreshButton.Content = T("刷新设备", "Refresh devices");
        PreviewTitleText.Text = T("展示配置预览", "Display profile preview");
        if (_snapshot is not null) BuildEditors();
        UpdateStatus();
    }

    private async Task ReloadAsync()
    {
        var generation = ++_loadGeneration;
        SetBusy(true);
        try
        {
            var snapshot = await Task.Run(_service.LoadSnapshot);
            if (!_active || generation != _loadGeneration) return;
            SetSnapshot(snapshot);
            if (snapshot.ReadWarnings.Count > 0)
                ShowStatus(T("部分设备未能读取", "Some devices could not be read"), string.Join("\n", snapshot.ReadWarnings), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            if (_active && generation == _loadGeneration)
                ShowStatus(T("读取失败", "Could not read devices"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (generation == _loadGeneration) SetBusy(false);
        }
    }

    private void SetSnapshot(HardwareEditorSnapshot snapshot)
    {
        _snapshot = snapshot;
        _draft.Clear();
        _loaded.Clear();
        foreach (var device in snapshot.Devices)
        {
            _draft[device.Id] = device;
            _loaded[device.Id] = device;
        }
        BuildEditors();
        UpdateStatus();
    }

    private void BuildEditors()
    {
        EditorsPanel.Children.Clear();
        _presetSelectors.Clear();
        _categoryButtons.Clear();
        _categorySummaries.Clear();
        CategoryCardsPanel.Children.Clear();
        if (_snapshot is null) return;
        foreach (var category in Enum.GetValues<HardwareModelCategory>())
        {
            var devices = _snapshot.Devices.Where(d => d.Category == category).ToArray();
            if (devices.Length == 0) continue;
            EditorsPanel.Children.Add(BuildEditor(category, devices));
            var tileBody = new StackPanel { Spacing = 10 };
            var tileHeading = new Grid { ColumnSpacing = 10 };
            tileHeading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            tileHeading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = category switch
            {
                HardwareModelCategory.Cpu => "\uEEA1", HardwareModelCategory.Motherboard => "\uE977",
                HardwareModelCategory.Gpu => "\uF211", HardwareModelCategory.Memory => "\uE950",
                HardwareModelCategory.Monitor => "\uE7F4", _ => "\uEEDA",
            }, FontSize = 20 };
            var iconPanel = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Child = icon };
            iconPanel.Style = (Style)Resources["EditorIconPanelStyle"];
            icon.Style = (Style)Resources["EditorIconStyle"];
            tileHeading.Children.Add(iconPanel);
            var title = Text(CategoryName(category), bold: true, size: 14);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            tileHeading.Children.Add(title);
            tileBody.Children.Add(tileHeading);
            var summary = Text("", bold: true, size: 16);
            summary.MaxLines = 2;
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            tileBody.Children.Add(summary);
            tileBody.Children.Add(Text(devices.Any(d => d.CanApplySystem) ? T("Windows 显示名称 / 本地配置", "Windows display name / local profile") : T("本工具本地展示配置", "Local display profile"), secondary: true, size: 11));
            var button = new Button { Content = tileBody, Tag = category };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, CategoryName(category));
            button.Click += (_, _) => SelectCategory(category);
            _categoryButtons[category] = button;
            _categorySummaries[category] = summary;
            CategoryCardsPanel.Children.Add(button);
        }
        ArrangeCategoryCards();
        SelectCategory(_activeCategory);
        UpdatePreview();
    }

    private void SelectCategory(HardwareModelCategory category)
    {
        _activeCategory = category;
        foreach (var (key, button) in _categoryButtons)
            button.Style = (Style)Resources[key == category ? "SelectedHardwareCategoryTileStyle" : "HardwareCategoryTileStyle"];
        foreach (var expander in EditorsPanel.Children.OfType<Expander>())
        {
            var selected = Equals(expander.Tag, category);
            expander.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            expander.IsExpanded = selected;
        }
    }

    private void CategoryCardsPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ArrangeCategoryCards();

    private void ArrangeCategoryCards()
    {
        var columns = CategoryCardsPanel.ActualWidth >= 950 ? 3 : CategoryCardsPanel.ActualWidth >= 610 ? 2 : 1;
        var rows = (CategoryCardsPanel.Children.Count + columns - 1) / columns;
        if (CategoryCardsPanel.ColumnDefinitions.Count != columns || CategoryCardsPanel.RowDefinitions.Count != rows)
        {
            CategoryCardsPanel.ColumnDefinitions.Clear();
            CategoryCardsPanel.RowDefinitions.Clear();
            for (var i = 0; i < columns; ++i) CategoryCardsPanel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < rows; ++i) CategoryCardsPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        }
        for (var i = 0; i < CategoryCardsPanel.Children.Count; ++i)
        {
            Grid.SetColumn((FrameworkElement)CategoryCardsPanel.Children[i], i % columns);
            Grid.SetRow((FrameworkElement)CategoryCardsPanel.Children[i], i / columns);
        }
    }

    private Expander BuildEditor(HardwareModelCategory category, EditorDevice[] devices)
    {
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(Text(CategoryName(category), bold: true));
        var detectedCount = devices.Count(d => !d.Id.StartsWith("local/", StringComparison.OrdinalIgnoreCase));
        heading.Children.Add(Text(detectedCount == 0
            ? T($"本地展示配置 · {HardwareModelCatalog.ForCategory(category).Count} 个型号可选", $"Local display profile · {HardwareModelCatalog.ForCategory(category).Count} presets")
            : T($"{detectedCount} 个设备 · {HardwareModelCatalog.ForCategory(category).Count} 个型号可选", $"{detectedCount} devices · {HardwareModelCatalog.ForCategory(category).Count} presets"), secondary: true, size: 12));
        var body = new StackPanel { Spacing = 10, Padding = new Thickness(8) };
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = 0; i < devices.Length; ++i)
        {
            var device = devices[i];
            var originalLabel = string.IsNullOrWhiteSpace(device.OriginalName) ? T("未检测到设备 · 可制作本地展示配置", "No device detected · local profile available") : device.OriginalName;
            picker.Items.Add(new DeviceOption(device.Id, $"{i + 1}. {originalLabel}"));
        }
        body.Children.Add(Text(T("选择要修改的设备", "Select a device"), secondary: true, size: 12));
        body.Children.Add(picker);
        var scope = Text("", secondary: true, size: 12);
        var original = Text("", secondary: true, size: 12);
        body.Children.Add(scope);
        body.Children.Add(original);
        body.Children.Add(Text(T("目标型号（搜索或直接输入）", "Target model (search or type your own)"), size: 12));
        var name = new AutoSuggestBox
        {
            PlaceholderText = T("例如：品牌、型号或系列名称", "Search a brand, model or series"),
            TextMemberPath = nameof(HardwareModelPreset.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxSuggestionListHeight = 300,
        };
        body.Children.Add(name);
        var browseModels = new Button { Content = T("浏览常见型号", "Browse model presets") };
        browseModels.Click += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.ItemsSource = HardwareModelCatalog.ForCategory(category);
            name.IsSuggestionListOpen = true;
        };
        body.Children.Add(browseModels);
        body.Children.Add(Text(T("展示厂商", "Display manufacturer"), size: 12));
        var manufacturer = new TextBox { PlaceholderText = T("选择型号会自动填写，也可手动修改", "Filled from the selected preset, or enter your own"), HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(manufacturer);
        var localOnly = devices.All(d => d.Id.StartsWith("local/", StringComparison.OrdinalIgnoreCase));
        var reset = new Button { Content = localOnly ? T("撤销本次编辑", "Undo this edit") : T("填回原始名称", "Use original name") };
        body.Children.Add(reset);
        var source = Text(T("型号库用于展示，不代表与当前电脑兼容。", "Presets are display labels, not a compatibility recommendation."), secondary: true, size: 12);
        body.Children.Add(source);
        string selectedId = devices[0].Id;
        var filling = false;
        void Fill()
        {
            filling = true;
            var device = _draft[selectedId];
            name.Text = device.CurrentName;
            manufacturer.Text = device.CurrentManufacturer;
            scope.Text = device.CanApplySystem
                ? T(category is HardwareModelCategory.Cpu or HardwareModelCategory.Motherboard ? "可同步注册表显示名称；固件型号不变。" : "可同步此设备的 Windows 友好名称；驱动、固件型号不变。",
                    category is HardwareModelCategory.Cpu or HardwareModelCategory.Motherboard ? "Registry display label can be synced; firmware is unchanged." : "This device's Windows friendly name can be synced; driver and firmware are unchanged.")
                : T("仅本工具本地展示配置", "Local display profile in this tool only");
            original.Text = string.IsNullOrWhiteSpace(device.OriginalName) ? T("未检测到此类设备；选择型号后可保存为本地展示配置。", "No device detected in this category; select a model to save a local display profile.")
                : T($"原始名称：{device.OriginalName}", $"Original: {device.OriginalName}");
            name.ItemsSource = null;
            filling = false;
        }
        void Store()
        {
            if (filling) return;
            _draft[selectedId] = _draft[selectedId] with { CurrentName = name.Text, CurrentManufacturer = manufacturer.Text };
            UpdatePreview();
            UpdateStatus();
        }
        void Choose(HardwareModelPreset preset)
        {
            filling = true;
            name.Text = preset.Name;
            manufacturer.Text = preset.Manufacturer;
            filling = false;
            source.Text = T($"官方型号来源：{new Uri(preset.SourceUrl).Host}", $"Official model source: {new Uri(preset.SourceUrl).Host}");
            Store();
        }
        _presetSelectors[category] = Choose;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not DeviceOption option) return;
            selectedId = option.Id;
            Fill();
        };
        name.TextChanged += (_, args) =>
        {
            if (filling || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            Store();
            var terms = name.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            name.ItemsSource = HardwareModelCatalog.ForCategory(category)
                .Where(model => terms.All(term => (model.Name + " " + model.Manufacturer).Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Take(30).ToArray();
        };
        name.GotFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) name.ItemsSource = HardwareModelCatalog.ForCategory(category).Take(30).ToArray();
        };
        name.SuggestionChosen += (_, args) => { if (args.SelectedItem is HardwareModelPreset preset) Choose(preset); };
        name.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is HardwareModelPreset preset) Choose(preset);
            else Store();
        };
        manufacturer.TextChanged += (_, _) => Store();
        reset.Click += (_, _) =>
        {
            var device = _draft[selectedId];
            var local = device.Id.StartsWith("local/", StringComparison.OrdinalIgnoreCase);
            var before = _loaded[selectedId];
            _draft[selectedId] = device with
            {
                CurrentName = local ? before.CurrentName : device.OriginalName,
                CurrentManufacturer = local ? before.CurrentManufacturer : device.OriginalManufacturer,
            };
            Fill();
            UpdatePreview();
            UpdateStatus();
        };
        picker.SelectedIndex = 0;
        var expander = new Expander
        {
            Header = heading, Content = body,
            IsExpanded = category == HardwareModelCategory.Cpu,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = category,
        };
        return expander;
    }

    private TextBlock Text(string value, bool secondary = false, bool bold = false, double size = 14)
    {
        var block = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size };
        block.Style = (Style)Resources[secondary ? "EditorSecondaryText" : "EditorPrimaryText"];
        if (bold) block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return block;
    }

    private void UpdatePreview()
    {
        foreach (var (category, summary) in _categorySummaries)
        {
            var names = _draft.Values.Where(d => d.Category == category).Select(d => string.IsNullOrWhiteSpace(d.CurrentName) ? T("选择一个型号", "Choose a model") : d.CurrentName).ToArray();
            summary.Text = string.Join(" · ", names.Take(2)) + (names.Length > 2 ? T($" 等 {names.Length} 项", $" ({names.Length} items)") : "");
        }
        PreviewPanel.Children.Clear();
        foreach (var device in _draft.Values)
        {
            var target = string.IsNullOrWhiteSpace(device.CurrentName) ? T("（尚未填写）", "(not entered)") : device.CurrentName;
            var line = $"{CategoryName(device.Category)} · {target}";
            if (!string.Equals(device.CurrentName, device.OriginalName, StringComparison.Ordinal))
                line += T($"  ←  {device.OriginalName}", $"  ←  {device.OriginalName}");
            PreviewPanel.Children.Add(Text(line, size: 13));
        }
    }

    private void UpdateStatus()
    {
        var count = _draft.Values.Count(d => !_loaded.TryGetValue(d.Id, out var original) || d.CurrentName != original.CurrentName || d.CurrentManufacturer != original.CurrentManufacturer);
        DraftStatusText.Text = T($"{count} 项未保存修改", $"{count} unsaved changes");
        BackupStatusText.Text = _fixture ? T("隔离预览 · 不读写真实硬件", "Isolated preview · no real hardware access")
            : _service.HasSystemBackup ? T("已保留系统原始名称备份，可一键恢复。", "Original Windows names are backed up and can be restored.")
            : T("系统名称修改前会自动备份；本地配置可随时恢复。", "Windows names are backed up before changes; local profiles can be reset anytime.");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var editor in EditorsPanel.Children.OfType<Control>()) editor.IsEnabled = !busy;
        foreach (var tile in _categoryButtons.Values) tile.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && !_fixture;
        RestoreButton.IsEnabled = !busy && !_fixture;
        RefreshButton.IsEnabled = !busy && !_fixture;
        SyncWindowsCheck.IsEnabled = !busy && !_fixture;
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _fixture || _snapshot is null) return;
        // Empty placeholders let every category be edited even on headless/VM hosts.
        // Untouched placeholders are not incomplete edits and must not block other devices.
        var items = _draft.Values.Where(d => !string.IsNullOrWhiteSpace(d.CurrentName)
            || (_loaded.TryGetValue(d.Id, out var before) && (d.CurrentName != before.CurrentName || d.CurrentManufacturer != before.CurrentManufacturer))).ToArray();
        if (items.Length == 0)
        {
            ShowStatus(T("无需修改", "No changes"), T("请先选择或输入一个型号。", "Select or enter a model first."), InfoBarSeverity.Informational);
            return;
        }
        var invalid = items.FirstOrDefault(d => string.IsNullOrWhiteSpace(d.CurrentName) || d.CurrentName.Length > 256 || d.CurrentManufacturer.Length > 256 || d.CurrentName.Any(char.IsControl) || d.CurrentManufacturer.Any(char.IsControl));
        if (invalid is not null)
        {
            ShowStatus(T("请检查输入", "Check your input"), T("型号不能为空，名称与厂商限 256 字，不能包含控制字符。", "Model names are required; names and manufacturers must be at most 256 characters with no control characters."), InfoBarSeverity.Warning);
            return;
        }
        var sync = SyncWindowsCheck.IsChecked == true;
        if (sync && items.Any(d => d.CanApplySystem && (d.CurrentName != d.OriginalName || (d.Category == HardwareModelCategory.Motherboard && d.CurrentManufacturer != d.OriginalManufacturer))) && !_service.IsAdmin)
        {
            ShowStatus(T("需要管理员权限", "Administrator required"), T("请以管理员身份运行后同步 Windows 名称，或取消勾选同步，先保存本地展示配置。", "Run as administrator to sync Windows names, or uncheck sync to save a local display profile."), InfoBarSeverity.Warning);
            return;
        }
        if (sync && !await ConfirmAsync(T("应用展示名称", "Apply display names"), T("将保存六类硬件的本地展示配置，并同步有更改且受支持的 Windows 名称。系统原值会先备份。内存仅在本工具展示配置中生效。", "Save the local profile and sync changed, supported Windows names after backing up originals. Memory changes apply only in this tool's local display profile."))) return;
        SetBusy(true);
        try
        {
            var results = await Task.Run(() =>
            {
                var profile = _service.SaveProfile(items);
                return profile.Any(r => !r.Success) || !sync ? profile : profile.Concat(_service.ApplySystemChanges(items)).ToArray();
            });
            ShowResults(results, T("配置已保存", "Profile saved"));
            if (_active) await ReloadAsync();
        }
        catch (Exception ex) { ShowStatus(T("修改未完成", "Changes could not be completed"), ex.Message, InfoBarSeverity.Error); }
        finally { if (_active) SetBusy(false); }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _fixture) return;
        if (_service.HasSystemBackup && !_service.IsAdmin)
        {
            ShowStatus(T("需要管理员权限", "Administrator required"), T("恢复已修改的 Windows 名称需要以管理员身份运行。", "Run as administrator to restore changed Windows names."), InfoBarSeverity.Warning);
            return;
        }
        if (!await ConfirmAsync(T("恢复原始展示名称", "Restore original display names"), T("恢复已备份的 Windows 名称，并清除本工具保存的展示配置。恢复失败的系统项会保留备份，方便重试。", "Restore backed-up Windows names and remove this tool's local display profile. Failed system entries retain their backups for retry."))) return;
        SetBusy(true);
        try
        {
            var results = await Task.Run(() =>
            {
                var restored = _service.RestoreSystem();
                if (restored.All(r => r.Success)) _service.ClearProfile();
                return restored;
            });
            ShowResults(results, T("原始展示名称已恢复", "Original display names restored"));
            if (_active) await ReloadAsync();
        }
        catch (Exception ex) { ShowStatus(T("恢复未完成", "Restore could not be completed"), ex.Message, InfoBarSeverity.Error); }
        finally { if (_active) SetBusy(false); }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _fixture) return;
        if (_draft.Values.Any(d => _loaded.TryGetValue(d.Id, out var original) && (d.CurrentName != original.CurrentName || d.CurrentManufacturer != original.CurrentManufacturer))
            && !await ConfirmAsync(T("重新读取设备", "Refresh devices"), T("刷新会丢弃尚未保存的输入。是否继续？", "Refreshing discards unsaved edits. Continue?"))) return;
        await ReloadAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = content, PrimaryButtonText = T("确定", "Confirm"),
            CloseButtonText = T("取消", "Cancel"), DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowResults(IReadOnlyList<HardwareEditorResult> results, string successTitle)
    {
        var failures = results.Where(r => !r.Success).ToArray();
        var detail = string.Join("\n", results.Select(r => $"{CategoryName(r.Category)}：{r.Message}"));
        ShowStatus(failures.Length == 0 ? successTitle : T("部分操作未完成，请查看原因", "Some operations failed; review the details"),
            detail.Length == 0 ? T("本地展示配置已重置。", "Local display profile reset.") : detail,
            failures.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private sealed record DeviceOption(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    internal void ChooseModelForFixture(HardwareModelCategory category, HardwareModelPreset preset)
    {
        if (!_fixture) throw new InvalidOperationException("Synthetic model selection is fixture-only.");
        if (preset.Category != category) throw new ArgumentException("Model category does not match editor.");
        _presetSelectors[category](preset);
    }
}
