using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaApplication1.Database;
using AvaloniaApplication1.Services;
using AvaloniaApplication1.ViewModels;
using AvaloniaApplication1.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AvaloniaApplication1;

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
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IClipStoreService, ClipStoreService>();
        services.AddSingleton<IClipboardMonitorService, ClipboardMonitorService>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}