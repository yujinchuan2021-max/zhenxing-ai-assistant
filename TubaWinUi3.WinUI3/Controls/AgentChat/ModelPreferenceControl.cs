using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;

namespace TubaWinUi3.Controls.AgentChat;

/// <summary>Displays model preferences; the chat page owns the reply and selection state.</summary>
internal sealed class ModelPreferenceControl : Grid
{
    private readonly TextBlock _title = new()
    {
        FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        FontFamily = AppFonts.WinUI, TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 12, FontFamily = AppFonts.WinUI, TextWrapping = TextWrapping.Wrap,
    };
    private readonly List<Button> _choiceButtons = [];
    private readonly List<(TextBlock Label, TextBlock Hint)> _choiceTexts = [];
    private readonly ThemeRefreshScope _theme;
    private bool _ready;
    private bool _busy;
    private bool _failed;
    private string? _selectedId;

    internal event Action<ModelPreferenceChoice>? ChoiceSelected;

    internal IReadOnlyList<Button> ChoiceButtons => _choiceButtons;
    internal string? SelectedId => _selectedId;

    internal ModelPreferenceControl()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolFlowThemeResources.AddPalette(this);
        _theme = ThemeRefreshScope.AttachRenderedContent(this);
        var surface = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
        };
        _theme.Bind(surface, Border.BackgroundProperty, ToolFlowThemeResources.Canvas);
        _theme.Bind(surface, Border.BorderBrushProperty, ToolFlowThemeResources.Stroke);
        Children.Add(surface);

        var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        surface.Child = content;
        _theme.Bind(_title, TextBlock.ForegroundProperty, ToolFlowThemeResources.PrimaryText);
        _theme.Bind(_status, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
        content.Children.Add(_title);
        content.Children.Add(_status);

        foreach (var choice in ModelPreferenceQuestion.Choices)
        {
            var label = new TextBlock
            {
                FontSize = 13, FontFamily = AppFonts.WinUI, TextWrapping = TextWrapping.Wrap,
            };
            var hint = new TextBlock
            {
                FontSize = 12, FontFamily = AppFonts.WinUI, TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            };
            var buttonContent = new StackPanel
            {
                Spacing = 3, HorizontalAlignment = HorizontalAlignment.Stretch,
                Children = { label, hint },
            };
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
                Content = buttonContent, Tag = choice, IsEnabled = false,
            };
            button.Click += OnChoiceClicked;
            _choiceButtons.Add(button);
            _choiceTexts.Add((label, hint));
            content.Children.Add(button);
        }

        ApplyLocalization();
    }

    internal void SetActionState(bool ready, bool busy, string? selectedId = null, bool failed = false)
    {
        _ready = ready;
        _busy = busy;
        _selectedId = string.IsNullOrWhiteSpace(selectedId) ? null : selectedId;
        _failed = failed;
        ApplyLocalization();
    }

    internal void ApplyLocalization()
    {
        var choices = ModelPreferenceQuestion.Choices;
        _title.Text = L("AiChoice_Title", "你希望怎样选择 AI 模型？", "How would you like to choose an AI model?");
        var enabled = _ready && !_busy && !_failed && _selectedId is null;
        for (var i = 0; i < _choiceButtons.Count; i++)
        {
            var choice = choices[i];
            var button = _choiceButtons[i];
            var (label, hint) = _choiceTexts[i];
            button.Tag = choice;
            button.IsEnabled = enabled;
            label.Text = choice.Label;
            hint.Text = choice.Hint ?? "";
            hint.Visibility = string.IsNullOrWhiteSpace(choice.Hint) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(button, choice.Label);
            AutomationProperties.SetHelpText(button, choice.Hint ?? "");
        }

        var selected = choices.FirstOrDefault(choice => choice.Id == _selectedId);
        _status.Text = _selectedId is not null
            ? string.Format(L("AiChoice_Selected", "已选择：{0}", "Selected: {0}"), selected?.Label ?? _selectedId)
            : _failed
                ? L("AiChoice_Failed", "这次提问未完成，请让 AI 重新提问。", "This question did not finish. Ask the AI again.")
            : !_ready
                ? L("AiChoice_Waiting", "等待提问完成…", "Waiting for the question to finish…")
            : _busy
                ? L("AiChoice_Busy", "等待当前回复完成后再选择。", "Wait for the current reply to finish before choosing.")
                : L("AiChoice_Ready", "请选择一项，AI 会继续帮你。", "Choose an option so the AI can continue helping.");
        AutomationProperties.SetName(this, _title.Text);
        AutomationProperties.SetHelpText(this, _status.Text);
    }

    private void OnChoiceClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _busy || _failed || _selectedId is not null ||
            sender is not Button button || !button.IsEnabled || button.Tag is not ModelPreferenceChoice choice)
            return;

        // The page marks the question selected before sending the user's reply.
        ChoiceSelected?.Invoke(choice);
    }

    private static string L(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
}
