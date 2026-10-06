// 【本轮·主审方案】XAML 验收测试的专用进程宿主（自包含标准激活）。
//
// 对照主项目 obj/…/App.g.i.cs 的真实生成入口（官方姿势）：
//   1) [STAThread] Main 上直接 InitializeComWrappers + Application.Start；
//   2) Start 回调里取【局部 DispatcherQueue】+ 建立 DispatcherQueueSynchronizationContext
//      + 实例化 Application 子类（本文件 TestApp）；
//   3) 用例经该局部 queue 入队执行——不依赖 OnLaunched 是否已发生（v1 的具体缺陷：Queue 只在
//      OnLaunched 赋值却在 new TestApp() 后立即使用）；
//   4) 用例跑完 Application.Exit() 退出消息泵 → Start 返回 → Main 返回 exit code。
//
// 自包含说明：csproj 为 WindowsAppSDKSelfContained=true + WindowsPackageType=None，与主程序一致——
// 【不做手动 Bootstrap】（避免"手动绑定系统 WindowsApps 版本"与"使用应用目录自包含运行时"冲突；
// 自包含部署的 WinAppSDK DLL 直接在应用目录）。
//
// 输出协议（stdout 逐行，xunit 侧解析）：
//   HOST_READY
//   CASE|<name>|PASS            / CASE|<name>|FAIL|<单行原因>
//   SUMMARY|<passed>|<total>
//   HOST_DONE
// 退出码：0=全部通过；1=有用例失败；31/32/33=宿主自身失败（HOST_FAIL|<reason>）。

using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>仅作为"XAML 运行时附着对象"存在；用例执行不依赖本类回调时机。</summary>
internal sealed class TestApp : Application, IXamlMetadataProvider
{
    private bool _sharedControlResourcesReady;
    // The isolated host needs the same type metadata for WinUI control templates,
    // without constructing the product App or loading its pages/settings.
    private readonly IXamlMetadataProvider _metadata = new TubaWinUi3.TubaWinUi3_XamlTypeInfo.XamlMetaDataProvider();
    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();

    public TestApp()
    {
        // Keep the application/system side deliberately different from a Light
        // fixture. Product theme checks must follow the attached fixture root.
        RequestedTheme = string.Equals(Environment.GetEnvironmentVariable("ZXAI_NATIVE_APP_THEME"),
            "Light", StringComparison.OrdinalIgnoreCase) ? ApplicationTheme.Light : ApplicationTheme.Dark;
        UnhandledException += (_, args) =>
        {
            // Diagnostic only: preserve the failure and native exit behavior.
            // Do not set Handled or continue after a XAML activation failure.
            var detail = args.Exception?.ToString() ?? args.Message;
            Program.Diag("xaml-unhandled " + detail.Replace("\r", " ").Replace("\n", " "));
            Console.Error.WriteLine("HOST_XAML_UNHANDLED|" + detail);
            Console.Error.Flush();
        };
    }

    internal void EnsureSharedControlResources()
    {
        if (_sharedControlResourcesReady) return;
        // The host keeps one real Window alive across cases. Keep its base native
        // templates and font resources alive too: deferred template callbacks can
        // still run after a case removes its page from that Window.
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            ["AppFontFamily"] = TubaWinUi3.Services.AppFonts.WinUI,
        });
        _sharedControlResourcesReady = true;
    }
}

internal static class Program
{
    private static int _exitCode;
    private static readonly string DiagPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zxai_xamlhost_diag.log");

