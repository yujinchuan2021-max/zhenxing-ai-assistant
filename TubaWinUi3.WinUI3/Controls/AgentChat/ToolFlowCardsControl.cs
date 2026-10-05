using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Controls.AgentChat;

/// <summary>Native, display-only recommendations. Selection is handled by the existing review flow.</summary>
internal sealed class ToolFlowCardsControl : Grid
{
    private readonly List<ToolFlowOptionCard> _cards = [];
    private int _columnCount;

    internal ToolFlowCardsControl(ToolFlowRecommendationSet set)
    {
        ColumnSpacing = 12;
        RowSpacing = 12;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var option in set.Options)
        {
            var card = new ToolFlowOptionCard(option, option.Id == set.RecommendedId);
            _cards.Add(card);
            Children.Add(card);
        }
        SizeChanged += (_, args) => ArrangeCards(args.NewSize.Width);
        ArrangeCards(0);
    }

    internal IReadOnlyList<ToolFlowOptionCard> Cards => _cards;

    internal void ApplyLocalization()
    {
        foreach (var card in _cards) card.ApplyLocalization();
    }

    private void ArrangeCards(double width)
    {
        var columns = width >= 760 ? 3 : 1;
        if (_columnCount == columns) return;
        _columnCount = columns;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < (_cards.Count + columns - 1) / columns; i++)
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _cards.Count; i++)
        {
            SetRow(_cards[i], i / columns);
            SetColumn(_cards[i], i % columns);
        }
    }
}

internal sealed class ToolFlowOptionCard : Grid
{
    private readonly ToolFlowRecommendationOption _option;
    private readonly ThemeRefreshScope _theme;
    private readonly TextBlock _tier;
    private readonly TextBlock _recommendation;
    private readonly TextBlock _fitLabel;
    private readonly TextBlock _toolsLabel;
    private readonly TextBlock _costLabel;
    private readonly TextBlock _requirementsLabel;
    private readonly TextBlock _summary;
    private readonly TextBlock _costValue;
    private readonly TextBlock _requirementsValue;
    private readonly Expander _details;
    private bool _detailsLoaded;
    private bool _ready;
    private bool _busy;
    private bool _selected;
    private bool _failed;

    internal Button SelectButton { get; }
    internal string OptionId => _option.Id;

