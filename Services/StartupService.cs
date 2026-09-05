using Microsoft.Win32;
using Serilog;

namespace Tarjem.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tarjem";

    /// <summary>
    /// Every registry write here is a side effect outside Tarjem's own data folder, so it is
    /// skipped entirely when running isolated. A test run once wrote an autostart entry pointing
    /// at <c>testhost.exe</c>, which would have launched the test harness on every Windows login.
    /// </summary>
    private static bool CanWriteRegistry => !AppPaths.IsIsolated;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read Run key to check startup-with-Windows state");
            return false;
        }
    }

    /// <summary>
    /// Syncs the registry autostart path with the current executable location.
    /// Call this at startup so that if the user moved the exe or reinstalled
    /// (changing the path the installer wrote), the registry entry stays correct.
    /// </summary>
    public static void SyncPath()
    {
        if (!CanWriteRegistry) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return;

            var stored = key.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(stored)) return;

            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return;

            var storedClean = stored.Trim('"');
            if (string.Equals(storedClean, current, StringComparison.OrdinalIgnoreCase)) return;

            Log.Information("Autostart path stale: stored={Stored} current={Current}, updating", storedClean, current);

            using var writeKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            writeKey?.SetValue(ValueName, $"\"{current}\"");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to sync autostart path");
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!CanWriteRegistry) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update Run key for startup-with-Windows (enabled={Enabled})", enabled);
        }
    }
}
