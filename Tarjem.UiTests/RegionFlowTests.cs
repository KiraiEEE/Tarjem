using System.Drawing;
using Tarjem.Services;
using Tarjem.Views;
using Xunit;

namespace Tarjem.UiTests;

/// <summary>
/// Pins the Alt+W fixes that are easy to regress without anything visibly failing: which source a
/// region is translated with, and when a capture is treated as blocked.
/// </summary>
[Collection(IsolatedDataCollection.Name)]
public class RegionFlowTests
{
    // ── Which source translates a region ──

    [Fact]
    public void AnExplicitRegionSourceIsAlwaysHonoured()
    {
        var settings = new SettingsService();
        settings.Current.RegionTranslationProvider = "mymemory";

        Assert.Equal("mymemory", new TranslationService(settings).ResolveRegionProvider());
    }

    [Fact]
    public void AutoPrefersTheBestAiSourceWithAKeyAndOtherwiseTheDefault()
    {
        var settings = new SettingsService();
        settings.Current.RegionTranslationProvider = TranslationService.AutoRegionProvider;
        settings.Current.DefaultTranslationProvider = "lingva";
        var service = new TranslationService(settings);

        // Whatever keys this machine happens to have, the answer must be the best-ranked AI source
        // among them - and the plain default only when there are none.
        var expected = new[] { "gemini", "groq", "cerebras" }
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(service.ResolveKey(id))) ?? "lingva";

        Assert.Equal(expected, service.ResolveRegionProvider());
    }

    [Fact]
    public void AutoIsOfferedFirstInTheRegionSourcePicker()
    {
        var sources = TranslationService.RegionTranslationSources;

        Assert.Equal(TranslationService.AutoRegionProvider, sources[0].Id);
        Assert.Contains(sources, s => s.Id == "google");
    }

    // ── Blocked captures ──

    [Fact]
    public void ASolidBlackCaptureIsTreatedAsBlocked()
    {
        using var bitmap = Solid(640, 360, Color.Black);

        Assert.True(RegionSelectionWindow.IsBlankCapture(bitmap));
    }

    [Fact]
    public void ADarkScreenWithAnythingOnItIsNotBlank()
    {
        using var bitmap = Solid(640, 360, Color.FromArgb(12, 12, 16));

        Assert.False(RegionSelectionWindow.IsBlankCapture(bitmap));
    }

    [Fact]
    public void ABlackScreenWithTextOnItIsNotBlank()
    {
        using var bitmap = Solid(640, 360, Color.Black);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 28))
        {
            for (var y = 0; y < 360; y += 40)
                g.DrawString("The quick brown fox jumps over the lazy dog", font, Brushes.White, 0, y);
        }

        Assert.False(RegionSelectionWindow.IsBlankCapture(bitmap));
    }

    private static Bitmap Solid(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);
        return bitmap;
    }
}
