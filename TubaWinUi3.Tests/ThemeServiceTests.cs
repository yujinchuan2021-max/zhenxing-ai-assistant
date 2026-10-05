using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【UI 改版·第一批】主题值解析/序列化的纯函数测试。
/// 只验证解析与序列化逻辑，不写盘、不读取真实 settings.json。
/// </summary>
public class ThemeServiceTests
{
    [Theory]
    [InlineData(null, AppTheme.Default)]
    [InlineData("", AppTheme.Default)]
    [InlineData("system", AppTheme.Default)]
    [InlineData(" Light ", AppTheme.Light)]
    [InlineData("DARK", AppTheme.Dark)]
    [InlineData("purple", AppTheme.Default)]   // 非法旧值一律回退"跟随系统"
    [InlineData("1", AppTheme.Default)]        // 数字等旧形态 → 回退
    public void ParseTheme_FallsBackToSystem_ForUnknownValues(string? raw, AppTheme expected)
        => Assert.Equal(expected, ThemeService.ParseTheme(raw));

    [Theory]
    [InlineData(AppTheme.Default, "system")]
    [InlineData(AppTheme.Light, "light")]
    [InlineData(AppTheme.Dark, "dark")]
    public void ThemeToString_RoundTrips(AppTheme theme, string expected)
    {
        Assert.Equal(expected, ThemeService.ThemeToString(theme));
        Assert.Equal(theme, ThemeService.ParseTheme(expected));
    }
}
