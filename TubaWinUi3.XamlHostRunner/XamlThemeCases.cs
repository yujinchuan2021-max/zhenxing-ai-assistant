// 【本轮·主审方案】主题解析验收用例（自 TubaWinUi3.Tests/ThemeResourceResolutionTests.cs 搬迁）。
//
// 这些断言需要"真实 XAML 核心"（XAML 有线程亲和性）：现在由专用子进程宿主（本项目的 Program）
// 在标准 STA Main + Application.Start 的 UI 线程上执行；xunit 侧只启动子进程并检查
// 结构化逐用例结果 / 退出码 / 超时——不再在 testhost 进程内 Application.Start。
//
// 断言语义与搬迁前逐字一致（不降低断言）；本文件不含任何真实模型请求。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using Windows.UI;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

internal static class XamlThemeCases
{
    public static (string Name, Func<Task> Body)[] All() =>
    [
        ("B1_DemoHistory_Themes", () => { B1(); return Task.CompletedTask; }),
        ("B2_Refresh_KeepsStreaming", () => { B2(); return Task.CompletedTask; }),
        ("B3_AccentButton_TwoThemes", () => { B3(); return Task.CompletedTask; }),
        ("B4_MarkdownRepeatedRenderAndGc", () => { B4(); return Task.CompletedTask; }),
        ("C2_StatusBrush_TwoThemes", () => { C2(); return Task.CompletedTask; }),
        ("D1_Scope_Unsubscribes", () => { D1(); return Task.CompletedTask; }),
        ("F1_FontSingleSource_VisibleNodes", F1_FontSingleSource),
        ("F2_AppFontDictionary_InjectKeys", F2_AppFontDictionary),
        ("F3_MergedImplicitStyle", F3_MergedImplicitStyle),
    ];

    // ---------- 离线演示历史：真实 Render 路径 ----------

    private const string DemoMarkdown = """
        ## 演示标题

        - 列表项一
        - 列表项二

        推荐阅读 [图吧工具箱官网](https://example.com/tuba) ，命令行用 `dotnet test` 验证。

        ```powershell
        winget install TubaWinUi3
        ```

        | 项目 | 状态 |
        | --- | --- |
        | 步骤链 | 通过 |
        | 主题刷色 | 通过 |

        [RECOMMEND_TOOL] CPU-Z | reason=查看硬件信息
        [WEBSITE] https://example.com/tuba | desc=图吧工具箱官网
        """;

    // ---------- B1：两态刷色 + 就地重刷（元素实例不变） ----------

    private static void B1()
    {
        using var env = new ThemeTestEnvironment("Light");
        var container = AiMarkdownRenderer.Render(DemoMarkdown);
        // Collect before enumeration pins any original projected inline wrapper.
        // Native Inlines/Children alone must not make theme bindings disappear.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // 浅色态：表格 / 链接 / 代码 / 列表 / 推荐卡 全部按浅色字典取值
        AssertPainted(container, "Light");

        // 记录整棵树的元素实例顺序（内容不重建的基线）
        var before = Flatten(container);

        // 切到深色：与生产同一条主题变化广播 → 就地重刷
        ThemeResourceResolver.CurrentThemeKeyOverrideForTest = "Dark";
        ThemeRefreshScope.BroadcastRefresh();

        AssertPainted(container, "Dark");

        var after = Flatten(container);
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Same(before[i], after[i]);
    }

    // ---------- B2：重刷保流式内容/展开态；状态色切到新主题 ----------

