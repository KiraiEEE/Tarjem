using System.IO;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// Writes the app's JSON files so that a crash, a power cut or the process being killed mid-write
/// leaves either the old file or the new one - never half of one. File.WriteAllText truncates the
/// target first, so dying between the truncate and the last byte left a settings.json or
/// history.json that no longer parsed, and the next launch quietly started from defaults.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);

        // Same folder, so this is a rename rather than a copy - the step that can't be half-done.
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Sets an unreadable file aside before the app replaces it with defaults, so what
    /// the user had can still be recovered by hand.</summary>
    public static void KeepCorruptCopy(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
            Log.Warning("Kept the unreadable {File} as {Backup}", Path.GetFileName(path), Path.GetFileName(backup));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not keep a copy of the unreadable {Path}", path);
        }
    }
}
