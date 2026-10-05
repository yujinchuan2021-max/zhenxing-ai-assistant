using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>《项目档案》工具组测试（路径注入到临时目录，互不干扰）。</summary>
public class ProjectProfileToolTests : IDisposable
{
    private readonly string _dir;

    public ProjectProfileToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zxai-profile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        ProjectProfileTool.ProfilePathOverride = Path.Combine(_dir, "project-profile.json");
    }

    public void Dispose()
    {
        ProjectProfileTool.ProfilePathOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Save_And_Get_RoundTrip()
    {
        var save = ProjectProfileTool.SaveProjectProfile("goal", "做一款2D小游戏");
        Assert.Contains("已存档", save);

        var profile = ProjectProfileTool.GetProjectProfile();
        Assert.Contains("项目目标", profile);
        Assert.Contains("做一款2D小游戏", profile);
    }

    [Fact]
    public void Save_Merge_PreservesOtherSections()
    {
        ProjectProfileTool.SaveProjectProfile("goal", "做网站");
        ProjectProfileTool.SaveProjectProfile("network", "国内直连，无外网");

        var profile = ProjectProfileTool.GetProjectProfile();
        Assert.Contains("做网站", profile);
        Assert.Contains("国内直连", profile);
    }

    [Fact]
    public void Save_InvalidSection_Rejected()
    {
        var r = ProjectProfileTool.SaveProjectProfile("hacked", "x");
        Assert.Contains("未知 section", r);
    }

    [Fact]
    public void UpdateStage_AdvancesAndRecordsHistory()
    {
        ProjectProfileTool.SaveProjectProfile("goal", "做游戏");
        var r1 = ProjectProfileTool.UpdateWorkflowStage("hardware", "硬件已体检：RX 6800 XT");
        Assert.Contains("硬件体检", r1);

        var r2 = ProjectProfileTool.UpdateWorkflowStage("plan", "方案已定稿");
        Assert.Contains("方案生成", r2);

        var profile = ProjectProfileTool.GetProjectProfile();
        Assert.Contains("方案生成", profile);        // 当前阶段
        Assert.Contains("硬件已体检", profile);       // 流水
    }

    [Fact]
    public void UpdateStage_Invalid_Rejected()
    {
        var r = ProjectProfileTool.UpdateWorkflowStage("launch", "x");
        Assert.Contains("未知阶段", r);
    }

    [Fact]
    public void Get_EmptyProfile_HintsInterview()
    {
        var profile = ProjectProfileTool.GetProjectProfile();
        Assert.Contains("访谈", profile);
    }

    [Fact]
    public void Save_WhitespaceContent_NotSaved()
    {
        var r = ProjectProfileTool.SaveProjectProfile("goal", "   ");
        Assert.Contains("未保存", r);
    }
}
