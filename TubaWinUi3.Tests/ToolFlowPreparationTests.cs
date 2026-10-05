using TubaWinUi3.Services.ToolFlows;
using WinUI3Localizer;

namespace TubaWinUi3.Tests;

public sealed class ToolFlowPreparationTests
{
    [Fact]
    public async Task InstalledToolsAndWinRarReuseLeaveConfirmation_ButRetainTheCompletePlan()
    {
        var git = Item("Git", "git");
        var archive = Item("7-Zip", "7zip");
        var missing = Item("Godot", "godot");
        var account = Item("Model account", null) with { Kind = "service" };
        var probes = new List<string>();
        var service = new ToolFlowPreparationService(["git", "7zip", "godot"], (key, _) =>
        {
            probes.Add(key);
            return Task.FromResult<InstalledToolEvidence?>(key switch
            {
                "git" => new("git", "Git"),
                "winrar" => new("winrar", "WinRAR"),
                _ => null,
            });
        });

        var result = await service.PrepareAsync([git, archive, missing, account], "Use an archiver to extract assets.");

        Assert.Equal(new[] { git, archive, missing, account }, result.AllItems);
        Assert.Equal(new[] { missing, account }, result.ConfirmationItems);
        Assert.Equal(2, result.DetectedItems.Count);
        Assert.Equal(ToolFlowPreparationState.ReusedInstalledTool, result.Rows[1].State);
        Assert.Equal("WinRAR", result.Rows[1].ExistingTool!.Name);
        Assert.Contains("WinRAR", result.Rows[1].Message);
        Assert.DoesNotContain("已安装", result.Rows[1].Message);
        Assert.Single(result.PendingItems);
        Assert.Single(result.NeedsUserAssist);
        Assert.Equal(new[] { "git", "7zip", "winrar", "godot" }, probes);
    }

    [Fact]
    public async Task ExactSevenZipTakesPriorityOverWinRar()
    {
        var probes = new List<string>();
        var service = Service((key, _) =>
        {
            probes.Add(key);
            return Task.FromResult<InstalledToolEvidence?>(new(key, "7-Zip"));
        });
        var row = await service.EvaluateAsync(Item("7-Zip", "7zip"));
        Assert.Equal(ToolFlowPreparationState.AlreadyInstalled, row.State);
        Assert.Equal("7zip", row.ExistingTool!.TargetKey);
        Assert.Equal(new[] { "7zip" }, probes);
    }

    [Theory]
    [InlineData("7-Zip CLI", "", null, (int)ToolFlowRequirementKind.SevenZipCli)]
    [InlineData("7-Zip", "Execute 7z.exe x assets.7z", null, (int)ToolFlowRequirementKind.SevenZipCli)]
    [InlineData("7-Zip", "Use 7-Zip command-line integration", null, (int)ToolFlowRequirementKind.SevenZipCli)]
    [InlineData("7-Zip", "通过 7-Zip 命令行自动解压", null, (int)ToolFlowRequirementKind.SevenZipCli)]
    [InlineData("7-Zip SDK", "", null, (int)ToolFlowRequirementKind.SevenZipApi)]
    [InlineData("7-Zip", "Call 7z.dll API.", null, (int)ToolFlowRequirementKind.SevenZipApi)]
    [InlineData("7-Zip", "Use 7z.exe and 7z.dll", null, (int)ToolFlowRequirementKind.SevenZipCliAndApi)]
    [InlineData("7-Zip", "必须用 7-Zip 指定版本", "24.09", (int)ToolFlowRequirementKind.ExactTarget)]
    [InlineData("7-Zip", "必须用 7-Zip", null, (int)ToolFlowRequirementKind.ExactTarget)]
    [InlineData("7-Zip", "7-Zip 创建 .7z 压缩包", null, (int)ToolFlowRequirementKind.ExactTarget)]
    public async Task SpecificSevenZipRequirementsDoNotSubstituteWinRar(string name, string plan, string? version,
        int expected)
    {
        var probes = new List<string>();
        var service = Service((key, _) =>
        {
            probes.Add(key);
            return Task.FromResult<InstalledToolEvidence?>(key == "winrar" ? new("winrar", "WinRAR") : null);
        });
        var row = await service.EvaluateAsync(Item(name, "7zip") with { Version = version }, plan);
        Assert.Equal((ToolFlowRequirementKind)expected, row.Requirement.Kind);
        Assert.Equal(ToolFlowPreparationState.PendingAutomatic, row.State);
        Assert.Equal(new[] { "7zip" }, probes);
    }

