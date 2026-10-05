using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace TubaWinUi3.Controls.AgentChat;

/// <summary>
/// 步骤链可视化：运行中实时展示每个 Agent 步骤（时间线行），
/// 整链执行完成后自动折叠为可展开的摘要节点。
/// 折叠/展开使用高度 + 透明度 + 箭头旋转的原生 Storyboard 动画。
/// </summary>
public sealed partial class StepChainControl : UserControl
{
    private const double ExpandedMaxHeight = 1200;
    private RunVm? _attachedRun;

    /// <summary>绑定的步骤链视图模型（由宿主通过 x:Bind 注入）。</summary>
    public RunVm? RunVm
    {
        get => (RunVm?)GetValue(RunVmProperty);
        set => SetValue(RunVmProperty, value);
    }

    public static readonly DependencyProperty RunVmProperty = DependencyProperty.Register(
        nameof(RunVm), typeof(RunVm), typeof(StepChainControl),
        new PropertyMetadata(null, OnRunVmChanged));

    private static void OnRunVmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (StepChainControl)d;
        if (control._attachedRun is not null)
        {
            control._attachedRun.PropertyChanged -= control.OnRunPropertyChanged;
            control._attachedRun.Steps.CollectionChanged -= control.OnStepsChanged;
        }

        control._attachedRun = e.NewValue as RunVm;
        if (control._attachedRun is not null)
        {
            control._attachedRun.PropertyChanged += control.OnRunPropertyChanged;
            // 【主题统一】流式追加的步骤行也要拿到主题上下文，否则状态色会退回 App 当前解析值
            control._attachedRun.Steps.CollectionChanged += control.OnStepsChanged;
            control.StepList.ItemsSource = control._attachedRun.Steps;
            control.SummaryText.Text = control._attachedRun.SummaryText;
            control.AttachThemeHostToSteps();
        }
    }

    /// <summary>【主题统一】把本控件作为步骤行的主题上下文（状态色按实际主题解析）。</summary>
    private void AttachThemeHostToSteps()
    {
        if (_attachedRun is null) return;
        foreach (var row in _attachedRun.Steps)
            row.AttachThemeHost(this);
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (var item in e.NewItems)
        {
            if (item is StepRowVm row) row.AttachThemeHost(this);
        }
    }

    public StepChainControl()
    {
        InitializeComponent();
        // 【UI 改版】主题变化时刷新所有步骤行的状态色（计算属性不会自动跟随主题）；
        // 先退订再订阅保持幂等，Unloaded 退订避免释放后回调。
        Loaded += (_, _) =>
        {
            TubaWinUi3.Services.ThemeService.ThemeChanged -= OnThemeChanged;
            TubaWinUi3.Services.ThemeService.ThemeChanged += OnThemeChanged;
            ActualThemeChanged -= OnActualThemeChanged;
            ActualThemeChanged += OnActualThemeChanged;
            AttachThemeHostToSteps();
            // 【主题复核·返修】Loaded 做一次【完整刷新】：控件 Unloaded 退订期间若在别处切了主题，
            // 回到缓存页时 StatusBrush 仍是旧主题的取值——必须重算现有行的状态色。
            RefreshAllRows();
        };
        Unloaded += (_, _) =>
        {
            TubaWinUi3.Services.ThemeService.ThemeChanged -= OnThemeChanged;
            ActualThemeChanged -= OnActualThemeChanged;
        };
    }

    private void OnThemeChanged(ElementTheme _)
    {
        AttachThemeHostToSteps();
        RefreshAllRows();
    }

    /// <summary>【主题复核】即使元素级主题（ActualTheme）变化也重算所有行的状态色。</summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        AttachThemeHostToSteps();
        RefreshAllRows();
    }

    private void RefreshAllRows()
    {
        if (_attachedRun is null) return;
        foreach (var row in _attachedRun.Steps)
        {
            try { row.RefreshTheme(); } catch { }
        }
    }

    private void OnRunPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunVm.SummaryText) && _attachedRun is not null)
            SummaryText.Text = _attachedRun.SummaryText;

        // 步骤组执行完成 → 自动折叠为摘要（单步也折叠，只留一行摘要防占版面）
        if (e.PropertyName == nameof(RunVm.IsRunning) &&
            RunVm is { } run && !run.IsRunning)
        {
            SetExpanded(false, animate: true);
        }
    }

    private void HeaderButton_Click(object sender, RoutedEventArgs e)
        => SetExpanded(!RunVm!.IsExpanded, animate: true);

    /// <summary>历史加载：无动画地以折叠态呈现步骤链（摘要可见、步骤隐藏）。</summary>
    public void ShowCollapsed()
    {
        if (RunVm is { } run) run.IsExpanded = false;
        StepList.MaxHeight = 0;
        StepList.Opacity = 0;
        (ChevronIcon.RenderTransform as RotateTransform)!.Angle = 180;
    }

    private void SetExpanded(bool expanded, bool animate)
    {
        if (RunVm is null) return;
        RunVm.IsExpanded = expanded;
        var target = expanded ? ExpandedMaxHeight : 0.0;

        if (!animate)
        {
            StepList.MaxHeight = target;
            StepList.Opacity = expanded ? 1 : 0;
            (ChevronIcon.RenderTransform as RotateTransform)!.Angle = expanded ? 0 : 180;
            return;
        }

        var sb = new Storyboard();

        var height = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(expanded ? 250 : 180)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(height, StepList);
        Storyboard.SetTargetProperty(height, "MaxHeight");
        sb.Children.Add(height);

        var opacity = new DoubleAnimation
        {
            To = expanded ? 1.0 : 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(150))
        };
        Storyboard.SetTarget(opacity, StepList);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        sb.Children.Add(opacity);

        var chevron = new DoubleAnimation
        {
            To = expanded ? 0.0 : 180.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(expanded ? 250 : 180))
        };
        Storyboard.SetTarget(chevron, ChevronIcon.RenderTransform);
        Storyboard.SetTargetProperty(chevron, "Angle");
        sb.Children.Add(chevron);

        if (expanded) StepList.Opacity = 1;
        sb.Begin();
    }
}
