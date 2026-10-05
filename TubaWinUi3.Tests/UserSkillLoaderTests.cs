using System.Reflection;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>用户自定义技能加载器测试（并入技能注册表集合同步串行）。</summary>
[Collection("AgentSkillRegistry")]
public class UserSkillLoaderTests
{
    private static readonly FieldInfo SkillsField =
        typeof(AgentSkillRegistry).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void ClearRegistry() => ((List<AgentSkill>)SkillsField.GetValue(null)!).Clear();

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zxai-skilltest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FromJson_Valid_ParsesAllFields()
    {
        var skill = UserSkillLoader.FromJson(
            """{"id":"my_skill","displayName":"我的技能","glyph":"\uE721","description":"测试简介","systemPromptFragment":"这是指导片段","triggerKeywords":["甲","乙"]}""");

        Assert.NotNull(skill);
        Assert.Equal("my_skill", skill!.Id);
        Assert.Equal("我的技能", skill.DisplayName);
        Assert.Equal("\uE721", skill.Glyph);
        Assert.Equal("测试简介", skill.Description);
        Assert.Equal("这是指导片段", skill.SystemPromptFragment);
        Assert.Equal(2, skill.TriggerKeywords.Length);
    }

    [Fact]
    public void FromJson_MissingRequired_ReturnsNull()
    {
        Assert.Null(UserSkillLoader.FromJson("""{"id":"x","displayName":"缺片段"}"""));
        Assert.Null(UserSkillLoader.FromJson("""{"displayName":"缺id","description":"d","systemPromptFragment":"f"}"""));
        Assert.Null(UserSkillLoader.FromJson("not json at all"));
    }

    [Fact]
    public void FromJson_PascalCase_Accepted()
    {
        var skill = UserSkillLoader.FromJson(
            """{"Id":"p_skill","DisplayName":"P","Description":"d","SystemPromptFragment":"f"}""");
        Assert.NotNull(skill);
        Assert.Equal("p_skill", skill!.Id);
    }

    [Fact]
    public void FromJson_IllegalIdChars_Sanitized()
    {
        var skill = UserSkillLoader.FromJson(
            """{"id":"bad id/path!","displayName":"n","description":"d","systemPromptFragment":"f"}""");
        Assert.NotNull(skill);
        Assert.Equal("badidpath", skill!.Id);
    }

    [Fact]
    public void LoadAll_ScansDir_RegistersValid_SkipsBrokenAndDuplicate()
    {
        var dir = MakeTempDir();
        UserSkillLoader.SkillsDirOverride = dir;
        ClearRegistry();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.skill.json"),
                """{"id":"user_a","displayName":"A","description":"d","systemPromptFragment":"f"}""");
            File.WriteAllText(Path.Combine(dir, "broken.skill.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "note.txt"), "非技能文件应被忽略");

            var loaded = UserSkillLoader.LoadAll();
            Assert.Equal(1, loaded);
            Assert.NotNull(AgentSkillRegistry.Find("user_a"));

            // 重复加载幂等
            Assert.Equal(0, UserSkillLoader.LoadAll());
        }
        finally
        {
            UserSkillLoader.SkillsDirOverride = null;
            ClearRegistry();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ImportFromFile_CopiesAndLoads()
    {
        var srcDir = MakeTempDir();
        var skillsDir = MakeTempDir();
        UserSkillLoader.SkillsDirOverride = skillsDir;
        ClearRegistry();
        try
        {
            var src = Path.Combine(srcDir, "imported.skill.json");
            File.WriteAllText(src,
                """{"id":"imported_skill","displayName":"导入的技能","description":"d","systemPromptFragment":"f","triggerKeywords":["导入"]}""");

            var (ok, msg) = UserSkillLoader.ImportFromFile(src);
            Assert.True(ok, msg);
            Assert.NotNull(AgentSkillRegistry.Find("imported_skill"));
            Assert.True(File.Exists(Path.Combine(skillsDir, "imported.skill.json")));
        }
        finally
        {
            UserSkillLoader.SkillsDirOverride = null;
            ClearRegistry();
            Directory.Delete(srcDir, true);
            Directory.Delete(skillsDir, true);
        }
    }

    [Fact]
    public void ImportFromFile_InvalidFile_Rejected()
    {
        var srcDir = MakeTempDir();
        var skillsDir = MakeTempDir();
        UserSkillLoader.SkillsDirOverride = skillsDir;
        ClearRegistry();
        try
        {
            var src = Path.Combine(srcDir, "bad.skill.json");
            File.WriteAllText(src, "{}");
            var (ok, msg) = UserSkillLoader.ImportFromFile(src);
            Assert.False(ok);
            Assert.Contains("有效", msg);
        }
        finally
        {
            UserSkillLoader.SkillsDirOverride = null;
            ClearRegistry();
            Directory.Delete(srcDir, true);
            Directory.Delete(skillsDir, true);
        }
    }
}
