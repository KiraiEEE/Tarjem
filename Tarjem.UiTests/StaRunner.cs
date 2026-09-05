using System.Windows;
using System.Windows.Threading;

using Application = System.Windows.Application;

namespace Tarjem.UiTests;

/// <summary>
/// Runs test bodies on one shared, long-lived STA thread with a live WPF
/// <see cref="Application"/>, rethrowing failures on the calling thread so xunit reports them
/// normally.
///
/// WPF needs all of this to behave like the real app: an STA apartment, a dispatcher, and an
/// Application whose merged resource dictionaries (App.xaml's WPF-UI theme + controls) are
/// loaded. It must be a *single shared* thread - only one Application can exist per process and
/// it belongs to whichever thread created it, so giving each test its own thread leaves later
/// tests using an Application whose dispatcher thread has already exited.
/// </summary>
internal static class StaRunner
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    public static void Run(Action body)
    {
        var dispatcher = GetDispatcher();

        Exception? failure = null;
        var completed = dispatcher.InvokeAsync(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }).Task;

        if (!completed.Wait(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("The UI test thread did not finish within two minutes.");

        if (failure != null)
            throw new InvalidOperationException($"Window failed to initialize: {failure.Message}", failure);
    }

    private static Dispatcher GetDispatcher()
    {
        lock (Gate)
        {
            if (_dispatcher != null) return _dispatcher;

            var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;

            // Background so it can't keep the test host alive once the run finishes.
            var thread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    var app = new Tarjem.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent(); // loads App.xaml's resource dictionaries
                }

                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Tarjem UI smoke tests",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(30)) || dispatcher == null)
                throw new TimeoutException("The UI test thread did not start.");

            _dispatcher = dispatcher;
            return dispatcher;
        }
    }
}
