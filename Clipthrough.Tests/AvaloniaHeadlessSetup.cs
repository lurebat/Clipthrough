using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
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
    // Binding failures are logged, never thrown, so a binding that resolves to
    // nothing is invisible to an assertion on view-model state. Routing them to
    // Trace is what lets BindingErrorHeadlessTests see them.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<Clipthrough.App>()

            // The app's own font. Fluent asks for Inter first; without it the
            // headless platform falls back to a stub font whose glyphs are half
            // as wide again as Inter's, and every layout assertion about what
            // fits on a screen measured text that does not exist - a search row
            // "overflowed" a 1366 DIP window it fits with room to spare.
            .WithInterFont();

        // Layout captures need real pixels, which headless drawing never
        // produces. Opt-in only: Skia also really decodes images, which several
        // tests rely on the stub renderer not doing. (Layout/LayoutSurveyCapture.cs)
        if (Layout.LayoutSurveyCapture.OutputDirectory is not null)
        {
            builder = builder.UseSkia();
        }

        return builder
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = Layout.LayoutSurveyCapture.OutputDirectory is null,
            })
            .LogToTrace(LogEventLevel.Warning, LogArea.Binding)
            .UseReactiveUI(_ => { });
    }
}
