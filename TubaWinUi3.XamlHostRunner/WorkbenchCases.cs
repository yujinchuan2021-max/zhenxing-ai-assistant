using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Pages;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.Ai;
using TubaWinUi3.Services.ToolFlows;
using Windows.Foundation;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

/// <summary>
/// Actual AiAgentPage in the isolated TestApp. All sessions are inert, all saved
/// selections stay under ZXAI_DATA_ROOT, and all items are synthetic web services
/// with no install target. Only the workbench/chat navigation handlers are invoked.
/// No model, browser, installer, console, account or user directory is opened.
/// </summary>
internal static class WorkbenchCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("Workbench_RealPage_WideFirstMessage", WideFirstMessage),
        ("Workbench_RealPage_RealPlansHaveReadingWidth", RealPlansHaveReadingWidth),
        ("Workbench_RealPage_NarrowTabsAndDraft", NarrowTabsAndDraft),
        ("Workbench_RealPage_ThemeAndRemount", ThemeAndRemount),
        ("Workbench_RealPage_ConversationOwnership", ConversationOwnership),
        ("Workbench_RealPage_PendingChoiceAttention", PendingChoiceAttention),
    ];

    private static Task WideFirstMessage() => WithPage(async (page, window, root) =>
    {
        await Resize(window, root, 1603);
        Assert.Equal(Visibility.Collapsed, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(page, "WorkbenchTabs").Visibility);
        var messages = Named<StackPanel>(page, "MsgPanel");
        Invoke(page, "AddUserBubble", "我想制作一段音乐。");
        await Settle(root);
        AssertChatWithinViewport(page, "first message before selection");
        var firstMessage = Assert.Single(messages.Children);

        var session = new SyntheticSession();
        Set(page, "_session", session);
        var selection = SaveSelection(session.Id, "将一句想法变成音乐");
        Invoke(page, "RestoreCurrentToolFlowTask");
        await Settle(root);
        var body = Named<Grid>(page, "WorkspaceBody");
        var task = Named<ScrollViewer>(page, "WorkbenchScroll");
        var assistant = Named<Grid>(page, "AssistantPane");
        Assert.True(body.ActualWidth >= 1080, "The fixture must exercise the wide split layout.");
        Assert.Equal(Visibility.Visible, task.Visibility);
        Assert.Equal(Visibility.Visible, assistant.Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(page, "WorkbenchTabs").Visibility);
        Assert.InRange(task.ActualWidth, 400, 561);
        Assert.True(assistant.ActualWidth >= 639, "A plan needs a readable conversation pane, not a fixed narrow sidebar.");
        Assert.True(assistant.ActualWidth > task.ActualWidth, "The current step stays compact while the plan has room to read.");
        Assert.True(X(assistant, body) >= task.ActualWidth, "The assistant must sit to the right of the task.");
        Assert.Same(firstMessage, Assert.Single(messages.Children));
        Assert.Equal(selection.SubmissionId, Get<ToolFlowSelection>(page, "_taskSelection").SubmissionId);
        AssertWorkbenchWithinViewport(page);
        AssertChatWithinViewport(page, "wide split after selection");
        Assert.Equal(0, session.SendCalls);
        if (Preview) await GoalGuideCases.SaveControlPreviewAsync(page, "workbench-real-page-wide-light.png");
    });

    private static Task RealPlansHaveReadingWidth() => WithPage(async (page, window, root) =>
    {
        // Reserve the same kind of space as the product's outer navigation rail.
        // AiAgentPage's own conversation rail remains real and participates in layout.
        page.Margin = new Thickness(204, 0, 0, 0);
        var session = new SyntheticSession();
        Set(page, "_session", session);
        SaveSelection(session.Id, "准备一个 2D 游戏项目所需的工具，并保留后续调整方案的入口。");
        Invoke(page, "RestoreCurrentToolFlowTask");
        Invoke(page, "AddUserBubble", "请对比这三个方案，我想知道工具准备好以后从哪里开始。");
        var bubble = Assert.IsType<AssistantBubble>(Invoke(page, "BeginAssistantBubble", false));
        bubble.StreamingRow.Visibility = Visibility.Collapsed;
        bubble.ContentHost.Content = new TextBlock
        {
            Text = "这三套方案均围绕当前目标。选定方案后，在左侧查看当前一步；有需要时继续补充需求。",
            TextWrapping = TextWrapping.Wrap, FontSize = 14,
        };
        ToolFlowRecommendationOption Option(string id, string name) => new(
            id, name, "按照目标准备工具、接入模型，并保留清楚的使用入口。",
            "想先做出一个可以运行的小项目，再逐步扩展。", "根据所选服务核对费用。",
            "先核对设备、网络与服务账号；这里仅展示，不执行安装。", [],
            [new ToolFlowItem { ItemId = Guid.NewGuid().ToString("D"), Name = "演示工具", Kind = "service",
                SourceUrl = "https://example.invalid/layout-only" }], true, "", "synthetic display only");
        var cards = new ToolFlowCardsControl(new ToolFlowRecommendationSet(
            "隔离方案宽度验收", "medium",
            [Option("light", "轻量方案 · 从一个小项目开始"), Option("medium", "中量方案 · 完整工具流程"),
                Option("heavy", "重量方案 · 更复杂的项目")], []));
        bubble.ToolFlowActions.Children.Add(cards);
        var messages = Named<StackPanel>(page, "MsgPanel");
        var taskControl = Named<ContentControl>(page, "ToolFlowTaskHost").Content;
        var input = Named<TextBox>(page, "InputBox");
        input.Text = "方案还没决定，保留这段草稿。";
        var splitCount = 0;
        var compactCount = 0;
        foreach (var width in new[] { 2032, 1603, 1360, 760, 2032 })
        {
            await Resize(window, root, width, 1104);
            var body = Named<Grid>(page, "WorkspaceBody");
            var assistant = Named<Grid>(page, "AssistantPane");
            if (body.ActualWidth >= 1080)
            {
                splitCount++;
                Assert.Equal(Visibility.Collapsed, Named<StackPanel>(page, "WorkbenchTabs").Visibility);
                Assert.InRange(Named<ScrollViewer>(page, "WorkbenchScroll").ActualWidth, 400, 561);
                Assert.True(assistant.ActualWidth >= 639);
                AssertWorkbenchWithinViewport(page);
                Assert.True(cards.ActualWidth >= 590,
                    $"The plan body must retain reading width after every nested inset: {cards.ActualWidth}.");
            }
            else
            {
                compactCount++;
                Assert.Equal(Visibility.Visible, Named<StackPanel>(page, "WorkbenchTabs").Visibility);
                Click(page, Named<ToggleButton>(page, "WorkbenchChatTab"));
                await Settle(root);
                Assert.Equal(Visibility.Collapsed, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
                Assert.InRange(Math.Abs(assistant.ActualWidth - body.ActualWidth), 0, 1);
            }
            await WaitForMessageAnimationsAsync(page, root);
            Assert.Same(taskControl, Named<ContentControl>(page, "ToolFlowTaskHost").Content);
            Assert.Same(messages, Named<StackPanel>(page, "MsgPanel"));
            Assert.Same(bubble.Root, messages.Children[1]);
            Assert.Same(cards, Assert.Single(bubble.ToolFlowActions.Children));
            Assert.Equal("方案还没决定，保留这段草稿。", input.Text);
            AssertChatWithinViewport(page, "real plans at " + width);
            AssertContained(bubble.ToolFlowActions, bubble.Root, "full-width plan body");
            Assert.InRange(X(bubble.ToolFlowActions, bubble.Root), 0, 14);
            Assert.True(bubble.ToolFlowActions.ActualWidth >= bubble.Root.ActualWidth - 28,
                "The avatar belongs in the heading; it must not reserve a column beside the plan body.");
            AssertContained(cards, Named<ScrollViewer>(page, "MsgScroll"), "plan comparison");
            foreach (var card in cards.Cards)
            {
                AssertContained(card, cards, "plan card");
                AssertContained(card.SelectButton, card, "plan action");
                foreach (var textName in new[] { "_summary", "_costValue", "_requirementsValue" })
                {
                    var text = Field<TextBlock>(card, textName);
                    AssertContained(text, card, "plan text " + textName);
                    // WinUI reports a short TextBlock's natural text width even
                    // when its parent allocated a wider slot. Measure that real
                    // slot for available reading room, not the number of glyphs.
                    var textSlot = LayoutInformation.GetLayoutSlot(text);
                    Assert.True(textSlot.Width >= 190 && text.ActualWidth > 0 && text.ActualHeight > 0,
                        $"Plan text needs usable width after the card's own padding: {textName} slot={textSlot.Width}, text={text.ActualWidth}.");
                    Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                }
                Assert.False(card.SelectButton.IsEnabled, "This display fixture must never grant installation authority.");
            }
            AssertContained(input, Named<Border>(page, "ComposerHost"), "plan composer");
            if (Preview && width == 2032)
                await GoalGuideCases.SaveControlPreviewAsync(page, "workbench-real-page-plans-wide-light.png");
            if (Preview && width == 1360)
                await GoalGuideCases.SaveControlPreviewAsync(page, "workbench-real-page-plans-tabs-light.png");
        }
        Assert.True(splitCount > 0 && compactCount > 0);
        Assert.Equal(0, session.SendCalls);
    });

    private static Task NarrowTabsAndDraft() => WithPage(async (page, window, root) =>
    {
        var session = new SyntheticSession();
        Set(page, "_session", session);
        SaveSelection(session.Id, "制作一段演示音乐");
        Invoke(page, "RestoreCurrentToolFlowTask");
        Invoke(page, "AddUserBubble", "原有对话不会因切换任务页丢失。");
        var messages = Named<StackPanel>(page, "MsgPanel");
        var first = Assert.Single(messages.Children);
        var messageScroll = Named<ScrollViewer>(page, "MsgScroll");
        var taskControl = Named<ContentControl>(page, "ToolFlowTaskHost").Content;
        var input = Named<TextBox>(page, "InputBox");
        input.Text = "未发送的草稿，保留选择与内容。";
        var taskTab = Named<ToggleButton>(page, "WorkbenchTaskTab");
        var chatTab = Named<ToggleButton>(page, "WorkbenchChatTab");
        foreach (var width in new[] { 760, 420, 1100, 1603, 760 })
        {
            await Resize(window, root, width);
            var wide = Named<Grid>(page, "WorkspaceBody").ActualWidth >= 1080;
            if (!wide)
            {
                Assert.Equal(Visibility.Visible, Named<StackPanel>(page, "WorkbenchTabs").Visibility);
                Click(page, taskTab);
                await Settle(root);
                Assert.Equal(Visibility.Visible, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
                Assert.Equal(Visibility.Collapsed, Named<Grid>(page, "AssistantPane").Visibility);
                Assert.True(taskTab.IsChecked);
                AssertWorkbenchWithinViewport(page);
                Assert.True(taskTab.IsTabStop && chatTab.IsTabStop);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(chatTab)));
                Assert.Same(taskTab, FocusManager.FindFirstFocusableElement(Named<StackPanel>(page, "WorkbenchTabs")));
                Assert.Same(chatTab, FocusManager.FindLastFocusableElement(Named<StackPanel>(page, "WorkbenchTabs")));
                Assert.True(taskTab.Focus(FocusState.Keyboard), "The native workbench tab must accept keyboard focus.");
                Assert.Same(taskTab, FocusManager.GetFocusedElement(page.XamlRoot));
                Assert.True(chatTab.Focus(FocusState.Keyboard), "The native chat tab must accept keyboard focus.");
                Assert.Same(chatTab, FocusManager.GetFocusedElement(page.XamlRoot));

                Click(page, chatTab);
                await Settle(root);
                Assert.Equal(Visibility.Collapsed, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
                Assert.Equal(Visibility.Visible, Named<Grid>(page, "AssistantPane").Visibility);
                Assert.True(chatTab.IsChecked);
            }
            Assert.Same(taskControl, Named<ContentControl>(page, "ToolFlowTaskHost").Content);
            Assert.Same(messages, Named<StackPanel>(page, "MsgPanel"));
            Assert.Same(messageScroll, Named<ScrollViewer>(page, "MsgScroll"));
            Assert.Same(first, Assert.Single(messages.Children));
            Assert.Equal("未发送的草稿，保留选择与内容。", input.Text);
            AssertChatWithinViewport(page, "resize " + width);
            AssertContained(Named<Border>(page, "ComposerHost"), Named<Grid>(page, "AssistantPane"), "composer");
            AssertContained(input, Named<Border>(page, "ComposerHost"), "input");
        }
        Assert.Equal(0, session.SendCalls);
        if (Preview) await GoalGuideCases.SaveControlPreviewAsync(page, "workbench-real-page-narrow-chat.png");
        Invoke(page, "AddUserBubble", string.Concat(Enumerable.Repeat("这是一段用于验证滚动位置保留的长消息。", 180)));
        await Settle(root);
        messageScroll.ChangeView(null, messageScroll.ScrollableHeight, null, disableAnimation: true);
        await Settle(root);
        var offset = messageScroll.VerticalOffset;
        Assert.True(offset > 0, "A real vertical extent is needed to verify tab-switch scroll preservation.");
        Click(page, taskTab);
        await Settle(root);
        Click(page, chatTab);
        await Settle(root);
        Assert.InRange(Math.Abs(offset - messageScroll.VerticalOffset), 0, 1);

        Click(page, taskTab);
        await Settle(root);
        Assert.True(page.TryPrefillInput("从技能库带来的新问题，等待用户发送。"));
        await Settle(root);
        Assert.Equal(Visibility.Visible, Named<Grid>(page, "AssistantPane").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
        Assert.True(chatTab.IsChecked);
        Assert.Same(input, Named<TextBox>(page, "InputBox"));
        Assert.Equal("从技能库带来的新问题，等待用户发送。", input.Text);
        Assert.Equal(0, session.SendCalls);

        // A long, real selected goal must remain available without consuming the
        // entire compact conversation viewport or pushing the composer offscreen.
        var longGoal = string.Concat(Enumerable.Repeat("我想制作一段音乐，并且保留完整的目标要求。", 100));
        SaveSelection(session.Id, longGoal);
        Invoke(page, "RestoreCurrentToolFlowTask");
        await Resize(window, root, 420);
        Click(page, chatTab);
        await Settle(root);
        Assert.True(messageScroll.ViewportHeight > 120, "A long goal must leave usable room for the conversation.");
        var hint = Named<TextBlock>(page, "WorkbenchAssistantHint");
        Assert.InRange(hint.ActualHeight, 1, 70);
        Assert.Contains(longGoal, Assert.IsType<string>(ToolTipService.GetToolTip(hint)));
        AssertContained(input, Named<Border>(page, "ComposerHost"), "long-goal composer");
        Assert.Equal(0, session.SendCalls);
    });

    private static Task ThemeAndRemount() => WithPage(async (page, window, root) =>
    {
        Assert.Equal(ApplicationTheme.Dark, Application.Current.RequestedTheme);
        var session = new SyntheticSession();
        Set(page, "_session", session);
        SaveSelection(session.Id, "任务工作台主题验收");
        Invoke(page, "RestoreCurrentToolFlowTask");
        Invoke(page, "AddUserBubble", "浅色与深色都应清楚易读。");
        await Resize(window, root, 1603);
        var task = Assert.IsType<ToolFlowTaskControl>(Named<ContentControl>(page, "ToolFlowTaskHost").Content);
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
        {
            page.RequestedTheme = theme;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Settle(root);
            AssertPalette(page, task, theme);
            if (Preview) await GoalGuideCases.SaveControlPreviewAsync(page,
                "workbench-real-page-" + theme.ToString().ToLowerInvariant() + ".png");
        }
        root.Children.Remove(page);
        await Settle(root);
        Assert.False(page.IsLoaded);
        page.RequestedTheme = ElementTheme.Dark;
        root.Children.Add(page);
        await Settle(root);
        Assert.Same(task, Named<ContentControl>(page, "ToolFlowTaskHost").Content);
        Assert.Equal(Visibility.Visible, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
        AssertPalette(page, task, ElementTheme.Dark);
        page.RequestedTheme = ElementTheme.Light;
        await Settle(root);
        AssertPalette(page, task, ElementTheme.Light);
        AssertChatWithinViewport(page, "same page remounted");
        Assert.Equal(0, session.SendCalls);
    });

    private static Task ConversationOwnership() => WithPage(async (page, window, root) =>
    {
        await Resize(window, root, 760);
        var first = new SyntheticSession();
        var second = new SyntheticSession();
        var selectionA = SaveSelection(first.Id, "会话 A 的音乐目标");
        var selectionB = SaveSelection(second.Id, "会话 B 的图片目标");
        Set(page, "_session", first);
        Invoke(page, "RestoreCurrentToolFlowTask");
        await Settle(root);
        AssertCurrent(page, selectionA);
        Click(page, Named<ToggleButton>(page, "WorkbenchChatTab"));
        await Settle(root);
        Assert.Equal(Visibility.Collapsed, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);

        // The real restoration projection reads each owning conversation's own
        // isolated store record. No engine/session restoration is constructed.
        Set(page, "_session", second);
        Invoke(page, "RestoreCurrentToolFlowTask");
        await Settle(root);
        AssertCurrent(page, selectionB);
        Assert.True(Named<ToggleButton>(page, "WorkbenchTaskTab").IsChecked);
        Set(page, "_session", first);
        Invoke(page, "RestoreCurrentToolFlowTask");
        await Settle(root);
        AssertCurrent(page, selectionA);

        // A stale display request from another conversation must not reveal A.
        Set(page, "_session", new SyntheticSession());
        Invoke(page, "RefreshToolFlowTaskCard");
        await Settle(root);
        AssertNoTask(page);
        Invoke(page, "RestoreCurrentToolFlowTask");
        Assert.Null(Get<object?>(page, "_taskSelection"));
        Set(page, "_session", second);
        Invoke(page, "RestoreCurrentToolFlowTask");
        await Settle(root);
        AssertCurrent(page, selectionB);
        Invoke(page, "ResetToNewChat");
        await Settle(root);
        Assert.Null(Get<object?>(page, "_session"));
        Assert.Null(Get<object?>(page, "_taskSelection"));
        AssertNoTask(page);
        Assert.Empty(Named<StackPanel>(page, "MsgPanel").Children);
        Assert.NotNull(new ToolFlowSelectionStore().GetBySubmissionId(selectionA.SubmissionId));
        Assert.NotNull(new ToolFlowSelectionStore().GetBySubmissionId(selectionB.SubmissionId));
        Assert.Equal(0, first.SendCalls + second.SendCalls);
    });

    private static Task PendingChoiceAttention() => WithPage(async (page, window, root) =>
    {
        var session = new SyntheticSession();
        Set(page, "_session", session);
        SaveSelection(session.Id, "制作一段音乐");
        Invoke(page, "RestoreCurrentToolFlowTask");
        await Resize(window, root, 760);
        Assert.Equal(Visibility.Visible, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
        Set(page, "_sendEpoch", Get<int>(page, "_displayEpoch"));
        Set(page, "_toolFlowRoundStart", 0);
        Set(page, "_toolFlowDisplayRoundStart", 0);
        Set(page, "_toolFlowRoundSucceeded", false);
        Set(page, "_sendHadError", false);
        Invoke(page, "AddUserBubble", "请帮我决定下一步。");
        Set(page, "_isProcessing", true);
        Invoke(page, "UpdateInputState");
        const string reply = "请确认风格。\n```choice-question\n" +
            "{\"schema\":1,\"question\":\"想生成哪种音乐？\",\"options\":[" +
            "{\"id\":\"song\",\"label\":\"有人声\",\"answer\":\"生成有人声的歌曲。\"}," +
            "{\"id\":\"instrumental\",\"label\":\"纯音乐\",\"answer\":\"生成纯音乐。\"}]}\n```";
        Invoke(page, "AppendChunk", reply);
        Invoke(page, "FinalizeStreaming");
        Set(page, "_isProcessing", false);
        Invoke(page, "UpdateInputState");
        Invoke(page, "FinalizeStreaming");
        Invoke(page, "CompleteToolFlowMessageActions", Get<int>(page, "_sendEpoch"), true);
        Invoke(page, "RefreshWorkbenchAttention");
        await Settle(root);
        Assert.Equal(Visibility.Visible, Named<Grid>(page, "AssistantPane").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
        Assert.True(Named<ToggleButton>(page, "WorkbenchChatTab").IsChecked);
        var actions = Get<System.Collections.IEnumerable>(page, "_modelChoiceActions").Cast<object>().ToArray();
        var action = Assert.Single(actions);
        var question = Assert.IsType<ChatChoiceControl>(action.GetType().GetProperty("QuestionControl")!.GetValue(action));
        Assert.Equal(2, question.ChoiceButtons.Count);
        Assert.All(question.ChoiceButtons, button => Assert.True(button.IsEnabled && button.ActualHeight > 0));
        Click(page, Named<ToggleButton>(page, "WorkbenchTaskTab"));
        await Settle(root);
        for (var refresh = 0; refresh < 3; refresh++)
        {
            Invoke(page, "UpdateInputState");
            Invoke(page, "RefreshWorkbenchAttention");
            await Settle(root);
            Assert.Equal(Visibility.Visible, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<Grid>(page, "AssistantPane").Visibility);
        }
        Assert.Equal(0, session.SendCalls);
    });

    private static void AssertCurrent(AiAgentPage page, ToolFlowSelection selection)
    {
        Assert.Equal(selection.SubmissionId, Get<ToolFlowSelection>(page, "_taskSelection").SubmissionId);
        Assert.Equal(Visibility.Visible, Named<ContentControl>(page, "ToolFlowTaskHost").Visibility);
        Assert.Equal(Visibility.Visible, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
        var task = Assert.IsType<ToolFlowTaskControl>(Named<ContentControl>(page, "ToolFlowTaskHost").Content);
        Assert.Contains(selection.ProjectGoal, Field<TextBlock>(task, "_goal").Text);
    }

    private static void AssertNoTask(AiAgentPage page)
    {
        Assert.Equal(Visibility.Collapsed, Named<ContentControl>(page, "ToolFlowTaskHost").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<ScrollViewer>(page, "WorkbenchScroll").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(page, "WorkbenchTabs").Visibility);
        Assert.Equal(Visibility.Visible, Named<Grid>(page, "AssistantPane").Visibility);
    }

    private static void AssertPalette(AiAgentPage page, ToolFlowTaskControl task, ElementTheme theme)
    {
        Assert.Equal(theme, page.ActualTheme);
        Assert.Equal(theme, task.ActualTheme);
        var surface = Assert.IsType<Border>(Assert.Single(task.Children));
        ToolFlowPaletteAssertions.Readable(surface.Background, Field<TextBlock>(task, "_goal").Foreground,
            Field<TextBlock>(task, "_summary").Foreground, theme == ElementTheme.Light ? "Light" : "Dark");
        var currentSurface = Field<Border>(task, "_currentSurface");
        ToolFlowPaletteAssertions.ContrastAtLeast(currentSurface.Background, Field<TextBlock>(task, "_summary").Foreground, 4.5);
        ToolFlowPaletteAssertions.ContrastAtLeast(currentSurface.Background, Field<TextBlock>(task, "_next").Foreground, 4.5);
        var canvas = ThemeResourceResolver.ResolveBrush(page, "AssistantCanvasBrush");
        ToolFlowPaletteAssertions.ContrastAtLeast(canvas, Named<TextBlock>(page, "WorkbenchAssistantTitle").Foreground, 4.5);
        ToolFlowPaletteAssertions.ContrastAtLeast(canvas, Named<TextBlock>(page, "WorkbenchAssistantHint").Foreground, 4.5);
        var user = Assert.Single(Named<StackPanel>(page, "MsgPanel").Children.OfType<Border>()
            .Where(border => border.Child is TextBlock));
        var userText = Assert.IsType<TextBlock>(user.Child);
        ToolFlowPaletteAssertions.ContrastAtLeast(user.Background, userText.Foreground, 4.5);
        Assert.Equal(ToolFlowPaletteAssertions.Solid(ThemeResourceResolver.ResolveBrush(page, "AssistantAccentBrush")).Color,
            ToolFlowPaletteAssertions.Solid(user.Background).Color);
        Assert.Equal(ToolFlowPaletteAssertions.Solid(ThemeResourceResolver.ResolveBrush(page, "AssistantAccentForegroundBrush")).Color,
            ToolFlowPaletteAssertions.Solid(userText.Foreground).Color);
    }

    private static void AssertWorkbenchWithinViewport(AiAgentPage page)
    {
        var scroll = Named<ScrollViewer>(page, "WorkbenchScroll");
        var viewport = Named<Grid>(page, "WorkbenchViewport");
        Assert.True(scroll.ViewportWidth > 0);
        Assert.InRange(Math.Abs(scroll.ViewportWidth - viewport.ActualWidth), 0, 1);
        Assert.InRange(Math.Abs(scroll.ExtentWidth - scroll.ViewportWidth), 0, 1);
        Assert.Equal(0, scroll.HorizontalOffset);
        AssertContained(Named<ContentControl>(page, "ToolFlowTaskHost"), viewport, "task card");
    }

    private static void AssertChatWithinViewport(AiAgentPage page, string stage)
    {
        var scroll = Named<ScrollViewer>(page, "MsgScroll");
        var viewport = Named<Grid>(page, "MsgViewport");
        var messages = Named<StackPanel>(page, "MsgPanel");
        Assert.True(scroll.ViewportWidth > 0, stage);
        Assert.InRange(Math.Abs(scroll.ViewportWidth - viewport.ActualWidth), 0, 1);
        Assert.InRange(Math.Abs(scroll.ExtentWidth - scroll.ViewportWidth), 0, 1);
        Assert.Equal(0, scroll.HorizontalOffset);
        AssertContained(messages, scroll, stage);
        foreach (var child in messages.Children.OfType<FrameworkElement>())
        {
            AssertContained(child, scroll, stage);
            var x = X(child, messages);
            Assert.True(x >= messages.Padding.Left - 1 &&
                x + child.ActualWidth <= messages.ActualWidth - messages.Padding.Right + 1,
                $"{stage}: message x={x}, width={child.ActualWidth}, panel={messages.ActualWidth}, padding={messages.Padding}");
            // Check final typed geometry after the entrance animation duration.
            // Production commits the final values and stops its storyboard on
            // completion; previews additionally check actual rendered placement.
            // Raw ReadLocalValue boxes as IInspectable on this native host.
            if (child.RenderTransform is TranslateTransform translate)
            {
                Assert.InRange(Math.Abs(translate.X), 0, .5);
                Assert.InRange(Math.Abs(translate.Y), 0, .5);
                Assert.Equal(1d, child.Opacity);
            }
        }
    }

    private static void AssertContained(FrameworkElement child, FrameworkElement parent, string context)
    {
        var x = X(child, parent);
        Assert.True(x >= -1 && x + child.ActualWidth <= parent.ActualWidth + 1,
            $"{context}: child x={x}, width={child.ActualWidth}, parent width={parent.ActualWidth}");
    }

    private static double X(FrameworkElement child, FrameworkElement parent) => child.TransformToVisual(parent).TransformPoint(new Point()).X;
    private static void Click(AiAgentPage page, ToggleButton button)
    {
        Assert.True(button.IsLoaded && button.IsEnabled && button.ActualWidth > 0);
        // ToggleButton exposes Toggle, not Invoke; do not pretend that assigning
        // IsChecked is a pointer click. Check its native accessibility pattern,
        // then execute the real page click handler without OS input injection.
        Assert.IsAssignableFrom<IToggleProvider>(new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle));
        Invoke(page, button.Name + "_Click", button, new RoutedEventArgs());
    }
    private static bool Preview => Environment.GetEnvironmentVariable("ZXAI_GOAL_PREVIEW") == "1";
    private static async Task Resize(Window window, Grid root, int width, int height = 920)
    {
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-20000, -20000, width, height));
        await Settle(root);
    }
    private static async Task Settle(Grid root) { await Task.Delay(320); root.UpdateLayout(); }

    private static async Task WaitForMessageAnimationsAsync(AiAgentPage page, Grid root)
    {
        // A cold native host may arrange the newly visible pane only at the end
        // of Resize/Settle. Its entrance animation starts then, not when the test
        // created the bubble. Arrange first, allow one full entrance duration,
        // and observe stable real visual coordinates before asserting bounds.
        root.UpdateLayout();
        await Task.Delay(320);
        var messages = Named<StackPanel>(page, "MsgPanel");
        double[] Snapshot() => messages.Children.OfType<FrameworkElement>().SelectMany(child =>
        {
            var translate = child.RenderTransform as TranslateTransform;
            return new[] { X(child, messages), child.ActualWidth, child.ActualHeight,
                child.Opacity, translate?.X ?? 0, translate?.Y ?? 0 };
        }).ToArray();
        var previous = Snapshot();
        var stableSamples = 0;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(50);
            root.UpdateLayout();
            var current = Snapshot();
            var resting = messages.Children.OfType<FrameworkElement>().All(child =>
                Math.Abs(child.Opacity - 1) < .001 && (child.RenderTransform is not TranslateTransform translate ||
                    Math.Abs(translate.X) < .01 && Math.Abs(translate.Y) < .01));
            var stable = previous.Length == current.Length &&
                previous.Zip(current).All(pair => Math.Abs(pair.First - pair.Second) < .1);
            stableSamples = resting && stable ? stableSamples + 1 : 0;
            if (stableSamples >= 2) return;
            previous = current;
        }
        Assert.True(false, "Message entrance animation did not settle before geometry validation: " +
            string.Join(", ", previous.Select(value => value.ToString("0.###"))));
    }

    private static ToolFlowSelection SaveSelection(string conversationId, string goal)
    {
        var id = Guid.NewGuid().ToString("D");
        return new ToolFlowSelectionStore().SelectForInstall(new()
        {
            FlowId = id, SubmissionId = id, Origin = ToolFlowOrigin.User, ConversationId = conversationId,
            SelectedAtUtc = DateTimeOffset.UtcNow, FlowName = "隔离演示方案", ProjectGoal = goal,
            GoalDescription = goal, FlowText = "合成的页面布局验收，禁止执行操作。", UploadEnabledAtSelection = false,
            Conversation = [], Items = [new() { ItemId = Guid.NewGuid().ToString("D"), Name = "演示音乐服务", Kind = "service",
                SourceUrl = "https://example.invalid/workbench", ManualHint = "仅展示下一步，不执行服务访问。" }],
        });
    }

    private static async Task WithPage(Func<AiAgentPage, Window, Grid, Task> body)
    {
        var dataRoot = Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(dataRoot));
        Assert.NotNull(DataRoots.TestRoot);
        Assert.Equal(Path.GetFullPath(dataRoot!), Path.GetFullPath(ConfigManager.GetDataDir()), ignoreCase: true);
        Assert.Null(AiAssistantService.HistoryDirOverride);
        Assert.Null(AiProviderStore.StoragePathOverride);
        Assert.Empty(Environment.GetCommandLineArgs().Where(arg => arg.StartsWith("--zxtest-", StringComparison.OrdinalIgnoreCase)));
        Assert.IsType<TestApp>(Application.Current).EnsureSharedControlResources();
        var resolver = AgentEngine.RuntimeResolverOverrideForTest;
        var probe = typeof(AgentEngine).GetField("_probeStarted", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = typeof(AgentEngine).GetField("_dshState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousProbe = probe.GetValue(null);
        var previousState = state.GetValue(null);
        var previousTheme = ThemeResourceResolver.CurrentThemeKeyOverrideForTest;
        var previousDictionaries = ThemeResourceResolver.ExtraRootDictionariesForTest;
        var calls = 0;
        AiAgentPage? page = null;
        try
        {
            AgentEngine.RuntimeResolverOverrideForTest = () =>
            {
                calls++;
                throw new InvalidOperationException("Workbench fixtures may not resolve or start a real runtime.");
            };
            probe.SetValue(null, 1);
            state.SetValue(null, 2);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = null;
            ThemeResourceResolver.ExtraRootDictionariesForTest = null;
            await ChatScrollProbeCases.WithWindowAsync(async (window, root) =>
            {
                page = new AiAgentPage(compact: false, autoLoadLatest: false) { RequestedTheme = ElementTheme.Light };
                root.Children.Add(page);
                await Settle(root);
                Assert.True(page.IsLoaded && page.XamlRoot is not null);
                try { await body(page, window, root); }
                finally { root.Children.Remove(page); page.Unload(); page = null; }
            });
            Assert.Equal(0, calls);
        }
        finally
        {
            page?.Unload();
            probe.SetValue(null, previousProbe);
            state.SetValue(null, previousState);
            ThemeResourceResolver.CurrentThemeKeyOverrideForTest = previousTheme;
            ThemeResourceResolver.ExtraRootDictionariesForTest = previousDictionaries;
            AgentEngine.RuntimeResolverOverrideForTest = resolver;
        }
    }

    private static T Named<T>(AiAgentPage page, string name) where T : class =>
        page.FindName(name) as T ?? throw new InvalidOperationException("Named workbench element missing: " + name);
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new InvalidOperationException("Field missing: " + name));
    private static T Get<T>(AiAgentPage page, string name) => (T)PageField(name).GetValue(page)!;
    private static void Set(AiAgentPage page, string name, object? value) => PageField(name).SetValue(page, value);
    private static FieldInfo PageField(string name) => typeof(AiAgentPage).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Page field missing: " + name);
    private static object? Invoke(AiAgentPage page, string name, params object?[] arguments) =>
        (typeof(AiAgentPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Page method missing: " + name)).Invoke(page, arguments);

    private sealed class SyntheticSession : IAgentSession
    {
        public string Id { get; } = "workbench-session-" + Guid.NewGuid().ToString("N");
        public string Title => "Isolated workbench fixture";
        public bool IsRunning => false;
        public string PersonaId => AgentPersonaCatalog.DefaultId;
        public AgentSendOutcome LastSendOutcome => AgentSendOutcome.Completed;
        public int TotalPromptTokens => 0;
        public int TotalCompletionTokens => 0;
        public int TotalCacheHitTokens => 0;
        public int TotalCacheMissTokens => 0;
        public IReadOnlyCollection<string> ActiveSkillIds => [];
        internal int SendCalls { get; private set; }
        public event Action<string>? TextChunk { add { } remove { } }
        public event Action<string>? ReasoningChunk { add { } remove { } }
        public event Action<AgentStep>? StepStarted { add { } remove { } }
        public event Action<AgentStep>? StepCompleted { add { } remove { } }
        public event Action<IReadOnlyList<AgentConfirmationRequest>>? ConfirmationsRequested { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public event Action? RunCompleted { add { } remove { } }
        public event Action? RoundStarted { add { } remove { } }
        public event Action<AgentStepGroupSummary>? StepGroupCompleted { add { } remove { } }
        public Task SendAsync(string userText, IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null)
        { SendCalls++; throw new InvalidOperationException("No model send is allowed in this fixture."); }
        public Task ResumeConfirmationsAsync(IReadOnlyList<AgentConfirmationDecision> decisions) =>
            throw new InvalidOperationException("No confirmation is allowed in this fixture.");
        public void Cancel() { }
        public void SetSkillEnabled(string id, bool enabled) { }
        public void SetPersona(string personaId) { }
        public void Rename(string title) { }
        public void Save() { }
        public void Dispose() { }
    }
}
