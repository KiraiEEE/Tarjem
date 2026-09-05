using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;
using Tarjem.Models;

namespace Tarjem.Services;

/// <summary>
/// Owns the two global hotkeys. Both are user-configurable, so registration has to be something
/// that can fail and be reported rather than a fixed startup step: any combination may already
/// be taken by another application, and Windows refuses a fair number of Win+key combinations
/// outright.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_WORD = 9000;
    private const int HOTKEY_ID_REGION = 9001;
    private const int HOTKEY_ID_EXPLAIN = 9002;

    private HwndSource? _source;
    private readonly IntPtr _windowHandle;

    private HotkeyBinding? _word;
    private HotkeyBinding? _region;
    private HotkeyBinding? _explain;

    public event EventHandler? WordHotkeyPressed;
    public event EventHandler? RegionHotkeyPressed;
    public event EventHandler? ExplainHotkeyPressed;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public HotkeyService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    /// <summary>The bindings currently registered with Windows.</summary>
    public HotkeyBinding Word => _word ?? HotkeyBinding.DefaultWord();
    public HotkeyBinding Region => _region ?? HotkeyBinding.DefaultRegion();
    public HotkeyBinding Explain => _explain ?? HotkeyBinding.DefaultExplain();

    /// <summary>Initial registration. Throws if any hotkey can't be taken, so startup can
    /// tell the user which one and why.</summary>
    public void Register(HotkeyBinding word, HotkeyBinding region, HotkeyBinding explain)
    {
        if (_source == null)
        {
            _source = HwndSource.FromHwnd(_windowHandle);
            _source?.AddHook(HwndHook);
        }

        if (!TryApply(word, region, explain, out var error))
            throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Swaps in a new set of hotkeys, rolling back to the previous set if any of them fails to
    /// register - a half-applied change would leave a feature silently without a hotkey.
    /// </summary>
    public bool TryApply(HotkeyBinding word, HotkeyBinding region, HotkeyBinding explain, out string? error)
    {
        var wanted = new[]
        {
            (Id: HOTKEY_ID_WORD, Binding: word),
            (Id: HOTKEY_ID_REGION, Binding: region),
            (Id: HOTKEY_ID_EXPLAIN, Binding: explain),
        };

        foreach (var (_, binding) in wanted)
            if (!binding.Validate(out error)) return false;

        // Every pair, not just the first two - with three shortcuts it's the third that most
        // easily collides with one of the others.
        for (var i = 0; i < wanted.Length; i++)
        {
            for (var j = i + 1; j < wanted.Length; j++)
            {
                if (wanted[i].Binding.SameAs(wanted[j].Binding))
                {
                    error = $"{wanted[i].Binding.Display} is assigned to two different shortcuts.";
                    return false;
                }
            }
        }

        var previous = (_word, _region, _explain);
        UnregisterAll();

        var taken = new List<int>();
        foreach (var (id, binding) in wanted)
        {
            if (TryRegister(id, binding))
            {
                taken.Add(id);
                continue;
            }

            error = $"{binding.Display} is already in use by another application.";
            foreach (var registered in taken)
                UnregisterHotKey(_windowHandle, registered);
            Rollback(previous);
            return false;
        }

        (_word, _region, _explain) = (word, region, explain);
        error = null;
        return true;
    }

    private void Rollback((HotkeyBinding? Word, HotkeyBinding? Region, HotkeyBinding? Explain) previous)
    {
        _word = Restore(HOTKEY_ID_WORD, previous.Word);
        _region = Restore(HOTKEY_ID_REGION, previous.Region);
        _explain = Restore(HOTKEY_ID_EXPLAIN, previous.Explain);

        if ((previous.Word != null && _word == null) ||
            (previous.Region != null && _region == null) ||
            (previous.Explain != null && _explain == null))
            Log.Warning("Failed to restore the previous hotkeys after a rejected change");

        HotkeyBinding? Restore(int id, HotkeyBinding? binding) =>
            binding != null && TryRegister(id, binding) ? binding : null;
    }

    private bool TryRegister(int id, HotkeyBinding binding) =>
        RegisterHotKey(_windowHandle, id, binding.Modifiers, binding.VirtualKey);

    private void UnregisterAll()
    {
        UnregisterHotKey(_windowHandle, HOTKEY_ID_WORD);
        UnregisterHotKey(_windowHandle, HOTKEY_ID_REGION);
        UnregisterHotKey(_windowHandle, HOTKEY_ID_EXPLAIN);
    }

    public void Unregister()
    {
        _source?.RemoveHook(HwndHook);
        UnregisterAll();
        _word = null;
        _region = null;
        _explain = null;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HOTKEY_ID_WORD:
                    WordHotkeyPressed?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case HOTKEY_ID_REGION:
                    RegionHotkeyPressed?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case HOTKEY_ID_EXPLAIN:
                    ExplainHotkeyPressed?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        GC.SuppressFinalize(this);
    }
}
