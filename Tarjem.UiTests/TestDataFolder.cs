using System.IO;
using Tarjem.Services;
using Xunit;

namespace Tarjem.UiTests;

/// <summary>
/// Points every service at a throwaway folder for the whole test run.
///
/// These tests construct real MainWindow and WelcomeWindow instances, and those windows genuinely
/// save settings and read stored API keys - so without this, running the suite rewrote the
/// developer's own settings.json (it was caught having replaced a real window size with the test
/// harness's) and wrote a Windows autostart entry pointing at testhost.exe.
///
/// Applied through an assembly-level collection so it is in place before the first test - and
/// before any service caches a path - rather than depending on test ordering.
/// </summary>
public sealed class TestDataFolder : IDisposable
{
    public string Root { get; }

    public TestDataFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "tarjem-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        AppPaths.OverrideRoot = Root;
        AppPaths.EnsureInitialized();
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;

        try { Directory.Delete(Root, recursive: true); }
        catch { /* a window may still hold a log file open; the temp folder is disposable anyway */ }
    }
}

[CollectionDefinition(Name)]
public sealed class IsolatedDataCollection : ICollectionFixture<TestDataFolder>
{
    public const string Name = "isolated-data";
}
