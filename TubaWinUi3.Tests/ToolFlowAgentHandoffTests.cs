using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowAgentHandoffTests
{
    [Fact]
    public void Build_UsesConfirmedProjectAndPlan_WithDistinctActualItemStatuses()
    {
        var selection = Selection();
        var result = new ToolFlowInstallResult(
        [
            Result(selection.Items[0], ToolFlowInstallItemStatus.Installed),
            Result(selection.Items[1], ToolFlowInstallItemStatus.AlreadyInstalled),
            Result(selection.Items[2], ToolFlowInstallItemStatus.ManualStep),
            Result(selection.Items[3], ToolFlowInstallItemStatus.Failed),
        ]);

        var text = ToolFlowAgentHandoff.Build(selection, result);

        Assert.Contains("我确认的项目目标：", text);
        Assert.Contains("做一款 2D 游戏", text);
        Assert.Contains("我选择的工具流：Godot 2D 游戏工具流", text);
        Assert.Contains("我确认的完整方案", text);
        Assert.Contains("Godot + Git + 素材 + AI Agent", text);
        Assert.Contains("Godot：本轮安装后状态检查通过", text);
        Assert.Contains("方案版本 4.x（实际安装版本未核实）", text);
        Assert.Contains("Git：本轮状态检查发现已安装", text);
        Assert.Contains("素材来源：需要我参与处理；尚未验证", text);
        Assert.Contains("AI Agent：本轮安装或状态检查未成功；尚未验证", text);
        Assert.Contains("第一个里程碑", text);
        Assert.DoesNotContain("作品已完成", text);
    }

    [Fact]
    public void Build_WithoutRunResult_LeavesEveryItemUnverified()
    {
        var text = ToolFlowAgentHandoff.Build(Selection(), result: null);

        Assert.Contains("Godot：尚无本轮核验结果；不要当作已安装", text);
        Assert.Contains("Git：尚无本轮核验结果；不要当作已安装", text);
        Assert.DoesNotContain("Godot：本轮安装后状态检查通过", text);
    }

    [Fact]
    public void Build_DoesNotCopyConversationEventDetailsOrResultMessages()
    {
        const string secret = "PRIVATE-CONVERSATION-TOKEN";
        const string eventDetail = "PRIVATE-EVENT-PATH";
        const string resultMessage = "PRIVATE-INSTALLER-ERROR";
        var selection = Selection() with
        {
            Conversation = [new ToolFlowConversationMessage
            {
                Role = "user", Content = secret, AtUtc = null,
            }],
            Events = [new ToolFlowExecutionEvent
            {
                Id = Guid.NewGuid().ToString("D"),
                ItemId = "godot",
                Kind = ToolFlowEventKind.Verified,
                AtUtc = DateTimeOffset.UtcNow,
                Detail = eventDetail,
            }],
        };
        var result = new ToolFlowInstallResult(
            [new ToolFlowInstallItemResult("godot", "unselected-name", ToolFlowInstallItemStatus.Installed, resultMessage)]);

        var text = ToolFlowAgentHandoff.Build(selection, result);

        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain(eventDetail, text);
        Assert.DoesNotContain(resultMessage, text);
        Assert.DoesNotContain("unselected-name", text);
        Assert.Contains("Godot：本轮安装后状态检查通过", text);
    }

    [Fact]
    public void Build_IgnoresUnselectedOrAmbiguousResults()
    {
        var selection = Selection();
        var result = new ToolFlowInstallResult(
        [
            Result(selection.Items[0], ToolFlowInstallItemStatus.Installed),
            Result(selection.Items[0], ToolFlowInstallItemStatus.Failed),
            new ToolFlowInstallItemResult("another-id", "Another", ToolFlowInstallItemStatus.Installed, ""),
        ]);

        var text = ToolFlowAgentHandoff.Build(selection, result);

        Assert.Contains("Godot：尚无本轮核验结果；不要当作已安装", text);
        Assert.DoesNotContain("Another", text);
    }

    [Fact]
    public void Build_WithoutCurrentResult_UsesLatestVerificationConsistently()
    {
        var selection = Selection();
        var item = selection.Items[0];
        selection = selection with { Items = [item], Events = [Event(item, ToolFlowEventKind.Verified, 1)] };

        var text = ToolFlowAgentHandoff.Build(selection, null);

        Assert.Contains("Godot：最新本机执行记录已验证安装；本轮未重新检查", text);
        Assert.Contains("- 无：所有项都有已验证的安装记录", text);
        Assert.DoesNotContain("尚无本轮核验结果", text);
    }

    [Theory]
    [InlineData(ToolFlowInstallItemStatus.Failed, "本轮安装或状态检查未成功", "本轮未成功，需要处理")]
    [InlineData(ToolFlowInstallItemStatus.ManualStep, "需要我参与处理；尚未验证", "需要我参与处理")]
    public void Build_CurrentIncompleteResultAppearsInUnfinishedListDespiteOldVerification(
        ToolFlowInstallItemStatus status, string itemDescription, string unfinishedReason)
    {
        var selection = Selection();
        var item = selection.Items[0];
        selection = selection with { Items = [item], Events = [Event(item, ToolFlowEventKind.Verified, 1)] };
        var result = new ToolFlowInstallResult([Result(item, status)]);

        var text = ToolFlowAgentHandoff.Build(selection, result);

        Assert.Contains("Godot：" + itemDescription, text);
        Assert.Contains("Godot：" + unfinishedReason, text);
        Assert.DoesNotContain("- 无：", text);
        Assert.DoesNotContain("Godot：最新本机执行记录已验证", text);
    }

    [Fact]
    public void Build_LatestFailureAppearsInBothLists_WithoutLeakingEventDetails()
    {
        var selection = Selection();
        var item = selection.Items[0];
        selection = selection with
        {
            Items = [item],
            Events = [Event(item, ToolFlowEventKind.InstallFailed, 2), Event(item, ToolFlowEventKind.Verified, 1)],
        };

        var text = ToolFlowAgentHandoff.Build(selection, null);

        Assert.Contains("Godot：最近一次安装或状态检查未成功；尚未验证", text);
        Assert.Contains("Godot：最近一次未成功，需要处理", text);
        Assert.DoesNotContain("- 无：", text);
        Assert.DoesNotContain("PRIVATE-EVENT-DETAIL", text);
    }

    [Fact]
    public void Build_InstallationSuccessWithoutVerification_RemainsUnfinished()
    {
        var selection = Selection();
        var item = selection.Items[0];
        selection = selection with { Items = [item], Events = [Event(item, ToolFlowEventKind.InstallSucceeded, 1)] };

        var text = ToolFlowAgentHandoff.Build(selection, null);

        Assert.Contains("Godot：尚无本轮核验结果；不要当作已安装", text);
        Assert.Contains("Godot：尚无验证记录", text);
        Assert.DoesNotContain("- 无：", text);
    }

    [Fact]
    public void Build_UserReportedDoneStaysUnverifiedInBothLists()
    {
        var selection = Selection();
        var item = selection.Items[0];
        selection = selection with
        {
            Items = [item],
            ItemMarks = [new() { ItemId = item.ItemId, Kind = ToolFlowItemMark.UserReportedDone, AtUtc = DateTimeOffset.UtcNow }],
        };

        var text = ToolFlowAgentHandoff.Build(selection, null);

        Assert.Contains("Godot：我已自报完成；应用未验证", text);
        Assert.Contains("Godot：我已自报完成，但应用未验证", text);
        Assert.DoesNotContain("- 无：", text);
    }

    [Fact]
    public void Build_ConflictingCurrentResultsDoNotHideUnfinishedItemBehindOldVerification()
    {
        var selection = Selection();
        var item = selection.Items[0];
        selection = selection with { Items = [item], Events = [Event(item, ToolFlowEventKind.Verified, 1)] };
        var result = new ToolFlowInstallResult(
        [
            Result(item, ToolFlowInstallItemStatus.Installed),
            Result(item, ToolFlowInstallItemStatus.Failed),
        ]);

        var text = ToolFlowAgentHandoff.Build(selection, result);

        Assert.Contains("Godot：尚无本轮核验结果；不要当作已安装", text);
        Assert.Contains("Godot：尚无验证记录", text);
        Assert.DoesNotContain("- 无：", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HandoffDescribesActualWinRarReuse_InCurrentAndHistoricalEvidence(bool current)
    {
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var selection = Selection() with
        {
            Items = [archive], FlowText = "Use an installed archiver to extract assets.",
            Events = current ? [] : [Event(archive, ToolFlowEventKind.Verified, 1) with { Detail = ToolFlowReuseEvidence.WinRarArchiveDetail }],
        };
        var result = current ? new ToolFlowInstallResult([
            new(archive.ItemId, "PRIVATE-RESULT-NAME", ToolFlowInstallItemStatus.ReusedInstalledTool, @"C:\PRIVATE\RAW-OUTPUT")
            {
                ExistingToolTargetKey = "winrar", ExistingToolName = @"C:\PRIVATE\FORGED-PROVIDER",
            },
        ]) : null;
        var text = ToolFlowAgentHandoff.Build(selection, result);
        Assert.Contains("WinRAR", text);
        Assert.Contains("普通解压需求", text);
        Assert.DoesNotContain("7-Zip：本轮状态检查发现已安装", text);
        Assert.DoesNotContain("7-Zip：最新本机执行记录已验证安装", text);
        Assert.DoesNotContain("PRIVATE", text);
        Assert.DoesNotContain("[reused-tool:", text);
        Assert.Contains("所有准备项已有安装或现有能力复用证据", text);
    }

    [Fact]
    public async Task ReadOnlyPreparationHandoffContainsExistingProviderWithoutPathsOrFalseInstallation()
    {
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var selection = Selection() with { Items = [archive] };
        var evidence = await ToolFlowPreparationTests.WinRarService().PrepareAsync(selection.Items, selection.FlowText);
        var text = ToolFlowAgentHandoff.Build(selection, null, evidence);
        Assert.Contains("WinRAR", text);
        Assert.Contains("未安装 7-Zip", text);
        Assert.DoesNotContain(@"C:\FAKE", text);
    }

    [Fact]
    public void ReuseWithoutTheFixedProviderEvidenceOrForSpecificCliStaysUnverified()
    {
        var archive = ToolFlowPreparationTests.Item("7-Zip CLI", "7zip");
        var selection = Selection() with { Items = [archive] };
        var result = new ToolFlowInstallResult([new(archive.ItemId, archive.Name,
            ToolFlowInstallItemStatus.ReusedInstalledTool, "Claimed reuse") { ExistingToolTargetKey = "winrar" }]);
        var text = ToolFlowAgentHandoff.Build(selection, result);
        Assert.Contains("尚无本轮核验结果", text);
        Assert.DoesNotContain("所有准备项已有", text);
    }

    private static ToolFlowExecutionEvent Event(ToolFlowItem item, ToolFlowEventKind kind, int hour) => new()
    {
        Id = Guid.NewGuid().ToString("D"), ItemId = item.ItemId, Kind = kind,
        AtUtc = DateTimeOffset.Parse("2026-10-02T00:00:00Z").AddHours(hour),
        Detail = "PRIVATE-EVENT-DETAIL",
    };

    private static ToolFlowInstallItemResult Result(ToolFlowItem item, ToolFlowInstallItemStatus status)
        => new(item.ItemId, item.Name, status, "仅用于测试的原始执行消息");

    private static ToolFlowSelection Selection() => new()
    {
        FlowId = Guid.NewGuid().ToString("D"),
        SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.User,
        SelectedAtUtc = DateTimeOffset.UtcNow,
        FlowName = "Godot 2D 游戏工具流",
        ProjectGoal = "做一款 2D 游戏",
        GoalDescription = "做一款 2D 游戏",
        FlowText = "Godot + Git + 素材 + AI Agent",
        UploadEnabledAtSelection = false,
        Conversation = [],
        Items =
        [
            Item("godot", "Godot"),
            Item("git", "Git"),
            Item("asset", "素材来源"),
            Item("agent", "AI Agent"),
        ],
    };

    private static ToolFlowItem Item(string id, string name) => new()
    {
        ItemId = id,
        Name = name,
        Kind = "software",
        Version = id == "godot" ? "4.x" : null,
    };
}
