using System.Text.Json;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;

namespace TubaWinUi3.Tests;

/// <summary>In-memory delivery evidence only: no discovery, COM, desktop writes, processes, credentials or network.</summary>
public sealed class ToolFlowPostInstallDeliveryTests
{
    private const string Root = @"C:\isolated-post-install";
    private const string Desktop = @"C:\isolated-desktop";

    [Theory]
    [InlineData(ToolFlowInstallItemStatus.Installed)]
    [InlineData(ToolFlowInstallItemStatus.AlreadyInstalled)]
    public async Task VerifiedDesktopInstallationAutomaticallyCreatesAnIcon(ToolFlowInstallItemStatus status)
    {
        var item = Item("godot");
        var entry = Entry("godot", "godot.exe");
        var shortcut = Path.Combine(Desktop, "Godot.lnk");
        var calls = 0;
        var service = Service(entry, (candidate, ct) =>
        {
            calls++;
            Assert.Equal("Godot", candidate.Name);
            Assert.Equal(entry.ExecutablePath, candidate.ExecutablePath);
            return Task.FromResult(shortcut);
        }, path => path == entry.ExecutablePath || path == shortcut);

        var result = await service.DeliverAsync(item, Installation(item, status));

        Assert.Equal(ToolFlowPostInstallDeliveryKind.DesktopShortcutReady, result.Kind);
        Assert.Equal(shortcut, result.ShortcutPath);
        Assert.Equal("godot", result.TargetKey);
        Assert.Null(result.CliCommand);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(Root, result.Message);
    }

    [Fact]
    public async Task ReusedArchiveToolDeliversTheActualProviderWithoutChangingThePlan()
    {
        var item = Item("7zip");
        var entry = Entry("winrar", "WinRAR.exe");
        var shortcut = Path.Combine(Desktop, "WinRAR.lnk");
        string? lookup = null;
        var service = new ToolFlowPostInstallDelivery((actual, ct) =>
        {
            lookup = actual.InstallTargetKey;
            Assert.Equal(item.ItemId, actual.ItemId);
            return Task.FromResult<ToolFlowToolAccessEntry?>(entry);
        }, (actual, ct) => Task.FromResult(shortcut),
            path => path == entry.ExecutablePath || path == shortcut, path => path == Root);
        var installation = Installation(item, ToolFlowInstallItemStatus.ReusedInstalledTool) with
        { ExistingToolTargetKey = "winrar", ExistingToolName = "WinRAR" };

        var result = await service.DeliverAsync(item, installation);

        Assert.Equal("winrar", lookup);
        Assert.Equal("winrar", result.TargetKey);
        Assert.Equal("WinRAR", result.Name);
        Assert.Equal("7zip", item.InstallTargetKey);
        Assert.Equal(ToolFlowPostInstallDeliveryKind.DesktopShortcutReady, result.Kind);
    }

    [Fact]
    public async Task RepeatedDeliveryReusesAMatchingLinkAndPreservesAnUnrelatedLink()
    {
        var entry = Entry("godot", "godot.exe");
        var first = Path.Combine(Desktop, "Godot.lnk");
        var second = Path.Combine(Desktop, "Godot (2).lnk");
        var links = new Dictionary<string, (bool, string?, string?)>(StringComparer.OrdinalIgnoreCase)
        { [first] = (true, @"C:\other\godot.exe", "") };
        var service = Service(entry, (actual, ct) =>
        {
            var path = WindowsSearchIndexService.SelectNonConflictingDesktopShortcutPath(Desktop,
                actual.Name, actual.ExecutablePath!,
                value => links.TryGetValue(value, out var link) ? link : (false, null, null));
            links.TryAdd(path, (true, actual.ExecutablePath, ""));
            return Task.FromResult(path);
        }, path => path == entry.ExecutablePath || links.ContainsKey(path));
        var item = Item("godot");
        var installation = Installation(item, ToolFlowInstallItemStatus.Installed);

        Assert.Equal(second, (await service.DeliverAsync(item, installation)).ShortcutPath);
        Assert.Equal(second, (await service.DeliverAsync(item, installation)).ShortcutPath);
        Assert.Equal(2, links.Count);
        Assert.Equal(@"C:\other\godot.exe", links[first].Item2);
    }

