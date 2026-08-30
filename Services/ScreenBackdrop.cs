using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// A frozen picture of the monitor a result card is about to appear on, so the Glass style can
/// actually blur what is behind it.
///
/// Windows' own acrylic (SetWindowCompositionAttribute / DWMWA_SYSTEMBACKDROP_TYPE) does not
/// composite under a WPF window with <c>AllowsTransparency="True"</c>, and both cards need that
/// for their rounded corners and drop shadow. Blurring a snapshot instead gives the same look
/// without redesigning either window: these cards are up for a couple of seconds over content that
/// isn't moving, so a still is indistinguishable from a live blur - and it can't ever sample
/// Tarjem's own windows, because it is taken before any of them are on screen.
/// </summary>
public sealed class ScreenBackdrop
{
    /// <summary>Physical-pixel rectangle <see cref="Image"/> covers.</summary>
    public Rectangle Bounds { get; }

    /// <summary>Frozen, so it can be handed to a window on any thread and never re-rendered.</summary>
    public BitmapSource Image { get; }

    private ScreenBackdrop(Rectangle bounds, BitmapSource image)
    {
        Bounds = bounds;
        Image = image;
    }

    /// <summary>
    /// Grabs the whole monitor containing <paramref name="physicalRect"/>. The whole monitor rather
    /// than just the card's own rectangle because the Alt+E card grows once its answer arrives, and
    /// re-capturing at that point would photograph the card itself; cropping a monitor-sized still
    /// costs nothing and covers wherever it ends up.
    /// </summary>
    /// <returns>Null when the capture failed - the caller falls back to a plain translucent card
    /// rather than showing nothing.</returns>
    public static ScreenBackdrop? CaptureMonitorFor(Rect physicalRect)
    {
        try
        {
            var centre = new System.Drawing.Point(
                (int)(physicalRect.Left + physicalRect.Width / 2),
                (int)(physicalRect.Top + physicalRect.Height / 2));

            var bounds = System.Windows.Forms.Screen.FromPoint(centre).Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    bounds.Left, bounds.Top, 0, 0,
                    new System.Drawing.Size(bounds.Width, bounds.Height),
                    CopyPixelOperation.SourceCopy);
            }

            return new ScreenBackdrop(bounds, ToBitmapSource(bitmap));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not capture the screen for the glass backdrop");
            return null;
        }
    }

    /// <summary>
    /// The part of the snapshot sitting behind <paramref name="physicalRect"/>, or null when that
    /// rectangle has wandered off the monitor this was taken of (a card clamped onto a second
    /// display, say).
    /// </summary>
    /// <param name="actualPhysicalRect">What was actually cropped. A card near a screen edge asks
    /// for bleed that runs off it, so the crop comes back smaller; the caller has to know by how
    /// much or it would stretch the shortfall back over the card and slide the backdrop out of
    /// alignment with what is really behind it.</param>
    public BitmapSource? Crop(Rect physicalRect, out Rect actualPhysicalRect)
    {
        actualPhysicalRect = physicalRect;

        try
        {
            var x = (int)Math.Round(physicalRect.Left) - Bounds.Left;
            var y = (int)Math.Round(physicalRect.Top) - Bounds.Top;
            var width = (int)Math.Round(physicalRect.Width);
            var height = (int)Math.Round(physicalRect.Height);

            var left = Math.Clamp(x, 0, Bounds.Width);
            var top = Math.Clamp(y, 0, Bounds.Height);
            var right = Math.Clamp(x + width, 0, Bounds.Width);
            var bottom = Math.Clamp(y + height, 0, Bounds.Height);

            if (right - left < 2 || bottom - top < 2) return null;

            var crop = new CroppedBitmap(Image, new Int32Rect(left, top, right - left, bottom - top));
            crop.Freeze();

            actualPhysicalRect = new Rect(
                Bounds.Left + left, Bounds.Top + top, right - left, bottom - top);

            return crop;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not crop the glass backdrop");
            return null;
        }
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
