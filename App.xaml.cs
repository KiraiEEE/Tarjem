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

        // Building a Windows OCR engine takes a noticeable moment, and the constructor only ever
        // builds the English one. Anyone reading in another language paid for theirs on their very
        // first lookup - the one press where the app has yet to prove it's quick. Off the UI thread
        // and best-effort: a language pack that isn't installed is handled at lookup time anyway.
        var warmUpLanguage = _settingsService.Current.SourceLanguage;
        _ = Task.Run(() =>
        {
            try { _ocrService.WarmUp(warmUpLanguage); }
            catch (Exception ex) { Log.Debug(ex, "OCR warm-up for {Language} failed", warmUpLanguage); }
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

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
        settingsItem.Click += (s, args) => ShowMainWindow(SettingsTabIndex);

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (s, args) => Shutdown();

        contextMenu.Items.Add(historyItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenu = contextMenu;

        // With history off there's no History page to land on, so go straight to Settings.
        _trayIcon.DoubleClickCommand = new DelegateCommand(() =>
            ShowMainWindow(_settingsService!.Current.HistoryEnabled ? HistoryTabIndex : SettingsTabIndex));
    }

    private void ApplyHistoryMenuVisibility()
    {
        if (_historyMenuItem != null)
            _historyMenuItem.Visibility = _settingsService!.Current.HistoryEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWordHotkeyPressed(object? sender, EventArgs e) => _ = RunLookupAsync(LookupMode.Translate);
    private void OnExplainHotkeyPressed(object? sender, EventArgs e) => _ = RunLookupAsync(LookupMode.Explain);

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

        // Tear down a region translation still on screen for the same reason the region hotkey
        // tears down a word popup: the two can't coexist, and a leftover region overlay keeps the
        // screen dimmed. At once rather than faded, and gone from the screen before the capture
        // below - otherwise OCR reads our own overlay instead of the word under the cursor.
        if (_regionTranslationService?.Cancel(immediate: true) == true)
            ScreenCaptureGuard.WaitForCompositor();

        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;

            // Alt+E shows its card immediately in a loading state, because the encyclopedia round
            // trip is the slowest of the three hotkeys and a key that appears to do nothing for a
            // second reads as broken.
            //
            // Alt+Q deliberately does NOT do this. Putting its highlight up before the lookup
            // finished made the wait feel shorter, and cost far more than it bought: every path
            // that ended without a result then had to remember to take the overlay back down
            // again, and the one that didn't left the whole screen dimmed until the next click.
            var word = mode == LookupMode.Explain
                ? await _lookupPipeline.PeekWordAsync(cursor, cts.Token)
                : null;

            ExplainWindow? explain = null;
            if (mode == LookupMode.Explain && word is { Found: true })
            {
                if (IsStale(generation)) return;
                _overlayService.ShowHighlight(word.WordRectPhysical);
                explain = _overlayService.ShowExplainLoading(word.WordRectPhysical, word.Word);
            }

            var outcome = await _lookupPipeline.RunAsync(mode, cursor, cts.Token, word);

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
                return;
            }

            if (mode == LookupMode.Explain && explain != null)
            {
                _overlayService.CompleteExplain(explain, outcome.Result, outcome.WordRectPhysical);
                return;
            }

            // Clear any loading card a previous Alt+E press left behind before showing the word popup.
            _overlayService.HideExplain();
            _overlayService.ShowHighlight(outcome.WordRectPhysical);
            _mainWindow?.RefreshHistory();
            _overlayService.ShowTranslationPopup(outcome.WordRectPhysical, outcome.Result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer lookup, or the overlay was dismissed mid-flight - either way
            // there's nothing stale left to show.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Lookup pipeline failed");
            if (!IsStale(generation))
                _overlayService?.DismissAll();
        }
    }

    private void OnRegionHotkeyPressed(object? sender, EventArgs e)
    {
        if (_regionTranslationService == null || _ocrService == null)
            return;

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
