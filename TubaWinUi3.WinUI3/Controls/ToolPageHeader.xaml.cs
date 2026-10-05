using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services;

namespace TubaWinUi3.Controls;

/// <summary>
/// 内置工具页统一页头：左上角返回按钮 + 图标瓦片 + 标题/副标题 + 右侧操作区。
/// 返回按钮采用 WinUI 3 官方独立返回按钮规范（TitleBarBackButtonStyle）；
/// 在 BuiltinToolWindow 独立窗口中不显示（窗口自身标题栏即可关闭）。
/// </summary>
public sealed partial class ToolPageHeader : UserControl
{
    /// <summary>独立工具窗口扩展标题栏（TitleBarHeightOption.Tall）的高度，页头据此避让。</summary>
    private const double ToolWindowTitleBarInset = 48;

    private readonly Brush? _defaultIconBackground;
    private readonly Brush? _defaultIconForeground;

    public ToolPageHeader()
    {
        InitializeComponent();
        _defaultIconBackground = IconTile.Background;
        _defaultIconForeground = IconGlyph.Foreground;

        Loaded += (_, _) => HookLocalization();
        Unloaded += (_, _) => UnhookLocalization();
    }

    /// <summary>
    /// 显示期翻译：本控件不自带 Uid，页面传入的 Title/Subtitle 在此统一译
    /// （未收录的自定义标题——如用户文档标题——原样显示）；
    /// 语言切换时随 LanguageChanged 重新套用，不需要每个页面各自接线。
    /// </summary>
    private void ApplyLocalizedTexts()
    {
        TitleText.Text = MiscTexts.T(Title);
        SubtitleText.Text = MiscTexts.T(Subtitle);
    }

    private void OnLocalizationChanged()
    {
        if (DispatcherQueue is null) { ApplyLocalizedTexts(); return; }
        DispatcherQueue.TryEnqueue(ApplyLocalizedTexts);
    }

    private void HookLocalization()
    {
        LocalizationService.LanguageChanged -= OnLocalizationChanged;
        LocalizationService.LanguageChanged += OnLocalizationChanged;
        ApplyLocalizedTexts();
    }

    private void UnhookLocalization() => LocalizationService.LanguageChanged -= OnLocalizationChanged;

    /// <summary>右侧操作区（刷新/重新检测等页面级按钮）。</summary>
    public UIElementCollection Actions => ActionsPanel.Children;

