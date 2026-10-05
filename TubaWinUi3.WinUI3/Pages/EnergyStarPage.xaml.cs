// EnergyStarWindow — standalone window UI for the EcoQoS efficiency-mode tool.
// UI design ported from EnergyStarX (https://github.com/JasonWei512/EnergyStarX)
// Copyright 2022 Bingxing Wang — MIT licensed (see Services/EnergyStar/LICENSE.txt).
//
// Adapted for TubaWinUi3: rewritten as a WinUI 3 Window (not a Page), themed via
// ThemeService, settings persisted via AppSettings, startup via schtasks.

using System.Security.Principal;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services;
using Windows.Graphics;
using Windows.UI;

namespace TubaWinUi3.Pages;

public sealed partial class EnergyStarPage : Page
{
    private static readonly Color AccentGreen = Color.FromArgb(255, 74, 222, 128);
    private static readonly Color AccentGray = Color.FromArgb(255, 148, 163, 184);
    private static readonly Color AccentOrange = Color.FromArgb(255, 251, 146, 60);

    // Guard against Toggled handlers firing while we programmatically sync the UI
    // to the current service state on open.
    private bool _loading = true;
    private bool _isAdmin;
    private bool _whitelistEditing;
    private bool _blacklistEditing;

    public EnergyStarPage()
    {
        InitializeComponent();

        _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        if (!_isAdmin)
        {
            AdminWarningText.Text = MiscTexts.T("当前进程未以管理员身份运行 — 后台进程节流可能失败, 建议以管理员身份重启应用。");
            AdminWarningText.Visibility = Visibility.Visible;
        }

        // Reflect current service state into the UI controls.
        SyncUiFromService();

        // Subscribe to live updates from the service.
        EnergyStarService.Log += OnServiceLog;
        EnergyStarService.ThrottleStatusChanged += OnThrottleStatusChanged;

        _loading = false;

        // Kick off async load of the startup toggle state.
        _ = LoadStartupStateAsync();

        Unloaded += EnergyStarPage_Unloaded;
    }

    // ---------------------------------------------------------------------
    // UI <-> service sync
    // ---------------------------------------------------------------------

    private void SyncUiFromService()
    {
        var status = EnergyStarService.ThrottleStatus;
        var isRunning = status != ThrottleStatus.Stopped;

        MainToggle.IsOn = isRunning;
        ThrottleWhenPluggedInToggle.IsOn = EnergyStarService.ThrottleWhenPluggedIn;
        PauseButton.IsEnabled = isRunning;
        UpdatePauseButton(EnergyStarService.PauseThrottling);
        UpdateStatusBadge(status, EnergyStarService.PauseThrottling);
    }

    private void UpdatePauseButton(bool paused)
    {
        if (paused)
        {
            PauseButtonIcon.Glyph = "\uE768"; // play
            PauseButtonText.Text = MiscTexts.T("继续节流");
        }
        else
        {
            PauseButtonIcon.Glyph = "\uE769"; // pause
            PauseButtonText.Text = MiscTexts.T("暂停节流");
        }
    }

    private void UpdateStatusBadge(ThrottleStatus status, bool paused)
    {
        if (paused || status == ThrottleStatus.Stopped)
        {
            StatusBadgeText.Text = MiscTexts.T("已停止");
            StatusBadge.Background = new SolidColorBrush(AccentGray);
            StatusBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
            PageHeader.Subtitle = paused ? "节流已暂停 (进程列表保留, 可随时恢复)" : "效率模式未启用";
        }
        else
        {
            StatusBadgeText.Text = EnergyStarService.IsOnBattery ? MiscTexts.T("电池模式") : MiscTexts.T("运行中");
            StatusBadge.Background = new SolidColorBrush(AccentGreen);
            StatusBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
            PageHeader.Subtitle = EnergyStarService.ThrottleStatusDescription(status);
        }
    }

