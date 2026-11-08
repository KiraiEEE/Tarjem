using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Tarjem.Core.Language;

namespace Tarjem.Views;

/// <summary>What the user picked. Either side is null when it didn't change.</summary>
/// <param name="Source">A catalog code, or <see cref="LanguageCatalog.Auto"/>.</param>
public sealed record LanguagePick(string? Source, string? Target);

/// <summary>Colours for <see cref="LanguagePickerPanel"/>, so it can sit on the always-dark region
/// overlay and on the light or dark result cards without looking pasted in.</summary>
public sealed record LanguagePickerPalette(Color Background, Color Border, Color Foreground, Color Muted, Color Hover, Color Accent)
{
    public static readonly LanguagePickerPalette Dark = new(
        Color.FromRgb(0x24, 0x24, 0x2E), Color.FromRgb(0x3A, 0x3A, 0x48), Color.FromRgb(0xEA, 0xEA, 0xEA),
        Color.FromRgb(0x8C, 0x8C, 0x98), Color.FromRgb(0x34, 0x34, 0x42), Color.FromRgb(0x5B, 0x9E, 0xF5));

    public static readonly LanguagePickerPalette Light = new(
        Color.FromRgb(0xFB, 0xFB, 0xFB), Color.FromRgb(0xDD, 0xDD, 0xE2), Color.FromRgb(0x1A, 0x1A, 0x1A),
        Color.FromRgb(0x77, 0x77, 0x80), Color.FromRgb(0xEC, 0xEC, 0xF0), Color.FromRgb(0x25, 0x63, 0xEB));

    public LanguagePickerPalette WithAccent(Color accent) => this with { Accent = accent };
}

/// <summary>
/// The "From / To" language picker every result window shares: the word popup, the "what is
/// this?" card and the region overlay.
///
/// Changing language used to mean opening Settings, finding the Languages page and coming back -
/// for something people do mid-read, often more than once. It now happens where the result is.
/// The source column leads with Auto-detect and then the languages Windows can actually read on
/// this machine; the rest are still listed, marked, because a pack can be installed at any time.
/// The target column leads with the ones used recently.
///
/// Built in code rather than XAML because it is hosted in three different windows with three
/// different hosting needs (a Popup on the cards, an in-window panel on the overlay).
/// </summary>
public sealed class LanguagePickerPanel : Border
{
    private const double ColumnWidth = 214;
    private const double ListHeight = 300;

    private readonly LanguagePickerPalette _palette;
    private readonly bool _showSource;

    public event EventHandler<LanguagePick>? Picked;

    public LanguagePickerPanel(LanguagePickerPalette palette, bool showSource = true)
    {
        _palette = palette;
        _showSource = showSource;

        Background = new SolidColorBrush(palette.Background);
        BorderBrush = new SolidColorBrush(palette.Border);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(6);
        SnapsToDevicePixels = true;
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black, Direction = 270,
        };

        TextElement.SetFontFamily(this, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        TextElement.SetFontSize(this, 12);
    }

