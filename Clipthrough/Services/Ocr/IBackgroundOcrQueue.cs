using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IBackgroundOcrQueue
{
    IObservable<long> OcrCompleted { get; }

    IObservable<Unit> QueueChanged { get; }

    void Start();

    Task StopAsync();

    void Enqueue(long clipId);

    Task EnqueueBacklogAsync(CancellationToken cancellationToken = default);
}
