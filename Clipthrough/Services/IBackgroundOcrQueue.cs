using System;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IBackgroundOcrQueue
{
    IObservable<long> OcrCompleted { get; }

    void Start();

    Task StopAsync();

    void Enqueue(long clipId);

    Task EnqueueBacklogAsync(CancellationToken cancellationToken = default);
}
