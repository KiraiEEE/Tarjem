using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Serilog;
using Tarjem.Core.Providers;
using Tarjem.Models;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>One line in the "Your setup" panel.</summary>
public sealed class SetupStatus
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "•";
    public Brush Color { get; init; } = Brushes.Gray;
    public string Timing { get; init; } = "";
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Shown once on first launch.
///
/// Two panels: what you choose on the left, whether it actually works on the right. The right-hand
/// side is the point - onboarding used to pick sources and verify none of them, so the first sign
/// that (say) a Windows OCR language pack was missing came later, as a hotkey that silently did
/// nothing. Everything here is one press away from being proven.
///
/// Prose is kept short deliberately. The earlier version explained each choice in a paragraph,
/// which nobody reads on a setup screen and which made a five-control window feel like a form.
/// </summary>
public partial class WelcomeWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private const int AnimationMs = 240;

    private readonly SettingsService _settings;
    private readonly ObservableCollection<SetupStatus> _status = [];
    private bool _isLoading = true;

    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99));

    private enum HotkeyTarget { None, Word, Region, Explain }
    private HotkeyTarget _capturing = HotkeyTarget.None;

    public WelcomeWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        StatusList.ItemsSource = _status;

        SourceLanguageCombo.ItemsSource = LanguageFlags.Sources();
        TargetLanguageCombo.ItemsSource = LanguageFlags.Targets();
        SelectLanguage(SourceLanguageCombo, _settings.Current.SourceLanguage);
        SelectLanguage(TargetLanguageCombo, _settings.Current.TargetLanguage);

        SelectionPopupToggle.IsChecked = _settings.Current.SelectionPopupEnabled;

        RefreshHotkeyButtons();
        _isLoading = false;

        ApplyRecommendations();
        ShowIdleStatus();
    }

    private void WelcomeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Never taller or wider than the screen it opens on - the design size assumes 1080p.
        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 40) Height = Math.Max(MinHeight, work.Height - 40);
        if (Width > work.Width - 40) Width = Math.Max(MinWidth, work.Width - 40);

        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;

        var duration = new Duration(TimeSpan.FromMilliseconds(AnimationMs));
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = EaseOut });
    }

    // ── Languages drive the recommendations ──

    private void LanguagePair_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        ApplyRecommendations();
        ShowIdleStatus();
    }

    private string SourceCode => (SourceLanguageCombo.SelectedItem as LanguageOption)?.Code ?? "en";
    private string TargetCode => (TargetLanguageCombo.SelectedItem as LanguageOption)?.Code ?? "ar";
    private string SourceName => (SourceLanguageCombo.SelectedItem as LanguageOption)?.Name ?? "English";

    private static void SelectLanguage(ComboBox combo, string code) =>
        combo.SelectedItem = combo.ItemsSource.OfType<LanguageOption>().FirstOrDefault(l => l.Code == code)
                             ?? combo.ItemsSource.OfType<LanguageOption>().FirstOrDefault();

    /// <summary>Re-picks the best sources for the current pair and refreshes the advanced pickers
    /// to match.</summary>
    private void ApplyRecommendations()
    {
        var dictionary = ProviderCatalog.RecommendedFor(ProviderKind.Dictionary, SourceCode, HasKey);
        var translation = ProviderCatalog.RecommendedFor(ProviderKind.Translation, TargetCode, HasKey);

        PopulateProviders(DictionaryProviderCombo, ProviderKind.Dictionary, SourceCode, dictionary?.Id);
        PopulateProviders(TranslationProviderCombo, ProviderKind.Translation, TargetCode, translation?.Id);

        AdvancedSummaryText.Text =
            $"Definitions from {dictionary?.Label ?? "the best available source"}, " +
            $"translation by {translation?.Label ?? "the best available source"}. " +
            "If one is unavailable, Tarjem uses the next best automatically.";

        var installed = OcrService.IsWindowsLanguagePackInstalled(SourceCode);
        LanguagePackWarningText.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        LanguagePackWarningText.Text = installed
            ? ""
            : $"Windows can't read {SourceName} from the screen yet. Add it under Windows Settings → Time & language → Language & region.";

        UpdateKeyField();
    }

    /// <summary>Shows the key box only when a chosen source actually needs one.</summary>
    private void UpdateKeyField()
    {
        var keyed = new[]
            {
                ProviderCatalog.Find(SelectedTag(DictionaryProviderCombo) ?? "", ProviderKind.Dictionary),
                ProviderCatalog.Find(SelectedTag(TranslationProviderCombo) ?? "", ProviderKind.Translation),
            }
            .FirstOrDefault(p => p is { RequiresKey: true });

        if (keyed == null)
        {
            AdvancedApiKeyBox.Visibility = Visibility.Collapsed;
            KeySignupText.Visibility = Visibility.Collapsed;
            return;
        }

        AdvancedApiKeyBox.Visibility = Visibility.Visible;
        KeySignupText.Visibility = Visibility.Visible;
        KeySignupText.Text = $"{keyed.Label} needs a key — free at {keyed.KeyUrl}";
        AdvancedApiKeyBox.Tag = keyed.Id;
    }

    private static bool HasKey(ProviderDescriptor provider) =>
        TranslationService.KeyNameFor(provider.Id) is { } name &&
        (!string.IsNullOrWhiteSpace(SecureKeyService.GetUserKey(name)) || SecureKeyService.HasKey(name));

    // ── Setup status ──

    private void ShowIdleStatus()
    {
        _status.Clear();
        _status.Add(new SetupStatus
        {
            Title = "Not checked yet",
            Detail = "Press Test all to send one real request to each source and see what answers.",
            Glyph = "•",
            Color = Idle,
        });
    }

    /// <summary>
    /// Runs the setup the user just chose, for real. Checks in the order they would actually bite:
    /// can Windows read this language at all, then does each chosen source answer.
    /// </summary>
    private async void TestSetupButton_Click(object sender, RoutedEventArgs e)
    {
        TestSetupButton.IsEnabled = false;
        _status.Clear();

        try
        {
            var installed = OcrService.IsWindowsLanguagePackInstalled(SourceCode);
            _status.Add(new SetupStatus
            {
                Title = $"Reading {SourceName} from the screen",
                Detail = installed ? "" : "Windows OCR pack not installed — lookups will fall back to English.",
                Glyph = installed ? "✓" : "!",
                Color = installed ? Good : Bad,
            });

            // A throwaway instance: the app's own is built after onboarding closes.
            var translation = new TranslationService(_settings);

            await ProbeAsync(translation, ProviderKind.Dictionary, SelectedTag(DictionaryProviderCombo), "Definitions");
            await ProbeAsync(translation, ProviderKind.Translation, SelectedTag(TranslationProviderCombo), "Translation");
            await ProbeAsync(translation, ProviderKind.Encyclopedia, "wikipedia", "Names (Wikipedia)");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Setup check failed");
            _status.Add(new SetupStatus { Title = "Check failed", Detail = ex.Message, Glyph = "!", Color = Bad });
        }
        finally
        {
            TestSetupButton.IsEnabled = true;
        }
    }

    private async Task ProbeAsync(TranslationService translation, ProviderKind kind, string? providerId, string label)
    {
        if (providerId == null) return;

        var name = ProviderCatalog.Find(providerId, kind)?.Label ?? providerId;

        var pending = new SetupStatus { Title = $"{label} — {name}", Detail = "Checking…", Glyph = "…", Color = Idle };
        _status.Add(pending);

        var probe = await translation.TestProviderAsync(kind, providerId);

        _status[_status.IndexOf(pending)] = new SetupStatus
        {
            Title = $"{label} — {name}",
            Detail = probe.Success ? "" : probe.Message ?? "No response.",
            Glyph = probe.Success ? "✓" : "!",
            Color = probe.Success ? Good : Bad,
            Timing = probe.Success ? $"{probe.LatencyMs} ms" : "",
        };
    }

    // ── Hotkeys ──

    private void WordHotkeyButton_Click(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Word);
    private void RegionHotkeyButton_Click(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Region);
    private void ExplainHotkeyButton_Click(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Explain);

    private Wpf.Ui.Controls.Button ButtonFor(HotkeyTarget target) => target switch
    {
        HotkeyTarget.Region => RegionHotkeyButton,
        HotkeyTarget.Explain => ExplainHotkeyButton,
        _ => WordHotkeyButton,
    };

    private void BeginCapture(HotkeyTarget target)
    {
        _capturing = target;
        ButtonFor(target).Content = "Press keys…";
        ButtonFor(target).Appearance = Wpf.Ui.Controls.ControlAppearance.Info;
        HotkeyStatusText.Text = "Press the combination you want. Esc cancels.";
        Keyboard.Focus(ButtonFor(target));
    }

    private void EndCapture()
    {
        _capturing = HotkeyTarget.None;
        foreach (var target in new[] { HotkeyTarget.Word, HotkeyTarget.Region, HotkeyTarget.Explain })
            ButtonFor(target).Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;

        RefreshHotkeyButtons();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturing == HotkeyTarget.None)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture();
            HotkeyStatusText.Text = "Change cancelled.";
            return;
        }

        if (HotkeyBinding.IsModifierKey(key)) return;

        // Registration happens when onboarding closes, so this only validates and stores.
        var candidate = HotkeyBinding.From(key, Keyboard.Modifiers);
        if (!candidate.Validate(out var error))
        {
            HotkeyStatusText.Text = error ?? "That combination can't be used.";
            return;
        }

        var others = _capturing switch
        {
            HotkeyTarget.Word => new[] { _settings.Current.RegionHotkey, _settings.Current.ExplainHotkey },
            HotkeyTarget.Region => [_settings.Current.WordHotkey, _settings.Current.ExplainHotkey],
            _ => new[] { _settings.Current.WordHotkey, _settings.Current.RegionHotkey },
        };

        if (others.Any(candidate.SameAs))
        {
            HotkeyStatusText.Text = $"{candidate.Display} is already used by another shortcut.";
            return;
        }

        switch (_capturing)
        {
            case HotkeyTarget.Word: _settings.Current.WordHotkey = candidate; break;
            case HotkeyTarget.Region: _settings.Current.RegionHotkey = candidate; break;
            case HotkeyTarget.Explain: _settings.Current.ExplainHotkey = candidate; break;
        }

        EndCapture();
        HotkeyStatusText.Text = $"Set to {candidate.Display}.";
    }

    private void RefreshHotkeyButtons()
    {
        WordHotkeyButton.Content = _settings.Current.WordHotkey.Display;
        RegionHotkeyButton.Content = _settings.Current.RegionHotkey.Display;
        ExplainHotkeyButton.Content = _settings.Current.ExplainHotkey.Display;
    }

    // ── Advanced ──

    private void AdvancedToggle_Changed(object sender, RoutedEventArgs e)
    {
        var open = AdvancedToggle.IsChecked == true;
        AdvancedPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AdvancedToggle.Content = open ? "Hide manual source picking" : "Choose sources manually";

        if (open)
            AdvancedPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170))) { EasingFunction = EaseOut });
    }

    private void AdvancedProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        UpdateKeyField();
    }

    // ── Finish ──

    private void GetStartedButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settings.Current;

        settings.SourceLanguage = SourceCode;
        settings.TargetLanguage = TargetCode;

        if (SelectedTag(DictionaryProviderCombo) is { } dictionaryId) settings.DefaultDictionaryProvider = dictionaryId;
        if (SelectedTag(TranslationProviderCombo) is { } translationId) settings.DefaultTranslationProvider = translationId;

        settings.SelectionPopupEnabled = SelectionPopupToggle.IsChecked == true;

        // A key typed here goes straight into the encrypted store; settings.json is plaintext.
        if (AdvancedApiKeyBox.Tag is string providerId &&
            !string.IsNullOrWhiteSpace(AdvancedApiKeyBox.Text) &&
            TranslationService.KeyNameFor(providerId) is { } keyName)
        {
            SecureKeyService.SaveUserKey(keyName, AdvancedApiKeyBox.Text.Trim());
        }

        FinishOnboarding();
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        // Skipping means "use the defaults", not "apply whatever happened to be selected".
        FinishOnboarding();
    }

    private void FinishOnboarding()
    {
        _settings.Current.HasCompletedOnboarding = true;
        _settings.Save();

        var fadeOut = new DoubleAnimation(RootGrid.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(160)))
        {
            EasingFunction = EaseOut,
        };
        fadeOut.Completed += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        RootGrid.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ── Combo helpers ──

    private void PopulateProviders(ComboBox combo, ProviderKind kind, string languageCode, string? selectedId)
    {
        var wasLoading = _isLoading;
        _isLoading = true;

        try
        {
            combo.Items.Clear();
            foreach (var provider in ProviderCatalog.Usable(kind, languageCode, HasKey))
                combo.Items.Add(new ComboBoxItem { Content = provider.Label, Tag = provider.Id });

            if (combo.Items.Count > 0)
            {
                var match = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == selectedId);
                combo.SelectedItem = match ?? combo.Items[0];
            }
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }

    private static string? SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
}
