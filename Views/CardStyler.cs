using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// Applies the popup visual style - Minimal, Fluent or Glass - to a result card.
///
/// Both cards Tarjem puts on screen wear the same style: the word popup (Alt+Q) and the "what is
/// this?" card (Alt+E). They have completely different contents, but the *surface* is one decision
/// the user made once in Settings, so it lives here rather than being implemented twice and
/// drifting apart.
/// </summary>
public sealed class CardStyler
{
    public const string Minimal = "Minimal";
    public const string Fluent = "Fluent";
    public const string Glass = "Glass";

    /// <summary>The styles Settings offers, in order.</summary>
    public static readonly (string Id, string Label)[] Styles =
    [
        (Minimal, "Minimal"),
        (Fluent, "Fluent"),
        (Glass, "Glass"),
    ];

    /// <summary>Old ids that may still be sitting in a settings.json.</summary>
    public static string Normalize(string? style) => style switch
    {
        Glass or "Glassmorphism" => Glass,
        Fluent or "CleanMatte" => Fluent,
        _ => Minimal,
    };

    /// <summary>How far past the card's own edges the blurred snapshot reaches. A Gaussian blur
    /// fades to nothing at the edge of what it is blurring, so without bleed the Glass card would
    /// have a pale halo all the way round it.</summary>
    private const double Bleed = 22;

    private readonly Window _window;
    private readonly Border _card;
    private readonly DropShadowEffect _shadow;
    private readonly Border _glassLayer;
    private readonly Border _glassTint;
    private readonly ImageBrush _glassBrush;

    private ScreenBackdrop? _backdrop;

    /// <summary>The style in force, already normalized.</summary>
    public string Style { get; private set; } = Minimal;

    /// <summary>The Windows accent, adjusted for the surface it will sit on. Read by Fluent and
    /// Glass; Minimal is deliberately undecorated and doesn't use it.</summary>
    private Color Accent { get; set; }

    public CardStyler(Window window, Border card, DropShadowEffect shadow, Border glassLayer, Border glassTint)
    {
        _window = window;
        _card = card;
        _shadow = shadow;
        _glassLayer = glassLayer;
        _glassTint = glassTint;

        _glassBrush = new ImageBrush { Stretch = Stretch.Fill };
        _glassLayer.Background = _glassBrush;
    }

    /// <summary>The snapshot the Glass style blurs. Set before the card is positioned; null (or
    /// never set) degrades Glass to a plain translucent card rather than failing.</summary>
    public ScreenBackdrop? Backdrop
    {
        get => _backdrop;
        set
        {
            _backdrop = value;
            if (Style == Glass) RefreshBackdrop();
        }
    }

    /// <summary>
    /// Restyles the card. <paramref name="surface"/> is the opaque background the card would have
    /// had; Glass makes its own translucent version of it.
    /// </summary>
    public void Apply(string? style, Color surface, bool isDark)
    {
        Style = Normalize(style);
        Accent = AccentColorService.AccentFor(isDark);

        switch (Style)
        {
            case Glass:
                ApplyGlass(surface, isDark);
                break;
            case Fluent:
                ApplyFluent(surface, isDark);
                break;
            default:
                ApplyMinimal(surface, isDark);
                break;
        }
    }

    /// <summary>
    /// The default. Follows WinUI/WPF-UI surface conventions rather than inventing a look: a plain
    /// card, a hairline border in the theme's own stroke colour, a soft shadow and no accent
    /// decoration at all - it appears over whatever you were already reading, so it asserts as
    /// little as it can.
    /// </summary>
    private void ApplyMinimal(Color surface, bool isDark)
    {
        HideGlass();

        _card.Background = new SolidColorBrush(surface);
        _card.CornerRadius = new CornerRadius(8);
        _card.BorderThickness = new Thickness(1);
        _card.BorderBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        _shadow.Color = Colors.Black;
        _shadow.BlurRadius = 16;
        _shadow.ShadowDepth = 2;
        _shadow.Opacity = 0.14;
    }

