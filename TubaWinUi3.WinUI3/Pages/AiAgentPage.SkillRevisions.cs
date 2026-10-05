using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private bool _skillEditorOpen;
    private async Task ShowSkillRevisionEditorAsync()
    {
        if (_skillEditorOpen || _lease.IsClosed || !IsLoaded || _awaitingConfirmation ||
            _toolFlowActionOpen || _toolFlowResumeOpen || _godotPlanDialogOpen) return;
        _skillEditorOpen = true;
        var epoch = _displayEpoch;
        var sessionId = _session?.Id;
        bool open = true, busy = false;
        using var stop = new CancellationTokenSource();
        bool Current() => open && !_lease.IsClosed && IsLoaded && epoch == _displayEpoch && sessionId == _session?.Id;
        bool CanApply() => Current() && !_isProcessing && !_awaitingConfirmation && !_toolFlowInstalling && _session?.IsRunning != true;
        var dataRoot = ConfigManager.GetDataDir();
        var store = new SkillRevisionStore(dataRoot);
        var control = new SkillRevisionEditorControl
        {
            Width = Math.Min(580, Math.Max(220, XamlRoot.Size.Width - 100)),
            BodyHeight = Math.Min(320, Math.Max(120, XamlRoot.Size.Height - 430)),
        };
        var dialog = new ContentDialog
        {
            Title = LocalizationService.L("SkillEdit_Title", "修改枕星目标助手"),
            Content = new ScrollViewer { Content = control, MaxHeight = Math.Max(220, XamlRoot.Size.Height - 200),
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = LocalizationService.L("SkillEdit_Close", "关闭"),
            XamlRoot = XamlRoot, RequestedTheme = ThemeService.CurrentElementTheme,
            DefaultButton = ContentDialogButton.Close,
        };
        SkillRevisionDraft? draft = null;
        void Render()
        {
            if (Current()) control.SetState(store.HasLocalTrial,
                draft is null || draft.EditedBody != control.BodyText || draft.ChangeSummary != control.SummaryText.Trim()
                    ? null : store.ReadReceipt(draft), busy, CanApply());
        }
        void Feedback(string key, string fallback) => control.SetFeedback(LocalizationService.L("SkillEdit_" + key, fallback));
        SkillRevisionDraft Save()
        {
            draft = store.SaveDraft(control.BodyText, control.SummaryText.Trim());
            return draft;
        }
        void OnUnloaded(object sender, RoutedEventArgs e) { stop.Cancel(); dialog.Hide(); }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { if (!Current()) { stop.Cancel(); dialog.Hide(); } else Render(); };
        Unloaded += OnUnloaded;
        // Closing can always abandon invalid/unsaved edits; the explicit Save,
        // Apply and Submit actions persist a valid draft before doing their work.
        dialog.Closing += (_, _) => stop.Cancel();
        control.ActionRequested += async action =>
        {
            if (!Current() || busy) return;
            try
            {
                if (action is SkillRevisionEditorAction.Apply or SkillRevisionEditorAction.Restore && !CanApply())
                { Feedback("WaitForReply", "当前回复结束后可试用或恢复；草稿可以先保存。"); return; }
                if (action == SkillRevisionEditorAction.Restore)
                {
                    store.RestoreOfficial(() => AiAgentWorkflowSkill.WriteDshProjection(dataRoot));
                    Feedback("Restored", "已恢复官方版本，下一条消息使用官方指导。");
                }
                else
                {
                    var frozen = Save();
                    switch (action)
                    {
                        case SkillRevisionEditorAction.Save:
                            Feedback("Saved", "草稿已保存。");
                            break;
                        case SkillRevisionEditorAction.Apply:
                            store.ApplyLocal(frozen, () => AiAgentWorkflowSkill.WriteDshProjection(dataRoot));
                            Feedback("Applied", "本机试用已开启，下一条消息使用修改版；已有对话保留。");
                            break;
                        case SkillRevisionEditorAction.Submit:
                            busy = true;
                            Render();
                            var receipt = await SkillRevisionUploadClient.CreateOfficial().SubmitAsync(frozen, stop.Token);
                            store.SaveReceipt(frozen, receipt);
                            if (Current()) Feedback("Submitted", "服务器已收到这份修改。本机试用状态保持不变。");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            { if (Current()) Feedback("SubmissionInterrupted", "提交已中断，草稿已保留；可用相同内容重试。"); }
            catch (HttpRequestException ex)
            {
                if (Current()) Feedback(ex.StatusCode == System.Net.HttpStatusCode.NotFound ? "ServiceUnavailable" : "SubmitFailed",
                    ex.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "审核服务尚未开放，草稿已保留。" : "提交未成功，草稿已保留，可以重试。");
            }
            catch (InvalidDataException)
            { if (Current()) Feedback("ValidationFailed", "请检查正文和修改说明：内容不能为空或过长，提交内容不能包含密钥。"); }
            catch (Exception)
            { if (Current()) Feedback("ActionFailed", "操作未完成。请检查修改内容和说明后重试；草稿与本机版本会分别保留。"); }
            finally
            {
                busy = false;
                if (Current()) Render();
            }
        };
        try
        {
            try
            {
                draft = store.LoadDraft();
                control.BodyText = draft?.EditedBody ?? SkillRevisionDocument.Split(store.ReadEffectiveDocument()).Body;
                control.SummaryText = draft?.ChangeSummary ?? "";
                Render();
            }
            catch (Exception)
            {
                control.BodyText = AiAgentWorkflowSkill.Body;
                Feedback("DraftUnreadable", "原草稿暂时无法读取，已显示官方内容。保存后会建立新草稿。");
            }
            timer.Start();
            await dialog.ShowAsync();
        }
        catch (Exception)
        { if (Current()) AddSystemBubble(LocalizationService.L("SkillEdit_OpenFailed", "技能编辑窗口暂未打开，请稍后重试。")); }
        finally
        {
            open = false;
            stop.Cancel();
            timer.Stop();
            Unloaded -= OnUnloaded;
            _skillEditorOpen = false;
        }
    }
}
