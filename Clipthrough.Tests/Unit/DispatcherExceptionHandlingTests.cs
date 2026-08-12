using System;
using System.Runtime.InteropServices;
using Clipthrough;
using Clipthrough.Localization;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The dispatcher's unhandled-exception hook used to mark every exception
/// handled and log a single trace line. That made any failure reaching it look
/// to the user exactly like a button that does nothing, and let the application
/// keep running - and keep writing to the clip database - after failures that
/// leave managed state undefined.
/// </summary>
public class DispatcherExceptionHandlingTests
{
    [Fact]
    public void AnOrdinaryFailure_IsHandledAndShownToTheUser()
    {
        var notifications = new TestNotificationService();

        var handled = App.TryHandleDispatcherException(
            new InvalidOperationException("converter blew up"), notifications);

        Assert.True(handled, "An ordinary UI failure should not take the application down.");
        Assert.NotNull(notifications.LastNotification);
        Assert.Equal(AppNotificationLevel.Error, notifications.LastNotification!.Level);
        Assert.Equal(AppText.UnexpectedErrorTitle, notifications.LastNotification.Title);
        Assert.Equal("converter blew up", notifications.LastNotification.Message);
    }

    [Fact]
    public void WithNoNotificationServiceYet_ItStillHandlesWithoutThrowing()
    {
        var handled = App.TryHandleDispatcherException(
            new InvalidOperationException("early startup"), notifications: null);

        Assert.True(handled);
    }

    public static TheoryData<Exception> ProcessLevelFailures() =>
    [
        new OutOfMemoryException(),
        new AccessViolationException(),
        new SEHException(),
        new AggregateException(new InvalidOperationException(), new OutOfMemoryException()),
        new InvalidOperationException("wrapper", new AccessViolationException()),
    ];

    [Theory]
    [MemberData(nameof(ProcessLevelFailures))]
    public void AProcessLevelFailure_IsNotHandled(Exception exception)
    {
        var notifications = new TestNotificationService();

        var handled = App.TryHandleDispatcherException(exception, notifications);

        Assert.False(
            handled,
            "Swallowing this leaves the process running with undefined state over the user's database.");
        Assert.Null(notifications.LastNotification);
    }

    public static TheoryData<Exception> RecoverableFailures() =>
    [
        new InvalidOperationException(),
        new NullReferenceException(),
        new TimeoutException(),
        // Thrown before allocating, so nothing is corrupted - despite deriving
        // from OutOfMemoryException.
        new InsufficientMemoryException(),
        new AggregateException(new InvalidOperationException(), new TimeoutException()),
    ];

    [Theory]
    [MemberData(nameof(RecoverableFailures))]
    public void ARecoverableFailure_IsHandled(Exception exception)
    {
        var handled = App.TryHandleDispatcherException(exception, new TestNotificationService());

        Assert.True(handled);
    }
}
