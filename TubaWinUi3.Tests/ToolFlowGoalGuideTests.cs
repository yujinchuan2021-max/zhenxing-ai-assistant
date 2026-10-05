using System.Reflection;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowGoalGuideTests
{
    [Fact]
    public void RunningLaterItemPreservesEarlierFailureAndDoesNotDuplicateTheRunningStep()
    {
        var guide = ToolFlowGoalGuide.Create(View(
            Entry("engine", "Engine", "software", state: ToolFlowResumeItemState.Failed, automatic: true),
            Entry("agent", "Agent", "software", state: ToolFlowResumeItemState.PendingAutomatic, automatic: true),
            Entry("account", "Account", "account")));
        var running = guide.WithActiveItem("agent", "Preparing Agent");
        Assert.Equal("agent", Assert.Single(running.CurrentStep!.Rows).ItemId);
        Assert.Equal("Preparing Agent", running.CurrentStep.Hint);
        Assert.Contains(running.LaterSteps, step => step.Rows.Any(row => row.ItemId == "engine") &&
            step.State == ToolFlowGoalStepState.Failed);
        var rows = new[] { running.CurrentStep }.Concat(running.LaterSteps).Concat(running.ReadySteps)
            .SelectMany(step => step.Rows).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(3, rows.Select(row => row.ItemId).Distinct().Count());
    }

    [Fact]
    public void FailedAutomaticStepComesBeforeOtherAutomaticAndManualSteps()
    {
        var view = View(
            Entry("account", "Creative account", "account", hint: "Sign in to the creative service"),
            Entry("editor", "Editor", "software", state: ToolFlowResumeItemState.PendingAutomatic, automatic: true),
            Entry("engine", "Engine", "software", state: ToolFlowResumeItemState.Failed, automatic: true));

        var guide = ToolFlowGoalGuide.Create(view);

        Assert.Equal(view.Selection.ProjectGoal, guide.Goal);
        Assert.Equal("engine", Assert.Single(guide.CurrentStep!.Rows).ItemId);
        Assert.Equal(ToolFlowGoalStepState.Failed, guide.CurrentStep.State);
        Assert.False(guide.CurrentStep.CanConfirmManual);
        Assert.True(guide.HasPendingAutomatic);
        Assert.Equal(new[] { "editor", "account" }, guide.LaterSteps.Select(step => step.Rows[0].ItemId));
        Assert.Contains("3", guide.Summary);
        Assert.DoesNotContain("/", guide.Summary);
    }

    [Fact]
    public void SameServiceWebsiteAccountAndSubscriptionFormOneStepButAssetsStaySeparate()
    {
        var view = View(
            Entry("site", "Suno website", "service", hint: "Open the service", source: "https://suno.com/"),
            Entry("account", "Suno account", "account", hint: "Create a Suno account", source: "https://www.suno.com/login"),
            Entry("rights", "Suno subscription", "subscription", hint: "Check whether the project needs commercial rights", source: "https://suno.com/subscribe"),
            Entry("assets", "Reference audio", "asset", hint: "Prepare reference audio", source: "https://suno.com/"));

        var guide = ToolFlowGoalGuide.Create(view);

        Assert.Equal(new[] { "site", "account", "rights" }, guide.CurrentStep!.Rows.Select(row => row.ItemId));
        Assert.Contains("Check whether", guide.CurrentStep.Hint);
        Assert.Contains("Create a Suno account", guide.CurrentStep.Hint);
        Assert.Contains("Suno", guide.CurrentStep.Title);
        Assert.Equal("assets", Assert.Single(Assert.Single(guide.LaterSteps).Rows).ItemId);
        Assert.Contains("2", guide.Summary);
        Assert.True(guide.CurrentStep.CanConfirmManual);
        Assert.False(guide.HasPendingAutomatic);
    }

    [Fact]
    public void ServiceNameDoesNotOverrideDifferentExplicitHosts()
    {
        var guide = ToolFlowGoalGuide.Create(View(
            Entry("site", "Suno website", "service", source: "https://suno.com/"),
            Entry("other", "Suno account", "account", source: "https://different.example.com/")));

        Assert.Single(guide.CurrentStep!.Rows);
        Assert.Single(Assert.Single(guide.LaterSteps).Rows);
    }

    [Fact]
    public void AssetNameCannotJoinAnAccountEvenWhenItsBroadKindIsService()
    {
        var guide = ToolFlowGoalGuide.Create(View(
            Entry("account", "Suno account", "account", source: "https://suno.com/"),
            Entry("asset", "Suno reference asset", "service", source: "https://suno.com/")));

        Assert.Equal("account", Assert.Single(guide.CurrentStep!.Rows).ItemId);
        Assert.Equal("asset", Assert.Single(Assert.Single(guide.LaterSteps).Rows).ItemId);
    }

    [Fact]
    public void SharedTargetAndLimitedRecognizedNamesCanConnectMissingWebsiteLinks()
    {
        var target = ToolFlowGoalGuide.Create(View(
            Entry("site", "Example studio", "service", source: "https://studio.example.com/", target: "studio"),
            Entry("account", "Example login", "account", target: "studio")));
        Assert.Equal(2, target.CurrentStep!.Rows.Count);

        var known = ToolFlowGoalGuide.Create(View(
            Entry("site", "Suno website", "service", source: "https://suno.com/"),
            Entry("account", "Suno 账号", "account")));
        Assert.Equal(2, known.CurrentStep!.Rows.Count);

        var unknown = ToolFlowGoalGuide.Create(View(
            Entry("first", "Creative account", "account"),
            Entry("second", "Creative account", "account")));
        Assert.Single(unknown.CurrentStep!.Rows);
        Assert.Single(unknown.LaterSteps);
    }

    [Theory]
    [InlineData("Premium subscription", "Check the project's usage terms", false)]
    [InlineData("Subscription（可选）", "Check commercial terms", true)]
    [InlineData("Subscription", "Optional: upgrade when needed", true)]
    [InlineData("Subscription", "按需配置", true)]
    [InlineData("Subscription", "This is not optional", false)]
    [InlineData("Subscription", "不可选，需核对使用条件", false)]
    public void OnlyExplicitOptionalTextMakesARequirementNonBlocking(string name, string hint, bool optional)
    {
        var guide = ToolFlowGoalGuide.Create(View(Entry("rights", name, "subscription", hint: hint)));

        Assert.Equal(optional, guide.CurrentStep is null);
        Assert.Equal(optional ? 1 : 0, guide.LaterSteps.Count);
        var step = guide.CurrentStep ?? guide.LaterSteps.Single();
        Assert.Equal(optional, step.IsOptional);
    }

    [Fact]
    public void UnknownSubscriptionNeedsARequirementCheckWithoutInventingPaymentOrOptionality()
    {
        var guide = ToolFlowGoalGuide.Create(View(Entry("rights", "Suno subscription", "subscription")));

        Assert.NotNull(guide.CurrentStep);
        Assert.False(guide.CurrentStep.IsOptional);
        Assert.Contains("订阅要求", guide.CurrentStep.Title);
        Assert.Contains("确认方案是否要求订阅", guide.CurrentStep.Hint);
        Assert.DoesNotContain("购买", guide.CurrentStep.Title + guide.CurrentStep.Hint);
    }

    [Fact]
    public void OptionalAutomaticItemsRemainAvailableLaterAndDoNotBlockStartingPractice()
    {
        var guide = ToolFlowGoalGuide.Create(View(
            Entry("editor", "Optional editor", "software", state: ToolFlowResumeItemState.PendingAutomatic, automatic: true),
            Entry("engine", "Engine", "software", state: ToolFlowResumeItemState.InstalledOrDetected)));

        Assert.Null(guide.CurrentStep);
        Assert.False(guide.HasPendingAutomatic);
        Assert.True(Assert.Single(guide.LaterSteps).HasAutomaticItems);
        Assert.Single(guide.ReadySteps);
    }

    [Fact]
    public void OptionalSubscriptionDoesNotJoinAndBlockTheRequiredAccount()
    {
        var guide = ToolFlowGoalGuide.Create(View(
            Entry("account", "Suno account", "account", source: "https://suno.com/"),
            Entry("rights", "Suno optional subscription", "subscription", source: "https://suno.com/")));

        Assert.Equal("account", Assert.Single(guide.CurrentStep!.Rows).ItemId);
        Assert.Equal("rights", Assert.Single(Assert.Single(guide.LaterSteps).Rows).ItemId);
    }

    [Fact]
    public void UserReportsAreHandledWithoutBeingRewrittenAsVerifiedEvidence()
    {
        var view = View(Entry("account", "Model account", "account", hint: "Configure the model",
            state: ToolFlowResumeItemState.UserReportedDone));
        var before = view.Rows[0];

        var guide = ToolFlowGoalGuide.Create(view);

        Assert.Null(guide.CurrentStep);
        Assert.Empty(guide.LaterSteps);
        var step = Assert.Single(guide.ReadySteps);
        Assert.Equal(ToolFlowGoalStepState.UserConfirmed, step.State);
        Assert.False(step.CanConfirmManual);
        Assert.Same(before, Assert.Single(step.Rows));
        Assert.Equal(ToolFlowResumeItemState.UserReportedDone, step.Rows[0].State);
        Assert.Contains("尚未验证", step.Hint);
        Assert.DoesNotContain("项目已完成", guide.Stage + guide.Summary);
    }

    [Fact]
    public void MixedServiceGroupRequestsOnlyRemainingActionsAndKeepsItsStableIdentity()
    {
        var view = View(
            Entry("account", "Suno account", "account", hint: "Create the account", source: "https://suno.com/",
                state: ToolFlowResumeItemState.UserReportedDone),
            Entry("rights", "Suno subscription", "subscription", hint: "Check commercial rights", source: "https://suno.com/"));
        var pending = ToolFlowGoalGuide.Create(view).CurrentStep!;

        Assert.DoesNotContain("Create the account", pending.Hint);
        Assert.Contains("Check commercial rights", pending.Hint);
        Assert.Equal(2, pending.Rows.Count);
        Assert.Equal(ToolFlowResumeItemState.UserReportedDone, pending.Rows[0].State);

        var handledView = view with { Rows = view.Rows.Select(row => row with { State = ToolFlowResumeItemState.UserReportedDone }).ToArray() };
        var handled = Assert.Single(ToolFlowGoalGuide.Create(handledView).ReadySteps);
        Assert.Equal(pending.Id, handled.Id);
        Assert.Equal(ToolFlowGoalStepState.UserConfirmed, handled.State);
    }

    [Fact]
    public void PresentationPreservesExistingReuseEvidenceAndDoesNotMutateSelectionOrRows()
    {
        var view = View(Entry("archive", "7-Zip", "software", state: ToolFlowResumeItemState.InstalledOrDetected));
        var reuse = view.Rows[0] with { IsExistingToolReuse = true, ExistingToolName = "WinRAR",
            ExistingToolTargetKey = "winrar", StatusLine = "Ordinary extraction reuses WinRAR; 7-Zip was not installed." };
        view = view with { Rows = [reuse] };
        var items = view.Selection.Items.ToArray();

        var guide = ToolFlowGoalGuide.Create(view);

        Assert.Same(reuse, Assert.Single(Assert.Single(guide.ReadySteps).Rows));
        Assert.Contains("reuses WinRAR", guide.ReadySteps[0].Hint);
        Assert.Equal(items, view.Selection.Items);
        Assert.Equal("archive", guide.ReadySteps[0].Rows[0].ItemId);
        Assert.Empty(view.Selection.ItemMarks);
        Assert.Empty(view.Selection.Events);
    }

    [Fact]
    public void RunningStageUsesReportedActivityWithoutChangingUnderlyingReadiness()
    {
        var view = View(Entry("engine", "Engine", "software", state: ToolFlowResumeItemState.PendingAutomatic, automatic: true));
        var idle = ToolFlowGoalGuide.Create(view);
        var running = ToolFlowGoalGuide.Create(view, running: true, activeStep: "Checking the engine installation");

        Assert.Equal(idle.CurrentStep!.Id, running.CurrentStep!.Id);
        Assert.Equal(ToolFlowGoalStepState.Running, running.CurrentStep.State);
        Assert.Equal("Checking the engine installation", running.CurrentStep.Hint);
        Assert.Equal(ToolFlowResumeItemState.PendingAutomatic, running.CurrentStep.Rows[0].State);
        Assert.DoesNotContain("%", running.Stage + running.Summary);
    }

    [Theory]
    [InlineData("http://suno.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("https://user:password@suno.com/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://localhost/")]
    [InlineData("https://studio.local/")]
    [InlineData("https://suno.com:8443/")]
    [InlineData("https://suno.com\\@example.com/")]
    public void UnsafeSourceUrlsAreNeverExposedOrTrustedForNameBasedGrouping(string source)
    {
        var guide = ToolFlowGoalGuide.Create(View(
            Entry("site", "Suno website", "service", source: source),
            Entry("account", "Suno account", "account", source: "https://suno.com/")));

        Assert.Null(guide.CurrentStep!.SourceUrl);
        Assert.Single(guide.CurrentStep.Rows);
        Assert.Single(guide.LaterSteps);
    }

    [Fact]
    public async Task LanguageSwitchChangesGeneratedTextButNotGoalRowsOrStepIdentity()
    {
        var field = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        var localizer = WinUI3Localizer.Localizer.Get();
        var previousLocalizerLanguage = localizer.GetCurrentLanguage();
        try
        {
            var view = View(Entry("model", "External Agent", "model", hint: "Configure a model"));
            await localizer.SetLanguage(LocalizationService.ChineseLanguage);
            field.SetValue(null, LocalizationService.ChineseLanguage);
            var zh = ToolFlowGoalGuide.Create(view);
            await localizer.SetLanguage(LocalizationService.EnglishLanguage);
            field.SetValue(null, LocalizationService.EnglishLanguage);
            var en = ToolFlowGoalGuide.Create(view);
            Assert.Contains("配置模型", zh.CurrentStep!.Title);
            Assert.Contains("Configure a model", en.CurrentStep!.Title);
            Assert.Equal(zh.CurrentStep.Id, en.CurrentStep.Id);
            Assert.Equal(zh.Goal, en.Goal);
            Assert.Same(zh.CurrentStep.Rows[0], en.CurrentStep.Rows[0]);
        }
        finally
        {
            try { await localizer.SetLanguage(previousLocalizerLanguage); }
            finally { field.SetValue(null, previous); }
        }
    }

    [Fact]
    public void EmptyListKeepsTheOriginalGoalAndDoesNotClaimReadiness()
    {
        var view = View();
        var guide = ToolFlowGoalGuide.Create(view);
        Assert.Equal("  Make a playable project  ", guide.Goal);
        Assert.Null(guide.CurrentStep);
        Assert.Empty(guide.ReadySteps);
        Assert.Empty(guide.LaterSteps);
        Assert.Contains("还没有准备清单", guide.Summary);
    }

    private sealed record Fixture(ToolFlowItem Item, ToolFlowResumeItemState State, bool Automatic);

    private static Fixture Entry(string id, string name, string kind, string? hint = null,
        string? source = null, string? target = null,
        ToolFlowResumeItemState state = ToolFlowResumeItemState.NeedsUserAssist, bool automatic = false) =>
        new(new() { ItemId = id, Name = name, Kind = kind, ManualHint = hint, SourceUrl = source,
            InstallTargetKey = target }, state, automatic);

    private static ToolFlowResumeView View(params Fixture[] entries)
    {
        var selection = new ToolFlowSelection
        {
            FlowId = "original-flow", SubmissionId = "original-submission", Origin = ToolFlowOrigin.User,
            SelectedAtUtc = DateTimeOffset.Parse("2026-09-24T08:00:00+00:00"), FlowName = "Original plan",
            ProjectGoal = "  Make a playable project  ", GoalDescription = "Original goal", FlowText = "Original plan text",
            UploadEnabledAtSelection = false, Conversation = [], Items = entries.Select(entry => entry.Item).ToList(),
        };
        var rows = entries.Select(entry => new ToolFlowResumeItemRow
        {
            ItemId = entry.Item.ItemId, Name = entry.Item.Name, Kind = entry.Item.Kind, State = entry.State,
            StatusLine = "Original evidence for " + entry.Item.ItemId, ManualHint = entry.Item.ManualHint,
            CanContinueAutomatically = entry.Automatic,
        }).ToArray();
        return new(selection, rows, rows.Where(row => row.CanContinueAutomatically).ToArray(),
            rows.Count(row => row.State == ToolFlowResumeItemState.InstalledOrDetected),
            rows.Count(row => row.State == ToolFlowResumeItemState.UserReportedDone),
            rows.Count(row => row.State == ToolFlowResumeItemState.Failed),
            rows.Count(row => row.State == ToolFlowResumeItemState.NeedsUserAssist));
    }
}
