using System.Runtime.InteropServices;

namespace Tarjem.Services;

/// <summary>
/// Ensures only one Tarjem process runs at a time. Without this, launching the app twice
/// silently produces two processes that both try to register the same global hotkey - the
/// second registration fails, so the second instance sits in the tray with a dead hotkey
/// and no indication anything is wrong. A second launch instead signals the first instance
/// (via a system-wide broadcast window message) to show its main window, then exits.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\Tarjem.SingleInstance";
    private static readonly int ShowWindowMessage = RegisterWindowMessage("Tarjem_ShowMainWindow");
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    private readonly Mutex _mutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Raised on the first instance's message loop when a second launch asks it to show its window.</summary>
    public event EventHandler? RequestShowMainWindow;

    public bool IsFirstInstance { get; }

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
    }

    /// <summary>Tells whichever instance is already running to show its main window.
    /// Call this (then exit) when <see cref="IsFirstInstance"/> is false.</summary>
    public static void NotifyRunningInstance() =>
        PostMessage(HwndBroadcast, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Add to the app's HwndSource hook chain (the same window already used for
    /// the global hotkey) so a broadcast from <see cref="NotifyRunningInstance"/> reaches it.</summary>
    public IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ShowWindowMessage)
        {
            RequestShowMainWindow?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); }
            catch (SynchronizationLockException) { /* not owned - nothing to release */ }
        }

        _mutex.Dispose();
    }
}
