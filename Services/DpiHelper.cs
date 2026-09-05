using System.Runtime.InteropServices;
using Serilog;

namespace Tarjem.Services;

public static class DpiHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    /// <summary>
    /// Screen capture (Cursor.Position, CopyFromScreen) works in physical pixels,
    /// while WPF window placement (Left/Top/Width/Height) works in DPI-independent
    /// units. On a scaled monitor these diverge, so translation results and the
    /// highlight overlay would visibly land in the wrong spot even though the OCR
    /// word match itself was correct. This returns the physical-to-DIP scale factor
    /// for the monitor under the given physical point so callers can convert.
    /// </summary>
    public static double GetScaleForPoint(int physicalX, int physicalY)
    {
        try
        {
            var hMonitor = MonitorFromPoint(new POINT { X = physicalX, Y = physicalY }, MonitorDefaultToNearest);
            if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve monitor DPI for ({X},{Y}); falling back to 100% scale", physicalX, physicalY);
        }
        return 1.0;
    }

    /// <summary>
    /// Geometry of the monitor containing a physical screen point, with its bounds and work area
    /// already converted to the DIP units WPF places windows in.
    ///
    /// Overlays must be sized and clamped against *this*, never against
    /// <c>SystemParameters.PrimaryScreenWidth/Height</c>: those describe the primary monitor only,
    /// so anything positioned with them lands off-window the moment the user is looking at a
    /// second display.
    /// </summary>
    public static MonitorGeometry MonitorFor(int physicalX, int physicalY)
    {
        var scale = GetScaleForPoint(physicalX, physicalY);
        if (scale <= 0) scale = 1.0;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(physicalX, physicalY));
        return new MonitorGeometry(ToDip(screen.Bounds, scale), ToDip(screen.WorkingArea, scale), scale);
    }

    /// <summary>Convenience overload for a rectangle already in physical pixels - uses its centre,
    /// so a selection straddling two monitors resolves to the one it mostly sits on.</summary>
    public static MonitorGeometry MonitorFor(System.Windows.Rect physicalRect) =>
        MonitorFor(
            (int)(physicalRect.Left + physicalRect.Width / 2),
            (int)(physicalRect.Top + physicalRect.Height / 2));

    private static System.Windows.Rect ToDip(System.Drawing.Rectangle r, double scale) =>
        new(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);
}

/// <param name="Bounds">Full monitor rectangle in DIPs.</param>
/// <param name="WorkArea">Monitor rectangle minus the taskbar, in DIPs.</param>
/// <param name="Scale">Physical pixels per DIP on this monitor.</param>
public readonly record struct MonitorGeometry(
    System.Windows.Rect Bounds, System.Windows.Rect WorkArea, double Scale)
{
    /// <summary>Converts a physical-pixel screen rectangle into DIPs relative to this monitor's
    /// top-left corner - i.e. into the coordinate space of a window covering the monitor.</summary>
    public System.Windows.Rect ToWindowLocal(System.Windows.Rect physicalRect) => new(
        physicalRect.Left / Scale - Bounds.Left,
        physicalRect.Top / Scale - Bounds.Top,
        physicalRect.Width / Scale,
        physicalRect.Height / Scale);
}
