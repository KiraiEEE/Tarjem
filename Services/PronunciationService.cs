using System.IO;
using System.Media;
using Serilog;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Tarjem.Services;

/// <summary>
/// Says a word out loud with the speech voices built into Windows - offline, no key, no package.
///
/// The popup has always shown <c>/ˈlaɪbrəri/</c> with no way to hear it, which for a vocabulary
/// tool was the clearest gap left. Windows ships a voice for most of the 18 languages, and the
/// rest can be added from Settings → Time &amp; language → Speech; where there is none, the
/// speaker button simply isn't shown rather than being a button that does nothing.
/// </summary>
public static class PronunciationService
{
    private static readonly Lazy<IReadOnlyList<VoiceInformation>> Voices = new(LoadVoices);

    private static readonly object PlayerLock = new();
    private static SoundPlayer? _player;

    /// <summary>A touch slower than conversational speed: the point is to hear how the word is
    /// said, and a single word at full speed is over before it registers.</summary>
    private const double SpeakingRate = 0.85;

    /// <summary>Enumerates the installed voices off the UI thread, so the first popup doesn't pay
    /// for it.</summary>
    public static void WarmUp() => _ = Voices.Value;

    public static bool CanSpeak(string? languageCode) => FindVoice(languageCode) != null;

    /// <summary>Speaks <paramref name="text"/>, cutting off anything still playing. False when there
    /// is no voice for the language or synthesis failed - never throws, since the caller is a
    /// click handler with nothing useful to do about it.</summary>
    public static async Task<bool> SpeakAsync(string? text, string? languageCode)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        var voice = FindVoice(languageCode);
        if (voice == null) return false;

        try
        {
            var audio = await Task.Run(async () =>
            {
                using var synth = new SpeechSynthesizer { Voice = voice };
                synth.Options.SpeakingRate = SpeakingRate;

                using var stream = await synth.SynthesizeTextToStreamAsync(text);
                var bytes = new byte[stream.Size];
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
                return bytes;
            });

            lock (PlayerLock)
            {
                _player?.Stop();
                _player?.Dispose();

                // The synthesized stream is a complete WAV file, which is exactly what SoundPlayer
                // plays - and it plays it asynchronously, so the popup stays responsive.
                _player = new SoundPlayer(new MemoryStream(audio));
                _player.Play();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not pronounce {Text} in {Language}", text, languageCode);
            return false;
        }
    }

    /// <summary>
    /// The voice for a language: an exact match on the region-qualified tag first (zh-CN vs zh-TW),
    /// then the Windows default voice if it speaks the language, then any voice that does. The
    /// default is preferred because it is the one the user picked, usually for a reason.
    /// </summary>
    private static VoiceInformation? FindVoice(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return null;

        var voices = Voices.Value;
        if (voices.Count == 0) return null;

        var exact = voices.FirstOrDefault(v => v.Language.Equals(languageCode, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var primary = languageCode.Split('-')[0];
        bool Speaks(VoiceInformation v) =>
            v.Language.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
            v.Language.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase);

        try
        {
            var preferred = SpeechSynthesizer.DefaultVoice;
            if (preferred != null && Speaks(preferred)) return preferred;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the default speech voice");
        }

        return voices.FirstOrDefault(Speaks);
    }

    private static IReadOnlyList<VoiceInformation> LoadVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices.ToList();
        }
        catch (Exception ex)
        {
            // Speech is an optional Windows component; a machine without it just gets no button.
            Log.Information(ex, "No speech voices available; pronunciation is off");
            return [];
        }
    }
}
