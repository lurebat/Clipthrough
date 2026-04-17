using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class AppNotificationService : IAppNotificationService, IDisposable
{
    private readonly Subject<AppNotification> _notifications = new();
    private bool _isDisposed;

    public IObservable<AppNotification> Notifications => _notifications.AsObservable();

    public void Publish(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_isDisposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            _notifications.OnNext(notification);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            _notifications.OnNext(notification);
        });
    }

    public void PublishInfo(string title, string message) => Publish(new AppNotification
    {
        Title = title,
        Message = message,
        Level = AppNotificationLevel.Information,
    });

    public void PublishWarning(string title, string message) => Publish(new AppNotification
    {
        Title = title,
        Message = message,
        Level = AppNotificationLevel.Warning,
    });

    public void PublishError(string title, string message) => Publish(new AppNotification
    {
        Title = title,
        Message = message,
        Level = AppNotificationLevel.Error,
    });

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifications.OnCompleted();
        _notifications.Dispose();
    }
}
