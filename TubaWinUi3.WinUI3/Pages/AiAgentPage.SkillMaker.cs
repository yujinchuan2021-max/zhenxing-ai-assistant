using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private async Task ShowSkillMakerAsync()
    {
        if (_skillEditorOpen || _lease.IsClosed || !IsLoaded || _awaitingConfirmation || _toolFlowActionOpen || _toolFlowResumeOpen || _godotPlanDialogOpen) return;
        _skillEditorOpen = true;
        var epoch = _displayEpoch;
        var session = _session;
        bool open = true, busy = false;
        SkillMakerSession? maker = null;
        SkillHubDraft? draft = null;
        using var stop = new CancellationTokenSource();
        bool Current() => open && !_lease.IsClosed && IsLoaded && epoch == _displayEpoch && ReferenceEquals(session, _session);
        bool CanLoad() => Current() && !_isProcessing && !_awaitingConfirmation && !_toolFlowInstalling && session?.IsRunning != true;
        static string L(string key, string fallback) => LocalizationService.L("SkillHub_" + key, fallback);
        var content = new StackPanel { Spacing = 10, Width = Math.Min(550, Math.Max(220, XamlRoot.Size.Width - 110)) };
        var hint = new TextBlock { Text = L("MakerHint", "说说技能要帮你做什么，AI 会用简短选项引导你。生成后可以修改、本机试用，再提交审核。"), TextWrapping = TextWrapping.Wrap };
        var question = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16 };
        var input = new TextBox { PlaceholderText = L("MakerGoal", "例如：帮我检查网页布局，给出三条最重要的修改"), MaxLength = 4000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
        var next = new Button { Content = L("Start", "开始制作") };
        var choices = new StackPanel { Spacing = 6 };
        var preview = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var name = new TextBox { Header = L("Name", "技能名称"), MaxLength = 80 };
        var description = new TextBox { Header = L("Description", "什么时候用"), MaxLength = 500, TextWrapping = TextWrapping.Wrap };
        var keywords = new TextBox { Header = L("Keywords", "触发词（用逗号分隔）"), MaxLength = 500 };
        var body = new TextBox { Header = L("Instructions", "技能指导（可以修改）"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 16000, Height = Math.Min(250, Math.Max(110, XamlRoot.Size.Height - 570)) };
        var save = new Button { Content = L("SaveLoad", "保存并加载") };
        var consent = new CheckBox { Content = new TextBlock { Text = L("ShareConsent", "我是内容作者，同意按 MIT 许可分享至技能库"), TextWrapping = TextWrapping.Wrap } };
        var submit = new Button { Content = L("Submit", "提交后台审核") };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        preview.Children.Add(name); preview.Children.Add(description); preview.Children.Add(keywords);
        preview.Children.Add(new Expander { Header = L("Instructions", "技能指导（可以修改）"), Content = body });
        preview.Children.Add(save); preview.Children.Add(consent); preview.Children.Add(submit);
        content.Children.Add(hint); content.Children.Add(question); content.Children.Add(choices); content.Children.Add(input);
        content.Children.Add(next); content.Children.Add(preview); content.Children.Add(feedback);
        var dialog = new ContentDialog { Title = L("Make", "制作技能"), XamlRoot = XamlRoot, RequestedTheme = ThemeService.CurrentElementTheme,
            CloseButtonText = L("Close", "关闭"), Content = new ScrollViewer { Content = content, MaxHeight = Math.Max(240, XamlRoot.Size.Height - 190),
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        void State()
        {
            next.IsEnabled = !busy; input.IsEnabled = !busy; choices.IsHitTestVisible = !busy;
            name.IsEnabled = description.IsEnabled = body.IsEnabled = keywords.IsEnabled = !busy;
            save.IsEnabled = !busy && draft is not null && CanLoad();
            submit.IsEnabled = !busy && draft is not null && consent.IsChecked == true;
            consent.IsEnabled = !busy;
        }
        SkillHubDraft Edited() => SkillHubDocument.Validate(draft! with { DisplayName = name.Text, Description = description.Text, SystemPromptFragment = body.Text,
            TriggerKeywords = keywords.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        async Task Answer(string answer)
        {
            if (!Current() || busy) return;
            busy = true; State(); feedback.Text = L("Making", "正在整理技能…");
            try
            {
                maker ??= new SkillMakerSession();
                var reply = await maker.ReplyAsync(answer, stop.Token);
                if (!Current()) return;
                feedback.Text = L("UsesModel", "使用全局配置的模型：") + maker.ModelName;
                choices.Children.Clear(); input.Text = "";
                if (reply.Draft is { } created)
                {
                    draft = created; name.Text = created.DisplayName; description.Text = created.Description; body.Text = created.SystemPromptFragment;
                    keywords.Text = string.Join("，", created.TriggerKeywords);
                    preview.Visibility = Visibility.Visible; question.Text = L("Preview", "技能草稿已准备好，检查后就可以加载。");
                    input.Visibility = next.Visibility = Visibility.Collapsed;
                }
                else
                {
                    question.Text = reply.Question; next.Content = L("Answer", "使用我的补充");
                    input.PlaceholderText = L("OptionalAnswer", "也可以简短补充自己的想法");
                    foreach (var option in reply.Options)
                    {
                        var button = new Button { Content = new TextBlock { Text = option, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
                        button.Click += async (_, _) => await Answer(option);
                        choices.Children.Add(button);
                    }
                }
            }
            catch (OperationCanceledException) { if (Current()) feedback.Text = L("Interrupted", "请求已停止，可以重试。"); }
            catch (SkillMakerConfigurationException) { if (Current()) feedback.Text = L("ConfigureAi", "请先在 AI 设置中配置接口和模型。"); }
            catch (Exception) { if (Current()) feedback.Text = L("GenerateFailed", "这次没有生成有效技能，请稍后重试；不会自动重复请求。"); }
            finally { busy = false; if (Current()) State(); }
        }
        next.Click += async (_, _) => await Answer(input.Text);
        consent.Checked += (_, _) => State(); consent.Unchecked += (_, _) => State();
        save.Click += (_, _) =>
        {
            if (!CanLoad() || busy) return;
            try
            {
                var edited = Edited(); SkillHubLocalStore.SaveAndLoad(edited, allowUpdate: true);
                session?.SetSkillEnabled(edited.Id, true); if (session is not null) SaveSessionGuarded(session);
                RefreshSkillsPanel(); feedback.Text = L("Loaded", "已保存并启用。下一条消息命中技能场景时使用。已有对话保留。");
            }
            catch (Exception) { feedback.Text = L("SaveFailed", "技能尚未加载，请检查名称、简介和正文；原有技能保持不变。"); }
        };
        submit.Click += async (_, _) =>
        {
            if (!Current() || busy || consent.IsChecked != true) return;
            busy = true; State(); feedback.Text = L("Submitting", "正在提交审核…");
            try
            {
                var receipt = await SkillHubClient.Official().SubmitAsync(Edited(), ConfigManager.GetDataDir(), stop.Token);
                if (Current()) feedback.Text = receipt.Status switch {
                    "accepted" => L("Accepted", "投稿已通过审核，已进入技能库。"),
                    "rejected" => L("Rejected", "这份投稿已退回。可以修改后重新提交。"),
                    _ => L("Pending", "服务器已收到，等待管理员审核。本机是否启用由你决定。") };
            }
            catch (Exception) { if (Current()) feedback.Text = L("SubmitFailed", "提交未完成，请检查内容和网络；已保存的本机技能保留。"); }
            finally { busy = false; if (Current()) State(); }
        };
        void OnUnload(object sender, RoutedEventArgs e) { stop.Cancel(); dialog.Hide(); }
        Unloaded += OnUnload; dialog.Closing += (_, _) => stop.Cancel();
        timer.Tick += (_, _) => { if (!Current()) { stop.Cancel(); dialog.Hide(); } else State(); };
        try { State(); timer.Start(); await dialog.ShowAsync(); }
        catch (Exception) { if (Current()) AddSystemBubble(L("OpenFailed", "技能制作窗口暂未打开，请稍后重试。")); }
        finally { open = false; stop.Cancel(); timer.Stop(); Unloaded -= OnUnload; maker?.Dispose(); _skillEditorOpen = false; }
    }
}