    /// <param name="source">The source language now in force (the detected one, when detecting).</param>
    /// <param name="sourceIsAuto">Whether auto-detect is on.</param>
    /// <param name="installedSources">Languages with a Windows OCR pack installed.</param>
    /// <param name="recentTargets">Most recent first.</param>
    public void Load(string source, bool sourceIsAuto, string target,
        IReadOnlyList<string> installedSources, IReadOnlyList<string> recentTargets)
    {
        var grid = new Grid();

        if (_showSource)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth) });

        var column = 0;
        if (_showSource)
        {
            var sourceColumn = Column("Translate from", SourceRows(source, sourceIsAuto, installedSources));
            Grid.SetColumn(sourceColumn, column++);
            grid.Children.Add(sourceColumn);

            var swap = SwapButton(source, sourceIsAuto, target);
            Grid.SetColumn(swap, column++);
            grid.Children.Add(swap);
        }

        var targetColumn = Column("Translate to", TargetRows(target, recentTargets));
        Grid.SetColumn(targetColumn, column);
        grid.Children.Add(targetColumn);

        Child = grid;
    }

    // ── Rows ──

    private IEnumerable<UIElement> SourceRows(string source, bool sourceIsAuto, IReadOnlyList<string> installed)
    {
        var detectedName = sourceIsAuto && LanguageCatalog.Find(source) is { } d ? d.Name : null;
        yield return Row("Auto-detect", detectedName != null ? $"found {detectedName}" : null, sourceIsAuto,
            () => Raise(new LanguagePick(LanguageCatalog.Auto, null)));

        var installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var ordered = LanguageCatalog.All.Where(l => installedSet.Contains(l.Code))
            .Concat(LanguageCatalog.All.Where(l => !installedSet.Contains(l.Code)))
            .ToList();

        var dividerShown = false;
        foreach (var language in ordered)
        {
            var hasPack = installedSet.Contains(language.Code);
            if (!hasPack && !dividerShown && installedSet.Count > 0)
            {
                dividerShown = true;
                yield return Caption("No OCR pack installed");
            }

            var code = language.Code;
            yield return Row(language.Name, language.NativeName, !sourceIsAuto && Same(code, source),
                () => Raise(new LanguagePick(code, null)), dim: !hasPack && installedSet.Count > 0);
        }
    }

    private IEnumerable<UIElement> TargetRows(string target, IReadOnlyList<string> recent)
    {
        var recentKnown = recent.Select(LanguageCatalog.Find).OfType<LanguageInfo>().DistinctBy(l => l.Code).Take(4).ToList();
        if (recentKnown.Count > 0)
        {
            yield return Caption("Recent");
            foreach (var language in recentKnown)
            {
                var code = language.Code;
                yield return Row(language.Name, language.NativeName, Same(code, target), () => Raise(new LanguagePick(null, code)));
            }
            yield return Caption("All languages");
        }

        foreach (var language in LanguageCatalog.All)
        {
            var code = language.Code;
            yield return Row(language.Name, language.NativeName, Same(code, target), () => Raise(new LanguagePick(null, code)));
        }
    }

    private static bool Same(string a, string b) =>
        LanguageCatalog.Find(a)?.Code is { } x && x == LanguageCatalog.Find(b)?.Code;

    private void Raise(LanguagePick pick) => Picked?.Invoke(this, pick);

    // ── Building blocks ──

    private FrameworkElement Column(string header, IEnumerable<UIElement> rows)
    {
        // Room on the right for the scrollbar, which overlays the content under the app's theme
        // and otherwise sat on top of the native names.
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var row in rows) panel.Children.Add(row);

        var scroller = new ScrollViewer
        {
            Content = panel,
            MaxHeight = ListHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly,
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = header,
            Foreground = new SolidColorBrush(_palette.Muted),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 2, 8, 6),
        });
        stack.Children.Add(scroller);
        return stack;
    }

    private UIElement Caption(string text) => new TextBlock
    {
        Text = text,
        Foreground = new SolidColorBrush(_palette.Muted),
        FontSize = 10.5,
        Margin = new Thickness(8, 8, 8, 3),
    };

    private UIElement Row(string name, string? detail, bool selected, Action onClick, bool dim = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var check = new TextBlock
        {
            Text = "✓",
            Foreground = new SolidColorBrush(_palette.Accent),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = selected ? Visibility.Visible : Visibility.Hidden,
        };
        var label = new TextBlock
        {
            Text = name,
            Foreground = new SolidColorBrush(dim ? _palette.Muted : _palette.Foreground),
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(check);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(detail) && detail != name)
        {
            var native = new TextBlock
            {
                Text = detail,
                Foreground = new SolidColorBrush(_palette.Muted),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(native, 2);
            grid.Children.Add(native);
        }

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 5, 8, 5),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var hover = new SolidColorBrush(_palette.Hover);
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return row;
    }

    /// <summary>Swaps the two sides - reading back a reply, or flipping a pair that came out the
    /// wrong way round. Disabled while detecting, since there is no single source to swap in.</summary>
    private UIElement SwapButton(string source, bool sourceIsAuto, string target)
    {
        var enabled = !sourceIsAuto && !Same(source, target);

        var glyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,3 L10,3 M7,0 L10,3 L7,6 M10,10 L0,10 M3,7 L0,10 L3,13"),
            Stroke = new SolidColorBrush(enabled ? _palette.Foreground : _palette.Muted),
            StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Border
        {
            Child = glyph,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = enabled ? 1 : 0.45,
            ToolTip = enabled ? "Swap languages" : "Pick a source language to swap",
        };

        if (enabled)
        {
            var hover = new SolidColorBrush(_palette.Hover);
            button.MouseEnter += (_, _) => button.Background = hover;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Raise(new LanguagePick(target, source));
            };
        }

        return button;
    }
}
