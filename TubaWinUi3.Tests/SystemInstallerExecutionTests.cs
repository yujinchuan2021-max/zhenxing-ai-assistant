using System.Diagnostics;
using System.Text;
using TubaWinUi3.Services;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Tests;

/// <summary>Fixed argument policies, fake metadata, and only test-owned hidden PowerShell children; no installer or network runs.</summary>
public sealed class SystemInstallerExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-system-installer-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRoot = DataRoots.TestRootOverrideForTest;

    public SystemInstallerExecutionTests()
    {
        Directory.CreateDirectory(_root);
        DataRoots.TestRootOverrideForTest = _root;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FixedWingetInstallUsesSilentModeForBothScopeAttempts(bool userScope)
    {
        var arguments = SystemInstaller.CreateWingetInstallArguments("godot", userScope);
        var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "install", "--id", "GodotEngine.GodotEngine", "-e" }, words.Take(4));
        Assert.Contains("--silent", words);
        Assert.Contains("--disable-interactivity", words);
        Assert.Contains("--accept-package-agreements", words);
        Assert.Contains("--accept-source-agreements", words);
        Assert.Equal(userScope, words.Contains("--scope"));
        Assert.DoesNotContain("--interactive", words);
        Assert.DoesNotContain("--allow-reboot", words);
    }

    [Theory]
    [InlineData("unknown-executable")]
    [InlineData("godot --interactive")]
    [InlineData("https://example.invalid/setup.exe")]
    public void SilentArgumentBuilderDoesNotCreateTargetsFromUntrustedText(string target)
        => Assert.Throws<ArgumentException>(() => SystemInstaller.CreateWingetInstallArguments(target, true));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    [InlineData(3010, false)]
    [InlineData(1641, false)]
    public void AFailedOrRestartRelatedExitIsNotPlainInstallationSuccess(int exitCode, bool success)
        => Assert.Equal(success, SystemInstaller.InstallCommandSucceeded(exitCode));

    [Fact]
    public void InstalledVersionReadsMetadataWithoutRunningTheTarget()
    {
        const string path = @"C:\FAKE-DESKTOP\Canva.exe";
        var reads = new List<string>();
        var version = SystemInstaller.ReadInstalledFileVersion(path, candidate => candidate == path, candidate =>
        {
            reads.Add(candidate);
            return " 9.8.7.6 ";
        });
        Assert.Equal("9.8.7.6", version);
        Assert.Equal(new[] { path }, reads);
    }

    [Fact]
    public void MissingFileAndUnavailableVersionAreHonestEmptyMetadata()
    {
        Assert.Equal("", SystemInstaller.ReadInstalledFileVersion(@"C:\FAKE-DESKTOP\missing.exe", _ => false,
            _ => throw new InvalidOperationException("Missing metadata must not be read.")));
        Assert.Equal("", SystemInstaller.ReadInstalledFileVersion(@"C:\FAKE-DESKTOP\restricted.exe", _ => true,
            _ => throw new UnauthorizedAccessException("Synthetic metadata is unavailable.")));
    }

    [Theory]
    [InlineData("figma", "Figma.exe")]
    [InlineData("canva", "Canva.exe")]
    [InlineData("cc-switch", "cc-switch.exe")]
    [InlineData("powertoys", "PowerToys.exe")]
    [InlineData("mobaxterm", "MobaXterm.exe")]
    [InlineData("comfyui", "ComfyUI.exe")]
    [InlineData("upscayl", "Upscayl.exe")]
    [InlineData("blockbench", "Blockbench.exe")]
    [InlineData("paintnet", "paintdotnet.exe")]
    [InlineData("jianying", "JianyingPro.exe")]
    [InlineData("openshot", "openshot-qt.exe")]
    [InlineData("typora", "Typora.exe")]
    [InlineData("rider", "rider64.exe")]
    [InlineData("twine", "Twine.exe")]
    [InlineData("capcut", "CapCut.exe")]
    [InlineData("clion", "clion64.exe")]
    public void ExistingDesktopTargetsKeepTheirFixedGuiEntry(string target, string executable)
    {
        Assert.Contains(target, SystemInstaller.KnownTargets);
        Assert.True(SystemInstaller.TryGetToolAccessMetadata(target, out var metadata));
        Assert.True(metadata.IsGui);
        Assert.Contains(executable, metadata.ExecutableNames);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    [InlineData("node")]
    [InlineData("python")]
    [InlineData("git")]
    [InlineData("cmake")]
    [InlineData("ollama")]
    [InlineData("wsl")]
    [InlineData("autohotkey")]
    [InlineData("open-webui")]
    [InlineData("langflow")]
    public void DesktopClassificationDoesNotPromoteKnownCliOrSdkTargets(string target)
    {
        Assert.True(SystemInstaller.TryGetToolAccessMetadata(target, out var metadata));
        Assert.False(metadata.IsGui);
    }

    [Theory]
    [InlineData("vllm")]
    [InlineData("inspect-ai")]
    [InlineData("ragas")]
    [InlineData("helm")]
    [InlineData("deepeval")]
    public void ExistingPipInstallTargetsRequireTheFixedPythonDependency(string target)
    {
        Assert.Contains(target, SystemInstaller.KnownTargets);
        Assert.Equal(new[] { "python" }, SystemInstaller.GetInstallDependencies(target));
        Assert.Equal(new[] { "python" }, SystemInstaller.GetInstallDependencies(" " + target.ToUpperInvariant() + " "));
    }

    [Theory]
    [InlineData("godot")]
    [InlineData("python")]
    [InlineData("codex")]
    [InlineData("unknown-executable")]
    [InlineData("vllm --untrusted")]
    public void DependencyLookupDoesNotInventDependenciesOrTargets(string target)
        => Assert.Empty(SystemInstaller.GetInstallDependencies(target));

    [Fact]
    public async Task ProcessFailureCannotBeOverriddenByASuccessSentenceOnStdout()
    {
        var start = SyntheticPowerShell("[Console]::Out.WriteLine('Successfully installed'); "
            + "[Console]::Error.WriteLine('synthetic failure'); exit 7");
        var result = await SystemInstaller.RunCommandAsync(start, 10_000, CancellationToken.None);
        Assert.Equal(7, result.Code);
        Assert.Contains("Successfully installed", result.Output);
        Assert.Contains("synthetic failure", result.Output);
        Assert.False(SystemInstaller.InstallCommandSucceeded(result.Code));
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
    }

    [Fact]
    public async Task ExitedParentWithAnUnfinishedInheritedPipeStillObeysTheDeadline()
    {
        var stdoutHeldBySyntheticChild = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrHeldBySyntheticChild = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.WhenAll(Task.CompletedTask, stdoutHeldBySyntheticChild.Task, stderrHeldBySyntheticChild.Task);
        using var deadline = new CancellationTokenSource(150);
        var clock = Stopwatch.StartNew();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SystemInstaller.AwaitCommandCompletionAsync(completion, deadline.Token));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "An exited parent must not leave an unbounded pipe read.");
        }
        finally
        {
            stdoutHeldBySyntheticChild.TrySetResult("");
            stderrHeldBySyntheticChild.TrySetResult("");
            await completion;
        }
    }

    [Fact]
    public async Task DeadlineTerminatesOnlyItsTestOwnedHiddenChild()
    {
        string pidFile = Path.Combine(_root, "timeout-child.pid");
        var start = SyntheticPowerShell(OwnChildMarkerScript(pidFile)
            + " [Console]::Out.WriteLine('fake-child-started'); Start-Sleep -Seconds 30");
        try
        {
            var clock = Stopwatch.StartNew();
            var result = await SystemInstaller.RunCommandAsync(start, 4_000, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(-1, result.Code);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(9));
            Assert.True(File.Exists(pidFile), "The synthetic child must have started; absence is not a passing timeout test.");
            Assert.False(OwnChildAlive(pidFile));
        }
        finally { KillOwnChildIfAlive(pidFile); }
    }

    [Fact]
    public async Task CallerCancellationTerminatesOnlyItsTestOwnedChild()
    {
        string pidFile = Path.Combine(_root, "cancel-child.pid");
        var start = SyntheticPowerShell(OwnChildMarkerScript(pidFile) + " Start-Sleep -Seconds 30");
        using var stop = new CancellationTokenSource();
        var task = SystemInstaller.RunCommandAsync(start, 20_000, stop.Token);
        try
        {
            var started = Stopwatch.StartNew();
            while (!OwnChildAlive(pidFile) && started.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(50);
            Assert.True(File.Exists(pidFile), "The synthetic child must start before cancellation is tested.");
            Assert.True(OwnChildAlive(pidFile));
            stop.Cancel();
            Assert.Equal(-1, (await task.WaitAsync(TimeSpan.FromSeconds(8))).Code);
            Assert.False(OwnChildAlive(pidFile));
        }
        finally
        {
            stop.Cancel();
            try { await task.WaitAsync(TimeSpan.FromSeconds(8)); } catch { }
            KillOwnChildIfAlive(pidFile);
        }
    }

    private static ProcessStartInfo SyntheticPowerShell(string script)
    {
        // This is a fixed Windows host for a test-authored script, never an installed tool,
        // profile script, user command or inherited model configuration.
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(shell), "The isolated process test requires the built-in Windows PowerShell host.");
        return new ProcessStartInfo(shell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
    }

    private static string QuotePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string OwnChildMarkerScript(string path)
        => "[IO.File]::WriteAllText('" + QuotePowerShell(path)
            + "', $PID.ToString() + '|' + [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());";

    private static Process? FindOwnChild(string pidFile)
    {
        if (!File.Exists(pidFile)) return null;
        var identity = File.ReadAllText(pidFile).Split('|');
        if (identity.Length != 2 || !int.TryParse(identity[0], out var pid) || !long.TryParse(identity[1], out var started)) return null;
        Process? child = null;
        try
        {
            child = Process.GetProcessById(pid);
            if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == started) return child;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        child?.Dispose();
        return null;
    }

    private static bool OwnChildAlive(string pidFile)
    {
        using var child = FindOwnChild(pidFile);
        return child is not null;
    }
    private static void KillOwnChildIfAlive(string pidFile)
    {
        try
        {
            using var child = FindOwnChild(pidFile);
            if (child is not null && !child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(3000); }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        DataRoots.TestRootOverrideForTest = _previousRoot;
        var full = Path.GetFullPath(_root);
        var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("zxai-system-installer-", StringComparison.Ordinal))
            throw new InvalidOperationException("Installer fixture escaped its isolated root.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
