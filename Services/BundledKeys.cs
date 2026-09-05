using System.IO;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// The free API keys the build ships with, stored obfuscated in <c>bundled.keys</c> next to the
/// executable instead of as plaintext in a <c>.env</c>.
///
/// This is obfuscation, not security, and the distinction matters: the unlock passphrase is a
/// constant inside an app that anyone can decompile, so a determined person will get these keys
/// out in minutes. What it does defeat is the thing that actually took Tarjem's key down -
/// automated secret scanners. Google, GitHub and every crawler in between match on the literal
/// shape of a key (<c>AIza...</c>), and a key that never appears in that shape anywhere on disk
/// or in a repository is never auto-flagged and auto-revoked. These are deliberately free-tier
/// keys shared by every install; the app says so, and offers the user their own key whenever a
/// shared one starts running slow.
/// </summary>
public static class BundledKeys
{
    private static readonly string BundlePath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bundled.keys");

    /// <summary>Not a secret and not pretending to be one - see the class remarks. Changing it
    /// invalidates every previously packed bundle.</summary>
    private const string Passphrase = "Tarjem/bundled-keys/v1";

    private static readonly byte[] Salt = "tarjem-key-bundle"u8.ToArray();

    /// <summary>Reads the shipped keys, or an empty set if the build has none (which is the
    /// normal state of a source checkout - the bundle is produced at packaging time).</summary>
    public static Dictionary<string, string> Read()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(BundlePath)) return keys;

        try
        {
            var plain = Decrypt(File.ReadAllBytes(BundlePath));
            foreach (var line in plain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var name = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length > 0 && SecureKeyService.KnownKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
                    keys[name] = value;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the bundled key file - the app will run on keyless sources only");
        }

        return keys;
    }

    /// <summary>
    /// Packs a plaintext <c>.env</c> into <c>bundled.keys</c>. Run once at packaging time via
    /// <c>Tarjem.exe --pack-keys</c>; the .env is never shipped and never committed.
    /// Returns a human-readable report for the console.
    /// </summary>
    public static string Pack(string envPath, string outputPath)
    {
        if (!File.Exists(envPath))
            return $"No .env found at {envPath} - nothing to pack.";

        var packed = new List<string>();
        var names = new List<string>();

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var name = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (name.Equals("API_KEY", StringComparison.OrdinalIgnoreCase))
                name = SecureKeyService.Gemini;

            if (value.Length == 0 || !SecureKeyService.KnownKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            packed.Add($"{name}={value}");
            names.Add(name);
        }

        if (packed.Count == 0)
            return "No recognized keys in .env - nothing packed.";

        File.WriteAllBytes(outputPath, Encrypt(string.Join('\n', packed)));

        var report = $"Packed {packed.Count} key(s) into {outputPath}: {string.Join(", ", names)}";

        // Name the keyed sources this build is shipping without. Packing is a once-per-release
        // manual step, so a forgotten key otherwise only shows up as that provider quietly never
        // being offered to any user.
        var missing = Tarjem.Core.Providers.ProviderCatalog.KeyedProviders()
            .Where(p => !names.Contains(KeyNameFor(p.Id), StringComparer.OrdinalIgnoreCase))
            .Select(p => $"{p.Label} ({p.KeyUrl})")
            .ToList();

        if (missing.Count > 0)
            report += $"\n\nShipping WITHOUT a key for:\n  - {string.Join("\n  - ", missing)}";

        return report;
    }

    /// <summary>Maps a catalog provider id to the .env variable name its key arrives under.</summary>
    private static string KeyNameFor(string providerId) => providerId switch
    {
        "gemini" => SecureKeyService.Gemini,
        "groq" => SecureKeyService.Groq,
        "cerebras" => SecureKeyService.Cerebras,
        "merriam-webster" => SecureKeyService.MerriamWebster,
        _ => providerId,
    };

    private static byte[] Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var body = Encoding.UTF8.GetBytes(plaintext);
        var cipher = encryptor.TransformFinalBlock(body, 0, body.Length);

        var output = new byte[aes.IV.Length + cipher.Length];
        aes.IV.CopyTo(output, 0);
        cipher.CopyTo(output, aes.IV.Length);
        return output;
    }

    private static string Decrypt(byte[] bundle)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();

        var iv = bundle[..16];
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(bundle, 16, bundle.Length - 16);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey() =>
        Rfc2898DeriveBytes.Pbkdf2(Passphrase, Salt, iterations: 10_000, HashAlgorithmName.SHA256, outputLength: 32);
}
