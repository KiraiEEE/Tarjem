using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Tarjem.Views;

public partial class OverlayWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;

    private static readonly IEasingFunction EaseOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private const int FadeMs = 140;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public event EventHandler? Clicked;

    public OverlayWindow()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE);
    }

    /// <summary>The monitor this overlay is currently covering. Everything inside is positioned
    /// and clamped against it, never against the primary monitor.</summary>
    private Services.MonitorGeometry _monitor = Services.DpiHelper.MonitorFor(0, 0);

    /// <summary>
    /// Stretches the overlay across the monitor containing <paramref name="anchorPhysical"/>
    /// (physical pixels), the way <see cref="RegionOverlayWindow.PositionOverRegion"/> already
    /// does. This used to hardcode the primary monitor at (0,0), so on a second display the
    /// highlight was placed outside the window's own bounds and simply never appeared.
    /// </summary>
    public void CoverMonitorFor(WpfRect anchorPhysical)
    {
        _monitor = Services.DpiHelper.MonitorFor(anchorPhysical);

        Left = _monitor.Bounds.Left;
        Top = _monitor.Bounds.Top;
        Width = _monitor.Bounds.Width;
        Height = _monitor.Bounds.Height;
    }

    /// <param name="wordRectPhysical">The word's rectangle in absolute physical screen pixels.</param>
    public void SetHighlight(WpfRect wordRectPhysical)
    {
        CoverMonitorFor(wordRectPhysical);
        var local = _monitor.ToWindowLocal(wordRectPhysical);

        HighlightBorder.Margin = new Thickness(local.X, local.Y, 0, 0);
        HighlightBorder.Width = local.Width;
        HighlightBorder.Height = local.Height;
        HighlightBorder.HorizontalAlignment = HorizontalAlignment.Left;
        HighlightBorder.VerticalAlignment = VerticalAlignment.Top;

        var duration = new Duration(TimeSpan.FromMilliseconds(FadeMs));
        HighlightBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
    }

    /// <summary>
    /// Draws the translated word directly over the word it translates - the same idea as the
    /// region overlay, scaled down to a single word, so the answer appears where the user is
    /// already looking instead of only in the popup off to the side.
    /// </summary>
    /// <param name="wordRectPhysical">The highlighted word's rectangle, in absolute physical
    /// screen pixels.</param>
    public void ShowWordOverlay(string translatedWord, WpfRect wordRectPhysical, bool isRtl)
    {
        if (string.IsNullOrWhiteSpace(translatedWord))
        {
            HideWordOverlay();
            return;
        }

        var wordRect = _monitor.ToWindowLocal(wordRectPhysical);

        // Match the height of the word being covered, so the overlay reads as a replacement for
        // that text rather than as a floating label of its own. Clamped because OCR occasionally
        // reports an implausible box, and an unclamped size would produce either invisible text
        // or a full-screen banner.
        var fontSize = Math.Clamp(wordRect.Height * 0.62, 11, 40);

        WordOverlayText.Text = translatedWord.Trim();
        WordOverlayText.FontFamily = Services.UiFonts.For(isRtl);
        WordOverlayText.FontSize = Services.UiFonts.SizeFor(fontSize, isRtl);
        WordOverlayText.FlowDirection = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        WordOverlayBorder.Visibility = Visibility.Visible;
        WordOverlayBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = WordOverlayBorder.DesiredSize;

        // Centred on the original word, then pushed back inside the screen if that would hang it
        // off an edge - words near the right margin are exactly where this is most useful.
        var left = wordRect.X + (wordRect.Width - desired.Width) / 2;
        var top = wordRect.Y + (wordRect.Height - desired.Height) / 2;
        left = Math.Clamp(left, 0, Math.Max(0, _monitor.Bounds.Width - desired.Width));
        top = Math.Clamp(top, 0, Math.Max(0, _monitor.Bounds.Height - desired.Height));

        WordOverlayBorder.Margin = new Thickness(left, top, 0, 0);

        var duration = new Duration(TimeSpan.FromMilliseconds(FadeMs));
        WordOverlayBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
    }

    public void HideWordOverlay()
    {
        WordOverlayBorder.BeginAnimation(OpacityProperty, null);
        WordOverlayBorder.Opacity = 0;
        WordOverlayBorder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Fades the screen dim in. <paramref name="enabled"/> is the user's
    /// "Dim the screen behind overlays" setting - when off, the window still covers the screen
    /// (it's what makes click-to-dismiss work) but stays fully transparent.</summary>
    public void ShowDim(bool enabled = true)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(FadeMs));
        DimOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(DimOverlay.Opacity, enabled ? 0.10 : 0, duration) { EasingFunction = EaseOut });
    }

    /// <summary>Fades the dim and highlight out, then invokes <paramref name="onComplete"/> (typically closing the window).</summary>
    public void HideDim(Action? onComplete = null)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(FadeMs));

        var dimAnim = new DoubleAnimation(DimOverlay.Opacity, 0, duration) { EasingFunction = EaseOut };
        if (onComplete != null)
            dimAnim.Completed += (_, _) => onComplete();

        DimOverlay.BeginAnimation(OpacityProperty, dimAnim);
        HighlightBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(HighlightBorder.Opacity, 0, duration) { EasingFunction = EaseOut });
    }
}
