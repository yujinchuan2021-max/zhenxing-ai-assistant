using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class PcTutorialTool : IBuiltinTool
{
    public string Id => "pc-tutorial";
    public string Name => "电脑使用教程";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "新电脑开箱指南、基础操作、烤机检测、常识与辟谣，手把手教你用好电脑";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE8D7";
    public string Category => "实用工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        try
        {
            App.MainWindow?.NavigateToToolPage(typeof(PcTutorialPage));
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcTutorial] Page constructor FAILED: {ex.Message}\n{ex.StackTrace}");
            ShowErrorDialog(context, ex);
            return Task.CompletedTask;
        }
    }

    private static async void ShowErrorDialog(BuiltinToolContext context, Exception ex)
    {
        try
        {
            var dialog = context.CreateDialog("电脑使用教程 - 启动失败");
            dialog.Content = new Microsoft.UI.Xaml.Controls.ScrollViewer
            {
                Content = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = $"{ex.Message}\n\n{ex.StackTrace}",
                    IsTextSelectionEnabled = true,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                }
            };
            dialog.CloseButtonText = "确定";
            await dialog.ShowAsync();
        }
        catch { }
    }
}
