using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Pages;

/// <summary>
/// 「恶意软件沙盒」页面：上部为 Sandboxie-Plus 安装包下载区域（按系统架构自动匹配，
/// 对接全局下载队列），下部为沙盒使用教程。
/// 主按钮三态：未下载 →「下载安装包」；安装包已下载 →「安装」；已安装 →「打开」。
/// </summary>
public sealed partial class SandboxiePage : Page
{
    private const string ReleaseBaseUrl = "https://github.com/sandboxie-plus/Sandboxie/releases/download/v1.18.3";
    private const string VersionTag = "v1.18.3";

    private static readonly (string Arch, string DisplayName, string FileName, string Size)[] ArchOptions =
    [
        ("x64", MiscTexts.T("x64（64 位系统）"), $"Sandboxie-Plus-x64-{VersionTag}.exe", MiscTexts.T("约 23.7 MB")),
        ("arm64", MiscTexts.T("ARM64（骁龙等）"), $"Sandboxie-Plus-ARM64-{VersionTag}.exe", MiscTexts.T("约 21.3 MB")),
    ];

    private (string Arch, string FileName, string Size) _selected;
    private bool _x86Blocked;
    private bool _initialized;
    private bool _suppressToggle;
    private DispatcherQueue? _dq;
    private OwnedWindowsPackage? _ownedPackage;
    private string? _catalogError;
    private bool _loadingPackage;
    private int _selectionGeneration;
    private CancellationTokenSource? _catalogCancellation;

    private enum ToolState { NotDownloaded, Downloaded, Installed }

    private ToolState _state = ToolState.NotDownloaded;

    private static string InstallerDir => Path.Combine(Path.GetTempPath(), "TubaWinUi3_Sandboxie");

