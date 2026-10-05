using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;

namespace TubaWinUi3.Controls.AgentChat;

public sealed partial class ReasoningDisclosureControl : UserControl
{
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private bool _expanded = true;

    public ReasoningDisclosureControl()
    {
        InitializeComponent();
    }

    public bool HasContent => ReasoningText.Text.Length > 0;
    public string ReasoningContent => ReasoningText.Text;

    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        ReasoningText.Text += chunk;
        MetaText.Text = MiscTexts.TSub($"{ReasoningText.Text.Length:N0} 字");
    }

    public void SetContent(string content, bool completed = true)
    {
        ReasoningText.Text = content ?? "";
        MetaText.Text = ReasoningText.Text.Length > 0 ? MiscTexts.TSub($"{ReasoningText.Text.Length:N0} 字") : "";
        if (completed) Complete(collapse: true);
    }

    public void Complete(bool collapse = true)
    {
        _timer.Stop();
        ThinkingProgress.IsActive = false;
        ThinkingProgress.Visibility = Visibility.Collapsed;
        ThinkingIcon.Visibility = Visibility.Visible;
        TitleText.Text = MiscTexts.T("思考过程");
        if (_timer.Elapsed.TotalSeconds >= 0.5)
            MetaText.Text = MiscTexts.TSub($"{_timer.Elapsed.TotalSeconds:F1} 秒 · {ReasoningText.Text.Length:N0} 字");
        if (collapse) SetExpanded(false);
    }

    private void HeaderButton_Click(object sender, RoutedEventArgs e)
        => SetExpanded(!_expanded);

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        ContentPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        AutomationProperties.SetName(HeaderButton, expanded ? MiscTexts.T("折叠思考过程") : MiscTexts.T("展开思考过程"));
    }
}
