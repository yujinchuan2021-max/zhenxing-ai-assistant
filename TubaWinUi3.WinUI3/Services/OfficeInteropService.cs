using System.Runtime.InteropServices;

namespace TubaWinUi3.Services;

/// <summary>
/// Office / WPS COM 互联：处理内置轻量引擎无法解析的旧版二进制格式
/// （.doc / .wps / .ppt / .dps / .et，以及需要 Office 参与的转换）。
/// 自动探测 Microsoft Office（Word/Excel/PowerPoint）与 WPS Office
/// （KWPS/KET/KWPP），在专用 STA 线程上执行，3 分钟超时。
/// </summary>
public static class OfficeInteropService
{
    private static readonly string[] WordProgIds = { "Word.Application", "KWPS.Application", "WPS.Application" };
    private static readonly string[] ExcelProgIds = { "Excel.Application", "KET.Application", "ET.Application" };
    private static readonly string[] PptProgIds = { "PowerPoint.Application", "KWPP.Application", "WPP.Application" };

    private const int TimeoutMinutes = 3;

    public static bool IsWordAvailable => ResolveProgId(WordProgIds) is not null;
    public static bool IsExcelAvailable => ResolveProgId(ExcelProgIds) is not null;
    public static bool IsPptAvailable => ResolveProgId(PptProgIds) is not null;

    /// <summary>该文件是否可由当前机器上的 Office/WPS 处理（按文件家族探测）。</summary>
    public static bool IsAvailableFor(string sourcePath)
        => FamilyOf(sourcePath) switch
        {
            "word" => IsWordAvailable,
            "excel" => IsExcelAvailable,
            "ppt" => IsPptAvailable,
            _ => false
        };

    /// <summary>
    /// 通过 Office/WPS 把 source 另存为 targetExt（.docx/.pdf/.html/.txt/.xlsx/.csv/.pptx）。
    /// 返回输出文件列表（单个输出路径由调用方传入）。
    /// </summary>
    public static Task<List<string>> ConvertAsync(string source, string targetExt,
        string outputPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // A05（XLM 策略）：旧版 Excel 二进制（.xls/.xla/.xlt）拒绝自动转换 —— 安全模式ForceDisable 不覆盖
        // Excel 4.0 宏，该路径无法保证安全，必须在创建 COM 实例之前拦下（拒绝文案见该方法）。
        var xlmRefusal = OfficeMacroSafety.TryRefuseXlmUnsafeAutoConversion(source);
        if (xlmRefusal is not null)
            throw new NotSupportedException(xlmRefusal);

        var family = FamilyOf(source)
            ?? throw new NotSupportedException(MiscTexts.TSub($"不支持的 Office 互联格式：{Path.GetExtension(source)}"));
        var format = FileFormatFor(family, targetExt)
            ?? throw new NotSupportedException(
                MiscTexts.TSub($"Office 互联不支持的转换目标：{Path.GetExtension(source)} → {targetExt}（可先转 PDF/DOCX 再继续）"));

        var progIds = family switch
        {
            "word" => WordProgIds,
            "excel" => ExcelProgIds,
            _ => PptProgIds
        };
        var appName = family switch { "word" => "Word", "excel" => "Excel", _ => "PowerPoint" };

        return Task.Run(() => RunSta(() =>
        {
            var type = ResolveProgId(progIds)
                ?? throw new InvalidOperationException(MiscTexts.TSub($"未安装可处理 {Path.GetExtension(source)} 的 Office / WPS 组件（{appName}）"));

            progress?.Report(MiscTexts.TSub($"正在通过 {appName} 转换 {Path.GetFileName(source)}…"));
            dynamic app;
            try
            {
                app = Activator.CreateInstance(type)!;
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    MiscTexts.TSub($"启动 {appName} 失败（{ex.Message}）。若反复失败，请尝试以普通权限运行本工具或直接用 Office 打开该文件另存。"));
            }

            try
            {
                TrySet(() => app.Visible = false);
                TrySet(() => app.DisplayAlerts = 0);

                // A05（审计整改）：打开文件【之前】强制禁宏。不得再用 TrySet 吞掉设置失败。
                // 闸门 + Open 由 RunGated 串成一次调用：闸门失败（设置抛错 / 读回抛错 / 读回值≠3）→ 抛错中止，
                // openAction 一次也不会执行（伪对象回归测试：TubaWinUi3.Tests/OfficeMacroSafetyTests.cs）。
                // Office 自动化默认 msoAutomationSecurityLow（允许宏），Visible=false / DisplayAlerts=false
                // 都不能代替宏控制（Microsoft：DisplayAlerts 不作用于安全警告）。
                string? macroGateError = OfficeMacroSafety.RunGated(
                    new DynamicComAppAdapter(app),
                    appName,
                    () => SaveSourceToFormat(app, family, source, outputPath, format));
                if (macroGateError is not null)
                    throw new InvalidOperationException(macroGateError);
            }
            finally
            {
                TrySet(() => app.Quit());
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException(MiscTexts.TSub($"{appName} 转换未产生输出文件"));
            return new List<string> { outputPath };
        }), ct);
    }

