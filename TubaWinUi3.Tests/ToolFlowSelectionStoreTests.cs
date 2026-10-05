using System.Text.Json.Nodes;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowSelectionStoreTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(),
        "zxai-flow-test-" + Guid.NewGuid().ToString("N"));

    public ToolFlowSelectionStoreTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { }
    }

    [Fact]
    public void ConversationLookup_RestoresOnlyItsOwnLatestPlan_AndDoesNotAttachLegacy()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var first = store.SelectForInstall(ExampleSelection(false) with { ConversationId = "chat-a", SelectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4) });
        var latestA = store.SelectForInstall(ExampleSelection(false) with { ConversationId = "chat-a", SelectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3) });
        store.SelectForInstall(ExampleSelection(false) with { ConversationId = "chat-b", SelectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2) });
        var legacy = store.SelectForInstall(ExampleSelection(false) with { SelectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var restarted = new ToolFlowSelectionStore(_dataRoot);
        Assert.Equal(latestA.SubmissionId, restarted.FindLatestForConversation("chat-a")!.SubmissionId);
        Assert.NotEqual(first.SubmissionId, restarted.FindLatestForConversation("chat-a")!.SubmissionId);
        Assert.Null(restarted.FindLatestForConversation("new-chat"));
        Assert.Null(restarted.FindLatestForConversation(null));
        Assert.Null(restarted.FindLatestForConversation(""));
        Assert.Equal(legacy.SubmissionId, restarted.FindLatestResumable()!.SubmissionId);
        Assert.Null(restarted.GetBySubmissionId(legacy.SubmissionId)!.ConversationId);
    }

    [Fact]
    public void ExplicitSelection_PersistsCompleteSnapshot_ForLaterReading()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selected = ExampleSelection(uploadEnabled: false);

        var saved = store.SelectForInstall(selected);
        var reloaded = new ToolFlowSelectionStore(_dataRoot).GetBySubmissionId(selected.SubmissionId);

        Assert.NotNull(reloaded);
        Assert.Equal(saved.FlowId, reloaded.FlowId);
        Assert.Equal(ToolFlowOrigin.User, reloaded.Origin);
        Assert.False(reloaded.UploadEnabledAtSelection);
        Assert.Equal(selected.FlowName, reloaded.FlowName);
        Assert.Equal(selected.ProjectGoal, reloaded.ProjectGoal);
        Assert.Equal(selected.GoalDescription, reloaded.GoalDescription);
        Assert.Equal(selected.FlowText, reloaded.FlowText);
        Assert.Equal("我想做一款 2D 游戏", reloaded.Conversation[0].Content);
        Assert.Null(reloaded.Conversation[0].AtUtc); // 历史展示记录没有逐条时间，不补造
        Assert.Equal("https://godotengine.org/download/windows/", reloaded.Items[0].SourceUrl);
        Assert.Equal("godot", reloaded.Items[0].InstallTargetKey);
        Assert.Empty(reloaded.Events);
        Assert.Equal(reloaded.SubmissionId, Assert.Single(store.ListAll()).SubmissionId);
    }

    [Fact]
    public void SubmissionRetry_IsIdempotent_ButChangedSelectionIsRejected()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selected = ExampleSelection(uploadEnabled: true);
        store.SelectForInstall(selected);

        var afterEvent = store.AppendEvent(selected.SubmissionId,
            Event(Guid.NewGuid().ToString("D"), selected.Items[0].ItemId, ToolFlowEventKind.DownloadStarted));
        Assert.Single(afterEvent.Events);
        Assert.Single(store.SelectForInstall(selected).Events); // 同一选定重试不能清掉后续事件

        Assert.Throws<InvalidOperationException>(() =>
            store.SelectForInstall(selected with { FlowText = "另一份方案" }));
        Assert.Throws<InvalidOperationException>(() =>
            store.SelectForInstall(selected with { FlowName = "另一份工作流" }));
        Assert.Throws<InvalidOperationException>(() =>
            store.SelectForInstall(selected with { ProjectGoal = "想做另一款游戏" }));
        Assert.Throws<InvalidOperationException>(() =>
            store.SelectForInstall(selected with { UploadEnabledAtSelection = false }));
    }

    [Fact]
    public void ExecutionEvents_RequireSelectedItem_AndAreIdempotent()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selected = store.SelectForInstall(ExampleSelection(uploadEnabled: true));
        var first = Event(Guid.NewGuid().ToString("D"), selected.Items[0].ItemId, ToolFlowEventKind.DownloadStarted);
        store.AppendEvent(selected.SubmissionId, first);
        Assert.Single(store.AppendEvent(selected.SubmissionId, first).Events);

        Assert.Throws<InvalidOperationException>(() =>
            store.AppendEvent(selected.SubmissionId,
                first with { Kind = ToolFlowEventKind.DownloadFailed }));
        Assert.Throws<InvalidOperationException>(() =>
            store.AppendEvent(selected.SubmissionId,
                Event(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), ToolFlowEventKind.InstallSucceeded)));
        Assert.Throws<FileNotFoundException>(() =>
            store.AppendEvent(Guid.NewGuid().ToString("D"), first));

        var last = store.AppendEvent(selected.SubmissionId,
            Event(Guid.NewGuid().ToString("D"), selected.Items[0].ItemId, ToolFlowEventKind.Verified));
        Assert.Equal(2, last.Events.Count);
        Assert.Equal(2, Assert.Single(store.ListAll()).Events.Count);
        var raw = File.ReadAllText(Path.Combine(_dataRoot, "ToolFlows", "Selections", selected.SubmissionId + ".json"));
        Assert.Contains("\"download_started\"", raw);
        Assert.Contains("\"verified\"", raw);
        Assert.Contains("\"origin\": \"user\"", raw); // enum wire spelling
    }

    [Fact]
    public void SelectionRejectsPathLikeIds_AndPrefilledEvents()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selected = ExampleSelection(uploadEnabled: true);
        Assert.Throws<ArgumentException>(() =>
            store.SelectForInstall(selected with { SubmissionId = "../outside" }));
        Assert.Throws<ArgumentException>(() =>
            store.SelectForInstall(selected with { Events = [Event(Guid.NewGuid().ToString("D"), selected.Items[0].ItemId, ToolFlowEventKind.DownloadStarted)] }));
        Assert.Empty(store.ListAll());
    }

    [Fact]
    public void NewSelections_RequireBoundedFlowNameAndProjectGoal()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selected = ExampleSelection(uploadEnabled: true);
        Assert.Throws<ArgumentException>(() => store.SelectForInstall(selected with { FlowName = " " }));
        Assert.Throws<ArgumentException>(() => store.SelectForInstall(selected with { FlowName = new string('名', 121) }));
        Assert.Throws<ArgumentException>(() => store.SelectForInstall(selected with { ProjectGoal = " " }));
        Assert.Throws<ArgumentException>(() => store.SelectForInstall(selected with { ProjectGoal = new string('需', 65537) }));

        var atLimit = selected with
        {
            FlowName = new string('名', 120),
            ProjectGoal = new string('需', 65536),
        };
        var saved = store.SelectForInstall(atLimit);
        Assert.Equal(120, saved.FlowName.Length);
        Assert.Equal(65536, saved.ProjectGoal.Length);
    }

    [Fact]
    public void LegacySelectionWithoutNewFields_DerivesBoundedValuesFromGoalDescription()
    {
        var store = new ToolFlowSelectionStore(_dataRoot);
        var selected = store.SelectForInstall(ExampleSelection(uploadEnabled: true) with
        {
            GoalDescription = "  想做一款游戏\n" + new string('需', 65537),
        });
        var path = Path.Combine(_dataRoot, "ToolFlows", "Selections", selected.SubmissionId + ".json");
        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(legacy.Remove("flowName"));
        Assert.True(legacy.Remove("projectGoal"));
        File.WriteAllText(path, legacy.ToJsonString());

        var reloaded = new ToolFlowSelectionStore(_dataRoot).GetBySubmissionId(selected.SubmissionId);
        Assert.NotNull(reloaded);
        Assert.StartsWith("想做一款游戏 ", reloaded.FlowName);
        Assert.Equal(120, reloaded.FlowName.Length);
        Assert.StartsWith("想做一款游戏\n", reloaded.ProjectGoal);
        Assert.Equal(65536, reloaded.ProjectGoal.Length);
        Assert.False(reloaded.UploadEnabledAtSelection); // 旧记录没有用户确认的工具流名称，不补传推断标题

        var updated = store.AppendEvent(selected.SubmissionId,
            Event(Guid.NewGuid().ToString("D"), selected.Items[0].ItemId, ToolFlowEventKind.Verified));
        Assert.Equal(reloaded.FlowName, updated.FlowName);
        Assert.Equal(reloaded.ProjectGoal, updated.ProjectGoal);
        var migrated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(updated.FlowName, migrated["flowName"]!.GetValue<string>());
        Assert.Equal(updated.ProjectGoal, migrated["projectGoal"]!.GetValue<string>());
        Assert.False(migrated["uploadEnabledAtSelection"]!.GetValue<bool>());
    }

    private static ToolFlowSelection ExampleSelection(bool uploadEnabled) => new()
    {
        FlowId = Guid.NewGuid().ToString("D"),
        SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.User,
        SelectedAtUtc = DateTimeOffset.UtcNow,
        FlowName = "2D 游戏开发工作流",
        ProjectGoal = "我想做一款 2D 游戏",
        GoalDescription = "做一款 2D 游戏",
        FlowText = "我自己整理的 Godot + 素材 + AI Agent 工作流全文。",
        UploadEnabledAtSelection = uploadEnabled,
        Conversation =
        [
            new ToolFlowConversationMessage { Role = "user", Content = "我想做一款 2D 游戏", AtUtc = null },
            new ToolFlowConversationMessage { Role = "assistant", Content = "先确定目标平台，再配工具。", AtUtc = null },
        ],
        Items =
        [
            new ToolFlowItem
            {
                ItemId = Guid.NewGuid().ToString("D"),
                Name = "Godot",
                Kind = "software",
                Version = "4.x",
                SourceUrl = "https://godotengine.org/download/windows/",
                DownloadUrl = null,
                InstallTargetKey = "godot",
            },
        ],
    };

    private static ToolFlowExecutionEvent Event(string id, string itemId, ToolFlowEventKind kind) => new()
    {
        Id = id,
        ItemId = itemId,
        Kind = kind,
        AtUtc = DateTimeOffset.UtcNow,
        Detail = "等待本地下载或验证结果",
    };
}
