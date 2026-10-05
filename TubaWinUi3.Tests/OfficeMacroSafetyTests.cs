using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// A05 · Office 附件禁宏闸门 + XLM（Excel 4.0 宏）策略回归。
/// 全部用伪 COM 访问层（<see cref="IDynComApp"/>）驱动生产实现，【不启动 Office、不使用宏样本】。
///
/// 覆盖 Codex 复核要求的四场景：设置抛错 / 读取抛错 / 读回值不是 3 / 成功 ——
/// 前三者必须断言 Open 次数为 0（闸门失败时打开动作一次也不得执行），成功场景必须先 set(3)、再 get 读回、最后才 Open。
/// 另固定旧版 Excel 二进制（可驻留 XLM 宏）的拒绝行为与文案口径「安全模式ForceDisable」。
/// </summary>
public class OfficeMacroSafetyTests
{
    private const string Component = "Excel / WPS 表格";

    /// <summary>伪 COM Application：记录调用顺序，可注入 设置/读回 抛错与任意读回值。</summary>
    private sealed class FakeComApp : IDynComApp
    {
        private readonly object? _readBack;
        private readonly Exception? _setError;
        private readonly Exception? _getError;

        public FakeComApp(object? readBack, Exception? setError = null, Exception? getError = null)
        {
            _readBack = readBack;
            _setError = setError;
            _getError = getError;
        }

        /// <summary>调用序列：set:3 / get / open 都进同一序列，用于断言「先 set 再 get 再 Open」。</summary>
        public List<string> Calls { get; } = new();

        public void SetAutomationSecurity(int value)
        {
            Calls.Add($"set:{value}");
            if (_setError is not null) throw _setError;
        }

        public object? GetAutomationSecurity()
        {
            Calls.Add("get");
            if (_getError is not null) throw _getError;
            return _readBack;
        }
    }

    /// <summary>跑一次「闸门 + 打开」：openAction 计数即「是否执行过 Office 打开」。</summary>
    private static (string? Error, int Opens, List<string> Calls) Run(FakeComApp app)
    {
        var opens = 0;
        var error = OfficeMacroSafety.RunGated(app, Component, () =>
        {
            opens++;
            app.Calls.Add("open");
        });
        return (error, opens, app.Calls);
    }

    // ─────────────── 场景 1：设置抛错 ───────────────

    [Fact]
    public void SetThrows_ErrorReturned_OpenNeverRuns()
    {
        var app = new FakeComApp(readBack: OfficeMacroSafety.ForceDisable,
            setError: new System.Runtime.InteropServices.COMException("AutomationSecurity 属性被拒绝"));

        var (error, opens, calls) = Run(app);

        Assert.NotNull(error);
        Assert.Contains(Component, error!);
        Assert.Contains("未打开", error!);
        Assert.Equal(0, opens);
        Assert.DoesNotContain("open", calls);
        // 设置就失败了 → 直接中止，不再读回
        Assert.Equal(new[] { $"set:{OfficeMacroSafety.ForceDisable}" }, calls);
    }

    // ─────────────── 场景 2：读回抛错 ───────────────

    [Fact]
    public void ReadBackThrows_ErrorReturned_OpenNeverRuns()
    {
        var app = new FakeComApp(readBack: OfficeMacroSafety.ForceDisable,
            getError: new InvalidOperationException("AutomationSecurity 不可读"));

        var (error, opens, calls) = Run(app);

        Assert.NotNull(error);
        Assert.Contains(Component, error!);
        Assert.Contains("无法确认宏已禁用", error!);
        Assert.Equal(0, opens);
        Assert.DoesNotContain("open", calls);
        Assert.Equal(new[] { $"set:{OfficeMacroSafety.ForceDisable}", "get" }, calls);
    }

    // ─────────────── 场景 3：读回值不是 3 ───────────────

    [Theory]
    [InlineData(0)] // 组件忽略设置（属性实际仍是 0）
    [InlineData(1)] // msoAutomationSecurityLow —— Office 自动化默认值，允许所有宏
    [InlineData(2)] // msoAutomationSecurityByUI
    public void ReadBackNotForceDisable_ErrorReturned_OpenNeverRuns(int readBack)
    {
        var app = new FakeComApp(readBack);

        var (error, opens, calls) = Run(app);

        Assert.NotNull(error);
        Assert.Contains($"期望 {OfficeMacroSafety.ForceDisable}", error!);
        Assert.Contains("安全模式ForceDisable", error!);
        Assert.Equal(0, opens);
        Assert.DoesNotContain("open", calls);
    }

    [Fact]
    public void ReadBackNull_ErrorReturned_OpenNeverRuns()
    {
        // 读回空值 = 未确认（不是「已禁用」）：必须同样按失败处理
        var app = new FakeComApp(readBack: null);

        var (error, opens, calls) = Run(app);

        Assert.NotNull(error);
        Assert.Equal(0, opens);
        Assert.DoesNotContain("open", calls);
    }

    // ─────────────── 场景 4：成功（先 set / get 再 Open） ───────────────

