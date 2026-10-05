using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

/// <summary>ZXAI：AI 工具分类页的卡片数据（x:Bind 数据源）。</summary>
/// <param name="Replaced">合规清退后的替换标注：本产品替代的原软件名（null = 无替换关系）。</param>
public sealed record AiToolCard(string Name, string Desc, string Glyph, string? Replaced = null)
{
    /// <summary>说明的显示层翻译（原始 Desc 仍为业务数据：搜索/收藏/去重均用原文）。</summary>
    public string DescDisplay => AiCardTexts.Desc(Desc);

    /// <summary>点击卡片时交给 AI 助手的预填消息。</summary>
    public string Prompt => string.Format(LocalizationService.L("AiCategory_InstallPrompt", "帮我安装 {0}（先问我问题，问清楚再带我装）"), Name);

    /// <summary>ZXAI：收藏星标文案（☆/★，随收藏状态刷新）。</summary>
    public string FavText { get; set; } = "☆";

    /// <summary>替换标注文案（仅 Replaced 非空时显示）。</summary>
    public string ReplacedText => Replaced is null ? "" : string.Format(LocalizationService.L("AiCategory_Replaced", "✓ 合规替换：原 {0}"), Replaced);

    public string FavoriteToolTip => LocalizationService.L("AiCategory_FavoriteTip", "收藏到「收藏」目录");
    public string AiInstallText => LocalizationService.L("AiCategory_AiInstall", "AI 安装");
    public string AiInstallToolTip => LocalizationService.L("AiCategory_AiInstallTip", "打开 AI 助手，手把手带我装（需先配置 API）");
    public string QuickInstallText => LocalizationService.L("AiCategory_QuickInstall", "⬇ 一键安装");
    public string QuickInstallToolTip => LocalizationService.L("AiCategory_QuickInstallTip", "本应用直接用 winget 安装（不需要 AI）");
    public string OpenOfficialText => LocalizationService.L("AiCategory_OpenOfficial", "点击卡片打开官网");

    /// <summary>替换标注可见性。</summary>
    public Visibility ReplacedVisibility => Replaced is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>🆕 官网地址（查表；未收录自动回退必应搜索「官网下载」）。</summary>
    public string OfficialUrl => AiToolLinks.OfficialUrl(Name);

    /// <summary>🆕 一键安装可见性（本应用 winget 直装，不依赖 AI/API）；纯网页工具藏掉（用户口径 2026-09-22）。</summary>
    public Visibility QuickInstallVisibility => AiToolLinks.HasQuickInstall(Name) && !AiToolLinks.IsWebOnly(Name) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ZXAI 2026-09-22：厂商图标（Assets/AiIcons 抓取图；无则回退 Glyph）。</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? IconSource => Services.AiIconService.GetIcon(Name);

    public Visibility IconVisibility => IconSource is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVisibility => IconSource is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ZXAI 2026-09-22（用户口径）：纯网页工具（如 DeepSeek）不显示「AI 安装」。</summary>
    public Visibility AiInstallVisibility => AiToolLinks.CanAiInstall(Name) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// ZXAI：AI 工具分类页（左导航「AI 工具 / 效率与创作」组的子分类共用）。
/// 卡片 = 安装引导入口：点击 → 打开主页 AI 助手并预填「帮我安装 xxx」，
/// 由 AI Agent 工作流技能接管（问诊 → 推荐 → 安装 → 教学）。
/// </summary>
public sealed partial class AiCategoryPage : Page
{
    private string _categoryKey = "agent";
    private string? _filterTerm;   // 搜索聚焦：搜索词（如 "codex"）
    private string? _focusName;    // 搜索聚焦：被点的那张主卡（如 "CC Switch"）

