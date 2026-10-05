using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace TubaWinUi3.Services;

/// <summary>Web links in conversation content use one application navigation handler.</summary>
internal static class InternalBrowserLink
{
    internal static bool TryGetWebUri(string? address, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    internal static bool Bind(Hyperlink link, string? address)
    {
        if (!TryGetWebUri(address, out var uri)) return false;
        // NavigateUri also invokes Windows' default browser. Keep it unset.
        link.Click += (_, _) => Pages.BrowserPage.Open(uri.AbsoluteUri);
        return true;
    }

    internal static bool Bind(HyperlinkButton link, string? address)
    {
        if (!TryGetWebUri(address, out var uri)) return false;
        link.Click += (_, _) => Pages.BrowserPage.Open(uri.AbsoluteUri);
        return true;
    }
}
