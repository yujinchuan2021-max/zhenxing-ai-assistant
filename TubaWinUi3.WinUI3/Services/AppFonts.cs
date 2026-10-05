using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Services
{
    /// <summary>
    /// 单个界面字体候选（Assets/Fonts/app-font.json 的 choices[] 一项）。
    /// 全应用所有字体取值都必须来自本配置（或其编译期镜像 <see cref="AppFontCatalog.Fallback"/>）——
    /// 页面/服务不得再写字体名或路径字面量。
    /// </summary>
    public sealed partial class AppFontSpec
    {
        /// <summary>候选 id（settings.json 的 UiFontChoice 存这个值；^[a-z][a-z0-9-]{0,31}$）。</summary>
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        /// <summary>「设置 > 外观 > 界面字体」下拉的显示名。</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; init; } = "";

        /// <summary>内部族名（XAML FontFamily / 打包 URI 的 #fragment）。</summary>
        [JsonPropertyName("familyName")]
        public string FamilyName { get; init; } = "";

        /// <summary>常规字重字体文件名（Fonts 目录内）。</summary>
        [JsonPropertyName("regular")]
        public string RegularFileName { get; init; } = "";

        /// <summary>粗体字体文件名（Fonts 目录内）。</summary>
        [JsonPropertyName("bold")]
        public string BoldFileName { get; init; } = "";

        /// <summary>字体许可文件名（Fonts 目录内，随包交付）。</summary>
        [JsonPropertyName("license")]
        public string LicenseFileName { get; init; } = "";

        /// <summary>字体署名/许可说明（「开源与致谢」卡片可见值的唯一来源；缺失即校验失败）。</summary>
        [JsonPropertyName("attribution")]
        public FontAttribution Attribution { get; init; } = new();

        /// <summary>署名信息（author=作者 / url=项目地址 / licenseText=许可说明文案）。</summary>
        public sealed class FontAttribution
        {
            [JsonPropertyName("author")]
            public string Author { get; init; } = "";

            [JsonPropertyName("url")]
            public string Url { get; init; } = "";

            [JsonPropertyName("licenseText")]
            public string LicenseText { get; init; } = "";
        }

        /// <summary>取 Web 字体栈里的首个字体族名（去外层成对引号 + 反斜杠转义还原；与生成器同一口径）。</summary>
        public static string PrimaryFamilyOf(string stack)
        {
            if (string.IsNullOrWhiteSpace(stack)) return "";
            var seg = stack.Split(',')[0].Trim();
            if (seg.Length >= 2 && seg[0] == seg[^1] && (seg[0] == '\'' || seg[0] == '"'))
                seg = Unescape(seg[1..^1]);
            return seg;
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    sb.Append(s[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }

        internal static bool HasControlChars(string s)
        {
            foreach (var c in s)
                if (char.IsControl(c)) return true;
            return false;
        }

        /// <summary>族名校验（结构性禁字符有明确原因；其余特殊字符由生成器按输出语法转义）。</summary>
        internal static void ValidateFamilyName(string family, string where)
        {
            if (string.IsNullOrWhiteSpace(family))
                throw new InvalidDataException($"{where} 缺失或为空");
            //   # = 打包 URI 的文件路径/族名分隔符；, = 字体栈与各宿主列表的族名分隔符；控制字符无法进三种输出。
            if (family != family.Trim())
                throw new InvalidDataException($"{where} 首尾不能有空白：{family}");
            if (family.Contains('#'))
                throw new InvalidDataException($"{where} 不能包含 #（打包 URI 的路径/族名分隔符）：{family}");
            if (family.Contains(','))
                throw new InvalidDataException($"{where} 不能包含 ,（字体栈/列表的族名分隔符）：{family}");
            if (HasControlChars(family))
                throw new InvalidDataException($"{where} 含控制字符");
        }

        /// <summary>文件名校验（必须留在 Fonts 目录内 + 文件存在）。</summary>
        internal static void ValidateFileName(string name, string where, bool requireExists, string fontsDirFull)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"{where} 缺失或为空");
            if (name != Path.GetFileName(name) || name.Contains(".."))
                throw new InvalidDataException($"{where} 必须留在 Fonts 目录内（不允许路径分隔或上级引用）：{name}");
            if (name.Contains('#'))
                throw new InvalidDataException($"{where} 不能包含 #（打包 URI 的路径/族名分隔符）：{name}");
            if (HasControlChars(name))
                throw new InvalidDataException($"{where} 含控制字符：{name}");
            if (requireExists)
            {
                var path = Path.GetFullPath(Path.Combine(fontsDirFull, name));
                if (!path.StartsWith(fontsDirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{where} 路径越出 Fonts 目录：{name}");
                if (!File.Exists(path))
                    throw new InvalidDataException($"字体/许可文件不存在：{path}");
            }
        }

        /// <summary>署名校验（三字段非空 + url http(s) + 无控制字符）。</summary>
        internal static void ValidateAttribution(FontAttribution? attr, string where)
        {
            if (attr is null)
                throw new InvalidDataException($"{where}.attribution 缺失");
            foreach (var (label, value) in new[]
                     {
                         ($"{where}.attribution.author", attr.Author),
                         ($"{where}.attribution.url", attr.Url),
                         ($"{where}.attribution.licenseText", attr.LicenseText),
                     })
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidDataException($"{label} 缺失或为空");
            }
            if (!Uri.TryCreate(attr.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"{where}.attribution.url 必须是 http(s):// 绝对地址：{attr.Url}");
            if (HasControlChars(attr.Author) || HasControlChars(attr.Url) || HasControlChars(attr.LicenseText))
                throw new InvalidDataException($"{where}.attribution 含控制字符");
        }
    }

    /// <summary>代码/数值等宽字体（与界面字体候选独立、随包交付；monoStack 首族必须是它）。</summary>
    public sealed partial class AppFontMonoSpec
    {
        [JsonPropertyName("familyName")]
        public string FamilyName { get; init; } = "";

        [JsonPropertyName("regular")]
        public string RegularFileName { get; init; } = "";

        [JsonPropertyName("bold")]
        public string BoldFileName { get; init; } = "";

        [JsonPropertyName("license")]
        public string LicenseFileName { get; init; } = "";
    }

    /// <summary>
    /// 字体唯一权威配置（Assets/Fonts/app-font.json，v2 目录化）：
    /// choices[] = 界面字体候选（顺序即设置页列表顺序；首项 = 默认，无已保存选择时使用）；
    /// mono = 等宽字体；uiStackSuffix = UI 字体栈的公共回退尾（Web 端）。
    /// 加载即全量校验，任何不合法都抛 <see cref="InvalidDataException"/>（fail-closed）。
    /// </summary>
    public sealed partial class AppFontCatalog
    {
        /// <summary>配置文件名（位于 Assets/Fonts/ 下）。</summary>
        public const string JsonFileName = "app-font.json";

        private static readonly Regex IdRe = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.Compiled);

        /// <summary>UI 字体栈的公共回退尾：运行时栈 = '所选族名', uiStackSuffix。</summary>
        [JsonPropertyName("uiStackSuffix")]
        public string UiStackSuffix { get; init; } = "";

        /// <summary>等宽（代码/数值）字体栈（--app-font-mono；首族必须等于 mono.familyName）。</summary>
        [JsonPropertyName("monoStack")]
        public string MonoStack { get; init; } = "";

        /// <summary>等宽字体规格。</summary>
        [JsonPropertyName("mono")]
        public AppFontMonoSpec Mono { get; init; } = new();

        /// <summary>界面字体候选目录（顺序即设置页列表顺序；首项为默认）。</summary>
        [JsonPropertyName("choices")]
        public List<AppFontSpec> Choices { get; init; } = new();

        /// <summary>按 id 查找候选（未知/空 → null）。</summary>
        public AppFontSpec? Find(string? id)
            => string.IsNullOrWhiteSpace(id) ? null
               : Choices.FirstOrDefault(c => string.Equals(c.Id, id.Trim(), StringComparison.Ordinal));

        /// <summary>
        /// 从 Fonts 目录（内含 app-font.json 与字体/许可文件）加载并全量校验。
        /// 缺失、JSON 错误、字段为空、结构性禁字符、路径越界、文件不存在、id 非法/重复、
        /// monoStack 首族不符、attribution 非法等一律抛 <see cref="InvalidDataException"/>。
        /// </summary>
        public static AppFontCatalog LoadFrom(string fontsDir)
        {
            if (string.IsNullOrWhiteSpace(fontsDir))
                throw new InvalidDataException("字体配置目录为空");

            var dirFull = Path.GetFullPath(fontsDir);
            var jsonPath = Path.Combine(dirFull, JsonFileName);
            if (!File.Exists(jsonPath))
                throw new InvalidDataException($"字体配置不存在：{jsonPath}");

            AppFontCatalog? spec;
            try
            {
                spec = JsonSerializer.Deserialize<AppFontCatalog>(
                    File.ReadAllText(jsonPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"字体配置不是合法 JSON：{jsonPath}", ex);
            }

            if (spec is null)
                throw new InvalidDataException($"字体配置为空：{jsonPath}");

            foreach (var (label, value) in new[]
                     {
                         ("uiStackSuffix", spec.UiStackSuffix),
                         ("monoStack", spec.MonoStack),
                     })
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidDataException($"字体配置缺少字段：{label}");
                if (AppFontSpec.HasControlChars(value))
                    throw new InvalidDataException($"字体配置 {label} 含控制字符");
            }

            var mono = spec.Mono;
            if (mono is null)
                throw new InvalidDataException("字体配置缺少字段：mono");
            AppFontSpec.ValidateFamilyName(mono.FamilyName, "mono.familyName");
            foreach (var (label, name) in new[]
                     {
                         ("mono.regular", mono.RegularFileName),
                         ("mono.bold", mono.BoldFileName),
                         ("mono.license", mono.LicenseFileName),
                     })
            {
                AppFontSpec.ValidateFileName(name, $"字体配置 {label}", requireExists: true, dirFull);
            }
            var monoFirst = AppFontSpec.PrimaryFamilyOf(spec.MonoStack);
            if (monoFirst != mono.FamilyName)
                throw new InvalidDataException($"字体配置 monoStack 的首个字体族必须是 {mono.FamilyName}（实际 {monoFirst}）");

            var choices = spec.Choices;
            if (choices is null || choices.Count == 0)
                throw new InvalidDataException("字体配置缺少字段：choices（至少一个候选字体）");

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                var where = $"choices[{i}]";
                if (choice is null)
                    throw new InvalidDataException($"字体配置 {where} 必须是对象");
                if (string.IsNullOrEmpty(choice.Id) || !IdRe.IsMatch(choice.Id))
                    throw new InvalidDataException($"字体配置 {where}.id 必须是 ^[a-z][a-z0-9-]{{0,31}}$：{choice.Id}");
                if (!seenIds.Add(choice.Id))
                    throw new InvalidDataException($"字体配置 {where}.id 重复：{choice.Id}");
                if (string.IsNullOrWhiteSpace(choice.DisplayName))
                    throw new InvalidDataException($"字体配置 {where}.displayName 缺失或为空");
                if (AppFontSpec.HasControlChars(choice.DisplayName))
                    throw new InvalidDataException($"字体配置 {where}.displayName 含控制字符");
                AppFontSpec.ValidateFamilyName(choice.FamilyName, $"{where}.familyName");
                foreach (var (label, name) in new[]
                         {
                             ($"{where}.regular", choice.RegularFileName),
                             ($"{where}.bold", choice.BoldFileName),
                             ($"{where}.license", choice.LicenseFileName),
                         })
                {
                    AppFontSpec.ValidateFileName(name, $"字体配置 {label}", requireExists: true, dirFull);
                }
                AppFontSpec.ValidateAttribution(choice.Attribution, where);
            }

            return spec;
        }
    }

    /// <summary>
    /// ZXAI 全局字体的唯一对外 API —— 读取/校验 Assets/Fonts/app-font.json（<see cref="AppFontCatalog"/>），
    /// 并按「设置 > 外观 > 界面字体」的已保存选择（settings.json 的 UiFontChoice）解析生效候选。
    /// 选择在下次启动时生效：正式应用由 App.xaml 的 &lt;svc:AppFontDictionary/&gt; 在解析期注入字体资源键
    /// （见 Services/AppFontDictionary.cs）；本类及生成物之外不得再出现字体名/路径字面量
    /// （唯一例外见 CreateGdiFont 的系统回退）。
    /// </summary>
    public static class AppFonts
    {
        /// <summary>settings.json 里保存界面字体选择的键（值为候选 id）。</summary>
        public const string UiFontChoiceKey = "UiFontChoice";

        private static AppFontCatalog _catalog = AppFontCatalog.Fallback;
        private static string? _choiceId;
        private static string? _loadError;

        /// <summary>最近一次 Initialize 的失败原因（成功为 null）。</summary>
        public static string? LoadError => _loadError;

        /// <summary>最近一次字体资源注入失败原因（成功为 null；诊断用）。</summary>
        public static string? LastResourceInjectError { get; private set; }

        internal static void NoteResourceInjectFailure(string message) => LastResourceInjectError = message;

        /// <summary>当前生效的字体目录配置。</summary>
        public static AppFontCatalog Catalog => _catalog;

        /// <summary>界面字体候选（顺序即设置页列表顺序）。</summary>
        public static IReadOnlyList<AppFontSpec> Choices => _catalog.Choices;

        /// <summary>当前生效的候选（已保存选择 → 目录首项兜底）。</summary>
        public static AppFontSpec Effective => _catalog.Find(_choiceId) ?? _catalog.Choices[0];

        /// <summary>当前生效候选的 id。</summary>
        public static string ChoiceId => Effective.Id;

        /// <summary>候选 id → 目录下标（未知 → -1；设置页下拉用）。</summary>
        public static int IndexOfChoice(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return -1;
            for (var i = 0; i < _catalog.Choices.Count; i++)
                if (string.Equals(_catalog.Choices[i].Id, id.Trim(), StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>
        /// 应用启动时调用（App() 构造函数，任何页面加载前）：
        /// 加载并校验配置；失败时退回编译期镜像（AppFontFallback.g.cs，与配置逐字段一致）并记录日志——
        /// 绝不静默使用系统字体，也不让启动因字体配置损坏而崩溃。不会改动已保存的选择。
        /// </summary>
        public static void Initialize() => InitializeFromDirectory(AppContext.BaseDirectory);

        /// <summary>从指定应用目录读取 Assets\Fonts\app-font.json（独立宿主与测试用同一实现）。</summary>
        public static void InitializeFromDirectory(string appDir)
        {
            try
            {
                _catalog = AppFontCatalog.LoadFrom(Path.Combine(appDir, "Assets", "Fonts"));
                _loadError = null;
            }
            catch (Exception ex)
            {
                _catalog = AppFontCatalog.Fallback;
                _loadError = ex.Message;
                try { AgentDebugLog.Error("字体配置加载失败，退回编译期镜像：" + ex.Message, ex); } catch { }
                System.Diagnostics.Debug.WriteLine($"[AppFonts] 字体配置加载失败，退回编译期镜像：{ex.Message}");
            }
        }

        /// <summary>
        /// 设定已保存的选择（null/空白 → 默认；未知 id 在解析时自然回落首项）。绝不抛。
        /// 仅影响本进程内取值；持久化由设置页经 AppSettings.Set("UiFontChoice", id) 完成。
        /// </summary>
        public static void SetChoice(string? id)
            => _choiceId = string.IsNullOrWhiteSpace(id) ? null : id.Trim();

        /// <summary>
        /// 启动最早点（App.xaml 解析 &lt;svc:AppFontDictionary/&gt; 时）调用：
        /// 确保目录就绪 + 定位已保存选择，返回应注入的 XAML 字体 URI。绝不抛。
        /// </summary>
        public static string PrepareForStartup(string? savedChoiceId)
        {
            try { Initialize(); } catch { /* Initialize 内部已兜底；双保险 */ }
            SetChoice(savedChoiceId);
            return XamlFontUri;
        }

        /// <summary>读取 settings.json 里的已保存选择（任何异常 → null，绝不抛）。</summary>
        public static string? TryReadSavedChoiceId()
        {
            try { return ReadSavedChoiceIdFrom(ConfigManager.GetSettingsPath()); }
            catch { return null; }
        }

        /// <summary>从指定 settings.json 路径读取已保存选择（纯函数；测试用同一实现）。</summary>
        internal static string? ReadSavedChoiceIdFrom(string settingsPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(UiFontChoiceKey, out var el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    var v = el.GetString();
                    return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                }
            }
            catch { }
            return null;
        }

        // ---- 由生效候选推导的取值（全应用唯一来源）----

        public static string FamilyName => Effective.FamilyName;
        public static string RegularFileName => Effective.RegularFileName;
        public static string BoldFileName => Effective.BoldFileName;
        public static string LicenseFileName => Effective.LicenseFileName;

        private static string FontsDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

        /// <summary>常规字重字体文件的绝对路径（System.Drawing / SkiaSharp 等按文件加载用）。</summary>
        public static string RegularFilePath => Path.Combine(FontsDir, Effective.RegularFileName);

        /// <summary>粗体字体文件的绝对路径。</summary>
        public static string BoldFilePath => Path.Combine(FontsDir, Effective.BoldFileName);

        /// <summary>字体许可文件的绝对路径。</summary>
        public static string LicensePath => Path.Combine(FontsDir, Effective.LicenseFileName);

        /// <summary>打包字体 URI（XAML 资源与 WinUI FontFamily 的统一入口；随文件名/族名自动变化）。
        /// 用打包 URI 而不是裸族名：不依赖系统安装，换机器不会静默回退。</summary>
        public static string XamlFontUri => BuildXamlFontUri(Effective);

        /// <summary>按候选生成打包字体 URI（测试/宿主复用）。</summary>
        public static string BuildXamlFontUri(AppFontSpec spec)
            => $"ms-appx:///Assets/Fonts/{spec.RegularFileName}#{spec.FamilyName}";

        /// <summary>WinUI FontFamily 实例（给 C# 动态创建控件用）。</summary>
        public static FontFamily WinUI => new(XamlFontUri);

        /// <summary>Web 端回退栈（UI 文本；'生效族名', uiStackSuffix）。</summary>
        public static string WebFontStack => $"'{Effective.FamilyName}', {_catalog.UiStackSuffix}";

        /// <summary>Web 端回退栈（数值/等宽上下文；与界面字体选择独立）。</summary>
        public static string WebMonoStack => _catalog.MonoStack;

        /// <summary>CSS format() 值：由文件扩展名推导（OTF/CFF 为 opentype，不写死 truetype）。</summary>
        public static string FormatOf(string fileName)
        {
            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".otf" or ".otc" => "opentype",
                ".woff2" => "woff2",
                ".woff" => "woff",
                _ => "truetype",
            };
        }

        // ---- 署名/许可说明（「开源与致谢」卡片可见值的唯一来源；全部来自 app-font.json）----

        /// <summary>字体作者署名（生效候选）。</summary>
        public static string AttributionAuthor => Effective.Attribution.Author;

        /// <summary>字体项目地址（http/https 绝对地址；生效候选）。</summary>
        public static string AttributionUrl => Effective.Attribution.Url;

        /// <summary>许可说明文案（生效候选）。</summary>
        public static string AttributionLicenseText => Effective.Attribution.LicenseText;

        /// <summary>「开源与致谢」卡片副标题：{署名} — 界面字体（{许可说明}）。</summary>
        public static string AttributionDetail => string.Format(LocalizationService.L("Settings_FontAttributionDetail", "{0} — 界面字体（{1}）"), MiscTexts.T(Effective.Attribution.Author), MiscTexts.T(Effective.Attribution.LicenseText));

        // ---- Web 端（内嵌 WebView 与 LAN 分享页共用）----

        /// <summary>
        /// Web 端 @font-face 块（全部候选字体的 Regular + Bold；页面按需加载）。
        /// urlBase 由调用方按宿主给出："/fonts"（LAN HTTP 路由）、"/Fonts"（zxassets/bench 虚拟主机根）、
        /// ""（与字体同目录，如 app-font.css）。
        /// </summary>
        public static string WebFontFaceCss(string urlBase = "")
        {
            var prefix = string.IsNullOrEmpty(urlBase) ? "" : urlBase.TrimEnd('/') + "/";
            var sb = new StringBuilder();
            foreach (var (family, file, weight) in EnumerateFaceSources())
            {
                sb.Append($"@font-face{{font-family:'{CssSingleQuoted(family)}';src:url('{prefix}{CssSingleQuoted(file)}') format('{FormatOf(file)}');font-weight:{weight};font-display:swap}}");
            }
            return sb.ToString();
        }

        private static IEnumerable<(string Family, string File, int Weight)> EnumerateFaceSources()
        {
            foreach (var c in _catalog.Choices)
            {
                yield return (c.FamilyName, c.RegularFileName, 400);
                yield return (c.FamilyName, c.BoldFileName, 700);
            }
            var m = _catalog.Mono;
            yield return (m.FamilyName, m.RegularFileName, 400);
            yield return (m.FamilyName, m.BoldFileName, 700);
        }

        /// <summary>
        /// 应用自有 HTML/WebView 的文档创建脚本：把 --app-font 变量覆盖为生效字体栈
        /// （css 里已定义全部候选的 @font-face）。纯页面层，不触碰 XAML 资源字典。
        /// </summary>
        public static string WebFontOverrideScript()
        {
            var js = WebFontStack.Replace("\\", "\\\\").Replace("'", "\\'");
            return "(function(){function f(){try{document.documentElement.style.setProperty('--app-font','" + js + "');}catch(e){}}"
                 + "if(document.documentElement){f();}else{document.addEventListener('DOMContentLoaded',f);}})();";
        }

        /// <summary>CSS 单引号字符串内容转义（运行时拼 CSS 用；对应生成器 esc_css_single）。</summary>
        private static string CssSingleQuoted(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        /// <summary>
        /// 把配置推导的字体键写入指定资源字典。<b>仅限独立宿主/测试在资源字典尚未被 XAML 使用前调用。</b>
        /// 正式应用的键由 App.xaml 解析期的 <see cref="AppFontDictionary"/> 注入（持久化选择 → 下次启动生效）；
        /// 不要在应用运行中用它替换活动 Application.Resources 中的字体键——实测会触发 WinUI
        /// stowed exception 崩溃（0xC000027B，延迟约 16s；2026-09-23 归因验证，见 font-maintenance §9）。
        /// </summary>
        public static void ApplyToApplicationResources(ResourceDictionary? resources = null)
        {
            resources ??= Application.Current?.Resources;
            if (resources is null) return;
            var family = new FontFamily(XamlFontUri);
            resources["ContentControlThemeFontFamily"] = family;
            resources["AppFontFamily"] = family;
        }

        // ---- System.Drawing (GDI+)：按文件加载，私有字体集进程级缓存 ----

        private static Lazy<System.Drawing.Text.PrivateFontCollection?> _gdiFonts = new(CreateGdiFontCollection);

        private static System.Drawing.Text.PrivateFontCollection? CreateGdiFontCollection()
        {
            try
            {
                var pfc = new System.Drawing.Text.PrivateFontCollection();
                if (File.Exists(RegularFilePath)) pfc.AddFontFile(RegularFilePath);
                if (File.Exists(BoldFilePath)) pfc.AddFontFile(BoldFilePath);
                return pfc.Families.Length > 0 ? pfc : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 创建 GDI+ 字体：优先随包字体（与 WinUI 同一字体源），加载失败回退系统 Segoe UI。
        /// 调用方负责 Dispose 返回的 Font；内部 PrivateFontCollection 为进程级缓存、不需释放。
        /// </summary>
        public static System.Drawing.Font CreateGdiFont(float size, bool bold = false)
        {
            var pfc = _gdiFonts.Value;
            if (pfc is not null)
            {
                var style = bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular;
                var family = pfc.Families.FirstOrDefault(f =>
                    string.Equals(f.Name, FamilyName, StringComparison.OrdinalIgnoreCase)) ?? pfc.Families[0];
                try { return new System.Drawing.Font(family, size, style); }
                catch { }
            }
            return new System.Drawing.Font("Segoe UI", size, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular);
        }

        // ---- 导出报告（HTML 文件写出到磁盘，交给系统浏览器打开）----

        /// <summary>
        /// 把生效候选的字体文件（Regular + Bold）与许可复制到导出报告所在目录（已存在且大小一致则跳过），
        /// 让「报告 + 字体 + 许可」同目录交付（单独移动 HTML 时需连这些文件一起移动；
        /// 仅 HTML 移动时由 ExportFontFaceCss 的安装目录绝对路径兜底）。
        /// </summary>
        public static void EnsureExportFont(string targetDir)
        {
            try
            {
                if (string.IsNullOrEmpty(targetDir)) return;
                CopyIfNeeded(RegularFilePath, targetDir);
                CopyIfNeeded(BoldFilePath, targetDir);
                CopyIfNeeded(LicensePath, targetDir);
            }
            catch { /* 复制失败不阻断导出：HTML 内绝对路径仍可兜底 */ }
        }

        private static void CopyIfNeeded(string src, string targetDir)
        {
            if (!File.Exists(src)) return;
            var dest = Path.Combine(targetDir, Path.GetFileName(src));
            var srcInfo = new FileInfo(src);
            var dstInfo = new FileInfo(dest);
            if (!dstInfo.Exists || dstInfo.Length != srcInfo.Length)
                File.Copy(src, dest, true);
        }

        /// <summary>
        /// 导出报告用 @font-face（生效候选的 Regular + Bold）：相对路径（报告同目录字体）优先，
        /// 应用安装目录绝对路径兜底。实测（2026-09-23，Chrome 无头 + file://）：相对与绝对两种来源均可加载。
        /// </summary>
        public static string ExportFontFaceCss()
        {
            string Face(string fileName, int weight)
            {
                var abs = new Uri(Path.Combine(FontsDir, fileName)).AbsoluteUri;
                return $"@font-face{{font-family:'{CssSingleQuoted(FamilyName)}';src:url('{CssSingleQuoted(fileName)}') format('{FormatOf(fileName)}'),url('{abs}') format('{FormatOf(fileName)}');font-weight:{weight};font-display:swap}}";
            }
            return Face(RegularFileName, 400) + Face(BoldFileName, 700);
        }

        /// <summary>测试/独立宿主：清空进程级缓存（GDI 字体集），使换配置后重新加载。</summary>
        internal static void ResetCachesForTest()
        {
            _gdiFonts = new(CreateGdiFontCollection);
            LastResourceInjectError = null;
        }
    }
}
