using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Tarjem.Services;

/// <summary>
/// Raises <see cref="Pressed"/> when Escape is pressed while it's running.
///
/// The region windows are deliberately <c>WS_EX_NOACTIVATE</c> so that drawing a selection over a
/// game doesn't pull focus away from it - which also means they never receive keyboard input, so
/// their KeyDown handlers were dead code. Polling the global key state is enough here and, unlike
/// a low-level keyboard hook or a temporary Escape hotkey, it can't swallow the key from whatever
/// app is actually focused or wedge system-wide input if something goes wrong.
/// </summary>
public sealed class EscapeWatcher : IDisposable
{
    private const int VK_ESCAPE = 0x1B;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly DispatcherTimer _timer;
    private bool _wasDown;

    public event EventHandler? Pressed;

    public EscapeWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30),
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        // Ignore an Escape that was already held when we started - otherwise the keypress that
        // dismissed something else immediately cancels this too.
        _wasDown = IsEscapeDown();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        var isDown = IsEscapeDown();
        var justPressed = isDown && !_wasDown;
        _wasDown = isDown;

        if (justPressed)
            Pressed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsEscapeDown() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
