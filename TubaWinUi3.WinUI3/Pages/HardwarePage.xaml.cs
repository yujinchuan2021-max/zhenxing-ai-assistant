using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using TubaWinUi3.Models;
using TubaWinUi3.Services;
using Windows.ApplicationModel.DataTransfer;

namespace TubaWinUi3.Pages;

public sealed partial class HardwarePage : Page, ILocalizablePage
{
    private DispatcherTimer? _uptimeTimer;
    private bool _dataLoaded;
    private bool _animatingDetails;
    private bool _nicknameMode;
    private bool _isAnimatingNickname;
    private IReadOnlyList<HardwareInfoSection>? _currentSections;
    private readonly Dictionary<HardwareInfoItem, string> _originalValues = [];

    public HardwarePage()
    {
        InitializeComponent();
        Loaded += HardwarePage_Loaded;
        Unloaded += HardwarePage_Unloaded;
        AppSettings.SettingChanged += OnSettingChanged;
    }

    private void OnSettingChanged(string key)
    {
        if (key == "UseCpuzDataSource" || key == "HardwareFitScreen" || key == "HardwareMultiDeviceNewLine")
        {
            if (key == "HardwareFitScreen")
                _currentLayoutFitScreen = null;
            _ = LoadHardwareInfoAsync(forceRefresh: true);
        }
    }

    private void HardwarePage_Loaded(object sender, RoutedEventArgs e)
    {
        StartUptimeTimer();
    }

    private void StartUptimeTimer()
    {
        _uptimeTimer?.Stop();
        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateUptime();
        _uptimeTimer.Start();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        StartUptimeTimer();
        // WMI 盘点在首次打开本页时后台执行（LoadAsync 自带缓存与并发合并，页面显示 loading）
        _ = LoadHardwareInfoAsync();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _uptimeTimer?.Stop();
        _uptimeTimer = null;
    }

