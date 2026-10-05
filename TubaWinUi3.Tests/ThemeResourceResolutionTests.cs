using System.Runtime.ExceptionServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using Windows.UI;

namespace TubaWinUi3.Tests;

/// <summary>
/// 主题解析统一验证（hermes-theme-resolution-followup / hermes-richtext-theme-followup）：
///  A. [主题 + 资源键] 逐层解析语义（脱离 XAML，用假字典；含"页面字典只含 Assistant* 键就把
///     查询带偏"的退化坑）；
///  B. 离线演示历史（表格 + 链接 + 代码 + 列表 + 推荐卡）走真实 AiMarkdownRenderer.Render
///     路径，断言浅/深两态的刷色生效，并断言重刷是【就地】的（元素实例不变 → 流式内容、
///     滚动位置、展开状态都保留）；
///  C. 步骤链 StepRowVm 状态色按【实际主题】解析，RefreshTheme 后取到新主题的值；
///  D. 容器退订语义（Unloaded 后停止接收主题变化）。
/// 全程不发真实模型请求：内容是用例内构造的离线演示历史。
/// XAML 断言体经 <see cref="XamlHost"/> 在独立 STA 线程的 XAML 核心上执行（XAML 有线程亲和性）；
/// 宿主起不来时按 Skip 处理，不伪造通过。
/// </summary>
public class ThemeResourceResolutionTests
{
    // ---------- 假字典：脱离 XAML 验证解析语义 ----------

    private sealed class FakeDictionary : IThemeResourceDictionary
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

    [Theory]
    [InlineData(ElementTheme.Light, "Light")]
    [InlineData(ElementTheme.Dark, "Dark")]
    [InlineData(ElementTheme.Default, null)]
    public void A1_ThemeKeyOf_MapsOnlyLightAndDark(ElementTheme theme, string? expected)
        => Assert.Equal(expected, ThemeResourceResolver.ThemeKeyOf(theme));

    [Fact]
    public void A2_TryResolve_PicksValueOfRequestedTheme()
    {
        var scope = new FakeDictionary()
            .WithTheme("Light", new FakeDictionary().With("AccentTextFillColorPrimaryBrush", "LIGHT_ACCENT"))
            .WithTheme("Dark", new FakeDictionary().With("AccentTextFillColorPrimaryBrush", "DARK_ACCENT"));

        Assert.True(ThemeResourceResolver.TryResolve([scope], "Light", "AccentTextFillColorPrimaryBrush", out var light));
        Assert.True(ThemeResourceResolver.TryResolve([scope], "Dark", "AccentTextFillColorPrimaryBrush", out var dark));
        Assert.Equal("LIGHT_ACCENT", light);
        Assert.Equal("DARK_ACCENT", dark);
    }

    [Fact]
    public void A3_TryResolve_PageLevelWins_OverAppLevel()
    {
        var page = new FakeDictionary().With("MyBrush", "PAGE");
        var app = new FakeDictionary().With("MyBrush", "APP");

        Assert.True(ThemeResourceResolver.TryResolve([page, app], "Light", "MyBrush", out var value));
        Assert.Equal("PAGE", value);
    }

    /// <summary>
    /// 已知坑回归：页面字典只有 Assistant* 键（聊天页局部主题字典）时，
    /// 查找 App 主题字典里的键【不能】被带偏退化（旧写法"先选中一个字典再查键"会失败）。
    /// </summary>
    [Fact]
    public void A4_PageDictionaryWithoutKey_DoesNotShadowAppThemeDictionaries()
    {
        var page = new FakeDictionary()
            .WithTheme("Light", new FakeDictionary().With("AssistantCanvasBrush", "#FCFBF9"))
            .WithTheme("Dark", new FakeDictionary().With("AssistantCanvasBrush", "#1A1A1C"));

        var app = new FakeDictionary()
            .WithTheme("Light", new FakeDictionary()
                .With("SystemFillColorSuccessBrush", "APP_LIGHT_SUCCESS")
                .With("CardStrokeColorDefaultBrush", "APP_LIGHT_CARD_STROKE"))
            .WithTheme("Dark", new FakeDictionary()
                .With("SystemFillColorSuccessBrush", "APP_DARK_SUCCESS")
                .With("CardStrokeColorDefaultBrush", "APP_DARK_CARD_STROKE"));

        Assert.True(ThemeResourceResolver.TryResolve([page, app], "Dark", "SystemFillColorSuccessBrush", out var dark));
        Assert.Equal("APP_DARK_SUCCESS", dark);

        Assert.True(ThemeResourceResolver.TryResolve([page, app], "Light", "CardStrokeColorDefaultBrush", out var light));
        Assert.Equal("APP_LIGHT_CARD_STROKE", light);

        // 页面字典自有的键仍然优先
        Assert.True(ThemeResourceResolver.TryResolve([page, app], "Dark", "AssistantCanvasBrush", out var pageValue));
        Assert.Equal("#1A1A1C", pageValue);
    }

