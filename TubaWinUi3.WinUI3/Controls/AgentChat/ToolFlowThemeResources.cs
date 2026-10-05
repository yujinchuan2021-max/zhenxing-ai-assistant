using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Services;

namespace TubaWinUi3.Controls.AgentChat;

/// <summary>
/// Goal-flow surfaces use the chat page's semantic palette in explicit local
/// theme dictionaries. Dedicated keys avoid native resource lookup falling back
/// to the application's theme when the containing view requests another theme.
/// Native Button and Expander templates remain unchanged.
/// </summary>
internal static class ToolFlowThemeResources
{
    private static readonly ConditionalWeakTable<ThemeRefreshScope, PaletteRefresh> Refreshers = new();
    private static readonly ConditionalWeakTable<FrameworkElement, ResourceDictionary> Palettes = new();
    internal const string Canvas = "ToolFlowCanvasBrush";
    internal const string Surface = "ToolFlowSurfaceBrush";
    internal const string Stroke = "ToolFlowStrokeBrush";
    internal const string PrimaryText = "ToolFlowPrimaryTextBrush";
    internal const string SecondaryText = "ToolFlowSecondaryTextBrush";
    internal const string Accent = "ToolFlowAccentBrush";
    internal const string OnAccent = "ToolFlowOnAccentBrush";

    // Corresponds to AssistantCanvas/SoftFill/Separator/PrimaryText/SecondaryText/
    // Accent/AccentForeground in Pages/AiAgentPage.xaml. Host source-contract
    // checks compare this palette with that independently declared page palette.
    private static readonly (string Key, uint Light, uint Dark)[] Palette =
    [
        (Canvas, 0xFFFCFBF9, 0xFF212123),
        (Surface, 0xFFF0EEEB, 0xFF2B2B2E),
        (Stroke, 0xFFDDD9D4, 0xFF3D3B3B),
        (PrimaryText, 0xFF2B2B30, 0xFFEDEDF0),
        (SecondaryText, 0xFF62646D, 0xFFB0AFB7),
        (Accent, 0xFF4B66AD, 0xFFA5B7ED),
        (OnAccent, 0xFFFFFFFF, 0xFF18243E),
    ];

    internal static ThemeRefreshScope Attach(FrameworkElement root)
    {
        AddPalette(root);
        // Construction binds children before they enter the visual tree. Their
        // private palette already belongs to root; searching an unattached child
        // would otherwise scan every application/system dictionary for a miss.
        var scope = ThemeRefreshScope.Attach(root, useRootResources: true);
        var refresher = new PaletteRefresh(root, scope);
        Refreshers.Add(scope, refresher);
        return scope;
    }

