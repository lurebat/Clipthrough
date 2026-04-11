using System;

namespace Clipthrough.Models;

public sealed class SessionLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public AppNotificationLevel Level { get; init; } = AppNotificationLevel.Information;

    public required string Message { get; init; }
}