    private void HardwarePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _uptimeTimer?.Stop();
        _uptimeTimer = null;
        AppSettings.SettingChanged -= OnSettingChanged;
    }

    private void UpdateUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = string.Format(L("Hw_UptimeFormat", "{0}天{1}小时{2}分钟{3}秒"), uptime.Days, uptime.Hours, uptime.Minutes, uptime.Seconds);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadHardwareInfoAsync(forceRefresh: true);
    }

    private void DetailButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(HardwareDetailPage), null, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
    }

    private void TitleText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _ = ToggleNicknameWithAnimationAsync();
    }

    private async Task ToggleNicknameWithAnimationAsync()
    {
        if (_currentSections is null || _isAnimatingNickname) return;
        _isAnimatingNickname = true;
        _nicknameMode = !_nicknameMode;

        var details = _currentSections[2].Items;

        foreach (var item in details)
        {
            if (!_originalValues.ContainsKey(item))
                _originalValues[item] = item.Value;
            if (item.NicknameValue is null)
                item.NicknameValue = BrandNicknameService.ApplyNickname(_originalValues[item]);
        }

        var summary = _currentSections[0].Items;
        var modelItem = summary.FirstOrDefault(i => i.Label == "设备型号");
        if (modelItem is not null)
        {
            if (!_originalValues.ContainsKey(modelItem))
                _originalValues[modelItem] = modelItem.Value;
            if (modelItem.NicknameValue is null)
                modelItem.NicknameValue = BrandNicknameService.ApplyNickname(_originalValues[modelItem]);
        }

        var valueTexts = new List<TextBlock>();
        for (int i = 0; i < DetailsRepeater.ItemsSourceView.Count; i++)
        {
            if (DetailsRepeater.TryGetElement(i) is Grid row)
            {
                var vt = FindChildByName<TextBlock>(row, "ValueText");
                if (vt is not null)
                    valueTexts.Add(vt);
            }
        }

        var eraseDuration = 180;
        var writeDuration = 250;
        var stagger = 40;

        foreach (var vt in valueTexts)
        {
            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(eraseDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(anim, vt);
            Storyboard.SetTargetProperty(anim, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
            sb.Children.Add(anim);
            sb.Begin();
        }

        if (valueTexts.Count > 0)
            await Task.Delay(eraseDuration + 20);

        for (int i = 0; i < valueTexts.Count && i < details.Count; i++)
        {
            var newText = _nicknameMode ? details[i].NicknameValue! : _originalValues[details[i]];
            valueTexts[i].Text = newText;
            details[i].Value = newText;
        }

        if (modelItem is not null)
            ModelText.Text = _nicknameMode ? modelItem.NicknameValue! : _originalValues[modelItem];

        for (int i = 0; i < valueTexts.Count; i++)
        {
            var vt = valueTexts[i];
            var delay = TimeSpan.FromMilliseconds(i * stagger);

            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var sb = new Storyboard();
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(writeDuration),
                    EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, vt);
                Storyboard.SetTargetProperty(anim, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
                sb.Children.Add(anim);
                sb.Begin();
            };
            timer.Start();
        }

        var totalWriteTime = valueTexts.Count * stagger + writeDuration;
        await Task.Delay(totalWriteTime);

        _isAnimatingNickname = false;

        ShowStatusBar(_nicknameMode ? L("Hw_NicknameModeOn", "彩蛋模式") : L("Hw_NicknameModeOff", "正常模式"), _nicknameMode ? L("Hw_NicknameOnHint", "品牌戏称已开启，点击标题恢复") : L("Hw_NicknameOffHint", "品牌戏称已关闭"), _nicknameMode ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (FastModeService.IsFastModeEnabled()) return;
        if (sender is not Border border) return;
        var sb = new Storyboard();
        var scaleX = new DoubleAnimation { To = 1.02, Duration = TimeSpan.FromMilliseconds(120), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var scaleY = new DoubleAnimation { To = 1.02, Duration = TimeSpan.FromMilliseconds(120), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(scaleX, border);
        Storyboard.SetTarget(scaleY, border);
        Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
        Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (FastModeService.IsFastModeEnabled()) return;
        if (sender is not Border border) return;
        var sb = new Storyboard();
        var scaleX = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var scaleY = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(scaleX, border);
        Storyboard.SetTarget(scaleY, border);
        Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
        Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    private async Task LoadHardwareInfoAsync(bool forceRefresh = false)
    {
        if (_dataLoaded)
        {
            if (FastModeService.IsFastModeEnabled())
            {
                SetElementStatesToExit();
            }
            else
            {
                ExitStoryboard.Begin();
                await Task.Delay(200);
            }
        }

        SetLoading(true);

        try
        {
            var sections = await HardwareInfoService.LoadAsync(forceRefresh);

            var useCpuz = AppSettings.GetBool("UseCpuzDataSource", false);
            if (useCpuz)
            {
                var cpuzInfo = CpuzInfoService.CachedInfo;
                if (cpuzInfo == null)
                {
                    try
                    {
                        cpuzInfo = await CpuzInfoService.FetchAsync(timeoutMs: 30000);
                    }
                    catch { }
                }

                if (cpuzInfo != null)
                {
                    sections = HardwareInfoService.ApplyCpuzOverride(sections, cpuzInfo);
                }
            }

            ApplySections(sections);
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ModelText.Text = L("Hw_Unknown", "未知");
            SystemText.Text = L("Hw_Unknown", "未知");
            UptimeText.Text = L("Hw_Unknown", "未知");
            DetailsRepeater.ItemsSource = Array.Empty<HardwareInfoItem>();
            ShowStatusBar(L("Hw_LoadFailed", "硬件信息读取失败"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void ApplySections(IReadOnlyList<HardwareInfoSection> sections)
    {
        UpdateLayoutStructure();

        _currentSections = sections;
        _nicknameMode = false;
        _originalValues.Clear();

        var summary = sections[0].Items;
        var system = sections[1].Items;
        var details = sections[2].Items;

        ModelText.Text = HardwareTexts.ValueText(summary.FirstOrDefault(item => item.Label == "设备型号")?.Value);
        SystemText.Text = HardwareTexts.ValueText(system.FirstOrDefault(item => item.Label == "系统")?.Value);
        UpdateUptime();
        _animatingDetails = !FastModeService.IsFastModeEnabled();
        DetailsRepeater.ItemsSource = details;

        CpuzBadge.Visibility = details.Any(it => it.IsVerified)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (FastModeService.IsFastModeEnabled())
        {
            SetElementStatesToVisible();
        }
        else
        {
            EntranceStoryboard.Begin();
        }
        _dataLoaded = true;
    }

    private static string L(string key, string fallback) => LocalizationService.L(key, fallback);

    /// <summary>语言切换：只刷新显示文本；保留彩蛋/动画状态与既有节点，不重跑盘点、不联网。</summary>
    public void ApplyLocalization()
    {
        UpdateUptime();
        RefreshDetailRowTexts();
        if (_currentSections is null || _currentSections.Count < 3) return;

        var summary = _currentSections[0].Items;
        var system = _currentSections[1].Items;
        var modelItem = summary.FirstOrDefault(item => item.Label == "设备型号");
        ModelText.Text = _nicknameMode && modelItem?.NicknameValue is not null
            ? modelItem.NicknameValue
            : HardwareTexts.ValueText(modelItem?.Value);
        SystemText.Text = HardwareTexts.ValueText(system.FirstOrDefault(item => item.Label == "系统")?.Value);
    }

    private void RefreshDetailRowTexts()
    {
        if (_currentSections is null || _currentSections.Count < 3) return;
        var details = _currentSections[2].Items;
        for (int i = 0; i < DetailsRepeater.ItemsSourceView.Count && i < details.Count; i++)
        {
            if (DetailsRepeater.TryGetElement(i) is not Grid row) continue;
            var lt = FindChildByName<TextBlock>(row, "LabelText");
            var vt = FindChildByName<TextBlock>(row, "ValueText");
            if (lt is not null) lt.Text = LocalizationService.TranslateHardwareLabel(details[i].Label);
            if (vt is not null)
                vt.Text = _nicknameMode && details[i].NicknameValue is not null
                    ? details[i].NicknameValue!
                    : HardwareTexts.ValueText(details[i].Value);
        }
    }

    private void SetElementStatesToVisible()
    {
        HeaderPanel.Opacity = 1;
        HeaderPanel.RenderTransform = new TranslateTransform { Y = 0 };
        MetricsPanel.Opacity = 1;
        MetricsPanel.RenderTransform = new TranslateTransform { Y = 0 };
        Card1.RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        Card2.RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        Card3.RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        DetailsPanel.Opacity = 1;
        DetailsPanel.RenderTransform = new TranslateTransform { Y = 0 };
    }

    private void SetElementStatesToExit()
    {
        HeaderPanel.Opacity = 0;
        MetricsPanel.Opacity = 0;
        DetailsPanel.Opacity = 0;
    }

    private void SetLoading(bool isLoading)
    {
        LoadingRing.IsActive = isLoading;
        LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Card1_Tapped(object sender, TappedRoutedEventArgs e) => CopyToClipboard(ModelText.Text);
    private void Card2_Tapped(object sender, TappedRoutedEventArgs e) => CopyToClipboard(SystemText.Text);
    private void Card3_Tapped(object sender, TappedRoutedEventArgs e) => CopyToClipboard(UptimeText.Text);

    private void DetailItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
    }

    private void DetailItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not HardwareInfoItem item) return;
        CopyToClipboard(item.Value);
    }

    private void CopyToClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        ShowCopyToast(text);
    }

    private DispatcherTimer? _statusBarTimer;

    private void ShowCopyToast(string text)
    {
        StatusBar.Title = L("Hw_Copied", MiscTexts.T("已复制"));
        StatusBar.Message = text.Length > 80 ? text[..80] + "…" : text;
        StatusBar.Severity = InfoBarSeverity.Success;
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

    private void DetailsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 0 || args.Element is not Grid el) return;

        if (args.Index % 2 == 1)
        {
            var brush = App.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var b) ? b : null;
            if (brush is not null) el.Background = (Microsoft.UI.Xaml.Media.Brush)brush;
        }

        if (DetailsRepeater.ItemsSource is IReadOnlyList<HardwareInfoItem> items && args.Index < items.Count)
        {
            var item = items[args.Index];

            // 显示层本地化：标签/值/工具提示按当前语言渲染（数据键保持中文，判定不受影响）。
            var labelTb = FindChildByName<TextBlock>(el, "LabelText");
            if (labelTb is not null) labelTb.Text = LocalizationService.TranslateHardwareLabel(item.Label);
            var valueTb = FindChildByName<TextBlock>(el, "ValueText");
            if (valueTb is not null)
                valueTb.Text = _nicknameMode && item.NicknameValue is not null
                    ? item.NicknameValue
                    : HardwareTexts.ValueText(item.Value);
            ToolTipService.SetToolTip(el, LocalizationService.L("Hw_CopyHint", "点击复制"));

            var verifiedBadge = FindChildByName<Border>(el, "VerifiedBadge");
            if (verifiedBadge is not null)
            {
                verifiedBadge.Visibility = item.IsVerified
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        if (!_animatingDetails)
        {
            el.Opacity = 1;
            return;
        }

        var idx = (int)args.Index;
        el.Opacity = 0;

        var delay = TimeSpan.FromMilliseconds(350 + idx * 60);
        var lastIdx = ((IReadOnlyList<HardwareInfoItem>)DetailsRepeater.ItemsSource!).Count - 1;

        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            var sb = new Storyboard();
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            sb.Begin();

            if (idx == lastIdx) _animatingDetails = false;
        };
        timer.Start();
    }

    private bool _isScreenshotting;

    private void CopyTextButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CopyToClipboard(BuildTextExport());
            ShowStatusBar(MiscTexts.T("纯文字已复制"), MiscTexts.T("硬件信息文本已复制到剪贴板"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatusBar(MiscTexts.T("复制失败"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void CopyMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CopyToClipboard(BuildMarkdownExport());
            ShowStatusBar(MiscTexts.T("Markdown 已复制"), MiscTexts.T("硬件信息 Markdown 已复制到剪贴板"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatusBar(MiscTexts.T("复制失败"), ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>将当前硬件信息汇总为纯文字文本。</summary>
    private string BuildTextExport()
    {
        var sections = _currentSections;
        if (sections == null || sections.Count == 0)
            return L("Hw_NoData", "暂无硬件信息");

        var sb = new StringBuilder();
        var isFirst = true;
        foreach (var section in sections)
        {
            if (!isFirst) sb.AppendLine();
            isFirst = false;
            sb.AppendLine(MiscTexts.TSub($"【{LocalizationService.TranslateHardwareLabel(section.Title)}】"));
            foreach (var item in section.Items)
            {
                sb.AppendLine($"{LocalizationService.TranslateHardwareLabel(item.Label)}: {HardwareTexts.ValueText(item.Value)}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>将当前硬件信息汇总为 Markdown 文本。</summary>
    private string BuildMarkdownExport()
    {
        var sections = _currentSections;
        if (sections == null || sections.Count == 0)
            return L("Hw_NoData", "暂无硬件信息");

        var sb = new StringBuilder();
        var isFirst = true;
        foreach (var section in sections)
        {
            if (!isFirst) sb.AppendLine();
            isFirst = false;
            sb.AppendLine($"## {LocalizationService.TranslateHardwareLabel(section.Title)}");
            sb.AppendLine();
            sb.AppendLine(L("Hw_MarkdownHeader", "| 项目 | 详情 |"));
            sb.AppendLine("| --- | --- |");
            foreach (var item in section.Items)
            {
                sb.AppendLine($"| {EscapeMarkdownCell(LocalizationService.TranslateHardwareLabel(item.Label))} | {EscapeMarkdownCell(HardwareTexts.ValueText(item.Value))} |");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string EscapeMarkdownCell(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", "<br>");
    }

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScreenshotting) return;
        _isScreenshotting = true;

        try
        {
            var statusWasOpen = StatusBar.IsOpen;
            StatusBar.IsOpen = false;

            HeaderButtons.Visibility = Visibility.Collapsed;

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(LayoutRoot);

            HeaderButtons.Visibility = Visibility.Visible;

            if (statusWasOpen) StatusBar.IsOpen = true;

            var pixelWidth = rtb.PixelWidth;
            var pixelHeight = rtb.PixelHeight;
            var pixels = await GetPixelsAsync(rtb);

            using var contentBmp = new Bitmap(pixelWidth, pixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bmpData = contentBmp.LockBits(new System.Drawing.Rectangle(0, 0, pixelWidth, pixelHeight), ImageLockMode.WriteOnly, contentBmp.PixelFormat);
            Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
            contentBmp.UnlockBits(bmpData);

            Bitmap? bgBmp = null;
            float bgOpacity = 0.15f;
            var mainWindowBg = (App.MainWindow as MainWindow)?.GetBackgroundImage();
            if (mainWindowBg is { Visibility: Visibility.Visible } && mainWindowBg.Source is not null)
            {
                bgOpacity = (float)mainWindowBg.Opacity;
                var bgRtb = new RenderTargetBitmap();
                await bgRtb.RenderAsync(mainWindowBg);
                var bgPixels = await GetPixelsAsync(bgRtb);
                bgBmp = new Bitmap(bgRtb.PixelWidth, bgRtb.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bgBmpData = bgBmp.LockBits(new System.Drawing.Rectangle(0, 0, bgRtb.PixelWidth, bgRtb.PixelHeight), ImageLockMode.WriteOnly, bgBmp.PixelFormat);
                Marshal.Copy(bgPixels, 0, bgBmpData.Scan0, bgPixels.Length);
                bgBmp.UnlockBits(bgBmpData);
            }

            var padding = 56;
            var totalW = pixelWidth + padding * 2;
            var totalH = pixelHeight + padding * 2;

            var isDark = ThemeService.CurrentTheme == AppTheme.Dark ||
                         (ThemeService.CurrentTheme == AppTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

            var outerBg1 = isDark
                ? System.Drawing.Color.FromArgb(255, 32, 32, 32)
                : System.Drawing.Color.FromArgb(255, 243, 243, 243);
            var outerBg2 = isDark
                ? System.Drawing.Color.FromArgb(255, 24, 24, 40)
                : System.Drawing.Color.FromArgb(255, 235, 238, 248);
            using var finalBmp = new Bitmap(totalW, totalH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(finalBmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            using (var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Point(0, 0),
                new System.Drawing.Point(totalW, totalH),
                outerBg1, outerBg2))
            {
                g.FillRectangle(bgBrush, 0, 0, totalW, totalH);
            }

            if (bgBmp is not null)
            {
                var bgColorMatrix = new System.Drawing.Imaging.ColorMatrix(new float[][]
                {
                    new float[] {1, 0, 0, 0, 0},
                    new float[] {0, 1, 0, 0, 0},
                    new float[] {0, 0, 1, 0, 0},
                    new float[] {0, 0, 0, bgOpacity, 0},
                    new float[] {0, 0, 0, 0, 1}
                });
                using var bgImgAttr = new System.Drawing.Imaging.ImageAttributes();
                bgImgAttr.SetColorMatrix(bgColorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(bgBmp,
                    new System.Drawing.Rectangle(padding, padding, pixelWidth, pixelHeight),
                    0, 0, bgBmp.Width, bgBmp.Height,
                    GraphicsUnit.Pixel, bgImgAttr);
            }

            g.DrawImage(contentBmp, padding, padding, pixelWidth, pixelHeight);

            using var ms = new MemoryStream();
            finalBmp.Save(ms, ImageFormat.Png);
            ms.Seek(0, SeekOrigin.Begin);

            var inMemStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var bytes = ms.ToArray();
            var winBuffer = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes);
            await inMemStream.WriteAsync(winBuffer);
            inMemStream.Seek(0);

            var dataPackage = new DataPackage();
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(inMemStream));
            dataPackage.RequestedOperation = DataPackageOperation.Copy;
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();

            ShowStatusBar(MiscTexts.T("截图已复制到剪贴板"), MiscTexts.T("可直接粘贴使用"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatusBar(MiscTexts.T("截图失败"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isScreenshotting = false;
        }
    }

    private static async Task<int[]> GetPixelsAsync(RenderTargetBitmap rtb)
    {
        var buffer = await rtb.GetPixelsAsync();
        var bytes = new byte[buffer.Length];
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.CopyTo(buffer, bytes);
        var pixels = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            var a = (c >> 24) & 0xFF;
            var r = (c >> 16) & 0xFF;
            var g = (c >> 8) & 0xFF;
            var b = c & 0xFF;
            pixels[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }
        return pixels;
    }

    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found && found.Name == name) return found;
            var result = FindChildByName<T>(child, name);
            if (result is not null) return result;
        }
        return null;
    }

    private bool? _currentLayoutFitScreen;

    private void UpdateLayoutStructure()
    {
        var fitScreen = AppSettings.GetBool("HardwareFitScreen", true);
        if (_currentLayoutFitScreen == fitScreen) return;
        _currentLayoutFitScreen = fitScreen;

        if (LayoutRoot.Parent is Viewbox parentViewbox)
            parentViewbox.Child = null;
        else if (LayoutRoot.Parent is ScrollViewer parentScroll)
            parentScroll.Content = null;

        RootHost.Child = null;

        if (!fitScreen)
        {
            LayoutRoot.Width = double.NaN;
            LayoutRoot.MaxWidth = 1100;
            LayoutRoot.HorizontalAlignment = HorizontalAlignment.Center;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            RootHost.Child = scroll;
            scroll.Content = LayoutRoot;
        }
        else
        {
            LayoutRoot.Width = 1100;
            LayoutRoot.MaxWidth = 1100;
            LayoutRoot.HorizontalAlignment = HorizontalAlignment.Stretch;

            var viewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly
            };
            RootHost.Child = viewbox;
            viewbox.Child = LayoutRoot;
        }
    }

    private DispatcherTimer? _statusBarAutoCloseTimer;

    private void ShowStatusBar(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;

        _statusBarAutoCloseTimer?.Stop();
        _statusBarAutoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusBarAutoCloseTimer.Tick += (s, e) =>
        {
            StatusBar.IsOpen = false;
            ((DispatcherTimer)s!).Stop();
        };
        _statusBarAutoCloseTimer.Start();
    }
}
