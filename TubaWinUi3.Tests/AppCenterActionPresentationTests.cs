using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

/// <summary>Only in-memory states and injected file evidence; no folders, processes, installs or real user configuration.</summary>
public sealed class AppCenterActionPresentationTests
{
    [Theory]
    [InlineData("NotInstalled", false, false, false, "Download", "download")]
    [InlineData("Installed", true, false, false, "Open", "installed")]
    [InlineData("Installed", true, true, false, "Update", "update")]
    [InlineData("Failed", false, false, false, "Retry", "download")]
    [InlineData("Failed", true, true, false, "Retry", "update")]
    [InlineData("Installed", false, false, false, "Download", "download")]
    public void MainActionUsesAvailableEntryAndCurrentState(string status, bool entry,
        bool update, bool pending, string action, string filter)
    {
        var view = AppCenterActionPresentation.Cloud(Enum.Parse<AppCenterToolStatus>(status), entry, false, update, pending);
        Assert.Equal(action, view.PrimaryAction.ToString());
        Assert.Equal(filter, view.StatusKey);
        Assert.True(view.PrimaryEnabled);
        Assert.False(view.CanRemoveManaged);
    }

    [Theory]
    [InlineData("Downloading")]
    [InlineData("Installing")]
    [InlineData("Updating")]
    [InlineData("Removing")]
    public void InFlightOperationsDisableRepeatedAndDestructiveActions(string status)
    {
        var view = AppCenterActionPresentation.Cloud(Enum.Parse<AppCenterToolStatus>(status), true, true, true, false, 20);
        Assert.False(view.PrimaryEnabled);
        Assert.False(view.CanCreateShortcut);
        Assert.False(view.CanOpenLocation);
        Assert.False(view.CanRemoveManaged);
        Assert.Equal("busy", view.StatusKey);
    }

    [Fact]
    public void RunningToolPendingUpdateKeepsLocationButNeverClaimsUpdateComplete()
    {
        var view = AppCenterActionPresentation.Cloud(AppCenterToolStatus.PendingUpdate, true, true, true, true);
        Assert.Equal("update", view.StatusKey);
        Assert.False(view.PrimaryEnabled);
        Assert.True(view.CanOpenLocation);
        Assert.False(view.CanRemoveManaged);
        Assert.Contains("退出工具", view.StatusLabel);
    }

    [Fact]
    public void MissingArchitecturePackageOffersSourceOnlyAndDoesNotOfferFakeRetry()
    {
        var view = AppCenterActionPresentation.Cloud(AppCenterToolStatus.Failed, false, false, false, false,
            canDownload: false, hasHomepage: true);
        Assert.Equal(AppCenterPrimaryAction.OpenWebsite, view.PrimaryAction);
        Assert.Equal("查看来源", view.PrimaryLabel);
        var noSource = AppCenterActionPresentation.Cloud(AppCenterToolStatus.Unsupported, false, false, false, false,
            canDownload: false, hasHomepage: false);
        Assert.False(noSource.PrimaryEnabled);
        Assert.Equal(AppCenterPrimaryAction.None, noSource.PrimaryAction);
    }

    [Fact]
    public void LegacyPackageStaysOpenableAndCannotBeRemovedAsManaged()
    {
        var view = AppCenterActionPresentation.Cloud(AppCenterToolStatus.Installed, true, false, false, false, canDownload: false);
        Assert.Equal(AppCenterPrimaryAction.Open, view.PrimaryAction);
        Assert.False(view.CanRemoveManaged);
        Assert.True(AppCenterActionPresentation.Cloud(AppCenterToolStatus.Installed, true, true, false, false).CanRemoveManaged);
    }

    [Fact]
    public void DirectoryRegistrationNeverGuessesAnExecutable()
    {
        var entry = AppCenterActionPresentation.ResolveLocalEntry(@"C:\isolated\registered", true,
            _ => true, _ => true);
        Assert.Null(entry.Executable);
        Assert.Equal(@"C:\isolated\registered", entry.Directory);
    }

    [Fact]
    public void OnlyExistingExplicitExeCanProvideLaunchAndShortcut()
    {
        const string exe = @"C:\isolated\tool\Tool.exe";
        var entry = AppCenterActionPresentation.ResolveLocalEntry(exe, true, path => path == exe,
            path => path == @"C:\isolated\tool");
        Assert.Equal(exe, entry.Executable);
        Assert.Null(AppCenterActionPresentation.ResolveLocalEntry(exe, false, _ => true,
            path => path == @"C:\isolated\tool").Executable);
        Assert.Null(AppCenterActionPresentation.ResolveLocalEntry(exe, true, _ => false, _ => false).Executable);
        Assert.Null(AppCenterActionPresentation.ResolveLocalEntry(@"C:\isolated\tool\Tool.cmd", true, _ => true,
            path => path == @"C:\isolated\tool").Executable);
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData(@"C:tool.exe")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData(@"C:\tools\%COMMAND%\tool.exe")]
    [InlineData(@"C:\tools:stream\tool.exe")]
    [InlineData("C:\\tools\\\"tool.exe")]
    public void UntrustedRemoteOrShellExpandedPathsCannotOpen(string path)
    {
        Assert.Null(AppCenterActionPresentation.NormalizeLocalPath(path));
        Assert.Null(AppCenterActionPresentation.ResolveLocalEntry(path, true, _ => true, _ => true).Executable);
    }

    [Fact]
    public void ManagedEnvironmentRootAndTraversalCannotBecomeRemovalTargets()
    {
        const string root = @"C:\isolated\environments";
        Assert.False(AppCenterActionPresentation.IsSandboxChild(root, root));
        Assert.False(AppCenterActionPresentation.IsSandboxChild(root + @"-other\tool", root));
        Assert.False(AppCenterActionPresentation.IsSandboxChild(root + @"\..\outside", root));
        Assert.True(AppCenterActionPresentation.IsSandboxChild(root + @"\tool", root));
    }

    [Fact]
    public void SearchAndStateFilterUseInstalledSnapshotWithoutNetwork()
    {
        var item = new AppCenterItem { Name = "Godot", Detail = "2D game engine", Version = "4.5", StatusKey = "installed", StatusLabel = "已安装" };
        Assert.True(AppCenterActionPresentation.Matches(item, "installed", "GAME"));
        Assert.True(AppCenterActionPresentation.Matches(item, "all", "4.5"));
        Assert.False(AppCenterActionPresentation.Matches(item, "download", "Godot"));
        Assert.False(AppCenterActionPresentation.Matches(item, "all", "Unknown"));
    }
}
