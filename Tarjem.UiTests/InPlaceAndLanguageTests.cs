using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tarjem.Core.Language;
using Tarjem.Models;
using Tarjem.Services;
using Tarjem.Views;
using Xunit;

using Cursors = System.Windows.Input.Cursors;
using Mouse = System.Windows.Input.Mouse;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButton = System.Windows.Input.MouseButton;

namespace Tarjem.UiTests;

/// <summary>
/// Alt+W's in-place mode, and the language pickers the result windows share.
/// </summary>
[Collection(IsolatedDataCollection.Name)]
public class InPlaceAndLanguageTests
{
    private static RegionOverlayWindow ShowOffscreen(Rect region)
    {
        var overlay = new RegionOverlayWindow();
        overlay.PositionOverRegion(region);
        overlay.ShowLoading();
        overlay.Left = -32000;
        overlay.Top = -32000;
        overlay.Show();
        overlay.UpdateLayout();
        return overlay;
    }

    [Fact]
    public void EachBlockIsDrawnWhereItsTextWas() => StaRunner.Run(() =>
    {
        var overlay = ShowOffscreen(new Rect(100, 100, 400, 200));
        try
        {
            var blocks = new[]
            {
                new RegionBlock(new Rect(10, 10, 80, 18), "Paramètres", Colors.Navy, Colors.White, 18, 1),
                new RegionBlock(new Rect(10, 60, 300, 40), "Vous pouvez changer la langue de toute l'interface ici.", Colors.Beige, Colors.Black, 16, 2),
            };

            overlay.SetBlocks(blocks, "Settings\nYou can change...", "Paramètres\nVous pouvez...", isRtl: false);
            overlay.UpdateLayout();

            Assert.True(overlay.IsInPlace);
            Assert.Equal(2, overlay.BlockCount);
            Assert.Equal("Paramètres\nVous pouvez...", overlay.CurrentTranslatedText);
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public void ThePanelReplacesTheBlocksWhenItTakesOver() => StaRunner.Run(() =>
    {
        var overlay = ShowOffscreen(new Rect(100, 100, 400, 200));
        try
        {
            overlay.SetBlocks([new RegionBlock(new Rect(0, 0, 50, 16), "Bonjour", Colors.Black, Colors.White, 16, 1)],
                "Hello", "Bonjour", isRtl: false);
            overlay.SetTranslation("Hello", "Bonjour");

            Assert.False(overlay.IsInPlace);
            Assert.Equal(0, overlay.BlockCount);
            Assert.Equal("Bonjour", overlay.CurrentTranslatedText);
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public void ALongOneLineTranslationNeverLeavesTheRegion() => StaRunner.Run(() =>
    {
        var region = new Rect(100, 100, 300, 120);
        var overlay = ShowOffscreen(region);
        try
        {
            var block = new RegionBlock(new Rect(200, 10, 60, 20),
                "Une traduction bien plus longue que le libellé qu'elle remplace", Colors.Black, Colors.White, 20, 1);
            overlay.SetBlocks([block], "Start", block.Text, isRtl: false);
            overlay.UpdateLayout();

            var layer = (Canvas)overlay.FindName("BlocksLayer");
            var patch = (FrameworkElement)layer.Children[0];
            var right = Canvas.GetLeft(patch) + patch.Width;
            var bottom = Canvas.GetTop(patch) + patch.Height;

            // Region in this window's DIPs starts at the monitor origin offset; compare widths
            // against the region's own right edge with a few pixels of padding allowance.
            var regionDip = (Rect)typeof(RegionOverlayWindow)
                .GetField("_regionDip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(overlay)!;

            Assert.True(right <= regionDip.Right + 6, $"patch reaches {right}, region ends at {regionDip.Right}");
            Assert.True(bottom <= regionDip.Bottom + 6, $"patch reaches {bottom}, region ends at {regionDip.Bottom}");
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public void TheLanguageChipNamesThePair() => StaRunner.Run(() =>
    {
        var overlay = ShowOffscreen(new Rect(100, 100, 400, 200));
        try
        {
            overlay.SetLanguages("fr", sourceIsAuto: true, "ar", ["en", "fr"], ["ar"]);
            var chip = (TextBlock)overlay.FindName("LanguageChipText");
            Assert.Contains("FR", chip.Text);
            Assert.Contains("AR", chip.Text);
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public void ThePickerOffersAutoDetectAndEveryLanguage() => StaRunner.Run(() =>
    {
        var panel = new LanguagePickerPanel(LanguagePickerPalette.Dark);
        panel.Load("en", sourceIsAuto: false, "ar", ["en", "ja"], ["ar", "fr"]);

        LanguagePick? picked = null;
        panel.Picked += (_, pick) => picked = pick;

        var rows = FindAll<Border>(panel).Where(b => b.Cursor == Cursors.Hand).ToList();

        // Source: Auto-detect + every language; target: two recent + every language; plus swap.
        Assert.True(rows.Count >= 1 + LanguageCatalog.All.Count + 2 + LanguageCatalog.All.Count,
            $"only {rows.Count} clickable rows");

        var auto = rows.First(r => FindAll<TextBlock>(r).Any(t => t.Text == "Auto-detect"));
        auto.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

        Assert.Equal(new LanguagePick(LanguageCatalog.Auto, null), picked);
    });

    [Fact]
    public void LanguageChoiceAppliesAndRemembersPicks() => StaRunner.Run(() =>
    {
        var settings = new SettingsService();
        settings.Current.SourceLanguage = "en";
        settings.Current.AutoDetectSourceLanguage = false;
        settings.Current.TargetLanguage = "ar";

        Assert.True(LanguageChoice.Apply(settings, new LanguagePick(LanguageCatalog.Auto, "uk")));
        Assert.True(settings.Current.AutoDetectSourceLanguage);
        Assert.Equal("uk", settings.Current.TargetLanguage);
        Assert.Equal(["uk", "ar"], settings.Current.RecentTargetLanguages);

        Assert.True(LanguageChoice.Apply(settings, new LanguagePick("ja", null)));
        Assert.False(settings.Current.AutoDetectSourceLanguage);
        Assert.Equal("ja", settings.Current.SourceLanguage);

        Assert.False(LanguageChoice.Apply(settings, new LanguagePick("ja", "uk")));
    });

    [Fact]
    public void ThePopupAndCardShowTheirLanguageChip() => StaRunner.Run(() =>
    {
        var popup = new PopupWindow();
        popup.SetTranslationResult(new TranslationResult
        {
            OriginalWord = "maison", SourceLanguage = "fr", TargetLanguage = "en", Meanings = ["house"],
        }, "freedictionaryapi.com", "google");

        Assert.Single(FindAll<LanguageChip>(popup));
        popup.Close();

        var card = new ExplainWindow();
        card.ShowLoading("NVIDIA", "en");
        Assert.Equal("en", card.CurrentSourceLanguage);
        card.Close();
    });

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in FindAll<T>(child))
                yield return found;
    }
}
