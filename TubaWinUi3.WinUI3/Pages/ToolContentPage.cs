using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TubaWinUi3.Controls;

namespace TubaWinUi3.Pages;

public sealed class ToolContentPageParam
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string Glyph { get; init; } = "";
    public required UIElement Content { get; init; }
    public Action? OnClose { get; init; }

    /// <summary>【主审补正·2026-09-22】导航离开（被其他页面替换视图）回调：
    /// 只做落盘/快照，不结束会话——"去设置再回来"属正常操作流程，不是关闭工具。
    /// 未提供时回退到旧行为（OnNavigatedFrom 直接走 OnClose）。</summary>
    public Action? OnNavigatedAway { get; init; }

    /// <summary>ZXAI：「主页=AI助手」等直达页面无返回目标时隐藏页头返回按钮。</summary>
    public bool HideBackButton { get; init; }

    /// <summary>ZXAI：隐藏页头整体（标题+描述行）——AI 助手页自带会话头部时用，避免"双层头"。</summary>
    public bool HideHeader { get; init; }
}

/// <summary>
/// Hosts tool UIs that are built in code (no dedicated XAML page).
/// The tool content fills the page below the standard <see cref="ToolPageHeader"/>.
/// </summary>
public sealed partial class ToolContentPage : Page
{
    private readonly ToolPageHeader _header;
    private readonly Grid _contentHost;
    private Action? _onClose;
    private Action? _onNavigatedAway;
    private UIElement? _content;

    public ToolContentPage()
    {
        InitializeComponent();

        _header = new ToolPageHeader();

        _contentHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_header, 0);
        Grid.SetRow(_contentHost, 1);
        root.Children.Add(_header);
        root.Children.Add(_contentHost);

        Content = root;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ToolContentPageParam param)
        {
            _onClose = param.OnClose;
            _onNavigatedAway = param.OnNavigatedAway;
            if (param.HideHeader) _header.Visibility = Visibility.Collapsed;   // ZXAI：避免与 AI 助手页自带头部叠成两层
            _header.Title = param.Title;
            _header.Subtitle = param.Description;
            _header.Glyph = param.Glyph;
            _header.ShowBackButton = !param.HideBackButton;
            _content = param.Content;
            // 【主审修复·2026-09-22】幂等 Add（同一实例可能仍挂在其他宿主上——见 OnNavigatedFrom
            // 的摘除逻辑；此处兜底防重复父元素 COMException）。
            if (!_contentHost.Children.Contains(param.Content))
                _contentHost.Children.Add(param.Content);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 【主审修复·2026-09-22】跨宿主父容器管理：把内容从本宿主摘下（XAML 元素只能有一个父级），
        // 使同一页面实例可被下一个宿主（GoBack/重新导航新建的 ToolContentPage）安全挂载。
        // 数据/会话/在途生成不受影响；不在此 Dispose。
        if (_content is not null && _contentHost.Children.Contains(_content))
            _contentHost.Children.Remove(_content);
        // 被其他页面导航替换 ≠ 工具被关闭：有 OnNavigatedAway 时只触发它（落盘快照，不结束会话）；
        // 真关闭仍由宿主调 Detach()。
        if (_onNavigatedAway is not null)
            _onNavigatedAway.Invoke();
        else
            Detach();
    }

    /// <summary>
    /// 执行与 OnNavigatedFrom 相同的清理逻辑。宿主（如独立工具窗口）在窗口关闭时
    /// 调用它，因为关闭窗口不会触发 Frame 的导航事件。
    /// </summary>
    public void Detach()
    {
        _onClose?.Invoke();
        _onClose = null;
        _onNavigatedAway = null;
        if (_content is not null && _contentHost.Children.Contains(_content))
            _contentHost.Children.Remove(_content);
        _content = null;
        _contentHost.Children.Clear();
    }
}
