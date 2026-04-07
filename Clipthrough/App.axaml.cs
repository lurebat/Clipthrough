using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Clipthrough.Database;
using Clipthrough.Services;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Clipthrough;

public partial class App : Application
{
    public App()
    {
        Services = ConfigureServices();
    }

    public IServiceProvider Services { get; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services.GetRequiredService<DatabaseInitializer>().InitializeAsync().GetAwaiter().GetResult();
            Services.GetRequiredService<IClipStoreService>().SeedSampleDataAsync().GetAwaiter().GetResult();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<ISensitivityService, SensitivityService>();
        services.AddSingleton<ISystemInteractionService, SystemInteractionService>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IClipStoreService, ClipStoreService>();
        services.AddSingleton<IClipboardMonitorService, ClipboardMonitorService>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}