using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using WinUI3Localizer;

namespace TubaWinUi3.Services;

/// <summary>
/// 页面在语言切换后主动刷新 code-behind 赋值的文本。
/// WinUI3Localizer 的 XAML 自动刷新需要 l:Uids.Uid 附加属性；单独的 x:Uid 不走这里加载的 RESW。
/// </summary>
public interface ILocalizablePage
{
    void ApplyLocalization();
}

/// <summary>
/// 界面语言服务：启动时构建 WinUI3Localizer；顶栏/设置页切换语言后广播 LanguageChanged。
/// 资源位于 exe 旁 Strings/&lt;lang&gt;/Resources.resw；缺失的键回退调用方提供的原文（渐进式迁移）。
/// </summary>
public static class LocalizationService
{
    public const string LanguageKey = "Language";
    public const string AutoLanguage = "auto";
    public const string ChineseLanguage = "zh-CN";
    public const string EnglishLanguage = "en-US";

    public static event Action? LanguageChanged;

    private static bool _initialized;
    private static string _currentLanguage = ChineseLanguage;

    /// <summary>当前实际生效的语言标签（zh-CN / en-US）。</summary>
    public static string CurrentLanguage => _currentLanguage;

    /// <summary>必须在 App 构造阶段调用——早于任何打了 Uid 的控件创建。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            string stringsFolder = Path.Combine(AppContext.BaseDirectory, "Strings");
            if (!Directory.Exists(stringsFolder))
            {
                Debug.WriteLine($"[Localization] Strings 目录不存在，界面保持原文: {stringsFolder}");
                return;
            }

            string effective = ResolveEffectiveLanguage(AppSettings.Get(LanguageKey) ?? AutoLanguage);

            // Build/SetLanguage 只做本地文件读取与字典操作；放到线程池同步等待，
            // 避免 UI 线程被 SynchronizationContext 捕获造成死锁。
            Task.Run(async () =>
            {
                await new LocalizerBuilder()
                    .AddStringResourcesFolderForLanguageDictionaries(stringsFolder)
                    .AddLocalizationAction(new LocalizationActions.ActionItem(
                        typeof(Button),
                        args => ToolTipService.SetToolTip(args.DependencyObject, args.Value)))
                    .SetOptions(options => options.DefaultLanguage = ChineseLanguage)
                    .Build();

                if (effective != ChineseLanguage)
                {
                    await Localizer.Get().SetLanguage(effective);
                }
            }).GetAwaiter().GetResult();