    /// <summary>A05：禁宏闸门确认通过后才执行 —— 按文件家族打开 source 并另存为 outputPath。
    /// dynamic 只出现在本方法内（闸门契约集中在 OfficeMacroSafety.RunGated，便于伪对象回归）。</summary>
    private static void SaveSourceToFormat(object application, string family, string source, string outputPath, int format)
    {
        dynamic app = application;
        switch (family)
        {
            case "word":
            {
                dynamic doc = app.Documents.Open(source);
                try { SaveAs(doc, outputPath, format); }
                finally { TrySet(() => doc.Close(false)); }
                break;
            }
            case "excel":
            {
                dynamic wb = app.Workbooks.Open(source);
                try { SaveAs(wb, outputPath, format); }
                finally { TrySet(() => wb.Close(false)); }
                break;
            }
            default:
            {
                dynamic pres = app.Presentations.Open(source, true, false, false);
                try { SaveAs(pres, outputPath, format); }
                finally { TrySet(() => pres.Close()); }
                break;
            }
        }
    }

    /// <summary>SaveAs2 优先，旧组件（WPS）回退 SaveAs。</summary>
    private static void SaveAs(dynamic doc, string path, int format)
    {
        try
        {
            doc.SaveAs2(path, format);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            doc.SaveAs(path, format);
        }
    }

    /// <summary>
    /// 【A05 审计整改 · 打开文件前的禁宏闸门】生产入口：接受 Office/WPS Application COM 对象，也接受
    /// <see cref="IDynComApp"/> 伪对象（回归测试注入）；实现与文案在 <see cref="OfficeMacroSafety.TryDisableMacrosBeforeOpen"/>。
    /// 实现用动态 COM 属性把 AutomationSecurity 置为 msoAutomationSecurityForceDisable(3)（安全模式ForceDisable），
    /// 并【读回校验】确认。返回 null = 已确认禁用宏，调用方可继续打开文件；返回非 null = <b>未能确认禁用宏</b>
    /// （属性不存在 / 设置抛错 / 读回失败 / 读回值不是 3），调用方【必须中止处理该文件，绝不能继续 Open】。
    /// 理由：Office 自动化在应用启动时 AutomationSecurity 默认为 msoAutomationSecurityLow(=1，允许所有宏)，
    /// Visible=false、DisplayAlerts=false 都不控制宏执行。
    /// Excel 官方建议该属性在程序化打开文件【前】紧邻设置（并在打开后复位，以免被恶意文档篡改）。
    ///
    /// 【能力边界 · 如实记录，勿据此宣称"已完全防护"】
    /// 1) Excel 4.0 宏（XLM）：Microsoft 对 msoAutomationSecurityForceDisable 明确注明
    ///    "This setting does not disable Microsoft Excel 4.0 macros. If a file that contains Microsoft Excel 4.0
    ///    macros is opened programmatically, the user will be prompted to decide whether to open the file."
    ///    —— 即本闸门不覆盖 XLM 宏。所以【不再靠注释收尾】：旧版 Excel 二进制（.xls/.xla/.xlt）的自动转换路径
    ///    已由 <see cref="OfficeMacroSafety.TryRefuseXlmUnsafeAutoConversion"/> 明确拒绝（文案口径「安全模式ForceDisable」），
    ///    不再进入 Workbooks.Open；本闸门只对仍允许的路径（Word / PPT / .et 等）做 VBA 宏控制。
    /// 2) 本闸门不做 BIFF 级解析、也读不到本机 XLM 策略状态：凡仍走 COM 的路径都无法在打开前判断文件是否含 XLM，
    ///    因此旧版 Excel 二进制一律走拒绝策略（见上），不宣称"已禁用所有宏"。
    /// 3) 本闸门只覆盖"经对象模型程序化打开文件"这一条路径的宏安全模式；文档内指向外部资源的内容
    ///    （Word 域/INCLUDETEXT、DDE、OLE 嵌入对象、外部数据连接、加载项等）不在其覆盖范围内。
    ///    需要强保证时应依赖组织级策略（禁宏、禁用 XLM、受保护视图）或改用不经 COM 的解析路径。
    /// </summary>
    /// <param name="app">已创建但【尚未打开任何文件】的 Office/WPS Application COM 对象，或 IDynComApp 测试替身。</param>
    /// <param name="componentName">用于错误提示的组件名（如 "Word" / "Excel / WPS 表格"）。</param>
    /// <returns>null = 确认已禁宏；非 null = 错误说明（调用方必须中止，不得打开文件）。</returns>
    public static string? TryDisableMacrosBeforeOpen(object? app, string componentName)
    {
        if (app is null)
            return MiscTexts.TSub($"{componentName} 的 Application 对象为空（未创建成功），已停止处理该文件，未打开它。");

        return OfficeMacroSafety.TryDisableMacrosBeforeOpen(
            app as IDynComApp ?? new DynamicComAppAdapter(app), componentName);
    }

