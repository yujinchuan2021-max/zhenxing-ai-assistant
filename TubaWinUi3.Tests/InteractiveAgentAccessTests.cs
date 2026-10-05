using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

public sealed class InteractiveAgentAccessTests
{
    [Theory]
    [InlineData("codex", "codex.exe")]
    [InlineData("opencode", "opencode.exe")]
    [InlineData("claude-code", "claude.exe")]
    public void FixedAgentLaunchUsesChosenProjectWithoutArguments(string key, string executable)
    {
        var entry = new ToolFlowToolAccessEntry(key, "Agent", @"C:\Tools\" + executable, @"C:\Tools", false);
        var start = ToolFlowToolAccess.CreateInteractiveAgentStartInfo(entry, @"C:\Projects\中文 & project", _ => true);
        Assert.Equal(entry.ExecutablePath, start.FileName);
        Assert.Equal(@"C:\Projects\中文 & project", start.WorkingDirectory);
        Assert.Empty(start.Arguments);
        Assert.Empty(start.ArgumentList);
        Assert.True(start.UseShellExecute);
    }

    [Theory]
    [InlineData("nodejs", "node.exe")]
    [InlineData("ffmpeg", "ffmpeg.exe")]
    [InlineData("codex", "cmd.exe")]
    [InlineData("opencode", "opencode.cmd")]
    [InlineData("claude-code", "claude.ps1")]
    public void SdkAndScriptPathsCannotBecomeInteractiveAgents(string key, string executable)
    {
        var entry = new ToolFlowToolAccessEntry(key, "tool", @"C:\Tools\" + executable, @"C:\Tools", false);
        Assert.False(ToolFlowToolAccess.IsInteractiveAgent(entry));
        Assert.Throws<InvalidOperationException>(() =>
            ToolFlowToolAccess.CreateInteractiveAgentStartInfo(entry, @"C:\Projects", _ => true));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share")]
    [InlineData(@"C:\Projects\bad:stream")]
    public void ProjectMustBeAnExistingLocalDirectory(string path)
    {
        var entry = new ToolFlowToolAccessEntry("codex", "Agent", @"C:\Tools\codex.exe", @"C:\Tools", false);
        Assert.Throws<InvalidOperationException>(() =>
            ToolFlowToolAccess.CreateInteractiveAgentStartInfo(entry, path, _ => true));
        Assert.Throws<InvalidOperationException>(() =>
            ToolFlowToolAccess.CreateInteractiveAgentStartInfo(entry, @"C:\Missing", _ => false));
    }
}
