using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TubaWinUi3.Pages;

public sealed partial class AiAgentPage
{
    private Flyout? _themedSkillsFlyout;
    private Action? _refreshSkillsFlyoutTheme;

    private void EnsureSkillsFlyoutTheme(Flyout flyout, StackPanel panel)
    {
        if (ReferenceEquals(_themedSkillsFlyout, flyout))
        {
            _refreshSkillsFlyoutTheme?.Invoke();
            return;
        }

        _themedSkillsFlyout = flyout;
        // A solid native themed surface covers both content and presenter padding.
        // The default compositor backdrop can otherwise retain a different theme.
        flyout.SystemBackdrop = null;
        var baseStyle = (Style)Resources["SkillsFlyoutPresenterStyle"];
        ElementTheme? styledTheme = null;

        void ApplyTheme()
        {
            var theme = ActualTheme;
            panel.RequestedTheme = theme;
            if (styledTheme != theme)
            {
                flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
                {
                    BasedOn = baseStyle,
                    Setters = { new Setter(FrameworkElement.RequestedThemeProperty, theme) },
                };
                styledTheme = theme;
            }
            // An already open popup has its own visual root. Update its actual
            // presenter as well as the style used for the next opening.
            for (var ancestor = VisualTreeHelper.GetParent(panel); ancestor is not null;
                 ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is FlyoutPresenter presenter)
                {
                    presenter.RequestedTheme = theme;
                    break;
                }
        }

        void OwnerThemeChanged(FrameworkElement sender, object args) => ApplyTheme();
        void PanelLoaded(object sender, RoutedEventArgs args) => ApplyTheme();
        void Detach()
        {
            ActualThemeChanged -= OwnerThemeChanged;
            Unloaded -= OwnerUnloaded;
            panel.Loaded -= PanelLoaded;
        }
        void OwnerUnloaded(object sender, RoutedEventArgs args)
        {
            Detach();
            flyout.Hide();
        }

        flyout.Opening += (_, _) =>
        {
            Detach();
            ActualThemeChanged += OwnerThemeChanged;
            Unloaded += OwnerUnloaded;
            panel.Loaded += PanelLoaded;
            ApplyTheme();
        };
        flyout.Opened += (_, _) => ApplyTheme();
        flyout.Closed += (_, _) => Detach();
        _refreshSkillsFlyoutTheme = ApplyTheme;
        ApplyTheme();
    }
}
