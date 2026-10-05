using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A13 回归】旧版配置文件的兼容迁移（engine.json / ai_providers.json / project-profile.json）。
/// 隔离原则：全部在临时目录进行，不触碰真实 %LOCALAPPDATA%\TubaWinUi3，不读真实 Key。
/// 语义：仅目标缺失且源存在时才复制；已有目标绝不覆盖；源文件保留。
/// </summary>
public class ConfigMigrationTests : IDisposable
{
    private readonly string _dir;

    public ConfigMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zxai-migrate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void TargetMissing_SourceExists_CopiesAndKeepsSource()
    {
        var src = Path.Combine(_dir, "legacy", "engine.json");
        var dst = Path.Combine(_dir, "newloc", "engine.json");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "{\"engine\":\"builtin\"}");

        var copied = ConfigManager.MigrateFileIfMissing(src, dst);

        Assert.True(copied);
        Assert.True(File.Exists(dst));
        Assert.Equal("{\"engine\":\"builtin\"}", File.ReadAllText(dst));
        Assert.True(File.Exists(src), "源文件必须保留");
    }

    [Fact]
    public void TargetExists_NotOverwritten()
    {
        var src = Path.Combine(_dir, "legacy", "project-profile.json");
        var dst = Path.Combine(_dir, "newloc", "project-profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(src, "OLD");
        File.WriteAllText(dst, "NEW");   // 目标已有数据

        var copied = ConfigManager.MigrateFileIfMissing(src, dst);

        Assert.False(copied);
        Assert.Equal("NEW", File.ReadAllText(dst));   // 绝不覆盖
    }

    [Fact]
    public void SourceMissing_NoAction()
    {
        var src = Path.Combine(_dir, "legacy", "missing.json");
        var dst = Path.Combine(_dir, "newloc", "missing.json");
        Assert.False(ConfigManager.MigrateFileIfMissing(src, dst));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void SamePath_NoAction()
    {
        var p = Path.Combine(_dir, "same.json");
        File.WriteAllText(p, "X");
        Assert.False(ConfigManager.MigrateFileIfMissing(p, p));
        Assert.Equal("X", File.ReadAllText(p));
    }
}
