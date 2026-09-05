using System.Text;
using System.Windows.Input;

namespace Tarjem.Models;

/// <summary>
/// A global hotkey, stored as modifier flags plus a key name so it survives a JSON round-trip
/// and can be handed to Win32's RegisterHotKey.
/// </summary>
public class HotkeyBinding
{
    public bool Control { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }

    /// <summary>The Windows key. Allowed, but discouraged - Windows reserves a lot of Win+key
    /// combinations for itself and those registrations simply fail.</summary>
    public bool Windows { get; set; }

    /// <summary>Name of a <see cref="System.Windows.Input.Key"/> value, e.g. "Q" or "F4".</summary>
    public string KeyName { get; set; } = "None";

    // Win32 modifier flags.
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    /// <summary>Stops Windows re-firing the hotkey on every key-repeat tick while it's held -
    /// without it, holding the combo re-runs the whole capture/OCR/translate pipeline over and
    /// over until the keys are released.</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    public static HotkeyBinding Alt_(Key key) => new() { Alt = true, KeyName = key.ToString() };

    public static HotkeyBinding DefaultWord() => Alt_(Key.Q);
    public static HotkeyBinding DefaultRegion() => Alt_(Key.W);

    /// <summary>"What is this?" - the encyclopedia lookup. Alt+E sits next to Q and W so the
    /// three shortcuts stay one row apart on the keyboard.</summary>
    public static HotkeyBinding DefaultExplain() => Alt_(Key.E);

    public static HotkeyBinding From(Key key, ModifierKeys modifiers) => new()
    {
        Control = modifiers.HasFlag(ModifierKeys.Control),
        Alt = modifiers.HasFlag(ModifierKeys.Alt),
        Shift = modifiers.HasFlag(ModifierKeys.Shift),
        Windows = modifiers.HasFlag(ModifierKeys.Windows),
        KeyName = key.ToString(),
    };

    public Key Key => Enum.TryParse<Key>(KeyName, out var key) ? key : System.Windows.Input.Key.None;

    public uint Modifiers
    {
        get
        {
            uint flags = MOD_NOREPEAT;
            if (Control) flags |= MOD_CONTROL;
            if (Alt) flags |= MOD_ALT;
            if (Shift) flags |= MOD_SHIFT;
            if (Windows) flags |= MOD_WIN;
            return flags;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>Human-readable form, e.g. "Ctrl + Shift + Q". Order matches how Windows itself
    /// writes shortcuts.</summary>
    public string Display
    {
        get
        {
            if (Key == System.Windows.Input.Key.None) return "Not set";

            var text = new StringBuilder();
            if (Control) text.Append("Ctrl + ");
            if (Alt) text.Append("Alt + ");
            if (Shift) text.Append("Shift + ");
            if (Windows) text.Append("Win + ");
            text.Append(FormatKey(Key));
            return text.ToString();
        }
    }

    private static string FormatKey(Key key) => key switch
    {
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => key.ToString()[1..],
        >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num " + key.ToString()[6..],
        System.Windows.Input.Key.OemComma => ",",
        System.Windows.Input.Key.OemPeriod => ".",
        System.Windows.Input.Key.OemQuestion => "/",
        System.Windows.Input.Key.OemMinus => "-",
        System.Windows.Input.Key.OemPlus => "=",
        System.Windows.Input.Key.OemOpenBrackets => "[",
        System.Windows.Input.Key.OemCloseBrackets => "]",
        System.Windows.Input.Key.OemSemicolon => ";",
        System.Windows.Input.Key.OemQuotes => "'",
        System.Windows.Input.Key.OemBackslash or System.Windows.Input.Key.OemPipe => "\\",
        System.Windows.Input.Key.OemTilde => "`",
        System.Windows.Input.Key.Space => "Space",
        _ => key.ToString(),
    };

    public bool SameAs(HotkeyBinding? other) =>
        other != null &&
        Control == other.Control && Alt == other.Alt &&
        Shift == other.Shift && Windows == other.Windows &&
        Key == other.Key;

    /// <summary>Keys that only make sense as part of a combination, never as the trigger key.</summary>
    public static bool IsModifierKey(Key key) => key is
        System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or
        System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or
        System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or
        System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin or
        System.Windows.Input.Key.System or System.Windows.Input.Key.None;

    /// <summary>
    /// Whether this is a shortcut we're willing to register globally. A global hotkey takes the
    /// key away from every other application, so a bare key - or one held only with Shift -
    /// would make that key unusable system-wide the moment it's set.
    /// </summary>
    public bool Validate(out string? error)
    {
        if (Key == System.Windows.Input.Key.None || IsModifierKey(Key))
        {
            error = "Press a key together with Ctrl, Alt or Win.";
            return false;
        }

        if (!Control && !Alt && !Windows)
        {
            error = "Add Ctrl, Alt or Win - a global shortcut without one would take that key away from every other app.";
            return false;
        }

        error = null;
        return true;
    }
}
