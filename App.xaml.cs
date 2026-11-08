using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using Serilog;
using Tarjem.Models;
using Tarjem.Services;
using Tarjem.Views;
using Windows.Media.Ocr;

namespace Tarjem;

public partial class App : Application
{
    private HotkeyService? _hotkeyService;
    private OverlayService? _overlayService;
    private RegionTranslationService? _regionTranslationService;
    private OcrService? _ocrService;
    private TranslationService? _translationService;
    private HistoryService? _historyService;
    private SettingsService? _settingsService;
    private LookupPipeline? _lookupPipeline;
    private SelectionTranslationService? _selectionService;
    private AvailableUpdate? _availableUpdate;
    private TaskbarIcon? _trayIcon;
    private System.Windows.Controls.MenuItem? _historyMenuItem;
    private IntPtr _windowHandle;
    private long _generation;
    private CancellationTokenSource? _cts;
    private HwndSource? _hwndSource;
    private MainWindow? _mainWindow;
    private SingleInstanceGuard? _singleInstanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Packaging step, not a user-facing feature: turns the developer's plaintext .env into
        // the obfuscated bundle that actually ships. Handled before anything else initializes so
        // it can run on a machine with no settings, no logging and no single-instance guard.
        if (e.Args.Contains("--pack-keys", StringComparer.OrdinalIgnoreCase))
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var report = BundledKeys.Pack(
                System.IO.Path.Combine(baseDir, ".env"),
                System.IO.Path.Combine(baseDir, "bundled.keys"));
            MessageBox.Show(report, "Tarjem - pack keys");
            Shutdown();
            return;
        }

        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.IsFirstInstance)
        {
            SingleInstanceGuard.NotifyRunningInstance();
            Shutdown();
            return;
        }

        AppPaths.EnsureInitialized();
        ConfigureLogging();
        WireGlobalExceptionHandlers();
        StartupService.SyncPath();

        Log.Information("Tarjem starting (version {Version})", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);

        var parameters = new HwndSourceParameters("Tarjem_HotkeyReceiver")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0x8000000
        };
        _hwndSource = new HwndSource(parameters);
        _windowHandle = _hwndSource.Handle;
        _hwndSource.AddHook(_singleInstanceGuard.WndProcHook);
        _singleInstanceGuard.RequestShowMainWindow += (_, _) => Dispatcher.Invoke(() => ShowMainWindow(0));

        _historyService = new HistoryService();
        _settingsService = new SettingsService();
        ThemeService.Apply(_settingsService.Current.Theme);

        if (!_settingsService.Current.HasCompletedOnboarding)
        {
            var welcome = new WelcomeWindow(_settingsService);
            welcome.ShowDialog();
        }

        _translationService = new TranslationService(_settingsService);
        _overlayService = new OverlayService(_settingsService, _translationService);
        _overlayService.Dismissed += (_, _) => _cts?.Cancel();

        try
        {
            _ocrService = new OcrService();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize OCR engine");
            MessageBox.Show(
                $"Failed to initialize OCR engine.\n\nError: {ex.Message}\n\nWindows has no text-recognition (OCR) language installed. Add one in Windows Settings > Time & language > Language & region.",
                "Tarjem - OCR Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _lookupPipeline = new LookupPipeline(_ocrService, _translationService, _settingsService, _historyService);
        _overlayService.RetranslateWord = async (previous, ct) =>
        {
            var result = await _lookupPipeline.RetranslateAsync(previous, ct);
            _mainWindow?.RefreshHistory();
            return result;
        };

        // Building a Windows OCR engine takes a noticeable moment, and the constructor only ever
        // builds the English one. Anyone reading in another language paid for theirs on their very
        // first lookup - the one press where the app has yet to prove it's quick. Off the UI thread
        // and best-effort: a language pack that isn't installed is handled at lookup time anyway.
        var warmUpLanguage = _settingsService.Current.SourceLanguage;
        _ = Task.Run(() =>
        {
            try { _ocrService.WarmUp(warmUpLanguage); }
            catch (Exception ex) { Log.Debug(ex, "OCR warm-up for {Language} failed", warmUpLanguage); }

            // Same reason: the first popup decides whether to show its speaker button.
            PronunciationService.WarmUp();

            // And the first translation shouldn't pay for a TLS handshake.
            _ = _translationService.WarmUpAsync();
        });

        _regionTranslationService = new RegionTranslationService(
            _ocrService, _translationService, _settingsService);

        // Reuses the hotkey window's message loop for clipboard notifications rather than creating
        // a second hidden window for it.
        _selectionService = new SelectionTranslationService(_translationService, _settingsService, _windowHandle);
        _selectionService.ApplySettings();

        // Our own copy buttons put text on the clipboard; without this the watcher would treat
        // each one as the user asking to translate what they just copied.
        _overlayService.TextCopied += (_, text) => _selectionService?.IgnoreOwnCopy(text);

        // Settings writes are the one signal that any of these preferences changed; re-reading
        // them here keeps theme and the selection watcher in step without Settings needing to
        // know either exists.
        _settingsService.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            ThemeService.Apply(_settingsService.Current.Theme);
            _selectionService?.ApplySettings();
        });

        _hotkeyService = new HotkeyService(_windowHandle);
        _hotkeyService.WordHotkeyPressed += OnWordHotkeyPressed;
        _hotkeyService.RegionHotkeyPressed += OnRegionHotkeyPressed;
        _hotkeyService.ExplainHotkeyPressed += OnExplainHotkeyPressed;

        RegisterHotkeys();

        SetupTrayIcon();

        if (!_settingsService.Current.StartMinimized)
            ShowMainWindow();

        if (_settingsService.Current.CheckForUpdates)
            _ = CheckForUpdatesAsync();
    }

    /// <summary>Fire-and-forget on startup. The result is remembered and handed to the main window
    /// whenever it opens; nothing pops up on its own.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            _availableUpdate = await new UpdateService().CheckAsync();
            if (_availableUpdate != null)
                _mainWindow?.ShowAvailableUpdate(_availableUpdate);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update check failed");
        }
    }

    /// <summary>
    /// Applies the saved hotkeys, falling back to the defaults if they can't be registered -
    /// a combination that was free when it was chosen may be taken by whatever else is running
    /// today, and an app whose only two entry points are hotkeys must not start with neither.
    /// </summary>
    private void RegisterHotkeys()
    {
        var settings = _settingsService!.Current;

        try
        {
            _hotkeyService!.Register(settings.WordHotkey, settings.RegionHotkey, settings.ExplainHotkey);
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saved hotkeys could not be registered; falling back to the defaults");
        }

        var word = HotkeyBinding.DefaultWord();
        var region = HotkeyBinding.DefaultRegion();
        var explain = HotkeyBinding.DefaultExplain();

        if (_hotkeyService!.TryApply(word, region, explain, out var error))
        {
            settings.WordHotkey = word;
            settings.RegionHotkey = region;
            settings.ExplainHotkey = explain;
            _settingsService.Save();

            OfferToChangeHotkeys(
                $"Your saved shortcuts were already taken by another application, so Tarjem went back to " +
                $"{word.Display}, {region.Display} and {explain.Display}.");
            return;
        }

        // Even the defaults are taken. The app has no other way in, so this is worth interrupting
        // for - and worth taking the user straight to where they can fix it.
        Log.Error("Failed to register hotkeys: {Error}", error);
        OfferToChangeHotkeys(
            $"Tarjem couldn't register its shortcuts.\n\n{error}\n\nUntil you pick different ones, " +
            "the hotkeys won't work.");
    }

    /// <summary>
    /// Tells the user a shortcut was unavailable and offers to open Settings so they can choose
    /// another. Previously this was an OK-only box that told them to go to Settings themselves,
    /// which is a worse version of the same conversation.
    /// </summary>
    private void OfferToChangeHotkeys(string message)
    {
        var answer = MessageBox.Show(
            $"{message}\n\nChoose different shortcuts now?",
            "Tarjem - shortcuts",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
            ShowMainWindow(SettingsTabIndex);
    }

    private const int HistoryTabIndex = 0;
    private const int SettingsTabIndex = 1;

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "tarjem-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024)
            .CreateLogger();
    }


    /// <summary>Without these, an exception anywhere outside the OCR/hotkey init try/catches
    /// crashes silently to desktop with no record of why - the app just vanishes from the
    /// tray. Dispatcher exceptions are marked Handled so a single bad frame doesn't take
    /// down the whole tray app; AppDomain-level ones can't be stopped, only logged before
    /// the process actually dies.</summary>
    private void WireGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled AppDomain exception (terminating={IsTerminating})", args.IsTerminating);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void SetupTrayIcon()
    {
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
        Icon icon;

        if (File.Exists(iconPath))
            icon = new Icon(iconPath);
        else
        {
            var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Tarjem.Assets.icon.ico");
            icon = stream != null ? new Icon(stream) : SystemIcons.Application;
        }

        _trayIcon = new TaskbarIcon
        {
            Icon = icon,
            ToolTipText = "Tarjem",
            Visibility = Visibility.Visible
        };

        var contextMenu = new System.Windows.Controls.ContextMenu();

        var historyItem = new System.Windows.Controls.MenuItem { Header = "History" };
        historyItem.Click += (s, args) => ShowMainWindow(HistoryTabIndex);

        // Hidden entirely while history is turned off, and kept in sync if the user flips the
        // setting while the app is running.
        _historyMenuItem = historyItem;
        ApplyHistoryMenuVisibility();
        _settingsService!.Changed += (_, _) => Dispatcher.Invoke(ApplyHistoryMenuVisibility);

        // Practice the saved words. Labelled with the count due when there are any.
        var reviewItem = new System.Windows.Controls.MenuItem { Header = "Review words" };
        reviewItem.Click += (_, _) => OpenReview();
        contextMenu.Opened += (_, _) =>
        {
            var due = _historyService!.DueCount(DateTime.Now);
            reviewItem.Header = due > 0 ? $"Review words ({due} due)" : "Review words";
            reviewItem.Visibility = _settingsService!.Current.HistoryEnabled ? Visibility.Visible : Visibility.Collapsed;
        };

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
        settingsItem.Click += (s, args) => ShowMainWindow(SettingsTabIndex);

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (s, args) => Shutdown();

        // Language quick switch. Rebuilt every time the menu opens, so it always shows what is
        // in force now - a pick in a popup, the overlay or Settings changes it too.
        var fromItem = new System.Windows.Controls.MenuItem { Header = "Translate from" };
        var toItem = new System.Windows.Controls.MenuItem { Header = "Translate to" };
        contextMenu.Opened += (_, _) =>
        {
            try { FillLanguageMenus(fromItem, toItem); }
            catch (Exception ex) { Log.Warning(ex, "Could not build the tray language menus"); }
        };

        contextMenu.Items.Add(fromItem);
        contextMenu.Items.Add(toItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(reviewItem);
        contextMenu.Items.Add(historyItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu = contextMenu;

        // With history off there's no History page to land on, so go straight to Settings.
        _trayIcon.DoubleClickCommand = new DelegateCommand(() =>
            ShowMainWindow(_settingsService!.Current.HistoryEnabled ? HistoryTabIndex : SettingsTabIndex));
    }

    /// <summary>
    /// The tray's "Translate from" / "Translate to" submenus. Short on purpose: the source menu
    /// offers Auto-detect and the languages Windows can actually read here, the target menu the
    /// recent ones, and each has a full list one level down - a tray menu with thirty-six entries
    /// at the top level is a list nobody can find anything in.
    /// </summary>
    private void FillLanguageMenus(System.Windows.Controls.MenuItem fromItem, System.Windows.Controls.MenuItem toItem)
    {
        var settings = _settingsService!.Current;
        var installed = OcrService.InstalledLanguages();

        fromItem.Items.Clear();
        fromItem.Items.Add(LanguageMenuItem("Auto-detect", settings.AutoDetectSourceLanguage,
            new LanguagePick(Tarjem.Core.Language.LanguageCatalog.Auto, null)));
        fromItem.Items.Add(new System.Windows.Controls.Separator());

        foreach (var code in installed)
            fromItem.Items.Add(LanguageMenuItem(Tarjem.Core.Language.LanguageCatalog.NameOf(code),
                !settings.AutoDetectSourceLanguage && settings.SourceLanguage == code, new LanguagePick(code, null)));

        var moreSources = new System.Windows.Controls.MenuItem { Header = installed.Count > 0 ? "Other languages" : "All languages" };
        foreach (var language in Tarjem.Core.Language.LanguageCatalog.All.Where(l => !installed.Contains(l.Code)))
            moreSources.Items.Add(LanguageMenuItem(language.Name,
                !settings.AutoDetectSourceLanguage && settings.SourceLanguage == language.Code, new LanguagePick(language.Code, null)));
        fromItem.Items.Add(moreSources);

        fromItem.Header = $"Translate from: {(settings.AutoDetectSourceLanguage ? "Auto-detect" : Tarjem.Core.Language.LanguageCatalog.NameOf(settings.SourceLanguage))}";

        toItem.Items.Clear();
        foreach (var code in LanguageChoice.RecentTargets(settings))
            toItem.Items.Add(LanguageMenuItem(Tarjem.Core.Language.LanguageCatalog.NameOf(code), settings.TargetLanguage == code, new LanguagePick(null, code)));
        toItem.Items.Add(new System.Windows.Controls.Separator());

        var allTargets = new System.Windows.Controls.MenuItem { Header = "All languages" };
        foreach (var language in Tarjem.Core.Language.LanguageCatalog.All)
            allTargets.Items.Add(LanguageMenuItem(language.Name, settings.TargetLanguage == language.Code, new LanguagePick(null, language.Code)));
        toItem.Items.Add(allTargets);

        toItem.Header = $"Translate to: {Tarjem.Core.Language.LanguageCatalog.NameOf(settings.TargetLanguage)}";
    }

    private System.Windows.Controls.MenuItem LanguageMenuItem(string header, bool isChecked, LanguagePick pick)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => LanguageChoice.Apply(_settingsService!, pick);
        return item;
    }

    private ReviewWindow? _review;

    /// <summary>The review session, opened from the tray. Through the main window when it's open,
    /// so there's only ever one session.</summary>
    private void OpenReview()
    {
        try
        {
            if (_mainWindow is { IsLoaded: true })
            {
                _mainWindow.OpenReview();
                return;
            }

            if (_review is { IsLoaded: true })
            {
                _review.Activate();
                return;
            }

            _review = new ReviewWindow(_historyService!);
            _review.Closed += (_, _) => _review = null;
            _review.Show();
            _review.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open the review window");
        }
    }

    private void ApplyHistoryMenuVisibility()
    {
        if (_historyMenuItem != null)
            _historyMenuItem.Visibility = _settingsService!.Current.HistoryEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWordHotkeyPressed(object? sender, EventArgs e)
    {
        WarnIfExclusiveFullscreen();
        _ = RunLookupAsync(LookupMode.Translate);
    }

    private void OnExplainHotkeyPressed(object? sender, EventArgs e)
    {
        WarnIfExclusiveFullscreen();
        _ = RunLookupAsync(LookupMode.Explain);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>QUNS_RUNNING_D3D_FULL_SCREEN: a Direct3D app owns the display exclusively.</summary>
    private const int ExclusiveFullscreen = 3;

    private bool _warnedAboutFullscreen;

    /// <summary>
    /// A game in exclusive fullscreen owns the display: nothing else can draw over it, and screen
    /// captures of it come back black. Every hotkey then silently did nothing. Windows does say
    /// when this is the case, so say it once per session - in the tray, which is still seen after
    /// alt-tabbing - with the one setting that fixes it.
    /// </summary>
    private void WarnIfExclusiveFullscreen()
    {
        if (_warnedAboutFullscreen) return;
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0 || state != ExclusiveFullscreen) return;

            _warnedAboutFullscreen = true;
            Log.Information("Hotkey pressed over an exclusive-fullscreen game");
            _trayIcon?.ShowBalloonTip("Tarjem can't draw over this game",
                "It's running in exclusive fullscreen. Switch it to Borderless or Windowed fullscreen in its display settings and Tarjem will work over it.",
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not query the fullscreen state");
        }
    }

    /// <summary>
    /// Drives one press of a word hotkey: cancel whatever is in flight, run the pipeline, show the
    /// result. The pipeline itself lives in <see cref="LookupPipeline"/>; what stays here is the
    /// part that is genuinely about being an application - superseding, and putting windows on
    /// screen.
    ///
    /// A press while a popup is already showing looks up whatever is under the cursor at that
    /// moment and replaces the popup, rather than dismissing it; dismissal is clicking anywhere on
    /// the dimmed overlay.
    /// </summary>
    private async Task RunLookupAsync(LookupMode mode)
    {
        if (_lookupPipeline == null || _overlayService == null) return;

        // A new press always supersedes whatever's in flight: bump the generation and cancel the
        // previous lookup's token instead of ignoring the new press, or letting the old one race
        // the new one to the popup. The generation check is the belt, the token is the braces -
        // either alone would do, but a stale response landing between the two checks is exactly
        // the kind of race worth not relying on a single guard for.
        var generation = Interlocked.Increment(ref _generation);
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        // A ceiling on the whole press. Every source has its own budget, but a chain of them plus
        // a slow AI fallback could still leave the Alt+E card spinning; past this the user is told
        // and the screen is given back.
        using var deadline = new CancellationTokenSource(LookupDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, deadline.Token);
        var token = linked.Token;
        var shownEarly = false;

        // Tear down a region translation still on screen for the same reason the region hotkey
        // tears down a word popup: the two can't coexist, and a leftover region overlay keeps the
        // screen dimmed. At once rather than faded, and gone from the screen before the capture
        // below - otherwise OCR reads our own overlay instead of the word under the cursor.
        // A live region stays: it's invisible to the capture below, and closing someone's live
        // subtitles because they looked up a word would make the two features fight.
        if (_regionTranslationService?.IsLive != true && _regionTranslationService?.Cancel(immediate: true) == true)
            ScreenCaptureGuard.WaitForCompositor();

        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            _lastCursor = cursor;

            // Alt+E shows its card immediately in a loading state, because the encyclopedia round
            // trip is the slowest of the three hotkeys and a key that appears to do nothing for a
            // second reads as broken.
            //
            // Alt+Q deliberately does NOT do this. Putting its highlight up before the lookup
            // finished made the wait feel shorter, and cost far more than it bought: every path
            // that ended without a result then had to remember to take the overlay back down
            // again, and the one that didn't left the whole screen dimmed until the next click.
            var word = mode == LookupMode.Explain
                ? await _lookupPipeline.PeekWordAsync(cursor, token)
                : null;

            ExplainWindow? explain = null;
            if (mode == LookupMode.Explain && word is { Found: true })
            {
                if (IsStale(generation)) return;
                _overlayService.ShowHighlight(word.WordRectPhysical);
                explain = _overlayService.ShowExplainLoading(word.WordRectPhysical, word.Word, word.LanguageTag);
            }

            // The popup goes up as soon as the translation is in; the definition follows into it.
            Action<LookupOutcome>? onEarly = mode == LookupMode.Translate
                ? early => Dispatcher.Invoke(() =>
                {
                    if (IsStale(generation) || early.Result == null || token.IsCancellationRequested) return;
                    _overlayService.HideExplain();
                    _overlayService.ShowHighlight(early.WordRectPhysical);
                    _overlayService.ShowTranslationPopup(early.WordRectPhysical, early.Result);
                    shownEarly = true;
                })
                : null;

            var outcome = await _lookupPipeline.RunAsync(mode, cursor, token, word, onEarly);

            // A newer press has superseded this one, so it now owns the shared overlay. Bow out
            // without touching it: tearing anything down here would pull the highlight or loading
            // card out from under the press that replaced us. That press is responsible for its
            // own cleanup.
            if (IsStale(generation))
                return;

            // No answer to show - nothing under the cursor, or the lookup came back empty. Anything
            // an earlier stage put on screen (the Alt+E loading card, the highlight, and above all
            // the full-screen dim) has to come down now. Only calling HideExplain here is what left
            // the whole screen greyed out until the next click.
            if (outcome?.Result == null)
            {
                _overlayService.DismissAll();
                ToastWindow.Show(mode == LookupMode.Explain && word is { Found: true }
                    ? "Nothing came back for that word."
                    : "No text found under the cursor.", cursor);
                return;
            }

            if (mode == LookupMode.Explain && explain != null)
            {
                _overlayService.CompleteExplain(explain, outcome.Result, outcome.WordRectPhysical);
                return;
            }

            _mainWindow?.RefreshHistory();

            if (!shownEarly || !_overlayService.UpdateTranslationPopup(outcome.WordRectPhysical, outcome.Result))
            {
                // Clear any loading card a previous Alt+E press left behind before showing the word popup.
                _overlayService.HideExplain();
                _overlayService.ShowHighlight(outcome.WordRectPhysical);
                _overlayService.ShowTranslationPopup(outcome.WordRectPhysical, outcome.Result);
            }

            _ = RefineAsync(generation, cts, outcome);
        }
        catch (OfflineException)
        {
            if (!IsStale(generation))
            {
                _overlayService.DismissAll();
                ToastWindow.Show("You're offline. Tarjem needs a connection to translate.", _lastCursor);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer lookup, or the overlay was dismissed mid-flight - either way
            // there's nothing stale left to show. Unless it was the deadline: then say so, rather
            // than leave the user wondering whether the press registered.
            if (deadline.IsCancellationRequested && !cts.IsCancellationRequested && !IsStale(generation))
            {
                Log.Warning("{Mode} gave up after {Seconds} s", mode, LookupDeadline.TotalSeconds);
                _overlayService.DismissAll();
                ToastWindow.Show("That took too long - the translation services may be busy. Try again in a moment.", _lastCursor);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Lookup pipeline failed");
            if (!IsStale(generation))
                _overlayService?.DismissAll();
        }
    }

    private static readonly TimeSpan LookupDeadline = TimeSpan.FromSeconds(20);
    private System.Drawing.Point _lastCursor;

    /// <summary>Swaps the AI's natural rewrite of the sentence into the popup once it arrives, as
    /// long as that popup is still the one on screen.</summary>
    private async Task RefineAsync(long generation, CancellationTokenSource cts, LookupOutcome outcome)
    {
        if (_lookupPipeline == null || _overlayService == null || outcome.Result == null) return;

        try
        {
            var refined = await _lookupPipeline.RefineAsync(outcome.Result, cts.Token);
            if (refined == null || IsStale(generation) || cts.IsCancellationRequested) return;

            _overlayService.UpdateTranslationPopup(outcome.WordRectPhysical, refined);
            _mainWindow?.RefreshHistory();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Refining a translation failed");
        }
    }

    private void OnRegionHotkeyPressed(object? sender, EventArgs e)
    {
        if (_regionTranslationService == null || _ocrService == null)
            return;

        WarnIfExclusiveFullscreen();

        // A word lookup and a region translation can't both be on screen at once. Without this,
        // an Alt+Q popup (and its full-screen dim) stayed up behind the region flow and was still
        // there - greying the whole screen - after the region overlay was dismissed.
        Interlocked.Increment(ref _generation);
        _cts?.Cancel();

        // Immediately, not faded: the region flow's first act is to freeze a screenshot, and a
        // fading dim or popup caught in it stays frozen over the whole screen.
        if (_overlayService?.DismissAll(immediate: true) == true)
            ScreenCaptureGuard.WaitForCompositor();

        _regionTranslationService.StartRegionTranslation();
    }

    private bool IsStale(long generation) => Interlocked.Read(ref _generation) != generation;

    /// <summary>
    /// Opens (or focuses) the main window. Everything here runs inside a try/catch because one
    /// of the two ways this is reached - the tray icon's double-click command - is dispatched
    /// from a native window-proc callback, where an escaping exception terminates the process
    /// outright instead of being caught by <see cref="WireGlobalExceptionHandlers"/>. That is
    /// exactly what a single invalid value in MainWindow.xaml did: the tray menu items silently
    /// did nothing (their exception was dispatcher-handled) while double-clicking the icon
    /// killed the app. A broken window must never be able to take the tray app down with it.
    /// </summary>
    private void ShowMainWindow(int tabIndex = 0)
    {
        try
        {
            if (_mainWindow == null || !_mainWindow.IsLoaded)
            {
                _mainWindow = new MainWindow(_historyService!, _settingsService!, _translationService!, _hotkeyService);
                _mainWindow.Show();

                // The check may have finished before the window ever existed.
                if (_availableUpdate != null)
                    _mainWindow.ShowAvailableUpdate(_availableUpdate);
            }
            else
            {
                _mainWindow.RefreshHistory();
                if (_mainWindow.WindowState == WindowState.Minimized)
                    _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }

            _mainWindow.SelectTab(tabIndex);
        }
        catch (Exception ex)
        {
            // Don't keep a half-constructed window around - the next attempt should start clean.
            _mainWindow = null;
            Log.Error(ex, "Failed to open the main window");
            MessageBox.Show(
                $"Couldn't open the Tarjem window.\n\n{ex.Message}\n\nTranslation hotkeys are still working. Details are in the log:\n{AppPaths.LogsDirectory}",
                "Tarjem",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Tarjem exiting");
        _cts?.Cancel();
        _cts?.Dispose();
        _hotkeyService?.Dispose();
        _regionTranslationService?.Dispose();
        _selectionService?.Dispose();
        _historyService?.Flush(TimeSpan.FromSeconds(3));
        _trayIcon?.Dispose();
        _hwndSource?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
        Log.CloseAndFlush();
    }
}

public class DelegateCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