    private static void B2()
    {
        using var env = new ThemeTestEnvironment("Light");

        // 流式定稿内容（Render 产物）+ 折叠态步骤链
        var container = AiMarkdownRenderer.Render(DemoMarkdown);
        var textBlocksBefore = Flatten(container).OfType<RichTextBlock>().ToList();
        Assert.NotEmpty(textBlocksBefore);
        var textsBefore = textBlocksBefore.Select(RunsText).ToList();

        var run = new RunVm { IsExpanded = false, SummaryText = "2 步完成" };
        run.Steps.Add(new StepRowVm(DemoStep(AgentStepStatus.Success)));
        run.Steps.Add(new StepRowVm(DemoStep(AgentStepStatus.Failed)));

        ThemeResourceResolver.CurrentThemeKeyOverrideForTest = "Dark";
        ThemeRefreshScope.BroadcastRefresh();

        // ① 内容未被重建：同一批 RichTextBlock 实例、同一段文本
        var textBlocksAfter = Flatten(container).OfType<RichTextBlock>().ToList();
        Assert.Equal(textBlocksBefore.Count, textBlocksAfter.Count);
        for (var i = 0; i < textBlocksBefore.Count; i++)
        {
            Assert.Same(textBlocksBefore[i], textBlocksAfter[i]);
            Assert.Equal(textsBefore[i], RunsText(textBlocksAfter[i]));
        }

        // ② 展开/折叠状态与摘要未被重置
        Assert.False(run.IsExpanded);
        Assert.Equal("2 步完成", run.SummaryText);

        // ③ 步骤链状态色已切到深色
        AssertThemeColor(run.Steps[0].StatusBrush, "Dark", "SystemFillColorSuccessBrush");
        AssertThemeColor(run.Steps[1].StatusBrush, "Dark", "SystemFillColorCriticalBrush");
    }

    // ---------- B3：操作卡强调按钮背景走主题资源 ----------

    private static void B3()
    {
        // [ACTION] 卡（需确认的操作）——"全部确认并执行"按钮背景走主题资源
        const string markdown = """
            [ACTION]
            [{"kind":"run_command","description":"清理临时文件","detail":"del /q %TEMP%\\*","reason":"释放空间"}]
            """;

        using var env = new ThemeTestEnvironment("Light");
        var container = AiMarkdownRenderer.Render(markdown);

        var accentButton = Flatten(container).OfType<Button>().FirstOrDefault(b =>
            b.Content is StackPanel sp && sp.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("全部确认")));
        Assert.NotNull(accentButton);
        AssertThemeColor(accentButton!.Background, "Light", "AccentFillColorDefaultBrush");