    internal ToolFlowOptionCard(ToolFlowRecommendationOption option, bool recommended)
    {
        _option = option;
        var surface = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16),
            BorderThickness = new Thickness(recommended ? 2 : 1),
        };
        Children.Add(surface);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        // The card is one fixed rendering (including its one-time lazy details).
        // Keep its original native wrappers so GC cannot drop text/theme bindings.
        ToolFlowThemeResources.AddPalette(this);
        _theme = ThemeRefreshScope.AttachRenderedContent(this);
        _theme.Bind(surface, Border.BackgroundProperty, ToolFlowThemeResources.Canvas);
        _theme.Bind(surface, Border.BorderBrushProperty, recommended ? ToolFlowThemeResources.Accent : ToolFlowThemeResources.Stroke);

        var layout = new Grid { RowSpacing = 10 };
        for (var i = 0; i < 8; i++) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.Child = layout;

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tier = Text("", 13, secondary: true);
        _recommendation = Text("", 11);
        _theme.Bind(_recommendation, TextBlock.ForegroundProperty, ToolFlowThemeResources.Accent);
        var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 3, 7, 3), Child = _recommendation };
        _theme.Bind(badge, Border.BackgroundProperty, ToolFlowThemeResources.Surface);
        badge.Visibility = recommended ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(badge, 1);
        header.Children.Add(_tier);
        header.Children.Add(badge);
        Add(layout, header, 0);

        var title = Text(option.Name, 17);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.MaxLines = 2;
        Add(layout, title, 1);
        _summary = Text(option.Summary, 13);
        _summary.MaxLines = 3;
        _summary.MinHeight = 40;
        Add(layout, _summary, 2);

        Add(layout, Field(out _fitLabel, out _, option.Fit, 2), 3);
        var names = string.Join(" · ", option.Items.Take(4).Select(i => i.Name));
        if (option.Items.Count > 4) names += " …";
        Add(layout, Field(out _toolsLabel, out _, names, 3), 4);
        Add(layout, Field(out _costLabel, out _costValue, option.Cost, 2), 5);
        Add(layout, Field(out _requirementsLabel, out _requirementsValue, option.Requirements, 3), 6);

        var notice = !option.Available ? option.UnavailableReason : option.Warnings.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(notice))
        {
            var warning = Text(notice, 12, secondary: true);
            warning.MaxLines = 3;
            Add(layout, warning, 7);
        }

        _details = new Expander { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _details.Expanding += (_, _) => EnsureDetails();
        SelectButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), IsEnabled = false,
        };
        if (recommended && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle)
            && accentStyle is Style style) SelectButton.Style = style;
        var footer = new StackPanel { Spacing = 10, Children = { _details, SelectButton } };
        Add(layout, footer, 9);
        ApplyLocalization();
    }

    internal void SetActionState(bool ready, bool busy, bool selected = false, bool failed = false)
    {
        _ready = ready;
        _busy = busy;
        _selected = selected;
        _failed = failed;
        ApplyLocalization();
    }

    internal void ApplyLocalization()
    {
        var tier = _option.Id switch
        {
            "single" => L("AiFlow_CurrentPlan", "当前方案", "Current plan"),
            "light" => L("AiFlow_LightMode", "轻量", "Light"),
            "heavy" => L("AiFlow_HeavyMode", "重量", "Heavy"),
            _ => L("AiFlow_MediumMode", "中量", "Medium"),
        };
        _tier.Text = tier;
        _recommendation.Text = L("AiFlow_Recommended", "推荐", "Recommended");
        _fitLabel.Text = L("AiFlow_FitLabel", "适合", "Best for");
        _toolsLabel.Text = L("AiFlow_ToolsLabel", "主要工具", "Core tools");
        _costLabel.Text = L("AiFlow_CostLabel", "费用", "Cost");
        _requirementsLabel.Text = L("AiFlow_RequirementsLabel", "要求", "Requirements");
        _details.Header = L("AiFlow_Details", "方案详情", "Plan details");
        if (_option.Id == "single")
        {
            _summary.Text = L("AiFlow_SingleSummary", "核对这套工具与必要的人工步骤。", "Review these tools and the required manual steps.");
            _costValue.Text = L("AiFlow_CostInDetails", "按所选工具与服务核对，详见方案说明。", "Check the selected tools and services; see the plan for details.");
            _requirementsValue.Text = L("AiFlow_RequirementsInDetails", "开始前核对网络、账号与设备条件。", "Check network, account and device requirements before starting.");
        }
        SelectButton.Content = !_option.Available ? L("AiFlow_Unavailable", "当前不可用", "Unavailable")
            : _selected ? L("AiFlow_Selected", "已选择", "Selected")
            : _failed ? L("AiFlow_IncompletePlan", "方案未完成", "Incomplete plan")
            : !_ready ? L("AiFlow_WaitForCompletion", "等待方案完成", "Waiting for completion")
            : _option.Id == "single" ? L("AiFlow_UseThisPlan", "使用这套方案", "Use this plan")
            : string.Format(L("AiFlow_SelectMode", "选择{0}方案", "Choose {0}"), tier);
        SelectButton.IsEnabled = _ready && !_busy && !_failed && _option.Available && !_selected;
        AutomationProperties.SetName(SelectButton, _option.Name + ": " + SelectButton.Content);
    }

    private void EnsureDetails()
    {
        if (_detailsLoaded) return;
        _detailsLoaded = true;
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(Text(_option.Summary, 13));
        panel.Children.Add(Text(_option.Fit, 12));
        panel.Children.Add(Text(_option.Cost, 12));
        panel.Children.Add(Text(_option.Requirements, 12));
        if (!_option.Available) panel.Children.Add(Text(_option.UnavailableReason, 12));
        foreach (var item in _option.Items)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(Text(item.Name, 13));
            if (!string.IsNullOrWhiteSpace(item.Version)) row.Children.Add(Text(item.Version, 12, secondary: true));
            AddLink(row, item.SourceUrl);
            if (item.DownloadUrl != item.SourceUrl) AddLink(row, item.DownloadUrl);
            panel.Children.Add(row);
        }
        foreach (var warning in _option.Warnings) panel.Children.Add(Text(warning, 12, secondary: true));
        _details.Content = panel;
    }

    private static void AddLink(StackPanel panel, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps) return;
        var link = new HyperlinkButton
        {
            Content = uri.Host, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0), FontSize = 12,
        };
        InternalBrowserLink.Bind(link, uri.AbsoluteUri);
        panel.Children.Add(link);
    }

    private StackPanel Field(out TextBlock label, out TextBlock text, string value, int lines)
    {
        label = Text("", 11, secondary: true);
        text = Text(value, 12);
        text.MaxLines = lines;
        return new StackPanel { Spacing = 3, Children = { label, text } };
    }

    private TextBlock Text(string value, double size, bool secondary = false)
    {
        var text = new TextBlock
        {
            Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true,
            FontFamily = AppFonts.WinUI,
        };
        _theme.Bind(text, TextBlock.ForegroundProperty, secondary ? ToolFlowThemeResources.SecondaryText : ToolFlowThemeResources.PrimaryText);
        return text;
    }

    private static void Add(Grid layout, FrameworkElement child, int row)
    {
        Grid.SetRow(child, row);
        layout.Children.Add(child);
    }

    private static string L(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
}
