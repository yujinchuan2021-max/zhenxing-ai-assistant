using System.Text.Json;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowLearningGuideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "zxai-learning-guide-test-" + Guid.NewGuid().ToString("N"));

    public ToolFlowLearningGuideTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ConfirmedGodot2D_UsesOnlyTextMilestones_AndReportsActualStatus()
    {
        var path = WriteTracks("装好 Godot 打开示例", "做出会走会跳的小人");
        var selection = Selection("我想做一款 2D 游戏", "godot");
        var result = new ToolFlowInstallResult([
            new(selection.Items[0].ItemId, "Godot", ToolFlowInstallItemStatus.Installed, "检测到目标"),
        ]);

        var card = ToolFlowLearningGuide.Build(selection, result, path);

        Assert.Contains("装好 Godot 打开示例", card);
        Assert.Contains("做出会走会跳的小人", card);
        Assert.Contains("目录更新日期：2026-09-19", card);
        Assert.Contains("本轮安装后检测到目标", card);
        Assert.Contains("不代表已核对本机 Godot 的实际版本", card);
        Assert.DoesNotContain("https://", card);
        Assert.DoesNotContain("BV1684y147h4", card);
    }

    [Fact]
    public void UnconfirmedGoalOrMissingGodot_DoesNotOfferGodotGuide()
    {
        var path = WriteTracks("装好 Godot 打开示例");

        Assert.Contains("暂无可确认匹配", ToolFlowLearningGuide.Build(
            Selection("我要做 3D 游戏", "godot"), null, path));
        Assert.Contains("暂无可确认匹配", ToolFlowLearningGuide.Build(
            Selection("我想做一款 2D 游戏", "git"), null, path));
        Assert.Contains("暂无可确认匹配", ToolFlowLearningGuide.Build(
            Selection("我想做一款游戏", "godot"), null, path));
        Assert.Contains("暂无可确认匹配", ToolFlowLearningGuide.Build(
            Selection("我不做 2D 游戏", "godot"), null, path));
    }

    [Fact]
    public void NoMatch_UsesCurrentLanguageAfterFirstCall()
    {
        var languageField = typeof(LocalizationService).GetField("_currentLanguage",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previous = languageField.GetValue(null);
        try
        {
            var selection = Selection("做 3D 游戏", "godot");
            languageField.SetValue(null, LocalizationService.ChineseLanguage);
            Assert.Contains("暂无可确认匹配", ToolFlowLearningGuide.Build(selection, null));
            languageField.SetValue(null, LocalizationService.EnglishLanguage);
            Assert.Contains("No confirmed local", ToolFlowLearningGuide.Build(selection, null));
            languageField.SetValue(null, LocalizationService.ChineseLanguage);
            Assert.Contains("暂无可确认匹配", ToolFlowLearningGuide.Build(selection, null));
        }
        finally
        {
            languageField.SetValue(null, previous);
        }
    }

    [Fact]
    public void EmbeddedLinksOrBvidInsideMilestone_AreNotDisplayed()
    {
        var path = WriteTracks(
            "安全的本地文字里程碑",
            "视频 https://www.bilibili.com/video/BV1684y147h4/",
            "搜索 BV1684y147h4 来看视频",
            "短链接 b23.tv/abc123");

        var card = ToolFlowLearningGuide.Build(Selection("做二维游戏", "godot"), null, path);

        Assert.Contains("安全的本地文字里程碑", card);
        Assert.DoesNotContain("https://", card);
        Assert.DoesNotContain("BV1684y147h4", card);
        Assert.DoesNotContain("b23.tv", card);
        Assert.Contains("尚无本轮安装结果", card);
    }

    [Fact]
    public void MissingOrMalformedCatalog_IsAnExplicitGap()
    {
        var selection = Selection("做一个 2D 游戏", "godot");
        var missing = Path.Combine(_root, "missing.json");
        var malformed = Path.Combine(_root, "malformed.json");
        File.WriteAllText(malformed, "{ invalid");

        Assert.Contains("目录缺失", ToolFlowLearningGuide.Build(selection, null, missing));
        Assert.Contains("目录缺失", ToolFlowLearningGuide.Build(selection, null, malformed));
    }

    private string WriteTracks(params string[] milestones)
    {
        var path = Path.Combine(_root, "project-tracks.json");
        var fakeCatalog = new
        {
            updatedAt = "2026-09-19",
            tracks = new object[]
            {
                new { id = "game3d-godot", milestones = new[] { "3D 不该出现" } },
                new
                {
                    id = "game2d-godot",
                    milestones,
                    videos = new[] { new { bvid = "BV1684y147h4", url = "https://example.test/video" } },
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(fakeCatalog));
        return path;
    }

    private static ToolFlowSelection Selection(string goal, string target) => new()
    {
        FlowId = Guid.NewGuid().ToString("D"),
        SubmissionId = Guid.NewGuid().ToString("D"),
        Origin = ToolFlowOrigin.User,
        SelectedAtUtc = DateTimeOffset.UtcNow,
        FlowName = "游戏工具流",
        ProjectGoal = goal,
        GoalDescription = goal,
        FlowText = "用户确认后的工具流。",
        UploadEnabledAtSelection = false,
        Conversation = [],
        Items = [new ToolFlowItem
        {
            ItemId = Guid.NewGuid().ToString("D"),
            Name = "Godot",
            Kind = "software",
            InstallTargetKey = target,
        }],
    };
}
