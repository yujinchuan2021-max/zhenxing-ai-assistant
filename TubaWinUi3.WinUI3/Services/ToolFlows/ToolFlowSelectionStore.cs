using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TubaWinUi3.Services.ToolFlows;

/// <summary>工具流来自枕星助手，或由用户自行编写。</summary>
public enum ToolFlowOrigin { Assistant, User }

/// <summary>逐项执行结果；JSON 值与服务端契约的 snake_case 字段对齐。</summary>
public enum ToolFlowEventKind
{
    DownloadStarted,
    DownloadSucceeded,
    DownloadFailed,
    InstallSucceeded,
    InstallFailed,
    Verified,
}

public sealed record ToolFlowConversationMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    /// <summary>历史展示记录可能没有逐条时间；未知时保持 null。</summary>
    public DateTimeOffset? AtUtc { get; init; }
}

public sealed record ToolFlowItem
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    /// <summary>例如 agent、software、asset、service；由工具流作者描述。</summary>
    public required string Kind { get; init; }
    public string? Version { get; init; }
    public string? SourceUrl { get; init; }
    public string? DownloadUrl { get; init; }
    /// <summary>匹配客户端固定安装目标时填写；未知或需用户协助时为空。</summary>
    public string? InstallTargetKey { get; init; }
    /// <summary>需要用户协助时的具体操作提示（保存时快照的一部分）；旧快照没有该字段，读取为 null。</summary>
    public string? ManualHint { get; init; }
}

/// <summary>
/// 用户对某一项的自报声明。目前只支持「用户自报完成」（user_reported_done）：
/// 只代表用户自述，绝不代表应用检测或验证通过，也不会生成上报资格。
/// </summary>
public sealed record ToolFlowItemMark
{
    public const string UserReportedDone = "user_reported_done";