    private void OnThrottleStatusChanged(object? sender, ThrottleStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Re-sync controls without re-entrancy firing handlers.
            _loading = true;
            var isRunning = status != ThrottleStatus.Stopped;
            MainToggle.IsOn = isRunning;
            PauseButton.IsEnabled = isRunning;
            UpdatePauseButton(EnergyStarService.PauseThrottling);
            UpdateStatusBadge(status, EnergyStarService.PauseThrottling);
            _loading = false;
        });
    }

    private void OnServiceLog(object? sender, string message)
    {
        // Log events may fire from background threads (housekeeping task).
        DispatcherQueue.TryEnqueue(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{stamp}] {message}{Environment.NewLine}";
        LogText.Text += line;

        // Cap log size to ~32KB to keep the TextBlock light.
        if (LogText.Text.Length > 32_000)
            LogText.Text = LogText.Text[^32_000..];

        LogScrollViewer.ChangeView(0, LogScrollViewer.ExtentHeight, 1);
    }

    // ---------------------------------------------------------------------
    // Main toggle / pause
    // ---------------------------------------------------------------------

    private void MainToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (MainToggle.IsOn)
        {
            try
            {
                EnergyStarService.Initialize();
                // Initialize keeps previous PauseThrottling state — clear it on
                // a fresh user-driven enable so throttling actually starts.
                EnergyStarService.PauseThrottling = false;
            }
            catch (Exception ex)
            {
                ShowToast(InfoBarSeverity.Error, MiscTexts.TSub($"启用失败: {ex.Message}"));
                _loading = true;
                MainToggle.IsOn = false;
                _loading = false;
            }
        }
        else
        {
            try { EnergyStarService.Shutdown(); }
            catch (Exception ex) { ShowToast(InfoBarSeverity.Error, MiscTexts.TSub($"停止失败: {ex.Message}")); }
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        EnergyStarService.PauseThrottling = !EnergyStarService.PauseThrottling;
        UpdatePauseButton(EnergyStarService.PauseThrottling);
        UpdateStatusBadge(EnergyStarService.ThrottleStatus, EnergyStarService.PauseThrottling);
    }

    private void ThrottleWhenPluggedInToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        EnergyStarService.ThrottleWhenPluggedIn = ThrottleWhenPluggedInToggle.IsOn;
    }

    // ---------------------------------------------------------------------
    // Startup toggle
    // ---------------------------------------------------------------------

    private async Task LoadStartupStateAsync()
    {
        try
        {
            var type = await EnergyStarStartupService.GetStartupTypeAsync();
            _loading = true;
            RunAtStartupToggle.IsOn = type == EnergyStarStartupService.StartupType.Admin;
            _loading = false;
            RunAtStartupHint.Text = type == EnergyStarStartupService.StartupType.Admin
                ? MiscTexts.TSub($"计划任务: {EnergyStarStartupService.ScheduleTaskName}")
                : MiscTexts.T("未配置开机自启");
        }
        catch (Exception ex)
        {
            RunAtStartupHint.Text = MiscTexts.TSub($"读取自启状态失败: {ex.Message}");
        }
    }

    private async void RunAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var desired = RunAtStartupToggle.IsOn;
        RunAtStartupToggle.IsEnabled = false;
        try
        {
            var ok = await EnergyStarStartupService.SetStartupEnabledAsync(desired);
            if (!ok)
            {
                ShowToast(InfoBarSeverity.Warning, desired ? MiscTexts.T("创建计划任务失败 (UAC 可能被拒绝)") : MiscTexts.T("删除计划任务失败"));
                _loading = true;
                RunAtStartupToggle.IsOn = !desired;
                _loading = false;
            }
            else
            {
                RunAtStartupHint.Text = desired
                    ? MiscTexts.TSub($"计划任务: {EnergyStarStartupService.ScheduleTaskName}")
                    : MiscTexts.T("未配置开机自启");
                ShowToast(InfoBarSeverity.Success, desired ? MiscTexts.T("已设置开机自启") : MiscTexts.T("已取消开机自启"));
            }
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, MiscTexts.TSub($"自启设置出错: {ex.Message}"));
        }
        finally
        {
            RunAtStartupToggle.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------------
    // Whitelist / blacklist editors
    // ---------------------------------------------------------------------

    private void ToggleWhitelistEdit_Click(object sender, RoutedEventArgs e)
    {
        _whitelistEditing = !_whitelistEditing;
        if (_whitelistEditing)
        {
            WhitelistEditor.Text = EnergyStarService.ProcessWhitelistString;
            WhitelistEditor.Visibility = Visibility.Visible;
            SaveWhitelistButton.Visibility = Visibility.Visible;
            ToggleWhitelistEditText.Text = MiscTexts.T("收起");
        }
        else
        {
            WhitelistEditor.Visibility = Visibility.Collapsed;
            SaveWhitelistButton.Visibility = Visibility.Collapsed;
            ToggleWhitelistEditText.Text = MiscTexts.T("编辑");
        }
    }

    private void SaveWhitelist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnergyStarService.ApplyAndSaveProcessWhitelist(WhitelistEditor.Text);
            ShowToast(InfoBarSeverity.Success, MiscTexts.T("白名单已保存并应用"));
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, MiscTexts.TSub($"保存失败: {ex.Message}"));
        }
    }

    private void RestoreWhitelist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnergyStarService.RestoreDefaultProcessWhitelist();
            if (_whitelistEditing) WhitelistEditor.Text = EnergyStarService.ProcessWhitelistString;
            ShowToast(InfoBarSeverity.Success, MiscTexts.T("已还原默认白名单"));
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, MiscTexts.TSub($"还原失败: {ex.Message}"));
        }
    }

    private void ToggleBlacklistEdit_Click(object sender, RoutedEventArgs e)
    {
        _blacklistEditing = !_blacklistEditing;
        if (_blacklistEditing)
        {
            BlacklistEditor.Text = EnergyStarService.ProcessBlacklistString;
            BlacklistEditor.Visibility = Visibility.Visible;
            SaveBlacklistButton.Visibility = Visibility.Visible;
            ToggleBlacklistEditText.Text = MiscTexts.T("收起");
        }
        else
        {
            BlacklistEditor.Visibility = Visibility.Collapsed;
            SaveBlacklistButton.Visibility = Visibility.Collapsed;
            ToggleBlacklistEditText.Text = MiscTexts.T("编辑");
        }
    }

    private void SaveBlacklist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnergyStarService.ApplyAndSaveProcessBlacklist(BlacklistEditor.Text);
            ShowToast(InfoBarSeverity.Success, MiscTexts.T("黑名单已保存并应用"));
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, MiscTexts.TSub($"保存失败: {ex.Message}"));
        }
    }

    private void RestoreBlacklist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnergyStarService.RestoreDefaultProcessBlacklist();
            if (_blacklistEditing) BlacklistEditor.Text = EnergyStarService.ProcessBlacklistString;
            ShowToast(InfoBarSeverity.Success, MiscTexts.T("已还原默认黑名单"));
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, MiscTexts.TSub($"还原失败: {ex.Message}"));
        }
    }

    // ---------------------------------------------------------------------
    // Misc
    // ---------------------------------------------------------------------

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogText.Text = string.Empty;

    private void ShowToast(InfoBarSeverity severity, string message)
    {
        ToastBar.Severity = severity;
        ToastBar.Message = message;
        ToastBar.IsOpen = true;
    }

    private void EnergyStarPage_Unloaded(object sender, RoutedEventArgs e)
    {
        EnergyStarService.Log -= OnServiceLog;
        EnergyStarService.ThrottleStatusChanged -= OnThrottleStatusChanged;
    }
}
