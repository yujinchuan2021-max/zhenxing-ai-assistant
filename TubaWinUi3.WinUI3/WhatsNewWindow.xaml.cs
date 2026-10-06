using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using Windows.Graphics;

namespace TubaWinUi3;

public sealed partial class WhatsNewWindow : Page
{
    private readonly Window _window;
    private bool _languageSubscribed;

    public WhatsNewWindow(Window window)
    {
        _window = window;
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        _window.Closed += OnWindowClosed;
        ApplyLocalization();
        VersionListView.SelectedIndex = 0;
    }

    public static void Show()
    {
        var window = new Window();
        var page = new WhatsNewWindow(window);
        page.RequestedTheme = ThemeService.CurrentElementTheme;

        window.Content = page;
        BackdropService.ApplyBackdrop(window);

        try
        {
            var displayArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea is not null)
            {
                var workArea = displayArea.WorkArea;
                var w = (int)(workArea.Width * 0.82);
                var h = (int)(workArea.Height * 0.85);
                window.AppWindow.Resize(new SizeInt32(w, h));
                window.AppWindow.Move(new PointInt32(
                    workArea.X + (int)((workArea.Width - w) / 2),
                    workArea.Y + (int)((workArea.Height - h) / 2)));
            }
        }
        catch
        {
            window.AppWindow.Resize(new SizeInt32(1100, 750));
            try
            {
                var mainPos = App.MainWindow?.AppWindow.Position;
                if (mainPos is not null)
                    window.AppWindow.Move(new PointInt32(mainPos.Value.X + 50, mainPos.Value.Y + 50));
            }
            catch { }
        }

        SafeTitleBar.ApplyExtendedTall(window);

        ApplyTitleBarTheme(window);
        window.Activate();
    }

    private static void ApplyTitleBarTheme(Window window)
    {
        var isDark = ThemeService.CurrentTheme == AppTheme.Dark ||
                     (ThemeService.CurrentTheme == AppTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        TitleBarPalette.Apply(SafeTitleBar.Get(window), isDark);
    }

    private static string L(string key, string fallback) => LocalizationService.L(key, fallback);

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_languageSubscribed)
        {
            LocalizationService.LanguageChanged += OnLanguageChanged;
            _languageSubscribed = true;
        }
        ApplyLocalization();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => UnsubscribeLanguage();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        UnsubscribeLanguage();
        Loaded -= OnPageLoaded;
        Unloaded -= OnPageUnloaded;
        _window.Closed -= OnWindowClosed;
    }

    private void UnsubscribeLanguage()
    {
        if (!_languageSubscribed)
            return;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _languageSubscribed = false;
    }

    private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_languageSubscribed)
            ApplyLocalization();
    });

    private void ApplyLocalization()
    {
        var title = L("WhatsNew_Title", "新增内容");
        _window.AppWindow.Title = title;
        WindowTitleText.Text = title;
        WindowSubtitleText.Text = L("WhatsNew_Subtitle", "0.1.1 公开预览版更新说明");
        ContentsTitleText.Text = L("WhatsNew_Contents", "版本");
        CloseButton.Content = L("WhatsNew_Close", "关闭");
        VersionListLabel.Text = "0.1.1";
        RenderChangelog();
    }

    private void RenderChangelog()
    {
        VersionTitleText.Text = L("WhatsNew_VersionTitle", "0.1.1 本次更新");
        VersionDateText.Text = "2026-10-06";
        var changelog = string.Join("\n\n", new[]
        {
            L("WhatsNew_PreviewNote", "0.1.1 公开预览版：新增六类硬件展示名称配置与主流型号选择，发布包与校验值见 GitHub 发行页。"),
            "- " + L("WhatsNew_HardwareNames", "配置修改器支持 CPU、主板、显卡、内存、显示器和硬盘六类名称，内置 96 个主流型号，可按设备选择并自定义。"),
            "- " + L("WhatsNew_HardwareScope", "六类名称均可保存为本地展示配置。内存 PartNumber 为只读；系统同步仅支持 CPU/主板显示字符串和选定设备的 PnP FriendlyName。"),
            "- " + L("WhatsNew_HardwareRestore", "系统名称修改前先保存原始类型与缺失状态，逐项校验写入和恢复结果；失败项保留备份以便重试。硬件信息与排行榜继续读取实际检测结果。"),
            L("WhatsNew_V011History", "0.1.1 更新行为（保留）"),
            "- " + L("WhatsNew_PreviewIdentity", "客户端与启动器版本为 0.1.1，程序更新继续使用官方预览通道；同版的三段与四段数字版本不重复提示。"),
            "- " + L("WhatsNew_PackageValidation", "更新清单先完整核对架构、包类型、官方 HTTPS 地址、文件大小与 SHA-256，再判断最新版或其他平台。清单缺失或无效时如实报错。"),
            "- " + L("WhatsNew_ManualUpdate", "便携更新包通过校验后可打开下载目录。先备份配置与会话、退出旧版，再完整解压到新目录并核对数据位置；不会自动替换运行中的程序。更新说明在内置浏览器打开。"),
            L("WhatsNew_R10History", "此前 V0.1（R10 预览）功能记录"),
            "- " + L("WhatsNew_AiPlan", "AI 给出完整方案后，可在该条回复下点击“使用这套方案”，核对项目和工具清单后继续。"),
            "- " + L("WhatsNew_Model", "完善模型服务接入指引，支持客户端中英文切换。"),
            "- " + L("WhatsNew_Sharing", "选定工具流统一分享到枕星服务器，默认开启，可在设置中关闭。"),
            "- " + L("WhatsNew_Downloads", "HTTP 下载默认“下载并运行”，保存到系统下载目录，也可自选保存位置。"),
            "- " + L("WhatsNew_Appearance", "移除品牌 Logo 和截图水印设置，保留界面字体选择。"),
            "- " + L("WhatsNew_Feedback", "反馈建议使用 yujinchuan2021@gmail.com；枕星社区入口已接入。"),
            "- " + L("WhatsNew_Backend", "主动拦截默认关闭，手动开启后在后台运行，不弹出日志窗口。"),
            "- " + L("WhatsNew_QqGroups", "关于页新增两个枕星 QQ 群，点击“加入QQ群”即可打开加群页面。"),
        });
        MarkdownTextService.RenderToRichTextBlock(ChangelogText, changelog);
    }

    private void VersionListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionListView.SelectedIndex != 0)
            return;

        RenderChangelog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _window.Close();
}
