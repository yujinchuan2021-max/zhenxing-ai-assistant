using System.Text.RegularExpressions;

namespace TubaWinUi3.Services.AppManagement;

internal enum RegisteredPackageState { Installed, RecordedPathMissing, IdentityMismatch, Unmatched, CheckFailed }
internal enum LocalPackagePresence { Present, RecordedPathMissing, Unknown, CheckFailed }
internal sealed record RegisteredPackageInspection(RegisteredPackageState State, string Message)
{
    internal bool CanUninstall => State == RegisteredPackageState.Installed;
}
internal sealed record RegisteredPackageUninstallResult(bool ConfirmedRemoved, bool UninstallStarted,
    RegisteredPackageInspection Inspection, string Message);

/// <summary>Exact package checks and uninstall orchestration. Injected delegates keep tests off the real machine.
/// No registration is ever deleted by this service; removing a record remains a separate user action.</summary>
internal sealed class RegisteredPackageOperations(
    Func<string, string, CancellationToken, Task<(int Code, string Output)>> command,
    Func<string, CancellationToken, Task<LocalPackagePresence>> localEvidence)
{
    // Microsoft winget-cli AppInstallerErrors.h: APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND.
    internal const int NoApplicationsFound = unchecked((int)0x8A150014);
    internal static bool IsPackageId(string? value) => value is not null &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._+-]{0,199}\z", RegexOptions.CultureInvariant);

    internal async Task<RegisteredPackageInspection> InspectAsync(string wingetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsPackageId(wingetId)) return Failed("包身份无效，未执行检查。", "The package identity is invalid; no check was run.");
        try
        {
            var query = await command("list", wingetId, ct);
            ct.ThrowIfCancellationRequested();
            // A successful exact query must actually contain the complete ID as a table token.
            // Text such as 'Successfully uninstalled' never overrides a nonzero exit code.
            if (query.Code == 0 && query.Output.Split('\n').Any(line =>
                    Regex.IsMatch(line, @"(?<!\S)" + Regex.Escape(wingetId) + @"(?=\s+\S)",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                return new(RegisteredPackageState.Installed, T("已确认对应的系统安装，可卸载。", "The matching installed package was found and can be uninstalled."));
            if (query.Code != NoApplicationsFound)
                return Failed($"安装状态检查未完成（代码 {query.Code}）；入口未找到不能证明已卸载。登记记录保留。",
                    $"The installation check did not complete (code {query.Code}). A missing entry does not prove removal. The record is retained.");

            var local = await localEvidence(wingetId, ct);
            ct.ThrowIfCancellationRequested();
            return local switch
            {
                LocalPackagePresence.Present => new(RegisteredPackageState.IdentityMismatch,
                    T("本机仍有软件或安装登记，但包身份未匹配；可能是安装方式或作用域不同。请到 Windows 已安装的应用核对。",
                        "Local software or an installation record remains, but this package identity did not match. Check Windows installed apps for a different installation method or scope.")),
                LocalPackagePresence.RecordedPathMissing => new(RegisteredPackageState.RecordedPathMissing,
                    T("登记位置已不存在，也未找到对应安装；这不代表已执行卸载。登记记录保留，可单独移除。",
                        "The recorded location is gone and no matching installation was found. No uninstall is claimed. The record is retained and can be removed separately.")),
                LocalPackagePresence.CheckFailed => Failed("本机安装证据未能完整读取，暂不能确认已卸载；登记记录保留。",
                    "Local installation evidence could not be read completely. Removal is unconfirmed and the record is retained."),
                _ => new(RegisteredPackageState.Unmatched,
                    T("未找到匹配的安装包；入口未找到不能证明已卸载。请到 Windows 已安装的应用核对，或单独移除登记记录。",
                        "No matching package was found. A missing entry does not prove removal. Check Windows installed apps, or remove only the registration record.")),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Failed("安装状态检查失败，未执行卸载；软件与登记记录保留。",
            "The installation check failed; no uninstall was performed. Software and registration are retained."); }
    }

    internal async Task<RegisteredPackageUninstallResult> UninstallAsync(string wingetId, CancellationToken ct)
    {
        var before = await InspectAsync(wingetId, ct);
        if (!before.CanUninstall) return new(false, false, before, before.Message);
        (int Code, string Output) result;
        try { result = await command("uninstall", wingetId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            var unknown = Failed("卸载结果未能确认，登记记录保留。请检查 Windows 已安装的应用。",
                "The uninstall result is unconfirmed. The record is retained; check Windows installed apps.");
            return new(false, true, unknown, unknown.Message);
        }
        ct.ThrowIfCancellationRequested();
        var after = await InspectAsync(wingetId, ct);
        if (result.Code != 0)
            return new(false, true, after, T($"卸载未确认成功（代码 {result.Code}）。", $"Uninstall success was not confirmed (code {result.Code}). ") + " " + after.Message);
        // An exact query no-match alone is insufficient, especially for pseudo-path registrations.
        if (after.State == RegisteredPackageState.RecordedPathMissing)
            return new(true, true, after, T("卸载已结束，已确认原登记位置及对应安装不再存在。登记记录保留，可单独移除。",
                "Uninstall completed; the recorded location and matching installation are no longer present. The registration is retained and can be removed separately."));
        return new(false, true, after, T("卸载程序已结束，仍需核对实际状态。", "The uninstaller finished; the actual state still needs checking. ") + " " + after.Message);
    }

    private static RegisteredPackageInspection Failed(string zh, string en) => new(RegisteredPackageState.CheckFailed, T(zh, en));
    internal static string T(string zh, string en) => LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh;
}
