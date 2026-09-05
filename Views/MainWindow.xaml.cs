using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using Tarjem.Core.Providers;
using Tarjem.Core.Translation;
using Tarjem.Models;
using Tarjem.Services;

namespace Tarjem.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>Credits. Names only - what each one does belongs in the docs, not on a page
    /// someone opens once to check the version number.</summary>
    private record CreditedName(string Name);

    private static readonly CreditedName[] Libraries =
    [
        new(".NET 8 / WPF"),
        new("WPF-UI"),
        new("Windows.Media.Ocr"),
        new("Hardcodet.NotifyIcon.Wpf"),
        new("System.Drawing.Common"),
        new("Serilog"),
        new("IBM Plex Sans Arabic"),
        new("flag-icons"),
    ];

    /// <summary>Built from the provider catalog so this can't drift out of step with the sources
    /// the app actually talks to.</summary>
    private static CreditedName[] ExternalApis =>
        ProviderCatalog.All
            .Select(p => p.Label)
            .Distinct()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new CreditedName(name))
            .ToArray();

    private static readonly TimeSpan PageFadeDuration = TimeSpan.FromMilliseconds(140);
    private static readonly IEasingFunction PageFadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    private readonly HistoryService _history;
    private readonly SettingsService _settings;
    private readonly TranslationService _translation;

    /// <summary>Null when the window is created outside the running app (tests) - the settings
    /// still save, they just aren't applied to a live registration.</summary>
    private readonly HotkeyService? _hotkeys;

    /// <summary>Which hotkey the next keypress should be captured into, if any.</summary>
    private HotkeyTarget _capturing = HotkeyTarget.None;

    private enum HotkeyTarget { None, Word, Region, Explain }
    private List<HistoryEntry> _allEntries = new();
    private bool _isLoading;
    private FrameworkElement? _currentPage;
    private FrameworkElement? _currentSettingsSubPage;
    private DispatcherTimer? _resizeSaveTimer;

    public MainWindow(HistoryService history, SettingsService settings, TranslationService translation,
        HotkeyService? hotkeys = null)
    {
        _history = history;
        _settings = settings;
        _translation = translation;
        _hotkeys = hotkeys;
        _isLoading = true;

        InitializeComponent();

        RestoreWindowPlacement();

        _currentPage = HistoryPage;
        _currentSettingsSubPage = SettingsGeneralPage;

        LibraryList.ItemsSource = Libraries;
        ApiList.ItemsSource = ExternalApis;
        AboutVersionText.Text = $"Version {UpdateService.CurrentVersion}";

        // Once, here rather than in LoadApiKeyBoxes, which "Clear all data" calls again.
        foreach (var box in new[] { ApiKeyBox, GroqApiKeyBox, CerebrasApiKeyBox, MerriamWebsterApiKeyBox })
            MakePasteOnly(box);

        _binder = new SettingsBinder(_settings);
        _binder.Changed += (_, _) => OnBoundSettingChanged();

        _binder.Load(() =>
        {
            LoadHistory();
            LoadApiKeyBoxes();
            StartWithWindowsToggle.IsChecked = StartupService.IsEnabled();
            LoadDefaultProviders();
            LoadSourceLanguageSettings();
            BindSimpleSettings();
            ApplyHistoryVisibility();
            RefreshHotkeyButtons();
        });

        _isLoading = false;

        SizeChanged += MainWindow_SizeChanged;
        StateChanged += MainWindow_StateChanged;
    }

    private readonly SettingsBinder _binder;

    /// <summary>
    /// Every setting that is just "a control edits a property" - which is nearly all of them.
    /// Each of these used to be a four-line handler plus a matching load line; the property being
    /// edited was the only part that differed.
    /// </summary>
    private void BindSimpleSettings()
    {
        _binder.Bind(StartMinimizedToggle, s => s.StartMinimized, (s, v) => s.StartMinimized = v);
        _binder.Bind(RegionShowOriginalToggle, s => s.RegionShowOriginal, (s, v) => s.RegionShowOriginal = v);
        _binder.Bind(DimBehindOverlaysToggle, s => s.DimBehindOverlays, (s, v) => s.DimBehindOverlays = v);
        _binder.Bind(ShowInlineWordTranslationToggle, s => s.ShowInlineWordTranslation, (s, v) => s.ShowInlineWordTranslation = v);
        _binder.Bind(ShowWordOverlayToggle, s => s.ShowWordOverlayOnHighlight, (s, v) => s.ShowWordOverlayOnHighlight = v);
        _binder.Bind(EncyclopediaFallbackToggle, s => s.EncyclopediaFallbackEnabled, (s, v) => s.EncyclopediaFallbackEnabled = v);
        _binder.Bind(RegionMatchesSceneThemeToggle, s => s.RegionMatchesSceneTheme, (s, v) => s.RegionMatchesSceneTheme = v);
        _binder.Bind(SelectionPopupToggle, s => s.SelectionPopupEnabled, (s, v) => s.SelectionPopupEnabled = v);
        _binder.Bind(CheckForUpdatesToggle, s => s.CheckForUpdates, (s, v) => s.CheckForUpdates = v);

        _binder.Fill(ThemeCombo, ThemeService.Options, _settings.Current.Theme);
        _binder.Bind(ThemeCombo, s => s.Theme, (s, v) => s.Theme = v);

        _binder.Bind(PopupStyleComboBox, s => s.PopupVisualStyle, (s, v) => s.PopupVisualStyle = v);
        _binder.Bind(RegionFontSizeModeCombo, s => s.RegionFontSizeMode, (s, v) => s.RegionFontSizeMode = v);

        _binder.Bind(ExplainAnswerLengthCombo, s => s.ExplainAnswerLength, (s, v) => s.ExplainAnswerLength = v);
        _binder.Bind(ExplainShowOriginalToggle, s => s.ExplainShowOriginal, (s, v) => s.ExplainShowOriginal = v);
        _binder.Bind(ExplainShowSourceLinkToggle, s => s.ExplainShowSourceLink, (s, v) => s.ExplainShowSourceLink = v);

        // These carry extra behaviour beyond storing a value, so they keep their own handlers
        // (declared in XAML) rather than being bound here.
        HistoryEnabledToggle.IsChecked = _settings.Current.HistoryEnabled;
        ShowTranslationSectionToggle.IsChecked = _settings.Current.ShowTranslationSection;
        ShowDictionarySectionToggle.IsChecked = _settings.Current.ShowDictionarySection;
        ExplainTranslateAnswerToggle.IsChecked = _settings.Current.ExplainTranslateAnswer;
        ApplyExplainOptionAvailability();
    }

    /// <summary>"Keep the original above the translation" has nothing to choose between when there
    /// is no translation being fetched, so it greys out rather than sitting there implying the
    /// card has two halves.</summary>
    private void ApplyExplainOptionAvailability() =>
        ExplainShowOriginalCard.IsEnabled = ExplainTranslateAnswerToggle.IsChecked == true;

    private void ExplainTranslateAnswerToggle_Changed(object sender, RoutedEventArgs e)
    {
        ApplyExplainOptionAvailability();

        if (_isLoading || _binder.IsLoading) return;

        _settings.Current.ExplainTranslateAnswer = ExplainTranslateAnswerToggle.IsChecked == true;
        _settings.Save();
    }

    /// <summary>Runs after any bound setting is saved. Theme is applied immediately so the picker
    /// shows its own effect rather than needing a restart.</summary>
    private void OnBoundSettingChanged() => ThemeService.Apply(_settings.Current.Theme);

    /// <summary>Pushes stored values back into every bound control - used after "Clear all data"
    /// replaces the whole settings object underneath them.</summary>
    private void RefreshBoundControls()
    {
        var s = _settings.Current;

        StartMinimizedToggle.IsChecked = s.StartMinimized;
        RegionShowOriginalToggle.IsChecked = s.RegionShowOriginal;
        DimBehindOverlaysToggle.IsChecked = s.DimBehindOverlays;
        ShowInlineWordTranslationToggle.IsChecked = s.ShowInlineWordTranslation;
        ShowWordOverlayToggle.IsChecked = s.ShowWordOverlayOnHighlight;
        EncyclopediaFallbackToggle.IsChecked = s.EncyclopediaFallbackEnabled;
        RegionMatchesSceneThemeToggle.IsChecked = s.RegionMatchesSceneTheme;
        SelectionPopupToggle.IsChecked = s.SelectionPopupEnabled;
        CheckForUpdatesToggle.IsChecked = s.CheckForUpdates;
        HistoryEnabledToggle.IsChecked = s.HistoryEnabled;
        ShowTranslationSectionToggle.IsChecked = s.ShowTranslationSection;
        ShowDictionarySectionToggle.IsChecked = s.ShowDictionarySection;
        ExplainTranslateAnswerToggle.IsChecked = s.ExplainTranslateAnswer;
        ExplainShowOriginalToggle.IsChecked = s.ExplainShowOriginal;
        ExplainShowSourceLinkToggle.IsChecked = s.ExplainShowSourceLink;
        ApplyExplainOptionAvailability();

        SettingsBinder.Select(ThemeCombo, s.Theme);
        SettingsBinder.Select(PopupStyleComboBox, s.PopupVisualStyle);
        SettingsBinder.Select(RegionFontSizeModeCombo, s.RegionFontSizeMode);
        SettingsBinder.Select(ExplainAnswerLengthCombo, s.ExplainAnswerLength);
    }

    /// <summary>Called by the app when the startup update check finds something. Doing this as a
    /// quiet line in Settings rather than a popup is deliberate - nobody launched a translator to
    /// be told about a release.</summary>
    public void ShowAvailableUpdate(AvailableUpdate update)
    {
        UpdateNoticeText.Text = $"Version {update.Version} is available (you have {UpdateService.CurrentVersion}).";
        UpdateNoticePanel.Visibility = Visibility.Visible;
        UpdateNoticePanel.Tag = update.Url;
    }

    private void UpdateNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateNoticePanel.Tag is string url)
            OpenUrl(url);
    }

    /// <summary>Tarjem writes good logs that no user would ever find on their own - Explorer is
    /// one click, and it's the first thing anyone needs when reporting a problem.</summary>
    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogsDirectory);

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(System.IO.Path.GetDirectoryName(AppPaths.SettingsFile)!);

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open {Path}", path);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open {Url}", url);
        }
    }

    /// <summary>Fills the key boxes from the encrypted store. Only the user's own key is shown -
    /// the shipped one is never displayed, since putting a shared secret on screen is exactly how
    /// it ends up pasted into a screenshot.</summary>
    private void LoadApiKeyBoxes()
    {
        ApiKeyBox.Password = SecureKeyService.GetUserKey(SecureKeyService.Gemini) ?? "";
        GroqApiKeyBox.Password = SecureKeyService.GetUserKey(SecureKeyService.Groq) ?? "";
        CerebrasApiKeyBox.Password = SecureKeyService.GetUserKey(SecureKeyService.Cerebras) ?? "";
        MerriamWebsterApiKeyBox.Password = SecureKeyService.GetUserKey(SecureKeyService.MerriamWebster) ?? "";
        UpdateKeyStatusRows();
    }

    /// <summary>
    /// Lets a key be pasted in but never taken back out.
    ///
    /// The masking alone already means Copy would only ever yield a string of dots, but a key box
    /// that quietly hands you something when you press Ctrl+C is worse than one that plainly does
    /// nothing: the dots look enough like a key to end up pasted into a support thread. Cut is
    /// blocked for the same reason, and because "cut" on a stored credential is a delete with a
    /// confusing name.
    /// </summary>
    private static void MakePasteOnly(Control box)
    {
        CommandManager.AddPreviewExecutedHandler(box, (_, e) =>
        {
            if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut)
                e.Handled = true;
        });

        // Right-click would otherwise offer Copy and Cut back regardless of the handler above.
        // Suppressed rather than replaced with an empty menu, which would still flash one open.
        box.ContextMenuOpening += (_, e) => e.Handled = true;
    }

    /// <summary>Restores the last size, clamped to the monitor it will actually open on so a
    /// window saved on a larger screen doesn't come back bigger than the display.</summary>
    private void RestoreWindowPlacement()
    {
        var available = SystemParameters.WorkArea;

        Width = Math.Clamp(_settings.Current.MainWindowWidth, MinWidth, Math.Max(MinWidth, available.Width));
        Height = Math.Clamp(_settings.Current.MainWindowHeight, MinHeight, Math.Max(MinHeight, available.Height));

        if (_settings.Current.MainWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_isLoading) return;
        _settings.Current.MainWindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    public void SelectTab(int index)
    {
        if (index == 1)
            NavSettings.IsChecked = true;
        else
            NavHistory.IsChecked = true;
    }

    private void SupportButton_Click(object sender, RoutedEventArgs e)
    {
        new SupportWindow(_history) { Owner = this }.ShowDialog();
    }

    private void NavHistory_Checked(object sender, RoutedEventArgs e) => ShowPage(HistoryPage);
    private void NavSettings_Checked(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);
    private void NavAbout_Checked(object sender, RoutedEventArgs e) => ShowPage(AboutPage);

    private void SettingsTabGeneral_Checked(object sender, RoutedEventArgs e) => ShowSettingsSubPage(SettingsGeneralPage);
    private void SettingsTabLanguages_Checked(object sender, RoutedEventArgs e) => ShowSettingsSubPage(SettingsLanguagesPage);
    private void SettingsTabOverlays_Checked(object sender, RoutedEventArgs e) => ShowSettingsSubPage(SettingsOverlaysPage);
    private void SettingsTabApis_Checked(object sender, RoutedEventArgs e) => ShowSettingsSubPage(SettingsApisPage);

    /// <summary>Same crossfade as <see cref="ShowPage"/>, kept separate since the Settings
    /// sub-tabs and the top-level nav track their "current" page independently.</summary>
    private void ShowSettingsSubPage(FrameworkElement? page)
    {
        if (page == null || _currentSettingsSubPage == page) return;

        var previous = _currentSettingsSubPage;
        _currentSettingsSubPage = page;

        page.Visibility = Visibility.Visible;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(PageFadeDuration)) { EasingFunction = PageFadeEase });

        if (previous != null)
        {
            var fadeOut = new DoubleAnimation(1, 0, new Duration(PageFadeDuration)) { EasingFunction = PageFadeEase };
            fadeOut.Completed += (_, _) => previous.Visibility = Visibility.Collapsed;
            previous.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    /// <summary>Debounced so a live-drag resize doesn't hammer disk I/O with a Save() per pixel -
    /// only persists ~400ms after the user stops resizing. Also covers the app being killed
    /// outright (crash, taskkill) between resizes, unlike the OnClosed-only save this used to
    /// rely on.</summary>
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _resizeSaveTimer.Stop();
        _resizeSaveTimer.Tick -= ResizeSaveTimer_Tick;
        _resizeSaveTimer.Tick += ResizeSaveTimer_Tick;
        _resizeSaveTimer.Start();
    }

    private void ResizeSaveTimer_Tick(object? sender, EventArgs e)
    {
        _resizeSaveTimer?.Stop();
        if (WindowState == WindowState.Normal)
        {
            _settings.Current.MainWindowWidth = Width;
            _settings.Current.MainWindowHeight = Height;
            _settings.Save();
        }
    }

    /// <summary>
    /// Crossfades between pages instead of an instant Visibility swap - kept short
    /// and subtle so switching feels smooth without drawing attention to itself.
    /// Named page elements aren't wired up yet the moment the default-checked
    /// RadioButton fires during InitializeComponent, so this no-ops until then;
    /// the XAML default Visibility values already show the right page at startup.
    /// </summary>
    private void ShowPage(FrameworkElement? page)
    {
        if (page == null || _currentPage == page) return;

        var previous = _currentPage;
        _currentPage = page;

        page.Visibility = Visibility.Visible;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(PageFadeDuration)) { EasingFunction = PageFadeEase });

        if (previous != null)
        {
            var fadeOut = new DoubleAnimation(1, 0, new Duration(PageFadeDuration)) { EasingFunction = PageFadeEase };
            fadeOut.Completed += (_, _) => previous.Visibility = Visibility.Collapsed;
            previous.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    public void RefreshHistory()
    {
        LoadHistory();
    }

    private void LoadHistory()
    {
        _allEntries = _history.Entries.ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text;
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allEntries
            : _history.Search(query);
        UpdateDisplay(filtered);
    }

    private void UpdateDisplay(List<HistoryEntry> entries)
    {
        // Reference equality is fine here: entries are the same HistoryService-owned
        // objects across refreshes, so this preserves the current selection (e.g.
        // while typing a search query) instead of always snapping back to the top.
        var previousSelection = HistoryList.SelectedItem as HistoryEntry;

        // ItemsSource must be a fresh list instance: when the query is empty,
        // `entries` is literally `_allEntries` again (same reference as last time,
        // just mutated in place by a delete/clear), and WPF skips the refresh
        // entirely when a DependencyProperty is set to the exact same reference.
        HistoryList.ItemsSource = new List<HistoryEntry>(entries);
        EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"{entries.Count} translation{(entries.Count != 1 ? "s" : "")}";

        if (entries.Count == 0)
        {
            ShowDetail(null);
            return;
        }

        HistoryList.SelectedItem = previousSelection != null && entries.Contains(previousSelection)
            ? previousSelection
            : entries[0];
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowDetail(HistoryList.SelectedItem as HistoryEntry);
    }

    private void ShowDetail(HistoryEntry? entry)
    {
        if (entry == null)
        {
            DetailEmptyState.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            return;
        }

        DetailEmptyState.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        DetailWordText.Text = entry.Word;
        DetailStar.Visibility = entry.IsFromGemini ? Visibility.Visible : Visibility.Collapsed;

        DetailCefrBadge.Visibility = string.IsNullOrEmpty(entry.CefrLevel) ? Visibility.Collapsed : Visibility.Visible;
        DetailCefrText.Text = entry.CefrLevel;

        DetailPosBadge.Visibility = string.IsNullOrEmpty(entry.PartOfSpeech) ? Visibility.Collapsed : Visibility.Visible;
        DetailPosText.Text = string.IsNullOrEmpty(entry.PartOfSpeech) ? "" : char.ToUpper(entry.PartOfSpeech[0]) + entry.PartOfSpeech[1..];

        DetailPhoneticText.Text = entry.Phonetic;

        DetailDefinitionText.Text = string.IsNullOrEmpty(entry.Definition) ? "Definition not found" : entry.Definition;

        var hasSynonyms = entry.Synonyms.Length > 0;
        DetailSynonymsLabel.Visibility = hasSynonyms ? Visibility.Visible : Visibility.Collapsed;
        DetailSynonymsText.Visibility = hasSynonyms ? Visibility.Visible : Visibility.Collapsed;
        DetailSynonymsText.Text = hasSynonyms ? string.Join(", ", entry.Synonyms) : "";

        DetailArabicText.Text = string.IsNullOrEmpty(entry.ArabicTranslation) ? "—" : entry.ArabicTranslation;

        var hasSentence = !string.IsNullOrEmpty(entry.Sentence) && !string.IsNullOrEmpty(entry.TranslatedSentence);
        DetailSentencePanel.Visibility = hasSentence ? Visibility.Visible : Visibility.Collapsed;
        DetailSentenceText.Text = entry.Sentence;
        DetailTranslatedSentenceText.Text = entry.TranslatedSentence;

        DetailTimestampText.Text = $"Looked up {entry.Timestamp:MMM d, yyyy 'at' h:mm tt}";
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.Button)sender).Tag is not HistoryEntry entry) return;

        _history.Remove(entry);
        _allEntries.Remove(entry);
        ApplyFilter();
    }

    private void DeleteDetail_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry) return;

        _history.Remove(entry);
        _allEntries.Remove(entry);
        ApplyFilter();
    }

    /// <summary>
    /// Writes the history out as a vocabulary list. The data has been sitting there since 0.1 -
    /// word, translation, definition, the sentence it was found in, and a difficulty level is
    /// exactly a flashcard - and until now the only thing that could be done with it was scroll it.
    /// </summary>
    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        // Nothing to say when there's nothing to export - the empty history is already on screen.
        if (_allEntries.Count == 0) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export vocabulary",
            FileName = $"tarjem-vocabulary-{DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".txt",
            Filter = "Anki deck (tab-separated)|*.txt|Spreadsheet (CSV)|*.csv",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            VocabularyExporter.Export(
                _history.Entries, dialog.FileName, VocabularyExporter.FormatForPath(dialog.FileName));
        }
        catch (Exception ex)
        {
            // A save dialog that appears to do nothing is worth one message; a successful save
            // is self-evident from the file the user just named.
            Serilog.Log.Warning(ex, "Vocabulary export failed");
            System.Windows.MessageBox.Show(this, ex.Message, "Couldn't export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Clear history?",
            Content = "This removes every saved translation. This can't be undone.",
            PrimaryButtonText = "Clear all",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var result = await dialog.ShowDialogAsync();

        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            _history.Clear();
            _allEntries.Clear();
            ApplyFilter();
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        QueueKeySave(SecureKeyService.Gemini, ApiKeyBox.Password);

    private void GroqApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        QueueKeySave(SecureKeyService.Groq, GroqApiKeyBox.Password);

    private void CerebrasApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        QueueKeySave(SecureKeyService.Cerebras, CerebrasApiKeyBox.Password);

    private void MerriamWebsterApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        QueueKeySave(SecureKeyService.MerriamWebster, MerriamWebsterApiKeyBox.Password);

    private readonly Dictionary<string, string?> _pendingKeys = new();
    private DispatcherTimer? _keySaveTimer;

    /// <summary>
    /// Debounced so that typing (or pasting, which TextChanged also reports character by character
    /// in some IMEs) a 40-character key doesn't perform 40 DPAPI encrypt-and-write cycles and 40
    /// client rebuilds - each of which also discarded the cached Gemini model health, since the
    /// key fingerprint changed on every keystroke.
    /// </summary>
    private void QueueKeySave(string keyName, string text)
    {
        if (_isLoading) return;

        _pendingKeys[keyName] = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        _keySaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _keySaveTimer.Stop();
        _keySaveTimer.Tick -= KeySaveTimer_Tick;
        _keySaveTimer.Tick += KeySaveTimer_Tick;
        _keySaveTimer.Start();
    }

    private void KeySaveTimer_Tick(object? sender, EventArgs e)
    {
        _keySaveTimer?.Stop();
        FlushPendingKeys();
    }

    /// <summary>Writes any queued key straight into the encrypted store - never into settings.json
    /// - and rebuilds the clients that use it so the next lookup picks it up without a restart. A
    /// blank box means "go back to the shared key", not "no key at all".</summary>
    private void FlushPendingKeys()
    {
        if (_pendingKeys.Count == 0) return;

        foreach (var (name, value) in _pendingKeys)
            SecureKeyService.SaveUserKey(name, value);

        _pendingKeys.Clear();
        _translation.RefreshKeys();
        UpdateKeyStatusRows();
    }

    /// <summary>
    /// Tells the user, per keyed source, whether they have a key at all and whether it works. A
    /// key box that silently accepts anything is the worst version of this: a typo looks exactly
    /// like a working key until a lookup quietly falls back to something else.
    /// </summary>
    private void UpdateKeyStatusRows()
    {
        Apply(GeminiKeyStatus, "gemini");
        Apply(GroqKeyStatus, "groq");
        Apply(CerebrasKeyStatus, "cerebras");
        Apply(MerriamWebsterKeyStatus, "merriam-webster");

        void Apply(KeyStatusRow status, string providerId)
        {
            if (_translation.IsUsingOwnKey(providerId))
            {
                status.ShowChecking();
                _ = VerifyKeyAsync(status, providerId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_translation.ResolveKey(providerId)))
            {
                status.ShowMessage("Using the key Tarjem ships with — shared by every install, so add your own if this feels slow.");
                return;
            }

            status.ShowMessage("No key, so this source isn't offered. Paste one above to enable it.", warn: true);
        }
    }

    /// <summary>Generation counter per provider, so a key typed and corrected quickly doesn't have
    /// an older probe's verdict land on top of the newer one.</summary>
    private readonly Dictionary<string, int> _keyProbeGeneration = new();

    /// <summary>Sends one real request with the key the user just entered and reports back on the
    /// same line, so "did that work?" is answered without them having to go and try a lookup.</summary>
    private async Task VerifyKeyAsync(KeyStatusRow status, string providerId)
    {
        var generation = _keyProbeGeneration.TryGetValue(providerId, out var g) ? g + 1 : 1;
        _keyProbeGeneration[providerId] = generation;

        status.ShowChecking();

        var kind = providerId == "merriam-webster" || providerId == "gemini"
            ? ProviderKind.Dictionary
            : ProviderKind.Translation;

        ProviderProbe probe;
        try
        {
            probe = await _translation.TestProviderAsync(kind, providerId);
        }
        catch (Exception ex)
        {
            probe = ProviderProbe.Failed(ex.Message);
        }

        // Superseded by a newer keystroke, or the box was cleared while we were waiting.
        if (_keyProbeGeneration[providerId] != generation) return;
        if (!_translation.IsUsingOwnKey(providerId)) return;

        if (probe.Success)
            status.ShowVerified(probe.LatencyMs);
        else
            status.ShowInvalid();
    }

    /// <summary>
    /// One handler for every popup/overlay content switch, since they all do the same thing.
    ///
    /// The one rule enforced here is that the popup can never be emptied: turning off the last
    /// visible section would leave Alt+Q showing a blank card, which reads as a broken app rather
    /// than as a setting working. Whichever switch was just turned off comes straight back on if
    /// it was the last one standing.
    /// </summary>
    /// <summary>
    /// The two popup sections aren't independent, which is why they aren't just bound: turning off
    /// the last visible one would leave Alt+Q showing a blank card, which reads as a broken app
    /// rather than as a setting working. Whichever switch was just turned off comes straight back
    /// on if it was the last one standing.
    /// </summary>
    private void PopupSectionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _binder.IsLoading) return;

        if (ShowTranslationSectionToggle.IsChecked != true && ShowDictionarySectionToggle.IsChecked != true)
        {
            _isLoading = true;
            if (ReferenceEquals(sender, ShowDictionarySectionToggle))
                ShowTranslationSectionToggle.IsChecked = true;
            else
                ShowDictionarySectionToggle.IsChecked = true;
            _isLoading = false;
        }

        _settings.Current.ShowTranslationSection = ShowTranslationSectionToggle.IsChecked == true;
        _settings.Current.ShowDictionarySection = ShowDictionarySectionToggle.IsChecked == true;
        _settings.Save();
    }

    private static readonly Color ProbeColorOk = Color.FromRgb(0x43, 0xA0, 0x47);
    private static readonly Color ProbeColorBad = Color.FromRgb(0xE5, 0x39, 0x35);
    private static readonly Color ProbeColorNeutral = Color.FromRgb(0x9E, 0x9E, 0x9E);

    private void DictionaryProbeButton_Click(object sender, RoutedEventArgs e) =>
        _ = ProbeSelectedProviderAsync(
            ProviderKind.Dictionary, DefaultDictionaryProviderCombo, DictionaryProbeButton, DictionaryProbeDot);

    private void TranslationProbeButton_Click(object sender, RoutedEventArgs e) =>
        _ = ProbeSelectedProviderAsync(
            ProviderKind.Translation, DefaultTranslationProviderCombo, TranslationProbeButton, TranslationProbeDot);

    private void EncyclopediaProbeButton_Click(object sender, RoutedEventArgs e) =>
        _ = ProbeSelectedProviderAsync(
            ProviderKind.Encyclopedia, DefaultEncyclopediaProviderCombo, EncyclopediaProbeButton, EncyclopediaProbeDot);

    /// <summary>
    /// Sends one real request to whichever source is selected beside this dot and reports what
    /// came back. Per-source rather than one global test, because with a failover chain the only
    /// question worth answering is "does *this* source work for me right now" - and a source that
    /// needs a key it doesn't have has a different answer again, so it says so instead of
    /// reporting a network failure it never attempted.
    /// </summary>
    private async Task ProbeSelectedProviderAsync(ProviderKind kind, ComboBox combo, Button button, Border dot)
    {
        if ((combo.SelectedItem as ComboBoxItem)?.Tag is not string providerId) return;

        button.IsEnabled = false;
        dot.Background = new SolidColorBrush(ProbeColorNeutral);
        ShowProbeMessage($"Checking {providerId}…");

        try
        {
            var probe = await _translation.TestProviderAsync(kind, providerId);
            dot.Background = new SolidColorBrush(probe.Success ? ProbeColorOk : ProbeColorBad);
            ShowProbeMessage(probe.Success
                ? $"{ProviderLabel(kind, providerId)} answered in {probe.LatencyMs} ms."
                : $"{ProviderLabel(kind, providerId)}: {probe.Message}");
        }
        catch (Exception ex)
        {
            dot.Background = new SolidColorBrush(ProbeColorBad);
            ShowProbeMessage($"{ProviderLabel(kind, providerId)}: {ex.Message}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string ProviderLabel(ProviderKind kind, string providerId) =>
        ProviderCatalog.Find(providerId, kind)?.Label ?? providerId;

    private void ShowProbeMessage(string message)
    {
        ProbeResultText.Text = message;
        ProbeResultText.Visibility = Visibility.Visible;
    }

    private void StartWithWindowsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        StartupService.SetEnabled(StartWithWindowsToggle.IsChecked == true);
    }

    // ── Hotkey capture ──

    private void WordHotkeyButton_Click(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Word);
    private void RegionHotkeyButton_Click(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Region);
    private void ExplainHotkeyButton_Click(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.Explain);

    private Wpf.Ui.Controls.Button ButtonFor(HotkeyTarget target) => target switch
    {
        HotkeyTarget.Region => RegionHotkeyButton,
        HotkeyTarget.Explain => ExplainHotkeyButton,
        _ => WordHotkeyButton,
    };

    /// <summary>Puts one of the shortcut buttons into "listening" mode. The actual keys arrive in
    /// <see cref="OnPreviewKeyDown"/>, which is where they can be intercepted before any control
    /// treats them as navigation.</summary>
    private void BeginCapture(HotkeyTarget target)
    {
        _capturing = target;

        var button = ButtonFor(target);
        button.Content = "Press keys...";
        button.Appearance = Wpf.Ui.Controls.ControlAppearance.Info;

        foreach (var other in new[] { HotkeyTarget.Word, HotkeyTarget.Region, HotkeyTarget.Explain })
            if (other != target) ButtonFor(other).IsEnabled = false;

        SetHotkeyStatus("Press the combination you want. Esc cancels.", isError: false);
        Keyboard.Focus(button);
    }

    private void EndCapture()
    {
        _capturing = HotkeyTarget.None;

        foreach (var target in new[] { HotkeyTarget.Word, HotkeyTarget.Region, HotkeyTarget.Explain })
        {
            var button = ButtonFor(target);
            button.IsEnabled = true;
            button.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        }

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

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture();
            SetHotkeyStatus("Change cancelled.", isError: false);
            return;
        }

        // Wait for a real key - the user is still holding modifiers down.
        if (HotkeyBinding.IsModifierKey(key)) return;

        ApplyCapturedHotkey(HotkeyBinding.From(key, Keyboard.Modifiers));
    }

    private void ApplyCapturedHotkey(HotkeyBinding candidate)
    {
        var target = _capturing;

        var word = target == HotkeyTarget.Word ? candidate : _settings.Current.WordHotkey;
        var region = target == HotkeyTarget.Region ? candidate : _settings.Current.RegionHotkey;
        var explain = target == HotkeyTarget.Explain ? candidate : _settings.Current.ExplainHotkey;

        if (!candidate.Validate(out var error) || !TryApplyHotkeys(word, region, explain, out error))
        {
            // Stay in capture mode so the user can just press another combination.
            SetHotkeyStatus(error ?? "That combination can't be used.", isError: true);
            return;
        }

        EndCapture();
        SetHotkeyStatus($"Shortcut set to {candidate.Display}.", isError: false);
    }

    private void ResetHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing != HotkeyTarget.None) EndCapture();

        var word = HotkeyBinding.DefaultWord();
        var region = HotkeyBinding.DefaultRegion();
        var explain = HotkeyBinding.DefaultExplain();

        if (TryApplyHotkeys(word, region, explain, out var error))
        {
            RefreshHotkeyButtons();
            SetHotkeyStatus($"Reset to {word.Display}, {region.Display} and {explain.Display}.", isError: false);
        }
        else
        {
            SetHotkeyStatus(error ?? "Couldn't restore the default shortcuts.", isError: true);
        }
    }

    /// <summary>Registers the set with Windows first and only persists them if that worked, so
    /// settings can never end up describing shortcuts that aren't actually active.</summary>
    private bool TryApplyHotkeys(HotkeyBinding word, HotkeyBinding region, HotkeyBinding explain, out string? error)
    {
        if (_hotkeys != null && !_hotkeys.TryApply(word, region, explain, out error))
            return false;

        // No HotkeyService (tests construct the window standalone): validate what we can rather
        // than persisting a combination Windows would have rejected.
        if (_hotkeys == null &&
            (!word.Validate(out error) || !region.Validate(out error) || !explain.Validate(out error)))
            return false;

        _settings.Current.WordHotkey = word;
        _settings.Current.RegionHotkey = region;
        _settings.Current.ExplainHotkey = explain;
        _settings.Save();

        error = null;
        return true;
    }

    private void RefreshHotkeyButtons()
    {
        WordHotkeyButton.Content = _settings.Current.WordHotkey.Display;
        RegionHotkeyButton.Content = _settings.Current.RegionHotkey.Display;
        ExplainHotkeyButton.Content = _settings.Current.ExplainHotkey.Display;
    }

    private void SetHotkeyStatus(string text, bool isError)
    {
        HotkeyStatusText.Text = text;
        HotkeyStatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30))
            : (Brush)FindResource("TextFillColorSecondaryBrush");
    }

    private void HistoryEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settings.Current.HistoryEnabled = HistoryEnabledToggle.IsChecked == true;
        _settings.Save();
        ApplyHistoryVisibility();
    }

    /// <summary>Hides the History nav item when history is turned off, and moves off that page
    /// if it's the one currently showing.</summary>
    private void ApplyHistoryVisibility()
    {
        var enabled = _settings.Current.HistoryEnabled;
        NavHistory.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled && NavHistory.IsChecked == true)
            NavSettings.IsChecked = true;
    }

    private void LoadDefaultProviders()
    {
        PopulateProviderCombo(DefaultDictionaryProviderCombo, TranslationService.DictionaryProviders, _settings.Current.DefaultDictionaryProvider);
        PopulateProviderCombo(DefaultTranslationProviderCombo, TranslationService.TranslationProviders, _settings.Current.DefaultTranslationProvider);
        PopulateProviderCombo(DefaultEncyclopediaProviderCombo, TranslationService.EncyclopediaProviders, _settings.Current.DefaultEncyclopediaProvider);
        UpdateEncyclopediaProviderNote();

        TargetLanguageCombo.ItemsSource = LanguageFlags.Targets();
        SelectLanguage(TargetLanguageCombo, _settings.Current.TargetLanguage);
    }

    private static void SelectLanguage(ComboBox combo, string code) =>
        combo.SelectedItem = combo.ItemsSource?.OfType<LanguageOption>().FirstOrDefault(l => l.Code == code)
                             ?? combo.ItemsSource?.OfType<LanguageOption>().FirstOrDefault();

    private static void PopulateProviderCombo(ComboBox combo, (string Id, string Label)[] providers, string selectedId)
    {
        combo.Items.Clear();
        foreach (var (id, label) in providers)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });

        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string?)item.Tag == selectedId)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void DefaultDictionaryProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (DefaultDictionaryProviderCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            _settings.Current.DefaultDictionaryProvider = id;
            _settings.Save();
        }
    }

    private void DefaultTranslationProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (DefaultTranslationProviderCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            _settings.Current.DefaultTranslationProvider = id;
            _settings.Save();
        }
    }

    private void DefaultEncyclopediaProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEncyclopediaProviderNote();

        if (_isLoading) return;
        if (DefaultEncyclopediaProviderCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            _settings.Current.DefaultEncyclopediaProvider = id;
            _settings.Save();
        }
    }

    /// <summary>
    /// Says out loud what picking an AI source here actually means. The wikis and the models look
    /// like peers in a dropdown and are not: one returns a page you can go and read, the other
    /// returns a recollection. Someone choosing the second should be told that once, here, rather
    /// than discovering it from a confidently wrong card.
    /// </summary>
    private void UpdateEncyclopediaProviderNote()
    {
        var id = (DefaultEncyclopediaProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        EncyclopediaProviderNoteText.Text = id is "gemini" or "groq" or "cerebras"
            ? "AI sources answer from the model's memory, not from a page, so there is no \"Read more\" link and an answer can be wrong. They're asked to say when they don't recognize a name rather than guess, and Tarjem still tries Wikipedia and Wikidata if this one declines."
            : "";

        EncyclopediaProviderNoteText.Visibility =
            string.IsNullOrEmpty(EncyclopediaProviderNoteText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TargetLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (TargetLanguageCombo.SelectedItem is LanguageOption language)
        {
            _settings.Current.TargetLanguage = language.Code;
            _settings.Save();
        }
    }

    private void LoadSourceLanguageSettings()
    {
        SourceLanguageCombo.ItemsSource = LanguageFlags.Sources();
        SelectLanguage(SourceLanguageCombo, _settings.Current.SourceLanguage);
        AutoDetectSourceLanguageToggle.IsChecked = _settings.Current.AutoDetectSourceLanguage;
        UpdateLanguageDependentState();
    }

    private void SourceLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (SourceLanguageCombo.SelectedItem is LanguageOption language)
        {
            _settings.Current.SourceLanguage = language.Code;
            _settings.Save();
            UpdateLanguageDependentState();
        }
    }

    private void AutoDetectSourceLanguageToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settings.Current.AutoDetectSourceLanguage = AutoDetectSourceLanguageToggle.IsChecked == true;
        _settings.Save();
        UpdateLanguageDependentState();
    }

    /// <summary>
    /// Keeps the General and APIs tabs honest about what will actually happen for the current
    /// source language.
    ///
    /// This used to disable the definition-source picker for any non-English source and display
    /// "Locked to Gemini", on the stated grounds that the other dictionaries "only understand
    /// English words". That was simply untrue: <see cref="ProviderCatalog"/> lists 17 languages
    /// for FreeDictionaryAPI and Wiktionary, the lookup path never forced Gemini, and the failover
    /// chain was already picking the right source on its own. So the setting still worked - the UI
    /// just claimed it didn't, while disabling it. Now the picker stays live and the note names
    /// which sources actually cover the chosen language, straight from the catalog.
    /// </summary>
    private void UpdateLanguageDependentState()
    {
        var autoDetect = AutoDetectSourceLanguageToggle.IsChecked == true;
        var sourceCode = (SourceLanguageCombo.SelectedItem as LanguageOption)?.Code ?? "en";
        var nonEnglishSource = !autoDetect && sourceCode != "en";

        SourceLanguageCombo.IsEnabled = !autoDetect;
        SourceLanguageNoteText.Visibility = nonEnglishSource ? Visibility.Visible : Visibility.Collapsed;

        SourceLanguagePackMissingText.Visibility = nonEnglishSource && !OcrService.IsWindowsLanguagePackInstalled(sourceCode)
            ? Visibility.Visible
            : Visibility.Collapsed;

        DefaultDictionaryProviderCombo.IsEnabled = true;
        DictionaryProviderNoteText.Text = DictionaryCoverageNote(autoDetect, sourceCode);
        DictionaryProviderNoteText.Visibility =
            string.IsNullOrEmpty(DictionaryProviderNoteText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Which definition sources can and can't answer for the selected source language,
    /// derived from the catalog so it can't drift out of step with the failover chain.</summary>
    private string DictionaryCoverageNote(bool autoDetect, string sourceCode)
    {
        if (autoDetect)
            return "Auto-detect picks the language per lookup. If a lookup lands on a language your chosen source doesn't cover, Tarjem falls back to one that does.";

        if (sourceCode == "en")
            return "";

        var all = ProviderCatalog.OfKind(ProviderKind.Dictionary).ToList();
        var covers = all.Where(p => p.Supports(sourceCode)).Select(p => p.Label).ToList();
        var doesnt = all.Where(p => !p.Supports(sourceCode)).Select(p => p.Label).ToList();

        var language = (SourceLanguageCombo.SelectedItem as LanguageOption)?.Name ?? sourceCode;

        if (covers.Count == 0)
            return $"No definition source covers {language}. Lookups will still translate, but won't show a definition.";

        var note = $"Covering {language}: {string.Join(", ", covers)}.";
        if (doesnt.Count > 0)
            note += $" {string.Join(" and ", doesnt)} {(doesnt.Count == 1 ? "is" : "are")} English-only and will be skipped automatically.";

        return note;
    }

    private async void ClearDataButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Clear all data?",
            Content = "This removes every saved translation and resets every setting - including your API keys - back to their defaults. This can't be undone.",
            PrimaryButtonText = "Clear everything",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var result = await dialog.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        _history.Clear();
        _allEntries.Clear();
        ApplyFilter();

        _isLoading = true;
        try
        {
            // Anything the user typed but hasn't been flushed yet must not survive the reset.
            _pendingKeys.Clear();
            _keySaveTimer?.Stop();

            _settings.ResetToDefaults();
            _translation.RefreshKeys();

            LoadApiKeyBoxes();
            StartupService.SetEnabled(false);
            StartWithWindowsToggle.IsChecked = false;

            // Re-read every bound control from the freshly-defaulted settings. The bindings
            // themselves survive - only the displayed values need refreshing.
            _binder.Load(RefreshBoundControls);

            ApplyHistoryVisibility();
            TryApplyHotkeys(HotkeyBinding.DefaultWord(), HotkeyBinding.DefaultRegion(), HotkeyBinding.DefaultExplain(), out _);
            RefreshHotkeyButtons();
            LoadDefaultProviders();
            LoadSourceLanguageSettings();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // A key typed and then immediately closed on would otherwise be lost with the timer.
        _keySaveTimer?.Stop();
        FlushPendingKeys();

        // Only record a real windowed size. While maximized these report the restore bounds on
        // some setups and the maximized bounds on others, and saving the latter meant reopening
        // windowed at full-screen size.
        if (WindowState == WindowState.Normal)
        {
            _settings.Current.MainWindowWidth = Width;
            _settings.Current.MainWindowHeight = Height;
        }

        _settings.Current.MainWindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
        base.OnClosed(e);
    }
}
