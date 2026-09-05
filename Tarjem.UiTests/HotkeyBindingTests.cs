using System.Text.Json;
using System.Windows.Input;
using Tarjem.Models;
using Xunit;

namespace Tarjem.UiTests;

public class HotkeyBindingTests
{
    [Fact]
    public void DefaultsAreAltQAltWAndAltE()
    {
        Assert.Equal("Alt + Q", HotkeyBinding.DefaultWord().Display);
        Assert.Equal("Alt + W", HotkeyBinding.DefaultRegion().Display);
        Assert.Equal("Alt + E", HotkeyBinding.DefaultExplain().Display);
    }

    [Fact]
    public void DefaultsDoNotCollideWithEachOther()
    {
        // HotkeyService rejects any set with a duplicate, so shipping colliding defaults would
        // leave a fresh install unable to register its own shortcuts.
        var defaults = new[]
        {
            HotkeyBinding.DefaultWord(),
            HotkeyBinding.DefaultRegion(),
            HotkeyBinding.DefaultExplain(),
        };

        for (var i = 0; i < defaults.Length; i++)
            for (var j = i + 1; j < defaults.Length; j++)
                Assert.False(defaults[i].SameAs(defaults[j]));
    }

    [Fact]
    public void FormatsModifiersInWindowsOrder()
    {
        var binding = HotkeyBinding.From(Key.F, ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal("Ctrl + Alt + Shift + F", binding.Display);
    }

    [Theory]
    [InlineData(Key.D4, "4")]
    [InlineData(Key.NumPad7, "Num 7")]
    [InlineData(Key.OemComma, ",")]
    [InlineData(Key.OemPeriod, ".")]
    [InlineData(Key.Space, "Space")]
    [InlineData(Key.F9, "F9")]
    public void FormatsAwkwardKeyNamesReadably(Key key, string expected)
    {
        Assert.Equal($"Ctrl + {expected}", HotkeyBinding.From(key, ModifierKeys.Control).Display);
    }

    [Fact]
    public void RejectsCombinationsWithoutCtrlAltOrWin()
    {
        // A bare key (or one held only with Shift) registered globally would take that key away
        // from every other application.
        Assert.False(HotkeyBinding.From(Key.Q, ModifierKeys.None).Validate(out _));
        Assert.False(HotkeyBinding.From(Key.Q, ModifierKeys.Shift).Validate(out _));

        Assert.True(HotkeyBinding.From(Key.Q, ModifierKeys.Alt).Validate(out _));
        Assert.True(HotkeyBinding.From(Key.Q, ModifierKeys.Control).Validate(out _));
        Assert.True(HotkeyBinding.From(Key.Q, ModifierKeys.Windows).Validate(out _));
        Assert.True(HotkeyBinding.From(Key.Q, ModifierKeys.Control | ModifierKeys.Shift).Validate(out _));
    }

    [Fact]
    public void RejectsAModifierAsTheTriggerKey()
    {
        Assert.False(HotkeyBinding.From(Key.LeftAlt, ModifierKeys.Alt).Validate(out _));
        Assert.False(new HotkeyBinding { Alt = true, KeyName = "None" }.Validate(out _));
    }

    [Fact]
    public void ValidationErrorsExplainWhatToDo()
    {
        HotkeyBinding.From(Key.Q, ModifierKeys.None).Validate(out var error);
        Assert.Contains("Ctrl", error);
    }

    [Fact]
    public void SameAsComparesModifiersAndKey()
    {
        var altQ = HotkeyBinding.From(Key.Q, ModifierKeys.Alt);

        Assert.True(altQ.SameAs(HotkeyBinding.From(Key.Q, ModifierKeys.Alt)));
        Assert.False(altQ.SameAs(HotkeyBinding.From(Key.W, ModifierKeys.Alt)));
        Assert.False(altQ.SameAs(HotkeyBinding.From(Key.Q, ModifierKeys.Control)));
        Assert.False(altQ.SameAs(HotkeyBinding.From(Key.Q, ModifierKeys.Alt | ModifierKeys.Shift)));
        Assert.False(altQ.SameAs(null));
    }

    [Fact]
    public void ModifierFlagsMatchWin32AndAlwaysSuppressKeyRepeat()
    {
        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;

        var all = HotkeyBinding.From(Key.Q, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
        Assert.Equal(MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN | MOD_NOREPEAT, all.Modifiers);

        // Holding the combo must never re-fire the whole OCR pipeline on key repeat.
        Assert.Equal(MOD_ALT | MOD_NOREPEAT, HotkeyBinding.From(Key.Q, ModifierKeys.Alt).Modifiers);
    }

    [Fact]
    public void VirtualKeyMatchesTheWin32Code()
    {
        Assert.Equal(0x51u, HotkeyBinding.From(Key.Q, ModifierKeys.Alt).VirtualKey); // 'Q'
        Assert.Equal(0x57u, HotkeyBinding.From(Key.W, ModifierKeys.Alt).VirtualKey); // 'W'
        Assert.Equal(0x78u, HotkeyBinding.From(Key.F9, ModifierKeys.Alt).VirtualKey);
    }

    [Fact]
    public void SurvivesAJsonRoundTrip()
    {
        var original = HotkeyBinding.From(Key.F9, ModifierKeys.Control | ModifierKeys.Shift);
        var restored = JsonSerializer.Deserialize<HotkeyBinding>(JsonSerializer.Serialize(original))!;

        Assert.True(original.SameAs(restored));
        Assert.Equal(original.Display, restored.Display);
        Assert.Equal(original.Modifiers, restored.Modifiers);
    }

    [Fact]
    public void UnparseableKeyNameDegradesInsteadOfThrowing()
    {
        var binding = new HotkeyBinding { Alt = true, KeyName = "NotARealKey" };

        Assert.Equal(Key.None, binding.Key);
        Assert.Equal("Not set", binding.Display);
        Assert.False(binding.Validate(out _));
    }

    [Fact]
    public void SettingsCarryTheHotkeysThroughSerialization()
    {
        var settings = new AppSettings
        {
            WordHotkey = HotkeyBinding.From(Key.J, ModifierKeys.Control | ModifierKeys.Alt),
            RegionHotkey = HotkeyBinding.From(Key.K, ModifierKeys.Control | ModifierKeys.Alt),
        };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal("Ctrl + Alt + J", restored.WordHotkey.Display);
        Assert.Equal("Ctrl + Alt + K", restored.RegionHotkey.Display);
    }

    [Fact]
    public void SettingsFileFromBeforeHotkeysWereConfigurableStillLoads()
    {
        // Old settings.json has no hotkey properties at all - they must come back as the defaults
        // rather than null, or startup would dereference them.
        var restored = JsonSerializer.Deserialize<AppSettings>("""{"TargetLanguage":"ar"}""")!;

        Assert.Equal("Alt + Q", restored.WordHotkey.Display);
        Assert.Equal("Alt + W", restored.RegionHotkey.Display);
    }
}
