using System.IO;
using System.Text.Json;
using Serilog;
using Tarjem.Models;

namespace Tarjem.Services;

public class SettingsService
{
    // A property, not a cached field: AppPaths can be redirected (by the UI tests) after this
    // type is first touched, and a field would have captured the real user path.
    private static string SettingsPath => AppPaths.SettingsFile;

    public event EventHandler? Changed;

    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        Current = Load();
        MigratePlaintextKeys();
    }

    /// <summary>
    /// Moves any API key left in a pre-0.4 settings.json into the encrypted store and blanks it
    /// here. settings.json is plaintext and is the file users copy between machines, paste into
    /// bug reports and back up to cloud folders - a key has no business sitting in it.
    ///
    /// The blanking is deliberately conditional on the encrypted write succeeding, so a failed
    /// DPAPI write can't destroy the only copy of a key the user typed in.
    /// </summary>
    private void MigratePlaintextKeys()
    {
        (string? Value, string Name, Action Clear)[] legacy =
        [
            (Current.GeminiApiKey, SecureKeyService.Gemini, () => Current.GeminiApiKey = null),
            (Current.GroqApiKey, SecureKeyService.Groq, () => Current.GroqApiKey = null),
            (Current.CerebrasApiKey, SecureKeyService.Cerebras, () => Current.CerebrasApiKey = null),
            (Current.MerriamWebsterApiKey, SecureKeyService.MerriamWebster, () => Current.MerriamWebsterApiKey = null),
        ];

        var moved = 0;
        foreach (var (value, name, clear) in legacy)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            SecureKeyService.SaveUserKey(name, value);
            if (SecureKeyService.GetUserKey(name) == value.Trim())
            {
                clear();
                moved++;
            }
            else
            {
                Log.Warning("Could not move {Key} into the encrypted store; leaving it in settings.json", name);
            }
        }

        if (moved > 0)
        {
            Save();
            Log.Information("Moved {Count} API key(s) out of settings.json into the encrypted key store", moved);
        }
    }

    /// <summary>Discards the current settings entirely and persists a fresh default
    /// <see cref="AppSettings"/> - used by Settings > Clear all data. Deliberately resets
    /// everything including API keys, not just app-behavior preferences, since that's what
    /// "clear all data" means to a user clicking a destructive, confirmation-gated button.</summary>
    public void ResetToDefaults()
    {
        SecureKeyService.ClearAll();
        Current = new AppSettings();
        Save();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings to {Path}", SettingsPath);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings from {Path}, using defaults", SettingsPath);
        }

        return new AppSettings();
    }
}
