using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>Pure data tests: no system probing, process launches, COM, real settings or desktop writes.</summary>
public sealed class ToolFlowToolAccessTests
{
    private const string Root = @"C:\isolated-tool-access";

    [Fact]
    public void OrdinaryArchiveReuseOpensActualProviderWithoutChangingTheProposal()
    {
        var original = new ToolFlowItem { ItemId = "archive", Name = "7-Zip", Kind = "software", InstallTargetKey = "7zip" };
        var row = new ToolFlowPreparationItem(original, new(ToolFlowRequirementKind.ArchiveExtraction),
            ToolFlowPreparationState.ReusedInstalledTool, new("winrar", "WinRAR"));
        var access = ToolFlowToolAccess.AccessItem(row);
        Assert.Equal("winrar", access.InstallTargetKey);
        Assert.Equal(original.ItemId, access.ItemId);
        Assert.Equal("7zip", original.InstallTargetKey);
        Assert.Same(original, ToolFlowToolAccess.AccessItem(row with
            { Requirement = new(ToolFlowRequirementKind.SevenZipCli) }));
        Assert.Same(original, ToolFlowToolAccess.AccessItem(row with
            { State = ToolFlowPreparationState.DetectionFailed }));
    }

    [Theory]
    [InlineData("godot", "godot.exe")]
    [InlineData("GODOT", "Godot_v4.5-stable_win64.exe")]
    [InlineData("pixelorama", "Pixelorama.exe")]
    [InlineData("tiled", "tiled.exe")]
    [InlineData("audacity", "audacity.exe")]
    [InlineData("blender", "blender.exe")]
    [InlineData("vscode", "Code.exe")]
    [InlineData("unityhub", "Unity Hub.exe")]
    [InlineData("claude-desktop", "Claude.exe")]
    [InlineData("winrar", "WinRAR.exe")]
    [InlineData("7zip", "7zFM.exe")]
    public void KnownGuiRequiresItsExpectedExistingExecutable(string key, string fileName)
    {
        var entry = Assert.IsType<ToolFlowToolAccessEntry>(Candidate(key, fileName));
        Assert.True(entry.IsGui);
        Assert.Equal(key.Trim().ToLowerInvariant(), entry.TargetKey);
        Assert.Equal(Path.Combine(Root, fileName), entry.ExecutablePath);
        Assert.Equal(Root, entry.DirectoryPath);
    }

    [Theory]
    [InlineData("godot", "setup.exe")]
    [InlineData("godot", "Godot_v4.5-stable_win64_console.exe")]
    [InlineData("vscode", "code.cmd")]
    [InlineData("unityhub", "Unity.exe")]
    [InlineData("winrar", "rar.exe")]
    [InlineData("7zip", "7z.exe")]
    [InlineData("made-up-model-target", "godot.exe")]
    public void WrongOrUnknownTargetCannotBecomeALaunchEntry(string key, string fileName)
        => Assert.Null(Candidate(key, fileName));

    [Theory]
    [InlineData("godot.exe")]
    [InlineData(@"C:godot.exe")]
    [InlineData(@"\\server\share\godot.exe")]
    [InlineData(@"C:\tools\%MODEL_COMMAND%\godot.exe")]
    [InlineData(@"C:\tools:stream\godot.exe")]
    [InlineData("C:\\tools\\\"godot.exe")]
    public void RelativeRemoteOrShellExpansionPathsAreRejected(string candidate)
        => Assert.Null(ToolFlowToolAccess.ResolveCandidate("godot", candidate, _ => true, _ => true));

    [Fact]
    public async Task ModelPathAndManualItemDoNotTriggerSystemLookup()
    {
        var item = new ToolFlowItem
        {
            ItemId = "manual", Name = @"C:\model-output\godot.exe", Kind = "software",
            SourceUrl = "file:///C:/model-output/godot.exe", DownloadUrl = "file:///C:/model-output/godot.exe",
        };
        Assert.Null(await ToolFlowToolAccess.ResolveAsync(item));
        Assert.Null(await ToolFlowToolAccess.ResolveAsync(item with { InstallTargetKey = @"C:\model-output\godot.exe" }));
    }

    [Theory]
    [InlineData("codex", "codex.exe")]
    [InlineData("claude-code", "claude.exe")]
    [InlineData("opencode", "opencode.exe")]
    [InlineData("python", "python.exe")]
    [InlineData("dotnet", "dotnet.exe")]
    [InlineData("node", "node.exe")]
    [InlineData("vllm", "vllm.exe")]
    public void CliAndPipTargetsHaveLocationButCannotLaunchAsGui(string key, string fileName)
    {
        var entry = Assert.IsType<ToolFlowToolAccessEntry>(Candidate(key, fileName));
        Assert.False(entry.IsGui);
        Assert.NotNull(entry.DirectoryPath);
        Assert.Throws<InvalidOperationException>(() => Validate(entry, entry, requireGui: true));
        Assert.Equal(entry, Validate(entry, entry, requireGui: false));
    }