        ThemeResourceResolver.CurrentThemeKeyOverrideForTest = "Dark";
        ThemeRefreshScope.BroadcastRefresh();
        AssertThemeColor(accentButton.Background, "Dark", "AccentFillColorDefaultBrush");
    }

    private static void B4()
    {
        using var env = new ThemeTestEnvironment("Light");
        var richText = new RichTextBlock();
        MarkdownTextService.RenderToRichTextBlock(richText, "[prior](https://example.com/prior)");
        var paletteCount = richText.Resources.MergedDictionaries.Count;
        var priorScope = ThemeRefreshScope.FindScopeForTest(richText);
        Assert.NotNull(priorScope);
        var priorRun = Flatten(richText).OfType<Run>().Single(run => run.Text == "prior");
        var previousScope = priorScope;
        for (var i = 0; i < 4; i++)
        {
            MarkdownTextService.RenderToRichTextBlock(richText, $"[current-{i}](https://example.com/current-{i})");
            Assert.Equal(paletteCount, richText.Resources.MergedDictionaries.Count);
            var currentScope = ThemeRefreshScope.FindScopeForTest(richText);
            Assert.NotNull(currentScope);
            Assert.NotSame(previousScope, currentScope);
            Assert.True(previousScope!.IsDisposedForTest);
            Assert.Equal(0, previousScope.BindingCountForTest);
            Assert.Equal(0, previousScope.RetainedTargetCountForTest);
            Assert.True(currentScope!.RetainedTargetCountForTest > 0);
            Assert.Equal(1, ThemeRefreshScope.LiveScopeCountForTest);
            previousScope = currentScope;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        ThemeResourceResolver.CurrentThemeKeyOverrideForTest = "Dark";
        ThemeRefreshScope.BroadcastRefresh();
        var currentRun = Flatten(richText).OfType<Run>().Single(run => run.Text == "current-3");
        AssertContentColor(currentRun.Foreground, "Dark", "AccentTextFillColorPrimaryBrush");
        AssertContentColor(priorRun.Foreground, "Light", "AccentTextFillColorPrimaryBrush");

        previousScope!.Dispose();
        Assert.True(previousScope.IsDisposedForTest);
        Assert.Equal(0, previousScope.BindingCountForTest);
        Assert.Equal(0, previousScope.RetainedTargetCountForTest);
        Assert.Null(ThemeRefreshScope.FindScopeForTest(richText));
        Assert.Equal(0, ThemeRefreshScope.LiveScopeCountForTest);
        Assert.False(ThemeRefreshScope.SubscribedForTest);
        ThemeResourceResolver.CurrentThemeKeyOverrideForTest = "Light";
        ThemeRefreshScope.BroadcastRefresh();
        AssertContentColor(currentRun.Foreground, "Dark", "AccentTextFillColorPrimaryBrush");
    }

    // ---------- C2：步骤链状态色按实际主题解析 + RefreshTheme 取新值 ----------

    private static void C2()
    {
        using var env = new ThemeTestEnvironment("Light");

        // 主题上下文：步骤行所在控件（这里用真实元素充当宿主）
        var host = new StackPanel();
        var row = new StepRowVm(DemoStep(AgentStepStatus.Success));
        row.AttachThemeHost(host);

        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        AssertThemeColor(row.StatusBrush, "Light", "SystemFillColorSuccessBrush");

        // 主题切换：RefreshTheme 通知后必须取到【新主题】的值
        ThemeResourceResolver.CurrentThemeKeyOverrideForTest = "Dark";
        row.RefreshTheme();

        Assert.Contains(nameof(StepRowVm.StatusBrush), notified);
        AssertThemeColor(row.StatusBrush, "Dark", "SystemFillColorSuccessBrush");

        var failed = new StepRowVm(DemoStep(AgentStepStatus.Failed));
        failed.AttachThemeHost(host);
        AssertThemeColor(failed.StatusBrush, "Dark", "SystemFillColorCriticalBrush");
    }

    // ---------- D1：容器 Unloaded 后退订 ----------

    private static void D1()
    {
        using var env = new ThemeTestEnvironment("Light");
        var container = AiMarkdownRenderer.Render(DemoMarkdown);

        Assert.True(ThemeRefreshScope.SubscribedForTest);
        var scope = ThemeRefreshScope.FindScopeForTest(container);
        Assert.NotNull(scope);

        // 模拟容器 Unloaded（页面卸载/控件回收）
        scope!.Detach();

        Assert.False(ThemeRefreshScope.SubscribedForTest);
        Assert.Equal(0, ThemeRefreshScope.LiveScopeCountForTest);

        // 退订后主题变化不再影响已卸载树（不抛、不刷）
        ThemeResourceResolver.CurrentThemeKeyOverrideForTest = "Dark";
        ThemeRefreshScope.BroadcastRefresh();
        AssertContentColor(FirstTableCellBrush(container), "Light", "SubtleFillColorSecondaryBrush");
    }

    // ---------- F1：字体单一入口（app-font.json → AppFonts → 资源键 → 可见文本节点） ----------

    /// <summary>F2：App.xaml 资源字典根（AppFontDictionary）的注入语义——按已保存选择构建字体键，
    /// 覆盖「选择 → 下次启动生效」的核心机制（不触碰真实用户 settings.json，直接调用内部构造）。</summary>
    private static Task F2_AppFontDictionary()
    {
        var loaded = new TubaWinUi3.Services.AppFontDictionary("noto");
        var cc = Assert.IsType<FontFamily>(loaded["ContentControlThemeFontFamily"]);
        var app = Assert.IsType<FontFamily>(loaded["AppFontFamily"]);
        Assert.EndsWith("NotoSansSC-Regular.otf#Noto Sans SC", cc.Source, StringComparison.Ordinal);
        Assert.Equal(cc.Source, app.Source);
        Assert.Null(AppFonts.LastResourceInjectError);

        var dflt = new TubaWinUi3.Services.AppFontDictionary(null);
        var cc2 = Assert.IsType<FontFamily>(dflt["ContentControlThemeFontFamily"]);
        Assert.EndsWith("SarasaUiSC-Regular.ttf#Sarasa UI SC", cc2.Source, StringComparison.Ordinal);

        var bogus = new TubaWinUi3.Services.AppFontDictionary("no-such-choice");
        var cc3 = Assert.IsType<FontFamily>(bogus["ContentControlThemeFontFamily"]);
        Assert.Equal(cc2.Source, cc3.Source);   // 未知选择 → 目录首项（更纱）

        Console.WriteLine($"F2: noto={cc.Source}; default={cc2.Source}; injectError={AppFonts.LastResourceInjectError ?? "(null)"}");
        return Task.CompletedTask;
    }

    /// <summary>F3：合并字典里的「隐式样式」仍应用 —— App.xaml 把无 x:Key 的样式从自定义字典根下移到
    /// 合并层内置 ResourceDictionary 的语义前提（不成立则全局文本字体不生效；2026-09-23 实机修复的验证点）。</summary>
    private static async Task F3_MergedImplicitStyle()
    {
        // Keep an empty owned Window alive while this font contract creates and
        // closes its own probe Window; do not end WinUI's application message pump.
        await ChatScrollProbeCases.WithWindowAsync((_, _) => Task.CompletedTask);
        Window? window = null;
        try
        {
            var app = Application.Current!;
            var saved = app.Resources;
            try
            {
                var probeUri = AppFonts.XamlFontUri;   // 当前生效候选（默认 = 更纱）
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily(probeUri)));
                var inner = new ResourceDictionary { [typeof(TextBlock)] = style };   // 隐式样式 = 以类型为键的条目
                var outer = new ResourceDictionary();
                outer.MergedDictionaries.Add(inner);
                var root = new ResourceDictionary();
                root.MergedDictionaries.Add(outer);   // 隐式样式只存在于合并层
                app.Resources = root;

                var tb = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Text="合并层隐式样式探针" />""");
                window = new Window { Content = tb };
                var loaded = new TaskCompletionSource();
                tb.Loaded += (_, _) => loaded.TrySetResult();
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 600, 200));
                window.AppWindow.Show(false);
                Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(8000)) == loaded.Task, "F3 探针未 Loaded");
                Console.WriteLine($"F3: merged implicit style -> {tb.FontFamily?.Source ?? "(null)"}");
                Assert.Equal(probeUri, tb.FontFamily?.Source);
            }
            finally { app.Resources = saved; }
        }
        finally { try { window?.Close(); } catch { } }
    }

    private static async Task F1_FontSingleSource()
    {
        await ChatScrollProbeCases.WithWindowAsync((_, _) => Task.CompletedTask);
        Window? window = null;
        string? tempDir = null;
        var evidence = new Dictionary<string, object?>();
        var app = Application.Current!;
        var previousResources = app.Resources;
        try
        {
            // 1) 宿主目录 = 真实打包资产：Assets/Fonts/app-font.json 必须存在并通过校验
            AppFonts.ResetCachesForTest();
            AppFonts.Initialize();
            Assert.Null(AppFonts.LoadError);
            Assert.True(AppFonts.Choices.Count >= 1, "候选字体目录为空");
            var expectedFamily = AppFonts.Choices[0].FamilyName;   // 默认（无已保存选择）= 目录首项
            Assert.Equal(expectedFamily, AppFonts.FamilyName);
            Assert.True(File.Exists(AppFonts.RegularFilePath), "Regular 字体缺失：" + AppFonts.RegularFilePath);
            Assert.True(File.Exists(AppFonts.BoldFilePath), "Bold 字体缺失：" + AppFonts.BoldFilePath);
            Assert.True(File.Exists(AppFonts.LicensePath), "许可文件缺失：" + AppFonts.LicensePath);
            var uri1 = $"ms-appx:///Assets/Fonts/{AppFonts.RegularFileName}#{AppFonts.FamilyName}";
            Assert.Equal(uri1, AppFonts.XamlFontUri);
            evidence["configuredUri"] = uri1;

            // 2) Resource keys must come from font configuration. The isolated
            // host removes product page XBF files but provides valid PRI/type
            // metadata for stock templates; this case explicitly checks the
            // configured font keys and text nodes, without creating a product page.
            Application.Current!.Resources = new ResourceDictionary();
            AppFonts.ApplyToApplicationResources(Application.Current.Resources);
            var ccKey = Assert.IsType<FontFamily>(Application.Current.Resources["ContentControlThemeFontFamily"]);
            var appKey = Assert.IsType<FontFamily>(Application.Current.Resources["AppFontFamily"]);
            Assert.Equal(uri1, ccKey.Source);
            Assert.Equal(uri1, appKey.Source);

            // 3) 可见文本节点：经独立窗口真实加载（隐式样式 / 显式 ThemeResource / 控件内文本 三路）
            Console.WriteLine("F1-S3a: loading fragment");
            var root = (StackPanel)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Padding="8" Spacing="4">
                  <StackPanel.Resources>
                    <Style TargetType="TextBlock"><Setter Property="FontFamily" Value="{ThemeResource AppFontFamily}" /></Style>
                    <Style TargetType="RichTextBlock"><Setter Property="FontFamily" Value="{ThemeResource AppFontFamily}" /></Style>
                  </StackPanel.Resources>
                  <TextBlock x:Name="BareText" Text="裸文本节点字体验证" />
                  <RichTextBlock x:Name="RichText"><Paragraph><Run Text="富文本节点字体验证" /></Paragraph></RichTextBlock>
                  <Button x:Name="StrBtn" Content="字符串按钮" />
                  <TextBlock x:Name="ExplicitText" FontFamily="{ThemeResource AppFontFamily}" Text="显式引用" />
                </StackPanel>
                """);
            window = new Window { Content = root };
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, 800, 400));
            window.AppWindow.Show(false);
            Console.WriteLine("F1-S3b: window shown, waiting Loaded");
            Assert.True(await Task.WhenAny(loaded.Task, Task.Delay(8000)) == loaded.Task, "探针根未 Loaded");
            Console.WriteLine("F1-S3c: root Loaded");
            await Task.Delay(300);
            root.UpdateLayout();

            var bare = (TextBlock?)FindByNameInTree(root, "BareText") ?? throw new InvalidOperationException("BareText 缺失");
            var rich = (RichTextBlock?)FindByNameInTree(root, "RichText") ?? throw new InvalidOperationException("RichText 缺失");
            var strBtn = (Button?)FindByNameInTree(root, "StrBtn") ?? throw new InvalidOperationException("StrBtn 缺失");
            var explicitText = (TextBlock?)FindByNameInTree(root, "ExplicitText") ?? throw new InvalidOperationException("ExplicitText 缺失");
            strBtn.ApplyTemplate();
            root.UpdateLayout();
            await Task.Delay(100);
            var btnInner = FindTextBlocksVisual(strBtn).FirstOrDefault()
                ?? throw new InvalidOperationException("字符串按钮内未找到文本节点");

            var observed = new Dictionary<string, string?>
            {
                ["bareText"] = bare.FontFamily?.Source,
                ["richText"] = rich.FontFamily?.Source,
                ["buttonText"] = btnInner.FontFamily?.Source,
                ["explicitText"] = explicitText.FontFamily?.Source,
            };
            evidence["visibleNodes"] = observed;
            foreach (var pair in observed)
            {
                Assert.True(pair.Value == uri1, $"{pair.Key} 期望 {uri1}，实际 {pair.Value ?? "(null)"}");
                Assert.DoesNotContain("Segoe", pair.Value ?? "");
            }

            // 3b) GDI/Skia 输出（非 XAML 子系统，读同一份配置）：字体替换后这里也要跟随
            using (var gdiFont = AppFonts.CreateGdiFont(12f))
            {
                var gdiName = gdiFont.FontFamily.Name;
                Console.WriteLine($"F1-S3b-GDI: {gdiName}/{gdiFont.Style}");
                // GDI 家族名按当前 UI 区域语言返回本地化名（如「更纱黑体 UI SC」），与 json 里的英文主名不同属正常；
                // 关键是：私有字体集合按名解析成功（未回退到系统 Segoe UI）。
                Assert.False(string.IsNullOrWhiteSpace(gdiName));
                Assert.False(gdiName.StartsWith("Segoe", StringComparison.OrdinalIgnoreCase),
                    $"GDI 私有字体加载失败并回退到系统字体：{gdiName}（期望 {expectedFamily}）");
            }
            var skiaReg = SkiaSharp.SKTypeface.FromFile(AppFonts.RegularFilePath);
            var skiaBold = SkiaSharp.SKTypeface.FromFile(AppFonts.BoldFilePath);
            Console.WriteLine($"F1-S3b-SKIA: reg={skiaReg?.FamilyName}/{skiaReg?.FontStyle.Weight}; bold={skiaBold?.FamilyName}/{skiaBold?.FontStyle.Weight}");
            // Skia 家族名取字体 name 表主名（英文），与 GDI 的本地化名口径不同；语义断言：同一家族、非空、且非系统回退
            Assert.True(skiaReg is not null && !string.IsNullOrEmpty(skiaReg.FamilyName), "Skia Regular 未加载");
            Assert.Equal(skiaReg!.FamilyName, skiaBold?.FamilyName);
            Assert.DoesNotContain("Segoe", skiaReg.FamilyName);
            Assert.True(skiaBold is not null && (int)skiaBold.FontStyle.Weight >= 600,
                "Bold 文件字重应 ≥ SemiBold（按字重选文件修复点的运行时验证）");

            // 4) 换配置（临时目录副本）：族名/文件名/URI 全随 app-font.json 变化，切换后仍无 Segoe 回退
            tempDir = Path.Combine(Path.GetTempPath(), "zxai-font-probe-" + Guid.NewGuid().ToString("N")[..8]);
            var tempFonts = Path.Combine(tempDir, "Assets", "Fonts");
            Directory.CreateDirectory(tempFonts);
            const string probeFamily = "Probe CN Mono";
            const string probeRegular = "Probe-CN-Regular.ttf";
            const string probeBold = "Probe-CN-Bold.ttf";
            File.Copy(AppFonts.RegularFilePath, Path.Combine(tempFonts, probeRegular));
            File.Copy(AppFonts.BoldFilePath, Path.Combine(tempFonts, probeBold));
            File.Copy(AppFonts.LicensePath, Path.Combine(tempFonts, "OFL.txt"));
            File.Copy(AppFonts.RegularFilePath, Path.Combine(tempFonts, "Probe-Mono-Regular.ttf"));
            File.Copy(AppFonts.BoldFilePath, Path.Combine(tempFonts, "Probe-Mono-Bold.ttf"));
            File.WriteAllText(Path.Combine(tempFonts, "app-font.json"), string.Format("""
                {{
                  "uiStackSuffix": "'Segoe UI Variable', 'Segoe UI', system-ui, sans-serif",
                  "monoStack": "'Probe Mono', monospace",
                  "mono": {{
                    "familyName": "Probe Mono",
                    "regular": "Probe-Mono-Regular.ttf",
                    "bold": "Probe-Mono-Bold.ttf",
                    "license": "OFL.txt"
                  }},
                  "choices": [
                    {{
                      "id": "probe",
                      "displayName": "探针候选",
                      "familyName": "{0}",
                      "regular": "{1}",
                      "bold": "{2}",
                      "license": "OFL.txt",
                      "attribution": {{ "author": "Probe Author", "url": "https://example.com/probe-font", "licenseText": "探针许可" }}
                    }}
                  ]
                }}
                """, probeFamily, probeRegular, probeBold));
            AppFonts.ResetCachesForTest();
            AppFonts.InitializeFromDirectory(tempDir);
            Assert.Null(AppFonts.LoadError);
            Assert.Equal(probeFamily, AppFonts.FamilyName);
            Assert.Equal("Probe Author", AppFonts.AttributionAuthor);   // 署名随配置切换（SettingsPage「开源与致谢」唯一来源）
            var uri2 = $"ms-appx:///Assets/Fonts/{probeRegular}#{probeFamily}";
            Assert.Equal(uri2, AppFonts.XamlFontUri);
            AppFonts.ApplyToApplicationResources(Application.Current.Resources);
            var switched = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Text="配置切换后" FontFamily="{ThemeResource AppFontFamily}" />""");
            root.Children.Add(switched);
            await Task.Delay(200);
            root.UpdateLayout();
            Assert.Equal(uri2, switched.FontFamily?.Source);
            evidence["configSwitch"] = new Dictionary<string, string?>
            {
                ["family"] = probeFamily,
                ["uri"] = switched.FontFamily?.Source,
                ["attributionAuthor"] = AppFonts.AttributionAuthor,
            };

            // 恢复正常配置（避免影响后续用例）
            AppFonts.ResetCachesForTest();
            AppFonts.Initialize();
            Assert.Null(AppFonts.LoadError);

            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "font-single-source-result.json"),
                System.Text.Json.JsonSerializer.Serialize(evidence, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { window?.Close(); } catch { }
            // This contract deliberately replaces active application resources
            // and switches to probe font keys. Restore both after the owned
            // window closes so later native templates see their original root.
            app.Resources = previousResources;
            AppFonts.ResetCachesForTest();
            AppFonts.Initialize();
            try { if (tempDir is not null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static DependencyObject? FindByNameInTree(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement fe && fe.Name == name) return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var hit = FindByNameInTree(VisualTreeHelper.GetChild(parent, i), name);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static IEnumerable<TextBlock> FindTextBlocksVisual(DependencyObject parent)
    {
        if (parent is TextBlock tb) yield return tb;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var textBlock in FindTextBlocksVisual(VisualTreeHelper.GetChild(parent, i)))
                yield return textBlock;
    }

    // ---------- 断言/构造辅助（与搬迁前逐字一致） ----------

    private static AgentStep DemoStep(AgentStepStatus status) => new()
    {
        ToolName = "demo_tool",
        DisplayName = "演示步骤",
        Glyph = "\uE713",
        Summary = "离线演示",
        CallId = $"demo-{status}",
        Status = status,
        Result = status == AgentStepStatus.Failed ? null : "ok",
        Error = status == AgentStepStatus.Failed ? "演示失败" : null,
    };

    private static void AssertPainted(StackPanel container, string themeKey)
    {
        var nodes = Flatten(container);

        // 链接
        var linkRun = nodes.OfType<Run>().FirstOrDefault(r => r.Text == "图吧工具箱官网");
        Assert.NotNull(linkRun);
        AssertContentColor(linkRun!.Foreground, themeKey, "AccentTextFillColorPrimaryBrush");

        // 行内代码
        var inlineCode = nodes.OfType<Run>().FirstOrDefault(r => r.Text == "dotnet test");
        Assert.NotNull(inlineCode);
        AssertContentColor(inlineCode!.Foreground, themeKey, "AccentTextFillColorPrimaryBrush");

        // 围栏代码块
        var codeBlock = nodes.OfType<Run>().FirstOrDefault(r => r.Text.Contains("winget install"));
        Assert.NotNull(codeBlock);
        AssertContentColor(codeBlock!.Foreground, themeKey, "TextFillColorSecondaryBrush");

        // 列表符号
        var bullet = nodes.OfType<Run>().FirstOrDefault(r => r.Text.Contains('\u2022'));
        Assert.NotNull(bullet);
        AssertContentColor(bullet!.Foreground, themeKey, "TextFillColorSecondaryBrush");

        // 表格外框 / 表头底色 / 单元格描边 / 交替行底色
        var tableGrid = nodes.OfType<Grid>().FirstOrDefault(g => g.RowDefinitions.Count >= 3);
        Assert.NotNull(tableGrid);
        AssertContentColor(tableGrid!.BorderBrush, themeKey, "CardStrokeColorDefaultBrush");

        var headerCell = tableGrid.Children.OfType<Border>().FirstOrDefault(b => Grid.GetRow(b) == 0);
        Assert.NotNull(headerCell);
        AssertContentColor(headerCell!.Background, themeKey, "SubtleFillColorSecondaryBrush");
        AssertContentColor(headerCell.BorderBrush, themeKey, "ControlStrokeColorDefaultBrush");

        var oddCell = tableGrid.Children.OfType<Border>().FirstOrDefault(b => Grid.GetRow(b) == 1);
        Assert.NotNull(oddCell);
        Assert.Null(oddCell!.Background);

        // 推荐卡 / 网站卡底色
        var card = nodes.OfType<Border>().FirstOrDefault(b => b.Child is Grid g && g.ColumnDefinitions.Count == 3);
        Assert.NotNull(card);
        AssertContentColor(card!.Background, themeKey, "CardBackgroundFillColorDefaultBrush");
    }

    private static Brush? FirstTableCellBrush(StackPanel container)
    {
        var tableGrid = Flatten(container).OfType<Grid>().First(g => g.RowDefinitions.Count >= 3);
        return tableGrid.Children.OfType<Border>().First(b => Grid.GetRow(b) == 0).Background;
    }

    private static void AssertThemeColor(Brush? brush, string themeKey, string key)
    {
        Assert.NotNull(brush);
        var solid = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(ThemeTestEnvironment.Color(themeKey, key), solid.Color);
    }

    // Fixed chat renderings now own the same explicit brand palette as the page.
    // Keep the synthetic system-color oracle above for native button/state paths.
    private static void AssertContentColor(Brush? brush, string themeKey, string role)
    {
        var (light, dark) = role switch
        {
            "AccentTextFillColorPrimaryBrush" => (0xFF4B66ADu, 0xFFA5B7EDu),
            "TextFillColorSecondaryBrush" => (0xFF62646Du, 0xFFB0AFB7u),
            "CardStrokeColorDefaultBrush" or "ControlStrokeColorDefaultBrush" => (0xFFDDD9D4u, 0xFF3D3B3Bu),
            "SubtleFillColorSecondaryBrush" or "CardBackgroundFillColorDefaultBrush" => (0xFFF0EEEBu, 0xFF2B2B2Eu),
            _ => throw new InvalidOperationException("Unknown content palette role: " + role),
        };
        var argb = themeKey == "Dark" ? dark : light;
        var expected = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(brush).Color);
    }

    private static string RunsText(RichTextBlock block)
        => string.Concat(block.Blocks.OfType<Paragraph>()
            .Select(p => p.Inlines.OfType<Run>().Aggregate("", (acc, r) => acc + r.Text)));

    private static IReadOnlyList<DependencyObject> Flatten(DependencyObject root)
    {
        var result = new List<DependencyObject>();
        void Walk(DependencyObject node)
        {
            result.Add(node);
            switch (node)
            {
                case RichTextBlock rtb:
                    foreach (var block in rtb.Blocks) Walk(block);
                    break;
                case Paragraph paragraph:
                    foreach (var inline in paragraph.Inlines.ToList()) Walk(inline);
                    break;
                case Span span:
                    foreach (var inline in span.Inlines.ToList()) Walk(inline);
                    break;
                case Panel panel:
                    foreach (var child in panel.Children) Walk(child);
                    break;
                case Border border when border.Child is { } borderChild:
                    Walk(borderChild);
                    break;
                case Button button when button.Content is DependencyObject buttonContent:
                    Walk(buttonContent);
                    break;
            }
        }

        Walk(root);
        return result;
    }

    /// <summary>注入浅/深两态假资源字典 + 主题键，走真实的解析/刷色/重刷路径。</summary>
    internal sealed class ThemeTestEnvironment : IDisposable
    {
        public ThemeTestEnvironment(string themeKey)
        {
            ThemeRefreshScope.ResetForTest();
            ThemeResourceResolver.ExtraRootDictionariesForTest = [Build("Light"), Build("Dark")];
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = themeKey;
        }

        public void Dispose()
        {
            ThemeRefreshScope.ResetForTest();
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
        }

        /// <summary>每个资源键一个可辨识的颜色（同键浅/深不同，不同键不同）。</summary>
        private static readonly string[] Keys =
        [
            "CardStrokeColorDefaultBrush",
            "ControlStrokeColorDefaultBrush",
            "SubtleFillColorSecondaryBrush",
            "ControlFillColorTransparentBrush",
            "AccentTextFillColorPrimaryBrush",
            "TextFillColorSecondaryBrush",
            "TextFillColorTertiaryBrush",
            "CardBackgroundFillColorDefaultBrush",
            "AccentFillColorDefaultBrush",
            "SystemFillColorSuccessBrush",
            "SystemFillColorCriticalBrush",
        ];

        public static Color Color(string themeKey, string key)
        {
            var index = Array.IndexOf(Keys, key);
            Assert.True(index >= 0, $"测试调色板缺少资源键 {key}");
            var channel = (byte)(index + 1);
            return themeKey == "Dark"
                ? Windows.UI.Color.FromArgb(255, 0, channel, 0)
                : Windows.UI.Color.FromArgb(255, channel, 0, 0);
        }

        private static IThemeResourceDictionary Build(string themeKey)
        {
            var theme = new FakeDictionary();
            foreach (var key in Keys)
                theme.Items[key] = new SolidColorBrush(Color(themeKey, key));
            return new FakeDictionary().WithTheme(themeKey, theme);
        }
    }

    /// <summary>脱离 XAML 的假资源字典（同测试项目版本）。</summary>
    internal sealed class FakeDictionary : IThemeResourceDictionary
    {
        public Dictionary<string, object?> Items { get; } = [];
        public Dictionary<string, IThemeResourceDictionary> Themes { get; } = [];
        public List<IThemeResourceDictionary> Merged { get; } = [];

        public object? Lookup(string key) => Items.TryGetValue(key, out var value) ? value : null;

        public IThemeResourceDictionary? ThemeDictionary(string themeKey)
            => Themes.TryGetValue(themeKey, out var dictionary) ? dictionary : null;

        public IReadOnlyList<IThemeResourceDictionary> MergedDictionaries => Merged;

        public FakeDictionary With(string key, object? value)
        {
            Items[key] = value;
            return this;
        }

        public FakeDictionary WithTheme(string themeKey, FakeDictionary theme)
        {
            Themes[themeKey] = theme;
            return this;
        }

        public FakeDictionary WithMerged(IThemeResourceDictionary merged)
        {
            Merged.Add(merged);
            return this;
        }
    }
}
