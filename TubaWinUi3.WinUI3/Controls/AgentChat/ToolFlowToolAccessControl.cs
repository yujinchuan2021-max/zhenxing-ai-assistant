using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Controls.AgentChat;

internal enum ToolAccessAction { OpenTool, OpenAgentConsole, OpenLocation, CreateShortcut, CopyCliCommand }

/// <summary>Only the page can resolve installed tools or perform a user-requested action.</summary>
internal sealed class ToolFlowToolAccessControl : Grid
{
    // Compose a native Expander: inheriting the projected control made template
    // application see ContentControl, which rejects the Expander default style.
    private readonly Expander _expander = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly ScrollViewer _scroll = new()
    {
        MaxHeight = 230, HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly ThemeRefreshScope _theme;
    private readonly List<Button> _buttons = [];
    private readonly List<TextBlock> _rowTexts = [];
    private IReadOnlyList<ToolFlowToolAccessEntry> _entries = [];
    private IReadOnlyDictionary<string, ToolFlowPostInstallDeliveryResult> _deliveryByTarget =
        new Dictionary<string, ToolFlowPostInstallDeliveryResult>(StringComparer.OrdinalIgnoreCase);
    private ToolFlowToolAccessEntry[] _renderedEntries = [];
    private KeyValuePair<string, ToolFlowPostInstallDeliveryResult>[] _renderedDelivery = [];
    private string? _renderedLanguage;
    private bool _hasShownEntries;
    private bool _needsInitialScroll;
    private int _viewportEpoch;
    private bool _busy;
    internal event Action<ToolFlowToolAccessEntry, ToolAccessAction>? ActionRequested;
    internal IReadOnlyList<ToolFlowToolAccessEntry> Entries => _entries;

    internal ToolFlowToolAccessControl()
    {
        _theme = ToolFlowThemeResources.Attach(this);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _scroll.Content = _rows;
        _expander.Content = _scroll;
        Children.Add(_expander);
        Loaded += (_, _) => QueueInitialScroll();
        _scroll.Loaded += (_, _) => QueueInitialScroll();
        Unloaded += (_, _) => _viewportEpoch++;
        Visibility = Visibility.Collapsed;
    }

    internal void Update(IReadOnlyList<ToolFlowToolAccessEntry> entries, bool busy, bool expandInitially = true,
        IReadOnlyDictionary<string, ToolFlowPostInstallDeliveryResult>? deliveryByTarget = null)
    {
        // A probe or progress update must not undo a user's later folding choice.
        if (!_hasShownEntries && entries.Count > 0)
        {
            if (expandInitially)
            {
                _needsInitialScroll = true;
                _expander.IsExpanded = true;
            }
            _hasShownEntries = true;
        }
        _entries = entries;
        _deliveryByTarget = deliveryByTarget is null
            ? new Dictionary<string, ToolFlowPostInstallDeliveryResult>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ToolFlowPostInstallDeliveryResult>(deliveryByTarget, StringComparer.OrdinalIgnoreCase);
        _busy = busy;
        ApplyLocalization();
        QueueInitialScroll();
    }

    internal void Collapse() => _expander.IsExpanded = false;

    internal void Reset()
    {
        _hasShownEntries = false;
        _needsInitialScroll = false;
        _viewportEpoch++;
        Collapse();
        Update([], busy: false, expandInitially: false);
    }

    private void QueueInitialScroll()
    {
        if (!_needsInitialScroll || !IsLoaded) return;
        var epoch = _viewportEpoch;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != _viewportEpoch || !_needsInitialScroll || !IsLoaded || !_scroll.IsLoaded || !_expander.IsExpanded) return;
            // A focus request from the previous task can bring its replacement's
            // first button into view. The first reveal must still include the name.
            _scroll.ChangeView(null, 0, null, disableAnimation: true);
            _needsInitialScroll = false;
        });
    }

    internal void ApplyLocalization()
    {
        _expander.Header = string.Format(Text("AiTools_ReadyEntries", "就绪工具与打开方式（{0}）",
            "Ready tools and how to open them ({0})"), _entries.Count);
        Visibility = _entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var delivery = _deliveryByTarget.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (_renderedLanguage == LocalizationService.CurrentLanguage && _entries.SequenceEqual(_renderedEntries)
            && delivery.SequenceEqual(_renderedDelivery))
        {
            // Keep native rows and the scroll position while a same-plan probe only changes busy state.
            foreach (var button in _buttons) button.IsEnabled = !_busy;
            ToolFlowThemeResources.Refresh(_theme);
            return;
        }
        _renderedLanguage = LocalizationService.CurrentLanguage;
        _renderedEntries = _entries.ToArray();
        _renderedDelivery = delivery;
        _buttons.Clear();
        _rowTexts.Clear();
        _rows.Children.Clear();
        foreach (var entry in _entries)
        {
            var row = new StackPanel { Spacing = 5 };
            var name = new TextBlock
            {
                Text = entry.Name + (entry.IsGui ? "" : " · " + Text("AiTools_CommandLine", "命令行工具", "Command-line tool")),
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
            };
            row.Children.Add(name);
            _rowTexts.Add(name);
            _deliveryByTarget.TryGetValue(entry.TargetKey, out var delivered);
            if (delivered is not null && !string.Equals(delivered.TargetKey, entry.TargetKey,
                StringComparison.OrdinalIgnoreCase)) delivered = null;
            var shortcutReady = delivered?.Kind == ToolFlowPostInstallDeliveryKind.DesktopShortcutReady
                && !string.IsNullOrWhiteSpace(delivered.ShortcutPath);
            var shortcutFailed = delivered?.Kind == ToolFlowPostInstallDeliveryKind.Failed;
            ToolFlowCliLaunchInstruction? cli = null;
            if (!entry.IsGui && ToolFlowToolAccess.TryGetCliLaunchInstruction(entry, out var instruction)) cli = instruction;
            var hint = new TextBlock
            {
                Text = entry.IsGui
                    ? shortcutReady
                        ? Text("AiTools_DesktopReady", "桌面图标已创建，可直接打开。", "A desktop shortcut is ready. You can open the software now.")
                        : shortcutFailed
                            ? Text("AiTools_DesktopRetry", "软件可直接打开；桌面图标未创建，可重试。", "You can open the software now. Retry creating its desktop shortcut.")
                            : Text("AiTools_GuiGuidance", "可直接打开，也可创建桌面图标。", "Open the software now, or create a desktop shortcut.")
                    : cli?.Guidance ?? Text("AiTools_CliLocationGuidance", "在终端中使用；可打开位置查看说明。", "Use this tool in a terminal. Open its location to find the instructions."),
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            row.Children.Add(hint);
            _rowTexts.Add(hint);
            ToolFlowThemeResources.BindText(_theme, hint, ToolFlowThemeResources.SecondaryText);
            if (cli is not null)
            {
                var command = new TextBlock
                {
                    Text = cli.Command, FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                };
                row.Children.Add(command);
                _rowTexts.Add(command);
                ToolFlowThemeResources.BindText(_theme, command, ToolFlowThemeResources.PrimaryText);
            }
            var buttons = new StackPanel { Spacing = 6, Orientation = Orientation.Horizontal };
            row.SizeChanged += (_, e) => buttons.Orientation = e.NewSize.Width < 370
                ? Orientation.Vertical : Orientation.Horizontal;
            void Add(string key, string chinese, string english, ToolAccessAction action, bool possible)
            {
                if (!possible) return;
                var button = new Button
                {
                    Content = Text(key, chinese, english), FontSize = 12,
                    Padding = new Thickness(10, 5, 10, 5), IsEnabled = !_busy,
                };
                button.Click += (_, _) =>
                {
                    if (!_busy && button.IsEnabled && _entries.Contains(entry))
                        ActionRequested?.Invoke(entry, action);
                };
                buttons.Children.Add(button);
                _buttons.Add(button);
            }
            Add("AiTools_OpenSoftware", "打开软件", "Open software", ToolAccessAction.OpenTool,
                entry.IsGui && entry.ExecutablePath is not null);
            Add("AiDelivery_OpenAgent", "打开所选 Agent", "Open the selected Agent", ToolAccessAction.OpenAgentConsole,
                ToolFlowToolAccess.IsInteractiveAgent(entry));
            Add("AiTools_CopyCliCommand", "复制启动命令", "Copy launch command", ToolAccessAction.CopyCliCommand, cli is not null);
            Add("AiTools_OpenLocation", "打开位置", "Open location", ToolAccessAction.OpenLocation, entry.DirectoryPath is not null);
            Add(shortcutFailed ? "AiTools_RetryShortcut" : "AiTools_CreateShortcut",
                shortcutFailed ? "重试桌面图标" : "创建桌面图标",
                shortcutFailed ? "Retry desktop shortcut" : "Create desktop shortcut", ToolAccessAction.CreateShortcut,
                entry.IsGui && entry.ExecutablePath is not null && !shortcutReady);
            row.Children.Add(buttons);
            _rows.Children.Add(row);
            ToolFlowThemeResources.BindText(_theme, name, ToolFlowThemeResources.PrimaryText);
        }
        ToolFlowThemeResources.Refresh(_theme);
    }

    private static string Text(string key, string chinese, string english) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? english : chinese);
}