    [Theory]
    [InlineData("codex", "codex.exe", "")]
    [InlineData("opencode", "opencode.exe", "")]
    [InlineData("claude-code", "claude.exe", "")]
    [InlineData("python", "python.exe", " --version")]
    [InlineData("node", "node.exe", " --version")]
    [InlineData("dotnet", "dotnet.exe", " --info")]
    [InlineData("ffmpeg", "ffmpeg.exe", " -version")]
    [InlineData("vllm", "vllm.exe", " --help")]
    public async Task CliDeliveryUsesTheActualExecutableWithoutRelyingOnPath(string key, string executable, string arguments)
    {
        var entry = Entry(key, executable);
        var service = Service(entry, (_, _) => throw new Xunit.Sdk.XunitException("CLI must not create a desktop icon"));
        var item = Item(key);

        var result = await service.DeliverAsync(item, Installation(item, ToolFlowInstallItemStatus.Installed));

        Assert.Equal(ToolFlowPostInstallDeliveryKind.CliReady, result.Kind);
        Assert.Equal("& '" + entry.ExecutablePath + "'" + arguments, result.CliCommand);
        Assert.NotNull(result.CliGuidance);
        Assert.Null(result.ShortcutPath);
        Assert.DoesNotContain(Root, result.Message);
    }

    [Fact]
    public void CliPathsAreQuotedAsLiteralTextEvenWithAnApostropheAndShellMetacharacters()
    {
        var directory = @"C:\isolated O'Brien $(never) & tools";
        var path = Path.Combine(directory, "codex.exe");
        var entry = Assert.IsType<ToolFlowToolAccessEntry>(ToolFlowToolAccess.ResolveCandidate("codex", path,
            value => value == path, value => value == directory));

        Assert.True(ToolFlowToolAccess.TryGetCliLaunchInstruction(entry, value => value == path,
            value => value == directory, out var instruction));
        Assert.Equal("& 'C:\\isolated O''Brien $(never) & tools\\codex.exe'", instruction.Command);
        Assert.DoesNotContain("-Command", instruction.Command);
    }

    [Theory]
    [InlineData("codex", "setup.exe", false)]
    [InlineData("codex", "codex.cmd", false)]
    [InlineData("codex", "codex.exe", true)]
    [InlineData("unknown", "codex.exe", false)]
    [InlineData("figma", "Figma.exe", false)]
    public void CliGuidanceRejectsWrongTargetsWrongExecutablesAndForgedGuiFlags(string key, string executable, bool gui)
    {
        var entry = new ToolFlowToolAccessEntry(key, "generated-name", Path.Combine(Root, executable), Root, gui);
        Assert.False(ToolFlowToolAccess.TryGetCliLaunchInstruction(entry, _ => true, _ => true, out _));
    }

    [Fact]
    public void MissingCliExecutableOrDirectoryDoesNotProduceACopyableCommand()
    {
        var entry = Entry("codex", "codex.exe");
        Assert.False(ToolFlowToolAccess.TryGetCliLaunchInstruction(entry, _ => false, _ => true, out _));
        Assert.False(ToolFlowToolAccess.TryGetCliLaunchInstruction(entry, _ => true, _ => false, out _));
        Assert.False(ToolFlowToolAccess.TryGetCliLaunchInstruction(entry with { ExecutablePath = null },
            _ => false, _ => true, out _));
    }

    [Theory]
    [InlineData(ToolFlowInstallItemStatus.Failed)]
    [InlineData(ToolFlowInstallItemStatus.ManualStep)]
    public async Task UnfinishedItemsNeverDiscoverSoftwareOrWriteTheDesktop(ToolFlowInstallItemStatus status)
    {
        var service = new ToolFlowPostInstallDelivery((_, _) => throw new Xunit.Sdk.XunitException("Unexpected discovery"),
            (_, _) => throw new Xunit.Sdk.XunitException("Unexpected shortcut"), _ => false, _ => false);
        var item = Item("godot");
        Assert.Equal(ToolFlowPostInstallDeliveryKind.Unavailable,
            (await service.DeliverAsync(item, Installation(item, status))).Kind);
    }

