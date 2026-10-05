using Microsoft.Web.WebView2.Core;

namespace TubaWinUi3.Services;

/// <summary>
/// 共享的 WebView2 环境：用户数据目录固定在 %LocalAppData%\TubaWinUi3\WebView2，
/// 避免应用安装在不可写目录（如 Program Files、MSIX 的 WindowsApps）时，
/// WebView2 无法在 exe 旁创建默认的 *.exe.WebView2 数据目录而报错。
/// 目录由此处显式指定，不经 WEBVIEW2_USER_DATA_FOLDER 环境变量——后者会连带覆盖
/// 第三方组件（FieldCure ChatPanel 等）自建环境的目录，详见 App() 中的说明。
/// </summary>
public static class WebView2EnvironmentService
{
    /// <summary>共享的 WebView2 用户数据目录（缓存所在位置）。【GUI 隔离】测试模式走隔离根。</summary>
    public static string UserDataFolder { get; } = ResolveUserDataFolder(DataRoots.TestRoot);

    /// <summary>
    /// 用户数据目录解析（纯函数，便于单测隔离规则，行为与原先一致）：
    /// 隔离模式（设置了 ZXAI_DATA_ROOT）→ &lt;隔离根&gt;\WebView2，绝不写真实用户数据；
    /// 生产模式 → %LocalAppData%\TubaWinUi3\WebView2。
    /// 注意：生产目录不等于 exe 旁的默认 <c>*.exe.WebView2</c>——AI 助手页的组件库环境用的是
    /// 后者，所以两者不会争用同一个目录（见 App() 中关于 WEBVIEW2_USER_DATA_FOLDER 的说明）。
    /// </summary>
    internal static string ResolveUserDataFolder(string? testRoot)
        => testRoot is { } tr
            ? Path.Combine(tr, "WebView2")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TubaWinUi3", "WebView2");

    private static readonly Lazy<Task<CoreWebView2Environment>> _environment = new(
        () => CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: null,
            userDataFolder: UserDataFolder,
            options: new CoreWebView2EnvironmentOptions()).AsTask());

    /// <summary>获取共享环境（所有 WebView2 实例共用同一用户数据目录与浏览器进程）。</summary>
    public static Task<CoreWebView2Environment> GetAsync() => _environment.Value;
}
