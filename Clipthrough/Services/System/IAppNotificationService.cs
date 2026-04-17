using System;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IAppNotificationService
{
    IObservable<AppNotification> Notifications { get; }

    void Publish(AppNotification notification);

    void PublishInfo(string title, string message);

    void PublishWarning(string title, string message);

    void PublishError(string title, string message);
}
