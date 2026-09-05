using System.IO;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// Holds the API keys the app ships with, encrypted per Windows user via DPAPI.
///
/// On first run the keys arrive as plaintext in a <c>.env</c> next to the executable; they are
/// immediately re-encrypted to <c>%LOCALAPPDATA%\Tarjem\keys.dat</c> and the .env is deleted, so
/// a plaintext copy never survives past the first launch. These are deliberately free-tier keys
/// shared by every install - the app tells the user as much, and offers to use their own instead
/// whenever a shared key is running slow.
/// </summary>
public static class SecureKeyService
{
    public const string Gemini = "GEMINI_API_KEY";
    public const string Groq = "GROQ_API_KEY";
    public const string Cerebras = "CEREBRAS_API_KEY";
    public const string MerriamWebster = "MERRIAM_WEBSTER_API_KEY";

    /// <summary>Every key name the .env may carry. Anything else in the file is ignored.</summary>
    public static readonly string[] KnownKeys = [Gemini, Groq, Cerebras, MerriamWebster];

    /// <summary>Prefix under which a user's *own* key is stored, so it sits in the same encrypted
    /// file as the shipped one without overwriting it. The distinction has to survive: "is the
    /// user on the shared key" drives whether the UI offers them their own, and merging the two
    /// would make that unanswerable.</summary>
    private const string UserPrefix = "USER_";

    // Properties rather than cached fields so a redirected AppPaths (the UI tests) is honoured -
    // a test must never read or overwrite the real user's stored API keys.
    private static string KeyStore => Path.Combine(Path.GetDirectoryName(AppPaths.SettingsFile)!, "keys.dat");

    /// <summary>Pre-0.4 single-key file, still read once so an existing install doesn't lose the
    /// key it already migrated.</summary>
    private static string LegacyGeminiKeyFile => Path.Combine(Path.GetDirectoryName(AppPaths.SettingsFile)!, "key.dat");

    private static readonly string[] EnvSearchPaths =
    [
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tarjem.env"),
    ];

    private static Dictionary<string, string>? _cache;
    private static readonly object Gate = new();

    /// <summary>The bundled key for a provider, or null if the build shipped without one.
    /// Decrypted once and held in memory - DPAPI round-trips are not free, and a lookup must
    /// not pay for one.</summary>
    public static string? GetKey(string name)
    {
        lock (Gate)
        {
            _cache ??= Load();
            return _cache.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        }
    }

    public static bool HasKey(string name) => !string.IsNullOrWhiteSpace(GetKey(name));

    /// <summary>The user's own key for a provider, or null if they haven't supplied one and are
    /// riding the shared key the build ships with.</summary>
    public static string? GetUserKey(string name) => GetKey(UserPrefix + name);

    /// <summary>Stores or replaces the user's own key. Passing null/blank means "go back to the
    /// shared key", which is not the same as having no key at all.</summary>
    public static void SaveUserKey(string name, string? apiKey)
    {
        lock (Gate)
        {
            _cache ??= Load();

            var slot = UserPrefix + name;
            if (string.IsNullOrWhiteSpace(apiKey))
                _cache.Remove(slot);
            else
                _cache[slot] = apiKey.Trim();

            Persist(_cache);
        }
    }

    /// <summary>Drops every stored key - Settings > Clear all data.</summary>
    public static void ClearAll()
    {
        lock (Gate)
        {
            foreach (var path in new[] { KeyStore, LegacyGeminiKeyFile })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { Log.Warning(ex, "Failed to delete key store at {Path}", path); }
            }

            // Null, not empty: the next read re-runs Load(), which re-seeds the *shipped* keys
            // from bundled.keys. Clearing to an empty dictionary instead left the install with no
            // working keys at all until the next restart - "clear all data" should put the app
            // back to how it arrived, not break it.
            _cache = null;
        }
    }

    private static Dictionary<string, string> Load()
    {
        var keys = ReadStore();

        // A pre-0.4 install has its Gemini key in the old single-key file and no keys.dat.
        if (!keys.ContainsKey(Gemini))
        {
            var legacy = ReadLegacyGeminiKey();
            if (!string.IsNullOrWhiteSpace(legacy))
                keys[Gemini] = legacy;
        }

        // A fresh install has neither, but does ship keys alongside the executable: normally the
        // obfuscated bundle, or a plaintext .env during development.
        var shipped = BundledKeys.Read();
        foreach (var (name, value) in ReadEnv())
            shipped[name] = value;

        var changed = false;
        foreach (var (name, value) in shipped)
        {
            if (keys.ContainsKey(name)) continue;
            keys[name] = value;
            changed = true;
        }

        // Only drop the plaintext .env once the encrypted copy is definitely on disk. Persist
        // swallows its own failures, so deleting unconditionally meant a failed write (locked
        // file, full disk, roaming profile hiccup) destroyed the only copy of the keys.
        if (changed || (shipped.Count > 0 && !File.Exists(KeyStore)))
        {
            if (Persist(keys))
                DeleteEnvFiles();
            else
                Log.Warning("Keeping the plaintext .env: the encrypted key store could not be written");
        }

        return keys;
    }

    private static Dictionary<string, string> ReadStore()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(KeyStore)) return keys;

        try
        {
            var encrypted = File.ReadAllBytes(KeyStore);
            var plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            foreach (var line in plain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) keys[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (CryptographicException ex)
        {
            // Encrypted by a different Windows account, or the profile's DPAPI material changed.
            // The bytes are unrecoverable, so drop them and let the .env (if any) re-seed.
            Log.Warning(ex, "DPAPI decryption failed - discarding unreadable key store");
            try { File.Delete(KeyStore); } catch { }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read key store");
        }
        return keys;
    }

    private static string? ReadLegacyGeminiKey()
    {
        if (!File.Exists(LegacyGeminiKeyFile)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(LegacyGeminiKeyFile);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read the legacy single-key file");
            return null;
        }
    }

    private static Dictionary<string, string> ReadEnv()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in EnvSearchPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;

                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0) continue;

                    var name = trimmed[..eq].Trim();
                    var value = trimmed[(eq + 1)..].Trim();
                    if (value.Length == 0) continue;

                    // "API_KEY" is what the very first .env format called the Gemini key.
                    if (name.Equals("API_KEY", StringComparison.OrdinalIgnoreCase))
                        name = Gemini;

                    if (KnownKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
                        keys[name] = value;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read .env file at {Path}", path);
            }
        }

        return keys;
    }

    /// <summary>Writes the encrypted store. Returns false (having logged) if the write failed, so
    /// callers can avoid destroying whatever plaintext copy the keys came from.</summary>
    private static bool Persist(Dictionary<string, string> keys)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyStore)!);
            var plain = string.Join('\n', keys.Select(kv => $"{kv.Key}={kv.Value}"));
            File.WriteAllBytes(KeyStore,
                ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write the encrypted key store");
            return false;
        }
    }

    private static void DeleteEnvFiles()
    {
        foreach (var path in EnvSearchPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                Log.Information("Migrated bundled keys out of {Path} and deleted the plaintext copy", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete .env file at {Path}", path);
            }
        }
    }
}
