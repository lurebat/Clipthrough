using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ReactiveUI.Avalonia;

[assembly: AvaloniaTestApplication(typeof(Clipthrough.Tests.TestAppBuilder))]

// PerAssembly, not PerTest. PerTest tears the Avalonia application down and
// rebuilds it between every test, and rebuilding is not safe against work that
// outlives a test: a pool-thread callback that touches Dispatcher.UIThread
// during the gap rebinds the dispatcher static to that thread, and the next
// setup then dies in DefaultRenderLoop.Add with "the calling thread cannot
// access this object". That surfaced as a Test Case Cleanup Failure blamed on
// whichever test happened to be running - roughly one run in three, on a
// different test each time. Nothing here needs a fresh application per test;
// each one builds its own database scope and view model.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Clipthrough.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Clipthrough.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .UseReactiveUI(_ => { });
}
