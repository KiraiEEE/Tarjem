using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Tarjem.Core.Language;

namespace Tarjem.Views;

/// <summary>
/// The small "EN → AR" label on the word popup and the "what is this?" card. Clicking it opens
/// the <see cref="LanguagePickerPanel"/> in a popup under it.
///
/// Muted and text-only on purpose: it is information first (which languages this answer is in)
/// and a control second, and the cards are small enough that another button-shaped thing in them
/// would compete with the answer.
/// </summary>
public sealed class LanguageChip : Border
{
    private readonly TextBlock _label;
    private readonly Popup _popup;

    private string _source = "en";
    private bool _sourceIsAuto;
    private string _target = "ar";
    private IReadOnlyList<string> _installed = Array.Empty<string>();
    private IReadOnlyList<string> _recent = Array.Empty<string>();

    private Color _foreground = Color.FromRgb(0x88, 0x88, 0x88);
    private Color _hover = Color.FromArgb(0x22, 0x80, 0x80, 0x80);
    private LanguagePickerPalette _palette = LanguagePickerPalette.Dark;

    public event EventHandler<LanguagePick>? Picked;

    public LanguageChip()
    {
        _label = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Child = _label;
        Padding = new Thickness(6, 2, 6, 2);
        CornerRadius = new CornerRadius(4);
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetInitialShowDelay(this, 400);

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Focusable = false,
        };

        MouseEnter += (_, _) => Background = new SolidColorBrush(_hover);
        MouseLeave += (_, _) => { if (!_popup.IsOpen) Background = Brushes.Transparent; };
        _popup.Closed += (_, _) => Background = IsMouseOver ? new SolidColorBrush(_hover) : Brushes.Transparent;
        MouseLeftButtonUp += OnClick;
    }

    /// <summary>Colours to match the card it sits on.</summary>
    public void ApplyColors(Color foreground, Color hover, LanguagePickerPalette palette)
    {
        _foreground = foreground;
        _hover = hover;
        _palette = palette;
        _label.Foreground = new SolidColorBrush(foreground);
    }

    public void Set(string source, bool sourceIsAuto, string target,
        IReadOnlyList<string> installedSources, IReadOnlyList<string> recentTargets)
    {
        _source = source;
        _sourceIsAuto = sourceIsAuto;
        _target = target;
        _installed = installedSources;
        _recent = recentTargets;

        _label.Text = $"{LanguageCatalog.ShortLabel(source)} → {LanguageCatalog.ShortLabel(target)}";
        ToolTip = $"{LanguageCatalog.NameOf(source)}{(sourceIsAuto ? " (detected)" : "")} to {LanguageCatalog.NameOf(target)}. Click to change.";
    }

    /// <summary>Dims the label while a re-translation runs, so the click visibly did something.</summary>
    public bool IsBusy
    {
        set => _label.Opacity = value ? 0.45 : 1;
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }

        var panel = new LanguagePickerPanel(_palette);
        panel.Load(_source, _sourceIsAuto, _target, _installed, _recent);
        panel.Picked += (_, pick) =>
        {
            _popup.IsOpen = false;
            Picked?.Invoke(this, pick);
        };

        // A little margin so the panel's drop shadow isn't clipped by the popup's own window.
        _popup.Child = new Border { Child = panel, Padding = new Thickness(8, 0, 8, 12) };
        _popup.IsOpen = true;
    }
}
