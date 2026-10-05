namespace TubaWinUi3.Services;

/// <summary>
/// A05（审计整改）：Office/WPS Application COM 对象的可注入访问层，只暴露「禁宏闸门」需要的两个动作。
/// 生产端由 <see cref="DynamicComAppAdapter"/> 包装已创建、尚未打开任何文件的 COM 对象；
/// 测试端用伪对象（记录调用顺序 / 注入抛错）即可完整回归闸门，无需启动 Office、不使用宏样本。
/// </summary>
public interface IDynComApp
{
    /// <summary>设置 AutomationSecurity。抛异常 = 组件不支持或拒绝该设置（闸门失败）。</summary>
    void SetAutomationSecurity(int value);

    /// <summary>读回 AutomationSecurity。抛异常 = 读回失败（无法确认宏已禁用）。</summary>
    object? GetAutomationSecurity();
}

/// <summary>A05：把已创建但【尚未打开任何文件】的 Office/WPS Application COM 对象适配成 <see cref="IDynComApp"/>。
/// dynamic 调度保持不变（含属性不存在时的 RuntimeBinderException），故生产行为与旧实现一致。</summary>
public sealed class DynamicComAppAdapter : IDynComApp
{
    private readonly object _application;

    public DynamicComAppAdapter(object application)
        => _application = application ?? throw new ArgumentNullException(nameof(application));

    public void SetAutomationSecurity(int value)
    {
        dynamic app = _application;
        app.AutomationSecurity = value;
    }

    public object? GetAutomationSecurity()
    {
        dynamic app = _application;
        return app.AutomationSecurity;
    }
}

/// <summary>
/// A05（审计整改）：两条防线实现在一处、生产与测试共用。
///
/// ① 禁宏闸门 <see cref="TryDisableMacrosBeforeOpen"/>：打开文件【之前】把 AutomationSecurity 置为
///    msoAutomationSecurityForceDisable(3)（即「安全模式ForceDisable」）并【读回校验】；
///    设置抛错 / 属性不可用 / 读回抛错 / 读回值不是 3 —— 任一步失败都返回错误，调用方必须中止（不打开文件）。
///    <see cref="RunGated"/> 把「闸门 + 打开」串成一次调用：闸门失败时打开动作一次也不会执行。
///
/// ② XLM 拒绝策略 <see cref="TryRefuseXlmUnsafeAutoConversion"/>：Excel 4.0 宏（XLM）【不在】①的覆盖范围内
///    （Microsoft 原文：msoAutomationSecurityForceDisable does not disable Microsoft Excel 4.0 macros），
///    旧版 Excel 二进制又无法在打开前可靠判定是否含 XLM → 该自动转换路径直接【拒绝】，
///    并给出不执行活动内容的替代路径（内置引擎另存为 .xlsx）。
///
/// 口径（勿写成「所有宏已禁用 / 问题全修」）：①只能保证「安全模式ForceDisable 已生效」，覆盖 VBA 宏；
/// XLM、DDE、OLE 嵌入对象、外部数据连接、加载项等【不在覆盖内】——这些走「拒绝路径 / 如实标注边界」，
/// 不靠注释宣称已修复。
/// </summary>
public static class OfficeMacroSafety
{
    /// <summary>msoAutomationSecurityForceDisable —— 安全模式ForceDisable（禁用宏且不提示）。</summary>
    public const int ForceDisable = 3;

    /// <summary>
    /// 【A05 · 打开文件前的禁宏闸门】把 AutomationSecurity 置为 3 并读回校验。
    /// 返回 null = 已确认禁用宏，调用方可继续打开文件；返回非 null = <b>未能确认禁用宏</b>
    /// （设置抛错 / 读回抛错 / 读回值不是 3），调用方【必须中止处理该文件，绝不能继续 Open】。
    /// 理由：Office 自动化默认 msoAutomationSecurityLow(=1，允许所有宏)，Visible=false、DisplayAlerts=false 都不控制宏执行。
    /// </summary>
    /// <param name="app">已创建但【尚未打开任何文件】的 Application（生产 = DynamicComAppAdapter 包装的 COM 对象）。</param>
    /// <param name="componentName">用于错误提示的组件名（如 "Word" / "Excel / WPS 表格"）。</param>
    public static string? TryDisableMacrosBeforeOpen(IDynComApp app, string componentName)
    {
        try
        {
            app.SetAutomationSecurity(ForceDisable);
        }
        catch (Exception ex)
        {
            return MiscTexts.TSub($"{componentName} 不支持或拒绝了在打开文件前禁用宏（AutomationSecurity 设置失败：{ex.Message}）；")
                 + MiscTexts.T("已停止处理该文件，未打开它。请先用 Office/WPS 打开该文件并另存为 .docx/.xlsx/.pptx 后再上传。");
        }

        int applied;
        try
        {
            var raw = app.GetAutomationSecurity();
            applied = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            return MiscTexts.TSub($"{componentName} 无法读回宏安全设置（{ex.Message}），无法确认宏已禁用；")
                 + MiscTexts.T("已停止处理该文件，未打开它。请先用 Office/WPS 打开该文件并另存为新格式后再上传。");
        }

        if (applied != ForceDisable)
        {
            return MiscTexts.TSub($"{componentName} 未接受禁宏设置（读回 AutomationSecurity={applied}，期望 {ForceDisable}，即安全模式ForceDisable）；")
                 + MiscTexts.T("已停止处理该文件，未打开它。请先用 Office/WPS 打开该文件并另存为新格式后再上传。");
        }

        return null;
    }

