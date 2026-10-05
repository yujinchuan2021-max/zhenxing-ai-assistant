using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services;
using Windows.UI;

namespace TubaWinUi3.Pages;

public sealed partial class SetupWizardDialog : ContentDialog
{
    private int _currentStep;
    private BackdropType _backdropType = BackdropType.Mica;

    private readonly Border[] _backdropOptions = [];

    public SetupWizardDialog()
    {
        InitializeComponent();

        _backdropOptions = [BackdropMicaOption, BackdropMicaAltOption, BackdropAcrylicOption];

        UpdateStepUI();
    }

    private void UpdateStepUI()
    {
        Step0Content.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1Content.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Content.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;

        StepPager.SelectedPageIndex = _currentStep;

        switch (_currentStep)
        {
            case 0:
                StepTitleText.Text = LocalizationService.L("Wizard_Step0Title", "欢迎使用枕星图吧AI助手");
                StepSubtitleText.Text = LocalizationService.L("Wizard_Step0Subtitle", "从 AI 助手说出目标开始：先理解需求，再组合可选工具流并把能自动的装好配好；需要你操作的步骤会逐步引导。图吧工具箱是附加的现成工具与排障底座。请先阅读以下重要信息。");
                PrimaryButtonText = LocalizationService.L("Wizard_Next", "下一步");
                SecondaryButtonText = LocalizationService.L("Wizard_Back", "上一步");
                IsSecondaryButtonEnabled = false;
                CloseButtonText = LocalizationService.L("Wizard_Skip", "跳过");
                break;
            case 1:
                StepTitleText.Text = LocalizationService.L("Wizard_Step1Title", "选择背景材质");
                StepSubtitleText.Text = LocalizationService.L("Wizard_Step1Subtitle", "不同的材质会为窗口带来不同的视觉效果。");
                PrimaryButtonText = LocalizationService.L("Wizard_Next", "下一步");
                SecondaryButtonText = LocalizationService.L("Wizard_Back", "上一步");
                IsSecondaryButtonEnabled = true;
                CloseButtonText = LocalizationService.L("Wizard_Skip", "跳过");
                break;
            case 2:
                StepTitleText.Text = LocalizationService.L("Wizard_Step2Title", "搜索集成");
                StepSubtitleText.Text = LocalizationService.L("Wizard_Step2Subtitle", "让工具出现在 Windows 搜索中，方便快速启动。");
                PrimaryButtonText = LocalizationService.L("Wizard_Finish", "完成");
                SecondaryButtonText = LocalizationService.L("Wizard_Back", "上一步");
                IsSecondaryButtonEnabled = true;
                CloseButtonText = LocalizationService.L("Wizard_Skip", "跳过");
                break;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_currentStep < 2)
        {
            args.Cancel = true;
            _currentStep++;
            UpdateStepUI();
        }
        else
        {
            ApplySettings();
        }
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_currentStep > 0)
        {
            _currentStep--;
            UpdateStepUI();
        }
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        AppSettings.Set("SetupCompleted", true);
    }

    private void BackdropOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        if (!Enum.TryParse<BackdropType>(border.Tag?.ToString(), out var type)) return;
        _backdropType = type;
        UpdateBackdropOptionSelection(border);
    }

    private void UpdateBackdropOptionSelection(Border selected)
    {
        foreach (var border in _backdropOptions)
        {
            if (border is null) continue;
            var isSelected = border == selected;
            border.BorderBrush = isSelected
                ? (Brush)App.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)App.Current.Resources["SubtleFillColorSecondaryBrush"];
        }
    }

    private void ApplySettings()
    {
        BackdropService.SetBackdropType(_backdropType);

        var enableSearchIndex = WizardSearchIndexToggle.IsOn;
        AppSettings.Set("WindowsSearchIndex", enableSearchIndex);
        if (enableSearchIndex)
            _ = WindowsSearchIndexService.RegisterAllToolsAsync();

        AppSettings.Set("SetupCompleted", true);
    }
}
