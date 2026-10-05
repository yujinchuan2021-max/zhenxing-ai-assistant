using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Controls.AgentChat;
using TubaWinUi3.Services;
using TubaWinUi3.Services.ToolFlows;
using Xunit;

namespace TubaWinUi3.XamlHostRunner;

internal static class ChoiceCases
{
    internal static (string Name, Func<Task> Body)[] All() =>
    [
        ("Choice_CompletionAndSelectionStates", () => { CompletionAndSelectionStates(); return Task.CompletedTask; }),
        ("Choice_LanguageRefresh", () => { LanguageRefresh(); return Task.CompletedTask; }),
        ("ToolAccess_GuiAndCommandLineStates", () => { ToolAccessStates(); return Task.CompletedTask; }),
        ("ToolAccess_AttachedTemplate", AttachedToolAccessAsync),
        ("Choice_GeneralClarificationStates", () => { GeneralClarificationStates(); return Task.CompletedTask; }),
        ("Choice_PagePlanDialogCompletionRecovery", PageChoiceCases.PlanDialogCompletionRecovery),
    ];

    private static void CompletionAndSelectionStates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var control = new ModelPreferenceControl();
        var events = 0;
        control.ChoiceSelected += _ => events++;
        Assert.Equal(4, control.ChoiceButtons.Count);
        Assert.All(control.ChoiceButtons, b => Assert.False(b.IsEnabled));
        control.SetActionState(ready: true, busy: false);
        Assert.All(control.ChoiceButtons, b => Assert.True(b.IsEnabled));
        control.SetActionState(ready: true, busy: true);
        Assert.All(control.ChoiceButtons, b => Assert.False(b.IsEnabled));
        control.SetActionState(ready: false, busy: false, failed: true);
        Assert.All(control.ChoiceButtons, b => Assert.False(b.IsEnabled));
        control.SetActionState(ready: true, busy: false, selectedId: "local");
        Assert.Equal("local", control.SelectedId);
        Assert.All(control.ChoiceButtons, b => Assert.False(b.IsEnabled));
        Assert.Equal(0, events); // Showing a question or restoring its state sends nothing.
    }

    private static void LanguageRefresh()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var field = typeof(LocalizationService).GetField("_currentLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            field.SetValue(null, LocalizationService.ChineseLanguage);
            var control = new ModelPreferenceControl();
            control.SetActionState(ready: true, busy: false);
            Assert.Contains("付费", ((ModelPreferenceChoice)control.ChoiceButtons[0].Tag).Answer);
            var localButton = control.ChoiceButtons[2];
            Assert.Contains("显存", ((ModelPreferenceChoice)localButton.Tag).Answer);
            field.SetValue(null, LocalizationService.EnglishLanguage);
            control.ApplyLocalization();
            Assert.Same(localButton, control.ChoiceButtons[2]);
            Assert.Equal("local", ((ModelPreferenceChoice)localButton.Tag).Id);
            Assert.Contains("VRAM", ((ModelPreferenceChoice)localButton.Tag).Answer);
            Assert.All(control.ChoiceButtons, b => Assert.True(b.IsEnabled));
        }
        finally { field.SetValue(null, previous); }
    }

    private static void RequireIsolatedData()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZXAI_DATA_ROOT")));
        Assert.NotNull(DataRoots.EffectiveTestRoot);
    }

    private static void GeneralClarificationStates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var prompt = new ChatChoicePrompt("你能正常使用这个服务吗？",
        [new("yes", "能正常用", "我能正常使用这个服务。"),
         new("no", "打不开或没有账号", "我无法打开或没有可用账号。"),
         new("unknown", "不确定", "我还不确定是否可以使用。")]);
        var control = new ChatChoiceControl(prompt);
        var events = 0;
        control.ChoiceSelected += _ => events++;
        Assert.Equal(3, control.ChoiceButtons.Count);
        Assert.All(control.ChoiceButtons, button => Assert.False(button.IsEnabled));
        control.SetActionState(ready: true, busy: false);
        Assert.All(control.ChoiceButtons, button => Assert.True(button.IsEnabled));
        control.SetActionState(ready: true, busy: true);
        Assert.All(control.ChoiceButtons, button => Assert.False(button.IsEnabled));
        control.SetActionState(ready: false, busy: false, failed: true);
        Assert.All(control.ChoiceButtons, button => Assert.False(button.IsEnabled));
        control.SetActionState(ready: true, busy: false, selectedId: "no");
        Assert.Equal("no", control.SelectedId);
        Assert.All(control.ChoiceButtons, button => Assert.False(button.IsEnabled));
        control.ApplyLocalization();
        Assert.Equal("我无法打开或没有可用账号。", ((ChatChoiceOption)control.ChoiceButtons[1].Tag).Answer);
        Assert.Equal(0, events); // Displaying a question never sends an answer or performs a tool action.
    }

    private static void ToolAccessStates()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        var control = new ToolFlowToolAccessControl();
        var events = 0;
        control.ActionRequested += (_, _) => events++;
        var gui = new ToolFlowToolAccessEntry("godot", "Godot", @"C:\fake\Godot.exe", @"C:\fake", true);
        var cli = new ToolFlowToolAccessEntry("node", "Node.js", @"C:\fake\node.exe", @"C:\fake", false);
        control.Update([gui, cli], busy: false);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, control.Visibility);
        var expander = Assert.IsType<Expander>(Assert.Single(control.Children));
        var rows = (StackPanel)((ScrollViewer)expander.Content).Content;
        var guiButtons = ((StackPanel)rows.Children[0]).Children.OfType<StackPanel>().Single();
        var cliButtons = ((StackPanel)rows.Children[1]).Children.OfType<StackPanel>().Single();
        Assert.Equal(3, guiButtons.Children.Count);
        Assert.Single(cliButtons.Children); // Command-line tools expose their folder, not a fake GUI shortcut.
        Assert.All(guiButtons.Children.OfType<Button>(), b => Assert.True(b.IsEnabled));
        control.Update([gui, cli], busy: true);
        Assert.All(rows.Children.OfType<StackPanel>().SelectMany(row =>
            row.Children.OfType<StackPanel>().Single().Children.OfType<Button>()), b => Assert.False(b.IsEnabled));
        control.Update([], busy: false);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, control.Visibility);
        Assert.Equal(0, events); // Discovery and rendering never launch programs or write the desktop.
    }

    private static async Task AttachedToolAccessAsync()
    {
        RequireIsolatedData();
        using var theme = new XamlThemeCases.ThemeTestEnvironment("Light");
        Application.Current.Resources.MergedDictionaries.Add(new XamlControlsResources());
        await ChatScrollProbeCases.WithWindowAsync(async (_, root) =>
        {
            // Control state checks alone do not apply the native Expander template.
            // Attach a stock Expander first to distinguish the product control's
            // type/style mismatch from a missing Windows UI runtime.
            Console.WriteLine("TEMPLATE|creating-stock-expander");
            Console.Out.Flush();
            var stock = new Expander { Header = "Native template baseline", Content = new TextBlock { Text = "Fake tool" }, IsExpanded = true };
            root.Children.Add(stock);
            await Task.Delay(150);
            root.UpdateLayout();
            Assert.True(stock.ActualHeight > 0);
            Assert.True(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(stock) > 0);
            Console.WriteLine("TEMPLATE|stock-expander|PASS");
            Console.Out.Flush();
            root.Children.Clear();

            var task = new ToolFlowTaskControl();
            // Populate before attaching, as confirmation/restoration does in the
            // product. These fake paths are never checked, opened or installed.
            var gui = new ToolFlowToolAccessEntry("godot", "Godot", @"C:\fake\Godot.exe", @"C:\fake", true);
            var cli = new ToolFlowToolAccessEntry("node", "Node.js", @"C:\fake\node.exe", @"C:\fake", false);
            task.ToolAccess.Update([gui, cli], busy: false);
            var host = new ContentControl { Content = task };
            root.Children.Add(host);
            await Task.Delay(150);
            root.UpdateLayout();
            Assert.True(task.ToolAccess.ActualHeight > 0);
            var expander = Assert.IsType<Expander>(Assert.Single(task.ToolAccess.Children));
            Assert.True(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(expander) > 0);
            Assert.True(expander.IsExpanded);
            var events = 0;
            task.ToolAccess.ActionRequested += (_, _) => events++;
            Console.WriteLine("TEMPLATE|populated-task-tool-access|PASS");
            task.ToolAccess.Update([gui, cli], busy: true);
            await Task.Delay(100);
            root.UpdateLayout();
            expander.IsExpanded = false;
            task.ApplyLocalization();
            Assert.False(expander.IsExpanded); // Refresh preserves the user's collapse state.
            task.ToolAccess.Update([], busy: false);
            root.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, task.ToolAccess.Visibility);
            task.ToolAccess.Update([gui], busy: false);
            await Task.Delay(100);
            root.UpdateLayout();
            Assert.True(task.ToolAccess.ActualHeight > 0);
            Assert.False(expander.IsExpanded); // An intermediate empty probe must preserve manual folding.
            task.ToolAccess.Reset(); // A genuinely new task has its own first reveal.
            task.ToolAccess.Update([gui], busy: false);
            await Task.Delay(100);
            root.UpdateLayout();
            Assert.True(expander.IsExpanded);
            // The resume list creates this control directly instead of inside a task.
            root.Children.Clear();
            var resumed = new ToolFlowToolAccessControl();
            resumed.Update([gui, cli], busy: false);
            root.Children.Add(resumed);
            await Task.Delay(100);
            root.UpdateLayout();
            Assert.True(resumed.ActualHeight > 0);
            Assert.Equal(0, events); // Template/display never launches or installs anything.
        });
    }
}
