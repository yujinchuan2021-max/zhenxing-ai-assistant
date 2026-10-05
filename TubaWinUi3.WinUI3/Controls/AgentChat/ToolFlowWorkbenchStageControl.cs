using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;

namespace TubaWinUi3.Controls.AgentChat;

/// <summary>
/// Four preparation stages, not project completion. The final stage is always
/// pending or current: opening an entry never proves account access or success.
/// </summary>
internal sealed class ToolFlowWorkbenchStageControl : Grid
{
    private readonly ThemeRefreshScope _theme;
    private readonly List<Border> _cards = [];
    private readonly List<TextBlock> _labels = [];
    private readonly List<TextBlock> _states = [];
    private int _columns;
    internal int CurrentStageIndex { get; private set; }
    internal int RecordedStageCount { get; private set; }

    internal ToolFlowWorkbenchStageControl()
    {
        _theme = ToolFlowThemeResources.Attach(this);
        RowSpacing = 8;
        ColumnSpacing = 8;
        for (var i = 0; i < 4; i++)
        {
            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 9, 10, 9) };
            var content = new StackPanel { Spacing = 4 };
            var label = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap };
            var state = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
            ToolFlowThemeResources.Bind(_theme, card, Border.BackgroundProperty, ToolFlowThemeResources.Surface);
            ToolFlowThemeResources.Bind(_theme, card, Border.BorderBrushProperty, ToolFlowThemeResources.Stroke);
            ToolFlowThemeResources.BindText(_theme, label, ToolFlowThemeResources.PrimaryText);
            ToolFlowThemeResources.BindText(_theme, state, ToolFlowThemeResources.SecondaryText);
            content.Children.Add(label);
            content.Children.Add(state);
            card.Child = content;
            _cards.Add(card);
            _labels.Add(label);
            _states.Add(state);
            Children.Add(card);
        }
        SizeChanged += (_, e) => ArrangeStages(e.NewSize.Width);
        ArrangeStages(320);
    }

    internal void Update(bool selectionConfirmed, bool hasGoal, bool canTry)
    {
        CurrentStageIndex = !selectionConfirmed ? (hasGoal ? 1 : 0) : canTry ? 3 : 2;
        // Identifying a tentative goal does not silently confirm either choice.
        RecordedStageCount = !selectionConfirmed ? 0 : canTry ? 3 : 2;
        var titles = new[]
        {
            Text("AiWorkbench_StageGoal", "确定目标", "Define goal"),
            Text("AiWorkbench_StagePlan", "选择方案", "Choose plan"),
            Text("AiWorkbench_StagePrepare", "准备工具", "Prepare tools"),
            Text("AiWorkbench_StageUse", "开始使用", "Start using"),
        };
        for (var i = 0; i < _cards.Count; i++)
        {
            _labels[i].Text = $"{i + 1:00}  {titles[i]}";
            _states[i].Text = i < RecordedStageCount
                ? i == 2 ? Text("AiWorkbench_PreparationRecorded", "准备有记录", "Preparation recorded")
                    : Text("AiWorkbench_Confirmed", "已确认", "Confirmed")
                : i == CurrentStageIndex ? Text("AiWorkbench_Current", "当前阶段", "Current stage")
                    : Text("AiWorkbench_Upcoming", "接下来", "Up next");
            // Width is a second, non-color signal. Semantic strokes stay on the
            // same palette so remount/theme refresh never keeps an old accent.
            _cards[i].BorderThickness = new Thickness(i == CurrentStageIndex ? 2 : 1);
            AutomationProperties.SetName(_cards[i], titles[i] + ": " + _states[i].Text);
        }
        ToolFlowThemeResources.Refresh(_theme);
    }

    private void ArrangeStages(double width)
    {
        var columns = width < 560 ? 2 : 4;
        if (_columns == columns) return;
        _columns = columns;
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4 / columns; i++)
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _cards.Count; i++)
        {
            SetColumn(_cards[i], i % columns);
            SetRow(_cards[i], i / columns);
        }
    }

    private static string Text(string key, string zh, string en) => LocalizationService.L(key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? en : zh);
}
