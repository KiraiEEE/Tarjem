using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tarjem.Models;
using Tarjem.Services;

using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace Tarjem.Views;

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

    /// <summary>Fires with the text the copy button just put on the clipboard, so a clipboard
    /// watcher can tell our own copy apart from the user copying something new.</summary>
    public event EventHandler<string>? TextCopied;

    /// <summary>The OCR'd text currently on screen - what a provider switch re-translates.</summary>
    public string CurrentOriginalText { get; private set; } = "";

    /// <summary>Region rect in this window's own DIP coordinates (window origin = monitor origin).</summary>
    private Rect _regionDip;

    /// <summary>The dark plate actually drawn. Starts as the selected region and only grows
    /// when the translation genuinely can't be shown inside it at a readable size.</summary>
    private Rect _plateDip;

    private Rect _chipRect;
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
    }

    // ── Dismissal ──

    /// <summary>Any click on the overlay dismisses it, except clicks on the provider chip or
    /// its open list - those belong to the dropdown, and the first click outside an open
    /// dropdown only closes the dropdown rather than tearing down the whole overlay.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (IsDescendantOf(src, ApiButton) || IsDescendantOf(src, ApiDropdown) || IsDescendantOf(src, CopyButton))
            return;

        if (_dropdownOpen)
        {
            CloseDropdown();
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

        var dur = TimeSpan.FromMilliseconds(FadeMs);
        FadeTo(HighlightBorder, 0, dur);
        FadeTo(TextContainer, 0, dur);
        FadeTo(LoadingPanel, 0, dur);
        FadeTo(ApiButton, 0, dur);
        FadeTo(CopyButton, 0, dur);

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

        // Both chips share a row above the plate's top-right corner, provider first then copy.
        var rowWidth = size.Width + 4 + copySize.Width;
        var left = _plateDip.Right - rowWidth;
        var top = _plateDip.Top - size.Height - 8;
        if (top < 4)
            top = _plateDip.Bottom + 8;

        left = Clamp(left, 4, Math.Max(4, Width - rowWidth - 4));
        top = Clamp(top, 4, Math.Max(4, Height - size.Height - 4));

        _chipRect = new Rect(left, top, size.Width, size.Height);
        ApiButton.Margin = new Thickness(left, top, 0, 0);
        CopyButton.Margin = new Thickness(left + size.Width + 4, top, 0, 0);

        if (_dropdownOpen)
            PositionDropdown();
    }

    /// <summary>Copies the translated text. The overlay covers the text it replaces, so without
    /// this the translation is the one thing on screen a user cannot get out of the app.</summary>
    private void CopyButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var text = TranslatedText.Text;
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
        ApiButton.Visibility = Visibility.Collapsed;

        var dur = TimeSpan.FromMilliseconds(FadeMs);
        DimOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, _dimBehind ? DimOpacity : 0, dur) { EasingFunction = EaseOut });
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
        _isRtl = isRtl;
        _sourceIsRtl = sourceIsRtl;
        _ocrLineCount = Math.Max(1, ocrLineCount);
        _sourceFontSizeDip = sourceFontSizePhysical > 0 ? sourceFontSizePhysical / _dpiScale : 0;

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
        ApiButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut });
        CopyButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut });
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

        var existingLines = clean.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (existingLines.Length >= lineCount)
            return string.Join("\n", existingLines.Take(lineCount));

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
