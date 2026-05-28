using System;
using System.Diagnostics;
using System.Linq;
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
    private IUpdateService? _updateService;
    private IAiTransformService? _aiTransformService;
    private IScriptingService? _scriptingService;
    private Clipthrough.Services.Search.IEmbeddingWorker? _embeddingWorker;
    private IDisposable? _embeddingWorkerCaptureSubscription;
    private IDisposable? _embeddingWorkerBatchSubscription;
    private IDisposable? _notificationSubscription;
    private bool _isExitRequested;
    private bool _hasShownTrayNotification;
    private int _incrementalPasteOffset = 0;
    private bool _firstOpenComplete;
    private readonly System.Collections.Generic.List<Avalonia.Input.KeyBinding> _customLocalKeyBindings = new();

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
            _updateService = Services.GetRequiredService<IUpdateService>();
            _aiTransformService = Services.GetRequiredService<IAiTransformService>();
            _scriptingService = Services.GetRequiredService<IScriptingService>();

            _mainWindow = desktop.MainWindow = new MainWindow(_systemInteractionService)
            {
                DataContext = mainWindowViewModel,
            };

            desktop.MainWindow.Opened += OnMainWindowOpened;
            desktop.MainWindow.Closing += OnMainWindowClosing;
            desktop.MainWindow.Closed += OnMainWindowClosed;
            desktop.MainWindow.PropertyChanged += OnMainWindowPropertyChanged;
            _settingsService.SettingsChanged += OnSettingsChanged;
            _notificationSubscription = _notificationService.Notifications
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(OnNotificationPublished);

            _embeddingWorker = Services.GetRequiredService<Clipthrough.Services.Search.IEmbeddingWorker>();
            // Don't start the embedding worker here — it's started in StartDatabaseAsync
            // after the DB is initialized and password is set.
            _embeddingWorkerCaptureSubscription = System.Reactive.Linq.Observable.Merge(
                    _clipboardMonitorService.CapturedClips,
                    _clipboardMonitorService.UpdatedClips)
                .Subscribe(_ => _embeddingWorker.Poke());
            var semanticSearch = Services.GetRequiredService<Clipthrough.Services.Search.ISemanticSearchService>();
            _embeddingWorkerBatchSubscription = _embeddingWorker.BatchCompleted
                .Subscribe((int count) => { _ = semanticSearch.RefreshCacheAsync(); });

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
        services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
        services.AddSingleton<IClipAngelImportService, ClipAngelImportService>();
        services.AddSingleton<IClipSampleDataService, ClipSampleDataService>();
        services.AddSingleton<IClipboardMonitorService, ClipboardMonitorService>();
        services.AddSingleton<IDragDropService, DragDropService>();
        services.AddSingleton<ICopilotAuthService, CopilotAuthService>();
        services.AddSingleton<IAiTransformService, AiTransformService>();
        services.AddSingleton<IScriptingService, ScriptingService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IOcrService, OcrService>();
        services.AddSingleton<IBackgroundOcrQueue, BackgroundOcrQueue>();
        services.AddSingleton<IBackgroundJobIndicator, BackgroundJobIndicator>();
        services.AddSingleton<Clipthrough.Services.Search.IEmbeddingService, Clipthrough.Services.Search.EmbeddingService>();
        services.AddSingleton<Clipthrough.Services.Search.IEmbeddingWorker, Clipthrough.Services.Search.EmbeddingWorker>();
        services.AddSingleton<Clipthrough.Services.Search.ISemanticSearchService, Clipthrough.Services.Search.SemanticSearchService>();
        services.AddSingleton<IRemoteControlService, RemoteControlService>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (_mainWindow?.DataContext is MainWindowViewModel vm)
        {
            vm.SetMainWindowVisible(true);
            if (_mainWindow is MainWindow mainWindow)
            {
                mainWindow.FocusClipOnNextActivation();
            }
        }

        // Avalonia fires `Opened` on every Show() after a Hide(). The hotkey
        // registration, update check, and remote-API kick-off are one-time
        // startup concerns — running them on every popup costs ~3s because
        // RegisterHotKey is synchronous and we now register a dozen filter
        // hotkeys by default. Settings changes already re-apply hotkeys via
        // OnSettingsChanged, so guarding by _firstOpenComplete is safe.
        if (_firstOpenComplete)
        {
            return;
        }

        _firstOpenComplete = true;
        UpdateGlobalHotKeyRegistration();
        _ = KickOffUpdateCheckAsync();
        _ = KickOffRemoteApiAsync();
    }

    private async Task KickOffRemoteApiAsync()
    {
        try
        {
            var svc = Services?.GetService<IRemoteControlService>();
            if (svc is null)
            {
                return;
            }
            await svc.ApplySettingsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Remote API startup failed: {ex}");
        }
    }

    private async Task KickOffUpdateCheckAsync()
    {
        try
        {
            var svc = Services?.GetService<IUpdateService>();
            if (svc is null)
            {
                return;
            }
            var result = await svc.CheckForUpdatesAsync().ConfigureAwait(false);
            if (result.HasUpdate && !string.IsNullOrWhiteSpace(result.Version))
            {
                PublishUpdateReadyNotification(result.Version!);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Update check failed: {ex}");
        }
    }

    /// <summary>
    /// Surfaces a downloaded update to the user with explicit consent actions.
    /// We deliberately do NOT shut the app down on the user's behalf — the
    /// running app stays put until the user clicks "Restart and install" or
    /// closes Clipthrough normally (in which case the on-exit handler swaps
    /// the binaries before relaunch is possible).
    /// </summary>
    private void PublishUpdateReadyNotification(string version)
    {
        if (_notificationService is null)
        {
            return;
        }

        var notification = new AppNotification
        {
            Title = $"Clipthrough update {version} ready",
            Message = "The new version is downloaded. Restart now to install, or it will be applied next time you close Clipthrough.",
            Level = AppNotificationLevel.Information,
            IsPersistent = true,
            Actions = new[]
            {
                new AppNotificationAction
                {
                    Label = "Restart and install",
                    ExecuteAsync = () =>
                    {
                        _updateService?.ApplyDownloadedUpdateAndRestart();
                        return Task.CompletedTask;
                    },
                },
                new AppNotificationAction
                {
                    Label = "Install on exit",
                    ExecuteAsync = () => Task.CompletedTask,
                },
            },
        };

        _notificationService.Publish(notification);
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
        _embeddingWorkerCaptureSubscription?.Dispose();
        _embeddingWorkerCaptureSubscription = null;
        _embeddingWorkerBatchSubscription?.Dispose();
        _embeddingWorkerBatchSubscription = null;
        _systemInteractionService?.UnregisterAllGlobalHotKeys();
        _ = Services.GetService<IBackgroundOcrQueue>()?.StopAsync();
        _ = Services.GetService<Clipthrough.Services.Search.IEmbeddingWorker>()?.StopAsync();
        _updateService?.ApplyDownloadedUpdateOnExit();
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
        foreach (var kb in _customLocalKeyBindings)
        {
            _mainWindow.KeyBindings.Remove(kb);
        }
        _customLocalKeyBindings.Clear();

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

        TryRegisterExtendedHotkey("copy-and-favorite",
            _settingsService.Current.EnableCopyAndFavoriteHotkey,
            _settingsService.Current.CopyAndFavoriteHotkey,
            CopyAndFavorite);
        TryRegisterExtendedHotkey("copy-and-sensitive",
            _settingsService.Current.EnableCopyAndSensitiveHotkey,
            _settingsService.Current.CopyAndSensitiveHotkey,
            CopyAndSensitive);
        TryRegisterExtendedHotkey("copy-without-saving",
            _settingsService.Current.EnableCopyWithoutSavingHotkey,
            _settingsService.Current.CopyWithoutSavingHotkey,
            CopyWithoutSaving);
        TryRegisterExtendedHotkey("paste-and-delete",
            _settingsService.Current.EnablePasteAndDeleteHotkey,
            _settingsService.Current.PasteAndDeleteHotkey,
            PasteAndDelete);
        TryRegisterExtendedHotkey("paste-and-favorite",
            _settingsService.Current.EnablePasteAndFavoriteHotkey,
            _settingsService.Current.PasteAndFavoriteHotkey,
            PasteAndFavorite);
        TryRegisterExtendedHotkey("paste-as-plain-text",
            _settingsService.Current.EnablePasteAsPlainTextHotkey,
            _settingsService.Current.PasteAsPlainTextHotkey,
            PasteAsPlainText);

        foreach (var binding in _settingsService.Current.CustomHotkeys)
        {
            if (string.IsNullOrWhiteSpace(binding.Gesture) || string.IsNullOrWhiteSpace(binding.Target))
            {
                continue;
            }
            if (!HotkeyGesture.TryParse(binding.Gesture, out var gesture, out _) || gesture is null)
            {
                Trace.TraceWarning($"Custom hotkey: failed to parse gesture '{binding.Gesture}' for target '{binding.Target}'");
                continue;
            }
            var localBinding = binding;
            if (binding.IsGlobal)
            {
                var registered = _systemInteractionService.TryRegisterGlobalHotKey(
                    _mainWindow,
                    "custom-" + localBinding.Id,
                    gesture,
                    () => ExecuteCustomHotkey(localBinding));
                if (!registered)
                {
                    Trace.TraceWarning($"Custom hotkey: failed to register global hotkey '{binding.Gesture}' for target '{binding.Target}'");
                }
            }
            else
            {
                var kb = new Avalonia.Input.KeyBinding
                {
                    Gesture = new Avalonia.Input.KeyGesture(gesture.Key, gesture.Modifiers),
                    Command = ReactiveUI.ReactiveCommand.Create(() => ExecuteCustomHotkey(localBinding)),
                };
                _mainWindow.KeyBindings.Add(kb);
                _customLocalKeyBindings.Add(kb);
            }
        }
    }

    private void TryRegisterExtendedHotkey(string id, bool enabled, string raw, Action callback)
    {
        if (!enabled || _mainWindow is null || _systemInteractionService is null)
        {
            return;
        }
        if (HotkeyGesture.TryParse(raw, out var gesture, out _) && gesture is not null)
        {
            _systemInteractionService.TryRegisterGlobalHotKey(_mainWindow, id, gesture, callback);
        }
    }

    private async void CopyAndFavorite()
    {
        if (_clipStoreService is null) return;
        try
        {
            // Give the clipboard monitor a moment to capture the latest clip, then
            // mark the newest one as favorite.
            await Task.Delay(150);
            var clip = await _clipStoreService.GetClipAtOffsetAsync(0);
            if (clip is not null)
            {
                await _clipStoreService.SetFavoriteAsync(clip.Id, true);
            }
        }
        catch (Exception ex) { Trace.TraceWarning($"CopyAndFavorite failed: {ex.Message}"); }
    }

    private async void CopyAndSensitive()
    {
        if (_clipStoreService is null) return;
        try
        {
            await Task.Delay(150);
            var clip = await _clipStoreService.GetClipAtOffsetAsync(0);
            if (clip is not null)
            {
                await _clipStoreService.SetSensitiveAsync(clip.Id, true);
            }
        }
        catch (Exception ex) { Trace.TraceWarning($"CopyAndSensitive failed: {ex.Message}"); }
    }

    private void CopyWithoutSaving()
    {
        _clipboardMonitorService?.SuppressNext();
    }

    private async void PasteAndDelete()
    {
        if (_clipStoreService is null || _systemInteractionService is null || _clipboardMonitorService is null) return;
        try
        {
            var clip = await _clipStoreService.GetClipAtOffsetAsync(0);
            if (clip is null) return;
            _clipboardMonitorService.SuppressNext();
            if (!string.IsNullOrEmpty(clip.Content))
            {
                await _systemInteractionService.CopyTextAsync(clip.Content);
            }
            await Task.Delay(120);
            _systemInteractionService.SimulatePasteKeystroke();
            await Task.Delay(120);
            await _clipStoreService.DeleteAsync(clip.Id);
        }
        catch (Exception ex) { Trace.TraceWarning($"PasteAndDelete failed: {ex.Message}"); }
    }

    private async void PasteAndFavorite()
    {
        if (_clipStoreService is null || _systemInteractionService is null || _clipboardMonitorService is null) return;
        try
        {
            var clip = await _clipStoreService.GetClipAtOffsetAsync(0);
            if (clip is null) return;
            _clipboardMonitorService.SuppressNext();
            if (!string.IsNullOrEmpty(clip.Content))
            {
                await _systemInteractionService.CopyTextAsync(clip.Content);
            }
            await _clipStoreService.MarkPastedAsync(clip.Id);
            await _clipStoreService.SetFavoriteAsync(clip.Id, true);
            await Task.Delay(120);
            _systemInteractionService.SimulatePasteKeystroke();
        }
        catch (Exception ex) { Trace.TraceWarning($"PasteAndFavorite failed: {ex.Message}"); }
    }

    private async void PasteAsPlainText()
    {
        if (_clipStoreService is null || _systemInteractionService is null || _clipboardMonitorService is null) return;
        try
        {
            var clip = await _clipStoreService.GetClipAtOffsetAsync(0);
            if (clip is null || string.IsNullOrEmpty(clip.Content)) return;
            _clipboardMonitorService.SuppressNext();
            // Always copy the plain-text Content field, regardless of stored HTML/RTF.
            await _systemInteractionService.CopyTextAsync(clip.Content);
            await _clipStoreService.MarkPastedAsync(clip.Id);
            await Task.Delay(120);
            _systemInteractionService.SimulatePasteKeystroke();
        }
        catch (Exception ex) { Trace.TraceWarning($"PasteAsPlainText failed: {ex.Message}"); }
    }

    private async void ExecuteCustomHotkey(CustomHotkeyBinding binding)
    {
        if (_clipStoreService is null || _systemInteractionService is null || _clipboardMonitorService is null) return;
        try
        {
            var target = binding.Target ?? string.Empty;
            var colon = target.IndexOf(':');
            if (colon <= 0)
            {
                Trace.TraceWarning($"Custom hotkey '{binding.Gesture}': invalid target format '{target}' (missing ':')");
                return;
            }
            var kind = target[..colon].Trim().ToLowerInvariant();
            var name = target[(colon + 1)..];

            // The "aiprompt:" target just opens the AI prompt dialog. It does not
            // need a recent clip and does not produce text to paste.
            if (kind == "aiprompt")
            {
                ExecuteAiPromptHotkey(name);
                return;
            }

            var clip = await _clipStoreService.GetClipAtOffsetAsync(0);
            if (clip is null || string.IsNullOrEmpty(clip.Content)) return;

            var input = clip.Content;
            string output;

            switch (kind)
            {
                case "builtin":
                    if (!Enum.TryParse<TextTransformation>(name, ignoreCase: true, out var tx) || tx == TextTransformation.None)
                    {
                        Trace.TraceWarning($"Custom hotkey '{binding.Gesture}': unknown builtin transform '{name}'");
                        return;
                    }
                    output = Clipthrough.Services.TextTransformationService.Apply(tx, input);
                    break;
                case "script":
                {
                    if (_scriptingService is null) return;
                    var script = _settingsService?.Current.UserScripts.FirstOrDefault(s =>
                        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (script is null)
                    {
                        Trace.TraceWarning($"Custom hotkey '{binding.Gesture}': user script '{name}' not found");
                        return;
                    }
                    output = await _scriptingService.EvaluateAsync(script.Code, input);
                    break;
                }
                case "ai":
                {
                    if (_aiTransformService is null || !_aiTransformService.IsConfigured) return;
                    var preset = _settingsService?.Current.AiPresets.FirstOrDefault(p =>
                        string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (preset is null)
                    {
                        Trace.TraceWarning($"Custom hotkey '{binding.Gesture}': AI preset '{name}' not found");
                        return;
                    }
                    output = await _aiTransformService.TransformAsync(preset.Prompt, input);
                    break;
                }
                case "prompt":
                {
                    if (_aiTransformService is null || !_aiTransformService.IsConfigured) return;
                    if (string.IsNullOrWhiteSpace(name)) return;
                    output = await _aiTransformService.TransformAsync(name, input);
                    break;
                }
                default:
                    Trace.TraceWarning($"Custom hotkey '{binding.Gesture}': unknown target kind '{kind}'");
                    return;
            }

            if (string.IsNullOrEmpty(output)) return;

            _clipboardMonitorService.SuppressNext();
            await _systemInteractionService.CopyTextAsync(output);
            if (binding.PasteAfter)
            {
                await Task.Delay(120);
                _systemInteractionService.SimulatePasteKeystroke();
            }
        }
        catch (Exception ex) { Trace.TraceError($"ExecuteCustomHotkey failed: {ex}"); }
    }

    private void ExecuteAiPromptHotkey(string spec)
    {
        if (_mainWindow?.DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Spec format: "<kind>[|<prefill text>]" where kind is one of
        // text, image-to-text, image-to-image, auto (default).
        var bar = spec.IndexOf('|');
        var kindRaw = (bar < 0 ? spec : spec.Substring(0, bar)).Trim();
        var prefill = bar < 0 ? null : spec.Substring(bar + 1);

        var resolved = kindRaw.ToLowerInvariant() switch
        {
            "text" or "text-to-text" or "t" => AiPresetKind.TextToText,
            "image-to-text" or "image2text" or "i2t" => AiPresetKind.ImageToText,
            "image-to-image" or "image2image" or "i2i" => AiPresetKind.ImageToImage,
            _ => (AiPresetKind?)null,
        };

        if (resolved is null && !string.IsNullOrEmpty(kindRaw) && !string.Equals(kindRaw, "auto", StringComparison.OrdinalIgnoreCase))
        {
            Trace.TraceWarning($"Unknown aiprompt kind '{kindRaw}', falling back to auto.");
        }

        EnsureMainWindowVisible();
        if (resolved is { } kind)
        {
            vm.OpenAiPromptWithPrefill(kind, prefill);
        }
        else
        {
            // "auto" — let the VM pick the default kind for the currently selected clip.
            Dispatcher.UIThread.Post(() =>
            {
                vm.OpenAiPromptCommand.Execute().Subscribe();
                if (!string.IsNullOrEmpty(prefill))
                {
                    vm.AiPromptInput = prefill;
                }
            });
        }
    }

    private void EnsureMainWindowVisible()
    {
        if (_mainWindow is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
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
            var sw = CommandLineOptions.LogPopupTimings ? Stopwatch.StartNew() : null;
            void Mark(string label)
            {
                if (sw is not null)
                {
                    Trace.TraceInformation($"[popup-timing] {label} @ {sw.ElapsedMilliseconds}ms");
                }
            }

            if (window.IsVisible && window.IsActive)
            {
                _systemInteractionService?.ClearTargetWindowCapture();
                if (window.DataContext is MainWindowViewModel viewModel)
                {
                    // Flip the visibility flag BEFORE clearing the search /
                    // hiding so that any queued/throttled refresh that lands
                    // after this point sees the window as hidden and skips
                    // touching Clips on the UI thread.
                    viewModel.SetMainWindowVisible(false);
                    _ = viewModel.ClearSearchFilterAsync(forceRefresh: false);
                }
                window.Hide();
                Mark("hide-complete");
                return;
            }

            // Record what window had focus so SimulatePasteKeystroke can restore it.
            _systemInteractionService?.CaptureTargetWindowForPaste();
            Mark("captured-target-window");

            PositionWindowNearCaret(window);
            Mark("positioned-near-caret");

            if (!window.IsVisible)
            {
                window.Show();
                Mark("window.Show");
            }

            RestoreAndActivateWindow(window);
            Mark("activated");

            if (window.DataContext is MainWindowViewModel vm)
            {
                // Flip visibility AFTER Show()+Activate so the window is on
                // screen before any refresh apply runs on the UI thread. If
                // clips changed while hidden, the VM kicks off one refresh.
                vm.SetMainWindowVisible(true);
                if (window is MainWindow mainWindow)
                {
                    mainWindow.FocusClipOnNextActivation();
                }
            }

            if (sw is not null)
            {
                sw.Stop();
                Trace.TraceInformation($"[popup-timing] show-total {sw.ElapsedMilliseconds}ms");
            }
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
            _systemInteractionService?.ClearTargetWindowCapture();
            if (_mainWindow.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.SetMainWindowVisible(false);
                _ = viewModel.ClearSearchFilterAsync(forceRefresh: false);
            }
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
            // Capture the current foreground window before Clipthrough steals focus.
            _systemInteractionService?.CaptureTargetWindowForPaste();

            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            RestoreAndActivateWindow(_mainWindow);
            if (_mainWindow is MainWindow mainWindow)
            {
                mainWindow.RestoreOwnedWindowsForCurrentState();
            }

            if (_mainWindow.DataContext is MainWindowViewModel vm)
            {
                vm.SetMainWindowVisible(true);
            }
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
