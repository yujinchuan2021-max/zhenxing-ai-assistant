using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TubaWinUi3.Services;

namespace TubaWinUi3.Models;

public sealed class ToolItem : INotifyPropertyChanged
{
    private IReadOnlyList<string> _categories = [];

    public required string Name { get; init; }


    /// <summary>显示名：内置工具按稳定 ID 映射（原始 Name 仍为业务键）；普通工具即 Name。</summary>
    public string NameDisplay => IsBuiltinLink && BuiltinToolId is not null
        ? ToolDisplayTexts.BuiltinName(BuiltinToolId, Name)
        : Name;


    public required string Category { get; init; }

    public string? PrimaryCategory { get; init; }

    public IReadOnlyList<string> Categories
    {
        get => _categories;
        init => _categories = value;
    }

    public bool IsLinked { get; init; }

    public bool IsBuiltinLink { get; init; }

    public string? BuiltinToolId { get; init; }

    public string? BuiltinKindText { get; init; }

    /// <summary>内置工具类型的显示层翻译（弹窗/后台任务/进度任务/即时操作）。</summary>
    public string? BuiltinKindDisplay => BuiltinKindText is null ? null : ToolDisplayTexts.BuiltinKind(BuiltinKindText);

    public string CategoriesDisplay => _categories.Count <= 1 ? "" : string.Join(" · ", _categories.Where(c => c != Category).Select(c => LocalizationService.GetCategoryDisplayName(c)));

    public IReadOnlyList<string> OtherCategories => _categories.Where(c => !c.Equals(Category, StringComparison.OrdinalIgnoreCase)).ToList();

    public required string Path { get; init; }

    public required string RelativePath { get; init; }

    public required string Extension { get; init; }

    /// <summary>「内置」徽章的显示层翻译（真实文件扩展名原样返回）。</summary>
    public string ExtensionDisplay => Extension == "内置"
        ? LocalizationService.L("Common_Builtin", "内置")
        : Extension;

