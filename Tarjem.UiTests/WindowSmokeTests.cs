using System.Windows;
using Tarjem.Models;

using RadioButton = System.Windows.Controls.RadioButton;
using Tarjem.Services;
using Tarjem.Views;
using Xunit;

namespace Tarjem.UiTests;

/// <summary>
/// Constructs every window in the app.
///
/// XAML is compiled to BAML but its *values* are only resolved at runtime, so a typo like
/// Symbol="Text24" (not a real SymbolRegular member) builds perfectly cleanly and then throws a
/// XamlParseException the first time the window is created. That shipped once and broke both
/// tray entry points into the app - the menu items silently did nothing, and double-clicking the
/// icon killed the process, because that path dispatches from a native callback where an
/// exception can't be caught.
///
/// These tests are the guard: if any window's XAML can't be realized, they fail here rather than
/// on the user's machine.
/// </summary>
[Collection(IsolatedDataCollection.Name)]
public class WindowSmokeTests
{
    [Fact]
    public void MainWindowCanBeConstructed() => StaRunner.Run(() =>
    {
        var settings = new SettingsService();
        var window = new MainWindow(new HistoryService(), settings, new TranslationService(settings));
        Realize(window);
    });

    /// <summary>Every nav page and settings tab, so a control that only exists on one of them
    /// can't slip through by virtue of that page being collapsed at startup.</summary>
    [Fact]
    public void EverySettingsTabCanBeShown() => StaRunner.Run(() =>
    {
        var settings = new SettingsService();
        var window = new MainWindow(new HistoryService(), settings, new TranslationService(settings));

        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.Show();

        foreach (var tabIndex in new[] { 0, 1 })
        {
            window.SelectTab(tabIndex);
            window.UpdateLayout();
        }

        foreach (var tab in new[] { "SettingsTabGeneral", "SettingsTabLanguages", "SettingsTabOverlays", "SettingsTabApis" })
        {
            var radio = (RadioButton)window.FindName(tab)!;
            Assert.NotNull(radio);
            radio.IsChecked = true;
            window.UpdateLayout();
        }

        window.Close();
    });

    [Fact]
    public void PopupWindowCanBeConstructed() => StaRunner.Run(() =>
    {
        var popup = new PopupWindow();

        // A pre-0.5 style id, which normalizes to Fluent - the settings.json of anyone upgrading
        // still has one of these in it.
        popup.ApplyStyle("CleanMatte");
        popup.SetTranslationResult(
            new TranslationResult
            {
                OriginalWord = "library",
                Meanings = ["A place where books are kept."],
                ArabicWord = "مكتبة",
                ArabicTranslation = "مكتبة",
                FullSentence = "The library was quiet.",
                TranslatedSentence = "كانت المكتبة هادئة.",
                TargetLanguage = "ar",
            },
            "dictionaryapi.dev",
            "google");
        Realize(popup);
    });

    [Fact]
    public void OverlayWindowCanBeConstructed() => StaRunner.Run(() =>
    {
        var overlay = new OverlayWindow();
        // Physical screen pixels - the overlay resolves the monitor and the DIP scale itself.
        overlay.SetHighlight(new Rect(200, 300, 80, 24));
        Realize(overlay);

        overlay.ShowWordOverlay("مكتبة", new Rect(200, 300, 80, 24), isRtl: true);
    });

    [Fact]
    public void RegionOverlayWindowCanBeConstructed() => StaRunner.Run(() =>
    {
        var overlay = new RegionOverlayWindow();
        overlay.PositionOverRegion(new Rect(100, 100, 400, 120));
        overlay.ShowLoading();
        Realize(overlay);

        overlay.SetProviders(TranslationService.TranslationProviders, "google");
        overlay.SetTranslation("hello", "مرحبا", isRtl: true, ocrLineCount: 1, sourceFontSizePhysical: 18);
    });

