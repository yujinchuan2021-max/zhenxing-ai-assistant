using Xunit;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【主审补正 B】XamlHostOutput.Parse 的定向用例：
/// 验证"PASS 后非零退出"与"缺完成标记"不得被判定为通过（负例），
/// 以及完整协议（PASS + SUMMARY 一致 + HOST_DONE + exit=0）才判通过（正例）。
/// 纯字符串解析，不启动任何进程。
/// </summary>
public class XamlHostOutputParseTests
{
    private const string CaseName = "B1_DemoHistory_Themes";

    [Fact]
    public void FullProtocol_ExitZero_IsOk()
    {
        var stdout = "HOST_READY\nCASE|" + CaseName + "|PASS\nSUMMARY|1|1\nHOST_DONE\n";
        var r = XamlHostOutput.Parse(stdout, 0, CaseName);
        Assert.True(r.Ok, r.Details);
    }

    // 【负例①】先打印 PASS 再以非零码退出 / 崩溃——不得判成功
    [Fact]
    public void PassThenNonZeroExit_IsFailure()
    {
        var stdout = "CASE|" + CaseName + "|PASS\nSUMMARY|1|1\nHOST_DONE\n";
        var r = XamlHostOutput.Parse(stdout, 1, CaseName);
        Assert.False(r.Ok);
        Assert.Contains("非零码退出", r.Details);
    }

    [Fact]
    public void PassThenCrashExitCode_IsFailure()
    {
        // 模拟 stowed exception 崩溃退出码 0xC000027B（有符号 int 为负）
        var stdout = "CASE|" + CaseName + "|PASS\n";
        var r = XamlHostOutput.Parse(stdout, unchecked((int)0xC000027B), CaseName);
        Assert.False(r.Ok);
    }

    // 【负例②】有 PASS 但缺少 HOST_DONE 完成标记——不得判成功
    [Fact]
    public void PassWithoutHostDone_IsFailure()
    {
        var stdout = "CASE|" + CaseName + "|PASS\nSUMMARY|1|1\n";
        var r = XamlHostOutput.Parse(stdout, 0, CaseName);
        Assert.False(r.Ok);
        Assert.Contains("HOST_DONE", r.Details);
    }

    // PASS 但 SUMMARY 缺失 / 不一致——不得判成功
    [Fact]
    public void PassWithoutSummary_IsFailure()
    {
        var stdout = "CASE|" + CaseName + "|PASS\nHOST_DONE\n";
        var r = XamlHostOutput.Parse(stdout, 0, CaseName);
        Assert.False(r.Ok);
        Assert.Contains("SUMMARY", r.Details);
    }

    [Fact]
    public void PassWithInconsistentSummary_IsFailure()
    {
        var stdout = "CASE|" + CaseName + "|PASS\nSUMMARY|1|2\nHOST_DONE\n";
        var r = XamlHostOutput.Parse(stdout, 0, CaseName);
        Assert.False(r.Ok);
        Assert.Contains("SUMMARY", r.Details);
    }

    // FAIL 用例：带原因判失败
    [Fact]
    public void FailCase_IsFailure_WithReason()
    {
        var stdout = "CASE|" + CaseName + "|FAIL|InvalidOperationException: boom\nSUMMARY|0|1\nHOST_DONE\n";
        var r = XamlHostOutput.Parse(stdout, 1, CaseName);
        Assert.False(r.Ok);
        Assert.Contains("boom", r.Details);
    }

    // 宿主初始化失败（exit=33, HOST_FAIL）——报错信息应指向宿主初始化失败
    [Fact]
    public void HostInitFailure_IsFailure()
    {
        var stdout = "HOST_FAIL|InvalidOperationException: 初始化失败\n";
        var r = XamlHostOutput.Parse(stdout, 33, CaseName);
        Assert.False(r.Ok);
        Assert.Contains("初始化失败", r.Details);
    }

    // 无任何输出（进程静默崩溃）——判失败
    [Fact]
    public void EmptyOutput_IsFailure()
    {
        var r = XamlHostOutput.Parse("", unchecked((int)0xC000027B), CaseName);
        Assert.False(r.Ok);
    }

    // 多用例输出中定位目标用例行（含前导噪声行）
    [Fact]
    public void NoiseLines_StillParsesTargetCase()
    {
        var stdout = "HOST_READY\n[diag] case-start X\nCASE|Other|PASS\nCASE|" + CaseName + "|PASS\nSUMMARY|2|2\nHOST_DONE\n";
        var r = XamlHostOutput.Parse(stdout, 0, CaseName);
        Assert.True(r.Ok, r.Details);
    }
}
