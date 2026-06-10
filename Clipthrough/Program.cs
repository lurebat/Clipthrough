using Avalonia;
using ReactiveUI.Avalonia;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Diagnostics;
using Velopack;

namespace Clipthrough;

sealed class Program
{
    private const string SingleInstanceMutexName = @"Local\Clipthrough.SingleInstance.v1";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Clipthrough.Diagnostics.TraceConfiguration.Initialize();
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            Trace.TraceError($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        CommandLineOptions.Parse(args);

        if (CommandLineOptions.ShowHelp)
        {
            Console.Write(CommandLineOptions.UsageText);
            return;
        }

        // Squirrel/Velopack hooks must run even if another copy holds the mutex (install/update may
        // spawn short-lived processes with --squirrel-* arguments). Let Velopack handle those first.
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Velopack initialization failed: {ex}");
        }

        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            Trace.TraceWarning("Clipthrough is already running. Exiting this instance.");
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Application terminated unexpectedly: {ex}");
        }
        finally
        {
            try { singleInstance.ReleaseMutex(); } catch { /* already released on exit */ }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI(_ => { })
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 256 * 1024 * 1024,
            });
#if DEBUG
        builder = builder.LogToTrace();
        builder = builder.WithDeveloperTools();
#endif
        return builder;
    }
}
