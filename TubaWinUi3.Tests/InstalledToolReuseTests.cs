using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

public sealed class InstalledToolReuseTests
{
    [Theory]
    [InlineData("7-Zip", "7z.exe", "7-Zip", true)]
    [InlineData("Anything entered by the user", "7zFM.exe", "7-Zip", true)]
    [InlineData("7-Zip", "other.exe", "7-Zip", false)]
    [InlineData("7-Zip", "7z.exe", "Other product", false)]
    [InlineData("7-Zip", "7z.exe", null, false)]
    public void RegistryNameAndPathAreOnlyHints_TheFileIdentityMustMatch(string name, string fileName,
        string? product, bool accepted)
    {
        var path = @"C:\FAKE-APP-CENTER\" + fileName;
        var metadata = new SystemInstaller.ToolAccessMetadata("7zip", "7-Zip", false, ["7z.exe", "7zFM.exe"]);
        var record = new SoftRecord { Name = name, Path = path, Version = "PRIVATE-VERSION", Source = "system" };
        var result = InstalledToolReuse.FindRegisteredPath(metadata, [record], p => [p], _ => product);
        Assert.Equal(accepted ? path : null, result);
    }

    [Fact]
    public void RegisteredDirectoryCanResolveVerifiedRuntimeExecutable_WithNoRegistryChanges()
    {
        var metadata = new SystemInstaller.ToolAccessMetadata("godot", "Godot", true, ["Godot_v*_win64.exe"]);
        var record = new SoftRecord { Name = "My game engine", Path = @"D:\FAKE\Godot", Source = "manual" };
        var executable = @"D:\FAKE\Godot\Godot_v4.5_win64.exe";
        var path = InstalledToolReuse.FindRegisteredPath(metadata, [record], _ => [executable], _ => "Godot Engine");
        Assert.Equal(executable, path);
        Assert.Equal("My game engine", record.Name);
        Assert.Equal(@"D:\FAKE\Godot", record.Path);
        Assert.Equal("manual", record.Source);
    }

    [Fact]
    public void RelativeRecordAndMissingRuntimeNeverCreateEvidence()
    {
        var metadata = new SystemInstaller.ToolAccessMetadata("7zip", "7-Zip", false, ["7z.exe"]);
        Assert.Null(InstalledToolReuse.FindRegisteredPath(metadata,
            [new SoftRecord { Name = "7-Zip", Path = "relative\\7z.exe" }], _ => throw new Exception("Must not expand"), _ => "7-Zip"));
        Assert.Null(InstalledToolReuse.FindRegisteredPath(metadata,
            [new SoftRecord { Name = "7-Zip", Path = @"C:\FAKE\7z.exe" }], _ => [], _ => "7-Zip"));
    }

    [Fact]
    public void ExistingWinRarIsAnAccessProviderButNeverAnAutomaticInstallTarget()
    {
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("winrar", out var winrar));
        Assert.True(winrar.IsGui);
        Assert.Equal(new[] { "WinRAR.exe" }, winrar.ExecutableNames);
        Assert.DoesNotContain(SystemInstaller.KnownTargets, x => x.Equals("winrar", StringComparison.OrdinalIgnoreCase));
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("7zip", out var sevenZip));
        Assert.Equal(new[] { "7zFM.exe" }, sevenZip.ExecutableNames);
    }

    [Theory]
    [InlineData("Claude", true)]
    [InlineData("Claude Desktop", true)]
    [InlineData("claude desktop", true)]
    [InlineData("Claude Code", false)]
    [InlineData("Claude Code Desktop", false)]
    [InlineData("Other product", false)]
    [InlineData(null, false)]
    public void ClaudeDesktopRequiresTheActualDesktopProductIdentity(string? product, bool accepted)
    {
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("claude-desktop", out var metadata));
        var path = @"C:\FAKE\AnthropicClaude\app-1.0\Claude.exe";
        var record = new SoftRecord { Name = "Claude 桌面版", Path = path, Source = "system" };
        var found = InstalledToolReuse.FindRegisteredPath(metadata, [record], p => [p], _ => product);
        Assert.Equal(accepted ? path : null, found);
        Assert.Equal("Claude 桌面版", metadata.Name);
        Assert.True(metadata.IsGui);
    }

    [Theory]
    [InlineData("Claude", true)]
    [InlineData("Claude Desktop", true)]
    [InlineData("Claude 1.0.5", true)]
    [InlineData("Claude Desktop 1.0.5", true)]
    [InlineData("Claude Code", false)]
    [InlineData("Claude Code 1.0.5", false)]
    [InlineData("Claude Code Desktop", false)]
    public void ClaudeOsInstallationAliasesNeverMatchClaudeCode(string display, bool desktop)
    {
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("claude-desktop", out var metadata));
        Assert.Equal(desktop, InstalledToolReuse.MatchesOsDisplayName(metadata, display));
    }

    [Fact]
    public void ClaudeSourceTokenAndUserEnteredNameDoNotEstablishDesktopInstallation()
    {
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("claude-desktop", out var metadata));
        var token = new SoftRecord { Name = "Claude", Path = "winget:Anthropic.Claude", Source = "system" };
        Assert.Null(InstalledToolReuse.FindRegisteredPath(metadata, [token],
            _ => throw new Exception("A source token is not an executable path"), _ => "Claude"));
        Assert.False(SystemInstaller.CanUseOsRegistrationWithoutExecutable("claude-desktop"));
        Assert.True(SystemInstaller.TryGetToolAccessMetadata("claude-code", out var code));
        Assert.False(code.IsGui);
        Assert.False(InstalledToolReuse.MatchesProductIdentity(metadata, "Claude Code"));
        Assert.True(InstalledToolReuse.MatchesProductIdentity(code, "Claude Code"));
        Assert.False(InstalledToolReuse.MatchesProductIdentity(code, "Claude"));
        Assert.False(InstalledToolReuse.MatchesProductIdentity(code, "Claude Desktop"));
        Assert.False(SystemInstaller.CanUseOsRegistrationWithoutExecutable("claude-code"));
    }
}