    /// <summary>
    /// The region overlay stays inside the rectangle the user dragged, however long the translation
    /// turns out to be.
    ///
    /// It used to shrink the text by at most 30% and then grow the plate downwards instead, which
    /// meant a wordy translation covered whatever sat below the text it was translating. Arabic
    /// makes this easy to hit: it is rendered larger than the Latin text it replaces, so it
    /// overflowed boxes that looked comfortably big enough.
    /// </summary>
    [Theory]
    [InlineData("Press the button to continue")]
    [InlineData("اضغط على الزر للمتابعة إلى القائمة التالية حيث يمكنك اختيار الإعدادات التي تناسبك ثم المتابعة إلى الشاشة الرئيسية للعبة، وهو نص طويل جدا لا يتسع في المربع")]
    public void RegionOverlayNeverGrowsPastTheSelection(string translation) => StaRunner.Run(() =>
    {
        var region = new Rect(100, 100, 420, 60);

        var overlay = new RegionOverlayWindow();
        overlay.PositionOverRegion(region);
        overlay.ShowLoading();

        overlay.Left = -32000;
        overlay.Top = -32000;
        overlay.ShowInTaskbar = false;
        overlay.Show();
        overlay.UpdateLayout();

        overlay.SetTranslation("Press the button to continue", translation,
            isRtl: true, ocrLineCount: 1, sourceFontSizePhysical: 20);
        overlay.UpdateLayout();

        var plate = overlay.PlateBounds;

        Assert.True(plate.Height <= region.Height + 0.5, $"plate grew to {plate.Height} for a {region.Height} region");
        Assert.True(plate.Width <= region.Width + 0.5, $"plate widened to {plate.Width} for a {region.Width} region");
        Assert.True(plate.Top >= region.Top - 0.5 && plate.Bottom <= region.Bottom + 0.5, "plate moved outside the region");

        overlay.Close();
    });

    [Fact]
    public void RegionSelectionWindowCanBeConstructed() => StaRunner.Run(() =>
    {
        Realize(new RegionSelectionWindow());
    });

    [Fact]
    public void SelectionButtonWindowCanBeShownAndHidden() => StaRunner.Run(() =>
    {
        var button = new SelectionButtonWindow();
        button.ShowNear(new System.Drawing.Point(400, 400));
        Realize(button);
        button.Hide();
    });

    /// <summary>Every popup style, on both cards that wear one - the Glass path in particular does
    /// real work (a screen capture and a crop) that the other two skip entirely.</summary>
    [Theory]
    [InlineData("Minimal")]
    [InlineData("Fluent")]
    [InlineData("Glass")]
    public void BothCardsCanWearEveryStyle(string style) => StaRunner.Run(() =>
    {
        var backdrop = ScreenBackdrop.CaptureMonitorFor(new Rect(10, 10, 100, 40));

        var popup = new PopupWindow();
        popup.ApplyStyle(style, backdrop);
        Realize(popup);

        var explain = new ExplainWindow();
        explain.ApplyStyle(style, backdrop);
        explain.ShowLoading("Nvidia");
        Realize(explain);
    });

    [Fact]
    public void ExplainWindowCanBeConstructed() => StaRunner.Run(() =>
    {
        var explain = new ExplainWindow();
        explain.ShowLoading("Nvidia");
        Realize(explain);

        explain.SetResult(new TranslationResult
        {
            OriginalWord = "Nvidia",
            Meanings = ["Nvidia is an American multinational technology company."],
            PartOfSpeech = "American technology company",
            TranslatedSentence = "إنفيديا هي شركة تكنولوجيا أمريكية.",
            IsEncyclopedic = true,
            DefinitionProvider = "wikipedia",
            SourceUrl = "https://en.wikipedia.org/wiki/Nvidia",
            TargetLanguage = "ar",
        });
    });

    [Fact]
    public void ExplainWindowHandlesAnEmptyResult() => StaRunner.Run(() =>
    {
        var explain = new ExplainWindow();
        Realize(explain);

        // The "nothing found" shape: no descriptor, no translation, no source link.
        explain.SetResult(new TranslationResult
        {
            OriginalWord = "asdfqwer",
            Meanings = ["Nothing found for “asdfqwer” in Wikipedia or Wikidata."],
            IsEncyclopedic = true,
        });
    });

    [Fact]
    public void WelcomeWindowCanBeConstructed() => StaRunner.Run(() =>
    {
        Realize(new WelcomeWindow(new SettingsService()));
    });

    /// <summary>Showing the window off-screen is what actually forces the XAML to be parsed and
    /// the visual tree built - merely calling the constructor can defer some of it.</summary>
    private static void Realize(Window window)
    {
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
        window.Close();
    }
}
