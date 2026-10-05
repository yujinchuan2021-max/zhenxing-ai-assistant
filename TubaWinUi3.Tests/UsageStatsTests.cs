using TubaWinUi3.Services;
using Xunit;

namespace TubaWinUi3.Tests;

/// <summary>ZXAI 2026-09-20：使用统计（卡片点击计数 / 热度排序 / AI 卡收藏）。</summary>
public class UsageStatsTests
{
    [Fact]
    public void RecordClick_IncrementsCount()
    {
        var key = "unit:test-" + Guid.NewGuid();
        Assert.Equal(0, UsageStats.GetClicks(key));
        UsageStats.RecordClick(key);
        UsageStats.RecordClick(key);
        Assert.Equal(2, UsageStats.GetClicks(key));
    }

    [Fact]
    public void RecordClick_IgnoresBlankKey()
    {
        UsageStats.RecordClick("");
        UsageStats.RecordClick("   ");
        // 不抛异常即可（空 key 不计）
    }

    [Fact]
    public void OrderByUsage_SortsByClicksDescending()
    {
        var a = "unit:o-" + Guid.NewGuid();   // 0 次
        var b = "unit:o-" + Guid.NewGuid();   // 1 次
        var c = "unit:o-" + Guid.NewGuid();   // 3 次
        UsageStats.RecordClick(b);
        UsageStats.RecordClick(c);
        UsageStats.RecordClick(c);
        UsageStats.RecordClick(c);

        var sorted = UsageStats.OrderByUsage(new[] { a, b, c }, x => x);
        Assert.Equal(new[] { c, b, a }, sorted);
    }

    [Fact]
    public void OrderByUsage_ZeroClicks_KeepsOriginalOrder()
    {
        var x1 = "unit:z-" + Guid.NewGuid();
        var x2 = "unit:z-" + Guid.NewGuid();
        var x3 = "unit:z-" + Guid.NewGuid();
        var sorted = UsageStats.OrderByUsage(new[] { x1, x2, x3 }, x => x);
        Assert.Equal(new[] { x1, x2, x3 }, sorted);
    }

    [Fact]
    public void AiFavorite_Toggles()
    {
        var name = "unit-fav-" + Guid.NewGuid();
        Assert.False(UsageStats.IsAiFavorite(name));

        UsageStats.ToggleAiFavorite(name);
        Assert.True(UsageStats.IsAiFavorite(name));
        Assert.Contains(name, UsageStats.GetAiFavorites());

        UsageStats.ToggleAiFavorite(name);
        Assert.False(UsageStats.IsAiFavorite(name));
    }
}
