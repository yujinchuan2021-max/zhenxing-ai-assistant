using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

/// <summary>Fake entry evidence only; never queries winget or reads/writes the user's registration store.</summary>
public sealed class AppCenterRegisteredPackageTests
{
    [Fact]
    public void PackageIdAloneDoesNotMakeAMissingEntryUninstallable()
    {
        var item = Record();
        AppCenterService.RefreshLocalEntry(item, _ => false, _ => false);
        Assert.False(item.CanUninstall);
        Assert.False(item.CanOpenTool);
        Assert.Equal("missing", item.StatusKey);
        Assert.Equal(nameof(AppCenterPrimaryAction.CheckInstallation), item.PrimaryAction);
        Assert.True(item.PrimaryEnabled);
        Assert.False(item.ShowPackageCheck); // The primary action already performs the check.
        Assert.True(item.ShowSystemApps);
        Assert.True(item.ShowRemove);
        Assert.Null(item.PackageInspection);
        Assert.Equal("winget:Anthropic.Claude", item.Path);
    }

    [Fact]
    public void AVerifiedExactPackageCanBeUninstalledEvenIfItsLaunchEntryWasNotFound()
    {
        var item = Record();
        item.PackageInspection = new(RegisteredPackageState.Installed, "Exact package evidence");
        AppCenterService.RefreshLocalEntry(item, _ => false, _ => false);
        Assert.True(item.CanUninstall);
        Assert.False(item.CanOpenTool);
        Assert.Equal("installed", item.StatusKey);
        Assert.Equal("Exact package evidence", item.StatusLabel);
    }

    [Theory]
    [InlineData((int)RegisteredPackageState.RecordedPathMissing)]
    [InlineData((int)RegisteredPackageState.IdentityMismatch)]
    [InlineData((int)RegisteredPackageState.Unmatched)]
    [InlineData((int)RegisteredPackageState.CheckFailed)]
    public void UnconfirmedOrMissingPackageKeepsRecordAndNeverOffersUninstall(int state)
    {
        var item = Record();
        item.PackageInspection = new((RegisteredPackageState)state, "Keep the original record");
        AppCenterService.RefreshLocalEntry(item, _ => false, _ => false);
        Assert.False(item.CanUninstall);
        Assert.True(item.ShowRemove);
        Assert.True(item.ShowSystemApps);
        Assert.Equal("Keep the original record", item.StatusLabel);
        Assert.Equal("original-record", item.RecordId);
        Assert.Equal("Anthropic.Claude", item.WingetId);
    }

    [Fact]
    public void ExistingExecutableDoesNotProveTheRecordedPackageIdentity()
    {
        var item = Record();
        item.Path = @"C:\isolated\Claude.exe";
        AppCenterService.RefreshLocalEntry(item, _ => true, p => p == @"C:\isolated");
        Assert.True(item.CanOpenTool);
        Assert.True(item.ShowPackageCheck);
        Assert.False(item.CanUninstall);
        item.PackageInspection = new(RegisteredPackageState.IdentityMismatch, "Different installation identity");
        AppCenterService.RefreshLocalEntry(item, _ => true, p => p == @"C:\isolated");
        Assert.True(item.CanOpenTool);
        Assert.False(item.CanUninstall);
        Assert.True(item.ShowSystemApps);
    }

    [Fact]
    public void SandboxAndManualRegistrationRemainSeparateFromWingetChecks()
    {
        var item = new AppCenterItem { Path = @"C:\isolated\sandbox\app", ManagedSandbox = true };
        AppCenterService.RefreshLocalEntry(item, _ => false, _ => true);
        Assert.True(item.CanUninstall);
        Assert.False(item.ShowPackageCheck);
        Assert.False(item.ShowSystemApps);
        var manual = new AppCenterItem { RecordId = "manual", Path = @"C:\isolated\missing.exe", ShowRemove = true };
        AppCenterService.RefreshLocalEntry(manual, _ => false, _ => false);
        Assert.False(manual.CanUninstall);
        Assert.True(manual.ShowRemove);
        Assert.False(manual.ShowPackageCheck);
        Assert.Equal(nameof(AppCenterPrimaryAction.None), manual.PrimaryAction);
    }

    private static AppCenterItem Record() => new()
    {
        RecordId = "original-record", Name = "Claude 桌面版", Path = "winget:Anthropic.Claude",
        WingetId = "Anthropic.Claude", ShowRemove = true, AllowLaunch = true,
    };
}
