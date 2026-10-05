using System.Text.Json;

namespace TubaWinUi3.Services.Community;

/// <summary>Only public appearance information is sent to the official community.</summary>
internal static class CommunityAppearanceBridge
{
    internal const string ReadyMessage = "zhenxing-community-ready";
    internal static bool IsOfficial(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "community.zhenxingai.com"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    internal static bool IsReady(string? source, string json)
    {
        if (!IsOfficial(source) || json.Length > 256) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String && type.GetString() == ReadyMessage;
        }
        catch (JsonException) { return false; }
    }

    internal static string Flatten(byte alpha, byte red, byte green, byte blue, string background)
    {
        byte Blend(byte value, int start) => (byte)Math.Round((value * alpha + Convert.ToByte(background.Substring(start, 2), 16) * (255 - alpha)) / 255d);
        return $"#{Blend(red, 1):x2}{Blend(green, 3):x2}{Blend(blue, 5):x2}";
    }

    internal static string StateJson(bool dark, string background, string surface, string text,
        string muted, string line, string accent, string accentText)
        => JsonSerializer.Serialize(new { type = "zhenxing-community-appearance", theme = dark ? "dark" : "light",
            background, surface, text, muted, line, accent, accentText });

    // Installed before navigation. The page requests appearance when its document is ready;
    // native theme changes update CSS without reloading posts, editors or login forms.
    internal const string DocumentScript = """
        (() => {
          if (window !== window.top || location.origin !== 'https://community.zhenxingai.com' || !window.chrome?.webview) return;
          const color = value => typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value);
          const mix = (a, b, amount) => '#' + [1,3,5].map(i => Math.round(parseInt(a.slice(i,i+2),16)*amount + parseInt(b.slice(i,i+2),16)*(1-amount)).toString(16).padStart(2,'0')).join('');
          const luminance = value => [1,3,5].map(i => parseInt(value.slice(i,i+2),16)/255).map(c => c <= .04045 ? c/12.92 : ((c+.055)/1.055)**2.4).reduce((sum,c,i) => sum+c*[.2126,.7152,.0722][i],0);
          const contrast = (a,b) => (Math.max(luminance(a),luminance(b))+.05)/(Math.min(luminance(a),luminance(b))+.05);
          const readable = (foreground,background,fallback) => contrast(foreground,background) >= 4.5 ? foreground : fallback;
          let current;
          const ensureStyle = () => {
            if (!document.head || document.getElementById('zxai-client-appearance')) return;
            const style = document.createElement('style');
            style.id = 'zxai-client-appearance';
            const nonce = document.querySelector('script[nonce]')?.nonce;
            if (nonce) style.nonce = nonce;
            // Scoped to this official, embedded document; no blanket recoloring of category badges or post contents.
            style.textContent = `html[data-zxai-client="true"],html[data-zxai-client="true"] body {
              color:var(--primary) !important;background-color:var(--secondary) !important;
            }
            html[data-zxai-client="true"] #main-outlet,html[data-zxai-client="true"] #main-outlet-wrapper {
              background-color:var(--d-content-background) !important;
            }`;
            document.head.appendChild(style);
          };
          const apply = state => {
            if (!state || state.type !== 'zhenxing-community-appearance' || !['light','dark'].includes(state.theme)
              || !['background','surface','text','muted','line','accent','accentText'].every(key => color(state[key]))) return;
            current = state;
            const root = document.documentElement;
            if (!root) return; // A document-created message can arrive before the HTML root/head exists.
            root.dataset.zxaiTheme = state.theme;
            root.dataset.zxaiClient = 'true';
            root.style.setProperty('color-scheme',state.theme,'important');
            const soft = mix(state.text,state.background,.06), tint = mix(state.accent,state.background,.12);
            const muted = readable(state.muted,state.background,state.text);
            const accentText = readable(state.accentText,state.accent,
              contrast('#000000',state.accent) > contrast('#ffffff',state.accent) ? '#000000' : '#ffffff');
            const values = {
              primary:state.text, secondary:state.background, tertiary:state.accent, quaternary:state.accent,
              header_background:state.background, header_primary:state.text, highlight:tint,
              'primary-very-low':state.surface, 'primary-low':soft, 'primary-low-mid':state.line,
              'primary-medium':muted, 'primary-high':state.text, 'primary-very-high':state.text,
              // Discourse compiles these numbered ramps into its palette. Updating only primary/secondary
              // leaves tokens, sidebars and editors using values from the previous light/dark scheme.
              'primary-50':state.surface, 'primary-100':soft, 'primary-200':mix(state.text,state.background,.12),
              'primary-300':state.line, 'primary-400':state.line, 'primary-500':muted,
              'primary-600':muted, 'primary-700':muted, 'primary-800':state.text, 'primary-900':state.text,
              'header_primary-low':soft, 'header_primary-low-mid':state.line,
              'header_primary-medium':muted, 'header_primary-high':state.text, 'header_primary-very-high':state.text,
              'secondary-low':state.text, 'secondary-medium':muted,
              'secondary-high':state.surface, 'secondary-very-high':state.background,
              'tertiary-very-low':tint, 'tertiary-low':mix(state.accent,state.background,.25),
              'tertiary-medium':mix(state.accent,state.background,.6), 'tertiary-high':state.accent,
              'tertiary-hover':mix(state.accent,state.text,.9),
              'tertiary-very-high':state.accent, 'quaternary-low':tint,
              'highlight-bg':tint, 'highlight-low':tint, 'highlight-medium':tint, 'highlight-high':state.text,
              'blend-primary-secondary-5':soft,
              'primary-med-or-secondary-med':muted, 'primary-med-or-secondary-high':muted,
              'primary-high-or-secondary-low':state.text, 'primary-low-mid-or-secondary-high':state.line,
              'primary-low-mid-or-secondary-low':state.line, 'primary-or-primary-low-mid':state.text,
              'highlight-low-or-medium':tint, 'tertiary-or-tertiary-low':state.accent,
              'tertiary-low-or-tertiary-high':state.accent, 'tertiary-med-or-tertiary':state.accent,
              'secondary-or-primary':accentText, 'tertiary-or-white':accentText,
              'd-content-background':state.background, 'd-input-bg-color':state.surface,
              'd-input-bg-color--disabled':soft, 'd-input-text-color':state.text,
              'd-selected':tint, 'd-selected-text-color':state.text, 'd-selected-hover':soft, 'd-hover':soft,
              'token-color-text-default':state.text, 'token-color-text-subtle':state.text,
              'token-color-text-subtlest':muted, 'token-color-text-inverse':accentText,
              'token-color-icon-default':state.text, 'token-color-icon-subtle':muted,
              'token-color-icon-subtlest':muted, 'token-color-surface':state.background,
              'token-color-surface-hovered':soft, 'token-color-surface-pressed':tint,
              'token-color-surface-focused':tint, 'token-color-surface-selected':tint,
              'token-color-background-input':state.surface, 'token-color-border-input':state.line,
              'token-color-border-default':state.line,
              'd-button-default-text-color':state.text, 'd-button-default-icon-color':state.text,
              'd-button-default-bg-color':soft, 'd-button-default-text-color--hover':state.text,
              'd-button-default-icon-color--hover':state.text, 'd-button-default-bg-color--hover':tint,
              'd-button-primary-text-color':accentText, 'd-button-primary-icon-color':accentText,
              'd-button-primary-text-color--hover':accentText, 'd-button-primary-icon-color--hover':accentText,
              'd-button-primary-bg-color':state.accent, 'd-button-primary-bg-color--hover':state.accent,
              'hljs-bg':soft, 'inline-code-bg':soft, 'hljs-comment':readable(muted,soft,state.text), 'hljs-punctuation':state.text,
              'zxai-surface':state.surface, 'zxai-border':state.line, 'zxai-muted':muted,
              'zxai-accent-text':accentText
            };
            [25,50,100,200,300,400,500,600,700,800,900].forEach((step,index) => {
              values['tertiary-'+step] = mix(state.accent,state.background,(index+1)/11);
            });
            ['attr','attribute','addition','deletion','keyword','title','name','symbol','variable','string'].forEach(name => {
              values['hljs-'+name] = readable(state.accent,soft,state.text);
            });
            Object.entries(values).forEach(([name,value]) => {
              root.style.setProperty('--'+name,value,'important');
              root.style.setProperty('--'+name+'-rgb',[1,3,5].map(i=>parseInt(value.slice(i,i+2),16)).join(', '),'important');
            });
            root.style.setProperty('--scheme-type',state.theme,'important');
            root.style.setProperty('--csstools-color-scheme--light',state.theme === 'light' ? 'initial' : ' ','important');
            ensureStyle();
          };
          window.chrome.webview.addEventListener('message',event=>apply(event.data));
          const ready = () => {
            if (current) apply(current);
            window.chrome.webview.postMessage({type:'zhenxing-community-ready'});
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded',ready,{once:true}); else ready();
        })();
        """;
}
