using System;
using System.Collections.Generic;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface ISessionLogService
{
    IObservable<SessionLogEntry> Entries { get; }

    IReadOnlyList<SessionLogEntry> Snapshot();
}