    internal static void Diag(string msg)
    {
        try { System.IO.File.AppendAllText(DiagPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); } catch { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try { System.IO.File.Delete(DiagPath); } catch { }
        Diag("main-enter");
        var only = ParseCaseFilter(args);
        Diag("args-parsed");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Diag("comwrappers-ok");
            Application.Start(p =>
            {
                Diag("start-callback-enter");
                // 官方姿势：Start 回调即 UI 线程就绪点（p 为框架回调参数）。
                var queue = DispatcherQueue.GetForCurrentThread();
                if (queue is null)
                {
                    Console.WriteLine("HOST_FAIL|no DispatcherQueue in Start callback");
                    _exitCode = 31;
                    Application.Current?.Exit();
                    return;
                }

                var context = new DispatcherQueueSynchronizationContext(queue);
                SynchronizationContext.SetSynchronizationContext(context);
                Diag("sync-context-ok");
                _ = new TestApp();   // 实例化 Application 子类（官方激活关键步骤）
                Diag("testapp-created");

                // 【局部 queue】执行用例：不依赖 OnLaunched 时机（v1 缺陷修复点）。
                queue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
                {
                    try
                    {
                        // Application metadata is available after its constructor returns.
                        ((TestApp)Application.Current).EnsureSharedControlResources();
                        _exitCode = await RunAllCasesAsync(only);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("HOST_FAIL|" + OneLine(ex));
                        _exitCode = 32;
                    }
                    finally
                    {
                        Diag("cases-done exit=" + _exitCode);
                        Console.Out.Flush();
                        ChatScrollProbeCases.CloseHostWindow();
                        Application.Current?.Exit();   // 退出消息泵 → Start 返回
                    }
                });
            });
        }
        catch (Exception ex)
        {
            Diag("main-catch " + OneLine(ex));
            Console.WriteLine("HOST_FAIL|" + OneLine(ex));
            return 33;
        }
        Diag("main-return " + _exitCode);
        return _exitCode;
    }


    private static HashSet<string>? ParseCaseFilter(string[] args)
    {
        var names = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--case" && i + 1 < args.Length) names.Add(args[++i]);
            else if (args[i].StartsWith("--cases=", StringComparison.Ordinal))
                names.AddRange(args[i]["--cases=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
        return names.Count > 0 ? new HashSet<string>(names, StringComparer.Ordinal) : null;
    }

    private static async Task<int> RunAllCasesAsync(HashSet<string>? only)
    {
        Console.WriteLine("HOST_READY");

        var cases = new List<(string Name, Func<Task> Body)>
        {
            // ── 最小宿主用例（先证明宿主真实可用：创建控件、正常退出）──
            ("CreateControl", () =>
            {
                var root = new Grid();
                var text = new TextBlock { Text = "xaml-ok" };
                root.Children.Add(text);
                if (root.Children.Count != 1) throw new InvalidOperationException("Children 计数异常");
                return Task.CompletedTask;
            }),
            ("ThemeDict", () =>
            {
                var dict = new ResourceDictionary();
                var theme = new ResourceDictionary();
                theme["ProbeBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
                dict.ThemeDictionaries["Light"] = theme;
                if (!dict.ThemeDictionaries.ContainsKey("Light"))
                    throw new InvalidOperationException("ThemeDictionaries 读取失败");
                return Task.CompletedTask;
            }),
        };
        // 【本轮】主题解析验收用例（自测试项目搬迁；在真实 XAML 核心上执行）。
        // 主题解析验收用例（自测试项目搬迁；在真实 XAML 核心上执行）。
        // Detached Measure/Arrange probes must run before any case creates the
        // shared Window and activates the native template/layout lifecycle.
        cases.AddRange(ChatLayoutCases.All());
        cases.AddRange(ToolAccessDeliveryCases.Detached());
        cases.AddRange(XamlThemeCases.All());
        cases.AddRange(CardsCases.All());
        cases.AddRange(TaskCases.All());
        cases.AddRange(ToolAccessDeliveryCases.Attached());
        cases.AddRange(AppCenterCases.All());
        cases.AddRange(CommunityAppearanceCases.All());
        cases.AddRange(CommunityManagedSourceCases.All());
        cases.AddRange(CommunityAuthenticationCases.All());
        cases.AddRange(GoalGuideCases.All());
        cases.AddRange(SkillEditorCases.All());
        cases.AddRange(AiNewsCases.All());
        cases.AddRange(ChoiceCases.All());
        cases.AddRange(WorkbenchCases.All());
        cases.AddRange(ReadabilityCases.All());
        cases.AddRange(ConversationMenuThemeCases.All());
        cases.AddRange(DownloadFlyoutThemeCases.All());
        cases.AddRange(SkillsFlyoutThemeCases.All());
        cases.AddRange(SettingsThemeCases.All());
        cases.AddRange(HomeAppearanceThemeCases.All());
        cases.AddRange(SkillLibraryCases.All());
        cases.AddRange(MasonryLayoutCases.All());
        cases.AddRange(ConversationFollowCases.All());
        cases.AddRange(LocalToolReadinessCases.All());
        cases.AddRange(HardwareEditorCases.All());
        cases.AddRange(ChatScrollProbeCases.All());

        var passed = 0;
        var total = 0;
        foreach (var (name, body) in cases)
        {
            if (only is not null && !only.Contains(name)) continue;
            total++;
            Diag("case-start " + name);
            try
            {
                await body();
                Console.WriteLine($"CASE|{name}|PASS");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CASE|{name}|FAIL|{OneLine(ex)}");
                Console.Error.WriteLine($"CASE_DETAIL|{name}|{ex}");
            }
            Diag("case-end " + name);
        }

        Console.WriteLine($"SUMMARY|{passed}|{total}");
        Console.WriteLine("HOST_DONE");
        return passed == total && total > 0 ? 0 : 1;
    }

    private static string OneLine(Exception ex)
        => (ex.GetType().Name + ": " + ex.Message).Replace("\r", " ").Replace("\n", " ");
}