    [Fact]
    public void ForceDisableAccepted_SetThenReadBackThenOpenExactlyOnce()
    {
        var app = new FakeComApp(readBack: OfficeMacroSafety.ForceDisable);

        var (error, opens, calls) = Run(app);

        Assert.Null(error);
        Assert.Equal(1, opens);
        Assert.Equal(new[] { $"set:{OfficeMacroSafety.ForceDisable}", "get", "open" }, calls);
    }

    [Theory]
    [InlineData((short)3)] // COM 常见的 short 返回
    [InlineData(3L)]
    [InlineData("3")]      // 字符串形式
    public void ForceDisableReadBack_ComReturnTypes_StillAccepted(object readBack)
    {
        var app = new FakeComApp(readBack);

        var (error, opens, _) = Run(app);

        Assert.Null(error);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void RunGated_OpenActionThrows_PropagatesUnchanged()
    {
        // 闸门通过后 openAction 的异常原样上抛（调用方的回退/清理逻辑不变），不得被误报成「闸门失败」
        var app = new FakeComApp(readBack: OfficeMacroSafety.ForceDisable);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OfficeMacroSafety.RunGated(app, Component, () => throw new InvalidOperationException("Open 失败")));

        Assert.Equal("Open 失败", ex.Message);
        Assert.Equal(new[] { $"set:{OfficeMacroSafety.ForceDisable}", "get" }, app.Calls);
    }

    [Fact]
    public void OfficeInteropEntry_AcceptsInjectedFake_SameContract()
    {
        // 生产入口（OfficeInteropService.TryDisableMacrosBeforeOpen）必须同样接受 IDynComApp 伪对象
        var bad = new FakeComApp(readBack: 1);
        Assert.NotNull(OfficeInteropService.TryDisableMacrosBeforeOpen(bad, Component));
        Assert.Equal(new[] { $"set:{OfficeMacroSafety.ForceDisable}", "get" }, bad.Calls);

        var good = new FakeComApp(readBack: OfficeMacroSafety.ForceDisable);
        Assert.Null(OfficeInteropService.TryDisableMacrosBeforeOpen(good, Component));
        Assert.Equal(new[] { $"set:{OfficeMacroSafety.ForceDisable}", "get" }, good.Calls);

        // Application 为空（未创建成功）→ 同样 fail-closed 返回错误，而不是抛异常
        Assert.NotNull(OfficeInteropService.TryDisableMacrosBeforeOpen(null, Component));
    }

    // ─────────────── XLM（Excel 4.0 宏）策略：旧版 Excel 二进制拒绝自动转换 ───────────────

    [Theory]
    [InlineData("book.xls")]
    [InlineData("BOOK.XLS")]
    [InlineData(@"C:\Users\Administrator\Downloads\结算表.xls")]
    [InlineData("addin.xla")]
    [InlineData("template.xlt")]
    public void LegacyExcelBinary_Refused_WithPreciseWording(string path)
    {
        Assert.True(OfficeMacroSafety.IsXlmCapableLegacyFormat(path));

        var refusal = OfficeMacroSafety.TryRefuseXlmUnsafeAutoConversion(path);

        Assert.NotNull(refusal);
        Assert.Contains("安全模式ForceDisable", refusal!); // 文案口径：精确为「安全模式ForceDisable」
        Assert.Contains("XLM", refusal!);
        Assert.Contains("无法保证安全", refusal!);
        Assert.Contains("未打开该文件", refusal!);
        Assert.Contains(".xlsx", refusal!);                // 给出不执行活动内容的替代路径
    }

    [Theory]
    [InlineData("a.xlsx")] // OOXML：内置 zip 解析，不走 COM
    [InlineData("a.xlsm")] // 宏启用 OOXML：同样走 zip 解析（不执行宏），故不在拒绝表内
    [InlineData("a.doc")]
    [InlineData("a.docx")]
    [InlineData("a.ppt")]
    [InlineData("a.et")]
    [InlineData("a.txt")]
    [InlineData("noext")]
    [InlineData("")]
    [InlineData(null)]
    public void NonLegacyExcelFormats_NotRefused(string? path)
    {
        Assert.False(OfficeMacroSafety.IsXlmCapableLegacyFormat(path));
        Assert.Null(OfficeMacroSafety.TryRefuseXlmUnsafeAutoConversion(path));
    }

    [Fact]
    public async Task ConvertAsync_LegacyXlsSource_RefusedBeforeAnyComWork()
    {
        // 拒绝必须在创建 Office/WPS COM 实例【之前】生效：正确实现下本用例不会启动 Office、不会打开文件。
        var src = Path.Combine(Path.GetTempPath(), $"zxai-a05-policy-{Guid.NewGuid():N}.xls");
        var outPath = src + ".xlsx";

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => OfficeInteropService.ConvertAsync(src, ".xlsx", outPath));

        Assert.Contains("安全模式ForceDisable", ex.Message);
        Assert.Contains("XLM", ex.Message);
        Assert.False(File.Exists(outPath));
    }
}