    /// <summary>
    /// 【A05 契约】把「禁宏闸门」与「打开文件」串成一次调用：只有闸门确认通过才会执行 <paramref name="openAction"/>。
    /// 返回 null = 已确认禁宏且 openAction 已执行（恰好一次）；返回非 null = 闸门失败的错误说明，此时
    /// <paramref name="openAction"/>【一次也没有执行】。<paramref name="openAction"/> 自身抛出的异常原样向上传播
    /// （调用方的回退 / 清理逻辑不变）。
    /// </summary>
    public static string? RunGated(IDynComApp app, string componentName, Action openAction)
    {
        var error = TryDisableMacrosBeforeOpen(app, componentName);
        if (error is not null) return error;
        openAction();
        return null;
    }

    /// <summary>
    /// 【A05 · XLM 策略】该路径是否属于「可驻留 Excel 4.0 宏（XLM 宏表）」的旧版 Excel 二进制格式。
    /// 命中：.xls / .xla / .xlt（BIFF 容器）。.xlsx / .xlsm 不在此列 —— 它们不走 COM，
    /// 由内置 zip 解析读取（不执行活动内容），所以无需在此拒绝。
    /// </summary>
    public static bool IsXlmCapableLegacyFormat(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xls" or ".xla" or ".xlt" => true,
            _ => false,
        };
    }

    /// <summary>
    /// 【A05 · XLM 策略】旧版 Excel 二进制（.xls/.xla/.xlt）的【自动转换】一律拒绝。
    /// 返回非 null = 拒绝原因，调用方必须中止：不得创建 Office/WPS 实例、不得打开该文件。
    ///
    /// 为什么是「拒绝」而不是「注释说明」：闸门只设置 AutomationSecurity=3（安全模式ForceDisable），
    /// Microsoft 明确写明该设置【不覆盖 Excel 4.0 宏】——带 XLM 的工作簿被程序化打开时，是否执行由用户 /
    /// 信任中心策略决定；本工具又不做 BIFF 级解析、读不到本机 XLM 策略状态，无法在打开前保证该文件不含
    /// 可执行的 XLM。因此这条自动转换路径无法保证安全，直接拒绝，并给出不执行活动内容的替代路径。
    /// </summary>
    public static string? TryRefuseXlmUnsafeAutoConversion(string? sourcePath)
    {
        if (!IsXlmCapableLegacyFormat(sourcePath)) return null;

        var ext = Path.GetExtension(sourcePath!).ToLowerInvariant();
        var name = Path.GetFileName(sourcePath!);
        if (string.IsNullOrWhiteSpace(name)) name = MiscTexts.T("该文件");

        return MiscTexts.TSub($"'{name}' 是旧版 Excel 二进制格式（{ext}），可携带 Excel 4.0 宏（XLM 宏表）：")
             + MiscTexts.T("打开前的禁宏闸门只设置 AutomationSecurity=3（安全模式ForceDisable），而 Microsoft 明确说明该设置")
             + MiscTexts.T("不覆盖 Excel 4.0 宏，本工具也无法在打开前判断文件是否含 XLM，故该自动转换路径无法保证安全，")
             + MiscTexts.T("已拒绝执行（未创建 Office/WPS 实例、未打开该文件）。")
             + MiscTexts.T("替代做法：用本应用『格式转换』的内置引擎（纯解析、不执行宏）把它另存为 .xlsx 后再上传；")
             + MiscTexts.T("或先用 Excel/WPS 手动打开并另存为 .xlsx。");
    }
}
