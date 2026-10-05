using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>项目轨道库工具：列出/详情/打卡/查询（ZXAI 教学陪跑）。</summary>
public class LearningTracksToolTests : IDisposable
{
    private readonly string _tmpProgress = Path.Combine(Path.GetTempPath(), $"zxai-progress-{Guid.NewGuid():N}.json");

    public LearningTracksToolTests()
    {
        LearningTracksTool.ProgressPathOverride = _tmpProgress;
    }

    public void Dispose()
    {
        LearningTracksTool.ProgressPathOverride = null;
        try { File.Delete(_tmpProgress); } catch { }
    }

    [Fact]
    public void List_AllTracks_Contains26Directions()
    {
        var text = LearningTracksTool.GetProjectTracks("");
        Assert.Contains("26 个方向", text);
        Assert.Contains("game2d-godot", text);
        Assert.Contains("ai-chatbot", text);
        Assert.Contains("miniprogram", text);
    }

    [Fact]
    public void Detail_ById_ContainsMilestonesToolsAndVideo()
    {
        var text = LearningTracksTool.GetProjectTracks("game2d-godot");
        Assert.Contains("里程碑", text);
        Assert.Contains("工具链", text);
        Assert.Contains("bilibili.com/video/BV1684y147h4", text);
    }

    [Fact]
    public void Detail_ByFuzzyKeyword_Matches()
    {
        var text = LearningTracksTool.GetProjectTracks("小程序");
        Assert.Contains("微信小程序", text);
        Assert.Contains("BV1f4kHBaE2C", text);
    }

    [Fact]
    public void Detail_Unknown_ReportsAvailableIds()
    {
        var text = LearningTracksTool.GetProjectTracks("量子计算机维修");
        Assert.Contains("没有匹配", text);
        Assert.Contains("game3d-godot", text);
    }

    [Fact]
    public void UpdateProgress_ThenReadBack()
    {
        var r1 = LearningTracksTool.UpdateLearningProgress("game2d-godot", 0, "示例跑通了");
        Assert.Contains("已打卡", r1);
        Assert.Contains("下一步", r1);

        var r2 = LearningTracksTool.GetLearningProgress();
        Assert.Contains("game2d-godot", r2);
        Assert.Contains("1 步", r2);
    }

    [Fact]
    public void UpdateProgress_CompletingAll_ShowsCongrats()
    {
        // game2d-godot 有 5 个里程碑：全打完应提示完成
        for (var i = 0; i < 5; i++)
            LearningTracksTool.UpdateLearningProgress("game2d-godot", i, "");
        var r = LearningTracksTool.UpdateLearningProgress("game2d-godot", 4, "重复打卡不重复计");
        Assert.Contains("5/5", r);
        Assert.Contains("全部完成", r);
    }

    [Fact]
    public void UpdateProgress_UnknownTrack_ReportsError()
    {
        var r = LearningTracksTool.UpdateLearningProgress("no-such-track", 0, "");
        Assert.Contains("未找到轨道", r);
    }
}
