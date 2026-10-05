using System.Reflection;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>只调用设置的静态取值方法；配置和 Downloads 均在一次性假根，不创建 GUI 或下载任务。</summary>
[Collection("GlobalConfigTests")]
public sealed class HttpDownloadSettingsTests : IDisposable
{
    private static readonly string TestParent = Path.Combine(Path.GetTempPath(), "zxai-http-download-settings-tests");
    private readonly string _root = Path.Combine(TestParent, Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot;

    public HttpDownloadSettingsTests()
    {
        _previousRoot = DataRoots.TestRootOverrideForTest;
        Directory.CreateDirectory(_root);
        DataRoots.TestRootOverrideForTest = _root;
        AppSettings.InvalidateCache();
        Assert.Equal(_root, ConfigManager.GetDataDir());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void MissingOrBlankPath_UsesIsolatedDownloads(string? savedPath)
    {
        SaveAndReload("HttpDownloadPath", savedPath);

        Assert.Equal(Path.Combine(_root, "Downloads"), ReadSetting("GetHttpDownloadPath"));
        Assert.False(Directory.Exists(Path.Combine(_root, "Downloads")), "读取默认路径不应创建目录或启动下载");
    }

    [Fact]
    public void SavedUserDirectory_IsPreservedAfterReload()
    {
        var selectedDirectory = Path.Combine(_root, "用户选择的下载目录 {项目}");
        SaveAndReload("HttpDownloadPath", selectedDirectory);

        Assert.Equal(selectedDirectory, AppSettings.Get("HttpDownloadPath"));
        Assert.Equal(selectedDirectory, ReadSetting("GetHttpDownloadPath"));
        Assert.False(Directory.Exists(selectedDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unsupported-action")]
    public void MissingOrInvalidAction_DefaultsToInstall(string? savedAction)
    {
        SaveAndReload("HttpDownloadAction", savedAction);

        Assert.Equal("install", ReadSetting("GetHttpDownloadAction"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("extract")]
    [InlineData("install")]
    public void SupportedAction_SurvivesSavedSettingsReload(string action)
    {
        SaveAndReload("HttpDownloadAction", action);

        Assert.Equal(action, AppSettings.Get("HttpDownloadAction"));
        Assert.Equal(action, ReadSetting("GetHttpDownloadAction"));
    }

    private static string ReadSetting(string methodName)
    {
        var method = typeof(SettingsPage).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, null));
    }

    private static void SaveAndReload(string key, string? value)
    {
        if (value is not null) AppSettings.Set(key, value);
        AppSettings.Flush();
        AppSettings.InvalidateCache();
    }

    public void Dispose()
    {
        try { AppSettings.Flush(); }
        finally
        {
            AppSettings.InvalidateCache();
            DataRoots.TestRootOverrideForTest = _previousRoot;
        }

        var fullRoot = Path.GetFullPath(_root);
        var allowedParent = Path.GetFullPath(TestParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (fullRoot.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }
}
