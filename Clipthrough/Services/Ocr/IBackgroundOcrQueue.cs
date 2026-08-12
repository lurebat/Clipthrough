using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IBackgroundOcrQueue
{
    IObservable<long> OcrCompleted { get; }

    IObservable<Unit> QueueChanged { get; }

    /// <summary>
    /// True between <see cref="Start"/> and <see cref="StopAsync"/>. Lets callers that
    /// quiesce the queue for a whole-database operation restore the state they found
    /// rather than unconditionally starting it.
    /// </summary>
    bool IsRunning { get; }

    void Start();

    Task StopAsync();

    void Enqueue(long clipId);

    Task EnqueueBacklogAsync(CancellationToken cancellationToken = default);
}
