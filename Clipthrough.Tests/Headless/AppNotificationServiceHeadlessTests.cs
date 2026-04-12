using System.Reactive.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Headless;

public sealed class AppNotificationServiceHeadlessTests
{
    [AvaloniaFact]
    public async Task Publish_FromBackgroundThread_NotifiesOnUiThread()
    {
        using var service = new AppNotificationService();
        AppNotification? received = null;
        var notifiedOnUiThread = false;

        using var subscription = service.Notifications.Subscribe(Observer.Create<AppNotification>(notification =>
        {
            received = notification;
            notifiedOnUiThread = Dispatcher.UIThread.CheckAccess();
        }));

        await Task.Run(() => service.PublishWarning("title", "message"));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(received);
        Assert.True(notifiedOnUiThread);
        Assert.Equal(AppNotificationLevel.Warning, received!.Level);
    }
}
