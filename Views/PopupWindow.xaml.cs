using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Tarjem.Core.Translation;
using Tarjem.Models;
using Tarjem.Services;

namespace Tarjem.Views;

public partial class PopupWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    private const int AnimationMs = 160;

    private static readonly FontFamily PopupFont = new("Segoe UI, sans-serif");

    /// <summary>Base size of the translated-sentence box, as set in XAML. The RTL font renders
    /// smaller at the same size, so <see cref="UiFonts.SizeFor"/> scales it back up.</summary>
    private const double TranslationFontSize = 13.5;

    /// <summary>Font the translated sentence is drawn in - the Arabic serif face for RTL target
    /// languages, the UI font otherwise. Everything else in the popup (the English word, its
    /// definition, the badges) stays in the UI font.</summary>
    private FontFamily _translationFont = UiFonts.Ltr;

    private bool _isClosing;
    private bool _isDarkMode;

    /// <summary>Set by <see cref="OverlayService"/> before <see cref="Show"/> so the source
    /// switchers can fetch an alternative definition/translation on demand without the popup
    /// needing a direct reference to <see cref="TranslationService"/>.</summary>
    public Func<string, Task<DictionaryLookupResult>>? DictionarySourceRequested { get; set; }
    public Func<string, Task<TranslationLookupResult>>? TranslationSourceRequested { get; set; }

    /// <summary>Fetches a fresh combined Gemini result on demand. Only needed when Gemini isn't
    /// already the configured default for a section (in which case <see cref="_currentResult"/>
    /// already has it); the result is cached per-popup so picking "Gemini" in both dropdowns
    /// only ever calls Gemini once.</summary>
    public Func<Task<TranslationResult?>>? GeminiRequested { get; set; }

    /// <summary>Which sections to show and how. Assigned by <see cref="Services.OverlayService"/>
    /// before the popup is shown; defaulted here so the parameterless constructor stays usable
    /// on its own, which the smoke tests rely on. Kept as plain settings rather than a
    /// SettingsService reference to match how the callbacks above avoid depending on services.</summary>
    public AppSettings Options { get; set; } = new();
    private Task<TranslationResult?>? _geminiFetchTask;

    private TranslationResult? _currentResult;
    private int _dictGeneration;
    private int _translationGeneration;

    // Theme colors (resolved from system or game accent)
    private Color _bgColor;
    private Color _textColor;
    private Color _secondaryColor;
    private Color _mutedColor;
    private Color _dividerColor;
    private Color _badgeBg;
    private Color _badgeText;
    private Color _accentColor;
    private Color _accentLight;
    private Color _highlightBg;
    private Color _highlightFg;

    /// <summary>Owns the card surface - see <see cref="CardStyler"/>. Shared with the Alt+E card so
    /// one setting genuinely governs both.</summary>
    private readonly CardStyler _styler;

    public PopupWindow()
    {
        InitializeComponent();
        _isDarkMode = ThemeService.IsDark;
        _styler = new CardStyler(this, RootCard, CardShadow, GlassBackdropLayer, GlassTintLayer);
        RootCard.SizeChanged += OnCardSizeChanged;
        ApplyBaseColors();

        // A style is always applied again from Settings before the popup is shown; this is only so
        // a popup constructed on its own (the smoke tests) still comes up looking like a card.
        ApplyStyle(CardStyler.Minimal);

        PopulateSourceCombo(DictionarySourceCombo, TranslationService.DictionaryProviders, "Definition source");
        PopulateSourceCombo(TranslationSourceCombo, TranslationService.TranslationProviders, "Translation source");

        DictionarySourceCombo.DropDownOpened += SourceCombo_DropDownOpened;
        TranslationSourceCombo.DropDownOpened += SourceCombo_DropDownOpened;
    }

    private static readonly IEasingFunction ComboEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>The dropdown's own PopupAnimation="Fade" handles the opacity fade; this adds a
    /// short grow-from-top + settle motion on top of it (matching the card's own animation
    /// language) each time it opens, since a Popup's content isn't re-templated per-open so a
    /// one-shot XAML trigger can't retrigger it.</summary>
    private void SourceCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo || combo.Template == null) return;

        combo.ApplyTemplate();
        if (combo.Template.FindName("PopupScale", combo) is not ScaleTransform scale ||
            combo.Template.FindName("PopupTranslate", combo) is not TranslateTransform translate)
            return;

        var duration = new Duration(TimeSpan.FromMilliseconds(140));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, duration) { EasingFunction = ComboEase });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, duration) { EasingFunction = ComboEase });
    }

    /// <summary>The switcher is an icon-only arrow button with no visible label, so the combo's
    /// own Tag carries the tooltip text ("Definition source: Gemini") shown on hover - it's
    /// kept in sync with the current pick in the SelectionChanged handlers below.</summary>
    private static void PopulateSourceCombo(ComboBox combo, (string Id, string Label)[] providers, string tooltipPrefix)
    {
        foreach (var (id, label) in providers)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        combo.Tag = $"{tooltipPrefix}: {providers[0].Label}";
    }

    /// <summary>Selects the given provider id in the combo (falling back to its first item if
    /// the id is unrecognized) without going through SelectionChanged, so displaying the result
    /// the automatic lookup already fetched doesn't trigger a redundant re-fetch of the exact
    /// same data.</summary>
    private static void SelectComboSilently(ComboBox combo, string providerId, string tooltipPrefix, SelectionChangedEventHandler handler)
    {
        combo.SelectionChanged -= handler;
        var match = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == providerId);
        var selected = match ?? (ComboBoxItem)combo.Items[0]!;
        combo.SelectedItem = selected;
        combo.Tag = $"{tooltipPrefix}: {selected.Content}";
        combo.SelectionChanged += handler;
    }

    private Task<TranslationResult?> GetOrFetchGeminiResultAsync()
    {
        _geminiFetchTask ??= GeminiRequested?.Invoke() ?? Task.FromResult<TranslationResult?>(null);
        return _geminiFetchTask;
    }

    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCardClip();

        // The popup grows when a source switcher fetches a longer definition, so the Glass blur has
        // to be re-cropped for the card's new footprint.
        _styler.RefreshBackdrop();
    }

    private void UpdateCardClip()
    {
        var w = RootCard.ActualWidth;
        var h = RootCard.ActualHeight;
        if (w > 0 && h > 0)
        {
            var r = RootCard.CornerRadius.TopLeft;
            RootCard.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        }
    }

    /// <summary>
    /// Applies the user's chosen popup style. Must be called before <see cref="Show"/>, and again
    /// (via <see cref="RefreshBackdrop"/>) whenever the card moves, so the Glass blur stays lined
    /// up with what is actually behind it.
    /// </summary>
    /// <param name="backdrop">The screen snapshot the Glass style blurs; ignored by the other two,
    /// and null when the capture failed, which degrades Glass to a plain translucent card.</param>
    public void ApplyStyle(string? style, ScreenBackdrop? backdrop = null)
    {
        // Fluent and Glass are the styles that carry the Windows accent; Minimal keeps the app's
        // own quiet purple, because the whole point of it is not to assert a colour at all.
        if (CardStyler.Normalize(style) != CardStyler.Minimal)
        {
            _accentColor = AccentColorService.AccentFor(_isDarkMode);
            _accentLight = Color.FromArgb(_isDarkMode ? (byte)30 : (byte)25, _accentColor.R, _accentColor.G, _accentColor.B);
        }

        ApplyColorsToElements();

        // Last, because the card surface itself belongs to the styler - under Glass it stays
        // transparent so the blur underneath shows through.
        _styler.Backdrop = backdrop;
        _styler.Apply(style, _bgColor, _isDarkMode);

        AccentStripe.Visibility = Visibility.Collapsed;
        UpdateCardClip();
    }

    /// <summary>Re-registers the Glass blur with the screen after the card has been moved or
    /// resized. A no-op under the other styles.</summary>
    public void RefreshBackdrop() => _styler.RefreshBackdrop();

    private void ApplyBaseColors()
    {
        if (_isDarkMode)
        {
            _bgColor = Color.FromRgb(0x1E, 0x1E, 0x2E);
            _textColor = Colors.White;
            _secondaryColor = Color.FromRgb(0xDD, 0xDD, 0xDD);
            _mutedColor = Color.FromRgb(0x88, 0x88, 0x88);
            _dividerColor = Color.FromRgb(0x3A, 0x3A, 0x4A);
            _badgeBg = Color.FromRgb(0x3A, 0x3A, 0x4A);
            _badgeText = Color.FromRgb(0xBB, 0xBB, 0xBB);
            _accentColor = Color.FromRgb(0x7C, 0x3A, 0xED);
            _accentLight = Color.FromArgb(30, 0x7C, 0x3A, 0xED);
            _highlightBg = Color.FromRgb(0xFF, 0xEB, 0x3B);
            _highlightFg = Color.FromRgb(0x1A, 0x1A, 0x1A);
        }
        else
        {
            _bgColor = Color.FromRgb(0xF5, 0xF5, 0xF5);
            _textColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
            _secondaryColor = Color.FromRgb(0x33, 0x33, 0x33);
            _mutedColor = Color.FromRgb(0x88, 0x88, 0x88);
            _dividerColor = Color.FromRgb(0xE0, 0xE0, 0xE0);
            _badgeBg = Color.FromRgb(0xF0, 0xF0, 0xF0);
            _badgeText = Color.FromRgb(0x55, 0x55, 0x55);
            _accentColor = Color.FromRgb(0x7C, 0x3A, 0xED);
            _accentLight = Color.FromArgb(25, 0x7C, 0x3A, 0xED);
            _highlightBg = Color.FromRgb(0xFF, 0xEB, 0x3B);
            _highlightFg = Color.FromRgb(0x1A, 0x1A, 0x1A);
        }

        ApplyColorsToElements();
    }

    private void ApplyColorsToElements()
    {
        TranslationHeader.Foreground = new SolidColorBrush(_mutedColor);
        MeaningHeader.Foreground = new SolidColorBrush(_mutedColor);
        OriginalWordText.Foreground = new SolidColorBrush(_textColor);
        MeaningText.Foreground = new SolidColorBrush(_secondaryColor);
        PhoneticText.Foreground = new SolidColorBrush(_mutedColor);
        SynonymsText.Foreground = new SolidColorBrush(_mutedColor);
        Divider.Background = new SolidColorBrush(_dividerColor);
        CefrBadge.Background = new SolidColorBrush(_accentColor);

        // The badge now wears whatever the user picked in Windows, which includes the yellows and
        // pale greens that white text disappears into.
        CefrLevelText.Foreground = new SolidColorBrush(
            IsLightColor(_accentColor) ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White);

        // The copy glyphs are strokes, not text, so they don't inherit a foreground.
        var muted = new SolidColorBrush(_mutedColor);
        CopyTranslationBtnIcon.Stroke = muted;
        CopyWordBtnIcon.Stroke = muted;
        CopyWordInlineBtnIcon.Stroke = muted;

        PosBadge.Background = new SolidColorBrush(_badgeBg);
        PartOfSpeechText.Foreground = new SolidColorBrush(_badgeText);
        AccentStripe.Background = new SolidColorBrush(_accentColor);
    }

    private static bool IsLightColor(Color c)
    {
        double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return luminance > 140;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE);
    }

    public void AnimateIn()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));

        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = EaseOut });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = EaseOut });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = EaseOut });
    }

    public void CloseAnimated()
    {
        if (_isClosing) return;
        _isClosing = true;

        var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));
        var fadeOut = new DoubleAnimation(RootCard.Opacity, 0, duration) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) => Close();

        RootCard.BeginAnimation(OpacityProperty, fadeOut);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(RootScale.ScaleX, 0.96, duration) { EasingFunction = EaseIn });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(RootScale.ScaleY, 0.96, duration) { EasingFunction = EaseIn });
    }

    /// <summary>
    /// <paramref name="dictionaryDefaultId"/>/<paramref name="translationDefaultId"/> are
    /// whichever providers Settings had configured when this result was fetched, so the two
    /// switcher dropdowns can show the source that's actually on screen instead of always
    /// defaulting their own display to "Gemini".
    /// </summary>
    public void SetTranslationResult(TranslationResult result, string dictionaryDefaultId, string translationDefaultId)
    {
        _currentResult = result;
        OriginalWordText.Text = result.OriginalWord;

        GeminiStar1.Visibility = result.IsTranslationFromGemini ? Visibility.Visible : Visibility.Collapsed;
        GeminiStar2.Visibility = result.IsDefinitionFromGemini ? Visibility.Visible : Visibility.Collapsed;

        ApplySectionVisibility(result);
        ApplyInlineWordTranslation(result);

        ApplyDictionaryFields(
            result.Meanings.FirstOrDefault() ?? "Definition not found",
            result.Phonetic, result.PartOfSpeech, result.CefrLevel, result.Synonyms);

        ApplyEncyclopedicFraming(result);

        SetArabicText(result);

        SelectComboSilently(DictionarySourceCombo, dictionaryDefaultId, "Definition source", DictionarySourceCombo_SelectionChanged);
        SelectComboSilently(TranslationSourceCombo, translationDefaultId, "Translation source", TranslationSourceCombo_SelectionChanged);
    }

    /// <summary>
    /// Hides whichever halves of the popup the user turned off in Settings, and drops the divider
    /// with them so a one-section popup doesn't come up with a rule floating along its edge. The
    /// data for a hidden section was never requested, so this is only the visual half of the
    /// setting - see <see cref="Services.TranslationService.TranslateWord"/> for the other.
    /// </summary>
    private void ApplySectionVisibility(TranslationResult result)
    {
        var settings = Options;

        var showTranslation = settings.ShowTranslationSection;
        var showDictionary = settings.ShowDictionarySection;

        // Never show nothing. If both are off - which Settings tries to prevent, but stale
        // settings.json files can still express - fall back to the dictionary rather than
        // presenting an empty card.
        if (!showTranslation && !showDictionary)
            showDictionary = true;

        TranslationSection.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        DictionarySection.Visibility = showDictionary ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = showTranslation && showDictionary ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the single translated word beside the headword. Suppressed when it would
    /// only repeat the word itself (some providers echo the input back when they can't
    /// translate), which would otherwise read as "heart · heart".</summary>
    private void ApplyInlineWordTranslation(TranslationResult result)
    {
        var translated = result.ArabicWord;
        var show = Options.ShowInlineWordTranslation
                   && !string.IsNullOrWhiteSpace(translated)
                   && !string.Equals(translated.Trim(), result.OriginalWord.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!show)
        {
            InlineWordTranslationText.Visibility = Visibility.Collapsed;
            return;
        }

        var isRtl = TranslationService.IsRtlLanguage(result.TargetLanguage);
        InlineWordTranslationText.Text = translated.Trim();
        InlineWordTranslationText.FontFamily = UiFonts.For(isRtl);
        InlineWordTranslationText.FontSize = UiFonts.SizeFor(19, isRtl);
        InlineWordTranslationText.FlowDirection = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        InlineWordTranslationText.Foreground = new SolidColorBrush(_accentColor);

        InlineWordTranslationText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Relabels the definition block when the answer came from an encyclopedia. "NVIDIA is a
    /// technology company" is not a definition, and presenting it under a "Definition" heading
    /// next to a part-of-speech badge would misrepresent both what it is and where it came from.
    /// </summary>
    private void ApplyEncyclopedicFraming(TranslationResult result)
    {
        if (!result.IsEncyclopedic)
        {
            MeaningHeader.Text = "Definition";
            return;
        }

        MeaningHeader.Text = "About";
        CefrBadge.Visibility = Visibility.Collapsed;
        PhoneticText.Text = "";

        // Wikipedia's own short descriptor ("American technology company") lands in the
        // part-of-speech slot, which is exactly the right size and position for it.
        if (!string.IsNullOrWhiteSpace(result.PartOfSpeech))
        {
            PosBadge.Visibility = Visibility.Visible;
            PartOfSpeechText.Text = result.PartOfSpeech;
        }

        // Wikipedia text is CC BY-SA: the attribution has to actually lead back to the article,
        // not just name the site. SourceUrl was being populated and then thrown away here.
        SynonymsText.Visibility = Visibility.Visible;
        SynonymsText.Inlines.Clear();

        if (!string.IsNullOrWhiteSpace(result.SourceUrl) &&
            Uri.TryCreate(result.SourceUrl, UriKind.Absolute, out var articleUri))
        {
            var link = new Hyperlink(new Run("Read on Wikipedia"))
            {
                NavigateUri = articleUri,
                Foreground = new SolidColorBrush(_accentColor),
            };
            link.RequestNavigate += SourceLink_RequestNavigate;
            SynonymsText.Inlines.Add(link);
        }
        else
        {
            SynonymsText.Inlines.Add(new Run("From Wikipedia"));
        }
    }

    private void SourceLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open the source article at {Url}", e.Uri);
        }

        e.Handled = true;
    }

    private void ApplyDictionaryFields(string definition, string phonetic, string partOfSpeech, string cefrLevel, string[] synonyms)
    {
        // Switching source on an encyclopedia result comes back through here without going via
        // ApplyEncyclopedicFraming, so the header has to be reset here or a real definition keeps
        // the "About" heading (and the Wikipedia attribution) from the previous answer.
        MeaningHeader.Text = "Definition";
        MeaningText.Text = definition;
        PhoneticText.Text = string.IsNullOrEmpty(phonetic) ? "" : phonetic;

        if (!string.IsNullOrEmpty(cefrLevel))
        {
            CefrBadge.Visibility = Visibility.Visible;
            CefrLevelText.Text = cefrLevel;
        }
        else
        {
            CefrBadge.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(partOfSpeech))
        {
            PosBadge.Visibility = Visibility.Visible;
            PartOfSpeechText.Text = char.ToUpper(partOfSpeech[0]) + partOfSpeech[1..];
        }
        else
        {
            PosBadge.Visibility = Visibility.Collapsed;
        }

        if (synonyms.Length > 0)
        {
            SynonymsText.Visibility = Visibility.Visible;
            SynonymsText.Text = $"Synonyms: {string.Join(", ", synonyms.Take(4))}";
        }
        else
        {
            SynonymsText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Fired when the user picks a different definition source from the popup's
    /// dropdown. "Gemini" just restores whatever the automatic lookup already produced;
    /// anything else fetches on demand and merges only the definition fields, leaving the
    /// word/translation/highlight untouched.</summary>
    private async void DictionarySourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentResult == null || DictionarySourceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string providerId)
            return;

        DictionarySourceCombo.Tag = $"Definition source: {item.Content}";
        var generation = ++_dictGeneration;

        if (providerId == "gemini")
        {
            if (_currentResult.IsDefinitionFromGemini)
            {
                // Already the configured default for this section - Gemini was already called
                // for the automatic lookup, no need to fetch again.
                FadeSwap(MeaningText, () => ApplyDictionaryFields(
                    _currentResult.Meanings.FirstOrDefault() ?? "Definition not found",
                    _currentResult.Phonetic, _currentResult.PartOfSpeech, _currentResult.CefrLevel, _currentResult.Synonyms));
                return;
            }

            if (GeminiRequested == null) return;

            FadeSwap(MeaningText, () =>
            {
                MeaningText.Text = "Loading...";
                PhoneticText.Text = "";
                SynonymsText.Visibility = Visibility.Collapsed;
                CefrBadge.Visibility = Visibility.Collapsed;
                PosBadge.Visibility = Visibility.Collapsed;
            });

            var gemini = await GetOrFetchGeminiResultAsync();
            if (generation != _dictGeneration) return;

            if (gemini == null)
            {
                FadeSwap(MeaningText, () => MeaningText.Text = "Gemini is unavailable right now.");
                return;
            }

            FadeSwap(MeaningText, () => ApplyDictionaryFields(
                gemini.Meanings.FirstOrDefault() ?? "Definition not found",
                gemini.Phonetic, gemini.PartOfSpeech, gemini.CefrLevel, gemini.Synonyms));
            return;
        }

        if (DictionarySourceRequested == null) return;

        FadeSwap(MeaningText, () =>
        {
            MeaningText.Text = "Loading...";
            PhoneticText.Text = "";
            SynonymsText.Visibility = Visibility.Collapsed;
            CefrBadge.Visibility = Visibility.Collapsed;
            PosBadge.Visibility = Visibility.Collapsed;
        });

        var result = await DictionarySourceRequested(providerId);
        if (generation != _dictGeneration) return; // superseded by a newer selection

        if (result.Error != null)
        {
            FadeSwap(MeaningText, () => MeaningText.Text = result.Error);
            return;
        }

        // Other sources don't have a CEFR concept - Gemini's own value would be misleading here.
        FadeSwap(MeaningText, () => ApplyDictionaryFields(result.Meanings.FirstOrDefault() ?? "Definition not found",
            result.Phonetic, result.PartOfSpeech, "", result.Synonyms));
    }

    /// <summary>Quick crossfade for content swaps that happen after the popup is already
    /// showing (source switches) - the initial load doesn't need this since the whole card
    /// already animates in via <see cref="AnimateIn"/>. Kept short and cheap (opacity-only,
    /// no layout/measure work) so it stays snappy rather than feeling laggy.</summary>
    private void FadeSwap(UIElement element, Action applyContent)
    {
        element.BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation(element.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(70))) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) =>
        {
            applyContent();
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(130))) { EasingFunction = EaseOut });
        };
        element.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>Same idea as <see cref="DictionarySourceCombo_SelectionChanged"/> but for the
    /// Arabic translation/highlight section.</summary>
    private async void TranslationSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentResult == null || TranslationSourceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string providerId)
            return;

        TranslationSourceCombo.Tag = $"Translation source: {item.Content}";
        var generation = ++_translationGeneration;

        if (providerId == "gemini")
        {
            if (_currentResult.IsTranslationFromGemini)
            {
                FadeSwap(TranslatedSentenceBox, () => SetArabicText(_currentResult));
                return;
            }

            if (GeminiRequested == null) return;

            FadeSwap(TranslatedSentenceBox, () => SetTranslatedSentenceMessage("...", leftToRight: true));

            var gemini = await GetOrFetchGeminiResultAsync();
            if (generation != _translationGeneration) return;

            if (gemini == null)
            {
                FadeSwap(TranslatedSentenceBox, () => SetTranslatedSentenceMessage("Gemini is unavailable right now.", leftToRight: true));
                return;
            }

            FadeSwap(TranslatedSentenceBox, () => SetArabicText(gemini));
            return;
        }

        if (TranslationSourceRequested == null) return;

        FadeSwap(TranslatedSentenceBox, () => SetTranslatedSentenceMessage("...", leftToRight: true));

        var result = await TranslationSourceRequested(providerId);
        if (generation != _translationGeneration) return; // superseded by a newer selection

        if (result.Error != null)
        {
            FadeSwap(TranslatedSentenceBox, () => SetTranslatedSentenceMessage(result.Error, leftToRight: true));
            return;
        }

        FadeSwap(TranslatedSentenceBox, () => SetArabicText(new TranslationResult
        {
            OriginalWord = _currentResult.OriginalWord,
            ArabicWord = result.ArabicWord,
            ArabicTranslation = result.ArabicWord,
            FullSentence = _currentResult.FullSentence,
            TranslatedSentence = !string.IsNullOrEmpty(result.TranslatedSentence) ? result.TranslatedSentence : result.ArabicWord,
            TranslatedSentenceHighlightStart = -1,
            TargetLanguage = _currentResult.TargetLanguage,
        }));
    }

    private void SetTranslatedSentenceMessage(string text, bool leftToRight)
    {
        TranslatedSentenceBox.Visibility = Visibility.Visible;
        TranslatedSentenceBox.Document.Blocks.Clear();

        ApplyFlowDirection(leftToRight ? FlowDirection.LeftToRight : FlowDirection.RightToLeft);

        var para = new Paragraph
        {
            // Leading-edge alignment - see the note in SetArabicText.
            TextAlignment = TextAlignment.Left,
            FlowDirection = leftToRight ? FlowDirection.LeftToRight : FlowDirection.RightToLeft
        };
        para.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(_secondaryColor), FontFamily = _translationFont });
        TranslatedSentenceBox.Document.Blocks.Add(para);
    }

    private void SetArabicText(TranslationResult result)
    {
        var arabicText = !string.IsNullOrEmpty(result.TranslatedSentence)
            ? result.TranslatedSentence
            : result.ArabicTranslation;

        TranslatedSentenceBox.Document.Blocks.Clear();

        if (string.IsNullOrEmpty(arabicText))
        {
            TranslatedSentenceBox.Visibility = Visibility.Collapsed;
            return;
        }

        TranslatedSentenceBox.Visibility = Visibility.Visible;

        // Prefer the span Gemini itself marked inside the sentence translation - it reflects
        // whatever inflected/prefixed form it actually used. When there isn't one (every
        // non-Gemini provider translates the word and the sentence separately, so the word
        // rarely appears verbatim in the sentence), the locator falls back through
        // orthographic normalization and affix-tolerant token matching so the word still gets
        // highlighted instead of the highlight silently disappearing.
        var (highlightStart, highlightLength) = TranslationHighlighter.Locate(
            arabicText,
            !string.IsNullOrEmpty(result.ArabicWord) ? result.ArabicWord : result.ArabicTranslation,
            result.TranslatedSentenceHighlightStart,
            result.TranslatedSentenceHighlightLength);

        var textColor = new SolidColorBrush(_secondaryColor);

        // The RichTextBox's own FlowDirection (set in XAML) doesn't reliably propagate into
        // its hosted FlowDocument/Paragraph content - they need it set explicitly, otherwise
        // the text (and the highlight-splitting below) renders in the wrong order regardless
        // of what the control itself is marked as. Direction depends on the target language -
        // only Arabic/Hebrew/Persian/Urdu are RTL, everything else reads left-to-right.
        var isRtl = TranslationService.IsRtlLanguage(result.TargetLanguage);
        var flowDirection = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ApplyFlowDirection(flowDirection);

        // TextAlignment is logical, not physical: under RightToLeft flow, TextAlignment.Left
        // means the leading edge - the right-hand side - while TextAlignment.Right pushes the
        // Arabic to the physical left. Asking for Right here is exactly why the translation
        // looked left-aligned (i.e. "RTL not working") even though the direction was correct.
        var para = new Paragraph { TextAlignment = TextAlignment.Left, FlowDirection = flowDirection };

        if (highlightStart >= 0)
        {
            if (highlightStart > 0)
            {
                para.Inlines.Add(new Run(arabicText[..highlightStart])
                {
                    Foreground = textColor,
                    FontFamily = _translationFont
                });
            }

            para.Inlines.Add(new Run(arabicText.Substring(highlightStart, highlightLength))
            {
                Background = new SolidColorBrush(_highlightBg),
                Foreground = new SolidColorBrush(_highlightFg),
                FontWeight = FontWeights.SemiBold,
                FontFamily = _translationFont
            });

            int endIdx = highlightStart + highlightLength;
            if (endIdx < arabicText.Length)
            {
                para.Inlines.Add(new Run(arabicText[endIdx..])
                {
                    Foreground = textColor,
                    FontFamily = _translationFont
                });
            }
        }
        else
        {
            para.Inlines.Add(new Run(arabicText)
            {
                Foreground = textColor,
                FontFamily = _translationFont
            });
        }

        TranslatedSentenceBox.Document.Blocks.Add(para);
    }

    /// <summary>FlowDirection is inherited, but the RichTextBox itself was pinned to
    /// RightToLeft in XAML - which left the control mirrored for left-to-right target
    /// languages and, more importantly, meant the direction never actually tracked the
    /// language. Setting it on the control, the document and the paragraph together is what
    /// makes the text lay out in the right direction.</summary>
    private void ApplyFlowDirection(FlowDirection direction)
    {
        var isRtl = direction == FlowDirection.RightToLeft;

        _translationFont = UiFonts.For(isRtl);
        TranslatedSentenceBox.FontFamily = _translationFont;
        TranslatedSentenceBox.FontSize = UiFonts.SizeFor(TranslationFontSize, isRtl);
        TranslatedSentenceBox.FlowDirection = direction;
        TranslatedSentenceBox.Document.FlowDirection = direction;
    }

    private void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(TranslatedSentenceBox.Document.ContentStart,
                                TranslatedSentenceBox.Document.ContentEnd).Text;

        Copy(text, CopyTranslationBtnIcon, CopyTranslationTick);
    }

    private void CopyWord_Click(object sender, RoutedEventArgs e) =>
        Copy(OriginalWordText.Text, CopyWordBtnIcon, CopyWordTick);

    private void CopyWordInline_Click(object sender, RoutedEventArgs e) =>
        Copy(OriginalWordText.Text, CopyWordInlineBtnIcon, CopyWordInlineTick);

    /// <summary>Raised with whatever the popup put on the clipboard, so the selection watcher can
    /// tell our own copy apart from the user copying something new to translate - the same contract
    /// the explain card and the region overlay already had, and the reason copying from here used
    /// to make the app translate what it had just copied.</summary>
    public event EventHandler<string>? TextCopied;

    private void Copy(string? text, UIElement icon, UIElement tick)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            // Announced before the write: the clipboard notification arrives synchronously.
            TextCopied?.Invoke(this, text);
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // Another application can hold the clipboard open; not worth interrupting anyone over.
            Serilog.Log.Debug(ex, "Could not copy from the popup");
            return;
        }

        ShowCopied(icon, tick);
    }

    /// <summary>Swaps the copy glyph for a tick briefly - a click with no visible effect reads as a
    /// broken button, which is most of what was wrong with the old one.</summary>
    private static void ShowCopied(UIElement icon, UIElement tick)
    {
        icon.Visibility = Visibility.Collapsed;
        tick.Visibility = Visibility.Visible;

        var revert = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            icon.Visibility = Visibility.Visible;
            tick.Visibility = Visibility.Collapsed;
        };
        revert.Start();
    }
}
