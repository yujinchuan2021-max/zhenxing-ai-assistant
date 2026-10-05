namespace TubaWinUi3.Services;

/// <summary>
/// 硬件块显示层本地化（值侧）。
/// Label 侧走 <see cref="LocalizationService.TranslateHardwareLabel"/>；
/// 数据层（HardwareInfoService / HardwareDetailPage.Item 的 Label 与 Value）保持中文业务键不变，
/// 判定（Label 比较、容量 == "空" 等）不受影响，只在显示层翻译。
/// </summary>
public static class HardwareTexts
{
    /// <summary>值显示：空白 → 「未知」（当前语言）；否则品牌括号语言化。</summary>
    public static string ValueText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? LocalizationService.L("Hw_Unknown", "未知") : BrandAware(value);

    /// <summary>
    /// 品牌显示名：en 界面优先纯拉丁侧——「华硕(ASUS)」→「ASUS」、「海力士(SK Hynix)」→「SK Hynix」、
    /// 「NXP(原飞利浦半导体)」→「NXP」；括号内外同为单一字符集或无法判型时原样返回；zh 界面原样。
    /// </summary>
    internal static string BrandAware(string text)
    {
        if (LocalizationService.CurrentLanguage != "en-US") return text;
        var i = text.IndexOf('(');
        if (i <= 0 || !text.EndsWith(")")) return text;
        var outside = text[..i].Trim();
        var inside = text[(i + 1)..^1].Trim();
        if (outside.Length == 0 || inside.Length == 0) return text;
        var outAscii = IsAsciiOnly(outside);
        var inAscii = IsAsciiOnly(inside);
        if (outAscii == inAscii) return text;
        return outAscii ? outside : inside;
    }

    private static bool IsAsciiOnly(string s) => s.All(c => c < 128);
}