    /// <summary>COM 属性设置失败不影响主流程（不同组件支持度不同）。
    /// 注意：仅限非安全性属性（Visible/DisplayAlerts/Close/Quit）；宏安全相关设置【不得】走这里。</summary>
    private static void TrySet(Action action)
    {
        try { action(); }
        catch { /* 忽略：WPS/Office 版本差异 */ }
    }

    private static string? FamilyOf(string source)
        => Path.GetExtension(source).ToLowerInvariant() switch
        {
            ".doc" or ".wps" or ".rtf" or ".odt" => "word",
            ".xls" or ".et" => "excel",
            ".ppt" or ".dps" or ".odp" => "ppt",
            _ => null
        };

    /// <summary>文件家族 → 目标扩展名的 SaveAs FileFormat 常量。</summary>
    private static int? FileFormatFor(string family, string targetExt) => (family, targetExt) switch
    {
        // Word: wdFormatXMLDocument=12, wdFormatPDF=17, wdFormatHTML=8, wdFormatUnicodeText=7
        ("word", ".docx") => 12,
        ("word", ".pdf") => 17,
        ("word", ".html") => 8,
        ("word", ".txt") => 7,
        // Excel: xlOpenXMLWorkbook=51, xlPDF=57, xlHtml=44, xlCSV=6
        ("excel", ".xlsx") => 51,
        ("excel", ".pdf") => 57,
        ("excel", ".html") => 44,
        ("excel", ".csv") => 6,
        // PowerPoint: ppSaveAsOpenXMLPresentation=24, ppSaveAsPDF=32
        ("ppt", ".pptx") => 24,
        ("ppt", ".pdf") => 32,
        _ => null
    };

    private static Type? ResolveProgId(string[] progIds)
    {
        foreach (var progId in progIds)
        {
            try
            {
                var type = Type.GetTypeFromProgID(progId);
                if (type is not null) return type;
            }
            catch
            {
                // 组件注册异常视为不可用
            }
        }
        return null;
    }

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        })
        { IsBackground = true, Name = "office-interop" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(TimeoutMinutes)))
            throw new TimeoutException(MiscTexts.TSub($"Office / WPS 转换超时（{TimeoutMinutes} 分钟无响应），已放弃"));
        if (error is not null)
            throw new InvalidOperationException(MiscTexts.TSub($"Office / WPS 转换失败：{error.Message}"), error);
        return result;
    }
}
