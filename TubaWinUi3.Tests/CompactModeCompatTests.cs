using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>兼容收口（2026-09-25 上线前窄改）：本版只保留默认卡片布局。
/// 旧配置 CompactModeEnabled=true 也必须显示默认卡片布局；向导与设置搜索都不再提供「卡牌/简洁模式」选择。</summary>
public sealed class CompactModeCompatTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(), "zxai-compact-compat-tests", Guid.NewGuid().ToString("N"));

    public CompactModeCompatTests()
    {
        Directory.CreateDirectory(_testRoot);
        DataRoots.TestRootOverrideForTest = _testRoot;
        AppSettings.InvalidateCache();
    }

    public void Dispose()
    {
        DataRoots.TestRootOverrideForTest = null;
        try { AppSettings.InvalidateCache(); } catch { }
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true); } catch { }
    }

    [Fact]
    public void StoredCompactModeTrueStillYieldsDefaultCardLayout()
    {
        // 旧预览版配置遗留 true（该设置已无 UI 入口，应用不再写入）
        AppSettings.Set("CompactModeEnabled", true);
        AppSettings.Flush();
        AppSettings.InvalidateCache();
        // 前置断言：旧值确实在配置里（否则下面的 false 可能只是"没写进去"的假绿）
        Assert.Equal("true", AppSettings.Get("CompactModeEnabled"));

        Assert.False(CompactModeService.IsCompactModeEnabled());

        // 兼容入口即使被误调用，也不会把布局切回简洁模式
        CompactModeService.SetCompactModeEnabled(true);
        Assert.False(CompactModeService.IsCompactModeEnabled());
    }

    [Fact]
    public void WizardAndSettingsSearchNoLongerOfferLayoutChoice()
    {
        var winUi3 = Path.Combine(FontSingleSourceTests.RepoRoot, "TubaWinUi3.WinUI3");

        var wizardXaml = File.ReadAllText(Path.Combine(winUi3, "Pages", "SetupWizardDialog.xaml"));
        Assert.DoesNotContain("CardModeOption", wizardXaml);
        Assert.DoesNotContain("CompactModeOption", wizardXaml);
        Assert.DoesNotContain("LayoutOption", wizardXaml);
        Assert.DoesNotContain("选择显示方式", wizardXaml);
        Assert.DoesNotContain("卡牌", wizardXaml);
        Assert.DoesNotContain("简洁模式", wizardXaml);
        Assert.Contains("NumberOfPages=\"3\"", wizardXaml);

        var wizardCs = File.ReadAllText(Path.Combine(winUi3, "Pages", "SetupWizardDialog.xaml.cs"));
        Assert.DoesNotContain("CompactMode", wizardCs);
        Assert.DoesNotContain("LayoutOption", wizardCs);

        var settingsCs = File.ReadAllText(Path.Combine(winUi3, "Pages", "SettingsPage.xaml.cs"));
        Assert.DoesNotContain("\"CompactMode\"", settingsCs);
    }
}