    public required string ItemId { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public string? Note { get; init; }
}

public sealed record ToolFlowExecutionEvent
{
    /// <summary>本次事件的稳定 ID，用于重试去重。</summary>
    public required string Id { get; init; }
    public required string ItemId { get; init; }
    public required ToolFlowEventKind Kind { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// UI 明确调用 SelectForInstall 后才存在的不可变选择快照。
/// FlowId 与 SubmissionId 均标识这次最终选定；重选或修订须生成新的 FlowId。
/// </summary>
public sealed record ToolFlowSelection
{
    public required string FlowId { get; init; }
    public required string SubmissionId { get; init; }
    public required ToolFlowOrigin Origin { get; init; }
    public required DateTimeOffset SelectedAtUtc { get; init; }
    /// <summary>Local chat ownership only. Legacy selections remain unbound; never included in upload payloads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConversationId { get; init; }
    /// <summary>用户选定的工作流名称，供后续按工作流主题检索和推荐。</summary>
    public string FlowName { get; init; } = "";
    /// <summary>用户确认想做的项目或提出的需求。</summary>
    public string ProjectGoal { get; init; } = "";
    public required string GoalDescription { get; init; }
    public required string FlowText { get; init; }
    /// <summary>最终选定时的分享资格快照；开关关闭或完整对话超接收上限时为 false，日后不补传。</summary>
    public required bool UploadEnabledAtSelection { get; init; }
    public required List<ToolFlowConversationMessage> Conversation { get; init; }
    public required List<ToolFlowItem> Items { get; init; }
    public List<ToolFlowExecutionEvent> Events { get; init; } = [];
    /// <summary>用户自报标记（如自报完成）；旧快照没有该字段（默认空列表）。不代表应用验证结果。</summary>
    public List<ToolFlowItemMark> ItemMarks { get; init; } = [];
}

/// <summary>
/// 只负责本地“最终选择安装工具流”及后续逐项执行记录。
/// 没有网络、自动选择、安装或上传副作用；调用方必须由明确的 UI 选择动作进入。
/// </summary>
public sealed class ToolFlowSelectionStore
{
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _directory;
    private readonly object _gate;

    /// <param name="dataRoot">可注入假根；省略时使用客户端数据根。</param>
    public ToolFlowSelectionStore(string? dataRoot = null)
    {
        var root = Path.GetFullPath(dataRoot ?? ConfigManager.GetDataDir());
        _directory = Path.Combine(root, "ToolFlows", "Selections");
        _gate = Gates.GetOrAdd(_directory, static _ => new object());
    }

    /// <summary>
    /// 只由明确的“选择并安装”UI 动作调用。同一 SubmissionId + 同一内容幂等返回；
    /// 同一 ID 指向不同内容则拒绝，不能悄悄改写用户已经选定的方案。
    /// </summary>
    public ToolFlowSelection SelectForInstall(ToolFlowSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidateSelection(selection, requireNoEvents: true);
        var path = SelectionPath(selection.SubmissionId);
        var json = JsonSerializer.Serialize(selection, JsonOptions);
        lock (_gate)
        {
            if (File.Exists(path)) return MatchExisting(path, selection);
            try
            {
                WriteAtomic(path, json, overwrite: false);
                return Deserialize(json, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                return MatchExisting(path, selection);
            }
        }
    }

    /// <summary>按本次选定 ID 读取；不存在返回 null，损坏记录则抛错。</summary>
    public ToolFlowSelection? GetBySubmissionId(string submissionId)
    {
        var path = SelectionPath(submissionId);
        lock (_gate)
            return File.Exists(path) ? Read(path) : null;
    }

    /// <summary>列出本地已选工具流（含执行事件），供未来上传器补发。</summary>
    public IReadOnlyList<ToolFlowSelection> ListAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return [];
            return Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Read)
                .OrderByDescending(x => x.SelectedAtUtc)
                .ThenBy(x => x.SubmissionId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// 关闭分享开关时调用：先立内存级撤销门闩（fail-closed，磁盘清理失败也不会再发送旧记录），
    /// 再把既有选定记录的上报资格统一作废（UploadEnabledAtSelection 置 false 并持久化）。
    /// 本地方案、对话与事件原样保留。返回 false = 至少一条记录的磁盘清理失败
    /// （该记录已由内存门闩在本次运行内阻止发送；请在重新开启前再次作废——作废未完成时开关将保持关闭）。
    /// </summary>
    public bool InvalidatePendingUploads()
    {
        // 门闩必须先立：无论磁盘清理是否成功，此后不得发送撤销时刻前的选定。
        ToolFlowUploadLatch.RevokeAll();
        var allCleared = true;
        try
        {
            lock (_gate)
            {
                if (!Directory.Exists(_directory)) return true;
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var selection = Read(file);
                        if (!selection.UploadEnabledAtSelection) continue;
                        var updated = selection with { UploadEnabledAtSelection = false };
                        WriteAtomic(file, JsonSerializer.Serialize(updated, JsonOptions), overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or InvalidDataException or JsonException)
                    {
                        // 单条失败不阻断其余记录；返回 false 让设置页提示重试。
                        allCleared = false;
                    }
                }
            }
        }
        catch (Exception)
        {
            allCleared = false;   // 枚举失败等：门闩已兜底，由设置页提示重试。
        }
        return allCleared;
    }

    /// <summary>
    /// 将下载/安装事件附到已选方案的某一项。同一事件 ID + 同一内容幂等返回；
    /// ID 冲突、条目不属于该方案或选定记录不存在时拒绝。
    /// </summary>
    public ToolFlowSelection AppendEvent(string submissionId, ToolFlowExecutionEvent executionEvent)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        ValidateEvent(executionEvent);
        var path = SelectionPath(submissionId);
        lock (_gate)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("选定工具流不存在。", path);
            var current = Read(path);
            if (!current.Items.Any(x => x.ItemId == executionEvent.ItemId))
                throw new InvalidOperationException("事件的 itemId 不属于所选工具流。");

            var existing = current.Events.FirstOrDefault(x => x.Id == executionEvent.Id);
            if (existing is not null)
            {
                if (EqualJson(existing, executionEvent)) return current;
                throw new InvalidOperationException("事件 ID 已对应另一条内容。");
            }

            var updated = current with { Events = [.. current.Events, executionEvent] };
            WriteAtomic(path, JsonSerializer.Serialize(updated, JsonOptions), overwrite: true);
            return updated;
        }
    }