    [Fact]
    public void A5_TryResolve_SearchesMergedDictionaries_AndThemeDictionariesInsideThem()
    {
        var merged = new FakeDictionary()
            .WithTheme("Light", new FakeDictionary().With("TextFillColorSecondaryBrush", "MERGED_LIGHT"))
            .With("PlainInMerged", "MERGED_PLAIN");
        var root = new FakeDictionary().WithMerged(merged);

        Assert.True(ThemeResourceResolver.TryResolve([root], "Light", "TextFillColorSecondaryBrush", out var themed));
        Assert.Equal("MERGED_LIGHT", themed);

        Assert.True(ThemeResourceResolver.TryResolve([root], "Light", "PlainInMerged", out var plain));
        Assert.Equal("MERGED_PLAIN", plain);
    }

    [Fact]
    public void A6_TryResolve_ThemeEntryWinsOverPlainEntry_InSameScope()
    {
        var scope = new FakeDictionary()
            .With("MyBrush", "PLAIN")
            .WithTheme("Dark", new FakeDictionary().With("MyBrush", "THEMED"));

        Assert.True(ThemeResourceResolver.TryResolve([scope], "Dark", "MyBrush", out var value));
        Assert.Equal("THEMED", value);

        // 主题字典里没有该键时仍能命中普通项（两轮查找各自的 visited 不能互相吞掉）
        Assert.False(ThemeResourceResolver.TryResolve([scope], "Dark", "NotThere", out _));
        Assert.True(ThemeResourceResolver.TryResolve([scope], "Light", "MyBrush", out var plain));
        Assert.Equal("PLAIN", plain);
    }

