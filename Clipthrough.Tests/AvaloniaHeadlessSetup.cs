using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ReactiveUI.Avalonia;

[assembly: AvaloniaTestApplication(typeof(Clipthrough.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Clipthrough.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Clipthrough.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .UseReactiveUI(_ => { });
}
