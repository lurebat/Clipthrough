using Avalonia;
using ReactiveUI.Avalonia;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Diagnostics;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
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

        if (CommandLineOptions.IsHelpRequested(args))
        {
            Console.Write(CommandLineOptions.UsageText);
            return;
        }

        // Squirrel/Velopack hooks must run even if another copy holds the mutex (install/update may
        // spawn short-lived processes with --squirrel-* arguments). Let Velopack handle those first -
        // before the data folder too, so a bad --data-dir or CLIPTHROUGH_DATA_DIR can never block an
        // install, update or uninstall hook, and a hook never writes into the data folder.
        Exception? velopackFailure = null;
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            velopackFailure = ex;
        }

        // Before tracing, because the log file lives in the data folder. An unusable folder is
        // reported and ends startup: falling back would put the user's clips somewhere they did
        // not choose.
        var dataRoot = ResolveDataRootOrReport(() => AppDataRootResolver.ResolveForCurrentProcess(args), StartupErrorDialog.Show);
        if (dataRoot is null)
        {
            Environment.ExitCode = 1;
            return;
        }

        Clipthrough.Diagnostics.TraceConfiguration.Initialize(dataRoot.LogFilePath);
        Trace.TraceInformation($"Data folder: {dataRoot.Directory} ({dataRoot.Source}).");
        if (velopackFailure is not null)
        {
            Trace.TraceError($"Velopack initialization failed: {velopackFailure}");
        }

        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            Trace.TraceError($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        CommandLineOptions.Parse(args);

        // One Clipthrough at a time, whatever its data folder: the name does not include it.
        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            Trace.TraceWarning("Clipthrough is already running. Exiting this instance.");
            return;
        }

        try
        {
            BuildAvaloniaApp(dataRoot).StartWithClassicDesktopLifetime(args);
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

    /// <summary>
    /// The data root to run from, or null after <paramref name="report"/> has
    /// told the user why there is none.
    /// </summary>
    internal static AppDataRoot? ResolveDataRootOrReport(Func<AppDataRoot> resolve, Action<string, string> report)
    {
        try
        {
            var dataRoot = resolve();
            AppDataRootResolver.EnsureUsable(dataRoot);
            return dataRoot;
        }
        catch (AppDataRootException ex)
        {
            report(AppText.DataFolderErrorTitle, ex.Message);
            return null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => Configure(AppBuilder.Configure<App>());

    private static AppBuilder BuildAvaloniaApp(AppDataRoot dataRoot)
        => Configure(AppBuilder.Configure(() => new App(dataRoot)));

    private static AppBuilder Configure(AppBuilder builder)
    {
        builder = builder
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
