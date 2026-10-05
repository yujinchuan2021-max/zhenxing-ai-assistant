using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Controls.AgentChat;

internal enum SkillRevisionEditorAction { Save, Apply, Submit, Restore }

/// <summary>Body-only editor. Rendering has no file or network effects.</summary>
internal sealed class SkillRevisionEditorControl : StackPanel
{
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBox _body = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        Height = 300, MaxLength = 128 * 1024 };
    private readonly TextBox _summary = new() { MaxLength = SkillRevisionDocument.MaxSummaryLength };
    private readonly TextBlock _local = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _review = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _apply = new();
    private readonly Button _submit = new();
    private readonly Button _save = new();
    private readonly Button _restore = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _secondary = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private bool _trial, _busy, _canApply = true;
    private SkillRevisionReceipt? _receipt;
    internal event Action<SkillRevisionEditorAction>? ActionRequested;
    internal string BodyText { get => _body.Text; set => _body.Text = value; }
    internal string SummaryText { get => _summary.Text; set => _summary.Text = value; }
    internal double BodyHeight { set => _body.Height = value; }

    internal SkillRevisionEditorControl()
    {
        Spacing = 10;
        ToolFlowThemeResources.AddPalette(this);
        var theme = ThemeRefreshScope.AttachRenderedContent(this);
        theme.Bind(_apply, Button.BackgroundProperty, ToolFlowThemeResources.Accent);
        theme.Bind(_apply, Button.ForegroundProperty, ToolFlowThemeResources.OnAccent);
        foreach (var text in new[] { _hint, _local, _review, _feedback })
            theme.Bind(text, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
        ScrollViewer.SetVerticalScrollBarVisibility(_body, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_body, ScrollBarVisibility.Disabled);
        Children.Add(_hint);
        Children.Add(_body);
        Children.Add(_summary);
        Children.Add(_local);
        Children.Add(_review);
        _actions.Children.Add(_apply);
        _actions.Children.Add(_submit);
        _secondary.Children.Add(_save);
        _secondary.Children.Add(_restore);
        Children.Add(_actions);
        Children.Add(_secondary);
        Children.Add(_feedback);
        foreach (var (button, action) in new[] { (_apply, SkillRevisionEditorAction.Apply),
                     (_submit, SkillRevisionEditorAction.Submit), (_save, SkillRevisionEditorAction.Save),
                     (_restore, SkillRevisionEditorAction.Restore) })
            button.Click += (_, _) => { if (button.IsEnabled && !_busy) ActionRequested?.Invoke(action); };
        SizeChanged += (_, e) =>
        {
            _actions.Orientation = _secondary.Orientation = e.NewSize.Width < 360
                ? Orientation.Vertical : Orientation.Horizontal;
        };
        ApplyLocalization();
    }

    internal void SetState(bool localTrial, SkillRevisionReceipt? receipt, bool busy, bool canApply = true)
    { _trial = localTrial; _receipt = receipt; _busy = busy; _canApply = canApply; ApplyLocalization(); }

    internal void SetFeedback(string text) => _feedback.Text = text;

    internal void ApplyLocalization()
    {
        _hint.Text = L("Hint", "修改只影响本机；提交后由管理员审核。请勿填写密钥或个人信息。");
        _body.Header = L("Body", "技能指导内容");
        _summary.Header = L("Summary", "这次改了什么");
        _summary.PlaceholderText = L("SummaryPlaceholder", "例如：先确认预算，再推荐工具");
        _local.Text = _trial ? L("LocalTrial", "本机：正在试用修改版") : L("LocalOfficial", "本机：使用官方版本");
        _review.Text = _busy ? L("Submitting", "正在提交…") : _receipt?.Status switch
        {
            "pending" => L("Pending", "已提交，等待审核"),
            "accepted" => L("Accepted", "这份修改已被采纳"),
            "rejected" => L("Rejected", "这份修改已退回"),
            _ => L("NotSubmitted", "当前草稿尚未提交")
        };
        _apply.Content = L("Apply", "本机试用");
        _submit.Content = L("Submit", "提交审核");
        _save.Content = L("Save", "保存草稿");
        _restore.Content = L("Restore", "恢复官方版本");
        _apply.IsEnabled = !_busy && _canApply;
        _restore.IsEnabled = !_busy && _canApply && _trial;
        _save.IsEnabled = _submit.IsEnabled = !_busy;
        _body.IsReadOnly = _summary.IsReadOnly = _busy;
    }
    private static string L(string key, string fallback) => LocalizationService.L("SkillEdit_" + key,
        LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage ? key switch
        {
            "Hint" => "Changes apply locally. Submit a copy for administrator review. Do not include secrets or personal information.",
            "Body" => "Skill instructions",
            "Summary" => "What changed",
            "SummaryPlaceholder" => "For example: ask about budget before recommending tools",
            "LocalTrial" => "Local: trial version active",
            "LocalOfficial" => "Local: official version active",
            "Submitting" => "Submitting…",
            "Pending" => "Submitted, awaiting review",
            "Accepted" => "This revision was accepted",
            "Rejected" => "This revision was declined",
            "NotSubmitted" => "This draft has not been submitted",
            "Apply" => "Try locally",
            "Submit" => "Submit for review",
            "Save" => "Save draft",
            "Restore" => "Restore official version",
            _ => fallback
        } : fallback);
}
