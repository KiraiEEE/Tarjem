using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Serilog;
using Tarjem.Core.Providers;
using Tarjem.Models;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// The Alt+E result: what a name actually <em>is</em>.
///
/// A window of its own rather than a reskin of the word popup, because it is answering a different
/// question with a different shape of answer. The word popup is built around a dictionary entry -
/// phonetics, part of speech, a CEFR badge, a highlighted word inside a translated sentence - and
/// none of that applies to "NVIDIA is an American technology company". Squeezing an encyclopedia
/// answer into it meant relabelling headings and hiding half the controls, which is how a card
/// ends up looking like a form with most of its fields blank.
/// </summary>
public partial class ExplainWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const int AnimationMs = 170;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private bool _isClosing;
    private string? _sourceUrl;

    /// <summary>Raised with the text put on the clipboard, so a clipboard watcher can tell our own
    /// copy apart from the user copying something new.</summary>
    public event EventHandler<string>? TextCopied;

    /// <summary>The Alt+E settings this card was opened with. Assigned by
    /// <see cref="Services.OverlayService"/>; defaulted so the parameterless constructor stays
    /// usable on its own, which the smoke tests rely on.</summary>
    public AppSettings Options { get; set; } = new();

    /// <summary>The card surface, shared with the word popup so one setting really does govern
    /// both - see <see cref="CardStyler"/>.</summary>
    private readonly CardStyler _styler;

    public ExplainWindow()
    {
        InitializeComponent();
        _styler = new CardStyler(this, RootCard, CardShadow, GlassBackdropLayer, GlassTintLayer);
        ApplyTheme();
        ApplyStyle(CardStyler.Minimal);

        RootCard.SizeChanged += (_, _) =>
        {
            UpdateCardClip();
            _styler.RefreshBackdrop();
        };

        foreach (var (id, label) in TranslationService.EncyclopediaProviders)
            SourceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
    }

    /// <summary>Fetches the same term from a different encyclopedia source. Assigned by
    /// <see cref="Services.OverlayService"/> before the card is shown; null in the smoke tests,
    /// where the switcher simply does nothing.</summary>
    public Func<string, Task<TranslationResult?>>? SourceRequested { get; set; }

    /// <summary>The term this card is about, kept so the switcher can re-ask about it.</summary>
    private string _term = "";

    /// <summary>The source currently on show, so a failed switch can fall back to it.</summary>
    private string _currentProvider = "";

    /// <summary>Guards against a stale re-ask landing on top of a newer one.</summary>
    private int _sourceGeneration;

    /// <summary>Selects a provider without running the handler - used to show which source the
    /// automatic lookup actually answered from, which after a failover is not the configured
    /// default.</summary>
    private void SelectSourceSilently(string? providerId)
    {
        SourceCombo.SelectionChanged -= SourceCombo_SelectionChanged;

        SourceCombo.SelectedItem = SourceCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == providerId);

        SourceCombo.SelectionChanged += SourceCombo_SelectionChanged;
    }

    private async void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not ComboBoxItem { Tag: string providerId }) return;
        if (SourceRequested == null || _term.Length == 0) return;

        var generation = ++_sourceGeneration;

        var previousChip = SourceChip.Text;
        SourceChip.Text = "asking…";

        TranslationResult? result = null;
        try
        {
            result = await SourceRequested(providerId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Encyclopedia switcher failed for {Provider}", providerId);
        }

        if (generation != _sourceGeneration || _isClosing) return;

        if (result == null)
        {
            // The source has nothing for this name - which for the AI ones is a normal, honest
            // answer. Say so and put the card back the way it was rather than blanking it.
            SourceChip.Text = previousChip;
            SelectSourceSilently(_currentProvider);
            ShowSourceMiss(providerId);
            return;
        }

        AnimateContentSwap(() =>
        {
            SetResult(result);
            UpdateLayout();
            Repositioned?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Raised after the switcher replaces the content, so the overlay can re-place the
    /// card (it changes size) and re-register the Glass blur.</summary>
    public event EventHandler? Repositioned;

    /// <summary>Briefly says a source had no entry, in the descriptor line - the one place on the
    /// card that is already a short, muted, secondary note.</summary>
    private void ShowSourceMiss(string providerId)
    {
        var label = ProviderCatalog.Find(providerId, ProviderKind.Encyclopedia)?.Label ?? providerId;

        var restoreText = DescriptorText.Text;
        var restoreVisibility = DescriptorText.Visibility;

        DescriptorText.Text = $"{label} has no entry for this";
        DescriptorText.Visibility = Visibility.Visible;

        var revert = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            if (_isClosing) return;
            DescriptorText.Text = restoreText;
            DescriptorText.Visibility = restoreVisibility;
        };
        revert.Start();
    }

    /// <summary>
    /// Applies the popup visual style - the same Minimal/Fluent/Glass the word popup wears.
    /// </summary>
    /// <param name="backdrop">The screen snapshot the Glass style blurs, or null when the capture
    /// failed (Glass then falls back to a plain translucent card).</param>
    public void ApplyStyle(string? style, ScreenBackdrop? backdrop = null)
    {
        var isDark = ThemeService.IsDark;

        // The descriptor line ("American technology company") is the one piece of colour on this
        // card, so it is what carries the accent when there is one to carry. Minimal keeps the
        // app's own quiet purple, the same way the word popup does.
        DescriptorText.Foreground = new SolidColorBrush(
            CardStyler.Normalize(style) == CardStyler.Minimal
                ? Color.FromRgb(0x9F, 0x7A, 0xEA)
                : AccentColorService.AccentFor(isDark));

        _styler.Backdrop = backdrop;
        _styler.Apply(style, isDark ? Color.FromRgb(0x1E, 0x1E, 0x2E) : Color.FromRgb(0xF7, 0xF7, 0xF9), isDark);

        UpdateCardClip();
    }

    /// <summary>Re-registers the Glass blur with the screen after the card has moved or grown -
    /// which it always does, since it opens in a loading state and then fills with an answer.</summary>
    public void RefreshBackdrop() => _styler.RefreshBackdrop();

    /// <summary>Rounded corners have to be clipped as well as drawn: the glass layers are square
    /// children, and without this they would fill the card's corners back in.</summary>
    private void UpdateCardClip()
    {
        var w = RootCard.ActualWidth;
        var h = RootCard.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var r = RootCard.CornerRadius.TopLeft;
        RootCard.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    private void ApplyTheme()
    {
        var dark = ThemeService.IsDark;

        // The card surface itself (background, border, shadow) belongs to CardStyler - what's left
        // here is the content, which every style shares.
        var primary = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        var body = new SolidColorBrush(dark ? Color.FromRgb(0xDD, 0xDD, 0xDD) : Color.FromRgb(0x33, 0x33, 0x33));
        var muted = new SolidColorBrush(dark ? Color.FromRgb(0x88, 0x88, 0xAA) : Color.FromRgb(0x77, 0x77, 0x88));
        var chrome = new SolidColorBrush(dark ? Color.FromRgb(0x2A, 0x2A, 0x3A) : Color.FromRgb(0xE8, 0xE8, 0xEE));

        TitleText.Foreground = primary;
        SummaryText.Foreground = body;
        TranslatedSummaryText.Foreground = primary;
        KindLabel.Foreground = muted;
        SourceChip.Foreground = muted;
        LoadingText.Foreground = muted;
        TranslationDivider.Background = new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x3A, 0x4A) : Color.FromRgb(0xE0, 0xE0, 0xE6));

        foreach (var button in new[] { CopyButton, SourceButton })
        {
            button.Background = chrome;
            button.Foreground = body;
        }
    }

    /// <summary>Shows the card in its loading state, so the window appears immediately on the
    /// keypress instead of after the network round-trip.</summary>
    public void ShowLoading(string word)
    {
        _term = word;
        TitleText.Text = word;
        LoadingText.Visibility = Visibility.Visible;
        SummaryText.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = Visibility.Collapsed;
        DescriptorText.Visibility = Visibility.Collapsed;
        TranslationDivider.Visibility = Visibility.Collapsed;
        TranslatedSummaryText.Visibility = Visibility.Collapsed;
        SourceChip.Text = "";
    }

    public void SetResult(TranslationResult result)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = Visibility.Visible;

        TitleText.Text = result.OriginalWord;

        var summary = result.Meanings.FirstOrDefault() ?? "";
        SummaryText.Text = summary;
        SummaryText.FlowDirection = FlowFor(result.SourceLanguage);
        SummaryText.FontFamily = UiFonts.For(TranslationService.IsRtlLanguage(result.SourceLanguage));

        // The short descriptor ("American technology company") sits under the name, where it reads
        // as an appositive rather than as part of the summary.
        DescriptorText.Text = result.PartOfSpeech;
        DescriptorText.Visibility = string.IsNullOrWhiteSpace(result.PartOfSpeech)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var translated = result.TranslatedSentence;
        var showTranslation = !string.IsNullOrWhiteSpace(translated) && translated != summary;

        // "Show the original" only has anything to hide once there is a translation to show
        // instead - with it off and no translation, the card would otherwise come up empty.
        var showOriginal = !string.IsNullOrWhiteSpace(summary) &&
                           (Options.ExplainShowOriginal || !showTranslation);

        SummaryText.Visibility = showOriginal ? Visibility.Visible : Visibility.Collapsed;

        // The divider separates the two; with only one of them on screen there is nothing to
        // divide.
        TranslationDivider.Visibility = showTranslation && showOriginal ? Visibility.Visible : Visibility.Collapsed;
        TranslatedSummaryText.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        TranslatedSummaryText.Margin = new Thickness(0, showOriginal ? 0 : 6, 0, 0);

        if (showTranslation)
        {
            var isRtl = TranslationService.IsRtlLanguage(result.TargetLanguage);
            TranslatedSummaryText.Text = translated;
            TranslatedSummaryText.FlowDirection = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            TranslatedSummaryText.FontFamily = UiFonts.For(isRtl);
            TranslatedSummaryText.FontSize = UiFonts.SizeFor(13.5, isRtl);
        }

        // Naming the source is the card's only honesty mechanism now that a model can be the one
        // answering: "Wikipedia" and "Gemini AI" are very different claims about the same-looking
        // paragraph, and the reader has to be able to tell which they were handed.
        _currentProvider = result.DefinitionProvider ?? "";
        SourceChip.Text = ProviderCatalog.Find(_currentProvider, ProviderKind.Encyclopedia)?.Label ?? "";

        // The switcher shows whoever actually answered, which after a failover is not the source
        // configured in Settings.
        SelectSourceSilently(_currentProvider);

        _sourceUrl = result.SourceUrl;
        SourceButton.Visibility = string.IsNullOrWhiteSpace(_sourceUrl) || !Options.ExplainShowSourceLink
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static FlowDirection FlowFor(string languageCode) =>
        TranslationService.IsRtlLanguage(languageCode) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    // ── Animation ──

    public void AnimateIn()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));

        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = EaseOut });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = EaseOut });
    }

    /// <summary>Crossfades in new content once the lookup finishes, so the card doesn't visibly
    /// snap from "Looking it up…" to a paragraph.</summary>
    public void AnimateContentSwap(Action applyContent)
    {
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(70))) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) =>
        {
            applyContent();
            RootCard.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = EaseOut });
        };
        RootCard.BeginAnimation(OpacityProperty, fadeOut);
    }

    public void CloseAnimated()
    {
        if (_isClosing) return;
        _isClosing = true;

        var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));
        var fadeOut = new DoubleAnimation(RootCard.Opacity, 0, duration) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) => Close();

        RootCard.BeginAnimation(OpacityProperty, fadeOut);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(RootScale.ScaleX, 0.97, duration) { EasingFunction = EaseIn });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(RootScale.ScaleY, 0.97, duration) { EasingFunction = EaseIn });
    }

    // ── Actions ──

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslatedSummaryText.Visibility == Visibility.Visible
            ? TranslatedSummaryText.Text
            : SummaryText.Text;

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            TextCopied?.Invoke(this, text);
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not copy the explanation");
        }
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourceUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(_sourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Url}", _sourceUrl);
        }
    }
}