    /// <summary>页面自定义返回逻辑（如未保存更改确认）；未订阅时默认调用 MainWindow.NavigateBack()。</summary>
    public event EventHandler? BackRequested;

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ToolPageHeader),
            new PropertyMetadata("", OnTitleChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ToolPageHeader),
            new PropertyMetadata("", OnSubtitleChanged));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(ToolPageHeader),
            new PropertyMetadata("", OnGlyphChanged));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>图标瓦片底色；留空则使用主题强调色。</summary>
    public static readonly DependencyProperty IconBackgroundProperty =
        DependencyProperty.Register(nameof(IconBackground), typeof(Brush), typeof(ToolPageHeader),
            new PropertyMetadata(null, OnIconBackgroundChanged));

    public Brush? IconBackground
    {
        get => (Brush?)GetValue(IconBackgroundProperty);
        set => SetValue(IconBackgroundProperty, value);
    }

    /// <summary>Optional brand image; other tool pages retain their existing font icon.</summary>
    public static readonly DependencyProperty IconSourceProperty =
        DependencyProperty.Register(nameof(IconSource), typeof(ImageSource), typeof(ToolPageHeader),
            new PropertyMetadata(null, OnIconSourceChanged));

    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    private static void OnIconSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var header = (ToolPageHeader)d;
        header.BrandIcon.Source = e.NewValue as ImageSource;
        header.BrandIcon.Visibility = e.NewValue is ImageSource ? Visibility.Visible : Visibility.Collapsed;
        header.IconGlyph.Visibility = e.NewValue is ImageSource ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>图标前景色；留空则使用强调色前景（TextOnAccent）。</summary>
    public static readonly DependencyProperty IconForegroundProperty =
        DependencyProperty.Register(nameof(IconForeground), typeof(Brush), typeof(ToolPageHeader),
            new PropertyMetadata(null, OnIconForegroundChanged));

    public Brush? IconForeground
    {
        get => (Brush?)GetValue(IconForegroundProperty);
        set => SetValue(IconForegroundProperty, value);
    }

    /// <summary>页头内边距；页面自身已有整页 Padding 时可设为 0 避免双重缩进。</summary>
    public static readonly DependencyProperty HeaderPaddingProperty =
        DependencyProperty.Register(nameof(HeaderPadding), typeof(Thickness), typeof(ToolPageHeader),
            new PropertyMetadata(new Thickness(24, 16, 24, 12), OnHeaderPaddingChanged));

    public Thickness HeaderPadding
    {
        get => (Thickness)GetValue(HeaderPaddingProperty);
        set => SetValue(HeaderPaddingProperty, value);
    }

    private static void OnHeaderPaddingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolPageHeader)d).ApplyHostLayout();

    /// <summary>是否显示页头底部 1px 分隔线。</summary>
    public static readonly DependencyProperty ShowDividerProperty =
        DependencyProperty.Register(nameof(ShowDivider), typeof(bool), typeof(ToolPageHeader),
            new PropertyMetadata(false, OnShowDividerChanged));

    public bool ShowDivider
    {
        get => (bool)GetValue(ShowDividerProperty);
        set => SetValue(ShowDividerProperty, value);
    }

    /// <summary>是否允许显示返回按钮（独立窗口模式下始终不显示）。</summary>
    public static readonly DependencyProperty ShowBackButtonProperty =
        DependencyProperty.Register(nameof(ShowBackButton), typeof(bool), typeof(ToolPageHeader),
            new PropertyMetadata(true, OnShowBackButtonChanged));

    public bool ShowBackButton
    {
        get => (bool)GetValue(ShowBackButtonProperty);
        set => SetValue(ShowBackButtonProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolPageHeader)d).ApplyLocalizedTexts();

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolPageHeader)d).ApplyLocalizedTexts();

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolPageHeader)d).IconGlyph.Glyph = e.NewValue as string ?? "";

    private static void OnIconBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var header = (ToolPageHeader)d;
        header.IconTile.Background = e.NewValue as Brush ?? header._defaultIconBackground;
    }

    private static void OnIconForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var header = (ToolPageHeader)d;
        header.IconGlyph.Foreground = e.NewValue as Brush ?? header._defaultIconForeground;
    }

    private static void OnShowDividerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolPageHeader)d).RootGrid.BorderThickness = (bool)e.NewValue ? new Thickness(0, 0, 0, 1) : new Thickness(0);

    private static void OnShowBackButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolPageHeader)d).ApplyHostLayout();

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyHostLayout();

    /// <summary>
    /// 按宿主调整页头：独立工具窗口隐藏返回按钮（交给窗口标题栏），
    /// 并整体下移避让扩展出的 Tall 标题栏，避免页头与系统标题按钮重叠。
    /// </summary>
    private void ApplyHostLayout()
    {
        var inToolWindow = BuiltinToolWindow.IsInToolWindow(this);

        BackButton.Visibility = ShowBackButton && !inToolWindow
            ? Visibility.Visible
            : Visibility.Collapsed;

        var padding = HeaderPadding;
        RootGrid.Padding = inToolWindow
            ? new Thickness(padding.Left, padding.Top + ToolWindowTitleBarInset, padding.Right, padding.Bottom)
            : padding;
    }

    private void OnBackButtonClick(object sender, RoutedEventArgs e)
    {
        if (BackRequested is not null)
            BackRequested(this, EventArgs.Empty);
        else
            App.MainWindow?.NavigateBack();
    }
}
