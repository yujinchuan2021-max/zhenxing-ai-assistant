using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

public sealed class ToolflowUploadSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "zxai-toolflow-setting-" + Guid.NewGuid().ToString("N"));

    public ToolflowUploadSettingTests()
    {
        Directory.CreateDirectory(_root);
        DataRoots.TestRootOverrideForTest = _root;
        AppSettings.InvalidateCache();
        Assert.Equal(_root, ConfigManager.GetDataDir());
    }

    [Fact]
    public void UploadIsEnabledByDefault_AndOptOutSurvivesReload()
    {
        Assert.Null(AppSettings.Get(AppSettings.ToolflowUploadEnabledKey));
        Assert.True(AppSettings.IsToolflowUploadEnabled);

        AppSettings.Set(AppSettings.ToolflowUploadEnabledKey, false);
        AppSettings.Flush();
        AppSettings.InvalidateCache();

        Assert.False(AppSettings.IsToolflowUploadEnabled);
        Assert.Equal("false", AppSettings.Get(AppSettings.ToolflowUploadEnabledKey));
    }

    public void Dispose()
    {
        AppSettings.InvalidateCache();
        DataRoots.TestRootOverrideForTest = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