    /// <summary>
    /// 记下用户对某一项的「自报完成」。幂等：同一项重复标记不重复追加；只写本地快照。
    /// 只代表用户自述，不代表应用检测或验证通过，也不改变上报资格。
    /// </summary>
    public ToolFlowSelection MarkItemUserReportedDone(string submissionId, string itemId)
    {
        ValidateId(itemId, nameof(itemId));
        var path = SelectionPath(submissionId);
        lock (_gate)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("选定工具流不存在。", path);
            var current = Read(path);
            if (!current.Items.Any(x => x.ItemId == itemId))
                throw new InvalidOperationException("该条目不属于所选工具流。");
            if (current.ItemMarks.Any(m => m.ItemId == itemId && m.Kind == ToolFlowItemMark.UserReportedDone))
                return current;
            var mark = new ToolFlowItemMark
            {
                ItemId = itemId,
                Kind = ToolFlowItemMark.UserReportedDone,
                AtUtc = DateTimeOffset.UtcNow,
                Note = null,
            };
            var updated = current with { ItemMarks = [.. current.ItemMarks, mark] };
            WriteAtomic(path, JsonSerializer.Serialize(updated, JsonOptions), overwrite: true);
            return updated;
        }
    }

    /// <summary>
    /// 恢复入口用：返回选定时间最新的一条可读记录（可加过滤）。单条损坏记录会被跳过，
    /// 不会让恢复入口整体不可用；没有可读记录时返回 null。
    /// </summary>
    public ToolFlowSelection? FindLatestResumable(Func<ToolFlowSelection, bool>? filter = null)
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return null;
            ToolFlowSelection? best = null;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                ToolFlowSelection candidate;
                try
                {
                    candidate = Read(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidDataException or JsonException)
                {
                    continue;   // 一条坏记录不应挡住恢复入口。
                }
                if (filter is not null && !filter(candidate)) continue;
                if (best is null || candidate.SelectedAtUtc > best.SelectedAtUtc) best = candidate;
            }
            return best;
        }
    }

    /// <summary>Exact local chat ownership; unbound legacy records are never silently attached.</summary>
    public ToolFlowSelection? FindLatestForConversation(string? conversationId) =>
        string.IsNullOrWhiteSpace(conversationId) ? null : FindLatestResumable(s =>
            string.Equals(s.ConversationId, conversationId, StringComparison.Ordinal));

    private ToolFlowSelection MatchExisting(string path, ToolFlowSelection incoming)
    {
        var existing = Read(path);
        var original = existing with { Events = [] };
        if (EqualJson(original, incoming)) return existing;
        throw new InvalidOperationException("SubmissionId 已对应另一份选定工具流。");
    }

    private static bool EqualJson<T>(T first, T second)
        => JsonSerializer.Serialize(first, JsonOptions) == JsonSerializer.Serialize(second, JsonOptions);

    private ToolFlowSelection Read(string path)
        => Deserialize(File.ReadAllText(path, Encoding.UTF8), path);

    private static ToolFlowSelection Deserialize(string json, string path)
    {
        try
        {
            var selection = JsonSerializer.Deserialize<ToolFlowSelection>(json, JsonOptions)
                ?? throw new JsonException("选定记录为空。");
            using var document = JsonDocument.Parse(json);
            var hasFlowName = document.RootElement.TryGetProperty("flowName", out _);
            var hasProjectGoal = document.RootElement.TryGetProperty("projectGoal", out _);
            if (!hasFlowName || !hasProjectGoal)
            {
                // Earlier snapshots had no user-confirmed title/project field. Keep them
                // readable, but never upload a derived title as if the user selected it.
                selection = selection with
                {
                    FlowName = hasFlowName ? selection.FlowName : LegacyFlowName(selection.GoalDescription),
                    ProjectGoal = hasProjectGoal ? selection.ProjectGoal : LegacyProjectGoal(selection.GoalDescription),
                    UploadEnabledAtSelection = false,
                };
            }
            ValidateSelection(selection, requireNoEvents: false);
            foreach (var executionEvent in selection.Events) ValidateEvent(executionEvent);
            if (selection.Events.Any(e => !selection.Items.Any(i => i.ItemId == e.ItemId)) ||
                selection.Events.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != selection.Events.Count)
                throw new InvalidDataException("事件与选定工具流不一致。");
            if (selection.ItemMarks.Any(m => !selection.Items.Any(i => i.ItemId == m.ItemId)) ||
                selection.ItemMarks.Select(m => m.Kind + "|" + m.ItemId).Distinct(StringComparer.Ordinal).Count() != selection.ItemMarks.Count)
                throw new InvalidDataException("自报标记与选定工具流不一致。");
            return selection;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"选定工具流记录损坏：{path}", ex);
        }
    }

    private static void ValidateSelection(ToolFlowSelection value, bool requireNoEvents)
    {
        ValidateId(value.FlowId, nameof(value.FlowId));
        ValidateId(value.SubmissionId, nameof(value.SubmissionId));
        if (value.ConversationId is not null &&
            (string.IsNullOrWhiteSpace(value.ConversationId) || value.ConversationId.Length > 128 ||
             value.ConversationId.Any(char.IsControl)))
            throw new ArgumentException("Invalid local conversation ID.");
        if (!Enum.IsDefined(value.Origin)) throw new ArgumentException("origin 必须为 assistant 或 user。");
        RequireUtc(value.SelectedAtUtc, nameof(value.SelectedAtUtc));
        if (string.IsNullOrWhiteSpace(value.FlowName) || value.FlowName.Length > 120)
            throw new ArgumentException("flowName 长度必须为 1 到 120 个字符。");
        if (string.IsNullOrWhiteSpace(value.ProjectGoal) || value.ProjectGoal.Length > 65536)
            throw new ArgumentException("projectGoal 长度必须为 1 到 65536 个字符。");
        if (string.IsNullOrWhiteSpace(value.GoalDescription)) throw new ArgumentException("goalDescription 不能为空。");
        if (string.IsNullOrWhiteSpace(value.FlowText)) throw new ArgumentException("flowText 不能为空。");
        if (value.Conversation is null || value.Items is null || value.Events is null || value.ItemMarks is null)
            throw new ArgumentException("conversation、items、events、itemMarks 不能为 null。");
        if (value.Items.Count == 0) throw new ArgumentException("选定工具流至少需要一项。");
        if (requireNoEvents && value.Events.Count != 0)
            throw new ArgumentException("选择安装时不能预填执行事件。");

        foreach (var message in value.Conversation)
        {
            if (message is null || message.Role is not ("user" or "assistant") || message.Content is null)
                throw new ArgumentException("conversation 只接受 user/assistant 文本消息。");
            if (message.AtUtc is { } atUtc) RequireUtc(atUtc, nameof(message.AtUtc));
        }

        foreach (var mark in value.ItemMarks)
        {
            if (mark is null) throw new ArgumentException("itemMarks 不能包含 null。");
            ValidateId(mark.ItemId, nameof(mark.ItemId));
            if (mark.Kind != ToolFlowItemMark.UserReportedDone)
                throw new ArgumentException("itemMarks 只接受 user_reported_done 标记。");
            RequireUtc(mark.AtUtc, nameof(mark.AtUtc));
        }

        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.Items)
        {
            if (item is null) throw new ArgumentException("items 不能包含 null。");
            ValidateId(item.ItemId, nameof(item.ItemId));
            if (!itemIds.Add(item.ItemId)) throw new ArgumentException("items 含重复 itemId。");
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Kind))
                throw new ArgumentException("工具名称和类别不能为空。");
        }
    }

    private static void ValidateEvent(ToolFlowExecutionEvent value)
    {
        ValidateId(value.Id, nameof(value.Id));
        ValidateId(value.ItemId, nameof(value.ItemId));
        if (!Enum.IsDefined(value.Kind)) throw new ArgumentException("事件类别无效。");
        RequireUtc(value.AtUtc, nameof(value.AtUtc));
        if (value.Detail is null) throw new ArgumentException("事件 detail 不能为 null。");
    }

    private static string LegacyFlowName(string? goalDescription)
    {
        if (string.IsNullOrWhiteSpace(goalDescription)) return "";
        var name = new StringBuilder(120);
        foreach (var rune in goalDescription.Trim().EnumerateRunes())
        {
            if (name.Length + rune.Utf16SequenceLength > 120) break;
            // A title should remain a single display line even if the old goal spans lines.
            if (Rune.IsWhiteSpace(rune))
            {
                if (name.Length > 0 && name[^1] != ' ') name.Append(' ');
            }
            else
            {
                name.Append(rune);
            }
        }
        return name.ToString().TrimEnd();
    }

    private static string LegacyProjectGoal(string? goalDescription)
    {
        var goal = goalDescription?.Trim() ?? "";
        if (goal.Length <= 65536) return goal;
        var length = 65536;
        if (char.IsHighSurrogate(goal[length - 1])) length--;
        return goal[..length];
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException($"{name} 必须使用 UTC。");
    }

    private static void ValidateId(string? value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out _))
            throw new ArgumentException($"{name} 必须为标准 UUID。");
    }

    private string SelectionPath(string submissionId)
    {
        ValidateId(submissionId, nameof(submissionId));
        return Path.Combine(_directory, submissionId + ".json");
    }

    private static void WriteAtomic(string path, string content, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
