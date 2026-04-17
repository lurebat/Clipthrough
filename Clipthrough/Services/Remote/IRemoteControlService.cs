using System;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IRemoteControlService : IAsyncDisposable
{
    bool IsRunning { get; }

    string? BaseUrl { get; }

    Task ApplySettingsAsync(CancellationToken cancellationToken = default);
}
