using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// A small note beside the cursor that says why a hotkey press produced nothing - no text there,
/// offline, took too long - and then fades away by itself.
///
/// Before this, a press that found nothing did nothing at all. That is defensible for one press
/// and corrosive over a week: people concluded the hotkey was broken, or pressed it five more
/// times. The note is click-through, never takes focus, never appears in a screenshot, and is
/// gone in under two seconds, so it costs nothing when it isn't wanted.
/// </summary>
public sealed class ToastWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private static ToastWindow? _current;

    private readonly Border _card;

    private ToastWindow(string message)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;

        var dark = ThemeService.IsDark;
        _card = new Border
        {
            Background = new SolidColorBrush(dark ? Color.FromArgb(0xF2, 0x26, 0x26, 0x30) : Color.FromArgb(0xF7, 0xFA, 0xFA, 0xFA)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x3A, 0x48) : Color.FromRgb(0xDD, 0xDD, 0xE2)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7, 12, 8),
            Margin = new Thickness(10),
            Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 },
            Child = new TextBlock
            {
                Text = message,
                MaxWidth = 320,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xEA, 0xEA, 0xEA) : Color.FromRgb(0x22, 0x22, 0x22)),
            },
        };
        Content = _card;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ScreenCaptureGuard.ExcludeFromCapture(hwnd);
    }

    /// <summary>Shows <paramref name="message"/> just below and right of a point in physical
    /// pixels (normally the cursor), replacing any note already showing.</summary>
    public static void Show(string message, System.Drawing.Point nearPhysical)
    {
        try
        {
            _current?.Close();

            var toast = new ToastWindow(message);
            _current = toast;

            var monitor = DpiHelper.MonitorFor(nearPhysical.X, nearPhysical.Y);
            toast.Left = -32000;
            toast.Top = -32000;
            toast.Show();
            toast.UpdateLayout();

            var area = monitor.WorkArea;
            var x = nearPhysical.X / monitor.Scale + 8;
            var y = nearPhysical.Y / monitor.Scale + 14;
            toast.Left = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - toast.ActualWidth));
            toast.Top = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - toast.ActualHeight));

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            toast._card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });

            var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
            hold.Tick += (_, _) =>
            {
                hold.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) =>
                {
                    toast.Close();
                    if (ReferenceEquals(_current, toast)) _current = null;
                };
                toast._card.BeginAnimation(OpacityProperty, fade);
            };
            hold.Start();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Could not show a toast");
        }
    }
}
