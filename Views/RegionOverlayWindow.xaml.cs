using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tarjem.Core.Language;
using Tarjem.Models;
using Tarjem.Services;

using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace Tarjem.Views;

/// <summary>One block of text to redraw in place.</summary>
/// <param name="BoundsPhysical">The original text's box, in physical pixels relative to the
/// region's top-left corner.</param>
/// <param name="FontSizePhysical">The size the original was drawn at, in physical pixels.</param>
/// <param name="RowCount">How many rows it spanned on screen.</param>
/// <remarks>No weight: measured stroke widths of regular and semibold UI text overlap by less
/// than a pixel at the sizes that matter, so a guess would be wrong often enough to look worse
/// than consistently using the regular weight.</remarks>
public sealed record RegionBlock(
    Rect BoundsPhysical, string Text, Color Background, Color Foreground, double FontSizePhysical, int RowCount);

/// <summary>
/// The Alt+W result overlay. Covers the whole monitor the selected region sits on - the dim
/// layer is what makes "click anywhere to dismiss" work, exactly like the Alt+Q
/// <see cref="OverlayWindow"/>; a window sized to just the region would let every click outside
/// it fall through to the app underneath and leave the overlay stuck on screen.
/// </summary>
public partial class RegionOverlayWindow : Window
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;
    private const int FadeMs = 160;
    private const double DimOpacity = 0.10;

    /// <summary>Never render translated text smaller than this - matching a tiny source font
    /// exactly is pointless if the result can't be read. Lower than it once was, because the
    /// overlay now stays inside the region the user dragged: a couple of points of legibility is a
    /// better trade than text spilling out of the box it is supposed to be covering.</summary>
    private const double MinFontSize = 8;
    private const double MaxFontSize = 40;

    /// <summary>Cap used when the source font size couldn't be estimated and we fall back to
    /// "as large as fits". Without it, a short translation in a big region got blown up to
    /// poster size - the reason the overlay text looked huge.</summary>
    private const double FallbackMaxFontSize = 18;

    /// <summary>How much of the original's measured size the translation starts at. Under one
    /// because the translation is nearly always the longer text.</summary>
    private const double SourceSizeAllowance = 0.88;

    /// <summary>Padding between the plate's edges and the text inside it.</summary>
    private const double PlatePadX = 10;
    private const double PlatePadY = 6;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public event EventHandler? Dismissed;
    public event EventHandler<string>? ApiChanged;

    /// <summary>The user picked a language from the overlay's own picker.</summary>
    public event EventHandler<LanguagePick>? LanguagePicked;

    /// <summary>The user asked to keep translating this region as it changes.</summary>
    public event EventHandler? LiveRequested;

    /// <summary>Live mode reads the region again while this window is over it, which only works
    /// when the window is invisible to screen capture (Windows 10 2004 and later).</summary>
    public bool CanGoLive { get; private set; }

    /// <summary>Whether the live chip is offered here at all (not for copied text).</summary>
    public bool OffersLive { get; set; }

    public bool IsLive => _live;
    private bool _live;

    /// <summary>Fires with the text the copy button just put on the clipboard, so a clipboard
    /// watcher can tell our own copy apart from the user copying something new.</summary>
    public event EventHandler<string>? TextCopied;

    /// <summary>The OCR'd text currently on screen - what a provider switch re-translates.</summary>
    public string CurrentOriginalText { get; private set; } = "";

    /// <summary>The whole translation, whichever way it is laid out - what the copy button copies.</summary>
    public string CurrentTranslatedText { get; private set; } = "";

    /// <summary>True while the translation is drawn block by block over the original text.</summary>
    public bool IsInPlace => _inPlace;

    /// <summary>The blocks currently drawn, for tests. Each is a patch plus its text.</summary>
    public int BlockCount => BlocksLayer.Children.Count / 2;

    private bool _inPlace;
    private bool _showingOriginalOnly;
    private bool _languagePanelOpen;

    /// <summary>Region rect in this window's own DIP coordinates (window origin = monitor origin).</summary>
    private Rect _regionDip;

    /// <summary>The dark plate actually drawn. Starts as the selected region and only grows
    /// when the translation genuinely can't be shown inside it at a readable size.</summary>
    private Rect _plateDip;

    private Rect _chipRect;
    private Rect _chipRowRect;
    private double _dpiScale = 1.0;

    /// <summary>Estimated font size (DIPs) of the text that was on screen in the region, so the
    /// translation can be drawn at the size it replaces instead of filling the box.</summary>
    private double _sourceFontSizeDip;

    private bool _dropdownOpen;
    private bool _isDismissing;
    private bool _isRtl;

    /// <summary>Direction of the *source* text, which is independent of the target language:
    /// showing an English original inside a right-to-left block puts its full stop on the wrong
    /// end of the line.</summary>
    private bool _sourceIsRtl;

    private int _ocrLineCount = 1;
    private string _currentApiId = "google";

    // User options - see ApplyOptions.
    private bool _dimBehind = true;
    private bool _showOriginal;
    private bool _matchSourceFontSize = true;

    public RegionOverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // Invisible to screen capture, so the region underneath can be read again while the
        // translation is showing - which is what live mode does, and what keeps an Alt+Q pressed
        // over the overlay from reading the translation instead of the original.
        CanGoLive = ScreenCaptureGuard.ExcludeFromCapture(hwnd);
    }

    // ── Dismissal ──

    /// <summary>Any click on the overlay dismisses it, except clicks on the provider chip or
    /// its open list - those belong to the dropdown, and the first click outside an open
    /// dropdown only closes the dropdown rather than tearing down the whole overlay.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_live) return;

        var src = e.OriginalSource as DependencyObject;
        if (IsDescendantOf(src, ApiButton) || IsDescendantOf(src, ApiDropdown) || IsDescendantOf(src, CopyButton) ||
            IsDescendantOf(src, LanguageButton) || IsDescendantOf(src, OriginalButton) || IsDescendantOf(src, LanguagePanelHost))
            return;

        if (_dropdownOpen || _languagePanelOpen)
        {
            CloseDropdown();
            CloseLanguagePanel();
            e.Handled = true;
            return;
        }

        Dismiss();
    }

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject parent)
    {
        while (child is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            if (child == parent) return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    public void Dismiss()
    {
        if (_isDismissing) return;
        _isDismissing = true;

        CloseDropdown(animate: false);
        CloseLanguagePanel();

        var dur = TimeSpan.FromMilliseconds(FadeMs);
        FadeTo(HighlightBorder, 0, dur);
        FadeTo(TextContainer, 0, dur);
        FadeTo(BlocksLayer, 0, dur);
        FadeTo(LoadingPanel, 0, dur);
        FadeTo(ApiButton, 0, dur);
        FadeTo(CopyButton, 0, dur);
        FadeTo(LanguageButton, 0, dur);
        FadeTo(OriginalButton, 0, dur);
        FadeTo(LiveButton, 0, dur);

        var dim = new DoubleAnimation(DimOverlay.Opacity, 0, dur) { EasingFunction = EaseOut };
        dim.Completed += (_, _) =>
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
            Close();
        };
        DimOverlay.BeginAnimation(OpacityProperty, dim);
    }

    private static void FadeTo(UIElement element, double to, TimeSpan duration) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(element.Opacity, to, duration) { EasingFunction = EaseOut });

    // ── Placement ──

    /// <summary>
    /// Stretches the window across the monitor containing <paramref name="screenRegionPhysical"/>
    /// (physical pixels) and remembers where the region lands inside it. DPI comes from the
    /// monitor itself rather than <see cref="PresentationSource"/>, which isn't available yet
    /// when this is called before <see cref="Window.Show"/>.
    /// </summary>
    public void PositionOverRegion(Rect screenRegionPhysical)
    {
        var centerX = (int)(screenRegionPhysical.Left + screenRegionPhysical.Width / 2);
        var centerY = (int)(screenRegionPhysical.Top + screenRegionPhysical.Height / 2);
        _dpiScale = DpiHelper.GetScaleForPoint(centerX, centerY);
        if (_dpiScale <= 0) _dpiScale = 1.0;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(centerX, centerY));
        var bounds = screen.Bounds;

        Left = bounds.Left / _dpiScale;
        Top = bounds.Top / _dpiScale;
        Width = bounds.Width / _dpiScale;
        Height = bounds.Height / _dpiScale;

        _regionDip = new Rect(
            (screenRegionPhysical.Left - bounds.Left) / _dpiScale,
            (screenRegionPhysical.Top - bounds.Top) / _dpiScale,
            screenRegionPhysical.Width / _dpiScale,
            screenRegionPhysical.Height / _dpiScale);

        ApplyPlate(_regionDip);
        PlaceAt(LoadingPanel, _regionDip);
    }

    /// <summary>The rectangle the translation is currently drawn in, in DIPs. Exposed so a test
    /// can assert the thing that is easy to regress and impossible to see otherwise: that it never
    /// grows past the region the user dragged.</summary>
    public Rect PlateBounds => _plateDip;

    /// <summary>Positions the plate, the text inside it and the chip above it.</summary>
    private void ApplyPlate(Rect plate)
    {
        _plateDip = plate;

        PlaceAt(HighlightBorder, plate);

        TextContainer.Margin = new Thickness(plate.Left + PlatePadX, plate.Top + PlatePadY, 0, 0);
        TextContainer.Width = Math.Max(20, plate.Width - PlatePadX * 2);
        TextContainer.MaxHeight = Math.Max(14, plate.Height - PlatePadY * 2);

        PositionApiChip();
    }

    private static void PlaceAt(FrameworkElement element, Rect rect)
    {
        element.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
        element.Width = Math.Max(1, rect.Width);
        element.Height = Math.Max(1, rect.Height);
    }

    /// <summary>Pins the chip just above the region's top-right corner, flipping below the
    /// region when there isn't room above, and clamping to the monitor either way.</summary>
    private void PositionApiChip()
    {
        var size = MeasureNatural(ApiButton);
        if (size.Width <= 0 || size.Height <= 0) return;

        var copySize = MeasureNatural(CopyButton);

        // The chips that are showing share a row above the plate's top-right corner: show
        // original, languages, provider, copy - in that order, so the provider list still hangs
        // off its own chip.
        var row = new List<(FrameworkElement Chip, Size Size)>();
        foreach (var chip in new FrameworkElement[] { LiveButton, OriginalButton, LanguageButton, ApiButton, CopyButton })
        {
            if (chip.Visibility != Visibility.Visible && chip != ApiButton) continue;
            row.Add((chip, chip == ApiButton ? size : chip == CopyButton ? copySize : MeasureNatural(chip)));
        }

        const double Gap = 4;
        var rowWidth = row.Sum(c => c.Size.Width) + Gap * Math.Max(0, row.Count - 1);
        var left = _plateDip.Right - rowWidth;
        var top = _plateDip.Top - size.Height - 8;
        if (top < 4)
            top = _plateDip.Bottom + 8;

        left = Clamp(left, 4, Math.Max(4, Width - rowWidth - 4));
        top = Clamp(top, 4, Math.Max(4, Height - size.Height - 4));

        _chipRowRect = new Rect(left, top, rowWidth, size.Height);

        var x = left;
        foreach (var (chip, chipSize) in row)
        {
            chip.Margin = new Thickness(x, top, 0, 0);
            if (chip == ApiButton)
                _chipRect = new Rect(x, top, chipSize.Width, chipSize.Height);
            x += chipSize.Width + Gap;
        }

        if (_dropdownOpen)
            PositionDropdown();
        if (_languagePanelOpen)
            PositionLanguagePanel();
    }

    /// <summary>Copies the translated text. The overlay covers the text it replaces, so without
    /// this the translation is the one thing on screen a user cannot get out of the app.</summary>
    private void CopyButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var text = CurrentTranslatedText;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            // Announced before the write, because the clipboard notification arrives synchronously
            // and would otherwise be treated as the user copying something new to translate.
            TextCopied?.Invoke(this, text);
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // Another application can hold the clipboard open; nothing here is worth interrupting
            // the user over.
            Serilog.Log.Debug(ex, "Could not copy the translation");
            return;
        }

        ShowCopiedConfirmation();
    }

    /// <summary>Swaps the copy glyph for a tick briefly - a click with no visible effect reads as
    /// a broken button.</summary>
    private void ShowCopiedConfirmation()
    {
        CopyIcon.Visibility = Visibility.Collapsed;
        CopiedTick.Visibility = Visibility.Visible;

        var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            CopyIcon.Visibility = Visibility.Visible;
            CopiedTick.Visibility = Visibility.Collapsed;
        };
        revert.Start();
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>Content size of an element, excluding its margin. Both the chip and the
    /// dropdown are positioned *by* their margin, and DesiredSize includes it - so measuring
    /// without clearing it first fed each call's own offset back in and walked the element off
    /// into the corner of the screen.</summary>
    private static Size MeasureNatural(FrameworkElement element)
    {
        element.Margin = new Thickness(0);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    // ── States ──

    /// <summary>User preferences that change how the overlay presents itself. Applied before
    /// the window is shown.</summary>
    public void ApplyOptions(bool dimBehind, bool showOriginal, bool matchSourceFontSize)
    {
        _dimBehind = dimBehind;
        _showOriginal = showOriginal;
        _matchSourceFontSize = matchSourceFontSize;
    }

    public void ShowLoading()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        HighlightBorder.Visibility = Visibility.Collapsed;
        TextContainer.Visibility = Visibility.Collapsed;
        BlocksLayer.Visibility = Visibility.Collapsed;
        ApiButton.Visibility = Visibility.Collapsed;

        var dur = TimeSpan.FromMilliseconds(FadeMs);
        // From wherever the dim already is: a language change re-enters loading with the overlay
        // up, and restarting the dim from zero flashed the whole screen.
        DimOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(DimOverlay.Opacity, _dimBehind ? DimOpacity : 0, dur) { EasingFunction = EaseOut });
        LoadingPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut });
    }

    /// <summary>Fills the provider chip's dropdown. Called once before the first
    /// <see cref="SetTranslation"/> so the list is never the empty box it used to be.</summary>
    public void SetProviders(IReadOnlyList<(string Id, string Label)> providers, string currentApiId)
    {
        ApiDropdownItems.Children.Clear();

        var current = providers.Any(p => p.Id == currentApiId) || providers.Count == 0
            ? currentApiId
            : providers[0].Id;
        _currentApiId = current;

        foreach (var (id, label) in providers)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x9E, 0xF5)),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = id == current ? Visibility.Visible : Visibility.Hidden
            };
            Grid.SetColumn(check, 0);

            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);

            grid.Children.Add(check);
            grid.Children.Add(text);

            var button = new Button
            {
                Style = (Style)FindResource("ApiItemStyle"),
                Tag = id,
                Content = grid
            };
            button.Click += ApiItem_Click;
            ApiDropdownItems.Children.Add(button);
        }

        ApiButton.ToolTip = $"Translated by {LabelFor(current)}";
        PositionApiChip();
    }

    /// <summary>
    /// Shows the finished translation. Sizes and lays the text out against the region as it
    /// actually appears on screen (DIPs), and honours <paramref name="isRtl"/> for both the
    /// flow direction and the alignment - direction alone leaves the text hugging the wrong
    /// edge of the box.
    /// </summary>
    /// <param name="sourceFontSizePhysical">Estimated font size of the text that was in the
    /// region, in physical pixels (0 when unknown). Drives the translation's own size so it
    /// reads like a replacement for the original rather than a blown-up caption.</param>
    public void SetTranslation(string originalText, string translatedText, bool isRtl = false,
        int ocrLineCount = 1, double sourceFontSizePhysical = 0, bool sourceIsRtl = false)
    {
        CurrentOriginalText = originalText;
        CurrentTranslatedText = translatedText;
        _isRtl = isRtl;
        _sourceIsRtl = sourceIsRtl;
        _ocrLineCount = Math.Max(1, ocrLineCount);
        _sourceFontSizeDip = sourceFontSizePhysical > 0 ? sourceFontSizePhysical / _dpiScale : 0;

        _inPlace = false;
        BlocksLayer.Children.Clear();
        BlocksLayer.Visibility = Visibility.Collapsed;
        ApplyDimCutout(false);
        ResetOriginalToggle();

        LoadingPanel.Visibility = Visibility.Collapsed;
        HighlightBorder.Visibility = Visibility.Visible;
        TextContainer.Visibility = Visibility.Visible;
        ApiButton.Visibility = ApiDropdownItems.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = string.IsNullOrWhiteSpace(translatedText) ? Visibility.Collapsed : Visibility.Visible;

        // ApplyText places the plate, which repositions the chips against it.
        ApplyText(translatedText);

        var dur = TimeSpan.FromMilliseconds(FadeMs);
        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut };
        HighlightBorder.BeginAnimation(OpacityProperty, fadeIn);
        TextContainer.BeginAnimation(OpacityProperty, fadeIn);
        FadeInChips(dur);
    }

    private void FadeInChips(TimeSpan dur)
    {
        if (_live)
        {
            HideChips();
            return;
        }

        LiveButton.Visibility = OffersLive && CanGoLive && !string.IsNullOrWhiteSpace(CurrentOriginalText)
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var chip in new UIElement[] { ApiButton, CopyButton, LanguageButton, OriginalButton, LiveButton })
        {
            if (chip.Opacity < 1)
                chip.BeginAnimation(OpacityProperty, new DoubleAnimation(chip.Opacity, 1, dur) { EasingFunction = EaseOut });
        }
    }

    // ── In place ──

    /// <summary>
    /// Draws the translation block by block over the text it replaces: each block at its own
    /// position, on its own background colour, in its own text colour and at its own size. A
    /// heading stays big, a blue button stays blue, and the parts of the region that had no text
    /// stay exactly as they were - instead of the whole selection turning into one dark card.
    /// </summary>
    public void SetBlocks(IReadOnlyList<RegionBlock> blocks, string originalText, string translatedText, bool isRtl)
    {
        CurrentOriginalText = originalText;
        CurrentTranslatedText = translatedText;
        _isRtl = isRtl;
        _inPlace = true;

        LoadingPanel.Visibility = Visibility.Collapsed;
        HighlightBorder.Visibility = Visibility.Collapsed;
        TextContainer.Visibility = Visibility.Collapsed;

        BlocksLayer.Children.Clear();

        // All the patches first, then all the text: a patch that grew to fit a longer translation
        // must never cover the text of the block below it.
        var built = blocks.Select(b => BuildBlock(b, isRtl)).ToList();
        foreach (var (patch, _) in built) BlocksLayer.Children.Add(patch);
        foreach (var (_, text) in built) BlocksLayer.Children.Add(text);

        BlocksLayer.Visibility = Visibility.Visible;
        ApplyDimCutout(true);

        ApiButton.Visibility = ApiDropdownItems.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = string.IsNullOrWhiteSpace(translatedText) ? Visibility.Collapsed : Visibility.Visible;
        OriginalButton.Visibility = Visibility.Visible;

        _plateDip = _regionDip;
        PositionApiChip();

        var dur = TimeSpan.FromMilliseconds(FadeMs);

        // Live updates swap in place: fading every new line up from nothing made dialogue flicker.
        var target = _showingOriginalOnly ? 0 : 1;
        BlocksLayer.BeginAnimation(OpacityProperty, _live
            ? new DoubleAnimation(target, TimeSpan.FromMilliseconds(90))
            : new DoubleAnimation(0, target, dur) { EasingFunction = EaseOut });
        FadeInChips(dur);
    }

    /// <summary>How far above the baseline the tallest letters reach, as a share of the font
    /// size - where the ink starts. The original's box starts there too, so lining the two up is
    /// what puts the translation on the original's line rather than a few pixels under it.</summary>
    private const double AscenderEm = 0.74;

    /// <summary>Up to this size, block text is snapped to the pixel grid for crispness.</summary>
    private const double DisplayModeMaxSize = 15;

    /// <summary>
    /// One block: a patch in the original's background colour over where its text was, and the
    /// translation on top in the original's text colour and size, starting at the
    /// original's first line.
    /// </summary>
    private (Border Patch, TextBlock Text) BuildBlock(RegionBlock block, bool isRtl)
    {
        var sizeDip = Math.Max(4, block.FontSizePhysical / _dpiScale);

        // Anti-aliasing spills a pixel or two past the ink box, and accents and descenders past
        // the row the box was measured on, so the patch reaches a little beyond the text it hides.
        var pad = Math.Max(1.5, sizeDip * 0.16);
        var inkLeft = _regionDip.X + block.BoundsPhysical.X / _dpiScale;
        var inkTop = _regionDip.Y + block.BoundsPhysical.Y / _dpiScale;
        var inkWidth = Math.Max(4, block.BoundsPhysical.Width / _dpiScale);
        var inkHeight = Math.Max(4, block.BoundsPhysical.Height / _dpiScale);

        var font = UiFonts.For(isRtl);
        var weight = FontWeights.Normal;
        var flow = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var size = Clamp(UiFonts.SizeFor(sizeDip, isRtl), 7, 96);
        var minimum = Math.Max(7, size * 0.62);

        // Room to the right of the block, inside the region - a one-line label whose translation
        // is longer than it ("Start" -> "Commencer") grows into it rather than wrapping or shrinking.
        var roomRight = Math.Max(inkWidth, _regionDip.Right - inkLeft - pad);
        var width = inkWidth;

        if (block.RowCount <= 1)
        {
            var natural = Measure(block.Text, font, flow, size, double.PositiveInfinity, weight).Width + 2;
            if (natural > width)
                width = Math.Min(roomRight, natural);
        }

        // Shrink to fit the original's height, allowing a little slack, but not past legibility.
        var lineSpacing = font.LineSpacing > 0 ? font.LineSpacing : 1.33;
        var allowedHeight = Math.Max(inkHeight * 1.2, size * lineSpacing * 1.05);
        var measured = Measure(block.Text, font, flow, size, width, weight);
        if (measured.Height > allowedHeight)
        {
            var lo = minimum;
            var hi = size;
            for (var i = 0; i < 10 && hi - lo > 0.25; i++)
            {
                var mid = (lo + hi) / 2;
                if (Measure(block.Text, font, flow, mid, width, weight).Height <= Math.Max(inkHeight * 1.2, mid * lineSpacing * 1.05)) lo = mid;
                else hi = mid;
            }
            size = Math.Round(lo, 1);
            measured = Measure(block.Text, font, flow, size, width, weight);
        }

        // The text's line box starts above its ink by the font's internal leading; shift it up by
        // that much so the first line's ink lands where the original's did.
        var baseline = font.Baseline > 0 ? font.Baseline : 0.92;
        var textTop = inkTop - (baseline - AscenderEm) * size;

        // Still too tall at the smallest readable size: let it run downwards within the region,
        // and only then cut it off with an ellipsis.
        var maxHeight = Math.Max(inkHeight, _regionDip.Bottom - textTop);
        var trimmed = measured.Height > maxHeight + 0.5;
        var textHeight = Math.Min(measured.Height, maxHeight);

        var text = new TextBlock
        {
            Text = block.Text,
            FontFamily = font,
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(block.Foreground),
            FlowDirection = flow,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = trimmed ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            Width = width,
            MaxHeight = textHeight + 1,
        };

        // Small text is crisper snapped to the pixel grid; large text looks better as WPF's ideal
        // layout draws it.
        if (size <= DisplayModeMaxSize)
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);

        Canvas.SetLeft(text, inkLeft);
        Canvas.SetTop(text, textTop);

        // The patch covers the original's ink and the new text, whichever reaches further.
        var top = Math.Min(inkTop, textTop + (baseline - AscenderEm) * size) - pad;
        var bottom = Math.Max(inkTop + inkHeight, textTop + textHeight - (lineSpacing - baseline) * size * 0.4) + pad;
        var right = Math.Max(inkLeft + inkWidth, inkLeft + Math.Min(width, measured.Width)) + pad;

        var patch = new Border
        {
            Background = new SolidColorBrush(block.Background),
            CornerRadius = new CornerRadius(Math.Min(3, pad)),
            Width = right - (inkLeft - pad),
            Height = Math.Min(bottom, _regionDip.Bottom + pad) - top,
            SnapsToDevicePixels = true,
        };

        Canvas.SetLeft(patch, inkLeft - pad);
        Canvas.SetTop(patch, top);
        return (patch, text);
    }

    private static Size Measure(string text, FontFamily fontFamily, FlowDirection flow, double fontSize, double maxWidth,
        FontWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return new Size(0, 0);

        // Measured the way it will be drawn: small text is drawn snapped to whole pixels (see
        // BuildBlock), which comes out slightly wider than WPF's ideal layout. Measured in the
        // ideal mode, a one-line label sized to fit exactly wrapped its last word onto a line that
        // was then cut off.
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            flow,
            new Typeface(fontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            Brushes.White,
            null,
            fontSize <= DisplayModeMaxSize ? TextFormattingMode.Display : TextFormattingMode.Ideal,
            1.0);

        if (!double.IsInfinity(maxWidth))
            formatted.MaxTextWidth = Math.Max(1, maxWidth);

        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>In place, the region itself is not dimmed: it should look like the screen with
    /// the words swapped, not like the screen under a tint with some patches on it.</summary>
    private void ApplyDimCutout(bool cut)
    {
        if (!cut || Width <= 0 || Height <= 0)
        {
            DimOverlay.Clip = null;
            return;
        }

        DimOverlay.Clip = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Width, Height)),
            new RectangleGeometry(_regionDip));
    }

    // ── Show original ──

    private void OriginalButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleOriginal();
    }

    /// <summary>Hides the translation to show the original underneath, or brings it back.</summary>
    public bool ToggleOriginal()
    {
        _showingOriginalOnly = !_showingOriginalOnly;

        var to = _showingOriginalOnly ? 0 : 1;
        var dur = TimeSpan.FromMilliseconds(120);
        foreach (var layer in _inPlace ? new UIElement[] { BlocksLayer } : new UIElement[] { HighlightBorder, TextContainer })
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(layer.Opacity, to, dur) { EasingFunction = EaseOut });

        OriginalButton.ToolTip = _showingOriginalOnly ? "Show the translation" : "Show the original";
        OriginalIcon.Stroke = new SolidColorBrush(_showingOriginalOnly ? Color.FromRgb(0x5B, 0x9E, 0xF5) : Color.FromRgb(0xDD, 0xDD, 0xDD));
        return _showingOriginalOnly;
    }

    // ── Live ──

    private void LiveButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        LiveRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Turns this overlay into a live one: click-through, so the game underneath keeps getting
    /// every click; no dim and no chips (their controls move to the live bar); the translation
    /// still drawn over the text. Nothing here reads the screen - the service does that.
    /// </summary>
    public void EnterLive()
    {
        if (_live) return;
        _live = true;

        CloseDropdown(animate: false);
        CloseLanguagePanel();
        HideChips();

        DimOverlay.BeginAnimation(OpacityProperty, null);
        DimOverlay.Opacity = 0;
        DimOverlay.Visibility = Visibility.Collapsed;

        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
    }

    /// <summary>Clears the translation while live, when the watched area has no text in it.</summary>
    public void ClearLive()
    {
        BlocksLayer.Children.Clear();
        HighlightBorder.Visibility = Visibility.Collapsed;
        TextContainer.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    private void HideChips()
    {
        foreach (var chip in new UIElement[] { ApiButton, CopyButton, LanguageButton, OriginalButton, LiveButton })
        {
            chip.BeginAnimation(OpacityProperty, null);
            chip.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetOriginalToggle()
    {
        _showingOriginalOnly = false;
        OriginalButton.ToolTip = "Show the original";
        OriginalIcon.Stroke = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
    }

    /// <summary>Whether the show-original chip is offered at all. The copied-text overlay has no
    /// original on screen to reveal, so it leaves this off.</summary>
    public bool OffersShowOriginal
    {
        set => OriginalButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Languages ──

    private string _source = "en";
    private bool _sourceIsAuto;
    private string _target = "ar";
    private IReadOnlyList<string> _installedSources = Array.Empty<string>();
    private IReadOnlyList<string> _recentTargets = Array.Empty<string>();

    /// <summary>Shows the language chip, labelled with the pair in force. <paramref name="source"/>
    /// is the language actually read - the detected one, when detecting.</summary>
    public void SetLanguages(string source, bool sourceIsAuto, string target,
        IReadOnlyList<string> installedSources, IReadOnlyList<string> recentTargets)
    {
        _source = source;
        _sourceIsAuto = sourceIsAuto;
        _target = target;
        _installedSources = installedSources;
        _recentTargets = recentTargets;

        LanguageChipText.Text = $"{LanguageCatalog.ShortLabel(source)}  \u2192  {LanguageCatalog.ShortLabel(target)}";
        LanguageButton.ToolTip =
            $"{LanguageCatalog.NameOf(source)}{(sourceIsAuto ? " (detected)" : "")} to {LanguageCatalog.NameOf(target)}. Click to change.";
        LanguageButton.Visibility = Visibility.Visible;
        PositionApiChip();
    }

    private void LanguageButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_languagePanelOpen)
        {
            CloseLanguagePanel();
            return;
        }

        CloseDropdown();

        var panel = new LanguagePickerPanel(LanguagePickerPalette.Dark);
        panel.Load(_source, _sourceIsAuto, _target, _installedSources, _recentTargets);
        panel.Picked += (_, pick) =>
        {
            CloseLanguagePanel();
            LanguagePicked?.Invoke(this, pick);
        };

        LanguagePanelHost.Content = panel;
        LanguagePanelHost.Visibility = Visibility.Visible;
        _languagePanelOpen = true;
        PositionLanguagePanel();

        LanguagePanelHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = EaseOut });
    }

    private void CloseLanguagePanel()
    {
        if (!_languagePanelOpen) return;
        _languagePanelOpen = false;
        LanguagePanelHost.Visibility = Visibility.Collapsed;
        LanguagePanelHost.Content = null;
    }

    /// <summary>Under the chip row, right edges aligned; above it when there's no room below.</summary>
    private void PositionLanguagePanel()
    {
        var size = MeasureNatural(LanguagePanelHost);
        if (size.Width <= 0 || size.Height <= 0) return;

        var left = _chipRowRect.Right - size.Width;
        var top = _chipRowRect.Bottom + 4;
        if (top + size.Height > Height - 4)
            top = _chipRowRect.Top - size.Height - 4;

        left = Clamp(left, 4, Math.Max(4, Width - size.Width - 4));
        top = Clamp(top, 4, Math.Max(4, Height - size.Height - 4));
        LanguagePanelHost.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>
    /// Tints the plate and the translated text with colors taken from the region itself, so the
    /// overlay reads as part of whatever is on screen rather than as a foreign card dropped on
    /// top of it. This is what makes the region overlay "match the game": a dark fantasy UI gets
    /// a warm plate, a blue sci-fi menu gets a cool one, and in both cases the translation sits
    /// where the original text sat and looks like it belongs there.
    ///
    /// The plate stays deliberately dark and the text deliberately light regardless of the
    /// extracted hue - legibility over a busy screenshot is the whole point of having a plate,
    /// and a faithfully-matched pale plate under pale text would defeat it.
    /// </summary>
    public void ApplySceneTheme(PopupTheme theme)
    {
        if (!theme.IsFromGame) return;

        var accent = theme.Accent;

        // Only a hint of the scene, not a match of it. Tinting hard made the overlay read as part
        // of the game's own UI at the cost of legibility - which is the entire reason there is a
        // plate behind the text in the first place. These weights are deliberately low: enough to
        // stop the overlay looking like a foreign card dropped on top, not enough to fight the text.
        const double PlateTint = 0.06;
        const double TextTint = 0.12;

        // Opaque, whatever the tint. The plate's job is to replace the text under it, and any
        // transparency at all leaves the original showing through the translation.
        HighlightBorder.Background = new SolidColorBrush(Color.FromArgb(
            0xFF,
            Mix(0x14, accent.R, PlateTint),
            Mix(0x14, accent.G, PlateTint),
            Mix(0x14, accent.B, PlateTint)));

        TranslatedText.Foreground = new SolidColorBrush(Color.FromRgb(
            Mix(0xEC, accent.R, TextTint),
            Mix(0xEC, accent.G, TextTint),
            Mix(0xEC, accent.B, TextTint)));

        HighlightBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        HighlightBorder.BorderThickness = new Thickness(1);

        static byte Mix(int baseValue, byte toward, double amount) =>
            (byte)Math.Clamp(baseValue + (toward - baseValue) * amount, 0, 255);
    }

    /// <summary>Swaps just the translated text in place (used when the user picks a different
    /// provider) without re-running the fade-in of the whole overlay.</summary>
    public void SetTranslationDirect(string translatedText)
    {
        CurrentTranslatedText = translatedText;
        var fadeOut = new DoubleAnimation(TextContainer.Opacity, 0, TimeSpan.FromMilliseconds(70));
        fadeOut.Completed += (_, _) =>
        {
            ApplyText(translatedText);
            TextContainer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = EaseOut });
        };
        TextContainer.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Lays the translation out at (as close as possible to) the size the original text was
    /// drawn at on screen. If the translation is too long to fit the region at that size - which
    /// is common, translations run longer than their source - it shrinks a little, and only if
    /// that still isn't enough does the plate grow downwards. Shrinking text to fit was what
    /// produced unreadable results before; maximising it was what produced huge ones.
    /// </summary>
    private void ApplyText(string translatedText)
    {
        var flow = _isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var fontFamily = UiFonts.For(_isRtl);

        var displayText = _ocrLineCount > 1 && !string.IsNullOrWhiteSpace(translatedText)
            ? SplitToLines(translatedText, _ocrLineCount)
            : translatedText;

        var maxWidth = Math.Max(20, _regionDip.Width - PlatePadX * 2);
        var maxHeight = Math.Max(14, _regionDip.Height - PlatePadY * 2);

        double fontSize;
        if (_matchSourceFontSize && _sourceFontSizeDip > 0)
        {
            // The estimate describes the source text's apparent size; convert it into this
            // font's own scale so Arabic doesn't come out smaller than the text it replaces.
            //
            // Then take a little off it. A translation is normally longer than what it replaced,
            // so starting at exactly the original's size means the fit below almost always has to
            // shrink it anyway - and starting slightly under lands more text at a size that was
            // chosen rather than salvaged.
            fontSize = Clamp(UiFonts.SizeFor(_sourceFontSizeDip, _isRtl) * SourceSizeAllowance, MinFontSize, MaxFontSize);
        }
        else
        {
            // "Fill region", or no usable OCR metrics: the largest that fits, capped so a short
            // translation in a large region doesn't become a billboard.
            fontSize = FitFontSize(displayText, fontFamily, flow, maxWidth, maxHeight, MinFontSize, FallbackMaxFontSize);
        }

        // The original line sits above the translation at two thirds its size when enabled, in
        // its own language's font and direction.
        var sourceFlow = _sourceIsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var sourceFont = UiFonts.For(_sourceIsRtl);
        // fontSize is in the translation font's scale; convert back to an apparent size before
        // re-scaling it into the source font's.
        var apparentSize = _isRtl ? fontSize / UiFonts.RtlSizeCompensation : fontSize;
        var originalSize = Math.Max(MinFontSize * 0.8, UiFonts.SizeFor(apparentSize * 0.66, _sourceIsRtl));
        var originalText = _showOriginal ? CurrentOriginalText : "";
        var originalHeight = string.IsNullOrWhiteSpace(originalText)
            ? 0
            : MeasureHeight(originalText, sourceFont, sourceFlow, originalSize, maxWidth) + 4;

        // Shrink as far as it takes, rather than the 30% this used to allow before growing the
        // plate instead. The overlay is drawing over a specific rectangle the user dragged around
        // specific text; a plate that grows past it covers whatever was next to that text, which
        // is at best confusing and at worst hides the thing they were about to read.
        var textHeight = MeasureHeight(displayText, fontFamily, flow, fontSize, maxWidth);
        if (textHeight + originalHeight > maxHeight)
        {
            fontSize = FitFontSize(displayText, fontFamily, flow, maxWidth, maxHeight - originalHeight, MinFontSize, fontSize);
            textHeight = MeasureHeight(displayText, fontFamily, flow, fontSize, maxWidth);
        }

        ApplyPlate(_regionDip);

        TextContainer.FlowDirection = flow;

        ApplyTextBlock(OriginalText, originalText, sourceFont, sourceFlow, originalSize, FontWeights.Normal);
        ApplyTextBlock(TranslatedText, displayText, fontFamily, flow, fontSize, FontWeights.SemiBold);
        OriginalText.Visibility = string.IsNullOrWhiteSpace(originalText) ? Visibility.Collapsed : Visibility.Visible;

        // Last resort, for a translation that will not fit even at the smallest readable size: cap
        // the block's height so the tail ellipsizes on the last line that fits. Without it the
        // container's clip simply sliced a line of text in half, which reads as a rendering bug
        // rather than as "there is more". The copy button still yields the whole translation.
        TranslatedText.MaxHeight = Math.Max(0, maxHeight - originalHeight);
        TranslatedText.TextTrimming = textHeight > maxHeight - originalHeight
            ? TextTrimming.CharacterEllipsis
            : TextTrimming.None;
    }

    private static void ApplyTextBlock(System.Windows.Controls.TextBlock block, string text,
        FontFamily fontFamily, FlowDirection flow, double fontSize, FontWeight weight)
    {
        block.FlowDirection = flow;

        // TextAlignment is *logical*, not physical: under RightToLeft flow, Left means the
        // leading edge - which is the right-hand side of the box - and Right pushes the text
        // to the physical left. Asking for Right here is what made Arabic hug the wrong edge,
        // so leading alignment is correct for both directions.
        block.TextAlignment = TextAlignment.Left;
        block.FontFamily = fontFamily;
        block.FontSize = fontSize;
        block.LineHeight = fontSize * 1.35;
        block.FontWeight = weight;
        block.Text = text;
    }

    /// <summary>Largest font size within [<paramref name="lo"/>, <paramref name="hi"/>] at which
    /// the text still wraps inside the box; <paramref name="lo"/> if none of them do. Measured
    /// with wrapping enabled - measuring the string as one unbroken line made every multi-word
    /// translation collapse to the minimum size.</summary>
    private static double FitFontSize(string text, FontFamily fontFamily, FlowDirection flow,
        double maxWidth, double maxHeight, double lo, double hi)
    {
        if (string.IsNullOrWhiteSpace(text)) return hi;
        if (hi <= lo) return lo;

        double best = lo;
        for (int i = 0; i < 14; i++)
        {
            var mid = (lo + hi) / 2;
            if (MeasureHeight(text, fontFamily, flow, mid, maxWidth) <= maxHeight)
            {
                best = mid;
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return Math.Round(best, 1);
    }

    private static double MeasureHeight(string text, FontFamily fontFamily, FlowDirection flow, double fontSize, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            flow,
            new Typeface(fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            fontSize,
            Brushes.White,
            96)
        {
            MaxTextWidth = maxWidth,
            LineHeight = fontSize * 1.35
        };

        return formatted.Height;
    }

    // ── Line splitting (unchanged behaviour: mirror the OCR's line count when possible) ──

    private static string SplitToLines(string text, int lineCount)
    {
        if (lineCount <= 1) return text;

        var clean = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // A translation that already has its own line structure keeps it, whole. This used to
        // truncate to the OCR's line count, silently dropping the end of any translation that
        // came back with an extra line; and splitting a multi-line answer again at its commas
        // chopped sentences the translator had deliberately kept together. Only a translation
        // that came back as one run-on line is redistributed below.
        var existingLines = clean.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (existingLines.Length > 1)
            return string.Join("\n", existingLines);

        var sentences = SplitAtBoundaries(clean);
        if (sentences.Count >= lineCount)
            return GroupIntoLines(sentences, lineCount);

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return clean;

        int wordsPerLine = Math.Max(1, words.Length / lineCount);
        var lines = new List<string>();
        for (int i = 0; i < lineCount && i * wordsPerLine < words.Length; i++)
        {
            int count = Math.Min(wordsPerLine, words.Length - i * wordsPerLine);
            if (i == lineCount - 1)
                count = words.Length - i * wordsPerLine;
            lines.Add(string.Join(" ", words.Skip(i * wordsPerLine).Take(count)));
        }
        return string.Join("\n", lines);
    }

    private static List<string> SplitAtBoundaries(string text)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '.' or '،' or '؛' or ';' or ',')
            {
                result.Add(text[start..(i + 1)].Trim());
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            var remaining = text[start..].Trim();
            if (remaining.Length > 0) result.Add(remaining);
        }
        return result;
    }

    private static string GroupIntoLines(List<string> sentences, int lineCount)
    {
        if (lineCount <= 0 || sentences.Count == 0) return string.Join(" ", sentences);

        var lines = new List<string>();
        int sentPerLine = Math.Max(1, sentences.Count / lineCount);
        int idx = 0;

        for (int i = 0; i < lineCount; i++)
        {
            int count = i == lineCount - 1 ? sentences.Count - idx : sentPerLine;
            if (count <= 0) break;

            lines.Add(string.Join(" ", sentences.Skip(idx).Take(count)));
            idx += count;
        }
        return string.Join("\n", lines);
    }

    // ── Provider dropdown ──

    private void ApiButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_dropdownOpen)
            CloseDropdown();
        else
            OpenDropdown();
    }

    private void OpenDropdown()
    {
        if (ApiDropdownItems.Children.Count == 0) return;
        CloseLanguagePanel();

        _dropdownOpen = true;
        ApiDropdown.Visibility = Visibility.Visible;
        PositionDropdown();

        var dur = TimeSpan.FromMilliseconds(130);
        ApiDropdown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut });
        DropdownScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, dur) { EasingFunction = EaseOut });
        DropdownTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, dur) { EasingFunction = EaseOut });

        ArrowPath.Data = Geometry.Parse("M0,4.5 L4.5,0 L9,4.5");
    }

    private void CloseDropdown(bool animate = true)
    {
        _dropdownOpen = false;
        ArrowPath.Data = Geometry.Parse("M0,0 L4.5,4.5 L9,0");

        if (!animate)
        {
            ApiDropdown.BeginAnimation(OpacityProperty, null);
            ApiDropdown.Opacity = 0;
            ApiDropdown.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(ApiDropdown.Opacity, 0, TimeSpan.FromMilliseconds(100));
        fade.Completed += (_, _) =>
        {
            if (!_dropdownOpen) ApiDropdown.Visibility = Visibility.Collapsed;
        };
        ApiDropdown.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Hangs the list off the chip - below it normally, above when the chip is close
    /// to the bottom of the screen - and keeps its right edge aligned with the chip's.</summary>
    private void PositionDropdown()
    {
        var size = MeasureNatural(ApiDropdown);
        if (size.Width <= 0 || size.Height <= 0) return;

        var left = _chipRect.Right - size.Width;
        var top = _chipRect.Bottom + 4;
        if (top + size.Height > Height - 4)
            top = _chipRect.Top - size.Height - 4;

        left = Clamp(left, 4, Math.Max(4, Width - size.Width - 4));
        top = Clamp(top, 4, Math.Max(4, Height - size.Height - 4));

        ApiDropdown.Margin = new Thickness(left, top, 0, 0);
    }

    private void ApiItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.Tag is not string apiId) return;

        CloseDropdown();
        if (apiId == _currentApiId) return;

        _currentApiId = apiId;
        ApiButton.ToolTip = $"Translated by {LabelFor(apiId)}";
        UpdateCheckMarks();

        ApiChanged?.Invoke(this, apiId);
    }

    private void UpdateCheckMarks()
    {
        foreach (var child in ApiDropdownItems.Children)
        {
            if (child is Button { Content: Grid grid, Tag: string id } && grid.Children.Count > 0 && grid.Children[0] is TextBlock check)
                check.Visibility = id == _currentApiId ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private static string LabelFor(string apiId)
    {
        foreach (var (id, label) in TranslationService.TranslationProviders)
        {
            if (id == apiId) return label;
        }
        return apiId;
    }
}
