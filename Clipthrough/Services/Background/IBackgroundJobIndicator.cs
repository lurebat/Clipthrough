using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IBackgroundJobIndicator
{
    IReadOnlyList<string> ActiveLabels { get; }

    event EventHandler? Changed;

    IDisposable Begin(string label);

    Task TrackAsync(string label, Func<Task> work);

    Task<T> TrackAsync<T>(string label, Func<Task<T>> work);
}
