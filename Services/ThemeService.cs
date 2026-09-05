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

    /// <summary>Applies <paramref name="preference"/> to the WPF-UI theme manager and remembers it
    /// for everything that asks later.</summary>
    public static void Apply(string? preference)
    {
        Preference = preference is Light or Dark or System ? preference : System;

        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply the {Theme} theme", Preference);
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