    public AiCategoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            Apply();
        };
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(Apply);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 【关键修复·2026-09-22】页面 NavigationCacheMode=Enabled，实例会被 Frame 缓存复用：
        // 每次导航进来必须先重置聚焦状态，否则上次的过滤词残留会把新分类的卡片全部滤掉（页面空白）。
        _filterTerm = null;
        _focusName = null;
        if (e.Parameter is string key && !string.IsNullOrWhiteSpace(key))
        {
            // 支持两种格式：
            //   "agent"                     —— 左侧导航直达 → 显示全部分类卡片
            //   "agent|codex|CC Switch"     —— 从搜索结果进入 → 只显示与搜索词相关的卡片（主卡第一）
            var parts = key.Split('|');
            _categoryKey = parts[0];
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) _filterTerm = parts[1].Trim();
            if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])) _focusName = parts[2].Trim();
        }
        Apply();
    }

    private void Apply()
    {
        var (title, desc, items) = Catalog(_categoryKey);
        TitleText.Text = LocalizationService.L($"AiCategory_{_categoryKey}_Title", title);
        DescText.Text = LocalizationService.L($"AiCategory_{_categoryKey}_Description", desc);
        CategoryHintText.Text = LocalizationService.L("AiCategory_Hint", "提示：每张卡都能「⬇ 一键安装」（本应用直接装，不需要 AI）；想自己来就点卡片打开官网下载页；点「AI 安装」交给主页 AI 助手手把手带你装好、教会你用（需先配置 API）。");

        foreach (var c in items) c.FavText = UsageStats.IsAiFavorite(c.Name) ? "★" : "☆";

        // 【搜索聚焦·2026-09-22】从全局搜索点进来时只展示相关的卡：
        // 主卡（被点的那张）排第一，随后是搜索词命中的模糊相关卡；无关卡片一律不显示。
        // 【跨分类关联·2026-09-22】关联不局限当前分类——搜 "claude" 时 Claude Code（AI agent 分类）
        // 之类同生态/同主题的工具也要带出来（用户口径：所有软件之间的关联性都做一下）。
        if (!string.IsNullOrWhiteSpace(_filterTerm))
        {
            var term = _filterTerm;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scored = new List<(AiToolCard Card, bool Focus, int Score)>();

            // ① 当前分类：主卡 + 模糊命中
            foreach (var c in items)
            {
                var focus = _focusName is not null && string.Equals(c.Name, _focusName, StringComparison.OrdinalIgnoreCase);
                var s = MatchScore(term, c);
                if (focus || s > 0) { scored.Add((c, focus, s)); seen.Add(c.Name); }
            }

            // ② 其他分类：跨分类关联卡（同名去重）——搜 "claude" 时带出 Claude Code 等同生态工具
            foreach (var key in AllCategoryKeys)
            {
                if (key == _categoryKey) continue;
                var (_, _, otherItems) = Catalog(key);
                foreach (var c in otherItems)
                {
                    if (seen.Contains(c.Name)) continue;
                    var s = MatchScore(term, c);
                    if (s > 0) { scored.Add((c, false, s)); seen.Add(c.Name); }
                }
            }

            // 统一排序：主卡第一 → 其余按匹配分降序（Claude 100 → Claude Code 60 → Poe 20）
            var focused = scored
                .OrderByDescending(x => x.Focus)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Card.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Card)
                .ToList();
            foreach (var c in focused) c.FavText = UsageStats.IsAiFavorite(c.Name) ? "★" : "☆";

            CardGrid.ItemsSource = null;
            CardGrid.ItemsSource = focused;
            return;
        }

        ApplySortAndFav(items);
    }

    /// <summary>搜索聚焦算分（与全局搜索同规则）：名称相等 100 / 前缀 80 / 包含 60 / 描述包含 20 / 关联词包含 20。
    /// 【多词·2026-09-22】查询按空格拆词（如 "codex 切换"），每词都要命中（AND），分数求和。</summary>
    private static int MatchScore(string term, AiToolCard c)
    {
        var words = term.Split([' ', '\u3000'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return 0;
        var total = 0;
        foreach (var w in words)
        {
            var s = MatchScoreSingle(w, c);
            if (s <= 0) return 0;
            total += s;
        }
        // 【frecency·2026-09-22】常用工具优先（一次计权，不随词数叠加）
        total += Math.Min(UsageStats.GetClicks("ai:" + c.Name), 10) * 3;
        return total;
    }

    private static int MatchScoreSingle(string term, AiToolCard c)
    {
        int s = 0;
        if (string.Equals(c.Name, term, StringComparison.CurrentCultureIgnoreCase)) s += 100;
        else if (c.Name.StartsWith(term, StringComparison.CurrentCultureIgnoreCase)) s += 80;
        else if (c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)) s += 60;
        else s += (int)UnifiedSearchService.SubsequenceScore(term, c.Name);   // 【fzf 式】子序列模糊（ccs → CC Switch）
        if (!string.IsNullOrEmpty(c.Desc) && c.Desc.Contains(term, StringComparison.CurrentCultureIgnoreCase)) s += 20;
        foreach (var r in RelatedTermsOf(c.Name))
        {
            if (r.Contains(term, StringComparison.CurrentCultureIgnoreCase)) { s += 20; break; }
        }
        return s;
    }

    /// <summary>
    /// 【跨软件关联·2026-09-22】关联关键词表：补足"描述里没写到"的品牌 / 生态 / 配套关键词，
    /// 让搜索按"关系"带出工具（用户口径：所有软件之间的关联性都要做）。
    /// 例：搜 openai 带出 ChatGPT；搜 ollama 带出 LM Studio；搜 claude 带出 Claude Code + CC Switch。
    /// </summary>
    public static string[] RelatedTermsOf(string cardName) => cardName switch
    {
        // —— OpenAI 系（ChatGPT / Codex / GPT）——
        "ChatGPT" or "ChatGPT 桌面版" => ["openai", "gpt", "codex", "sora", "cc switch"],
        "Codex" => ["openai", "chatgpt", "cc switch", "codex cli", "vs code", "visual studio code", "node.js", "git", "编程"],
        "GPT Image" => ["openai", "chatgpt", "生图", "gpt"],
        "OpenAI API" => ["openai", "chatgpt", "codex", "gpt", "cc switch"],
        // —— Anthropic 系（Claude / Claude Code / CC Switch）——
        "Claude" => ["anthropic", "claude code", "cc switch"],
        "Claude Code" => ["anthropic", "claude", "cc switch", "codex"],
        "Anthropic Claude API" => ["anthropic", "claude", "claude code"],
        "CC Switch" => ["claude code", "codex", "claude", "openai"],
        // —— Google 系（Gemini / NotebookLM）——
        "Google Gemini" or "Google Gemini API" => ["google", "谷歌", "gemini cli", "notebooklm"],
        "Gemini CLI" => ["google", "谷歌", "gemini"],
        "NotebookLM" => ["google", "谷歌", "gemini"],
        "Nano Banana" => ["google", "谷歌", "gemini", "生图"],
        "Veo" => ["google", "谷歌", "视频"],
        "Jules" => ["google", "谷歌"],
        // —— 深度求索（DeepSeek / dsh）——
        "DeepSeek" or "DeepSeek 开放平台" => ["deepseek", "dsh", "deepseek harness"],
        "DeepSeek Harness" => ["deepseek", "dsh", "深度求索"],
        // —— 字节系 ——
        "豆包" or "豆包 MarsCode" or "豆包大模型（火山引擎）" => ["字节", "doubao", "volcengine", "火山引擎"],
        "扣子 Coze" => ["字节", "coze", "bot"],
        // 注：Trae / Seedance / 海绵音乐 的关联词在下方对应生态节统一定义（避免 switch 重复 arm）
        // —— 阿里系（千问 / 通义）——
        "千问" or "千问（Qwen）" or "Qwen Code" => ["阿里", "qwen", "通义"],
        "通义灵码" => ["阿里", "qwen", "编程"],
        "Qoder" => ["阿里", "qoder", "ai ide"],
        "通义万相" => ["阿里", "通义", "生图"],
        // —— 腾讯系 ——
        "腾讯元宝" or "腾讯混元" or "腾讯 CodeBuddy" or "腾讯智影" => ["腾讯", "tencent", "混元"],
        // —— 百度系 ——
        "文心一言" or "文心一格" or "百度千帆（文心）" => ["百度", "文心", "ernie", "千帆"],
        // —— 月之暗面 ——
        "Kimi" or "Kimi 开放平台" or "Kimi 智能助手" => ["月之暗面", "moonshot", "kimi"],
        // —— 智谱系 ——
        "智谱清言" or "智谱清影" or "智谱 GLM" => ["智谱", "glm", "zhipu"],
        // —— 讯飞系 ——
        "讯飞星火" or "讯飞星火 App" or "讯飞听见" or "讯飞智作" => ["科大讯飞", "iflytek", "讯飞"],
        // —— MiniMax 系 ——
        "海螺 AI" or "海螺 AI 语音" or "海螺视频" or "MiniMax 海螺" => ["minimax", "海螺"],
        // —— xAI ——
        "Grok" or "xAI Grok" => ["xai", "马斯克", "grok"],
        // —— 本地部署生态（组内互相关联）——
        "Ollama" => ["lm studio", "jan", "open webui", "anythingllm", "vllm", "本地部署", "本地模型", "开源模型"],
        "LM Studio" => ["ollama", "jan", "open webui", "anythingllm", "vllm", "本地部署", "本地模型"],
        "Jan" => ["ollama", "lm studio", "open webui", "anythingllm", "vllm", "本地部署"],
        "vLLM" => ["ollama", "lm studio", "jan", "open webui", "anythingllm", "本地部署"],
        "Open WebUI" => ["ollama", "lm studio", "jan", "anythingllm", "vllm", "本地部署"],
        "AnythingLLM" => ["ollama", "lm studio", "jan", "open webui", "vllm", "本地部署"],
        // —— AI 绘画生态（组内互相关联）——
        "ComfyUI" => ["stable diffusion", "flux", "liblibai", "吐司", "midjourney", "ai 绘画", "生图"],
        "Stable Diffusion WebUI" => ["comfyui", "flux", "liblibai", "吐司", "ai 绘画", "生图"],
        "Flux" => ["comfyui", "stable diffusion", "liblibai", "吐司", "ai 绘画", "生图"],
        "Midjourney" => ["comfyui", "stable diffusion", "flux", "ai 绘画", "生图"],
        "LiblibAI" => ["comfyui", "stable diffusion", "flux", "吐司", "ai 绘画"],
        "吐司 TusiArt" => ["comfyui", "stable diffusion", "flux", "liblibai", "ai 绘画"],
        // —— AI 音乐生态（组内互相关联）——
        "Suno" => ["udio", "海绵音乐", "天工 skymusic", "riffsusion", "ace studio", "ai 音乐"],
        "Udio" => ["suno", "海绵音乐", "天工 skymusic", "ai 音乐"],
        "海绵音乐" => ["suno", "udio", "天工 skymusic", "ai 音乐", "字节"],
        "天工 SkyMusic" => ["suno", "udio", "海绵音乐", "ai 音乐"],
        "Riffusion" => ["suno", "udio", "ai 音乐"],
        "ACE Studio" => ["suno", "ai 音乐", "歌声合成"],
        // —— AI 视频生态（组内互相关联）——
        "可灵 AI" => ["即梦", "pika", "runway", "vidu", "luma", "seedance", "ai 视频"],
        "即梦 AI" => ["可灵", "seedance", "pika", "runway", "ai 视频", "字节"],
        "Pika" => ["可灵", "即梦", "runway", "luma", "ai 视频"],
        "Runway" => ["可灵", "即梦", "pika", "luma", "ai 视频"],
        "Vidu" => ["可灵", "即梦", "pika", "ai 视频"],
        "Luma Dream Machine" => ["可灵", "即梦", "pika", "runway", "ai 视频"],
        "Seedance" => ["可灵", "即梦", "ai 视频", "字节"],
        // —— AI 编程编辑器 / IDE 生态（互相关联）——
        "Cursor" => ["windsurf", "trae", "zed", "vs code", "ai 编辑器", "ai ide", "编程"],
        "Windsurf" => ["cursor", "trae", "zed", "vs code", "ai 编辑器", "ai ide", "编程"],
        "Trae" => ["cursor", "windsurf", "zed", "字节", "ai 编辑器", "ai ide", "编程"],
        "Zed" => ["cursor", "windsurf", "trae", "ai 编辑器", "编程"],
        "Kiro" => ["cursor", "windsurf", "aws", "ai ide", "编程"],
        "Visual Studio Code" or "GitHub Copilot" or "Cline" or "Roo Code" or "Kilo Code" or "Continue"
            => ["编程", "vscode", "编辑器", "codex", "claude code"],
        // —— 命令行编程 agent 生态 ——
        "Aider" or "OpenCode" or "Goose" or "OpenClaw" or "iFlow CLI" or "Droid" or "Amp"
            => ["cli", "命令行", "编程 agent"],
        // —— 笔记 / 知识库生态 ——
        "Obsidian" or "Notion" or "语雀" or "Joplin" or "Typora" or "MarkText" or "Zettlr"
            => ["笔记", "知识库", "写作"],
        // —— 游戏引擎生态 ——
        "Godot" or "Unity" or "Unreal Engine" or "Cocos Creator" or "GameMaker" or "Defold" or "GDevelop"
            => ["游戏引擎", "做游戏", "游戏开发"],
        // —— 像素画生态 ——
        "Aseprite" or "Pixelorama" or "LibreSprite" or "Piskel" => ["像素画", "像素", "美术"],
        // —— 3D 建模生态 ——
        "Blender" or "MagicaVoxel" or "Blockbench" or "ZBrushCoreMini" or "MeshLab" => ["3d", "建模", "模型制作"],
        // —— 视频剪辑生态 ——
        "剪映" or "CapCut" or "必剪" or "DaVinci Resolve" or "Premiere Pro" or "Kdenlive" or "Shotcut"
            => ["剪辑", "视频编辑", "后期"],
        // —— 压缩工具 ——
        "7-Zip" or "Bandizip" or "NanaZip" => ["压缩", "解压"],
        // —— API 调试 ——
        "Apifox" or "Postman" or "Bruno" => ["api", "接口调试"],
        // —— DAW / 音频工作站 ——
        "FL Studio" or "Cubase" or "Studio One" or "Reaper" or "LMMS" or "Cakewalk" => ["daw", "编曲", "音乐制作"],
        _ => []
    };

    /// <summary>ZXAI 2026-09-20：星标初始化 + 按点击次数降序展示（常用的自然靠前）。</summary>
    private void ApplySortAndFav(List<AiToolCard> items)
    {
        foreach (var c in items) c.FavText = UsageStats.IsAiFavorite(c.Name) ? "★" : "☆";
        CardGrid.ItemsSource = null;
        CardGrid.ItemsSource = UsageStats.OrderByUsage(items, c => "ai:" + c.Name);
    }

    private static readonly string[] AllCategoryKeys =
        ["assistant", "agent", "local", "models", "dev", "game", "office", "create", "image", "video", "audio", "writing", "translate"];

    /// <summary>ZXAI：全部 AI 工具卡（全局搜索索引；同名卡去重，保留首个分类）。</summary>
    public static IEnumerable<(string Name, string Desc, string Glyph, string CategoryKey, string CategoryTitle)> AllToolCards()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AllCategoryKeys)
        {
            var (title, _, items) = Catalog(key);
            var shortTitle = title.Split('·')[0].Trim();
            foreach (var card in items)
            {
                if (!seen.Add(card.Name)) continue;   // 同一工具出现在多个分类时不重复
                yield return (card.Name, card.Desc, card.Glyph, key, shortTitle);
            }
        }
    }

    /// <summary>ZXAI：按卡名在所有频道中查找（收藏目录用）。</summary>
    public static AiToolCard? FindCardByName(string name)
    {
        foreach (var key in AllCategoryKeys)
        {
            var (_, _, items) = Catalog(key);
            var hit = items.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    private static (string Title, string Desc, List<AiToolCard> Items) Catalog(string key) => key switch
    {
        "assistant" => ("聊天助手 · 对话 / 搜索 / 客户端",
            "你问它答型的 AI 伙伴：日常问问题、查资料、写东西、做研究——网页版即开即用，客户端装上桌面随时呼出。（想让它动手干活写代码？看「AI agent」频道）",
            new List<AiToolCard>
            {
                new("ChatGPT", "全球第一大 AI 助手：写作 / 编程 / 分析全能（需网络环境，有免费档）。", "\uE99A"),
                new("Claude", "Anthropic 的 AI 助手：长文写作 / 代码能力强，界面舒服（需网络环境）。", "\uE99A"),
                new("Google Gemini", "谷歌 AI 助手：多模态全能，和谷歌全家桶联动（需网络环境）。", "\uE774"),
                new("Grok", "xAI 的 AI 助手：实时信息强，X 平台集成（需网络环境）。", "\uE774"),
                new("DeepSeek", "深度求索：国产之光，免费网页版 + 便宜 API，本助手同款大脑。", "\uE99A"),
                new("豆包", "字节 AI 助手：免费好用，语音 / 图像 / 写作全能娃娃脸。", "\uE99A"),
                new("Kimi", "月之暗面：超长文档分析见长，学生党研究党利器。", "\uE99A"),
                new("千问", "阿里 AI 助手（原通义）：办公 / 学习全场景，PPT 大纲一绝。", "\uE99A"),
                new("文心一言", "百度 AI 助手：中文理解强，生态插件多。", "\uE99A"),
                new("腾讯元宝", "腾讯 AI 助手：微信生态联动，公众号写作顺。", "\uE99A"),
                new("智谱清言", "智谱 AI 助手：免费、编程与写作均衡。", "\uE99A"),
                new("讯飞星火", "科大讯飞：语音交互强，会议记录 / 口语练习。", "\uE99A"),
                new("海螺 AI", "MiniMax：长文 / 角色对话有特色，语音自然。", "\uE99A"),
                new("Perplexity", "AI 搜索标杆：答案带引用来源，查资料首选（需网络环境）。", "\uE721"),
                new("秘塔 AI 搜索", "国产 AI 搜索：无广告、直接给结构化作答，研究党神器。", "\uE721"),
                new("纳米 AI 搜索", "360 出品：多模型聚合搜索，中文场景全。", "\uE721"),
                new("天工 AI", "昆仑万维：搜索 + 创作一体，音乐生成有特色。", "\uE721"),
                new("NotebookLM", "谷歌笔记研究神器：喂文档自动生成总结 / 播客（需网络环境）。", "\uE70F"),
                new("Cherry Studio", "开源多模型桌面客户端（51k star）：一个应用接所有大模型 + 知识库，国内直连。", "\uE8F1"),
                new("ChatBox", "开源 AI 桌面客户端：接任意模型，简洁好用。", "\uE8F1"),
                new("LobeChat", "开源聊天框架：可自部署的 ChatGPT 平替，插件生态。", "\uE8F1"),
                new("Monica", "浏览器 AI 插件：网页随处呼出，划词 / 总结 / 写作。", "\uE895"),
                new("Sider", "浏览器 AI 侧边栏：多模型随叫随到，阅读助手。", "\uE895"),
                new("Poe", "Quora 出品模型聚合：一个订阅用 GPT / Claude / Gemini 全家桶（需网络环境）。", "\uE8F1"),
            }),
        "dev" => ("软件开发 · 配合 AI 的工具",
            "用 AI 帮你写代码、做网站、做小工具——下面是各类项目最常用的基础装备，AI 助手会带你一步步装好。",
            new List<AiToolCard>
            {
                new("Visual Studio Code", "最流行的免费代码编辑器：AI 改完的代码在这里查看、运行、回退。", "\uE70F"),
                new("Node.js", "做网站 / 网页小工具的运行环境（不少 AI 开发工具也依赖它）。", "\uE943"),
                new("Git", "代码的「存档与回退」工具，做项目必装；出问题能回到上一个好版本。", "\uE8C8"),
                new("Python", "最易上手的编程语言：写小工具、办公自动化首选。", "\uE756"),
                new("GitHub Desktop", "图形化 Git：点按钮就能存档/回退代码，不想敲命令行就用它。", "\uE8C8"),
                new("Windows Terminal", "现代终端工具：多标签、好看好用，跑命令更顺手。", "\uE756"),
                new("Apifox", "国产 API 调试神器：接口测试 / 文档 / Mock 一体，前后端联调必备。", "\uE943"),
                new("Postman", "全球流行的 API 调试工具：接口开发测试的标准装备。", "\uE943"),
                new("DBeaver", "开源数据库客户端：MySQL / PostgreSQL / SQLite 都能连，免费。", "\uE8F1"),
                new("Docker Desktop", "容器工具：一键跑起别人的开发环境，进阶项目再学。", "\uE7C4"),
                new("HBuilderX", "uni-app 官方 IDE：一套代码做小程序 + App + H5。", "\uE7FC"),
                new("PyCharm 社区版", "Python 专业 IDE（社区版免费）：项目大了比 VS Code 更顺手。", "\uE756"),
                new("IntelliJ IDEA 社区版", "Java 最强 IDE（社区版免费）：写 Java 就用它。", "\uE713"),
                new("Visual Studio 社区版", "微软 IDE（免费）：做 C# / C++ / 桌面应用选它。", "\uE70F"),
                new("WSL2", "Windows 里的 Linux 子系统：学 Linux、跑服务器程序不用装双系统。", "\uE756"),
                new("VMware Workstation", "虚拟机（个人免费）：一台电脑跑多个系统，测试折腾必备。", "\uE7C4"),
                new("Wireshark", "开源抓包分析：看网络请求到底发了什么，排障神器。", "\uE701"),
                new("FileZilla", "开源 FTP 工具：上传网站文件到服务器用。", "\uE8DE"),
                new("MobaXterm", "全能终端：SSH / SFTP / 会话管理一站搞定。", "\uE756"),
                new("HeidiSQL", "轻量数据库工具：连 MySQL 查数据，比装大软件省事。", "\uE8F1"),
                new("Sublime Text", "经典轻量编辑器：启动飞快、插件多，写代码看代码都舒服（可无限试用）。", "\uE70F"),
                new("Notepad++", "老牌文本编辑器：看日志 / 改配置顺手，免费。", "\uE70F"),
                new("Notepad--", "国产开源编辑器：Notepad++ 的国产替代，中文生态。", "\uE70F"),
                new("Neovim", "命令行编辑器天花板：键盘流极客最爱（新手慎入）。", "\uE756"),
                new("WinMerge", "开源文件对比：两份代码的差异一目了然。", "\uE8C8"),
                new("Fork", "口碑 Git 图形客户端：界面清爽，程序员爱用（可免费试用）。", "\uE8C8"),
                new("Sourcetree", "免费 Git 图形工具：Atlassian 出品，功能全。", "\uE8C8"),
                new("JetBrains Rider", "C# / Unity 开发 IDE：写游戏脚本利器（有免费档）。", "\uE713"),
                new("CLion", "C/C++ 专业 IDE：单片机 / 算法开发用（收费，有免费档）。", "\uE713"),
                new("Bruno", "开源 API 调试：Postman 的免费离线替代，数据存本地。", "\uE943"),
            }),
        "game" => ("游戏开发 · 配合 AI 的工具",
            "用 AI 做游戏的主力装备——引擎管画面与物理，AI 帮你写脚本和逻辑，你负责出主意。",
            new List<AiToolCard>
            {
                new("Godot", "免费开源的游戏引擎（2D/3D 都能做，轻量、上手快，最适合做第一个游戏）。", "\uE7FC"),
                new("Blender", "免费开源的三维建模 / 动画工具，做 3D 游戏素材用。", "\uE7C4"),
                new("Unity", "商业引擎里生态最大（教程多），适合想走职业路线的人。", "\uE713"),
                new("Unreal Engine", "虚幻引擎：3A 级写实画面（配置要求高，建议有基础后再上）。", "\uE7FC"),
                new("Cocos Creator", "国产手游引擎：做小游戏 / 手游上微信、抖音。", "\uE8F1"),
                new("RPG Maker", "RPG 制作大师：不做程序也能做出剧情 RPG，经典入门利器。", "\uE713"),
                new("GameMaker", "经典 2D 引擎：《传说之下》等名作用的工具，2D 开发很顺。", "\uE7FC"),
                new("Construct 3", "无代码游戏制作：拖拽积木做游戏，浏览器里就能用。", "\uE790"),
                new("Aseprite", "像素画绘制工具，做复古 / 像素风游戏美术用（收费小软件）。", "\uE790"),
                new("Pixelorama", "开源像素画编辑器：Aseprite 的免费替代，做像素游戏素材。", "\uE790"),
                new("LibreSprite", "开源像素动画工具：画像素角色 + 做帧动画。", "\uE790"),
                new("Piskel", "在线像素画工具：打开浏览器就能画，零安装。", "\uE790"),
                new("Tiled", "开源地图编辑器：给 2D 游戏画关卡地图（行业通用格式）。", "\uE8F1"),
                new("Spine", "2D 骨骼动画工具：让角色动起来顺滑的职业级工具。", "\uE790"),
                new("MagicaVoxel", "免费体素建模：做 Minecraft 风格的方块模型。", "\uE7C4"),
                new("Blockbench", "免费开源方块建模：Minecraft 模组/皮肤/模型制作标准工具。", "\uE7C4"),
                new("Material Maker", "开源材质制作：给 3D 模型做纹理材质，程序化生成。", "\uE790"),
                new("Mixamo", "免费在线角色动画库：上传模型自动绑骨骼配动作。", "\uE7C4"),
                new("GDevelop", "开源无代码引擎：事件表做游戏，不用写代码就能出成品。", "\uE7FC"),
                new("Defold", "免费 2D 引擎：轻量专业，小团队口碑好。", "\uE7FC"),
                new("Ren'Py", "视觉小说引擎：做恋爱 / 文字冒险游戏的全球标准（免费开源）。", "\uE7FC"),
                new("Twine", "文字冒险制作：不用编程做互动小说，可导出网页。", "\uE7FC"),
                new("Adventure Game Studio", "免费引擎：做点击式冒险游戏（经典猴岛式）。", "\uE7FC"),
                new("Stencyl", "拖拽式游戏制作：像搭积木一样做 2D 游戏。", "\uE7FC"),
                new("Solar2D", "免费 2D 引擎：Lua 开发、上手简单，发布手机方便。", "\uE7FC"),
                new("ZBrushCoreMini", "免费雕刻建模：像捏黏土一样做角色模型。", "\uE7C4"),
                new("MeshLab", "开源 3D 处理：模型清理 / 转换 / 修复。", "\uE7C4"),
                new("Yarn Spinner", "对话叙事系统：给游戏角色写对白和剧情树（Unity/Godot 可用）。", "\uE8BD"),
            }),
        "local" => ("AI 本地部署 · 在自己电脑上跑模型",
            "完全免费、数据不出本机：本地推理运行时与一键管理工具——装上就能离线跑开源模型（吃显卡，助手会按你的配置推荐合适的体量）。想要云端大脑看「AI 模型」频道。",
            new List<AiToolCard>
            {
                new("Ollama", "在自己电脑上跑大模型：命令行一键拉取运行，生态最广。", "\uE8F1"),
                new("LM Studio", "图形界面的本地模型工具：下载即用，适合尝鲜。", "\uE8F1"),
                new("Strata", "本地 Qwen 聊天、推理服务与监控；建议 12GB 显存、32GB 内存，首次下载约 70GB 模型。", "\uE8F1"),
                new("vLLM", "高性能本地部署框架：把开源模型跑成服务给人调用（进阶）。", "\uE8F1"),
                new("Jan", "开源本地客户端：下载即用跑本地模型，隐私优先。", "\uE8F1"),
                new("Open WebUI", "自建 ChatGPT 界面：本地跑模型给它接上，数据全在自己手里。", "\uE8F1"),
                new("AnythingLLM", "开源本地知识库：把文档喂进去，本地问答不联网。", "\uE8F1"),
            }),
        "models" => ("AI 模型 · 工具们的『大脑』",
            "自己申请模型 API（推荐 DeepSeek：便宜、国内直连）后，AI 助手会帮你把它接进 Claude Code / Codex 等各种工具里。",
            new List<AiToolCard>
            {
                new("DeepSeek 开放平台", "提供 API 接入；可在枕星 AI 助手中按需配置，费用和可用性以官方页面为准。", "\uE99A"),
                new("智谱 GLM", "国产大模型：有面向编程的套餐，国内直连。", "\uE774"),
                new("Kimi 开放平台", "月之暗面出品：超长文本处理见长，有 API。", "\uE774"),
                new("千问（Qwen）", "阿里大模型 API（原通义千问）：模型全、生态广、中文强。", "\uE774"),
                new("百度千帆（文心）", "文心大模型 API：国内老牌、企业生态全，中文能力强。", "\uE774"),
                new("讯飞星火", "科大讯飞：中文语音+大模型组合，做语音应用很合适。", "\uE774"),
                new("腾讯混元", "腾讯大模型 API：社交/内容生态广，价格友好。", "\uE774"),
                new("MiniMax 海螺", "语音合成 / 视频生成能力突出，做多模态应用首选。", "\uE774"),
                new("零一万物", "Yi 系列模型：开源模型口碑好，有 API。", "\uE774"),
                new("百川智能", "Baichuan 系列：中文 / 行业优化，有 API。", "\uE774"),
                new("阶跃星辰", "Step 系列：多模态（图/音/视频理解）能力突出。", "\uE774"),
                new("面壁智能", "MiniCPM 系列：端侧小模型，手机 / 低配设备也能跑。", "\uE774"),
                new("硅基流动 SiliconFlow", "模型聚合平台：一个 Key 用几十个开源模型，超便宜国内直连。", "\uE8F1"),
                new("OpenRouter", "全球模型聚合：一个 Key 自由切换 GPT / Claude / Gemini（需网络环境）。", "\uE8F1"),
                new("OpenAI API", "GPT 系列官方 API：能力天花板之一（需网络环境，按量付费）。", "\uE943"),
                new("Anthropic Claude API", "Claude 系列官方 API：编程 / 写作能力强（需网络环境）。", "\uE99A"),
                new("Google Gemini API", "谷歌 Gemini：多模态强，有免费额度（需网络环境）。", "\uE774"),
                new("书生·浦语 InternLM", "上海 AI Lab 开源模型体系：学术口碑好，有 API。", "\uE774"),
                new("天工大模型", "昆仑万维出品：音乐生成 / 搜索多模态玩法多。", "\uE774"),
                new("商汤日日新", "商汤大模型：视觉能力突出，企业方案全。", "\uE774"),
                new("豆包大模型（火山引擎）", "字节官方 API：性价比高、国内直连稳。", "\uE774"),
                new("Mistral", "欧洲最强开源系：模型小巧高效，有 API（需网络环境）。", "\uE774"),
                new("xAI Grok", "马斯克系大模型：实时信息能力强（需网络环境）。", "\uE774"),
                new("Groq", "超快推理平台：开源模型极速运行，免费额度大方（需网络环境）。", "\uE8F1"),
                new("Hugging Face", "全球开源模型社区：找模型 / 在线试玩 / Inference API。", "\uE8F1"),
                new("Jev（TypeSafe）", "2026 新物种：不做聊天，直接输出类型化概率决策——快百倍、便宜百倍，本周最热。", "\uE99A"),
                new("LM Evaluation Harness", "EleutherAI 开源评测标杆：60+ 学术基准，Hugging Face 榜单官方后端。", "\uE9D9"),
                new("OpenCompass 司南", "上海 AI Lab 开源：中文横评事实标准，100+ 数据集。", "\uE8F1"),
                new("EvalScope", "魔搭（ModelScope）出品：模型评测 + 推理性能压测一站式。", "\uE8F1"),
                new("FlagEval", "智源研究院开源：大模型 / 多模态评测平台。", "\uE8F1"),
                new("HELM", "斯坦福开源：准确性 / 鲁棒性 / 公平性全方位评测。", "\uE9D9"),
                new("Inspect", "英国 AI 安全研究院（AISI）：开源安全评测框架。", "\uE9D9"),
                new("DeepEval", "开源 LLM 评测框架：pytest 风格写测试，接 CI 一条龙。", "\uE9D9"),
                new("Promptfoo", "开源提示词评测 CLI：多模型 / 多提示词对比，声明式配置。", "\uE9D9"),
                new("RAGAS", "开源 RAG 评测：检索增强生成质量自动化打分。", "\uE9D9"),
                new("Langfuse", "开源 LLM 可观测 + 评测：追踪 / 数据集 / 打分一体。", "\uE9D9"),
                new("Arize Phoenix", "Arize 开源：LLM 应用可观测与评测，本地可跑。", "\uE9D9"),
                new("Braintrust", "商用评测平台：评测驱动开发，可视化对比模型表现。", "\uE9D9"),
                new("W&B Weave", "Weights & Biases 出品：LLM 追踪与评测工具。", "\uE9D9"),
                new("LMArena（Chatbot Arena）", "人类盲测榜单：模型真实偏好排名，选型参考第一站。", "\uE9D9"),
                new("SWE-bench", "代码 agent 评测标杆：真实 GitHub issue 修复率排行榜。", "\uE9D9"),
                new("SuperCLUE", "中文大模型综合评测榜：月度更新。", "\uE9D9"),
            }),
        "office" => ("办公效率 · 让打工人早点下班",
            "写文档、做表格、搜文件、看视频——下面这些免费/开源工具，很多比收费软件还好用，AI 助手带你装好配好。",
            new List<AiToolCard>
            {
                new("LibreOffice", "免费开源办公套件（文档/表格/演示）：兼容 Office 格式，无广告不收费。", "\uE8A5"),
                new("WPS Office", "国民办公套件：轻快兼容 Office，内置 AI 功能多，个人免费。", "\uE8A5"),
                new("Microsoft Office", "微软官方办公套件：兼容性天花板（订阅制收费，可选装）。", "\uE8A5"),
                new("飞书", "一站式协作：文档 / 表格 / 会议 / 机器人，团队办公全家桶。", "\uE8BD"),
                new("Notion", "笔记 + 数据库 + 项目管理：个人免费，用顺手就离不开了。", "\uE70F"),
                new("语雀", "国产知识库：文档 / 表格 / 画板一体，中文体验好。", "\uE70F"),
                new("Stirling PDF", "开源 PDF 工具箱：合并/拆分/压缩/转换，全部本地处理不外传。", "\uE8A5"),
                new("SumatraPDF", "极速 PDF 阅读器：轻量开源、打开秒出，还支持漫画格式。", "\uE8A5"),
                new("7-Zip", "开源压缩解压神器：ZIP/RAR/7z 全支持，免费无广告。", "\uE8B7"),
                new("Everything", "秒搜全盘文件的搜索神器：输入即出结果，免费轻量。", "\uE721"),
                new("Snipaste", "截图 + 贴图神器：截图钉在屏幕上对照干活，效率翻倍。", "\uE790"),
                new("Quicker", "快捷动作面板：把常用操作变成一键（选中文字就翻译/搜索…）。", "\uE895"),
                new("uTools", "效率工具箱：快捷键呼出，搜索即用的万能小工具集合。", "\uE721"),
                new("Obsidian", "本地优先的笔记与知识库：个人使用免费，插件生态强大。", "\uE70F"),
                new("Typora", "Markdown 写作工具：专注写作、所见即所得（收费，平替 MarkText）。", "\uE70F"),
                new("AutoHotkey", "自动化脚本神器：键盘鼠标的重复劳动全自动，小白也能写。", "\uE756"),
                new("PowerToys", "微软官方效率工具集：分屏 / 取色 / 批量改名 / 快速启动全都有。", "\uE713"),
                new("XMind", "思维导图标准工具：整理思路、做汇报大纲，免费版够用。", "\uE8BD"),
                new("滴答清单", "待办与日程管理：跨手机电脑同步，GTD 利器。", "\uE8BD"),
                new("PotPlayer", "全能视频播放器：格式全、性能好、免费无广告。", "\uE8B2"),
                new("向日葵远程", "国产远程控制：远程帮家人修电脑 / 远程办公，免费够用。", "\uE8BD"),
                new("Bandizip", "高颜值压缩工具：解压快、界面清爽（免费版够用）。", "\uE8B7"),
                new("NanaZip", "开源压缩：7-Zip 的现代化界面版。", "\uE8B7"),
                new("Ditto", "开源剪贴板增强：历史剪贴一键回贴，效率神器。", "\uE8A5"),
                new("幕布", "大纲笔记：写大纲一键变思维导图。", "\uE8BD"),
                new("石墨文档", "国产在线文档：表格 / 文档协作，轻快好用。", "\uE8A5"),
                new("ProcessOn", "在线流程图：画流程图 / 架构图 / 脑图，有免费额度。", "\uE8BD"),
                new("WizTree", "极速磁盘分析：秒扫全盘找出占地大户，清理必备。", "\uE721"),
                new("通义听悟", "阿里会议助手：实时转写 / 翻译 / AI 摘要，会议记录神器。", "\uE8BD"),
                new("讯飞听见", "科大讯飞转写：录音转文字准确率高，会议 / 采访必备。", "\uE8BD"),
                new("飞书妙记", "飞书会议记录：转写 + 智能纪要，团队协作顺手。", "\uE8BD"),
                new("Microsoft 365 Copilot", "微软办公 AI：Word / Excel / PPT 深度集成（订阅制）。", "\uE8A5"),
                new("Gamma", "AI 生成 PPT：一句话出精美演示稿（需网络环境）。", "\uE8BD"),
                new("AiPPT", "国产 AI PPT：职场汇报快速出稿，模板接地气。", "\uE8BD"),
            }),
        "create" => ("内容创作 · 做自媒体 / 剪辑 / 绘画",
            "拍视频、剪片子、做设计、录播客——用工具把想法变成作品；配合 AI 助手，连文案和脚本都有人帮你想。",
            new List<AiToolCard>
            {
                new("剪映", "国民视频剪辑：手机电脑都有，AI 字幕 / 图文成片，新手最快出片。", "\uE714"),
                new("OBS Studio", "开源直播/录屏软件：主播、录课、演示必备，功能强大还免费。", "\uE714"),
                new("DaVinci Resolve", "专业级视频剪辑调色：个人免费版就够做完整片子。", "\uE714"),
                new("Kdenlive", "开源视频剪辑：免费轻量，老电脑也能流畅剪。", "\uE714"),
                new("Canva", "在线设计：封面 / 海报 / PPT 模板海量，拖拖拽拽就出图。", "\uE790"),
                new("Figma", "UI/UX 设计行业标准工具（个人免费）：做界面设计必备。", "\uE790"),
                new("GIMP", "开源图像处理：Photoshop 的免费替代，插件丰富。", "\uE790"),
                new("Krita", "开源数字绘画：插画、漫画、概念图创作。", "\uE790"),
                new("Inkscape", "开源矢量图设计：Logo / 图标 / 排版（Illustrator 的免费替代）。", "\uE790"),
                new("ComfyUI", "AI 绘画工作流工具：本地出图神器，配合显卡玩转 AI 绘图。", "\uE790"),
                new("OpenToonz", "开源二维动画：吉卜力同款技术路线，做动画短片。", "\uE714"),
                new("Audacity", "开源音频录制剪辑：播客、配音、降噪。", "\uE8D6"),
                new("Cakewalk", "免费专业音乐制作（DAW）：编曲 / 混音不花一分钱。", "\uE8D6"),
                new("HandBrake", "开源视频转码压缩：任何格式变小变清晰。", "\uE8B2"),
                new("创客贴", "国产在线设计：海报 / 名片 / 视频封面，模板接地气。", "\uE790"),
                new("醒图", "手机修图神器：一键出片，手机创作必备。", "\uE790"),
                new("Adobe Express", "Adobe 在线设计：模板 + AI 功能，浏览器可用（有免费版）。", "\uE790"),
                new("Leonardo AI", "AI 绘画平台：出图质量高，有免费额度（需网络环境）。", "\uE774"),
                new("Ideogram", "AI 文字设计：做海报 / Logo 上的文字图最强（需网络环境）。", "\uE774"),
                new("Krea AI", "实时 AI 创作：边画边生成，创意玩法多（需网络环境）。", "\uE774"),
            }),
        "image" => ("图像设计 · 画图 / 修图 / 设计",
            "从修图、绘画到 UI 设计、AI 出图——设计师和爱好玩家都能找到趁手工具，AI 助手帮你装好配置好。",
            new List<AiToolCard>
            {
                new("Photoshop", "图像处理行业标准（订阅制收费，可选装）。", "\uE790"),
                new("GIMP", "开源图像处理：PS 的免费替代，插件丰富、功能全。", "\uE790"),
                new("Krita", "开源数字绘画：插画、漫画、概念图，笔刷强大。", "\uE790"),
                new("Paint.NET", "轻量图像编辑器：比系统画图强得多，免费够日常修图。", "\uE790"),
                new("Photopea", "在线版 PS：浏览器打开就能用，免费（界面几乎和 PS 一样）。", "\uE790"),
                new("Inkscape", "开源矢量图设计：Logo / 图标 / 排版（免费）。", "\uE790"),
                new("Illustrator", "矢量设计行业标准（订阅制收费，可选装）。", "\uE790"),
                new("Figma", "UI/UX 设计标准工具（个人免费）：网页 / App 界面设计。", "\uE790"),
                new("Canva", "在线设计：海报 / 封面 / PPT，模板海量，零基础可用。", "\uE790"),
                new("稿定设计", "国产在线设计：电商图 / 海报 / 公众号配图，中文模板多。", "\uE790"),
                new("美图秀秀", "国民修图：一键美颜 / 抠图 / 拼图，手机电脑都有。", "\uE790"),
                new("Clip Studio Paint", "漫画 / 插画专业工具（买断制）：画漫画首选。", "\uE790"),
                new("Procreate", "iPad 绘画王者（收费，仅苹果平板）：数字绘画首选。", "\uE790"),
                new("SketchBook", "免费草图绘画：Autodesk 出品，画草图上手快。", "\uE790"),
                new("SAI", "轻量绘画工具：手绘入门经典，线条顺滑。", "\uE790"),
                new("ComfyUI", "AI 绘画工作流：本地出图神器，工作流可分享可复用。", "\uE790"),
                new("Stable Diffusion WebUI", "AI 绘画经典界面（A1111）：文生图 / 图生图 / 修脸。", "\uE790"),
                new("Midjourney", "顶级 AI 出图（订阅制）：艺术质感强，网页里用。", "\uE790"),
                new("通义万相", "阿里 AI 绘画：中文提示词友好，有免费额度。", "\uE774"),
                new("即梦 AI", "字节 AI 创作平台：出图 + 出视频，中文用着顺。", "\uE774"),
                new("可图", "快手 AI 绘画：免费额度大，人像效果好。", "\uE774"),
                new("Affinity 系列", "专业级图像 / 矢量 / 排版工具：买断制性价比之王（现推出免费版）。", "\uE790"),
                new("Upscayl", "开源 AI 图像放大：老照片 / 低清图秒变高清。", "\uE790"),
                new("waifu2x", "AI 无损放大：二次元图 / 照片放大神器（免费）。", "\uE790"),
                new("Real-ESRGAN", "开源画质修复：模糊图片放大还原细节。", "\uE790"),
                new("Remove.bg", "一键抠图：3 秒去背景，免费额度够用。", "\uE790"),
                new("MasterGo", "国产协作设计：Figma 的国产替代，团队协作顺滑。", "\uE790"),
                new("即时设计", "国产 UI 设计：免费在线设计工具，中文生态完善。", "\uE790"),
                new("Pixso", "国产设计协作：Figma 平替，支持离线使用。", "\uE790"),
                new("Nano Banana", "谷歌 Gemini 图像模型：改图一致性之王，P 图玩法风靡全网（需网络环境）。", "\uE774"),
                new("GPT Image", "OpenAI 生图模型：文字渲染最准，做海报 / 信息图强（需网络环境）。", "\uE943"),
                new("Flux", "开源生图模型之王：可本地跑也可云端用，生态工具多。", "\uE790"),
                new("文心一格", "百度 AI 绘画：中文水墨 / 国风出图有特色，免费额度。", "\uE774"),
                new("LiblibAI", "国内最大模型社区：海量 SD 模型 / 在线生图 / 工作流分享。", "\uE790"),
                new("吐司 TusiArt", "在线 AI 绘画平台：模型库 + 一键生图，不用装显卡。", "\uE790"),
                new("无界 AI", "国产 AI 绘画平台：模板多，商用素材库丰富。", "\uE790"),
                new("Recraft", "AI 设计工具：矢量 / 图标 / 品牌设计专用（需网络环境）。", "\uE790"),
            }),
        "video" => ("视频制作 · 剪辑 / 特效 / AI 视频",
            "从手机剪辑到专业后期，再到 AI 生成视频——做视频的全套装备都在这。",
            new List<AiToolCard>
            {
                new("剪映", "国民剪辑：AI 字幕 / 图文成片 / 一键包装，新手最快出片。", "\uE714"),
                new("CapCut", "剪映国际版：同样的爽快体验，模板全球化。", "\uE714"),
                new("必剪", "B站官方剪辑：一键投稿 B 站，虚拟形象 / 素材多。", "\uE714"),
                new("DaVinci Resolve", "专业剪辑调色：免费版功能≈付费版 90%，电影级工具。", "\uE714"),
                new("Premiere Pro", "Adobe 专业剪辑（订阅制收费）：行业通用、格式全。", "\uE714"),
                new("After Effects", "动效 / 特效 / 合成标准（订阅制收费）：做片头片尾。", "\uE714"),
                new("Final Cut Pro", "苹果专业剪辑（买断制，仅 Mac）：流畅省资源。", "\uE714"),
                new("Kdenlive", "开源剪辑：免费多轨，老电脑友好。", "\uE714"),
                new("Shotcut", "开源剪辑：格式兼容广，免费跨平台。", "\uE714"),
                new("OpenShot", "开源剪辑：界面简单，新手练手用。", "\uE714"),
                new("OBS Studio", "开源录屏/直播：录教程、直播、采集游戏画面。", "\uE714"),
                new("HandBrake", "开源转码：视频压缩 / 格式转换。", "\uE8B2"),
                new("ShanaEncoder", "高速转码：H265/AV1 压缩体积小画质好。", "\uE8B2"),
                new("LosslessCut", "无损剪辑：不重编码秒切视频，剪广告 / 切片神器。", "\uE714"),
                new("Runway", "AI 视频生成先驱：文生视频 / 视频 AI 编辑（需网络环境）。", "\uE774"),
                new("可灵 AI", "快手 AI 视频：文生/图生视频，中文提示词效果好。", "\uE774"),
                new("即梦 AI", "字节 AI 视频：生成 + 编辑一体，中文友好。", "\uE774"),
                new("Pika", "AI 视频生成：创意短片 / 特效玩法多。", "\uE774"),
                new("HeyGen", "AI 数字人：真人形象口播视频，营销 / 教学用。", "\uE8BD"),
                new("腾讯智影", "腾讯 AI 视频工具：数字人 / 配音 / 剪辑一体，国内可用。", "\uE8BD"),
                new("度加剪辑", "百度 AI 剪辑：图文成片 / 智能字幕，免费。", "\uE714"),
                new("D-ID", "照片说话：上传照片让画像开口说话（数字人入门玩法）。", "\uE8BD"),
                new("Descript", "AI 视频剪辑：删文字即删片段，口播 / 播客神器（需网络环境）。", "\uE714"),
                new("Opus Clip", "AI 长剪短：长视频自动切出爆款短视频（需网络环境）。", "\uE714"),
                new("Subtitle Edit", "开源字幕工具：字幕制作 / 翻译 / 时间轴，免费。", "\uE714"),
                new("Aegisub", "字幕制作经典：特效字幕 / 时间轴（免费开源）。", "\uE714"),
                new("Vrew", "AI 字幕剪辑：像编辑文档一样剪视频（有免费版）。", "\uE714"),
                new("Clipchamp", "微软官方剪辑：Win11 预装风格，免费够用。", "\uE714"),
                new("威力导演", "消费级剪辑：模板 / 特效一键出片（收费）。", "\uE714"),
                new("会声会影", "经典剪辑：老牌易上手，国内教程多（收费）。", "\uE714"),
                new("Veo", "谷歌视频模型：画质与物理感天花板之一（需网络环境）。", "\uE774"),
                new("Vidu", "生数科技：国产视频模型黑马，参考图一致性玩法火。", "\uE774"),
                new("海螺视频", "MiniMax 视频模型：特效玩法多，中文提示词友好。", "\uE774"),
                new("智谱清影", "智谱 AI 视频：图生视频 / 文生视频，国内免费额度大方。", "\uE774"),
                new("Luma Dream Machine", "Luma AI 视频生成：镜头运动自然（需网络环境）。", "\uE774"),
                new("Seedance", "字节视频模型：动作流畅，即梦同源技术。", "\uE774"),
                new("Filmora", "万兴喵影：国产剪辑，AI 功能多、出海口碑好（收费）。", "\uE714"),
                new("万彩动画大师", "国产动画制作：做动画课件 / 宣传短片，模板化上手快。", "\uE714"),
            }),
        "audio" => ("音频制作 · 音乐 / 配音 / 播客",
            "做音乐、配音、播客、修音——从免费专业工具到 AI 生成音频，声音玩法全集。",
            new List<AiToolCard>
            {
                new("Audacity", "开源音频编辑：录音 / 剪辑 / 降噪，播客配音首选。", "\uE8D6"),
                new("Ocenaudio", "轻量音频编辑：比 Audacity 更好上手，免费。", "\uE8D6"),
                new("GoldWave", "经典音频工具：老牌共享软件，功能扎实。", "\uE8D6"),
                new("Cakewalk", "免费专业 DAW：前身声纳，编曲混音全功能零成本。", "\uE8D6"),
                new("Reaper", "高性价比 DAW（$60 授权，可无限试用）：轻快稳定。", "\uE8D6"),
                new("LMMS", "开源音乐制作：免费编曲、做电子乐。", "\uE8D6"),
                new("FL Studio", "电子音乐神器（收费）：从入门到职业都强。", "\uE8D6"),
                new("Cubase", "老牌专业 DAW（收费）：录音棚标准之一。", "\uE8D6"),
                new("Studio One", "现代 DAW（收费）：工作流顺，PreSonus 出品。", "\uE8D6"),
                new("Suno", "AI 写歌王者：输入想法出完整带唱歌曲，游戏 BGM 也能做。", "\uE774"),
                new("Udio", "AI 音乐生成：音质细腻，风格多样。", "\uE774"),
                new("ElevenLabs", "AI 语音天花板：超真实配音 / 声音克隆（需网络环境）。", "\uE774"),
                new("讯飞智作", "科大讯飞 AI 配音：中文语音自然，宣传片 / 课件配音。", "\uE8BD"),
                new("魔音工坊", "AI 配音平台：几十种音色，有声书 / 短视频配音。", "\uE8BD"),
                new("海螺 AI 语音", "MiniMax 语音：中文情感合成强，克隆音色自然。", "\uE8BD"),
                new("Voice.ai", "变声 / 声音克隆：实时变声开黑、配音玩梗。", "\uE8D6"),
                new("Voicemeeter", "虚拟声卡：直播多路音频混音路由（进阶）。", "\uE8D6"),
                new("OBS Studio", "录屏 / 直播音频：录教程声音、推流。", "\uE714"),
                new("Adobe Audition", "专业音频工作站：修音 / 混音 / 降噪（订阅制收费）。", "\uE8D6"),
                new("Waves", "音频插件大厂：混音 / 母带插件套装（收费）。", "\uE8D6"),
                new("Vital", "免费合成器神器：做电子音色，免费里最强。", "\uE8D6"),
                new("Surge XT", "开源合成器：功能强大、完全免费。", "\uE8D6"),
                new("Spitfire LABS", "免费音源库：钢琴 / 弦乐等高质量音色免费拿。", "\uE8D6"),
                new("MuseScore", "开源打谱软件：写曲谱 / 编曲免费专业。", "\uE8D6"),
                new("MP3tag", "音频标签管理：批量改歌名 / 封面 / 信息。", "\uE8D6"),
                new("foobar2000", "发烧友播放器：音质好、可高度定制，免费。", "\uE8B2"),
                new("海绵音乐", "字节 AI 音乐：免费生成歌曲，中文歌词效果好。", "\uE774"),
                new("天工 SkyMusic", "昆仑万维 AI 音乐：中文歌生成质量高，免费玩。", "\uE774"),
                new("ACE Studio", "AI 歌声合成：输入曲谱歌词出演唱级人声，调音区神器。", "\uE8BD"),
                new("TME Studio", "腾讯音乐创作：伴奏 / 扒谱 / 智能歌词工具集。", "\uE8BD"),
                new("Riffusion", "AI 音乐生成：实时哼唱变编曲，玩法新颖（需网络环境）。", "\uE774"),
                new("BandLab", "在线音乐制作：网页编曲 + 社区，免费。", "\uE8D6"),
            }),
        "writing" => ("文案写作 · AI 写作搭子",
            "写文案、写小说、写报告、写公众号——这些工具让笔杆子产量翻倍。",
            new List<AiToolCard>
            {
                new("秘塔写作猫", "中文 AI 写作：改写 / 润色 / 纠错，论文报告都能用。", "\uE70F"),
                new("笔灵 AI", "AI 写作平台：公文 / 论文 / 小说 / 短视频脚本模板多。", "\uE70F"),
                new("火山写作", "字节 AI 写作：中文润色 / 续写，免费额度多。", "\uE70F"),
                new("彩云小梦", "AI 小说续写：网文作者神器，遇卡文就续一段。", "\uE70F"),
                new("Effie", "写作 + 思维导图一体：大纲到成稿一条线（免费功能够用）。", "\uE70F"),
                new("MarkText", "开源 Markdown 编辑器：Typora 的免费替代。", "\uE70F"),
                new("Zettlr", "开源学术写作：论文 / 引用管理 / Markdown 一气呵成。", "\uE70F"),
                new("Joplin", "开源笔记：端到端加密 + 全平台同步，写作用它收纳素材。", "\uE70F"),
                new("语雀", "知识库写作：文档 / 博客 / 团队知识沉淀。", "\uE70F"),
                new("飞书文档", "协作写作：多人同时编辑 + 评论，团队写材料就用它。", "\uE8BD"),
                new("腾讯文档", "在线文档：微信 / QQ 分享方便，国民级协作。", "\uE8BD"),
                new("金山文档", "WPS 在线文档：兼容 Office 格式的在线协作。", "\uE8A5"),
                new("Notion", "全能写作 + 管理：笔记 / 策划 / 发布一站。", "\uE70F"),
                new("Obsidian", "卡片盒笔记法：长期写作 / 知识管理，写小说做设定集好用。", "\uE70F"),
                new("Grammarly", "英文写作助手：语法 / 语气一键优化（需网络环境）。", "\uE774"),
                new("DeepL Write", "AI 英文润色：比语法检查更自然（有免费额度）。", "\uE774"),
                new("枕星 AI 助手", "给主题出大纲、逐段扩写、改语气——在主页对话框直接试。", "\uE99A"),
                new("豆包", "字节 AI 助手：写文案 / 列大纲 / 头脑风暴，免费好用。", "\uE99A"),
                new("Kimi 智能助手", "月之暗面出品：长文分析 / 写作改写，App + 网页都好用。", "\uE99A"),
                new("文心一言", "百度 AI 助手：中文理解强，生态全。", "\uE99A"),
                new("千问", "阿里 AI 助手（原通义）：写报告 / 出 PPT 大纲，免费。", "\uE99A"),
                new("腾讯元宝", "腾讯 AI 助手：公众号写作 / 资料整理顺手。", "\uE99A"),
                new("智谱清言", "智谱 AI 助手：编程 / 写作均衡，免费。", "\uE99A"),
                new("讯飞星火 App", "科大讯飞：语音写作 / 会议记录有优势。", "\uE99A"),
                new("海螺 AI", "MiniMax：长文写作 / 角色扮演有特色。", "\uE99A"),
            }),
        "translate" => ("翻译工具 · 读外文不再头疼",
            "网页 / 文档 / 视频字幕 / 实时对话——翻译场景全覆盖，免费的比收费的还好用。",
            new List<AiToolCard>
            {
                new("DeepL", "翻译质量天花板：翻译腔少，文档翻译支持好（有免费额度）。", "\uE774"),
                new("沉浸式翻译", "浏览器插件神器：网页双语对照，看外文网站无痛。", "\uE774"),
                new("彩云小译", "中英同传级翻译：网页 / 视频字幕 / 语音，免费好用到出圈。", "\uE774"),
                new("有道翻译", "国民翻译：词典 + 文档 + 截图翻译，中英日韩都行。", "\uE774"),
                new("腾讯翻译君", "腾讯出品：语音对话翻译（出国旅行神器），免费。", "\uE774"),
                new("百度翻译", "老牌翻译：支持语种最多，文档翻译能力强。", "\uE774"),
                new("Google 翻译", "全球最通用：网页整页翻译 + 拍照翻译（需网络环境）。", "\uE774"),
                new("小牛翻译", "国产专业翻译：支持 400+ 语种，文档批量翻译。", "\uE774"),
                new("Pot 翻译", "开源划词翻译（pot-app）：选中即翻，可接任意翻译源。", "\uE774"),
                new("SPlayer", "开源播放器：视频外挂字幕 + 实时翻译，追剧学外语。", "\uE8B2"),
                new("剪映字幕翻译", "剪辑工具自带：视频字幕一键翻译成多语言。", "\uE714"),
                new("枕星 AI 助手", "粘贴外文翻译、处理文档或字幕稿——在主页对话框直接问。", "\uE99A"),
                new("微软翻译", "Microsoft Translator：办公全家桶集成，多设备同步。", "\uE774"),
                new("欧路词典", "词典神器：划词翻译 + 生词本，学英语标配。", "\uE70F"),
                new("GoldenDict", "开源词典：聚合多个词典源，查词专业。", "\uE70F"),
                new("Anki", "记忆神器：背单词 / 背知识点，全平台免费。", "\uE8BD"),
                new("多邻国", "游戏化语言学习：碎片时间学外语。", "\uE8BD"),
                new("每日英语听力", "听力练习：VOA / BBC / 影视原声带字幕。", "\uE8D6"),
                new("LanguageTool", "开源语法检查：多语言写作纠错（浏览器插件）。", "\uE70F"),
                new("Lingva Translate", "开源翻译前端：多引擎对比翻译，免费。", "\uE774"),
            }),
        _ => ("AI agent · 你的 AI 开发搭档",
            "让 AI 自己动手写代码、跑命令、改文件的「智能伙伴」——选一个装好，之后的开发活都交给它（没梯子也能用，见卡片说明）。",
            new List<AiToolCard>
            {
                new("Claude Code", "Anthropic 的 AI 开发搭档：有【桌面版】图形界面（新手友好）和命令行版；没梯子可直连 DeepSeek 模型。", "\uE99A"),
                new("Codex", "OpenAI 的 AI 开发搭档：【桌面版】/ VS Code 插件 / 命令行三种形态；走 ChatGPT 会员或 API。", "\uE943"),
                new("Cursor", "全球最火的 AI 代码编辑器：补全速度快、改代码稳（约 $20/月）。", "\uE70F"),
                new("Trae", "字节跳动出品的 AI IDE：完全免费，内置 Claude / GPT / DeepSeek，零成本上手。", "\uE7FC"),
                new("WorkBuddy", "办公场景的 AI Agent：写研究报告、跑跨工具自动化任务（连 Slack / Notion / Office 等）。", "\uE8BD"),
                new("DeepSeek Harness", "DeepSeek 官方的 Agent 工具（dsh）：全中文、国内直连、按量付费超便宜。", "\uE756"),
                new("Windsurf", "Cascade 智能体驱动的 AI 编辑器：多文件任务自动完成，插件覆盖 40+ IDE。", "\uE713"),
                new("腾讯 CodeBuddy", "腾讯云的 AI 编程伙伴：全栈 AI IDE，30 天免费试用。", "\uE8BD"),
                new("Qoder", "阿里推出的 AI IDE：任务自动规划执行，有免费额度。", "\uE946"),
                new("通义灵码", "阿里云智能编码助手：免费、中文体验好，有 VS Code / JetBrains 插件。", "\uE774"),
                new("豆包 MarsCode", "字节的免费 AI 编程助手：插件形态，开箱即用。", "\uE8F1"),
                new("GitHub Copilot", "最老牌的 AI 编程插件：VS Code / JetBrains 全支持（付费）。", "\uE8C8"),
                new("Cline", "开源的 AI 编程插件：免费，可接入 DeepSeek 等任意模型。", "\uE943"),
                new("Roo Code", "开源的 AI 编程插件（Cline 增强版）：功能更猛，免费接任意模型。", "\uE943"),
                new("Kilo Code", "新一代开源 AI 编程插件：多模型自由切换，社区活跃。", "\uE943"),
                new("Continue", "开源编辑器 AI 助手：VS Code 里的免费 Copilot 替代。", "\uE70F"),
                new("Aider", "开源命令行 AI 编程搭档：免费，可接 DeepSeek 等国产模型，省钱党首选。", "\uE756"),
                new("Zed", "极速代码编辑器（Rust 出品）：内置 AI 协作，快得飞起。", "\uE70F"),
                new("OpenClaw", "开源的个人 AI 助手：装在自己电脑上，能接微信 / WhatsApp 等多渠道干活。", "\uE8BD"),
                new("OpenCode", "开源免费的命令行开发助手：可接任意国产模型，灵活省钱。", "\uE756"),
                new("Lovable", "Vibe coding 网站生成器：一句话生成能用的网站 / 应用（浏览器里用）。", "\uE774"),
                new("Bolt.new", "浏览器里的全栈开发：说话就出网站，零配置上手。", "\uE774"),
                new("v0", "Vercel 出品：文字生成精美网页界面代码，前端神器。", "\uE790"),
                new("Replit", "在线编程 + AI Agent：打开浏览器就能开发，不用装环境。", "\uE943"),
                new("Devin", "Cognition 的云端 AI 工程师：网页里派任务自动完成（较贵，尝鲜可看）。", "\uE99A"),
                new("Dify", "开源 LLM 应用平台：拖拽搭 AI 应用 / 知识库问答，可自部署。", "\uE8F1"),
                new("n8n", "开源自动化工作流：把各种软件串起来自动干活（Zapier 的免费替代）。", "\uE895"),
                new("扣子 Coze", "字节的 AI Bot 平台：零代码做聊天机器人，一键发到微信 / 飞书。", "\uE8BD"),
                new("Hermes", "Nous Research 的全功能 Agent：支持自定义模型。", "\uE99A"),
                new("CC Switch", "配套小工具：一键在多家供应商之间切换 Claude Code / Codex 的配置。", "\uE895"),
                new("Gemini CLI", "谷歌官方命令行 AI 编程：开源免费，Gemini 模型驱动（需网络环境）。", "\uE774"),
                new("Qwen Code", "阿里千问的命令行编程 agent：开源免费、中文友好、国内直连。", "\uE774"),
                new("iFlow CLI", "心流 AI 命令行助手：国内直连，中文体验好。", "\uE756"),
                new("Droid", "Factory AI 的编码 agent：自动化跑任务能力强（需网络环境）。", "\uE943"),
                new("Amp", "Sourcegraph 出品的编码 agent：理解大型代码库很在行（需网络环境）。", "\uE943"),
                new("Kiro", "AWS 的 AI IDE：规格驱动开发，适合规范流程（预览期免费）。", "\uE713"),
                new("Warp", "AI 终端：命令自动补全 / 报错解释，终端体验革新。", "\uE756"),
                new("Goose", "Block 开源的本地 agent：数据不出本机，可接任意模型。", "\uE8F1"),
                new("OpenHands", "开源通用 agent：类 Devin 的开源实现，可自部署。", "\uE8F1"),
                new("Jules", "Google 异步编码 agent：派任务它后台写完交 PR（需网络环境）。", "\uE774"),
                new("Manus", "现象级国产通用 Agent：云端执行任务，直接交付成品（报告 / 网页 / 数据）。", "\uE99A"),
                new("Genspark", "AI Agent 搜索：自动调研出报告，Super Agent 玩法多（需网络环境）。", "\uE721"),
                new("FastGPT", "开源知识库 Agent 平台：喂资料做问答机器人，国内可自部署。", "\uE8F1"),
                new("RAGFlow", "开源 RAG 引擎：文档理解深，搭企业知识库首选。", "\uE8F1"),
                new("LangFlow", "低代码 AI 工作流：拖拽搭 agent 流程，LangChain 可视化。", "\uE8F1"),
                new("Zapier", "自动化老大哥：8000+ 应用互联，AI 加持后更强（需网络环境）。", "\uE895"),
                new("AutoGen", "微软多智能体框架：让多个 AI 分工协作（开发者向）。", "\uE8F1"),
            }),
    };

    private async void CardGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AiToolCard card) return;
        UsageStats.RecordClick("ai:" + card.Name);   // ZXAI：点击计数（热度排序用）
        // ZXAI 2026-09-22（用户口径）：点击卡片 = 打开官网下载页（官网按钮职责并入卡片点击；
        // "交给 AI 装" 改为卡片上的「AI 安装」按钮，见 AiInstall_Click）。
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(card.OfficialUrl));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiCategory] open official failed: {ex.Message}");
        }
    }

    /// <summary>ZXAI 2026-09-22（用户口径）：「AI 安装」按钮——打开 AI 助手并预填安装引导（原卡片点击职责）。</summary>
    private void AiInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name || string.IsNullOrWhiteSpace(name)) return;
        UsageStats.RecordClick("ai:" + name);
        App.MainWindow?.OpenAiAssistantWithPrompt(string.Format(LocalizationService.L("AiCategory_InstallPrompt", "帮我安装 {0}（先问我问题，问清楚再带我装）"), name));
    }

    /// <summary>ZXAI：点星标收藏 / 取消（进「收藏」目录）。</summary>
    private void ToggleAiFav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name || string.IsNullOrWhiteSpace(name)) return;
        UsageStats.ToggleAiFavorite(name);
        Apply();   // 刷新星标与排序
    }

    /// <summary>🆕「⬇ 一键安装」：本应用直接用 winget 安装（不依赖 AI 助手/API）。</summary>
    private async void QuickInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name || string.IsNullOrWhiteSpace(name)) return;
        var key = AiToolLinks.QuickInstallKey(name);
        if (key is null) return;

        var confirm = new ContentDialog
        {
            Title = string.Format(LocalizationService.L("AiCategory_QuickInstallTitle", "一键安装 {0}"), name),
            Content = string.Format(LocalizationService.L("AiCategory_QuickInstallConfirmation", "将直接用 winget 安装到系统（不需要 AI 助手，可能持续几分钟，期间可继续用应用）：\n{0}"), name),
            PrimaryButtonText = LocalizationService.L("AiCategory_StartInstall", "开始安装"),
            CloseButtonText = LocalizationService.L("Common_Cancel", "取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var old = b.Content;
        b.IsEnabled = false;
        string report;
        try
        {
            // 依赖预检：缺的自动装好、已装的跳过——用户看到的仍只是一个按钮
            var reports = new List<string>();
            foreach (var req in AiToolLinks.RequiresOf(name))
            {
                b.Content = LocalizationService.L("AiCategory_CheckingDependencies", "检查依赖…");
                if (await TubaWinUi3.Services.AppManagement.SystemInstaller.IsInstalledAsync(req, CancellationToken.None))
                    continue;
                b.Content = string.Format(LocalizationService.L("AiCategory_InstallingDependency", "安装依赖（{0}）…"), req);
                var dep = await TubaWinUi3.Services.AppManagement.SystemInstaller.InstallAsync(req, null, CancellationToken.None);
                reports.Add(dep);
                if (!dep.StartsWith("✅") && !dep.Contains("已经装过"))
                {
                    reports.Add(string.Format(LocalizationService.L("AiCategory_DependencyFailed", "（依赖 {0} 未装成功，已中止主安装）"), req));
                    throw new InvalidOperationException(string.Join("\n\n", reports));
                }
            }
            b.Content = LocalizationService.L("Common_Installing", "安装中…");
            reports.Add(await TubaWinUi3.Services.AppManagement.SystemInstaller.InstallAsync(key, null, CancellationToken.None));
            report = string.Join("\n\n", reports);
        }
        catch (Exception ex)
        {
            report = ex.Message.StartsWith("❌") || ex.Message.Contains("依赖") ? ex.Message : string.Format(LocalizationService.L("AiCategory_InstallFailedWithReason", "❌ 安装失败：{0}"), ex.Message);
        }
        finally
        {
            b.Content = old;
            b.IsEnabled = true;
        }

        var done = new ContentDialog
        {
            Title = report.StartsWith("✅") ? LocalizationService.L("AiCategory_InstallComplete", "安装完成") : LocalizationService.L("AiCategory_InstallIncomplete", "安装未完成"),
            Content = report,
            CloseButtonText = LocalizationService.L("Common_OK", "好"),
            XamlRoot = XamlRoot,
        };
        _ = done.ShowAsync();
    }

    /// <summary>🆕「🌐 官网」：浏览器打开官网（未收录工具自动回退搜索页），用户可自行下载。</summary>
    [Obsolete("2026-09-22 用户口径：官网入口并入卡片点击（CardGrid_ItemClick）。保留仅供内部引用检查。")]
    private async void OpenOfficial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(AiToolLinks.OfficialUrl(name)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiCategory] open url failed: {ex.Message}");
        }
    }
}
