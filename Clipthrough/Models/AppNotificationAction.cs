using System;
using System.Threading.Tasks;

namespace Clipthrough.Models;

public sealed class AppNotificationAction
{
    public required string Label { get; init; }

    public required Func<Task> ExecuteAsync { get; init; }
}
