using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Clipthrough.Database;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
using Clipthrough.Services.Platform;
using Clipthrough.ViewModels;
using Clipthrough.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Reactive.Linq;
using Avalonia.Styling;
using ReactiveUI;

namespace Clipthrough;

public partial class App : Application
{
    private Window? _mainWindow;
    private TrayIcon? _trayIcon;
    private ISystemInteractionService? _systemInteractionService;
    private ISettingsService? _settingsService;
    private IClipStoreService? _clipStoreService;
    private IClipboardMonitorService? _clipboardMonitorService;
    private IAppNotificationService? _notificationService;
    private IDisposable? _notificationSubscription;
    private bool _isExitRequested;
    private bool _hasShownTrayNotification;
    private int _incrementalPasteOffset = 0;

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
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            InitializeTrayIcon();

            var mainWindowViewModel = Services.GetRequiredService<MainWindowViewModel>();
            _systemInteractionService = Services.GetRequiredService<ISystemInteractionService>();
            _settingsService = Services.GetRequiredService<ISettingsService>();
            _clipStoreService = Services.GetRequiredService<IClipStoreService>();
            _clipboardMonitorService = Services.GetRequiredService<IClipboardMonitorService>();
            ApplyThemeMode(_settingsService.Current.ThemeMode);
            _notificationService = Services.GetRequiredService<IAppNotificationService>();

