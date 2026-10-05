using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.Agent;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

internal static class SkillEditorCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [ ("SkillEditor_BusyAndLocalizedState", BusyAndLocalizedState),
      ("SkillEditor_AttachedActionsAndNarrowLayout", AttachedActionsAndNarrowLayout) ];

    private static Task BusyAndLocalizedState()
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Dark");
        var control = new SkillRevisionEditorControl { BodyText = "USER EDITED BODY", SummaryText = "USER SUMMARY" };
        var field = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            field.SetValue(null, LocalizationService.ChineseLanguage);
            control.SetState(true, new(Guid.NewGuid(), "hash", "pending", DateTimeOffset.UtcNow), false);
            Assert.Contains("等待审核", Get<TextBlock>(control, "_review").Text);
            var apply = Get<Button>(control, "_apply");
            control.SetState(true, null, true);
            Assert.False(apply.IsEnabled);
            Assert.True(Get<TextBox>(control, "_body").IsReadOnly);
            Assert.False(Get<Button>(control, "_restore").IsEnabled);
            field.SetValue(null, LocalizationService.EnglishLanguage);
            control.ApplyLocalization();
            Assert.Equal("Try locally", apply.Content);
            Assert.Equal("USER EDITED BODY", control.BodyText);
            Assert.Equal("USER SUMMARY", control.SummaryText);
            control.SetState(false, null, false, canApply: false);
            Assert.False(apply.IsEnabled);
            Assert.True(Get<Button>(control, "_save").IsEnabled);
            Assert.True(Get<Button>(control, "_submit").IsEnabled);
        }
        finally { field.SetValue(null, previous); }
        return Task.CompletedTask;
    }

    private static async Task AttachedActionsAndNarrowLayout()
    {
        Assert.NotNull(DataRoots.EffectiveTestRoot);
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Dark");
        var control = new SkillRevisionEditorControl { Width = 300, BodyText = "Synthetic instructions.", SummaryText = "Synthetic change" };
        var actions = new List<SkillRevisionEditorAction>();
        control.ActionRequested += actions.Add;
        control.SetState(true, null, false);
        await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
        {
            root.Children.Add(control);
            await Task.Delay(120);
            root.UpdateLayout();
            Assert.True(control.ActualWidth > 0);
            Assert.Equal(Orientation.Vertical, Get<StackPanel>(control, "_actions").Orientation);
            Assert.NotNull(Get<TextBox>(control, "_body").XamlRoot);
            foreach (var name in new[] { "_apply", "_submit", "_save", "_restore" })
            {
                var button = Get<Button>(control, name);
                Assert.NotNull(button.XamlRoot);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(20);
            }
            Assert.Equal(new[] { SkillRevisionEditorAction.Apply, SkillRevisionEditorAction.Submit,
                SkillRevisionEditorAction.Save, SkillRevisionEditorAction.Restore }, actions);
            Assert.Equal("Synthetic instructions.", control.BodyText);
            Assert.Equal("Synthetic change", control.SummaryText);
        });
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
