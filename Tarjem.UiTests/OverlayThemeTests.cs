using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Tarjem.Services;
using Tarjem.Views;
using Xunit;

namespace Tarjem.UiTests;

/// <summary>
/// The "whole screen goes grey" bug.
///
/// Applying the theme makes WPF-UI put a Mica backdrop on every open window, and on a transparent
/// window DWM draws that backdrop in all the transparent area - so a full-screen overlay turned the
/// entire monitor solid #202020. The theme was re-applied on every settings save, and Gemini saves
/// its model health after every request, so it struck whenever a Gemini answer arrived while an
/// overlay was up.
///
/// Checked on real screen pixels over a known colour, because nothing observable from WPF changes:
/// the window's Background stays transparent throughout, which is exactly why this went unnoticed.
/// </summary>
[Collection(IsolatedDataCollection.Name)]
public class OverlayThemeTests
{
    private static readonly System.Drawing.Color Underneath = System.Drawing.Color.Magenta;

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [Fact]
    public void ChangingTheThemeLeavesAnOpenOverlaySeeThrough() => StaRunner.Run(() =>
    {
        using var under = new System.Windows.Forms.Form
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Underneath,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Bounds = new System.Drawing.Rectangle(50, 50, 700, 500),
        };
        under.Show();

        var overlay = new RegionOverlayWindow();
        overlay.PositionOverRegion(new Rect(100, 100, 300, 200));
        overlay.ApplyOptions(dimBehind: false, showOriginal: false, matchSourceFontSize: true);
        overlay.SetTranslation("x", "hello");
        overlay.Show();

        try
        {
            Settle();
            Assert.Equal(Underneath.ToArgb(), PixelAt(600, 450).ToArgb());

            // Two real changes, so neither can be skipped as "already applied".
            foreach (var theme in new[] { ThemeService.Light, ThemeService.Dark })
            {
                ThemeService.Apply(theme);
                Settle();

                var seen = PixelAt(600, 450);
                Console.Error.WriteLine($"DIAG {theme}: {seen} app={System.Windows.Application.Current != null} windows={System.Windows.Application.Current?.Windows.Count}");
                Assert.True(seen.ToArgb() == Underneath.ToArgb(),
                    $"After applying {theme}, the overlay's transparent area showed {seen} instead of what is underneath it");
            }
        }
        finally
        {
            overlay.Close();
            ThemeService.Apply(ThemeService.System);
        }
    });

    private static void Settle()
    {
        var until = DateTime.Now.AddMilliseconds(600);
        while (DateTime.Now < until)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        DwmFlush();
        DwmFlush();
    }

    private static System.Drawing.Color PixelAt(int x, int y)
    {
        using var bitmap = new System.Drawing.Bitmap(1, 1);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(1, 1));
        return bitmap.GetPixel(0, 0);
    }
}
