using System.Text;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>
/// 「继续最近选定的 Godot 工具流」聚焦检查：
/// 恢复读取（含旧 JSON 兼容与坏记录跳过）、逐项状态推导、用户自报完成的持久化与口径、
/// 「继续自动项」不会把已完成项当作新安装、handoff 的未完成项与「无 Agent 可自选」提示。
/// 全部使用假数据根：不联网、不触真实数据目录、不安装、不上传。
/// </summary>
public sealed class ToolFlowResumeTests
{
    [Fact]
    public void FindLatestResumable_SkipsCorruptFiles_AndReadsLegacySnapshotWithoutNewFields()
    {
        var root = NewRoot();
        try
        {
            var store = new ToolFlowSelectionStore(root);

            // 较旧的 Godot 快照（当前格式）。
            var older = Selection("2D 游戏 · 稳妥自建（免费开源）", DateTimeOffset.Parse("2026-09-24T08:00:00+00:00"));
            store.SelectForInstall(older);

            // 较新的非 Godot 快照 + 一条损坏记录：都不能让恢复入口不可用。
            var other = Selection("Godot 2D 游戏工具流（旧命名）", DateTimeOffset.Parse("2026-09-24T09:00:00+00:00"));
            store.SelectForInstall(other);
            File.WriteAllText(Path.Combine(root, "ToolFlows", "Selections", "broken.json"), "{ not json");

            Assert.Equal(other.SubmissionId, store.FindLatestResumable()!.SubmissionId);
            Assert.Equal(older.SubmissionId,
                store.FindLatestResumable(ToolFlowResume.IsGodotFlow)!.SubmissionId);

            // 旧格式 JSON（没有 itemMarks / manualHint 字段）必须仍可读。
            var legacyId = Guid.NewGuid().ToString("D");
            var legacyJson = """
            {
              "flowId": "00000000-0000-0000-0000-0000000000aa",
              "submissionId": "SUBMISSION",
              "origin": "assistant",
              "selectedAtUtc": "2026-09-24T10:00:00+00:00",
              "flowName": "2D 游戏 · 旧快照",
              "projectGoal": "做一款 2D 游戏",
              "goalDescription": "做一款 2D 游戏",
              "flowText": "旧格式方案文本（没有新字段）",
              "uploadEnabledAtSelection": false,
              "conversation": [],
              "items": [
                { "itemId": "00000000-0000-0000-0000-0000000000bb", "name": "Godot", "kind": "software", "installTargetKey": "godot" }
              ],
              "events": []
            }
            """.Replace("SUBMISSION", legacyId);
            File.WriteAllText(Path.Combine(root, "ToolFlows", "Selections", legacyId + ".json"), legacyJson, Encoding.UTF8);

            var loaded = store.FindLatestResumable(ToolFlowResume.IsGodotFlow)!;
            Assert.Equal(legacyId, loaded.SubmissionId);
            Assert.Empty(loaded.ItemMarks);
            Assert.Null(loaded.Items.Single().ManualHint);

            // 恢复视图对旧快照同样可用：清单仍能给出状态（不因缺新字段而失败）。
            var view = ToolFlowResume.Build(loaded, lastResult: null, knownTargets: ["godot"]);
            Assert.Single(view.Rows);
            Assert.Equal(ToolFlowResumeItemState.PendingAutomatic, view.Rows[0].State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void FindLatestResumable_RecognizesGodotFlowAcrossLanguages_PicksNewestRegardlessOfUiLanguage()
    {
        var root = NewRoot();
        try
        {
            var store = new ToolFlowSelectionStore(root);

            // 中文界面时代的较旧快照。
            var olderZh = Selection("2D 游戏 · 稳妥自建（免费开源）", DateTimeOffset.Parse("2026-09-25T08:00:00+00:00"), "中文旧快照的目标");
            store.SelectForInstall(olderZh);

            // 较新的英文快照（英文界面选定产生）——修复前识别不到，会丢恢复入口或误选更旧中文快照。
            var newerEn = Selection("2D game · Safe self-build (free & open-source)", DateTimeOffset.Parse("2026-09-25T09:00:00+00:00"), "target from the newer English snapshot");
            store.SelectForInstall(newerEn);

            var latest = store.FindLatestResumable(ToolFlowResume.IsGodotFlow);
            Assert.NotNull(latest);
            // 必须选最新的正确方案（英文新快照），而不是更旧的中文快照。
            Assert.Equal(newerEn.SubmissionId, latest!.SubmissionId);
            // 业务目标随快照原样保持（不因语言变化被改写）。
            Assert.Equal("target from the newer English snapshot", latest.ProjectGoal);

            // 反向：仅中文快照存在时，识别依然有效（跨语言切换后不失效）。
            Assert.True(ToolFlowResume.IsGodotFlow(olderZh));
            Assert.True(ToolFlowResume.IsGodotFlow(newerEn));
            // 非 Godot 名称仍被排除。
            Assert.False(ToolFlowResume.IsGodotFlow(Selection("Godot 2D 游戏工具流（旧命名）", DateTimeOffset.UtcNow)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Build_DerivesPerItemStates_WithUnverifiedWordingKeptHonest()
    {
        var godot = Guid.NewGuid().ToString("D");
        var krita = Guid.NewGuid().ToString("D");
        var asset = Guid.NewGuid().ToString("D");
        var drawn = Guid.NewGuid().ToString("D");
        var agent = Guid.NewGuid().ToString("D");
        var selection = new ToolFlowSelection
        {
            FlowId = Guid.NewGuid().ToString("D"),
            SubmissionId = Guid.NewGuid().ToString("D"),
            Origin = ToolFlowOrigin.Assistant,
            SelectedAtUtc = DateTimeOffset.Parse("2026-09-24T08:00:00+00:00"),
            FlowName = "2D 游戏 · 稳妥自建（免费开源）",
            ProjectGoal = "做一款 2D 游戏",
            GoalDescription = "做一款 2D 游戏",
            FlowText = "示例方案文本",
            UploadEnabledAtSelection = false,
            Conversation = [],
            Items =
            [
                new() { ItemId = godot, Name = "Godot", Kind = "software", InstallTargetKey = "godot" },
                new() { ItemId = krita, Name = "Krita", Kind = "software", InstallTargetKey = "krita" },
                new() { ItemId = asset, Name = "免费音效素材包（候选）", Kind = "asset", ManualHint = "到素材站下载并核对许可；不需要账号。" },
                new() { ItemId = drawn, Name = "自绘素材路线", Kind = "software", ManualHint = "自己画像素图。" },
                new() { ItemId = agent, Name = "AI Agent（候选）", Kind = "agent", InstallTargetKey = "godot" },
            ],
            Events =
            [
                Event(godot, ToolFlowEventKind.Verified, "安装后验证通过。"),
                Event(agent, ToolFlowEventKind.InstallFailed, "安装已执行，但安装后未检测到目标；需要用户排查。"),
            ],
            ItemMarks =
            [
                new ToolFlowItemMark
                {
                    ItemId = drawn,
                    Kind = ToolFlowItemMark.UserReportedDone,
                    AtUtc = DateTimeOffset.Parse("2026-09-24T09:30:00+00:00"),
                },
            ],
        };

        var view = ToolFlowResume.Build(selection, lastResult: null,
            knownTargets: ["godot", "git", "krita", "audacity"]);

        Assert.Equal(ToolFlowResumeItemState.InstalledOrDetected, State(view, godot));
        Assert.Equal(ToolFlowResumeItemState.PendingAutomatic, State(view, krita));
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, State(view, asset));
        Assert.Equal(ToolFlowResumeItemState.UserReportedDone, State(view, drawn));
        Assert.Equal(ToolFlowResumeItemState.Failed, State(view, agent));

        // 人工项提示来自保存时快照；自报完成必须带「应用未验证」口径；失败项要给出下一步。
        Assert.Contains("到素材站下载并核对许可", Row(view, asset).StatusLine);
        Assert.Equal("到素材站下载并核对许可；不需要账号。", Row(view, asset).ManualHint);
        Assert.Contains("应用未验证", Row(view, drawn).StatusLine);
        Assert.Contains("未成功", Row(view, agent).StatusLine);
        Assert.Contains("下一步", Row(view, agent).StatusLine);
        Assert.Contains("未验证", ToolFlowResume.Label(ToolFlowResumeItemState.UserReportedDone));

        // 本轮 runner 结果优先：刚检测到已安装的项不再显示「待自动处理」。
        var runResult = new ToolFlowInstallResult(
        [
            new ToolFlowInstallItemResult(krita, "Krita", ToolFlowInstallItemStatus.AlreadyInstalled, "已安装，跳过。"),
            new ToolFlowInstallItemResult(krita, "Krita", ToolFlowInstallItemStatus.Installed, "重复结果不应被采用"),
        ]);
        var afterRun = ToolFlowResume.Build(selection, runResult, knownTargets: ["godot", "git", "krita", "audacity"]);
        Assert.Equal(ToolFlowResumeItemState.PendingAutomatic, State(afterRun, krita));   // 歧义结果不解释成成功

        var cleanRun = new ToolFlowInstallResult(
        [
            new ToolFlowInstallItemResult(krita, "Krita", ToolFlowInstallItemStatus.AlreadyInstalled, "已安装，跳过。"),
        ]);
        var cleanView = ToolFlowResume.Build(selection, cleanRun, knownTargets: ["godot", "git", "krita", "audacity"]);
        Assert.Equal(ToolFlowResumeItemState.InstalledOrDetected, State(cleanView, krita));
        Assert.Contains("已检测到安装", Row(cleanView, krita).StatusLine);
    }

    [Fact]
    public void MarkItemUserReportedDone_PersistsIdempotently_WithoutCreatingVerificationOrUploadQualification()
    {
        var root = NewRoot();
        try
        {
            var store = new ToolFlowSelectionStore(root);
            var itemId = Guid.NewGuid().ToString("D");
            var selection = new ToolFlowSelection
            {
                FlowId = Guid.NewGuid().ToString("D"),
                SubmissionId = Guid.NewGuid().ToString("D"),
                Origin = ToolFlowOrigin.Assistant,
                SelectedAtUtc = DateTimeOffset.Parse("2026-09-24T08:00:00+00:00"),
                FlowName = "2D 游戏 · 稳妥自建（免费开源）",
                ProjectGoal = "做一款 2D 游戏",
                GoalDescription = "做一款 2D 游戏",
                FlowText = "示例方案文本",
                UploadEnabledAtSelection = true,
                Conversation = [],
                Items = [new() { ItemId = itemId, Name = "Krita", Kind = "software", InstallTargetKey = "krita" }],
            };
            store.SelectForInstall(selection);

            var marked = store.MarkItemUserReportedDone(selection.SubmissionId, itemId);
            Assert.Single(marked.ItemMarks);
            var reloaded = store.GetBySubmissionId(selection.SubmissionId)!;
            Assert.Single(reloaded.ItemMarks);
            Assert.Equal(ToolFlowItemMark.UserReportedDone, reloaded.ItemMarks[0].Kind);
            Assert.Empty(reloaded.Events);                       // 不写任何「验证」事件
            Assert.True(reloaded.UploadEnabledAtSelection);     // 自报完成不改变既有上报资格，也不生成新的

            // 幂等：重复标记不重复追加。
            var again = store.MarkItemUserReportedDone(selection.SubmissionId, itemId);
            Assert.Single(again.ItemMarks);

            // 不属于该方案的条目要拒绝。
            Assert.Throws<InvalidOperationException>(() =>
                store.MarkItemUserReportedDone(selection.SubmissionId, Guid.NewGuid().ToString("D")));

            // 恢复视图里必须显示为「自报完成、应用未验证」。
            var view = ToolFlowResume.Build(reloaded, lastResult: null, knownTargets: ["krita"]);
            Assert.Equal(ToolFlowResumeItemState.UserReportedDone, view.Rows[0].State);
            Assert.Contains("应用未验证", view.Rows[0].StatusLine);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Build_PendingAutomaticIncludesRetryableFailures_ExcludesVerifiedAndMarkedItems()
    {
        var done = Guid.NewGuid().ToString("D");
        var marked = Guid.NewGuid().ToString("D");
        var failed = Guid.NewGuid().ToString("D");
        var clean = Guid.NewGuid().ToString("D");
        var selection = new ToolFlowSelection
        {
            FlowId = Guid.NewGuid().ToString("D"),
            SubmissionId = Guid.NewGuid().ToString("D"),
            Origin = ToolFlowOrigin.Assistant,
            SelectedAtUtc = DateTimeOffset.Parse("2026-09-24T08:00:00+00:00"),
            FlowName = "2D 游戏 · 稳妥自建（免费开源）",
            ProjectGoal = "做一款 2D 游戏",
            GoalDescription = "做一款 2D 游戏",
            FlowText = "示例方案文本",
            UploadEnabledAtSelection = false,
            Conversation = [],
            Items =
            [
                new() { ItemId = done, Name = "Godot", Kind = "software", InstallTargetKey = "godot" },
                new() { ItemId = marked, Name = "Krita", Kind = "software", InstallTargetKey = "krita" },
                new() { ItemId = failed, Name = "Audacity", Kind = "software", InstallTargetKey = "audacity" },
                new() { ItemId = clean, Name = "Git", Kind = "software", InstallTargetKey = "git" },
            ],
            Events =
            [
                Event(done, ToolFlowEventKind.Verified, "安装后验证通过。"),
                Event(failed, ToolFlowEventKind.InstallFailed, "未确认成功。"),
            ],
            ItemMarks =
            [
                new ToolFlowItemMark
                {
                    ItemId = marked,
                    Kind = ToolFlowItemMark.UserReportedDone,
                    AtUtc = DateTimeOffset.Parse("2026-09-24T09:00:00+00:00"),
                },
            ],
        };

        var view = ToolFlowResume.Build(selection, lastResult: null,
            knownTargets: ["godot", "git", "krita", "audacity"]);

        // 未成功的自动项必须可重试；完成和自报完成的项不会被当作新的安装项。
        Assert.Equal(new[] { failed, clean }, view.PendingAutomaticItems.Select(r => r.ItemId));
        Assert.DoesNotContain(view.PendingAutomaticItems, r => r.ItemId == done);
        Assert.DoesNotContain(view.PendingAutomaticItems, r => r.ItemId == marked);
        Assert.True(Row(view, failed).CanContinueAutomatically);
        Assert.False(Row(view, done).CanContinueAutomatically);
        Assert.False(Row(view, marked).CanContinueAutomatically);
        Assert.Equal(1, view.InstalledOrDetectedCount);
        Assert.Equal(1, view.FailedCount);
        Assert.Equal(1, view.UserReportedDoneCount);
    }

    [Fact]
    public void Handoff_ListsUnfinishedItems_AndSaysUserMayPickAnyAgent_WhenPlanHasNoAgent()
    {
        var godot = Guid.NewGuid().ToString("D");
        var asset = Guid.NewGuid().ToString("D");
        var withoutAgent = new ToolFlowSelection
        {
            FlowId = Guid.NewGuid().ToString("D"),
            SubmissionId = Guid.NewGuid().ToString("D"),
            Origin = ToolFlowOrigin.Assistant,
            SelectedAtUtc = DateTimeOffset.Parse("2026-09-24T08:00:00+00:00"),
            FlowName = "2D 游戏 · 稳妥自建（免费开源）",
            ProjectGoal = "做一款 2D 游戏",
            GoalDescription = "做一款 2D 游戏",
            FlowText = "Godot + Krita + 免费音效",
            UploadEnabledAtSelection = false,
            Conversation = [],
            Items =
            [
                new() { ItemId = godot, Name = "Godot", Kind = "software", InstallTargetKey = "godot" },
                new() { ItemId = asset, Name = "免费音效素材包（候选）", Kind = "asset" },
            ],
            Events = [Event(godot, ToolFlowEventKind.Verified, "安装后验证通过。")],
        };

        var text = ToolFlowAgentHandoff.Build(withoutAgent, result: null);

        Assert.Contains("尚未完成的准备项", text);
        Assert.Contains("免费音效素材包（候选）：尚无验证记录", text);
        Assert.DoesNotContain("Godot：尚无验证记录", text);   // 已验证项不进未完成列表
        Assert.Contains("你可以自行选择任意 AI Agent", text);   // 无 Agent 项：明确可自选，不写死品牌
        foreach (var brand in new[] { "Blender", "Codex", "Claude", "ChatGPT", "Cursor" })
            Assert.DoesNotContain(brand, text);

        // 全部验证通过时，未完成列表明确写「无」。
        var allDone = withoutAgent with
        {
            Events = [Event(godot, ToolFlowEventKind.Verified, "安装后验证通过。"),
                      Event(asset, ToolFlowEventKind.Verified, "用户确认已就绪。")],
        };
        Assert.Contains("- 无：所有项都有已验证的安装记录", ToolFlowAgentHandoff.Build(allDone, result: null));

        // 方案里带 agent 项时，不再出现「可自选」提示（由方案自己给出候选）。
        var withAgent = withoutAgent with
        {
            Items = [.. withoutAgent.Items, new ToolFlowItem { ItemId = Guid.NewGuid().ToString("D"), Name = "AI Agent（候选）", Kind = "agent" }],
        };
        Assert.DoesNotContain("你可以自行选择任意 AI Agent", ToolFlowAgentHandoff.Build(withAgent, result: null));
    }

    [Theory]
    [InlineData(ToolFlowInstallItemStatus.Failed, ToolFlowResumeItemState.Failed)]
    [InlineData(ToolFlowInstallItemStatus.ManualStep, ToolFlowResumeItemState.NeedsUserAssist)]
    public void Build_CurrentUnsuccessfulStatusOverridesOlderVerification(
        ToolFlowInstallItemStatus currentStatus, ToolFlowResumeItemState expectedState)
    {
        var selection = Selection("当前测试", DateTimeOffset.UtcNow);
        var item = selection.Items.Single();
        selection = selection with { Events = [Event(item.ItemId, ToolFlowEventKind.Verified, "旧验证")] };
        var result = new ToolFlowInstallResult([new(item.ItemId, item.Name, currentStatus, "当前未成功")]);

        var view = ToolFlowResume.Build(selection, result, ["godot"]);

        Assert.Equal(expectedState, view.Rows.Single().State);
        Assert.Equal(currentStatus, view.Rows.Single().CurrentRunStatus);
        Assert.Equal(0, view.InstalledOrDetectedCount);
        Assert.Equal(currentStatus == ToolFlowInstallItemStatus.Failed, view.Rows.Single().CanContinueAutomatically);
    }

    [Fact]
    public void Build_CurrentFailureOverridesUserReportedDone()
    {
        var selection = Selection("当前测试", DateTimeOffset.UtcNow);
        var item = selection.Items.Single();
        selection = selection with
        {
            ItemMarks = [new() { ItemId = item.ItemId, Kind = ToolFlowItemMark.UserReportedDone, AtUtc = DateTimeOffset.UtcNow }],
        };
        var result = new ToolFlowInstallResult([new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Failed, "检测失败")]);

        var view = ToolFlowResume.Build(selection, result, ["godot"]);

        Assert.Equal(ToolFlowResumeItemState.Failed, view.Rows.Single().State);
        Assert.Single(view.PendingAutomaticItems);
        Assert.Equal(0, view.UserReportedDoneCount);
    }

    [Theory]
    [InlineData(ToolFlowEventKind.InstallFailed, ToolFlowResumeItemState.Failed)]
    [InlineData(ToolFlowEventKind.DownloadFailed, ToolFlowResumeItemState.Failed)]
    [InlineData(ToolFlowEventKind.InstallSucceeded, ToolFlowResumeItemState.PendingAutomatic)]
    [InlineData(ToolFlowEventKind.DownloadStarted, ToolFlowResumeItemState.PendingAutomatic)]
    [InlineData(ToolFlowEventKind.DownloadSucceeded, ToolFlowResumeItemState.PendingAutomatic)]
    public void Build_LatestEventInvalidatesOldVerification(
        ToolFlowEventKind latestKind, ToolFlowResumeItemState expectedState)
    {
        var selection = Selection("历史测试", DateTimeOffset.UtcNow);
        var item = selection.Items.Single();
        var older = Event(item.ItemId, ToolFlowEventKind.Verified, "旧验证");
        var later = Event(item.ItemId, latestKind, "最新事件") with { AtUtc = older.AtUtc.AddHours(1) };
        // 事件输入乱序也必须按时间确定最近事实。
        selection = selection with { Events = [later, older] };

        var view = ToolFlowResume.Build(selection, null, ["godot"]);

        Assert.Equal(expectedState, view.Rows.Single().State);
        Assert.Equal(0, view.InstalledOrDetectedCount);
        Assert.Single(view.PendingAutomaticItems);
    }

    [Fact]
    public void Build_FailedManualItemHasNoAutomaticRetryOrMisleadingInstruction()
    {
        var selection = Selection("手动测试", DateTimeOffset.UtcNow);
        var item = selection.Items.Single() with { InstallTargetKey = null };
        selection = selection with
        {
            Items = [item], Events = [Event(item.ItemId, ToolFlowEventKind.InstallFailed, "旧失败")],
        };

        var view = ToolFlowResume.Build(selection, null, ["godot"]);

        Assert.Equal(ToolFlowResumeItemState.Failed, view.Rows.Single().State);
        Assert.Empty(view.PendingAutomaticItems);
        Assert.False(view.Rows.Single().CanContinueAutomatically);
        Assert.DoesNotContain("继续自动项", view.Rows.Single().StatusLine);
        Assert.Contains("手动处理", view.Rows.Single().StatusLine);
    }

    [Fact]
    public void Build_AmbiguousCurrentResultsDoNotFallBackToOldSuccess()
    {
        var selection = Selection("歧义测试", DateTimeOffset.UtcNow);
        var item = selection.Items.Single();
        selection = selection with { Events = [Event(item.ItemId, ToolFlowEventKind.Verified, "旧验证")] };
        var result = new ToolFlowInstallResult(
        [
            new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Installed, "结果一"),
            new(item.ItemId, item.Name, ToolFlowInstallItemStatus.Failed, "结果二"),
        ]);

        var view = ToolFlowResume.Build(selection, result, ["godot"]);

        Assert.Equal(ToolFlowResumeItemState.PendingAutomatic, view.Rows.Single().State);
        Assert.Null(view.Rows.Single().CurrentRunStatus);
        Assert.Equal(0, view.InstalledOrDetectedCount);
    }

    [Fact]
    public async Task FreshPreparationKeepsFullPlan_RemovesReusedArchiveFromPending_AndClearsStaleCertainty()
    {
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var selection = Selection("Archives", DateTimeOffset.UtcNow) with { Items = [archive] };
        var oldView = ToolFlowResume.Build(selection, knownTargets: ["7zip"]);
        var detected = await ToolFlowPreparationTests.WinRarService().PrepareAsync(selection.Items, selection.FlowText);
        var merged = ToolFlowResume.ApplyPreparation(oldView, detected);

        Assert.Same(selection, merged.Selection);
        Assert.Single(merged.Rows);
        Assert.Empty(merged.PendingAutomaticItems);
        Assert.True(merged.Rows[0].IsExistingToolReuse);
        Assert.Equal("WinRAR", merged.Rows[0].ExistingToolName);
        Assert.Contains("WinRAR", merged.Rows[0].StatusLine);
        Assert.DoesNotContain("已安装", merged.Rows[0].StatusLine);

        var absent = await new ToolFlowPreparationService(["7zip"], (_, _) => Task.FromResult<InstalledToolEvidence?>(null))
            .PrepareAsync(selection.Items, selection.FlowText);
        var checkedAgain = ToolFlowResume.ApplyPreparation(merged, absent);
        Assert.Equal(ToolFlowResumeItemState.PendingAutomatic, checkedAgain.Rows[0].State);
        Assert.False(checkedAgain.Rows[0].IsExistingToolReuse);
        Assert.Null(checkedAgain.Rows[0].ExistingToolTargetKey);
        Assert.Single(checkedAgain.PendingAutomaticItems);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Execute 7z.exe x assets.7z", false)]
    public void HistoricalReuseOnlySatisfiesTheSameOrdinaryExtractionRequirement(string plan, bool satisfied)
    {
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var selection = Selection("Archives", DateTimeOffset.UtcNow) with
        {
            Items = [archive], FlowText = plan,
            Events = [Event(archive.ItemId, ToolFlowEventKind.Verified, ToolFlowReuseEvidence.WinRarArchiveDetail)],
        };
        var view = ToolFlowResume.Build(selection, knownTargets: ["7zip"]);
        Assert.Equal(satisfied, view.Rows[0].IsExistingToolReuse);
        Assert.Equal(satisfied ? ToolFlowResumeItemState.InstalledOrDetected : ToolFlowResumeItemState.PendingAutomatic, view.Rows[0].State);
        if (satisfied) Assert.Contains("WinRAR", view.Rows[0].StatusLine);
    }

    [Fact]
    public async Task FreshProbeFailureDoesNotPreserveHistoricalVerification()
    {
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var selection = Selection("Archives", DateTimeOffset.UtcNow) with
        {
            Items = [archive], Events = [Event(archive.ItemId, ToolFlowEventKind.Verified, ToolFlowReuseEvidence.WinRarArchiveDetail)],
        };
        var evidence = await new ToolFlowPreparationService(["7zip"], (_, _) => throw new IOException("PRIVATE-PATH"))
            .PrepareAsync(selection.Items, selection.FlowText);
        var view = ToolFlowResume.ApplyPreparation(ToolFlowResume.Build(selection, knownTargets: ["7zip"]), evidence);
        Assert.Equal(ToolFlowResumeItemState.Failed, view.Rows[0].State);
        Assert.False(view.Rows[0].IsExistingToolReuse);
        Assert.Equal(0, view.InstalledOrDetectedCount);
        Assert.Single(view.PendingAutomaticItems);
        Assert.DoesNotContain("PRIVATE-PATH", view.Rows[0].StatusLine);
    }

    [Fact]
    public void MergeIgnoresEvidenceFromAChangedItemAndNeverLosesManualSteps()
    {
        var archive = ToolFlowPreparationTests.Item("7-Zip", "7zip");
        var manual = ToolFlowPreparationTests.Item("Account", null) with { Kind = "service" };
        var selection = Selection("Archives", DateTimeOffset.UtcNow) with { Items = [archive, manual] };
        var view = ToolFlowResume.Build(selection, knownTargets: ["7zip"]);
        var changed = archive with { Name = "7-Zip CLI" };
        var evidence = new ToolFlowPreparation([
            new(changed, new(ToolFlowRequirementKind.ArchiveExtraction), ToolFlowPreparationState.ReusedInstalledTool,
                new("winrar", "WinRAR")),
            new(manual, new(ToolFlowRequirementKind.ExactTarget), ToolFlowPreparationState.NeedsUserAssist),
        ]);
        var merged = ToolFlowResume.ApplyPreparation(view, evidence);
        Assert.Equal(ToolFlowResumeItemState.PendingAutomatic, merged.Rows[0].State);
        Assert.False(merged.Rows[0].IsExistingToolReuse);
        Assert.Equal(ToolFlowResumeItemState.NeedsUserAssist, merged.Rows[1].State);
        Assert.Equal(2, merged.Rows.Count);
    }

    private static ToolFlowExecutionEvent Event(string itemId, ToolFlowEventKind kind, string detail) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        ItemId = itemId,
        Kind = kind,
        AtUtc = DateTimeOffset.Parse("2026-09-24T09:00:00+00:00"),
        Detail = detail,
    };

    private static ToolFlowResumeItemRow Row(ToolFlowResumeView view, string itemId)
        => view.Rows.Single(r => r.ItemId == itemId);

    private static ToolFlowResumeItemState State(ToolFlowResumeView view, string itemId)
        => Row(view, itemId).State;

    private static ToolFlowSelection Selection(string flowName, DateTimeOffset atUtc, string projectGoal = "做一款 2D 游戏") => new()
    {
        FlowId = Guid.NewGuid().ToString("D"),
        SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.Assistant,
        SelectedAtUtc = atUtc,
        FlowName = flowName,
        ProjectGoal = projectGoal,
        GoalDescription = projectGoal,
        FlowText = "示例方案文本",
        UploadEnabledAtSelection = false,
        Conversation = [],
        Items = [new() { ItemId = Guid.NewGuid().ToString("D"), Name = "Godot", Kind = "software", InstallTargetKey = "godot" }],
    };

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "zxai-toolflow-resume-" + Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
