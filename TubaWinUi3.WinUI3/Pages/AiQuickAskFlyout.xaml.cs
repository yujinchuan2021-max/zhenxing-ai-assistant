using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TubaWinUi3.Pages;

public sealed partial class AiQuickAskFlyout : UserControl
{
    private readonly AiConversationHost _chatPage;
    private bool _disposed;

    public AiQuickAskFlyout()
    {
        InitializeComponent();
        _chatPage = new AiConversationHost(compact: true);
        ChatHost.Content = _chatPage;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        _chatPage.Unload();
        ChatHost.Content = null;
    }
}
