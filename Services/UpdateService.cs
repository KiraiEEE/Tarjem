using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace Tarjem.Services;

/// <param name="Version">The released version, normalised (no leading "v").</param>
/// <param name="Url">The release page to send the user to.</param>
public sealed record AvailableUpdate(string Version, string Url);

/// <summary>
/// Asks GitHub once per launch whether there is a newer release.
///
/// Deliberately does not download, install, or nag: it returns a version and a link, and Settings
/// shows a line of text. An installer-based app silently replacing itself is a much bigger
/// promise than this needs to make, and a background updater is a much bigger attack surface.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/IdleBuilt/Tarjem/releases/latest";
    private const string ReleasesPage = "https://github.com/IdleBuilt/Tarjem/releases/latest";

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // GitHub rejects requests with no User-Agent outright.
        _http.DefaultRequestHeaders.Add("User-Agent", "Tarjem-update-check");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>The newer release, or null when this build is current or the check failed. A failed
    /// check is not worth telling the user about - they didn't ask.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseApi, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } rawTag)
                return null;

            var latest = rawTag.TrimStart('v', 'V');
            if (!IsNewer(latest, CurrentVersion)) return null;

            var url = doc.RootElement.TryGetProperty("html_url", out var link) && link.GetString() is { Length: > 0 } page
                ? page
                : ReleasesPage;

            Log.Information("Update available: {Latest} (running {Current})", latest, CurrentVersion);
            return new AvailableUpdate(latest, url);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check failed");
            return null;
        }
    }

    /// <summary>Compares dotted numeric versions. Anything unparseable is treated as "not newer",
    /// so a malformed tag can never produce a phantom update prompt.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Pad(candidate), out var a) &&
        Version.TryParse(Pad(current), out var b) &&
        a > b;

    /// <summary>Version.TryParse needs at least major.minor, so "1" alone would fail.</summary>
    private static string Pad(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Count(c => c == '.') == 0 ? trimmed + ".0" : trimmed;
    }
}
