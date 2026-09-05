using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace Tarjem.Services;

/// <summary>
/// Raises <see cref="TextCopied"/> whenever text lands on the clipboard from any application.
///
/// This replaced a watcher that tried to detect the *selection* itself, by polling for a
/// press-drag-release. That could tell a text selection from a drag across a game's viewport only
/// by guessing, so the button appeared constantly where there was nothing to translate and failed
/// to appear where there was. A copy is unambiguous: the user has deliberately taken text, and the
/// text is right there - no Ctrl+C to synthesize, no clipboard to save and restore, and it works
/// identically in every application including ones that never expose a selection.
///
/// Uses the clipboard-format listener, which is a notification rather than a poll: Windows posts
/// WM_CLIPBOARDUPDATE only when the contents actually change.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly IntPtr _windowHandle;
    private readonly HwndSource? _source;
    private bool _listening;

    /// <summary>Text Tarjem itself put on the clipboard, so the copy buttons in the popup and the
    /// region overlay don't bounce straight back as a new translation request.</summary>
    private string? _selfCopied;

    /// <summary>Fires with the copied text.</summary>
    public event EventHandler<string>? TextCopied;

    /// <param name="windowHandle">A window to receive the notifications - the app's existing
    /// message-only hotkey window.</param>
    public ClipboardWatcher(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
    }

    public bool IsRunning => _listening;

    public void Start()
    {
        if (_listening || _source == null) return;

        if (!AddClipboardFormatListener(_windowHandle))
        {
            Log.Warning("Could not register the clipboard listener (error {Error})", Marshal.GetLastWin32Error());
            return;
        }

        _source.AddHook(WndProc);
        _listening = true;
        Log.Information("Clipboard watcher started");
    }

    public void Stop()
    {
        if (!_listening) return;

        _source?.RemoveHook(WndProc);
        RemoveClipboardFormatListener(_windowHandle);
        _listening = false;
    }

    /// <summary>Marks text as ours, so the copy that follows is ignored.</summary>
    public void IgnoreNext(string text) => _selfCopied = text;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE) return IntPtr.Zero;

        try
        {
            // Anything that isn't text - an image, a file list - is not something to translate.
            if (!Clipboard.ContainsText()) return IntPtr.Zero;

            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return IntPtr.Zero;

            if (text == _selfCopied)
            {
                _selfCopied = null;
                return IntPtr.Zero;
            }

            TextCopied?.Invoke(this, text.Trim());
        }
        catch (Exception ex)
        {
            // The clipboard is single-owner: another application can hold it open at the exact
            // moment we're notified. Nothing here is worth interrupting the user over.
            Log.Debug(ex, "Clipboard read failed on update");
        }

        return IntPtr.Zero;
    }

    public void Dispose() => Stop();
}
