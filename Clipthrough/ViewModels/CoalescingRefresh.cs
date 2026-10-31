using System;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.ViewModels;

/// <summary>
/// Runs an async refresh so that triggers arriving while one is in flight
/// collapse into a single trailing run (F-25). The coverage chips are
/// refreshed per OCR queue event and per embedding batch, and each refresh is
/// a full-library aggregate - 3.5 s at 60k clips, 17.8 s encrypted - so
/// overlapping triggers used to stack up identical queries. The trailing run
/// keeps the last change visible: a change that lands mid-query is not lost.
/// </summary>
internal sealed class CoalescingRefresh(Func<Task> refresh)
{
    private const int Idle = 0;
    private const int Running = 1;
    private const int RunningWithPending = 2;

    private int _state;

    public async Task RunAsync()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state == Idle)
            {
                if (Interlocked.CompareExchange(ref _state, Running, Idle) == Idle)
                {
                    break;
                }

                continue;
            }

            if (state == RunningWithPending
                || Interlocked.CompareExchange(ref _state, RunningWithPending, Running) == Running)
            {
                return;
            }
        }

        while (true)
        {
            try
            {
                await refresh();
            }
            catch
            {
                Volatile.Write(ref _state, Idle);
                throw;
            }

            if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running)
            {
                return;
            }

            Volatile.Write(ref _state, Running);
        }
    }
}