    /// <summary>Provide the existing palette to a separately owned fixed rendering scope.</summary>
    internal static void AddPalette(FrameworkElement root)
    {
        if (Palettes.TryGetValue(root, out _)) return;
        var dictionary = new ResourceDictionary();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var values = new ResourceDictionary();
            foreach (var (key, light, dark) in Palette)
            {
                var argb = theme == "Light" ? light : dark;
                values[key] = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24),
                    (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            }
            dictionary.ThemeDictionaries[theme] = values;
        }
        root.Resources.MergedDictionaries.Add(dictionary);
        Palettes.Add(root, dictionary);
    }

    internal static void Bind(ThemeRefreshScope scope, DependencyObject target, DependencyProperty property, string key)
    {
        scope.Bind(target, property, key);
        if (Refreshers.TryGetValue(scope, out var refresher)) refresher.Register(target, property, key);
    }

    internal static void Refresh(ThemeRefreshScope scope)
    {
        if (Refreshers.TryGetValue(scope, out var refresher)) refresher.Refresh();
        else scope.RefreshAll();
    }

    internal static void BindText(ThemeRefreshScope scope, TextBlock text, string key)
    {
        Bind(scope, text, TextBlock.ForegroundProperty, key);
        // Deferred Expander/CheckBox content obtains its inherited theme when
        // its native template attaches. Refresh then as well as at the root.
        text.ActualThemeChanged += (_, _) =>
        {
            if (Refreshers.TryGetValue(scope, out var refresher)) refresher.Enqueue();
        };
    }

    /// <summary>One root palette, refreshed after native theme propagation rather than from each child's event.</summary>
    private sealed class PaletteRefresh
    {
        private readonly WeakReference<FrameworkElement> _root;
        private readonly ThemeRefreshScope _scope;
        private readonly List<(WeakReference<DependencyObject> Target, DependencyProperty Property, string Key)> _bindings = [];
        private readonly List<FrameworkElement> _themeAncestors = [];
        private readonly Windows.Foundation.TypedEventHandler<FrameworkElement, object> _ancestorThemeChanged;
        private int _mountEpoch;
        private int _queuedEpoch = -1;
        private bool _loaded;

        internal PaletteRefresh(FrameworkElement root, ThemeRefreshScope scope)
        {
            _root = new(root);
            _scope = scope;
            _ancestorThemeChanged = OnAncestorThemeChanged;
            root.ActualThemeChanged += (_, _) => Enqueue();
            root.Loaded += (_, _) =>
            {
                _loaded = true;
                TrackThemeAncestors(root);
                ThemeService.ThemeChanged -= OnAppThemeChanged;
                ThemeService.ThemeChanged += OnAppThemeChanged;
                Enqueue();
            };
            root.Unloaded += (_, _) =>
            {
                _loaded = false;
                _mountEpoch++;
                _queuedEpoch = -1;
                UntrackThemeAncestors();
                ThemeService.ThemeChanged -= OnAppThemeChanged;
            };
        }

        internal void Register(DependencyObject target, DependencyProperty property, string key)
            => _bindings.Add((new(target), property, key));

        private void OnAppThemeChanged(ElementTheme _) => Enqueue();

        // WinUI may stop raising an inherited ActualThemeChanged on a cached
        // element after it is reattached. The still-attached page ancestor raises
        // the event reliably; defer our refresh until propagation has completed.
        // Keep only the current mount's ancestors and release them on Unloaded.
        private void TrackThemeAncestors(FrameworkElement root)
        {
            UntrackThemeAncestors();
            for (var parent = VisualTreeHelper.GetParent(root); parent is not null;
                parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not FrameworkElement element) continue;
                _themeAncestors.Add(element);
                element.ActualThemeChanged += _ancestorThemeChanged;
            }
        }

        private void UntrackThemeAncestors()
        {
            foreach (var element in _themeAncestors)
                element.ActualThemeChanged -= _ancestorThemeChanged;
            _themeAncestors.Clear();
        }

        private void OnAncestorThemeChanged(FrameworkElement _, object __) => Enqueue();

        internal void Enqueue()
        {
            if (!_loaded || _queuedEpoch == _mountEpoch || !_root.TryGetTarget(out var root)) return;
            var epoch = _mountEpoch;
            _queuedEpoch = epoch;
            if (!root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                if (epoch != _mountEpoch) return;
                _queuedEpoch = -1;
                if (_loaded && _root.TryGetTarget(out var current) && current.IsLoaded) Refresh();
            })) _queuedEpoch = -1;
        }

        internal void Refresh()
        {
            _scope.RefreshAll();
            if (!_root.TryGetTarget(out var root)) return;
            // Surface and text must use the same attached container theme. A
            // child's previous Application/system theme is not a separate palette.
            var theme = ThemeResourceResolver.CurrentThemeKeyOverrideForTest
                ?? ThemeResourceResolver.ThemeKeyOf(root.ActualTheme)
                ?? ThemeResourceResolver.ResolveThemeKey(root);
            var chain = ThemeResourceResolver.BuildChain(root);
            _bindings.RemoveAll(binding => !binding.Target.TryGetTarget(out _));
            foreach (var binding in _bindings)
            {
                if (binding.Target.TryGetTarget(out var target)
                    && ThemeResourceResolver.ResolveBrush(chain, theme, binding.Key) is { } brush)
                    target.SetValue(binding.Property, brush);
            }
        }
    }
}
