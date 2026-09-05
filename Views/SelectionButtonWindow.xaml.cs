using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// The small Tarjem button that appears beside text the user has just selected.
///
/// It must never steal focus: taking activation would collapse the very selection it exists to
/// act on, in most applications. Hence WS_EX_NOACTIVATE, <c>Focusable=false</c>, and
/// <see cref="ShowNear"/> rather than <c>Show</c> + <c>Activate</c>.
/// </summary>
public partial class SelectionButtonWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_EXSTYLE = -20;

    /// <summary>How long the button lingers before giving up. Long enough to notice and reach,
    /// short enough that it never feels like litter left on the screen.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private readonly DispatcherTimer _autoHide;

    /// <summary>The user clicked the button. Named Clicked rather than Activated so it doesn't
    /// shadow <see cref="Window.Activated"/>, which means something quite different.</summary>
    public event EventHandler? Clicked;

    public SelectionButtonWindow()
    {
        InitializeComponent();

        _autoHide = new DispatcherTimer { Interval = Lifetime };
        _autoHide.Tick += (_, _) => Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Places the button just below-right of <paramref name="anchorPhysical"/> (physical
    /// screen pixels), nudged back inside the monitor's work area when that would hang it off an
    /// edge.</summary>
    public void ShowNear(System.Drawing.Point anchorPhysical)
    {
        var monitor = DpiHelper.MonitorFor(anchorPhysical.X, anchorPhysical.Y);
        var area = monitor.WorkArea;

        // SizeToContent means ActualWidth is only valid once shown; the border is a fixed 30x30
        // plus its shadow, so use the declared size rather than measuring.
        const double size = 30;
        const double gap = 12;

        var left = Math.Clamp(anchorPhysical.X / monitor.Scale + gap, area.Left + 4, Math.Max(area.Left + 4, area.Right - size - 4));
        var top = Math.Clamp(anchorPhysical.Y / monitor.Scale + gap, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - size - 4));

        Left = left;
        Top = top;

        Show();

        var duration = new Duration(TimeSpan.FromMilliseconds(120));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ButtonRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        ButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1, duration) { EasingFunction = ease });
        ButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1, duration) { EasingFunction = ease });

        _autoHide.Stop();
        _autoHide.Start();
    }

    public new void Hide()
    {
        _autoHide.Stop();
        base.Hide();
    }

    private void Button_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Hide();
        Clicked?.Invoke(this, EventArgs.Empty);
    }
}