            _currentLanguage = effective;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Localization] 初始化失败，界面保持原文: {ex}");
        }
    }

    /// <summary>取本地化字符串；键缺失时返回 fallback（迁移期内保持原文）。</summary>
    public static string L(string key, string fallback = "")
    {
        try
        {
            string value = Localizer.Get().GetLocalizedString(key);
            // 注意：库未构建时返回的是 NullLocalizer，它把键名当作结果返回；
            // 缺键时返回空串。两种情况都必须回退 fallback，否则界面会显示键名。
            return string.IsNullOrEmpty(value) || string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Localization] 读取 '{key}' 失败: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// 硬件信息标签的显示翻译。HardwareInfoService 的 Label 是数据键（恒中文，服务内部按它比较），
    /// 只在显示层经此映射翻译；未知标签原样返回。
    /// </summary>
    public static string TranslateHardwareLabel(string label) => label switch
    {
        "设备型号" => L("Hw_Label_DeviceModel", "设备型号"),
        "主板" => L("Hw_Label_Motherboard", "主板"),
        "BIOS" => L("Hw_Label_Bios", "BIOS"),
        "系统" => L("Hw_Label_System", "系统"),
        "版本" => L("Hw_Label_Version", "版本"),
        "运行时间" => L("Hw_Label_Uptime", "运行时间"),
        "处理器" => L("Hw_Label_Cpu", "处理器"),
        "内存" => L("Hw_Label_Memory", "内存"),
        "显卡" => L("Hw_Label_Gpu", "显卡"),
        "NPU" => L("Hw_Label_Npu", "NPU"),
        "显示器" => L("Hw_Label_Display", "显示器"),
        "硬盘" => L("Hw_Label_Disk", "硬盘"),
        "声卡" => L("Hw_Label_Sound", "声卡"),
        "网卡" => L("Hw_Label_Network", "网卡"),
        // 详情页字段标签（HardwareDetailPage 的 Item(...) 数据键）
        "名称" => L("Hw_Field_Name", "名称"),
        "代号" => L("Hw_Field_CodeName", "代号"),
        "封装" => L("Hw_Field_Package", "封装"),
        "核心数" => L("Hw_Field_Cores", "核心数"),
        "线程数" => L("Hw_Field_Threads", "线程数"),
        "最大频率" => L("Hw_Field_MaxClock", "最大频率"),
        "当前频率" => L("Hw_Field_CurrentClock", "当前频率"),
        "外频" => L("Hw_Field_BaseClock", "外频"),
        "L2 缓存" => L("Hw_Field_L2Cache", "L2 缓存"),
        "L3 缓存" => L("Hw_Field_L3Cache", "L3 缓存"),
        "架构" => L("Hw_Field_Architecture", "架构"),
        "制造商" => L("Hw_Field_Manufacturer", "制造商"),
        "ProcessorID" => L("Hw_Field_ProcessorId", "ProcessorID"),
        "型号" => L("Hw_Field_Model", "型号"),
        "芯片组" => L("Hw_Field_Chipset", "芯片组"),
        "BIOS 品牌" => L("Hw_Field_BiosBrand", "BIOS 品牌"),
        "BIOS 版本" => L("Hw_Field_BiosVersion", "BIOS 版本"),
        "BIOS 日期" => L("Hw_Field_BiosDate", "BIOS 日期"),
        "总容量" => L("Hw_Field_TotalCapacity", "总容量"),
        "类型" => L("Hw_Field_Type", "类型"),
        "通道模式" => L("Hw_Field_ChannelMode", "通道模式"),
        "插槽" => L("Hw_Field_Slots", "插槽"),
        "GPU 代码" => L("Hw_Field_GpuCode", "GPU 代码"),
        "显存" => L("Hw_Field_Vram", "显存"),
        "显存类型" => L("Hw_Field_VramType", "显存类型"),
        "显存位宽" => L("Hw_Field_VramBus", "显存位宽"),
        "驱动版本" => L("Hw_Field_DriverVersion", "驱动版本"),
        "驱动日期" => L("Hw_Field_DriverDate", "驱动日期"),
        "视频处理器" => L("Hw_Field_VideoProcessor", "视频处理器"),
        "当前分辨率" => L("Hw_Field_CurrentResolution", "当前分辨率"),
        "刷新率" => L("Hw_Field_RefreshRate", "刷新率"),
        "容量" => L("Hw_Field_Capacity", "容量"),
        "温度" => L("Hw_Field_Temperature", "温度"),
        "接口" => L("Hw_Field_Interface", "接口"),
        "固件版本" => L("Hw_Field_Firmware", "固件版本"),
        "序列号" => L("Hw_Field_Serial", "序列号"),
        "算力" => L("Hw_Field_ComputePower", "算力"),
        "分辨率" => L("Hw_Field_Resolution", "分辨率"),
        "尺寸" => L("Hw_Field_Size", "尺寸"),
        "状态" => L("Hw_Field_Status", "状态"),
        "MAC 地址" => L("Hw_Field_MacAddress", "MAC 地址"),
        "速度" => L("Hw_Field_Speed", "速度"),
        "主显示器" => L("Hw_Field_PrimaryDisplay", "主显示器"),
        "型号信息" => L("Hw_Section_ModelInfo", "型号信息"),
        "系统信息" => L("Hw_Section_SystemInfo", "系统信息"),
        "详细信息" => L("Hw_Section_Details", "详细信息"),
        _ => label
    };

    /// <summary>切换语言（"auto" | "zh-CN" | "en-US"）：资源切换成功后才持久化并通知界面。</summary>
    public static async Task SetLanguageAsync(string value)
    {
        if (value is not (AutoLanguage or ChineseLanguage or EnglishLanguage))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported UI language.");

        string effective = ResolveEffectiveLanguage(value);
        if (!LocalizerBuilder.IsLocalizerAlreadyBuilt)
        {
            Debug.WriteLine("[Localization] 切换语言失败: 本地化资源尚未加载");
            return;
        }

        string previousLanguage = _currentLanguage;
        try
        {
            await Localizer.Get().SetLanguage(effective);
            AppSettings.Set(LanguageKey, value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Localization] 切换语言失败: {ex}");
            if (effective != previousLanguage)
            {
                try { await Localizer.Get().SetLanguage(previousLanguage); }
                catch (Exception rollbackEx) { Debug.WriteLine($"[Localization] 回退语言失败: {rollbackEx}"); }
            }
            return;
        }

        _currentLanguage = effective;
        if (effective != previousLanguage)
            LanguageChanged?.Invoke();
    }

    /// <summary>外部工具分类（Tools/ 目录名）→ 显示名；未知分类沿用原有「去掉『工具』后缀」行为。</summary>
    public static string GetCategoryDisplayName(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return string.Empty;

        return CategoryDisplayNames.TryGetToolCategoryKey(category, out string key)
            ? L(key, category)
            : category.Replace("工具", "");
    }

    /// <summary>内置工具分类（IBuiltinTool.Category，显示保留「工具」后缀）→ 显示名。</summary>
    public static string GetBuiltinCategoryDisplayName(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return string.Empty;

        return CategoryDisplayNames.TryGetBuiltinCategoryKey(category, out string key)
            ? L(key, category)
            : category;
    }

    /// <summary>工具标签（tools.json tags，恒中文数据键）→ 显示名；未知标签原样返回。</summary>
    public static string GetTagDisplayName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;

        return CategoryDisplayNames.TryGetTagKey(tag, out string key)
            ? L(key, tag)
            : tag;
    }

    /// <summary>"auto" 解析为跟随系统：中文系统 → zh-CN，其余 → en-US（当前仅支持这两种）。</summary>
    public static string ResolveEffectiveLanguage(string value)
    {
        return value switch
        {
            ChineseLanguage => ChineseLanguage,
            EnglishLanguage => EnglishLanguage,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? ChineseLanguage
                : EnglishLanguage,
        };
    }
}