    [Theory]
    [InlineData("Use Codex CLI and OpenAI API.\n7-Zip: extract the downloaded assets.")]
    [InlineData("7-Zip 可以解压 .7z 和 .rar 文件。")]
    public async Task UnrelatedCliAndApiOrAnArchiveExtensionDoNotDemandSevenZip(string plan)
    {
        var row = await WinRarService().EvaluateAsync(Item("7-Zip", "7zip"), plan);
        Assert.Equal(ToolFlowRequirementKind.ArchiveExtraction, row.Requirement.Kind);
        Assert.Equal(ToolFlowPreparationState.ReusedInstalledTool, row.State);
    }

    [Theory]
    [InlineData("Use 7z.exe", false, true, (int)ToolFlowPreparationState.PendingAutomatic)]
    [InlineData("Use 7z.exe", true, false, (int)ToolFlowPreparationState.AlreadyInstalled)]
    [InlineData("Use 7z.dll API", true, false, (int)ToolFlowPreparationState.PendingAutomatic)]
    [InlineData("Use 7z.dll API", false, true, (int)ToolFlowPreparationState.AlreadyInstalled)]
    [InlineData("Use 7z.exe and 7z.dll", false, true, (int)ToolFlowPreparationState.PendingAutomatic)]
    [InlineData("Use 7z.exe and 7z.dll", true, true, (int)ToolFlowPreparationState.AlreadyInstalled)]
    public async Task ActualSevenZipCapabilitiesMustMatchTheRequestedInterface(string plan, bool cli, bool api,
        int state)
    {
        var service = Service((key, _) => Task.FromResult<InstalledToolEvidence?>(key == "7zip"
            ? new("7zip", "7-Zip", HasSevenZipCli: cli, HasSevenZipApi: api) : new("winrar", "WinRAR")));
        Assert.Equal((ToolFlowPreparationState)state, (await service.EvaluateAsync(Item("7-Zip", "7zip"), plan)).State);
    }

    [Theory]
    [InlineData("24.09", "24.09.0.0", (int)ToolFlowPreparationState.AlreadyInstalled)]
    [InlineData("24.09", "25.0", (int)ToolFlowPreparationState.PendingAutomatic)]
    [InlineData("24.09", null, (int)ToolFlowPreparationState.PendingAutomatic)]
    public async Task PinnedArchiveVersionIsCheckedWithoutRunningTheProgram(string expected, string? actual,
        int state)
    {
        var service = Service((_, _) => Task.FromResult<InstalledToolEvidence?>(new("7zip", "7-Zip", Version: actual)));
        Assert.Equal((ToolFlowPreparationState)state, (await service.EvaluateAsync(Item("7-Zip", "7zip") with { Version = expected },
            "Must use the specific 7-Zip version listed in the plan.")).State);
    }

    [Fact]
    public async Task DescriptiveRecommendedVersionDoesNotPreventOrdinaryWinRarReuse()
    {
        var item = Item("7-Zip", "7zip") with { Version = "25.01" };
        var row = await WinRarService().EvaluateAsync(item, "7-Zip: extract downloaded assets.");
        Assert.Equal(ToolFlowRequirementKind.ArchiveExtraction, row.Requirement.Kind);
        Assert.Null(row.Requirement.Version);
        Assert.Equal(ToolFlowPreparationState.ReusedInstalledTool, row.State);
    }

    [Fact]
    public async Task UnknownTargetsAndManualAccountsAreNotGuessedFromNames()
    {
        var service = Service((_, _) => throw new Exception("Must not probe"));
        var rows = await service.PrepareAsync([Item("7-Zip", null), Item("WinRAR", "arbitrary-command")]);
        Assert.All(rows.Rows, row => Assert.Equal(ToolFlowPreparationState.NeedsUserAssist, row.State));
        Assert.Equal(2, rows.ConfirmationItems.Count);
    }

