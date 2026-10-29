using System.Runtime.InteropServices;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// Keeps Tarjem's own windows out of the screenshots Tarjem takes.
///
/// Every hotkey starts by photographing the screen, and a screen capture sees whatever is on it -
/// including a dim layer or overlay of ours that is still up, or still fading out, from the press
/// before. The worst case was Alt+W: its selection window shows a frozen screenshot under a 35%
/// dim, so pressing Alt+W while one was already up froze a picture of the *dimmed* screen and
/// dimmed it again - 35%, then 58%, then 73% black - until the whole screen looked grey or simply
/// gone. The same thing happened with an Alt+Q dim still fading when Alt+W was pressed.
/// </summary>
public static class ScreenCaptureGuard
{
    /// <summary>The window is composited normally but left out of every capture - GDI screen
    /// copies included. Windows 10 2004 and later, which is this app's minimum.</summary>
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>
    /// Leaves a window out of screen captures, so it can't end up in the next hotkey's screenshot.
    /// Only for windows that are pure chrome - the dim layers and the selection window. A window
    /// showing a translation stays capturable, because a user taking their own screenshot of it is
    /// a reasonable thing to want; those are closed before a capture instead.
    /// </summary>
    public static void ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                Log.Debug("SetWindowDisplayAffinity failed ({Error}); overlay may appear in captures", Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not exclude an overlay from screen capture");
        }
    }

    /// <summary>
    /// Blocks until the desktop compositor has presented a frame, so a window that was just closed
    /// is really gone from the screen before it is photographed. Closing a window destroys it at
    /// once, but the frame on screen - and so in a capture - still shows it until DWM composes the
    /// next one. Twice, because the close can land partway through a composition pass.
    /// </summary>
    public static void WaitForCompositor()
    {
        try
        {
            DwmFlush();
            DwmFlush();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DwmFlush failed");
        }
    }
}
