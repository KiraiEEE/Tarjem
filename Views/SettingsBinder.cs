using System.Windows;
using System.Windows.Controls;
using Tarjem.Services;

namespace Tarjem.Views;

/// <summary>
/// Wires a settings control to the property it edits.
///
/// Every toggle and picker in Settings did the same four things by hand - read the value on load,
/// ignore events while loading, write the value back, call Save - which is around forty
/// near-identical handlers whose only real content is the property name. That volume of
/// boilerplate is where an inconsistency hides: one of them forgetting the loading guard, or
/// saving to the wrong property, looks exactly like all the others.
/// </summary>
public sealed class SettingsBinder
{
    private readonly SettingsService _settings;

    /// <summary>Set while controls are being populated from stored values, so the change events
    /// that fire as a side effect don't write those same values straight back.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Runs after any bound control is changed and saved - for the handful of settings
    /// that other UI has to react to.</summary>
    public event EventHandler? Changed;

    public SettingsBinder(SettingsService settings) => _settings = settings;

    /// <summary>Populates every bound control without triggering writes.</summary>
    public void Load(Action load)
    {
        IsLoading = true;
        try { load(); }
        finally { IsLoading = false; }
    }

    /// <summary>Binds a toggle to a bool property. Typed as ToggleButton because that is the
    /// common base of WPF-UI's ToggleSwitch and the stock CheckBox.</summary>
    public void Bind(System.Windows.Controls.Primitives.ToggleButton toggle, Func<Models.AppSettings, bool> read, Action<Models.AppSettings, bool> write)
    {
        toggle.IsChecked = read(_settings.Current);

        void Handler(object sender, RoutedEventArgs e)
        {
            if (IsLoading) return;
            write(_settings.Current, toggle.IsChecked == true);
            Persist();
        }

        toggle.Checked += Handler;
        toggle.Unchecked += Handler;
    }

    /// <summary>Binds a combo whose items carry their value in Tag to a string property. Falls
    /// back to the first item when the stored value isn't in the list, which is what a settings
    /// file written by an older or newer build looks like.</summary>
    public void Bind(ComboBox combo, Func<Models.AppSettings, string> read, Action<Models.AppSettings, string> write)
    {
        Select(combo, read(_settings.Current));

        combo.SelectionChanged += (_, _) =>
        {
            if (IsLoading) return;
            if (combo.SelectedItem is not ComboBoxItem { Tag: string value }) return;

            write(_settings.Current, value);
            Persist();
        };
    }

    /// <summary>Fills a combo with (id, label) pairs and selects one, without firing writes.</summary>
    public void Fill(ComboBox combo, IEnumerable<(string Id, string Label)> items, string selectedId)
    {
        var wasLoading = IsLoading;
        IsLoading = true;

        try
        {
            combo.Items.Clear();
            foreach (var (id, label) in items)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });

            Select(combo, selectedId);
        }
        finally
        {
            IsLoading = wasLoading;
        }
    }

    public static void Select(ComboBox combo, string? id)
    {
        var match = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == id);
        combo.SelectedItem = match ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void Persist()
    {
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
