using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TubaWinUi3.Services;

/// <summary>
/// App.xaml 的资源字典根（见 App.xaml &lt;svc:AppFontDictionary/&gt;）。
///
/// 机制：XAML 解析本元素时（App 构造 → InitializeComponent 内，早于任何页面/窗口与 AppSettings.Load）
/// 由构造函数按「设置 > 外观 > 界面字体」的已保存选择注入两个字体资源键：
/// <c>ContentControlThemeFontFamily</c>（覆盖主题字体键，控件模板与继承链全部生效）与
/// <c>AppFontFamily</c>（隐式样式 / 显式引用）。键值自字典创建起即存在——解析期构造，而非运行期改写，
/// 因此不经过 font-maintenance.md §9 记录的「运行中替换活动资源字典字体键 → WinUI stowed exception
/// 0xC000027B 延迟崩溃」路径；选择在下次应用启动时生效。
///
/// 与既有生成机制的关系：本类即原 App.xaml「APP-FONT 生成块」的替代——块内两个静态 FontFamily 字面量
/// 退场，改为启动时按目录 + 已存选择推导（字面量只此一处存在于 app-font.json）。生成器不再触碰
/// App.xaml；FontSingleSourceTests 钉住结构（根元素类型、无字体字面量）与本类语义。
///
/// 安全边界（启动路径，绝不抛）：配置缺失/损坏 → <see cref="AppFontSpec"/> 编译期镜像；
/// 未保存/未知选择 → 目录首项（更纱）；资源注入异常 → 记录 <see cref="AppFonts.LastResourceInjectError"/>
/// 并放行（大不了回到系统默认字体，绝不让应用因字体启动失败）。
/// </summary>
public sealed partial class AppFontDictionary : ResourceDictionary
{
    /// <summary>XAML 解析入口：读取已保存选择（settings.json 的 UiFontChoice；缺失/损坏按默认）。</summary>
    public AppFontDictionary() : this(AppFonts.TryReadSavedChoiceId())
    {
    }

    /// <summary>可测试接缝：显式指定已保存选择（null = 未保存 → 默认；未知 id → 默认）。</summary>
    internal AppFontDictionary(string? savedChoiceId)
    {
        try
        {
            // PrepareForStartup：加载/校验目录（失败 → 编译期镜像）→ 定位选择 → 返回 XAML 字体 URI。绝不抛。
            var uri = AppFonts.PrepareForStartup(savedChoiceId);
            var family = new FontFamily(uri);
            this["ContentControlThemeFontFamily"] = family;
            this["AppFontFamily"] = family;
        }
        catch (System.Exception ex)
        {
            // 兜底：不注入任何键（界面回到系统默认字体），记录原因供诊断；绝不因字体让启动崩溃。
            AppFonts.NoteResourceInjectFailure(ex.Message);
            System.Diagnostics.Debug.WriteLine($"[AppFontDictionary] 字体资源注入失败：{ex.Message}");
        }
    }
}