    [Fact]
    public async Task MissingOrFailedProbesNeverRemoveAnItemFromConfirmation()
    {
        var missing = await Service((_, _) => Task.FromResult<InstalledToolEvidence?>(null))
            .PrepareAsync([Item("7-Zip", "7zip")]);
        var failed = await Service((_, _) => throw new IOException("PRIVATE-PROBE-PATH"))
            .PrepareAsync([Item("7-Zip", "7zip")]);
        Assert.Single(missing.ConfirmationItems);
        Assert.Single(failed.ConfirmationItems);
        Assert.Equal(ToolFlowPreparationState.DetectionFailed, failed.Rows[0].State);
        Assert.DoesNotContain("PRIVATE-PROBE-PATH", failed.Rows[0].Message);
    }

    [Fact]
    public async Task MismatchedProviderIdentityDoesNotEstablishAnInstalledTarget()
    {
        var row = await Service((_, _) => Task.FromResult<InstalledToolEvidence?>(new("git", "Git")))
            .EvaluateAsync(Item("7-Zip", "7zip"));
        Assert.False(row.IsSatisfied);
    }

    [Fact]
    public async Task SnapshotDeduplicatesProbes_AndLaterRunsCheckAgain()
    {
        var probes = new List<string>();
        var service = Service((key, _) => { probes.Add(key); return Task.FromResult<InstalledToolEvidence?>(null); });
        await service.PrepareAsync([Item("7-Zip", "7zip"), Item("7-Zip assets", "7zip")]);
        Assert.Equal(new[] { "7zip", "winrar" }, probes);
        await service.EvaluateAsync(Item("7-Zip", "7zip"));
        Assert.Equal(new[] { "7zip", "winrar", "7zip", "winrar" }, probes);
    }

    [Fact]
    public async Task CanceledDetectionIsCanceledInsteadOfClaimingAnInstallFailure()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var service = Service((_, _) => throw new Exception("Must not probe"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync([Item("7-Zip", "7zip")], cancellationToken: stop.Token));
    }

    [Fact]
    public async Task OsRegistrationWithoutALaunchPathStillRecognizesTheSameInstalledApplication()
    {
        var item = Item("GIMP", "gimp");
        var service = new ToolFlowPreparationService(["gimp"], (_, _) =>
            Task.FromResult<InstalledToolEvidence?>(new("gimp", "GIMP", Version: "3.0")));
        var evidence = await service.PrepareAsync([item]);
        Assert.Empty(evidence.ConfirmationItems);
        Assert.Equal(ToolFlowPreparationState.AlreadyInstalled, evidence.Rows[0].State);
        Assert.Null(evidence.Rows[0].ExistingTool!.ExecutablePath);
    }

    [Fact]
    public async Task ExistingReuseDescriptionRefreshesLanguageWithoutRepeatingTheProbe()
    {
        var strings = Path.Combine(FontSingleSourceTests.RepoRoot, "TubaWinUi3.WinUI3", "Strings");
        var localizer = LocalizerBuilder.IsLocalizerAlreadyBuilt ? Localizer.Get() : await new LocalizerBuilder().AddStringResourcesFolderForLanguageDictionaries(strings)
            .SetOptions(options => options.DefaultLanguage = "zh-CN").Build();
        try
        {
            var row = await WinRarService().EvaluateAsync(Item("7-Zip", "7zip"));
            await localizer.SetLanguage("zh-CN");
            var chinese = row.Message;
            await localizer.SetLanguage("en-US");
            var english = row.Message;
            Assert.NotEqual(chinese, english);
            Assert.Contains("WinRAR", chinese);
            Assert.Contains("WinRAR", english);
            Assert.Contains("7-Zip", english);
        }
        finally { await localizer.SetLanguage("zh-CN"); }
    }

    internal static ToolFlowPreparationService WinRarService() => Service((key, _) =>
        Task.FromResult<InstalledToolEvidence?>(key == "winrar" ? new("winrar", "WinRAR", @"C:\FAKE\WinRAR.exe") : null));
    private static ToolFlowPreparationService Service(Func<string, CancellationToken, Task<InstalledToolEvidence?>> probe)
        => new(["7zip"], probe);
    internal static ToolFlowItem Item(string name, string? target) => new()
    {
        ItemId = Guid.NewGuid().ToString("D"), Name = name, Kind = "software", InstallTargetKey = target,
    };
}
