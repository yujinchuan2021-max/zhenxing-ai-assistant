using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

/// <summary>In-memory command receipts only. Never invokes winget, touches software records or probes the OS.</summary>
public sealed class RegisteredPackageOperationsTests
{
    private const string Id = "Anthropic.Claude";
    private static (int, string) Present => (0, "Name                Id                 Version Source\nClaude              Anthropic.Claude   1.0     winget");
    private static (int, string) Missing => (RegisteredPackageOperations.NoApplicationsFound, "Localized text is irrelevant");

    [Theory]
    [InlineData((int)LocalPackagePresence.Present, (int)RegisteredPackageState.IdentityMismatch)]
    [InlineData((int)LocalPackagePresence.RecordedPathMissing, (int)RegisteredPackageState.RecordedPathMissing)]
    [InlineData((int)LocalPackagePresence.Unknown, (int)RegisteredPackageState.Unmatched)]
    [InlineData((int)LocalPackagePresence.CheckFailed, (int)RegisteredPackageState.CheckFailed)]
    public async Task NoMatchRequiresIndependentEvidenceAndNeverStartsAnUninstall(int presence, int state)
    {
        var fake = new Fake([( "list", Missing )], (LocalPackagePresence)presence);
        var result = await fake.Service.UninstallAsync(Id, CancellationToken.None);
        Assert.Equal((RegisteredPackageState)state, result.Inspection.State);
        Assert.False(result.ConfirmedRemoved);
        Assert.False(result.UninstallStarted);
        Assert.False(result.Inspection.CanUninstall);
        Assert.Equal(new[] { "list" }, fake.Calls);
        Assert.Equal(1, fake.LocalChecks);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1978335196)]
    [InlineData(5)]
    public async Task QueryErrorsAreNotAbsenceEvenWhenTextClaimsNoMatchOrSuccess(int code)
    {
        var fake = new Fake([("list", (code, "No installed package found. Successfully uninstalled 已成功卸载"))], LocalPackagePresence.RecordedPathMissing);
        var result = await fake.Service.UninstallAsync(Id, CancellationToken.None);
        Assert.Equal(RegisteredPackageState.CheckFailed, result.Inspection.State);
        Assert.False(result.ConfirmedRemoved);
        Assert.False(result.UninstallStarted);
        Assert.Equal(0, fake.LocalChecks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Anthropic.ClaudeCode 1.0 winget")]
    [InlineData("Anthropic.Claude")]
    public async Task SuccessfulQueryMustContainTheExactPackageTableIdentity(string output)
    {
        var fake = new Fake([("list", (0, output))], LocalPackagePresence.Present);
        var result = await fake.Service.InspectAsync(Id, CancellationToken.None);
        Assert.Equal(RegisteredPackageState.CheckFailed, result.State);
        Assert.False(result.CanUninstall);
    }

    [Fact]
    public async Task ExactPackageTokenUsesTheSameCaseInsensitiveIdentityAsRegistration()
    {
        var service = new RegisteredPackageOperations((verb, id, _) =>
        {
            Assert.Equal("list", verb);
            Assert.Equal("anthropic.claude", id);
            return Task.FromResult(Present);
        }, (_, _) => throw new InvalidOperationException("A positive exact match needs no fallback evidence."));
        var inspection = await service.InspectAsync("anthropic.claude", CancellationToken.None);
        Assert.True(inspection.CanUninstall);
        Assert.Equal(RegisteredPackageState.Installed, inspection.State);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1978335212)]
    public async Task FailedUninstallDoesNotBecomeSuccessfulBecauseOfConsoleTextOrLaterAbsence(int exitCode)
    {
        var fake = new Fake([("list", Present), ("uninstall", (exitCode, "Successfully uninstalled 已成功卸载")), ("list", Missing)], LocalPackagePresence.RecordedPathMissing);
        var result = await fake.Service.UninstallAsync(Id, CancellationToken.None);
        Assert.True(result.UninstallStarted);
        Assert.False(result.ConfirmedRemoved);
        Assert.Equal(new[] { "list", "uninstall", "list" }, fake.Calls);
    }

    [Theory]
    [InlineData((int)LocalPackagePresence.Present, false, (int)RegisteredPackageState.IdentityMismatch)]
    [InlineData((int)LocalPackagePresence.Unknown, false, (int)RegisteredPackageState.Unmatched)]
    [InlineData((int)LocalPackagePresence.CheckFailed, false, (int)RegisteredPackageState.CheckFailed)]
    [InlineData((int)LocalPackagePresence.RecordedPathMissing, true, (int)RegisteredPackageState.RecordedPathMissing)]
    public async Task SuccessfulUninstallerStillRequiresActualPostState(int local, bool removed, int state)
    {
        var fake = new Fake([("list", Present), ("uninstall", (0, "irrelevant")), ("list", Missing)], (LocalPackagePresence)local);
        var result = await fake.Service.UninstallAsync(Id, CancellationToken.None);
        Assert.True(result.UninstallStarted);
        Assert.Equal(removed, result.ConfirmedRemoved);
        Assert.Equal((RegisteredPackageState)state, result.Inspection.State);
    }

    [Fact]
    public async Task ZeroExitWithPackageStillInstalledIsNotVerifiedRemoval()
    {
        var fake = new Fake([("list", Present), ("uninstall", (0, "")), ("list", Present)], LocalPackagePresence.Unknown);
        var result = await fake.Service.UninstallAsync(Id, CancellationToken.None);
        Assert.False(result.ConfirmedRemoved);
        Assert.Equal(RegisteredPackageState.Installed, result.Inspection.State);
        Assert.Equal(0, fake.LocalChecks);
    }

    [Fact]
    public async Task FailedPostQueryRetainsAnUnconfirmedState()
    {
        var fake = new Fake([("list", Present), ("uninstall", (0, "")), ("list", (-1, ""))], LocalPackagePresence.RecordedPathMissing);
        var result = await fake.Service.UninstallAsync(Id, CancellationToken.None);
        Assert.False(result.ConfirmedRemoved);
        Assert.Equal(RegisteredPackageState.CheckFailed, result.Inspection.State);
        Assert.Equal(0, fake.LocalChecks);
    }

    [Fact]
    public async Task CancellationNeverStartsTheNextAction()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var fake = new Fake([], LocalPackagePresence.Unknown);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.Service.UninstallAsync(Id, stopped.Token));
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--all")]
    [InlineData("Anthropic.Claude --all")]
    [InlineData("Anthropic.Claude\n--purge")]
    [InlineData("\"Anthropic.Claude\"")]
    public async Task InvalidIdentityNeverReachesTheCommandRunner(string id)
    {
        var fake = new Fake([], LocalPackagePresence.Unknown);
        Assert.Equal(RegisteredPackageState.CheckFailed, (await fake.Service.InspectAsync(id, CancellationToken.None)).State);
        Assert.Empty(fake.Calls);
        Assert.Throws<ArgumentException>(() => SystemInstaller.CreatePackageManagementCommand("uninstall", id));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("uninstall")]
    public void CommandUsesOneExactIdAndDoesNotBroadenScopeOrForceRemoval(string verb)
    {
        var command = SystemInstaller.CreatePackageManagementCommand(verb, Id);
        Assert.Equal("winget", command.FileName);
        Assert.Empty(command.Arguments);
        Assert.Equal(new[] { verb, "--id", Id, "--exact", "--disable-interactivity" }, command.ArgumentList);
    }

    private sealed class Fake
    {
        internal readonly List<string> Calls = [];
        internal int LocalChecks;
        internal RegisteredPackageOperations Service { get; }
        internal Fake((string Verb, (int Code, string Output) Receipt)[] receipts, LocalPackagePresence local)
        {
            var pending = new Queue<(string Verb, (int Code, string Output) Receipt)>(receipts);
            Service = new((verb, id, _) =>
            {
                Assert.Equal(Id, id);
                Calls.Add(verb);
                Assert.NotEmpty(pending);
                var next = pending.Dequeue();
                Assert.Equal(next.Verb, verb);
                return Task.FromResult(next.Receipt);
            }, (_, _) => { LocalChecks++; return Task.FromResult(local); });
        }
    }
}
