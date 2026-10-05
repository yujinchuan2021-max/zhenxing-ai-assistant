using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>A complete, structured recommendation bound to one visible assistant message.</summary>
internal sealed record ToolFlowProposal(
    string Name, string Text, IReadOnlyList<ToolFlowItem> Items,
    string SuggestedProjectGoal, IReadOnlyList<ToolFlowConversationMessage> Conversation);

/// <summary>
/// Reads explicit recommendation contracts. No software-name inference, settings,
/// file access or execution: an ordinary reply or incomplete block is not an install plan.
/// </summary>
internal static partial class ToolFlowProposalParser
{
    private static readonly Regex ItemBlock = new(
        @"(?m)^\s*```toolflow-items[ \t]*\r?\n(?<items>[\s\S]*?)\r?\n[ \t]*```[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AnyCodeBlock = new(@"(?m)^\s*```[^\r\n]*\r?\n[\s\S]*?^\s*```[ \t]*\r?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex FlowName = new(
        @"(?mi)^[ \t]*(?:#{1,6}[ \t]+)?(?:\*\*)?(?:工具流名称|Tool[ \t]*flow[ \t]+name|Workflow[ \t]+name)[ \t]*[:：][ \t]*(?<name>[^\r\n]+)\r?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ProjectGoal = new(
        @"(?mi)^[ \t]*(?:\*\*)?(?:项目目标|用户目标|Project[ \t]+goal)[ \t]*[:：][ \t]*(?<goal>[^\r\n]+)\r?$",
        RegexOptions.CultureInvariant);

    public static bool TryCapture(IReadOnlyList<ToolFlowConversationMessage> visibleMessages,
        int assistantIndex, out ToolFlowProposal? proposal)
    {
        proposal = null;
        if (assistantIndex < 0 || assistantIndex >= visibleMessages.Count) return false;
        var message = visibleMessages[assistantIndex];
        if (message.Role != "assistant" || string.IsNullOrWhiteSpace(message.Content)
            || message.Content.Length > 65536) return false;
        // Alternatives must be chosen individually; never let a mixed reply enter
        // the old single-plan path, even when it also contains toolflow-items.
        if (FindProtocolBlocks(message.Content, includePartialLabels: true).Any(x => x.Kind != "toolflow-items")) return false;
        var context = visibleMessages.Take(assistantIndex + 1).ToArray();
        if (!context.Any(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content))) return false;

        var blocks = ItemBlock.Matches(message.Content);
        if (blocks.Count != 1) return false; // Do not merge alternative plans into one install list.
        var prose = AnyCodeBlock.Replace(message.Content, "");
        var names = FlowName.Matches(prose);
        if (names.Count != 1) return false;
        var name = TrimMarkdown(names[0].Groups["name"].Value);
        if (name.Length is 0 or > 120) return false;
        // A title and machine-readable rows alone are not a complete explained recommendation.
        if (string.IsNullOrWhiteSpace(FlowName.Replace(prose, ""))) return false;

        var lines = blocks[0].Groups["items"].Value.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is 0 or > 32) return false;
        var items = new List<ToolFlowItem>(lines.Length);
        foreach (var line in lines)
        {
            var fields = line.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 6 || fields[0].Length is 0 or > 120
                || fields[1].Length > 64 || fields[2].Length > 80 || fields[5].Length > 100
                || !ValidHttpsUrl(fields[3]) || !ValidHttpsUrl(fields[4])) return false;
            string? Field(int i) => string.IsNullOrWhiteSpace(fields[i]) ? null : fields[i];
            items.Add(new ToolFlowItem
            {
                ItemId = Guid.NewGuid().ToString("D"), Name = fields[0],
                Kind = Field(1) ?? "software", Version = Field(2),
                SourceUrl = Field(3), DownloadUrl = Field(4), InstallTargetKey = Field(5),
            });
        }

        var goal = ProjectGoal.Match(prose);
        var goalText = goal.Success ? TrimMarkdown(goal.Groups["goal"].Value)
            : context.First(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content)).Content;
        proposal = new ToolFlowProposal(name, message.Content, items.AsReadOnly(), goalText,
            Array.AsReadOnly(context));
        return true;
    }

    private static string TrimMarkdown(string text) => text.Trim().Trim('*').Trim();

    private static bool ValidHttpsUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > 2048 || value != value.Trim() || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns
            || !uri.Host.Contains('.') || uri.Host.EndsWith('.') || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || value.Contains('\\')) return false;
        return !new[] { ".local", ".localhost", ".internal", ".lan", ".test", ".invalid" }
            .Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildOutputInstructions(IEnumerable<string> knownTargets,
        IReadOnlyDictionary<string, string>? targetDescriptions = null) =>
        "目标范围：按用户要求的成果选择最小充分流程，每个清单项必须是明确需求或当前无法交付时不可缺的依赖。直接用AI生成音乐、图片或视频，优先选覆盖生成与取得结果的服务，核对其必要网络、账号及使用条件；不默认配编曲、混音、编辑、转换、编程Agent或独立API，也不把未要求的扩展放进可选清单。只有用户明确要求相应能力，或所需交付存在具体缺口时再增加；用户说只要生成时撤回多加项并收缩后续清单。直接生成类不硬套开发三档或编程模型问卷，开发类仍保留真正必要的开发主干。单个服务覆盖多个角色时复用，不因完整性或专业性补软件。" +
        "开发类推荐排序：在用户已确认的费用、地区资格、账号、网络和硬件条件内，先核对当前模型的任务能力与可用接入，再选择真实兼容的外部 AI Agent；用户指定工具时尊重其选择。开发类比较应包括 Codex，不能因为缺少自动安装代码就漏掉强候选，也不能强制所有档都选 Codex。中国大陆用户不默认推荐其尚无地区/账号资格的 Claude 官方服务；Codex使用OpenAI等海外服务时同样核对实际提供方资格与可用性，国内模型接入按其自身条件核对，VPN不等于可用。游戏引擎结合目标平台的商业化成熟度、发布与资产生态、授权和Agent适配比较，不因2D或免费就默认特定引擎，也不强推不适合的重型引擎。可比较兼容国内API的桌面Agent、Codex桌面版+该客户端实际支持的可用ChatGPT方案、商业引擎+高能力Agent等完整路线，但这些只是例子，不固定三档品牌；Claude受阻时可以保留合适引擎并换接入，便宜不等于弱，付费不等于强。APP/网站、图像视频、办公数据等分别按自己的业务瓶颈与交付链选型，不能照搬游戏或编程排名。当前型号与能力查官方资料，不写死最强排名；这些判断在内部完成，正文仍保持简短。固定安装代码仅限制自动执行范围，不限制合理推荐，必要人工项仍列入清单。" +
        "Agent客户端默认桌面优先：在目标能力、模型兼容、平台、费用及账号网络条件成立的候选中，优先官方桌面版或具备所需项目操作能力的图形界面Agent，并复用已验证可用的桌面客户端。只有用户明确选择CLI、没有合适可用桌面候选或必需能力仅CLI支持时才用命令行，卡片一行解释原因；不因已有CLI或它更容易自动安装而默认选CLI。普通聊天桌面软件不自动等于能读写和执行项目的开发Agent；同名桌面版与CLI的模型/API/会员接入分别核对。清单名称标明桌面版或CLI，下载与固定安装代码须指向同一形态；宿主未提供匹配的桌面安装代码时留空，给核实过的官方桌面入口或合适桌面替代，不能拿CLI代码冒充桌面安装。安装代码后的括号是宿主描述，只填代码本身；既有已确认清单不静默换装。低成本dsh等CLI示例也受此例外限制，不固定为默认方案。" +
        "网络与版本门槛必须在展示可选方案前核对：具体版本、安装包、注册/首次登录、模型服务及必要构建资源分别确认。官网可达或软件已安装不证明登录与AI可用；用户已明确不能使用海外服务时，依赖海外登录的路线不可作为可行档，不等装完再要求解决网络。国内API不解除Agent自己的登录门槛。国内版与国际版的账号、模型能力和安装代码分别核对，中文界面或国内下载镜像不能证明是国内版。宿主trae固定指向国际版ByteDance.Trae，官方说明国际版首次登录需海外网络；国内版ByteDance.Trae.CN不能用trae代码安装，未提供匹配代码就留空并保留待核的准备步骤。推荐前核对当前官方条件，不保证任何版本在用户电脑上已验通，不假定它支持任意兼容API。已安装、登录待完成、模型待验证分别表达；保留现有可用引擎，仅调整受阻项，已确认方案不静默换装。" +
        "所有选择式澄清都用按钮：平台、账号可用性、网络、目标范围、已有资源等只问当前影响方案的一个问题，用唯一闭合的 choice-question 代码块，JSON严格为 {\"schema\":1,\"question\":\"短问题\",\"options\":[{\"id\":\"唯一短ID\",\"label\":\"简短可点击选项\",\"answer\":\"与该选项一致的自然语言回答\"}]}，options为2到6项。不要让用户输入序号或抄写答案，不在正文重复长选项，不同题不堆在同一回复，不要求用户在聊天中提交Key、密码或其它凭据。回答只发送用户选择，不代表购买、安装、登录或系统操作授权，不加action/command/url字段，不同时输出任何工具流确认块。仍需自由描述的问题保留短问句，不强凑选项。" +
        "模型偏好澄清：只有需要独立模型接入的路线，且费用与模型使用意愿未知、会改变选择时，正文短问一句，随后输出唯一闭合的 model-preference 代码块，JSON严格为 {\"schema\":1}。宿主渲染四个可点击选项：可以付费，并使用顶级模型；可以付费，需要便宜的模型；不想付费，用本地模型；我也不知道，给我推荐。不要要求输入序号或重复列出长选项；问完等待回答，不同时输出toolflow-options/toolflow-items或choice-question。选本地后先只读核对内存、CPU、显卡、显存等是否支持，未知不承诺能跑；已知答案不重复问。选择第四项也算已回答本题，助手先按已知条件给建议，不再次抛相同偏好选项，不替用户默认同意付费。直接生成服务的费用、账号或网络问题按实际服务用choice-question核对，不套编程模型四选题。选择只发送用户偏好，不代表购买或安装授权。" +
        "已有软件优先复用：在给出待准备清单前，用宿主可用的只读软件查询核对电脑已有工具，不只查应用自己的登记表。已经具备且满足这条业务链所需能力的软件，不再列为待安装。普通解压可复用已安装WinRAR等现有解压工具，不重复加入7-Zip；只有明确依赖7z命令/API或特定版本时才单独核对必需项。不能把替代软件说成已安装原目标，未查到就说明未知，不编造检测结果；完整方案可保留已有环境上下文，确认清单只列缺项和需要用户协助的步骤。" +
        "工具流卡片格式：普通问答、澄清问题、格式示例与未完成草案不要输出确认结构。开发需求已充分明确时，正文只用1到3句说明推荐理由与关键未知项，然后附唯一、闭合的 toolflow-options JSON代码块，不同时输出 toolflow-items。JSON严格使用 schema:1、goal（用户项目目标）、recommended（light/medium/heavy之一）、options（恰好三个对象，各有唯一id：light、medium、heavy，依次为轻量、中量、重量）。每档字段为 id、name、summary、fit、cost、requirements（均字符串），可选warnings（字符串数组），items（完整清单数组）；清单每项字段为 name、type、version、sourceUrl、downloadUrl、installTargetKey（均字符串）。name是主方案名称，summary最多两句，fit/cost/requirements写短行。每档必须保留项目所需的核心AI Agent、与用户模型兼容的接入方式及开发环境；只在完成同一已确认成果所需的成本、配置复杂度、能力和自动化上分档，不为凑档位增加未要求的制作环节，每档各有一套有实质差异的主方案，不能只换标签，也不在单档内堆并列备选。遵守用户的预算、硬件、联网及账号限制；不可行档用 available:false、unavailableReason写原因，items可以为空，并禁止推荐该档；available省略表示true。不得为凑三档捏造可行路线，全部不可行时先澄清受阻条件，不输出候选块。用户会点击一张卡片再核对它自己的清单；选择和确认之前不执行安装。单软件推荐、排障或明确只需一套方案时保留单方案兼容格式：正文单独写“工具流名称：……”（英文“Workflow name: ...”），简述项目目标、步骤、依据与未知项，末尾唯一闭合 toolflow-items 代码块；每行六列：名称 | 类型 | 版本 | 来源 HTTPS 网址 | 下载 HTTPS 网址 | 固定安装目标代码。未知版本或网址留空并在说明中标为待核，网址必须HTTPS；账号、模型接入、服务、素材等人工项的安装代码留空，不指定固定品牌。所有安装目标仅可选本轮宿主已提供并明确适用于该项的固定代码："
        + string.Join(", ", knownTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(key => targetDescriptions is not null && targetDescriptions.TryGetValue(key, out var description)
                ? $"{key}（{description}）" : key))
        + "。未知目标保留人工步骤，不根据软件名称猜代码，不填命令、脚本或路径，不执行网址、脚本或代码块。";
}
