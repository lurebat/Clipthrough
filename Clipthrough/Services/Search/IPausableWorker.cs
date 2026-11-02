using System;
using System.Threading.Tasks;

namespace Clipthrough.Services.Search;

/// <summary>
/// A worker that owns its own lifetime (F-23): <c>Start</c>/<c>StopAsync</c>
/// record what the user wants, and <see cref="PauseAsync"/> holds it stopped
/// for a whole-database operation regardless of what the settings say
/// meanwhile. Releasing the last pause applies whatever is wanted by then.
/// </summary>
public interface IPausableWorker
{
    /// <summary>Stops the worker (waiting for it) until the returned handle is disposed.</summary>
    Task<IAsyncDisposable> PauseAsync();
}