    [Fact]
    public async Task MismatchedRowAndUnknownTargetNeverTriggerDiscovery()
    {
        var calls = 0;
        var service = new ToolFlowPostInstallDelivery((_, _) =>
        { calls++; return Task.FromResult<ToolFlowToolAccessEntry?>(null); }, fileExists: _ => false, directoryExists: _ => false);
        var item = Item("godot");
        Assert.Equal(ToolFlowPostInstallDeliveryKind.Unavailable,
            (await service.DeliverAsync(item, Installation(item, ToolFlowInstallItemStatus.Installed) with { ItemId = "other" })).Kind);
        var unknown = Item(@"C:\generated\godot.exe");
        Assert.Equal(ToolFlowPostInstallDeliveryKind.Unavailable,
            (await service.DeliverAsync(unknown, Installation(unknown, ToolFlowInstallItemStatus.Installed))).Kind);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDirectoryOnlyEntryKeepsTheInstallationSuccessful(bool directoryOnly)
    {
        var item = Item("godot");
        var installation = Installation(item, ToolFlowInstallItemStatus.Installed);
        var entry = directoryOnly ? Entry("godot", "godot.exe") with { ExecutablePath = null } : null;
        var service = Service(entry, (_, _) => throw new Xunit.Sdk.XunitException("No executable"), _ => false);

        Assert.Equal(ToolFlowPostInstallDeliveryKind.Unavailable, (await service.DeliverAsync(item, installation)).Kind);
        Assert.Equal(ToolFlowInstallItemStatus.Installed, installation.Status);
    }

    [Fact]
    public async Task ShortcutFailureDoesNotChangeInstallationStatusOrExposeAnExceptionPath()
    {
        var item = Item("godot");
        var installation = Installation(item, ToolFlowInstallItemStatus.Installed);
        var service = Service(Entry("godot", "godot.exe"), (_, _) =>
            throw new IOException(@"C:\private-user\desktop cannot be written"));

        var result = await service.DeliverAsync(item, installation);

        Assert.Equal(ToolFlowPostInstallDeliveryKind.Failed, result.Kind);
        Assert.Equal(ToolFlowInstallItemStatus.Installed, installation.Status);
        Assert.Null(result.ShortcutPath);
        Assert.DoesNotContain("private-user", result.Message);
    }

    [Fact]
    public async Task ACreatorReturningANonexistentLinkDoesNotClaimAnIconWasCreated()
    {
        var entry = Entry("godot", "godot.exe");
        var item = Item("godot");
        var service = Service(entry, (_, _) => Task.FromResult(Path.Combine(Desktop, "Godot.lnk")));
        Assert.Equal(ToolFlowPostInstallDeliveryKind.Failed,
            (await service.DeliverAsync(item, Installation(item, ToolFlowInstallItemStatus.Installed))).Kind);
    }

    [Fact]
    public async Task CancellationBeforeDeliveryDoesNotDiscoverOrWriteAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ToolFlowPostInstallDelivery((_, _) => throw new Xunit.Sdk.XunitException("Unexpected discovery"));
        var item = Item("godot");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeliverAsync(item,
            Installation(item, ToolFlowInstallItemStatus.Installed), cancellation.Token));
    }

    [Fact]
    public void SerializationExcludesAllLocalPathsAndCliInstructions()
    {
        var result = new ToolFlowPostInstallDeliveryResult("id", "Godot", ToolFlowPostInstallDeliveryKind.DesktopShortcutReady, "Ready")
        {
            TargetKey = "godot", ShortcutPath = @"C:\private\Godot.lnk",
            CliCommand = "& 'C:\\private\\codex.exe'", CliGuidance = "local-only guidance",
        };
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("ShortcutPath", json);
        Assert.DoesNotContain("CliCommand", json);
        Assert.DoesNotContain("CliGuidance", json);
        Assert.Contains("godot", json);
    }

    private static ToolFlowItem Item(string key) => new() { ItemId = "item", Name = "model display name", Kind = "software", InstallTargetKey = key };
    private static ToolFlowInstallItemResult Installation(ToolFlowItem item, ToolFlowInstallItemStatus status)
        => new(item.ItemId, item.Name, status, "installed-result");
    private static ToolFlowToolAccessEntry Entry(string key, string executable)
        => Assert.IsType<ToolFlowToolAccessEntry>(ToolFlowToolAccess.ResolveCandidate(key, Path.Combine(Root, executable),
            value => value == Path.Combine(Root, executable), value => value == Root));
    private static ToolFlowPostInstallDelivery Service(ToolFlowToolAccessEntry? entry,
        Func<ToolFlowToolAccessEntry, CancellationToken, Task<string>> shortcut,
        Func<string, bool>? fileExists = null)
        => new((_, _) => Task.FromResult(entry), shortcut, fileExists ?? (value => value == entry?.ExecutablePath), value => value == Root);
}
