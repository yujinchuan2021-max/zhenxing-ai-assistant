using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A04 回归】Git 技能导入的路径安全校验。
/// 审计复现：`https://example.invalid/team/..` 尾段经 GetFileNameWithoutExtension 得到 "."，
/// 目标等于 Skills 根目录，原实现在克隆前先 Directory.Delete(target, true) 整个技能库。
/// 本测试只做纯字符串/路径解析，不写盘、不克隆。
/// </summary>
public class UserSkillImportSecurityTests
{
    [Theory]
    [InlineData("https://example.invalid/team/..", false)]           // 审计复现用例：拒绝
    [InlineData("https://example.invalid/team/.", false)]
    [InlineData("https://example.invalid/team/", true)]              // 尾段 team（URL 尾斜杠被修剪）
    [InlineData("https://example.invalid/team/valid-skill", true)]
    [InlineData("https://example.invalid/team/valid_skill.git", true)]
    [InlineData("https://example.invalid/team/中文技能", false)]      // 非 ASCII 拒绝
    [InlineData("https://example.invalid/team/a b", false)]          // 空格拒绝
    [InlineData("https://example.invalid/team/a*b", false)]          // 非法字符拒绝
    [InlineData("git@github.com:user/my-skills.git", true)]
    public void TryComputeRepoName_ValidatesStrictly(string url, bool expected)
    {
        var ok = UserSkillLoader.TryComputeRepoName(url, out var name, out var error);
        Assert.Equal(expected, ok);
        if (expected) Assert.False(string.IsNullOrWhiteSpace(name));
        else Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryResolveSafeTarget_AllowsSimpleName_RejectsEscape()
    {
        // 合法：普通子目录（解析为 Skills 下的绝对路径）
        Assert.True(UserSkillLoader.TryResolveSafeTarget("valid-skill", out var target, out _));
        Assert.EndsWith("valid-skill", target);

        // 防御纵深：即使名字校验被绕过，逃逸路径也必须被拒
        Assert.False(UserSkillLoader.TryResolveSafeTarget(@"..\evil", out _, out _));
        Assert.False(UserSkillLoader.TryResolveSafeTarget("..", out _, out _));
        Assert.False(UserSkillLoader.TryResolveSafeTarget(@"sub\..\..\evil", out _, out _));
    }
}
