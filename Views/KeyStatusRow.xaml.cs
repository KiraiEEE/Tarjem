using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tarjem.Views;

/// <summary>
/// The one line under each API key box that says where that source's key is coming from and, when
/// it is the user's own, whether it actually works.
///
/// A verified key reports as a green dot, a checkmark and the round-trip time - nothing else. It
/// used to say "✓ Your key works — answered in 412 ms", which is three claims where one glance
/// would do, in a typographic register that doesn't belong in a Fluent app.
/// </summary>
public partial class KeyStatusRow : UserControl
{
    private static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Warn = Freeze(Color.FromRgb(0xE0, 0xA0, 0x30));

    public KeyStatusRow() => InitializeComponent();

    /// <summary>Plain prose - which key is in use, or why this source isn't available.</summary>
    public void ShowMessage(string text, bool warn = false)
    {
        Dot.Visibility = Visibility.Collapsed;
        Tick.Visibility = Visibility.Collapsed;

        Message.Text = text;

        if (warn)
            Message.Foreground = Warn;
        else
            Message.ClearValue(ForegroundProperty);

        Visibility = Visibility.Visible;
    }

    public void ShowChecking() => ShowMessage("Checking your key…");

    /// <param name="latencyMs">How long the provider took to answer a real request.</param>
    public void ShowVerified(long latencyMs)
    {
        Dot.Background = Ok;
        Dot.Visibility = Visibility.Visible;

        Tick.Foreground = Ok;
        Tick.Visibility = Visibility.Visible;

        Message.ClearValue(ForegroundProperty);
        Message.Text = $"{latencyMs} ms";

        Visibility = Visibility.Visible;
    }

    /// <summary>Deliberately says nothing about <em>why</em>. The provider's own error text is a
    /// stack of HTTP nouns that can only make a wrong key feel like the user's fault.</summary>
    public void ShowInvalid() => ShowMessage("Key invalid", warn: true);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
