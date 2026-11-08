using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// The controls for live mode: a small pill beside the watched region with pause, show original
/// and stop. It is its own window because the overlay becomes click-through while live - the game
/// underneath has to keep getting its clicks - and a click-through window can't have buttons.
/// </summary>
public sealed class LiveBarWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public event EventHandler? PauseToggled;
    public event EventHandler? OriginalToggled;
    public event EventHandler? StopRequested;

    private readonly Ellipse _dot;
    private readonly TextBlock _label;
    private readonly Path _pauseGlyph;
    private readonly Path _eyeGlyph;

    private static readonly Color Live = Color.FromRgb(0xF0, 0x4A, 0x4A);
    private static readonly Color Muted = Color.FromRgb(0xA0, 0xA0, 0xAA);
    private static readonly Color Text = Color.FromRgb(0xE6, 0xE6, 0xEA);
    private static readonly Color Accent = Color.FromRgb(0x5B, 0x9E, 0xF5);

    public LiveBarWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        _dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Live), VerticalAlignment = VerticalAlignment.Center };
        _label = new TextBlock
        {
            Text = "Live",
            Foreground = new SolidColorBrush(Text),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _pauseGlyph = Glyph("M2,1 L2,9 M7,1 L7,9");
        _eyeGlyph = Glyph("M0,4 Q5,-1.5 10,4 Q5,9.5 0,4 Z M5,2.4 A1.6,1.6 0 1 1 4.99,2.4 Z");
        var stopGlyph = Glyph("M1,1 L9,9 M9,1 L1,9");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_label);
        row.Children.Add(Button(_pauseGlyph, "Pause", () => PauseToggled?.Invoke(this, EventArgs.Empty)));
        row.Children.Add(Button(_eyeGlyph, "Show the original", () => OriginalToggled?.Invoke(this, EventArgs.Empty)));
        row.Children.Add(Button(stopGlyph, "Stop", () => StopRequested?.Invoke(this, EventArgs.Empty)));

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x2C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 3, 4, 3),
            Margin = new Thickness(6),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.45 },
            Child = row,
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ScreenCaptureGuard.ExcludeFromCapture(hwnd);
    }

    /// <summary>Reflects the state: watching, paused, or working on a new line.</summary>
    public void SetState(bool paused, bool busy, bool showingOriginal)
    {
        _dot.Fill = new SolidColorBrush(paused ? Muted : Live);
        _label.Text = paused ? "Paused" : busy ? "Translating…" : "Live";
        _pauseGlyph.Data = Geometry.Parse(paused ? "M2,1 L9,5 L2,9 Z" : "M2,1 L2,9 M7,1 L7,9");
        _eyeGlyph.Stroke = new SolidColorBrush(showingOriginal ? Accent : Text);
    }

    /// <summary>Above the region's top-right corner, or below it when there's no room above.
    /// <paramref name="regionPhysical"/> is in physical pixels.</summary>
    public void PlaceBeside(Rect regionPhysical)
    {
        var monitor = DpiHelper.MonitorFor(regionPhysical);
        Left = -32000;
        Top = -32000;
        if (!IsVisible) Show();
        UpdateLayout();

        var area = monitor.WorkArea;
        var right = regionPhysical.Right / monitor.Scale;
        var top = regionPhysical.Top / monitor.Scale - ActualHeight - 2;
        if (top < area.Top) top = regionPhysical.Bottom / monitor.Scale + 2;

        Left = Math.Clamp(right - ActualWidth, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }

    private static Path Glyph(string data) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = new SolidColorBrush(Text),
        StrokeThickness = 1.3,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Button(Path glyph, string tooltip, Action onClick)
    {
        var button = new Border
        {
            Width = 24,
            Height = 20,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = glyph,
        };
        var hover = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x46));
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return button;
    }
}