    public SandboxiePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        // 下载队列变化时刷新按钮状态（下载完成 → 按钮变「安装」）
        DownloadQueueService.QueueChanged += OnQueueChanged;
        InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DownloadQueueService.QueueChanged -= OnQueueChanged;
        _catalogCancellation?.Cancel();
        _selectionGeneration++;
    }

    private async void InitializeAsync()
    {
        if (_initialized) { await LoadOwnedPackageAsync(); return; }
        var osArch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        foreach (var opt in ArchOptions)
            ArchCombo.Items.Add(opt.DisplayName);

        var bestIndex = Array.FindIndex(ArchOptions, o => o.Arch == osArch);
        if (bestIndex >= 0)
        {
            ArchCombo.SelectedIndex = bestIndex;
        }
        else
        {
            // x86 等没有对应安装包的架构：展示提示并允许手动查看可用选项
            ArchCombo.SelectedIndex = 0;
            SizeText.Text = "";
            _x86Blocked = true;
            ArchWarnBar.Severity = InfoBarSeverity.Error;
            ArchWarnBar.Title = MiscTexts.T("当前系统为 32 位（x86）");
            ArchWarnBar.Message = MiscTexts.T("发布页未提供 x86 安装包，且 32 位系统无法运行 x64 安装包，无法在此设备上安装。");
            ArchWarnBar.IsOpen = true;
        }

        _initialized = true;
        await LoadOwnedPackageAsync();
    }

    private async void ArchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = ArchCombo.SelectedIndex >= 0 ? ArchCombo.SelectedIndex : 0;
        var opt = ArchOptions[index];
        _selected = (opt.Arch, opt.FileName, opt.Size);
        SizeText.Text = opt.Size;

        // 架构切换后，已下载的安装包文件名随之变化，需重新判定按钮状态
        if (_initialized)
            await LoadOwnedPackageAsync();
    }

    private async Task LoadOwnedPackageAsync()
    {
        if (_x86Blocked) { RefreshState(); return; }
        var generation = ++_selectionGeneration;
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        _catalogCancellation = new CancellationTokenSource();
        var token = _catalogCancellation.Token;
        _ownedPackage = null;
        _catalogError = null;
        _loadingPackage = true;
        SizeText.Text = "正在获取国内安装包信息…";
        RefreshState();
        try
        {
            var package = await OwnedInstallerDownloads.Manager.ResolveAsync("sandboxie", token, _selected.Arch);
            if (generation != _selectionGeneration) return;
            _ownedPackage = package;
            SizeText.Text = package is null ? _selected.Size : $"{package.Version} · {ToolDownloaderService.FormatSize(package.SizeBytes)}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (generation != _selectionGeneration) return;
            _catalogError = ex.Message;
            SizeText.Text = "安装包信息暂不可用";
        }
        finally
        {
            if (generation == _selectionGeneration) { _loadingPackage = false; RefreshState(); }
        }
    }

    private void OnQueueChanged()
    {
        // QueueChanged 可能从后台线程触发，切回 UI 线程刷新
        _dq?.TryEnqueue(RefreshState);
    }

    /// <summary>根据「已安装 / 安装包已下载 / 未下载」刷新主按钮文案与提示。</summary>
    private void RefreshState()
    {
        var installedExe = SandboxieShellMenuService.GetSandboxiePlusExe();

        if (installedExe is not null)
        {
            _state = ToolState.Installed;
            ActionText.Text = MiscTexts.T("打开");
            ActionIcon.Glyph = "\uE768";
            ActionBtn.IsEnabled = true;
            ArchWarnBar.Severity = InfoBarSeverity.Success;
            ArchWarnBar.Title = MiscTexts.T("已安装 Sandboxie-Plus");
            ArchWarnBar.Message = MiscTexts.T("点击「打开」直接启动 Sandboxie-Plus；如需重新安装，可先卸载后再下载安装包。");
            ArchWarnBar.IsOpen = true;
            DownloadHint.Opacity = 0;
            RefreshShellMenuToggle();
            return;
        }

        if (_x86Blocked)
        {
            ActionBtn.IsEnabled = false;
            ShellMenuToggle.IsEnabled = false;
            return;
        }

        RefreshShellMenuToggle();

        if (_loadingPackage)
        {
            ActionBtn.IsEnabled = false;
            ActionText.Text = "获取安装包信息…";
            return;
        }
        if (_catalogError is not null)
        {
            _state = ToolState.NotDownloaded;
            ActionBtn.IsEnabled = true;
            ActionText.Text = "重新获取安装包";
            DownloadHint.Text = _catalogError;
            DownloadHint.Opacity = 1;
            return;
        }

        var installer = GetInstallerPath();
        if (WindowsDownloadValidation.IsValid(installer, _ownedPackage?.SizeBytes ?? 0, _ownedPackage?.ExecutableArchitecture))
        {
            _state = ToolState.Downloaded;
            ActionText.Text = MiscTexts.T("安装");
            ActionIcon.Glyph = "\uE768";
            ActionBtn.IsEnabled = true;
            ArchWarnBar.IsOpen = false;
            DownloadHint.Text = "安装包已下载，点击后重新校验并运行安装程序；完成系统安装后此按钮会变为「打开」。";
            DownloadHint.Opacity = 1;
        }
        else
        {
            _state = ToolState.NotDownloaded;
            ActionText.Text = MiscTexts.T("下载安装包");
            ActionIcon.Glyph = "\uE896";
            ActionBtn.IsEnabled = true;
            ArchWarnBar.IsOpen = false;
            DownloadHint.Opacity = 0;
        }
    }

    private async void ActionBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (_state)
            {
                case ToolState.Installed:
                    Launch(SandboxieShellMenuService.GetSandboxiePlusExe());
                    break;
                case ToolState.Downloaded:
                    ActionBtn.IsEnabled = false;
                    var path = GetInstallerPath();
                    if (_ownedPackage is not null)
                        await new OwnedDownloadPostProcessor("sandboxie", _ownedPackage).ExecuteAsync(path, Path.GetDirectoryName(path)!,
                            new Progress<string>(text => { DownloadHint.Text = text; DownloadHint.Opacity = 1; }), CancellationToken.None);
                    else
                        await new InstallerLaunchProcessor().ExecuteAsync(path, InstallerDir,
                            new Progress<string>(text => { DownloadHint.Text = text; DownloadHint.Opacity = 1; }), CancellationToken.None);
                    break;
                case ToolState.NotDownloaded:
                    if (_catalogError is not null) await LoadOwnedPackageAsync();
                    else await DownloadInstallerAsync();
                    break;
            }
        }
        catch (Exception ex) { DownloadHint.Text = ex.Message; DownloadHint.Opacity = 1; }
        finally { ActionBtn.IsEnabled = !_x86Blocked && !_loadingPackage; }
    }

    private async Task DownloadInstallerAsync()
    {
        if (string.IsNullOrEmpty(_selected.FileName)) return;

        if (await OwnedInstallerDownloads.EnqueueAsync("sandboxie", $"Sandboxie-Plus 安装包（{_selected.Arch.ToUpperInvariant()}）", "\uEA18",
            text => { DownloadHint.Text = text; DownloadHint.Opacity = 1; }, architecture: _selected.Arch)) return;

        var url = $"{ReleaseBaseUrl}/{_selected.FileName}";

        DownloadQueueService.Enqueue(
            displayName: MiscTexts.TSub($"Sandboxie-Plus 安装包（{_selected.Arch.ToUpperInvariant()}）"),
            downloadUrl: url,
            destinationPath: InstallerDir,
            postProcessor: new InstallerLaunchProcessor(),
            description: MiscTexts.TSub($"恶意软件沙盒 Sandboxie-Plus 安装包，{_selected.Size}，下载完成后自动启动安装程序"),
            glyph: "\uEA18");

        DownloadHint.Text = "国内目录尚未发布，已尝试官方备用来源；校验通过后运行安装程序，完成安装后再打开。";
        DownloadHint.Opacity = 1;
    }

    private string GetInstallerPath()
    {
        if (_ownedPackage is not null)
            return Path.Combine(OwnedInstallerDownloads.Manager.Destination("sandboxie", _ownedPackage), _ownedPackage.FileName);
        if (string.IsNullOrEmpty(_selected.FileName)) return string.Empty;
        return Path.Combine(InstallerDir, _selected.FileName);
    }

    private static void Launch(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>同步「文件夹背景右键菜单」开关：需已安装 Sandboxie-Plus 才可开启。</summary>
    private void RefreshShellMenuToggle()
    {
        var installed = SandboxieShellMenuService.GetSandboxieDir() is not null;
        _suppressToggle = true;
        ShellMenuToggle.IsEnabled = installed;
        ShellMenuToggle.IsOn = installed && SandboxieShellMenuService.IsRegistered();
        _suppressToggle = false;
    }

    private void ShellMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;

        if (ShellMenuToggle.IsOn)
        {
            if (SandboxieShellMenuService.Enable())
            {
                DownloadHint.Text = MiscTexts.T("已在文件夹背景右键菜单注册「在沙箱中运行」，可在资源管理器中右键文件夹空白处使用。");
                DownloadHint.Opacity = 1;
            }
            else
            {
                _suppressToggle = true;
                ShellMenuToggle.IsOn = false;
                _suppressToggle = false;
                DownloadHint.Text = MiscTexts.T("注册右键菜单失败：未找到 Sandboxie-Plus 安装目录（需含 Start.exe）。");
                DownloadHint.Opacity = 1;
            }
        }
        else
        {
            SandboxieShellMenuService.Disable();
            DownloadHint.Text = MiscTexts.T("已移除文件夹背景右键菜单中的「在沙箱中运行」。");
            DownloadHint.Opacity = 1;
        }
    }
}
