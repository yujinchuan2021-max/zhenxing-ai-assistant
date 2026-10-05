using TubaWinUi3.Models;

namespace TubaWinUi3.Tests;

/// <summary>ZXAI 合规替换标注：卡片徽标文案逻辑（清退执行配套）。</summary>
public class ToolItemReplacementTests
{
    private static ToolItem Make(string? replacedFrom) => new()
    {
        Name = "CrystalDiskInfo",
        Category = "硬盘工具",
        Path = @"C:\x\DiskInfo64.exe",
        RelativePath = @"硬盘工具\CrystalDiskInfo\DiskInfo64.exe",
        Extension = "EXE",
        ReplacedFrom = replacedFrom,
    };

    [Fact]
    public void ReplacementText_WithSource_ShowsBadge()
    {
        Assert.Equal("✓ 合规替换：原 HD Tune", Make("HD Tune").ReplacementText);
    }

    [Fact]
    public void ReplacementText_Empty_NoBadge()
    {
        Assert.Equal("", Make(null).ReplacementText);
        Assert.Equal("", Make("").ReplacementText);
        Assert.Equal("", Make("   ").ReplacementText);
    }
}
