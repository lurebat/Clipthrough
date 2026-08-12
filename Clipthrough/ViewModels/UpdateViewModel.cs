using System;
using System.Diagnostics;
using System.Reactive;
using System.Threading.Tasks;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

/// <summary>
/// Owns the application self-update interactions (check / restart-and-install),
/// extracted from <see cref="MainWindowViewModel"/> to keep that view model
/// focused. Status text is reported back through the <c>setStatus</c> callback
/// supplied by the host view model so the shared status bar stays a single
/// surface.
/// </summary>
public sealed class UpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _updateService;
    private readonly IBackgroundJobIndicator _jobIndicator;
    private readonly IAppNotificationService _notificationService;
    private readonly Action<string> _setStatus;

    public UpdateViewModel(
        IUpdateService updateService,
        IBackgroundJobIndicator jobIndicator,
        IAppNotificationService notificationService,
        Action<string> setStatus)
    {
        _updateService = updateService;
        _jobIndicator = jobIndicator;
        _notificationService = notificationService;
        _setStatus = setStatus;

        CheckForUpdateCommand = ReactiveCommand.CreateFromTask(CheckForUpdateAsync);
        CheckForUpdatesNowCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesNowAsync);
        RestartAndInstallUpdateCommand = ReactiveCommand.Create(RestartAndInstallUpdate);
        ObserveCommandErrors();
    }

    public ReactiveCommand<Unit, Unit> CheckForUpdateCommand { get; }

    /// <summary>
    /// User-initiated update check from Settings. Downloads the update if one is
    /// available and offers explicit "Restart and install" / "Install on exit".
    /// </summary>
    public ReactiveCommand<Unit, Unit> CheckForUpdatesNowCommand { get; }

    /// <summary>
    /// User-initiated "restart now to install the downloaded update". No-op with
    /// a status message when nothing is waiting to install.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RestartAndInstallUpdateCommand { get; }

    private async Task CheckForUpdateAsync()
    {
        _setStatus(AppText.CheckingForUpdateStatus);

        try
        {
            var result = await _jobIndicator.TrackAsync(
                AppText.CheckingForUpdateStatus,
                () => _updateService.CheckForUpdatesAsync(ignoreAutoUpdateDisabled: true));
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? AppText.UpdateCheckCompleteStatus
                : result.Message;

            _setStatus(message);
            if (result.HasUpdate && !string.IsNullOrWhiteSpace(result.Version))
            {
                // Mirror the background-check notification: surface explicit
                // "Restart and install" / "Install on exit" actions instead of
                // leaving the user with a plain info toast.
                _notificationService.Publish(new AppNotification
                {
                    Title = $"Clipthrough update {result.Version} ready",
                    Message = "Restart now to install, or it will be applied next time you close Clipthrough.",
                    Level = AppNotificationLevel.Information,
                    IsPersistent = true,
                    Actions = new[]
                    {
                        new AppNotificationAction
                        {
                            Label = "Restart and install",
                            ExecuteAsync = () =>
                            {
                                _updateService.ApplyDownloadedUpdateAndRestart();
                                return Task.CompletedTask;
                            },
                        },
                        new AppNotificationAction
                        {
                            Label = "Install on exit",
                            ExecuteAsync = () => Task.CompletedTask,
                        },
                    },
                });
            }
            else
            {
                _notificationService.PublishInfo(AppText.UpdateCheckCompleteTitle, message);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Update check failed: {ex}");
            _setStatus(AppText.FormatUpdateCheckFailed(ex.Message));
            _notificationService.PublishError(AppText.UpdateCheckFailedTitle, ex.Message);
        }
    }

    private async Task CheckForUpdatesNowAsync()
    {
        if (_updateService is null)
        {
            _setStatus("Updates are not available in this build.");
            return;
        }

        _setStatus("Checking for updates\u2026");
        try
        {
            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(true);
            if (result.HasUpdate && !string.IsNullOrWhiteSpace(result.Version))
            {
                _setStatus($"Update {result.Version} downloaded. Restart Clipthrough to install.");
                _notificationService.Publish(new AppNotification
                {
                    Title = $"Clipthrough update {result.Version} ready",
                    Message = "Restart now to install, or it will be applied next time you close Clipthrough.",
                    Level = AppNotificationLevel.Information,
                    IsPersistent = true,
                    Actions = new[]
                    {
                        new AppNotificationAction
                        {
                            Label = "Restart and install",
                            ExecuteAsync = () =>
                            {
                                _updateService.ApplyDownloadedUpdateAndRestart();
                                return Task.CompletedTask;
                            },
                        },
                        new AppNotificationAction
                        {
                            Label = "Install on exit",
                            ExecuteAsync = () => Task.CompletedTask,
                        },
                    },
                });
            }
            else
            {
                _setStatus(string.IsNullOrWhiteSpace(result.Message)
                    ? "Clipthrough is up to date."
                    : result.Message!);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Manual update check failed: {ex}");
            _setStatus(AppText.FormatErrorStatus(ex.Message));
        }
    }

    private void RestartAndInstallUpdate()
    {
        if (_updateService is null)
        {
            _setStatus("Updates are not available in this build.");
            return;
        }

        if (!_updateService.ApplyDownloadedUpdateAndRestart())
        {
            _setStatus("No downloaded update is waiting to install. Check for updates first.");
        }
    }
}
