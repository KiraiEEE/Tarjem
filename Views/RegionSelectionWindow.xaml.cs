using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Tarjem.Views;

public partial class RegionSelectionWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private System.Windows.Point _startPoint;
    private bool _isDragging;
    private double _dpiScale = 1.0;

    /// <summary>Physical bounds of the monitor this window is covering. The selection is
    /// reported in absolute screen pixels, so the monitor's own origin has to be added back -
    /// without it a selection on a secondary monitor was captured from the primary one.</summary>
    private System.Drawing.Rectangle _screenBounds;

    /// <summary>The frozen full-screen capture shown as this window's background, kept so the
    /// selected region can be cropped from it. Released when the window closes.</summary>
    private Bitmap? _screenshot;

    /// <summary>Fires when the user completes a selection. The Rect is in screen coordinates (physical pixels).</summary>
    public event EventHandler<Rect>? RegionSelected;

    /// <summary>Fires when the user cancels (Escape or right-click).</summary>
    public event EventHandler? SelectionCancelled;

    public RegionSelectionWindow()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += OnMouseLeftDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseLeftUp;
        PreviewKeyDown += OnKeyDown;
        PreviewMouseRightButtonDown += (_, _) => Cancel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Captures the full screen as a frozen screenshot, then shows it as the background.
    /// The user draws their selection on the frozen image, not on the live desktop.
    /// This prevents the game/app from losing focus.
    /// </summary>
    public void ShowWithScreenshot()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var bounds = screen.Bounds;
        _screenBounds = bounds;

        // PresentationSource isn't available before the window is shown, so the old
        // GetDpiScale() call here always returned 1.0 and the selection landed off-target on
        // any scaled monitor. Ask the monitor directly instead.
        _dpiScale = Tarjem.Services.DpiHelper.GetScaleForPoint(cursor.X, cursor.Y);
        if (_dpiScale <= 0) _dpiScale = 1.0;

        // Position window to cover the entire screen (in DIPs)
        Left = bounds.Left / _dpiScale;
        Top = bounds.Top / _dpiScale;
        Width = bounds.Width / _dpiScale;
        Height = bounds.Height / _dpiScale;

        // Capture the full screen. The bitmap is kept (not disposed here) so the selected
        // region can be cropped straight out of it - re-capturing the screen afterwards would
        // race the result overlay's own fade-in and OCR our overlay instead of the app's text.
        try
        {
            _screenshot?.Dispose();
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            }
            _screenshot = bmp;

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();

            ScreenshotBg.Source = bi;
        }
        catch
        {
            // If screenshot fails, just show a dim overlay
        }

        // Fade in the screenshot and dim
        ScreenshotBg.Opacity = 1.0;
        DimOverlay.Opacity = 0.35;

        Show();
        // Do NOT call Activate/Focus - keeps the game in the background
    }

    /// <summary>Crops the selected region (absolute physical pixels) out of the frozen
    /// screenshot. Returns null if the capture failed or the rect falls outside it, leaving
    /// the caller to fall back to a live screen grab.</summary>
    public Bitmap? CropRegion(Rect physicalRect)
    {
        if (_screenshot == null) return null;

        var x = (int)Math.Round(physicalRect.Left) - _screenBounds.Left;
        var y = (int)Math.Round(physicalRect.Top) - _screenBounds.Top;
        var w = (int)Math.Round(physicalRect.Width);
        var h = (int)Math.Round(physicalRect.Height);

        var source = System.Drawing.Rectangle.Intersect(
            new System.Drawing.Rectangle(x, y, w, h),
            new System.Drawing.Rectangle(0, 0, _screenshot.Width, _screenshot.Height));

        if (source.Width <= 0 || source.Height <= 0) return null;

        try
        {
            return _screenshot.Clone(source, PixelFormat.Format32bppArgb);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _screenshot?.Dispose();
        _screenshot = null;
    }

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        _isDragging = true;

        HintText.Visibility = Visibility.Collapsed;

        SelectionRect.Visibility = Visibility.Visible;
        SelectionRect.Margin = new Thickness(_startPoint.X, _startPoint.Y, 0, 0);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;

        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var current = e.GetPosition(this);
        var x = Math.Min(_startPoint.X, current.X);
        var y = Math.Min(_startPoint.Y, current.Y);
        var w = Math.Abs(current.X - _startPoint.X);
        var h = Math.Abs(current.Y - _startPoint.Y);

        SelectionRect.Margin = new Thickness(x, y, 0, 0);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();

        var endPoint = e.GetPosition(this);
        var x = Math.Min(_startPoint.X, endPoint.X);
        var y = Math.Min(_startPoint.Y, endPoint.Y);
        var w = Math.Abs(endPoint.X - _startPoint.X);
        var h = Math.Abs(endPoint.Y - _startPoint.Y);

        if (w < 10 || h < 10)
        {
            Cancel();
            return;
        }

        // Convert from window-local WPF DIPs to absolute physical screen pixels, clamped to the
        // monitor. CropRegion intersects the request with the screenshot it actually holds, so an
        // unclamped rect running a pixel past the edge produced a crop smaller than the region the
        // result overlay was then positioned over, and the two disagreed.
        var left = Math.Clamp((int)(x * _dpiScale), 0, _screenBounds.Width);
        var top = Math.Clamp((int)(y * _dpiScale), 0, _screenBounds.Height);
        var width = Math.Clamp((int)(w * _dpiScale), 0, _screenBounds.Width - left);
        var height = Math.Clamp((int)(h * _dpiScale), 0, _screenBounds.Height - top);

        if (width <= 0 || height <= 0)
        {
            Cancel();
            return;
        }

        var screenRect = new Rect(
            _screenBounds.Left + left,
            _screenBounds.Top + top,
            width,
            height);

        RegionSelected?.Invoke(this, screenRect);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Cancel();
    }

    private void Cancel()
    {
        _isDragging = false;
        ReleaseMouseCapture();
        SelectionCancelled?.Invoke(this, EventArgs.Empty);
    }
}
