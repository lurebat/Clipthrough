using System;
using System.Collections.Generic;

namespace Clipthrough.Models;

public sealed class AppNotification
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public AppNotificationLevel Level { get; init; } = AppNotificationLevel.Information;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsPersistent { get; init; }

    public Action? Activated { get; init; }

    public IReadOnlyList<AppNotificationAction> Actions { get; init; } = Array.Empty<AppNotificationAction>();
}
