namespace TubaWinUi3.Services.Community;

/// <summary>
/// A short-lived navigation allowance for the official community's same-view sign-in.
/// This holds only navigation state: it never inspects credentials, cookies or query values.
/// </summary>
internal sealed class CommunityAuthenticationScope
{
    internal static readonly TimeSpan AuthenticationLifetime = TimeSpan.FromMinutes(10);
    private const string ProviderHost = "id.discourse.com";
    private static readonly string CommunityHost = new Uri(CommunitySite.TargetOfficialUrl).Host;
    private readonly Func<DateTimeOffset> _utcNow;
    private DateTimeOffset? _expiresAt;
    private bool _protectContinuation;
    private bool _restartRequired;

    internal CommunityAuthenticationScope(string? configuredUrl, Func<DateTimeOffset>? utcNow)
    {
        Enabled = IsOfficialPage(configuredUrl);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal bool Enabled { get; }
    internal string? RestartUrl => Enabled ? CommunitySite.TargetOfficialUrl + "/login" : null;
    internal bool IsActive { get { Expire(); return _expiresAt is not null; } }
    internal bool RestartRequired { get { Expire(); return _restartRequired; } }
    internal bool ProtectsContinuation { get { Expire(); return _protectContinuation; } }

    internal bool TryBegin(string? targetUrl, string? sourcePageUrl)
    {
        if (!Enabled || !IsAuthStart(targetUrl) || !IsOfficialPage(sourcePageUrl)
            || (IsCommunityAuthPage(sourcePageUrl) && !IsFailurePage(sourcePageUrl))) return false;

        // Redirects and repeated provider steps never extend the original deadline.
        if (!IsActive) _expiresAt = _utcNow() + AuthenticationLifetime;
        _protectContinuation = true;
        _restartRequired = false;
        return true;
    }

    internal void RequireRestart()
    {
        if (!Enabled) return;
        _expiresAt = null;
        _protectContinuation = true;
        _restartRequired = true;
    }

    internal void Cancel()
    {
        // Revocation must not turn a delayed OAuth redirect into an ordinary external link.
        _protectContinuation |= _expiresAt is not null || _restartRequired;
        _expiresAt = null;
        _restartRequired = false;
    }

    internal void ReturnedToCommunity()
    {
        _expiresAt = null;
        _protectContinuation = false;
        _restartRequired = false;
    }

    internal static bool IsOfficialPage(string? url) => HasOrigin(url, CommunityHost, out _);
    internal static bool IsProviderPage(string? url) => HasOrigin(url, ProviderHost, out _);
    internal static bool IsCommunityAuthPage(string? url)
        => HasOrigin(url, CommunityHost, out var uri)
           && (uri!.AbsolutePath.Equals("/auth", StringComparison.OrdinalIgnoreCase)
               || Uri.UnescapeDataString(uri.AbsolutePath).StartsWith("/auth/", StringComparison.OrdinalIgnoreCase));
    internal static bool IsSensitivePage(string? url) => IsProviderPage(url) || IsCommunityAuthPage(url);

    private static bool IsAuthStart(string? url)
        => HasOrigin(url, CommunityHost, out var uri)
           && uri!.AbsolutePath.Equals("/auth/discourse_id", StringComparison.Ordinal)
           && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsFailurePage(string? url)
        => HasOrigin(url, CommunityHost, out var uri)
           && uri!.AbsolutePath.Equals("/auth/failure", StringComparison.Ordinal);

    private static bool HasOrigin(string? url, string host, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var text = url.Trim();
        if (text.Contains('\\') || text.Any(char.IsControl)
            || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || parsed.Port != 443 || parsed.UserInfo.Length != 0
            || !parsed.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    private void Expire()
    {
        if (_expiresAt is { } deadline && _utcNow() >= deadline) RequireRestart();
    }
}