            _mainWindow = desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };

            desktop.MainWindow.Opened += OnMainWindowOpened;
            desktop.MainWindow.Closing += OnMainWindowClosing;
            desktop.MainWindow.Closed += OnMainWindowClosed;
            desktop.MainWindow.PropertyChanged += OnMainWindowPropertyChanged;
            _settingsService.SettingsChanged += OnSettingsChanged;
            _notificationSubscription = _notificationService.Notifications
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnNotificationPublished);

            StartApplicationAsync(mainWindowViewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async void StartApplicationAsync(MainWindowViewModel mainWindowViewModel)
    {
        try
        {
            await mainWindowViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Application startup failed: {ex}");
            mainWindowViewModel.ReportStartupFailure(ex);
        }
    }

    /// <summary>
    /// Global safety net for unhandled dispatcher exceptions.
    /// Logs the error and marks it as handled to prevent app termination.
    /// </summary>
    private static void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError($"Dispatcher unhandled exception (handled): {e.Exception}");
        e.Handled = true;
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Platform-specific services
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IDataProtectionService, WindowsDataProtectionService>();
            services.AddSingleton<ISourceApplicationResolver, WindowsSourceApplicationResolver>();
        }
        else
        {
            services.AddSingleton<IDataProtectionService, NoOpDataProtectionService>();
            services.AddSingleton<ISourceApplicationResolver, NullSourceApplicationResolver>();
        }

        services.AddSingleton<IStorageOptionsService, StorageOptionsService>();
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<ISensitivityService, SensitivityService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAppNotificationService, AppNotificationService>();
        services.AddSingleton<ISessionLogService>(_ => SessionLogService.Instance);
        services.AddSingleton<ISystemInteractionService, SystemInteractionService>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IClipExportService, ClipExportService>();
        services.AddSingleton<ISearchHistoryService, SearchHistoryService>();
        services.AddSingleton<IImageEditorService, ShareXImageEditorService>();
        services.AddSingleton<IClipStoreService, ClipStoreService>();
        services.AddSingleton<IClipSampleDataService, ClipSampleDataService>();
        services.AddSingleton<IClipboardMonitorService, ClipboardMonitorService>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        UpdateGlobalHotKeyRegistration();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.PropertyChanged -= OnMainWindowPropertyChanged;
        }

        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        _notificationSubscription?.Dispose();
        _notificationSubscription = null;
        _systemInteractionService?.UnregisterAllGlobalHotKeys();
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        if (_settingsService?.Current.CloseToTray != true)
        {
            return;
        }

        e.Cancel = true;
        HideMainWindowToTray();
    }

    private void OnMainWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isExitRequested || _mainWindow is null || e.Property != Window.WindowStateProperty)
        {
            return;
        }

        if (_settingsService?.Current.MinimizeToTray != true)
        {
            return;
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            HideMainWindowToTray();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        UpdateGlobalHotKeyRegistration();
        _systemInteractionService?.SyncStartWithWindows(e.StartWithWindows);
        ApplyThemeMode(e.ThemeMode);
    }

    public static void ApplyThemeMode(ThemeMode mode)
    {
        if (Current is null)
        {
            return;
        }

        Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void UpdateGlobalHotKeyRegistration()
    {
        if (_mainWindow is null || _systemInteractionService is null || _settingsService is null)
        {
            return;
        }

        _systemInteractionService.UnregisterAllGlobalHotKeys();

        if (_settingsService.Current.EnableToggleWindowHotkey
            && HotkeyGesture.TryParse(_settingsService.Current.ToggleWindowHotkey, out var windowHotkey, out _))
        {
            _systemInteractionService.TryRegisterGlobalHotKey(_mainWindow, windowHotkey!, ToggleMainWindowVisibility);
        }

        if (_settingsService.Current.EnableIncrementalPasteHotkey
            && HotkeyGesture.TryParse(_settingsService.Current.IncrementalPasteHotkey, out var incHotkey, out _))
        {
            _systemInteractionService.TryRegisterGlobalHotKey(_mainWindow, "incremental-paste", incHotkey!, IncrementalPaste);
        }

        if (_settingsService.Current.EnableDecrementalPasteHotkey
            && HotkeyGesture.TryParse(_settingsService.Current.DecrementalPasteHotkey, out var decHotkey, out _))
        {
            _systemInteractionService.TryRegisterGlobalHotKey(_mainWindow, "decremental-paste", decHotkey!, DecrementalPaste);
        }
    }

    private void ToggleMainWindowVisibility()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _incrementalPasteOffset = 0;
        ToggleMainWindowVisibility(_mainWindow);
    }

    private void IncrementalPaste()
    {
        _incrementalPasteOffset++;
        PasteAtOffset(_incrementalPasteOffset);
    }

    private void DecrementalPaste()
    {
        if (_incrementalPasteOffset > 0)
        {
            _incrementalPasteOffset--;
        }

        PasteAtOffset(_incrementalPasteOffset);
    }

    private async void PasteAtOffset(int offset)
    {
        if (_clipStoreService is null || _systemInteractionService is null || _clipboardMonitorService is null)
        {
            return;
        }

        try
        {
            var clip = await _clipStoreService.GetClipAtOffsetAsync(offset);
            if (clip is null)
            {
                _incrementalPasteOffset = Math.Max(0, offset - 1);
                return;
            }

            _clipboardMonitorService.SuppressNext();

            if (!string.IsNullOrEmpty(clip.Content))
            {
                await _systemInteractionService.CopyTextAsync(clip.Content);
            }

            await _clipStoreService.MarkPastedAsync(clip.Id);

            // Give the target window a moment to become ready and the clipboard
            // change to propagate before we synthesize the paste keystroke.
            await Task.Delay(120);
            _systemInteractionService.SimulatePasteKeystroke();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Incremental paste failed at offset {offset}: {ex.Message}");
        }
    }

    private void ToggleMainWindowVisibility(Window window)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (window.IsVisible && window.IsActive)
            {
                window.Hide();
                return;
            }

            PositionWindowNearCaret(window);

            if (!window.IsVisible)
            {
                window.Show();
            }

            RestoreAndActivateWindow(window);
        });
    }

    private void PositionWindowNearCaret(Window window)
    {
        var caretPosition = _systemInteractionService?.GetCaretScreenPosition();
        if (caretPosition is not { } caret)
        {
            return;
        }

        var screen = window.Screens.ScreenFromPoint(caret);
        if (screen is null)
        {
            return;
        }

        var bounds = screen.WorkingArea;
        var windowWidth = (int)window.Width;
        var windowHeight = (int)window.Height;
        if (windowWidth <= 0)
        {
            windowWidth = 800;
        }

        if (windowHeight <= 0)
        {
            windowHeight = 600;
        }

        // Position below and to the right of the caret, clamped to screen bounds
        var x = Math.Min(caret.X, bounds.Right - windowWidth);
        var y = caret.Y + 20;
        if (y + windowHeight > bounds.Bottom)
        {
            y = caret.Y - windowHeight - 10;
        }

        x = Math.Max(bounds.X, x);
        y = Math.Max(bounds.Y, y);

        window.Position = new PixelPoint(x, y);
    }

    private void HideMainWindowToTray()
    {
        if (_mainWindow is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            RestoreWindowState(_mainWindow);
            _mainWindow.Hide();

            if (_hasShownTrayNotification)
            {
                return;
            }

            _hasShownTrayNotification = true;
            Trace.TraceInformation("Clipthrough moved to the tray for the first time this session.");
            _notificationService?.Publish(new AppNotification
            {
                Title = AppText.TrayNotificationTitle,
                Message = AppText.TrayNotificationMessage,
                Level = AppNotificationLevel.Information,
                Activated = ShowMainWindowFromTray,
            });
        });
    }

    private void OnNotificationPublished(AppNotification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow is null || _mainWindow.IsVisible)
            {
                return;
            }

            _systemInteractionService?.ShowNotification(notification);
        });
    }

    private void ShowMainWindowFromTray()
    {
        if (_mainWindow is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            RestoreAndActivateWindow(_mainWindow);
        });
    }

    private static void RestoreAndActivateWindow(Window window)
    {
        RestoreWindowState(window);
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();

        if (window is MainWindow mainWindow)
        {
            mainWindow.FocusSearchBox();
        }
    }

    private static void RestoreWindowState(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindowFromTray();
    }

    private void OnTrayShowClicked(object? sender, EventArgs e)
    {
        ShowMainWindowFromTray();
    }

    private void OnTrayExitClicked(object? sender, EventArgs e)
    {
        _isExitRequested = true;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }

        _mainWindow?.Close();
    }

    private void InitializeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        var showMenuItem = new NativeMenuItem("Show Clipthrough");
        showMenuItem.Click += OnTrayShowClicked;

        var exitMenuItem = new NativeMenuItem("Exit");
        exitMenuItem.Click += OnTrayExitClicked;

        var menu = new NativeMenu();
        menu.Items.Add(showMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitMenuItem);

        WindowIcon? trayWindowIcon = null;
        var iconUri = new Uri("avares://Clipthrough/Assets/avalonia-logo.ico");
        using (var iconStream = AssetLoader.Open(iconUri))
        {
            trayWindowIcon = new WindowIcon(iconStream);
        }

        _trayIcon = new TrayIcon
        {
            Icon = trayWindowIcon,
            ToolTipText = "Clipthrough",
            IsVisible = true,
            Menu = menu,
        };
        _trayIcon.Clicked += OnTrayIconClicked;

        var trayIcons = new TrayIcons();
        trayIcons.Add(_trayIcon);
        SetValue(TrayIcon.IconsProperty, trayIcons);
    }
}
