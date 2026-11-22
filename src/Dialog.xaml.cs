using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Clearcut;

/// <summary>An in-app replacement for MessageBox that matches the Clearcut theme.</summary>
public partial class Dialog : Window
{
    public enum Kind { Info, Warning, Danger }

    private Dialog() => InitializeComponent();

    /// <summary>Shows the dialog over <paramref name="owner"/> and returns true when the primary button is chosen.</summary>
    public static bool Show(Window owner, string heading, string message, string primary,
        string? secondary = null, Kind kind = Kind.Info, bool focusSecondary = false)
    {
        var dialog = new Dialog { Owner = owner, Title = heading };
        dialog.Heading.Text = heading;
        dialog.Message.Text = message;
        dialog.PrimaryButton.Content = primary;
        dialog.PrimaryButton.Style = (Style)dialog.FindResource(kind == Kind.Danger ? "Danger" : "Primary");
        if (secondary is null) dialog.SecondaryButton.Visibility = Visibility.Collapsed;
        else dialog.SecondaryButton.Content = secondary;

        (string color, string glyph) = kind switch
        {
            Kind.Danger => ("#F87171", "M3 5 H17 M8 5 V3 H12 V5 M5 5 L6 18 H14 L15 5"),
            Kind.Warning => ("#FBBF24", "M10 3 L18 17 H2 Z M10 8 V12 M10 14.5 V14.6"),
            _ => ("#C8F135", "M10 2 A8 8 0 1 1 10 18 A8 8 0 1 1 10 2 Z M10 9 V14 M10 6 V6.1")
        };
        var tint = (Color)ColorConverter.ConvertFromString(color);
        dialog.Glyph.Data = Geometry.Parse(glyph);
        dialog.Glyph.Stroke = new SolidColorBrush(tint);
        dialog.Badge.Background = new SolidColorBrush(Color.FromArgb(36, tint.R, tint.G, tint.B));

        dialog.Loaded += (_, _) => (focusSecondary && secondary is not null ? dialog.SecondaryButton : dialog.PrimaryButton).Focus();
        return dialog.ShowDialog() == true;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnSecondary(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}
