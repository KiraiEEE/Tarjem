using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// One answer to "is the app dark right now", for the whole app.
///
/// The popup and the main window each used to read the Windows registry themselves, which meant
/// they could only ever follow the system - there was nowhere for a user preference to live, and
/// two copies of the same registry-poking code to keep in step.
/// </summary>
public static class ThemeService
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static readonly (string Id, string Label)[] Options =
    [
        (System, "Follow Windows"),
        (Light, "Light"),
        (Dark, "Dark"),
    ];

    /// <summary>The preference in force. Set once at startup and whenever Settings changes it, so
    /// windows created later (the popup, the overlays) don't need a SettingsService reference just
    /// to know what colour to be.</summary>
    public static string Preference { get; private set; } = System;

    /// <summary>True when the app should render dark, honouring the user's override.</summary>
    public static bool IsDark => Preference switch
    {
        Light => false,
        Dark => true,
        _ => IsWindowsDark(),
    };

    /// <summary>Whether the theme last handed to WPF-UI was dark; null before the first apply.</summary>
    private static bool? _appliedDark;

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies <paramref name="preference"/> to the WPF-UI theme manager and remembers it for
    /// everything that asks later.
    ///
    /// WPF-UI applies a Mica backdrop to <em>every</em> open window when the theme is applied, and
    /// on a transparent window that backdrop is drawn in all the transparent area - for the
    /// full-screen overlays, the whole monitor turned solid #202020 (or #F3F3F3 in light mode).
    /// This used to run on every settings save, and Gemini saves its model health after every
    /// request, so any Gemini answer landing while an overlay was open greyed out the screen. Hence
    /// both guards: only re-apply when the theme actually changes, and strip the backdrop back off
    /// our transparent windows when it does.
    /// </summary>
    public static void Apply(string? preference)
    {
        Preference = preference is Light or Dark or System ? preference : System;

        var dark = IsDark;
        if (_appliedDark == dark) return;
        _appliedDark = dark;

        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply the {Theme} theme", Preference);
        }

        RemoveBackdropFromTransparentWindows();
    }

    /// <summary>Turns the DWM system backdrop back off on every transparent window - the overlays
    /// and the popup cards. They draw their own backgrounds; a backdrop behind them only ever
    /// shows up as an opaque slab where they are meant to be see-through.</summary>
    private static void RemoveBackdropFromTransparentWindows()
    {
        if (global::System.Windows.Application.Current is not { } app) return;

        foreach (global::System.Windows.Window window in app.Windows)
        {
            if (!window.AllowsTransparency) continue;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) continue;

            try
            {
                var none = DWMSBT_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not clear the backdrop from a transparent window");
            }
        }
    }

    private static bool IsWindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the Windows theme preference");
        }

        // Dark is the safer default for an overlay that sits on top of games.
        return true;
    }
}
