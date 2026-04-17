using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public sealed class BackgroundJobIndicator : IBackgroundJobIndicator
{
    private readonly object _lock = new();
    private readonly List<JobHandle> _jobs = new();

    public IReadOnlyList<string> ActiveLabels
    {
        get
        {
            lock (_lock)
            {
                var snapshot = new string[_jobs.Count];
                for (var i = 0; i < _jobs.Count; i++)
                {
                    snapshot[i] = _jobs[i].Label;
                }
                return snapshot;
            }
        }
    }

    public event EventHandler? Changed;

    public IDisposable Begin(string label)
    {
        var handle = new JobHandle(this, label ?? string.Empty);
        lock (_lock)
        {
            _jobs.Add(handle);
        }
        Raise();
        return handle;
    }

    public async Task TrackAsync(string label, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var _ = Begin(label);
        await work().ConfigureAwait(false);
    }

    public async Task<T> TrackAsync<T>(string label, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var _ = Begin(label);
        return await work().ConfigureAwait(false);
    }

    private void Remove(JobHandle handle)
    {
        lock (_lock)
        {
            _jobs.Remove(handle);
        }
        Raise();
    }

    private void Raise()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class JobHandle : IDisposable
    {
        private readonly BackgroundJobIndicator _owner;
        private int _disposed;

        public JobHandle(BackgroundJobIndicator owner, string label)
        {
            _owner = owner;
            Label = label;
        }

        public string Label { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _owner.Remove(this);
        }
    }
}
