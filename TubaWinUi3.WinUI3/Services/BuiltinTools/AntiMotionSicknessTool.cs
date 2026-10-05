using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

public sealed class AntiMotionSicknessTool : IBuiltinTool
{
    public string Id => "anti-motion-sickness";
    public string Name => "游戏防晕3D";
    /// <summary>显示名（按稳定 ID 的显示层翻译；数据键仍为 Name）。</summary>
    public string DisplayName => ToolDisplayTexts.BuiltinName(Id, Name);

    public string Description => "屏幕中央准星+四边标记辅助，缓解3D眩晕。一键帮你节约13块钱！";
    /// <summary>描述（显示层翻译；数据键仍为 Description）。</summary>
    public string DisplayDescription => ToolDisplayTexts.BuiltinDescription(Id, Description);

    public string Glyph => "\uE7FC";
    public string Category => "游戏工具";
    /// <summary>分类（显示层翻译；数据键仍为 Category）。</summary>
    public string DisplayCategory => LocalizationService.GetCategoryDisplayName(Category);

    public BuiltinToolKind Kind => BuiltinToolKind.Dialog;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        try
        {
            App.MainWindow?.NavigateToToolPage(typeof(AntiMotionSicknessWindow));
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AntiMotionSickness] ExecuteAsync FAILED: {ex.Message}\n{ex.StackTrace}");
            ShowErrorDialog(context, ex);
            return Task.CompletedTask;
        }
    }

    private static async void ShowErrorDialog(BuiltinToolContext context, Exception ex)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "游戏防晕3D - 启动失败",
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = $"{ex.Message}\n\n{ex.StackTrace}",
                        IsTextSelectionEnabled = true,
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                CloseButtonText = "确定",
                XamlRoot = context.XamlRoot,
                RequestedTheme = ThemeService.CurrentElementTheme
            };
            await dialog.ShowAsync();
        }
        catch { }
    }
}