    /// <summary>
    /// The card wearing your Windows accent colour. This is the whole point of the style, and the
    /// reason it used to look identical to Minimal: it drew a barely-there edge in a hard-coded
    /// purple that no other part of the card picked up. Now the accent is the one from
    /// Personalization &gt; Colors, the edge is actually visible, and the surface carries a faint
    /// wash of the same colour so the card reads as tinted rather than as outlined.
    /// </summary>
    private void ApplyFluent(Color surface, bool isDark)
    {
        HideGlass();

        _card.Background = new SolidColorBrush(Blend(surface, Accent, isDark ? 0.10 : 0.06));
        _card.CornerRadius = new CornerRadius(10);
        _card.BorderThickness = new Thickness(1);
        _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, Accent.R, Accent.G, Accent.B));

        // Tinting the shadow rather than the usual black is what makes an accent-coloured card
        // sit in its own light instead of looking like a normal card with a coloured outline.
        _shadow.Color = isDark ? Accent : Blend(Colors.Black, Accent, 0.5);
        _shadow.BlurRadius = 26;
        _shadow.ShadowDepth = 3;
        _shadow.Opacity = isDark ? 0.42 : 0.28;
    }

    /// <summary>
    /// Translucent, over a real blur of whatever is behind the card - see
    /// <see cref="ScreenBackdrop"/> for why that is a snapshot rather than live acrylic. The card
    /// itself becomes a tint sitting on top of that blur, which is what makes it read as frosted
    /// glass rather than as a card that has merely been turned down to 80% opacity.
    /// </summary>
    private void ApplyGlass(Color surface, bool isDark)
    {
        _card.Background = Brushes.Transparent;
        _card.CornerRadius = new CornerRadius(14);
        _card.BorderThickness = new Thickness(1);
        _card.BorderBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

        _shadow.Color = Colors.Black;
        _shadow.BlurRadius = 34;
        _shadow.ShadowDepth = 5;
        _shadow.Opacity = 0.26;

        // Light enough to see the blur through, opaque enough to keep text legible over whatever
        // happens to be underneath - which, over a game or a bright web page, is the hard part.
        _glassTint.Background = new SolidColorBrush(Color.FromArgb(
            isDark ? (byte)0xA8 : (byte)0xAE, surface.R, surface.G, surface.B));
        _glassTint.Visibility = Visibility.Visible;

        _glassLayer.Visibility = Visibility.Visible;
        RefreshBackdrop();
    }

    private void HideGlass()
    {
        _glassLayer.Visibility = Visibility.Collapsed;
        _glassTint.Visibility = Visibility.Collapsed;
        _glassBrush.ImageSource = null;
    }

    /// <summary>
    /// Re-crops the snapshot for wherever the card is now. Called again whenever the card is moved
    /// or resized - the Alt+E card does both once its answer arrives - because the blur has to stay
    /// registered with what is genuinely behind it, or the illusion collapses instantly.
    /// </summary>
    public void RefreshBackdrop()
    {
        if (Style != Glass) return;

        if (_backdrop == null || _card.ActualWidth <= 0 || _card.ActualHeight <= 0)
        {
            _glassBrush.ImageSource = null;
            return;
        }

        // Both cards centre their card inside the window (the window is larger only by the margin
        // the drop shadow needs), so halving the difference recovers the card's own offset without
        // measuring through a RenderTransform that is mid-animation.
        var insetX = Math.Max(0, (_window.ActualWidth - _card.ActualWidth) / 2);
        var insetY = Math.Max(0, (_window.ActualHeight - _card.ActualHeight) / 2);

        var wanted = new Rect(
            _window.Left + insetX - Bleed,
            _window.Top + insetY - Bleed,
            _card.ActualWidth + Bleed * 2,
            _card.ActualHeight + Bleed * 2);

        var scale = DpiHelper.GetScaleForPoint(_backdrop.Bounds.Left + 1, _backdrop.Bounds.Top + 1);
        if (scale <= 0) scale = 1;

        var cropped = _backdrop.Crop(
            new Rect(wanted.Left * scale, wanted.Top * scale, wanted.Width * scale, wanted.Height * scale),
            out var actual);

        if (cropped == null)
        {
            _glassBrush.ImageSource = null;
            return;
        }

        _glassBrush.ImageSource = cropped;

        // The glass layer overhangs the card by exactly as much snapshot as we actually got, so
        // the image maps 1:1 onto the screen it was taken from.
        var actualDip = new Rect(
            actual.Left / scale, actual.Top / scale, actual.Width / scale, actual.Height / scale);

        _glassLayer.Margin = new Thickness(
            actualDip.Left - (_window.Left + insetX),
            actualDip.Top - (_window.Top + insetY),
            _window.Left + insetX + _card.ActualWidth - actualDip.Right,
            _window.Top + insetY + _card.ActualHeight - actualDip.Bottom);
    }

    private static Color Blend(Color under, Color over, double amount) => Color.FromRgb(
        (byte)(under.R + (over.R - under.R) * amount),
        (byte)(under.G + (over.G - under.G) * amount),
        (byte)(under.B + (over.B - under.B) * amount));
}