    [Fact]
    public void DirectoryOnlyEvidenceAllowsLocationButNotLaunchOrShortcut()
    {
        var entry = Assert.IsType<ToolFlowToolAccessEntry>(ToolFlowToolAccess.ResolveCandidate("godot", Root,
            _ => false, path => path == Root));
        Assert.Null(entry.ExecutablePath);
        Assert.Equal(entry, ToolFlowToolAccess.ValidateActionTarget(entry, entry, false,
            _ => false, path => path == Root));
        Assert.Throws<InvalidOperationException>(() => ToolFlowToolAccess.ValidateActionTarget(entry, entry, true,
            _ => false, path => path == Root));
    }

    [Fact]
    public void AChangedOrCallerSuppliedPathFailsFreshValidation()
    {
        var current = Assert.IsType<ToolFlowToolAccessEntry>(Candidate("godot", "godot.exe"));
        var forged = current with { ExecutablePath = @"C:\model-output\godot.exe" };
        Assert.Throws<InvalidOperationException>(() => Validate(forged, current));
        Assert.Throws<InvalidOperationException>(() => Validate(current with { DirectoryPath = @"C:\model-output" }, current));
        Assert.Throws<InvalidOperationException>(() => Validate(current with { TargetKey = "blender" }, current));
        Assert.Throws<InvalidOperationException>(() => Validate(current, current with { ExecutablePath = Path.Combine(Root, "setup.exe") }));
    }

    [Fact]
    public void ForgedGuiFlagAndRemovedExecutableFailValidation()
    {
        var cli = Assert.IsType<ToolFlowToolAccessEntry>(Candidate("codex", "codex.exe"));
        Assert.Throws<InvalidOperationException>(() => Validate(cli with { IsGui = true }, cli));
        var gui = Assert.IsType<ToolFlowToolAccessEntry>(Candidate("godot", "godot.exe"));
        Assert.Throws<InvalidOperationException>(() => ToolFlowToolAccess.ValidateActionTarget(gui, gui, true,
            _ => false, path => path == Root));
        Assert.Throws<InvalidOperationException>(() => Validate(gui, null));
    }

    [Fact]
    public void DisplayNameAlwaysComesFromFixedMetadata()
    {
        var current = Assert.IsType<ToolFlowToolAccessEntry>(Candidate("godot", "godot.exe"));
        var validated = Validate(current with { Name = "model suggested shortcut name" }, current);
        Assert.Equal("Godot", validated.Name);
    }

    [Fact]
    public void ConflictingDesktopLinksAndUnreadableEntriesArePreserved()
    {
        var target = Path.Combine(Root, "godot.exe");
        var existing = new Dictionary<string, (bool, string?, string?)>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(Root, "Godot.lnk")] = (true, @"C:\another-app\godot.exe", ""),
            [Path.Combine(Root, "Godot (2).lnk")] = (true, null, null),
        };
        var chosen = WindowsSearchIndexService.SelectNonConflictingDesktopShortcutPath(Root, "Godot", target,
            path => existing.TryGetValue(path, out var link) ? link : (false, null, null));
        Assert.Equal(Path.Combine(Root, "Godot (3).lnk"), chosen);
        Assert.Equal(2, existing.Count);
    }

    [Fact]
    public void MatchingDesktopTargetIsReusedOnlyWhenArgumentsAreEmpty()
    {
        var target = Path.Combine(Root, "godot.exe");
        var first = Path.Combine(Root, "Godot.lnk");
        Assert.Equal(first, WindowsSearchIndexService.SelectNonConflictingDesktopShortcutPath(Root, "Godot", target,
            path => path == first ? (true, target, "") : (false, null, null)));
        Assert.Equal(Path.Combine(Root, "Godot (2).lnk"),
            WindowsSearchIndexService.SelectNonConflictingDesktopShortcutPath(Root, "Godot", target,
                path => path == first ? (true, target, "--some-other-project") : (false, null, null)));
    }

    private static ToolFlowToolAccessEntry? Candidate(string key, string fileName)
    {
        var path = Path.Combine(Root, fileName);
        return ToolFlowToolAccess.ResolveCandidate(key, path,
            value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase),
            value => string.Equals(value, Root, StringComparison.OrdinalIgnoreCase));
    }

    private static ToolFlowToolAccessEntry Validate(ToolFlowToolAccessEntry requested,
        ToolFlowToolAccessEntry? current, bool requireGui = true)
        => ToolFlowToolAccess.ValidateActionTarget(requested, current, requireGui,
            path => path == current?.ExecutablePath, path => path == current?.DirectoryPath);
}