    [Fact]
    public void A7_TryResolve_MissingKey_ReturnsFalse()
    {
        var scope = new FakeDictionary().With("A", "1");
        Assert.False(ThemeResourceResolver.TryResolve([scope], "Light", "NotThere", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void A8_ResolveBrush_ReturnsNull_WithoutBrushValue()
    {
        var scope = new FakeDictionary().WithTheme("Light", new FakeDictionary().With("NotABrush", "just-a-string"));
        Assert.Null(ThemeResourceResolver.ResolveBrush([scope], "Light", "NotABrush"));
        Assert.Null(ThemeResourceResolver.ResolveBrush([scope], "Light", "Missing"));
    }

    // ---------- 离线演示历史：真实 Render 路径（XAML 宿主断言） ----------

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

    [Fact]
    public void B1_DemoHistory_Render_PaintsThemeBrushes_InBothThemes()
    {
        // 【本轮·主审方案】断言体已搬迁到独立宿主进程（TubaWinUi3.XamlHostRunner）执行；
        // 本测试只启动子进程并校验结构化逐用例结果——宿主崩溃/超时/缺结果均判失败（不 Skip）。
        var r = XamlHostProcess.RunCase("B1_DemoHistory_Themes");
        Assert.True(r.Passed, r.Details);
    }

    [Fact]
    public void B2_Refresh_KeepsStreamingContent_ScrollState_AndExpandState()
    {
        // 【本轮·主审方案】断言体已搬迁到独立宿主进程（TubaWinUi3.XamlHostRunner）执行；
        // 本测试只启动子进程并校验结构化逐用例结果——宿主崩溃/超时/缺结果均判失败（不 Skip）。
        var r = XamlHostProcess.RunCase("B2_Refresh_KeepsStreaming");
        Assert.True(r.Passed, r.Details);
    }

    [Fact]
    public void B3_Render_AppliesAccentButtonBrush_InsideActionCard()
    {
        // 【本轮·主审方案】断言体已搬迁到独立宿主进程（TubaWinUi3.XamlHostRunner）执行；
        // 本测试只启动子进程并校验结构化逐用例结果——宿主崩溃/超时/缺结果均判失败（不 Skip）。
        var r = XamlHostProcess.RunCase("B3_AccentButton_TwoThemes");
        Assert.True(r.Passed, r.Details);
    }

    // ---------- 步骤链：状态色按实际主题解析 ----------

    [Theory]
    [InlineData(AgentStepStatus.Success, "SystemFillColorSuccessBrush")]
    [InlineData(AgentStepStatus.Failed, "SystemFillColorCriticalBrush")]
    [InlineData(AgentStepStatus.Rejected, "TextFillColorSecondaryBrush")]
    [InlineData(AgentStepStatus.AwaitingConfirmation, "AccentTextFillColorPrimaryBrush")]
    [InlineData(AgentStepStatus.Running, "AccentTextFillColorPrimaryBrush")]
    [InlineData(AgentStepStatus.Cancelled, "AccentTextFillColorPrimaryBrush")]
    public void C1_StatusBrushKey_MapsEveryStatus(AgentStepStatus status, string expected)
        => Assert.Equal(expected, StepRowVm.StatusBrushKeyFor(status));

    [Fact]
    public void C2_StatusBrush_ResolvesLightAndDark_AndRefreshPicksNewTheme()
    {
        // 【本轮·主审方案】断言体已搬迁到独立宿主进程（TubaWinUi3.XamlHostRunner）执行；
        // 本测试只启动子进程并校验结构化逐用例结果——宿主崩溃/超时/缺结果均判失败（不 Skip）。
        var r = XamlHostProcess.RunCase("C2_StatusBrush_TwoThemes");
        Assert.True(r.Passed, r.Details);
    }

    // ---------- 退订语义 ----------

    [Fact]
    public void D1_Scope_Unsubscribes_WhenContainerUnloaded()
    {
        // 【本轮·主审方案】断言体已搬迁到独立宿主进程（TubaWinUi3.XamlHostRunner）执行；
        // 本测试只启动子进程并校验结构化逐用例结果——宿主崩溃/超时/缺结果均判失败（不 Skip）。
        var r = XamlHostProcess.RunCase("D1_Scope_Unsubscribes");
        Assert.True(r.Passed, r.Details);
    }

    // ---------- 断言/构造辅助 ----------

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
        AssertThemeColor(linkRun!.Foreground, themeKey, "AccentTextFillColorPrimaryBrush");

        // 行内代码
        var inlineCode = nodes.OfType<Run>().FirstOrDefault(r => r.Text == "dotnet test");
        Assert.NotNull(inlineCode);
        AssertThemeColor(inlineCode!.Foreground, themeKey, "AccentTextFillColorPrimaryBrush");

        // 围栏代码块
        var codeBlock = nodes.OfType<Run>().FirstOrDefault(r => r.Text.Contains("winget install"));
        Assert.NotNull(codeBlock);
        AssertThemeColor(codeBlock!.Foreground, themeKey, "TextFillColorSecondaryBrush");

        // 列表符号
        var bullet = nodes.OfType<Run>().FirstOrDefault(r => r.Text.Contains('\u2022'));
        Assert.NotNull(bullet);
        AssertThemeColor(bullet!.Foreground, themeKey, "TextFillColorSecondaryBrush");

        // 表格外框 / 表头底色 / 单元格描边 / 交替行底色
        var tableGrid = nodes.OfType<Grid>().FirstOrDefault(g => g.RowDefinitions.Count >= 3);
        Assert.NotNull(tableGrid);
        AssertThemeColor(tableGrid!.BorderBrush, themeKey, "CardStrokeColorDefaultBrush");

        var headerCell = tableGrid.Children.OfType<Border>().FirstOrDefault(b => Grid.GetRow(b) == 0);
        Assert.NotNull(headerCell);
        AssertThemeColor(headerCell!.Background, themeKey, "SubtleFillColorSecondaryBrush");
        AssertThemeColor(headerCell.BorderBrush, themeKey, "ControlStrokeColorDefaultBrush");

        var oddCell = tableGrid.Children.OfType<Border>().FirstOrDefault(b => Grid.GetRow(b) == 1);
        Assert.NotNull(oddCell);
        AssertThemeColor(oddCell!.Background, themeKey, "ControlFillColorTransparentBrush");

        // 推荐卡 / 网站卡底色
        var card = nodes.OfType<Border>().FirstOrDefault(b => b.Child is Grid g && g.ColumnDefinitions.Count == 3);
        Assert.NotNull(card);
        AssertThemeColor(card!.Background, themeKey, "CardBackgroundFillColorDefaultBrush");
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
    private sealed class ThemeTestEnvironment : IDisposable
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
}

/// <summary>
/// 【本轮·主审方案】XAML 验收用例的宿主进程驱动：
/// 只负责启动 TubaWinUi3.XamlHostRunner 子进程（标准 STA Main + Application.Start 的真实 XAML 核心），
/// 解析其结构化逐用例输出（CASE|name|PASS / CASE|name|FAIL|reason / SUMMARY / HOST_DONE），
/// 并按结果断言。宿主进程崩溃/超时/输出缺失 = 本测试失败——不 Skip、不吞、不硬写成功。
/// </summary>
internal static class XamlHostProcess
{
    public static (bool Passed, string Details) RunCase(string name, int timeoutSeconds = 150)
    {
        var exe = FindHostExe();
        if (exe is null)
            return (false, "未找到 TubaWinUi3.XamlHostRunner.exe（先构建 TubaWinUi3.XamlHostRunner 项目）");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--case " + name,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, $"宿主超时（{timeoutSeconds}s）");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var code = proc.ExitCode;

            // 【主审补正 B】统一走纯解析函数：必须 CASE=PASS + HOST_DONE + SUMMARY 一致 + exit=0
            // 才判通过；PASS 后非零退出 / 缺完成标记 / SUMMARY 不一致一律判失败（不吞、不硬写成功）。
            var r = XamlHostOutput.Parse(stdout, code, name);
            if (r.Ok) return (true, "");
            return (false, $"{r.Details} stdout={Trim(stdout)} stderr={Trim(stderr)}");
        }
        catch (Exception ex)
        {
            return (false, $"启动宿主失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length > 800 ? s[..800] + "…" : s;

    private static string? FindHostExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "TubaWinUi3.XamlHostRunner", "bin");
            if (Directory.Exists(bin))
            {
                var exe = Directory.GetFiles(bin, "TubaWinUi3.XamlHostRunner.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (exe is not null) return exe;
            }
        }
        return null;
    }
}
