using Avalonia;
using ReactiveUI.Avalonia;
using System;
using System.Diagnostics;
using Clipthrough.Diagnostics;

namespace Clipthrough;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        TraceConfiguration.Initialize();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Application terminated unexpectedly: {ex}");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 256 * 1024 * 1024,
            });
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder;
    }
}
