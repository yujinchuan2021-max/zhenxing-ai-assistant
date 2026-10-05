using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Models;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using Windows.Graphics.Imaging;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>Real popup and compiled download templates with in-memory rows; no live queue or actions.</summary>
internal static class DownloadFlyoutThemeCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("DownloadFlyout_Empty_NativeThemeCycleAndReopen", () => NativeFlyoutAsync(withItems: false)),
        ("DownloadFlyout_Items_NativeThemeCycleAndReopen", () => NativeFlyoutAsync(withItems: true)),
    ];

    private static async Task NativeFlyoutAsync(bool withItems)
    {
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        string declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT") ?? "";
        Assert.False(string.IsNullOrWhiteSpace(declared));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        Assert.Equal(Path.GetFullPath(declared), Path.GetFullPath(DataRoots.EffectiveTestRoot!), ignoreCase: true);
        var previousResolver = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        try
        {
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
            {
                var previousTheme = root.RequestedTheme;
                var previousBackground = root.Background;
                var anchor = new Button { Content = "下载队列", HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20) };
                var expectedSurface = Assert.IsType<Border>(XamlReader.Load("""
                    <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        Width="1" Height="1" Opacity="0" Background="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
                    """));
                var rows = withItems ? FakeRows(declared) : Array.Empty<DownloadItem>();
                var content = new DownloadQueueFlyout(rows);
                var flyout = DownloadQueueFlyout.CreateThemedFlyout(anchor, content);
                var nativeSurfaces = new Dictionary<ElementTheme, (Brush Background, Thickness Padding)>();
                try
                {
                    root.RequestedTheme = ElementTheme.Light;
                    root.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
                    root.Children.Add(anchor);
                    root.Children.Add(expectedSurface);
                    await SettleAsync(root);
                    flyout.ShowAt(anchor);
                    await WaitUntilAsync(() => content.IsLoaded && Presenter(content) is not null);
                    var models = content.Items.ToArray();
                    foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                    {
                        root.RequestedTheme = theme;
                        root.Background = new SolidColorBrush(theme == ElementTheme.Light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                        await SettleAsync(root);
                        var presenter = Assert.IsType<FlyoutPresenter>(Presenter(content));
                        AssertTheme(flyout, presenter, content, rows, expectedSurface, theme);
                        nativeSurfaces[theme] = (presenter.Background, presenter.Padding);
                        Assert.Equal(models.Length, content.Items.Count);
                        for (int i = 0; i < models.Length; i++) Assert.Same(models[i], content.Items[i]);
                    }

                    flyout.Hide();
                    await WaitUntilAsync(() => !content.IsLoaded);
                    root.RequestedTheme = ElementTheme.Dark;
                    await SettleAsync(root);
                    Assert.Equal(ElementTheme.Light, content.RequestedTheme); // Closed popup detached its theme listener.
                    flyout.ShowAt(anchor);
                    await WaitUntilAsync(() => content.IsLoaded && Presenter(content) is not null);
                    await SettleAsync(root);
                    AssertTheme(flyout, Assert.IsType<FlyoutPresenter>(Presenter(content)), content, rows, expectedSurface, ElementTheme.Dark);

                    root.Children.Remove(anchor); // Host removal also closes and detaches the popup.
                    await WaitUntilAsync(() => !content.IsLoaded);
                    Console.WriteLine($"DOWNLOAD_FLYOUT_NATIVE|items={rows.Length}|themes=light-dark-light|reopen=dark|host-unload=closed|queue=not-subscribed|actions=not-run");
                    if (Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1")
                    {
                        // Popup redirection surfaces cannot be captured by RenderTargetBitmap.
                        // Only after all real-popup assertions pass, reuse the same compiled
                        // content under an ordinary native Border with the observed popup brush.
                        flyout.Content = null;
                        await SettleAsync(root);
                        await CaptureMountedContentAsync(root, content, nativeSurfaces, withItems);
                    }
                }
                finally
                {
                    flyout.Hide();
                    root.Children.Remove(anchor);
                    root.Children.Remove(expectedSurface);
                    root.RequestedTheme = previousTheme;
                    root.Background = previousBackground;
                    await SettleAsync(root);
                }
            });
        }
        finally
        {
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousResolver;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
        }
    }

    private static async Task CaptureMountedContentAsync(Grid root, DownloadQueueFlyout content,
        Dictionary<ElementTheme, (Brush Background, Thickness Padding)> nativeSurfaces, bool withItems)
    {
        var models = content.Items.ToArray();
        var surface = new Border
        {
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24),
        };
        try
        {
            root.Children.Add(surface);
            await WaitUntilAsync(() => surface.IsLoaded && content.IsLoaded);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = surface.RequestedTheme = content.RequestedTheme = theme;
                root.Background = new SolidColorBrush(theme == ElementTheme.Light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                surface.Background = nativeSurfaces[theme].Background;
                surface.Padding = nativeSurfaces[theme].Padding;
                await SettleAsync(root);
                Assert.Equal(theme, content.ActualTheme);
                Assert.True(surface.ActualWidth > 0 && surface.ActualHeight > 0);
                Assert.True(content.ActualWidth > 0 && content.ActualHeight > 0);
                Assert.Equal(models.Length, content.Items.Count);
                for (int i = 0; i < models.Length; i++) Assert.Same(models[i], content.Items[i]);
                AssertOpaqueSolid(surface.Background);
                await SaveRequiredPreviewAsync(surface,
                    $"download-flyout-{(withItems ? "items" : "empty")}-{theme.ToString().ToLowerInvariant()}.png");
            }
        }
        finally
        {
            surface.Child = null;
            root.Children.Remove(surface);
            await SettleAsync(root);
        }
    }

    private static async Task SaveRequiredPreviewAsync(FrameworkElement control, string fileName)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(control);
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0,
            $"The mounted download content must produce preview pixels (layout={control.ActualWidth}x{control.ActualHeight}).");
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[buffer.Length];
        buffer.CopyTo(pixels);
        var visibleColors = new HashSet<uint>();
        for (int i = 0; i + 3 < pixels.Length && visibleColors.Count <= 4; i += 4)
            if (pixels[i + 3] > 0)
                visibleColors.Add((uint)(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16 | pixels[i + 3] << 24));
        Assert.True(visibleColors.Count > 4, "The native preview is blank or transparent.");
        var declared = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")!;
        var path = Path.Combine(Path.GetFullPath(declared), fileName);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        using (var stream = file.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        Assert.True(new FileInfo(path).Length > 100, "The required native preview PNG was not written.");
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(path).Take(8).ToArray());
        Console.WriteLine($"DOWNLOAD_FLYOUT_PREVIEW|{path}|{bitmap.PixelWidth}x{bitmap.PixelHeight}|same-content=true|observed-native-theme-brush=true|content-template-preview=not-popup-compositor-pixels");
        Console.Out.Flush();
    }

    private static DownloadItem[] FakeRows(string isolationRoot)
    {
        var queued = DownloadItem.CreateDirect("模拟下载任务", "https://example.invalid/not-requested", Path.Combine(isolationRoot, "never-created-queued"), description: "只渲染内容，不发起下载");
        var failed = DownloadItem.CreateDirect("模拟失败任务", "https://example.invalid/not-requested", Path.Combine(isolationRoot, "never-created-failed"), description: "按钮保留原有操作，不执行");
        failed.State = DownloadItemState.Failed;
        failed.ErrorMessage = "模拟网络暂不可用";
        return [queued, failed];
    }

    private static void AssertTheme(Flyout flyout, FlyoutPresenter presenter, DownloadQueueFlyout content,
        DownloadItem[] rows, Border expectedSurface, ElementTheme theme)
    {
        Assert.Equal(theme, presenter.ActualTheme);
        Assert.Equal(theme, content.ActualTheme);
        Assert.True(presenter.ActualWidth > 0 && presenter.ActualHeight > 0);
        Assert.True(VisualTreeHelper.GetChildrenCount(presenter) > 0);
        Assert.Null(flyout.SystemBackdrop);
        var popup = Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
            .Where(candidate => candidate.IsOpen && candidate.Child is { } child &&
                Flatten(child).Any(node => ReferenceEquals(node, presenter))));
        Assert.Null(popup.SystemBackdrop);
        // Check the brush that the live native template actually paints, not Acrylic.FallbackColor.
        // The old implementation must fail here even when all ActualTheme properties are correct.
        var background = AssertOpaqueSolid(presenter.Background);
        Assert.Same(content.Background, presenter.Background);
        var templateSurface = Assert.IsType<Border>(VisualTreeHelper.GetChild(presenter, 0));
        Assert.Same(presenter.Background, templateSurface.Background);
        Assert.Equal(1d, presenter.Opacity);
        Assert.Equal(1d, templateSurface.Opacity);
        Assert.True(templateSurface.ActualWidth >= content.ActualWidth);
        Assert.True(templateSurface.ActualHeight >= content.ActualHeight);
        Assert.True(expectedSurface.IsLoaded);
        Assert.Equal(theme, expectedSurface.ActualTheme);
        Assert.Equal(AssertOpaqueSolid(expectedSurface.Background), background);
        Console.WriteLine($"DOWNLOAD_FLYOUT_SURFACE|theme={theme}|actual-solid={background}|native-reference={ColorOf(expectedSurface.Background)}|template-border=opaque|flyout-backdrop=null|popup-backdrop=null");
        Assert.True(theme == ElementTheme.Light ? Luminance(background) > 0.65 : Luminance(background) < 0.2,
            $"The native popup background did not follow {theme}: {background}.");
        foreach (var text in Flatten(content).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text) && IsVisible(text)))
        {
            Assert.Equal(theme, text.ActualTheme);
            var surface = background;
            // Compose the native item-card and badge fills over the popup surface.
            var borders = new List<Border>();
            for (var parent = VisualTreeHelper.GetParent(text); parent is not null && parent != presenter; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Border { Background: SolidColorBrush } border) borders.Add(border);
            foreach (var border in borders.AsEnumerable().Reverse()) surface = Composite(ColorOf(border.Background), surface);
            AssertContrast(surface, Composite(ColorOf(text.Foreground), surface), 4.5);
        }
        Assert.Equal(rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            Assert.IsType<StackPanel>(content.FindName("EmptyState")).Visibility);
        foreach (var item in rows)
        {
            Assert.Contains(Flatten(content).OfType<TextBlock>(), text => text.Text == item.DisplayName && IsVisible(text));
            Assert.Contains(Flatten(content).OfType<Button>(), button => Equals(button.Tag, item.Id) && IsVisible(button));
        }
    }

    private static FlyoutPresenter? Presenter(DependencyObject content)
    {
        for (var parent = VisualTreeHelper.GetParent(content); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is FlyoutPresenter presenter) return presenter;
        return null;
    }
    private static bool IsVisible(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }
    private static IEnumerable<DependencyObject> Flatten(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Flatten(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Color ColorOf(Brush brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        _ => throw new InvalidOperationException("Unexpected native theme brush: " + brush?.GetType().Name),
    };
    private static Color AssertOpaqueSolid(Brush brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal((byte)255, solid.Color.A);
        Assert.Equal(1d, solid.Opacity);
        return solid.Color;
    }
    private static Color Composite(Color foreground, Color background)
    {
        double alpha = foreground.A / 255d;
        byte Mix(byte a, byte b) => (byte)Math.Round(a * alpha + b * (1 - alpha));
        return Color.FromArgb(255, Mix(foreground.R, background.R), Mix(foreground.G, background.G), Mix(foreground.B, background.B));
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { double v = value / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
    private static void AssertContrast(Color background, Color text, double minimum)
    {
        double a = Luminance(background), b = Luminance(text);
        double contrast = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        Assert.True(contrast >= minimum, $"Download popup text contrast is {contrast:F2}; expected at least {minimum}.");
    }
    private static async Task SettleAsync(Grid root) { await Task.Delay(200); root.UpdateLayout(); }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
        Assert.True(condition(), "The isolated native download popup did not reach its expected state.");
    }
}
