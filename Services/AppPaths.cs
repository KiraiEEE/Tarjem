using System.IO;

namespace Tarjem.Services;

/// <summary>
/// Centralizes where Tarjem keeps its writable data. Settings/history used to be written
/// next to the executable (AppDomain.CurrentDomain.BaseDirectory), which throws
/// UnauthorizedAccessException the moment the app is installed to Program Files, and would
/// be wiped out by any installer that replaces the app folder wholesale on update.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Overrides the data folder. Set by the UI tests before any service is constructed, because
    /// those tests build real MainWindow/WelcomeWindow instances and those windows genuinely save
    /// settings - which meant a test run rewrote the developer's own settings.json (it was found
    /// having replaced a real window size with the test harness's) and, through
    /// <see cref="StartupService"/>, wrote a Windows autostart entry pointing at testhost.exe.
    ///
    /// A test that quietly edits the machine it runs on is worse than no test.
    /// </summary>
    public static string? OverrideRoot { get; set; }

    private static string Root => OverrideRoot ?? DefaultRoot;

    private static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tarjem");

    /// <summary>True when running against a throwaway folder, so components with side effects
    /// beyond that folder (the registry, the tray) can opt out.</summary>
    public static bool IsIsolated => OverrideRoot != null;

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string HistoryFile => Path.Combine(Root, "history.json");
    public static string LogsDirectory => Path.Combine(Root, "logs");

    private static bool _initialized;

    /// <summary>Creates the LOCALAPPDATA\Tarjem folder structure and, on first run after
    /// upgrading from the old layout, migrates any settings/history sitting next to the
    /// exe. Must run before anything touches SettingsFile/HistoryFile. Safe to call more
    /// than once.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized && !IsIsolated) return;

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);

        MigrateLegacyFile("settings.json", SettingsFile);
        MigrateLegacyFile("history.json", HistoryFile);

        _initialized = true;
    }

    /// <summary>Each step (read old, write new, delete old) is its own try/catch: a
    /// permission failure at one step - e.g. the old install directory no longer being
    /// writable so the delete fails - shouldn't undo a step that already succeeded.</summary>
    private static void MigrateLegacyFile(string legacyFileName, string newPath)
    {
        if (File.Exists(newPath)) return;

        var legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, legacyFileName);

        string? content = null;
        try
        {
            if (File.Exists(legacyPath))
                content = File.ReadAllText(legacyPath);
        }
        catch
        {
            // Old location unreadable (e.g. no longer permitted) - start fresh at the new one.
        }

        if (content == null) return;

        try
        {
            File.WriteAllText(newPath, content);
        }
        catch
        {
            return; // Couldn't write the new copy; leave the old file in place untouched.
        }

        try
        {
            File.Delete(legacyPath);
        }
        catch
        {
            // Harmless leftover now that the new copy exists and is what the app reads from.
        }
    }
}
