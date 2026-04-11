using System;

namespace Clipthrough.Models;

public sealed class AppNotification
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public AppNotificationLevel Level { get; init; } = AppNotificationLevel.Information;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