    private string? _iconPath;
    public string? IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value);
    }

    private string? _iconGlyph;
    public string? IconGlyph
    {
        get => _iconGlyph;
        set => SetField(ref _iconGlyph, value);
    }

    public string? Description { get; init; }

    /// <summary>随包策展说明的显示层翻译；用户自定义 / 未收录说明原样返回。</summary>
    public string DescriptionDisplay
    {
        get
        {
            // 社区条目：服务端原文；内置工具：按稳定 ID 走内置描述映射；
            // 随包策展条目（tools.json metadata）：说明反查；其余来源（用户放置等）：保留原文不翻译。
            if (IsCommunity) return Description ?? "";
            if (IsBuiltinLink && BuiltinToolId is not null)
                return ToolDisplayTexts.BuiltinDescription(BuiltinToolId, Description);
            if (IsCatalogCurated) return ToolDisplayTexts.Description(Description);
            return Description ?? "";
        }
    }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public string? DatabaseSource { get; init; }

    public string? DownloadUrl { get; init; }

    public string? CloudToolId { get; init; }
    public bool CloudHasPackage { get; init; }

    public string? DownloadFilter { get; init; }

    public string? WingetId { get; init; }

    public string? RemoteUrl { get; init; }

    public string? TutorialUrl { get; init; }

    /// <summary>tools.json 的 order 字段：卡片排序主键（null = 未收录的自定义工具，排在后面）。</summary>
    public int? SortOrder { get; init; }

    /// <summary>ZXAI 合规替换标注：本工具替换的原软件名；空 = 非替换项。</summary>
    public string? ReplacedFrom { get; init; }

    public string ReplacementText => string.IsNullOrWhiteSpace(ReplacedFrom) ? "" : string.Format(LocalizationService.L("AiCategory_Replaced", "✓ 合规替换：原 {0}"), ReplacedFrom);

    public bool HasTutorial => !string.IsNullOrWhiteSpace(TutorialUrl);

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string TagsText => Tags.Count > 0 ? string.Join("  ", Tags.Select(LocalizationService.GetTagDisplayName)) : "";

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetField(ref _isFavorite, value);
    }

    public string Folder => System.IO.Path.GetDirectoryName(RelativePath) ?? Category;

    public bool NeedsDownload => !IsBuiltinLink && !File.Exists(EffectivePath) && (!string.IsNullOrWhiteSpace(DownloadUrl) || !string.IsNullOrWhiteSpace(WingetId));

    public bool HasUpdateSource => !string.IsNullOrWhiteSpace(DownloadUrl);

    public bool NeedsWingetInstall => !string.IsNullOrWhiteSpace(WingetId);

    /// <summary>内置工具（有注册 Id）也能发桌面快捷方式：以 --open-builtin 启动自身直达工具。</summary>
    public bool CanSendToDesktop => CloudToolId is not null ? File.Exists(EffectivePath)
        : !IsBuiltinLink || !string.IsNullOrWhiteSpace(BuiltinToolId);

    private bool _isWingetInstalled;
    public bool IsWingetInstalled
    {
        get => _isWingetInstalled;
        set
        {
            if (SetField(ref _isWingetInstalled, value))
            {
                OnPropertyChanged(nameof(LaunchButtonText));
                OnPropertyChanged(nameof(IsWingetInstalling));
                OnPropertyChanged(nameof(CanLaunch));
            }
        }
    }

    private bool _isWingetInstalling;
    public bool IsWingetInstalling
    {
        get => _isWingetInstalling;
        set
        {
            if (SetField(ref _isWingetInstalling, value))
            {
                OnPropertyChanged(nameof(LaunchButtonText));
                OnPropertyChanged(nameof(CanLaunch));
            }
        }
    }

    private int _wingetInstallProgress;
    public int WingetInstallProgress
    {
        get => _wingetInstallProgress;
        set => SetField(ref _wingetInstallProgress, value);
    }

    private string _wingetInstallStatus = "";
    public string WingetInstallStatus
    {
        get => _wingetInstallStatus;
        set => SetField(ref _wingetInstallStatus, value);
    }

    public bool CanLaunch => IsBuiltinLink || !IsWingetInstalling;

    public string? PrimaryArch { get; init; }

    public IReadOnlyList<ArchVariant> AlternateVersions { get; init; } = [];

    public bool HasAlternateVersions => AlternateVersions.Count > 0;

    public ObservableCollection<ArchOption> ArchOptions { get; } = [];

    private ArchOption? _selectedArch;
    public ArchOption? SelectedArch
    {
        get => _selectedArch;
        set
        {
            if (_suppressArchSelection) return;
            if (SetField(ref _selectedArch, value))
            {
                OnPropertyChanged(nameof(EffectivePath));
                OnPropertyChanged(nameof(EffectiveWorkingDir));
                OnPropertyChanged(nameof(LaunchButtonText));
            }
        }
    }

    public string EffectivePath => CloudToolId is { } cloudId
        ? Services.CloudTools.CloudToolService.GetInstalledEntryPath(cloudId) ?? Path
        : SelectedArch?.Path ?? Path;

    public string EffectiveWorkingDir =>
        System.IO.Path.GetDirectoryName(EffectivePath) ?? ToolCatalog.ToolsRoot;

    public string LaunchButtonText
    {
        get
        {
            if (IsBuiltinLink) return LocalizationService.L("Tool_Open", "打开");
            if (CloudToolId is not null)
                return File.Exists(EffectivePath) ? LocalizationService.L("Tool_Open", "打开")
                    : CloudHasPackage ? LocalizationService.L("Common_Download", "下载") : "获取方式";
            if (!string.IsNullOrWhiteSpace(DownloadUrl) && !File.Exists(EffectivePath))
                return LocalizationService.L("Common_Download", "下载");
            if (!string.IsNullOrWhiteSpace(WingetId))
            {
                if (IsWingetInstalling) return LocalizationService.L("Tool_InstallingShort", "安装中...");
                return IsWingetInstalled ? LocalizationService.L("Tool_Open", "打开") : LocalizationService.L("Common_Download", "下载");
            }
            return LocalizationService.L("Tool_Open", "打开");
        }
    }

    public void SetCategories(IReadOnlyList<string> categories)
    {
        _categories = categories;
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(CategoriesDisplay));
        OnPropertyChanged(nameof(OtherCategories));
    }

    private bool _suppressArchSelection;

    public void InitArchOptions()
    {
        _suppressArchSelection = true;
        ArchOptions.Clear();
        var primary = new ArchOption { Name = Name, Path = Path, Arch = PrimaryArch ?? "" };
        ArchOptions.Add(primary);
        foreach (var v in AlternateVersions)
        {
            ArchOptions.Add(new ArchOption { Name = v.Name, Path = v.Path, Arch = v.Arch });
        }
        _suppressArchSelection = false;
        SelectedArch = ToolCatalog.PickPreferredArchOption(ArchOptions, primary);
    }

    /// <summary>ZXAI 2026-09-23：社区工具标记（社区频道已分摊到各分类；来源=社区插件库）。</summary>
    public bool IsCommunity { get; init; }

    /// <summary>来自随包策展目录（tools.json metadata）：仅此类条目的说明参与显示层反查翻译；
    /// 其它来源（用户放置等）描述保持原文——由本标记区分。</summary>
    public bool IsCatalogCurated { get; init; }

    /// <summary>社区工具原始对象（下载/安装/详情用）。IsCommunity=true 时非空。</summary>
    public CommunityTool? CommunitySource { get; init; }

    /// <summary>社区贡献者（显示用）。</summary>
    public string? CommunityAuthor => CommunitySource?.Author;

    /// <summary>卡片类别行显示文本：社区卡显示「社区 · 作者 v版本」，本地卡显示分类名。</summary>
    public string CategoryDisplayText => IsCommunity ? CommunityCreditText : LocalizationService.GetCategoryDisplayName(Category);

    /// <summary>社区卡标识（XAML 显示"社区"徽标用）。</summary>
    public Visibility CommunityBadgeVisibility => IsCommunity ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>社区卡副标题：作者 + 版本。</summary>
    public string CommunityCreditText
    {
        get
        {
            if (CommunitySource is null) return "";
            var author = string.IsNullOrWhiteSpace(CommunitySource.Author) ? LocalizationService.L("Tool_CommunityCredit", "社区贡献") : string.Format(LocalizationService.L("Tool_CommunityAuthor", "社区 · {0}"), CommunitySource.Author);
            var ver = string.IsNullOrWhiteSpace(CommunitySource.Version) ? "" : $"  v{CommunitySource.Version}";
            return author + ver;
        }
    }

    /// <summary>语言切换后刷新卡片上的本地化显示文本（属性通知触发绑定更新）。</summary>
    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(ReplacementText));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(CategoryDisplayText));
        OnPropertyChanged(nameof(CommunityCreditText));
        OnPropertyChanged(nameof(CategoriesDisplay));
        OnPropertyChanged(nameof(DescriptionDisplay));
        OnPropertyChanged(nameof(NameDisplay));
        OnPropertyChanged(nameof(ExtensionDisplay));
        OnPropertyChanged(nameof(BuiltinKindDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static DispatcherQueue? _uiDispatcher;

    public static void SetUIDispatcher(DispatcherQueue dispatcher) => _uiDispatcher = dispatcher;

    private void RaisePropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        if (_uiDispatcher is not null && !_uiDispatcher.HasThreadAccess)
        {
            _uiDispatcher.TryEnqueue(() => handler.Invoke(this, new PropertyChangedEventArgs(propertyName)));
        }
        else
        {
            handler.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName!);
        return true;
    }

    private void OnPropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);
}

public sealed class ArchVariant
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Arch { get; init; }
}

public sealed class ArchOption : IEquatable<ArchOption>
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Arch { get; init; }

    public string DisplayText => string.IsNullOrEmpty(Arch) ? LocalizationService.L("Common_Default", "默认") : Arch;

    public override string ToString() => DisplayText;

    public bool Equals(ArchOption? other) =>
        other is not null && Path.Equals(other.Path, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as ArchOption);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path);
}
