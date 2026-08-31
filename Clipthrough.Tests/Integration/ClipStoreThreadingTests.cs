using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// SQLite has no asynchronous I/O. Microsoft's own guidance for the provider we
/// use is explicit: "Async ADO.NET methods will execute synchronously in
/// Microsoft.Data.Sqlite. Avoid calling them." So every <see cref="ClipStoreService"/>
/// body runs to completion on whatever thread called it, and awaiting one from
/// the UI thread freezes the window for exactly as long as the query takes.
///
/// That used to be the caller's problem: 38 call sites across the view model,
/// <c>App.axaml.cs</c> and the clipboard monitor each wrapped their own
/// <c>Task.Run</c>, and the invariant survived only as a line in
/// <c>.github/copilot-instructions.md</c> that a new call site could silently
/// ignore. The hop now lives in the service, so the guarantee is structural.
///
/// These two tests are deliberately paired and neither is sufficient alone:
///
/// * <see cref="EveryStoreMethod_HandsItsBodyToTheThreadPool"/> covers all 37
///   methods but asserts on shape.
/// * <see cref="StoreMethods_DoNotOpenTheirConnectionOnTheCallingThread"/>
///   asserts the real thread, but only for a representative sample and only at
///   the point the connection is opened.
///
/// The shape assertion is what makes the sample generalise; the thread assertion
/// is what stops the shape assertion being a proxy for nothing.
/// </summary>
public sealed class ClipStoreThreadingTests
{
    private const string OffloadHelperName = "RunOffCallerAsync";

    private static readonly MethodInfo[] TaskRunOverloads =
        typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(Task.Run))
            .Select(m => m.IsGenericMethodDefinition ? m.GetGenericMethodDefinition() : m)
            .ToArray();

    private static readonly MethodInfo[] OffloadHelpers =
        typeof(ClipStoreService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == OffloadHelperName)
            .Select(m => m.IsGenericMethodDefinition ? m.GetGenericMethodDefinition() : m)
            .ToArray();

    // ---------------------------------------------------------------- shape

    public static TheoryData<string> StoreMethodNames()
    {
        var data = new TheoryData<string>();
        foreach (var method in typeof(IClipStoreService).GetMethods())
        {
            if (typeof(Task).IsAssignableFrom(method.ReturnType))
            {
                data.Add(method.Name);
            }
        }

        return data;
    }

    /// <summary>
    /// Every method must be a thin wrapper that delegates to the offload helper,
    /// and must not itself be an async method.
    ///
    /// The second half is the load-bearing one. <see cref="IlCallScanner"/>
    /// deliberately follows an async method into its state machine, so
    /// <c>async Task Foo() { await Task.Run(...); /* then more inline work */ }</c>
    /// satisfies a bare "calls Task.Run" scan while still running the tail of its
    /// body on the caller. Requiring the absence of the state machine forces the
    /// whole body into the private <c>*CoreAsync</c> method, where the helper is
    /// the only way in.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreMethodNames))]
    public void EveryStoreMethod_HandsItsBodyToTheThreadPool(string methodName)
    {
        var target = ImplementationOf(methodName);

        Assert.Null(target.GetCustomAttribute<AsyncStateMachineAttribute>());

        var delegations = OffloadHelpers.Sum(helper => IlCallScanner.CountCallsIn(target, helper));
        Assert.True(
            delegations > 0,
            $"{methodName} does not delegate to {OffloadHelperName}, so its body runs on the caller's thread.");
    }

    /// <summary>
    /// The helper the wrappers delegate to is where the hop actually happens.
    /// Asserted separately because the per-method test above only proves the
    /// wrappers reach it.
    /// </summary>
    [Fact]
    public void TheOffloadHelper_UsesTaskRun()
    {
        // Without this the loop below passes vacuously the moment the helper is
        // renamed - and so does every delegation assertion above, for the same
        // reason. This is the guard that turns a rename into a red test.
        Assert.NotEmpty(OffloadHelpers);

        foreach (var helper in OffloadHelpers)
        {
            var hops = TaskRunOverloads.Sum(run => IlCallScanner.CountCallsIn(helper, run));
            Assert.True(hops > 0, $"{helper} does not call Task.Run, so nothing leaves the calling thread.");
        }
    }

    private static MethodInfo ImplementationOf(string interfaceMethodName)
    {
        var map = typeof(ClipStoreService).GetInterfaceMap(typeof(IClipStoreService));
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name == interfaceMethodName)
            {
                return map.TargetMethods[i];
            }
        }

        throw new InvalidOperationException($"{interfaceMethodName} is not implemented by ClipStoreService.");
    }

    // --------------------------------------------------------------- thread

    /// <summary>
    /// A representative slice - read, aggregate, write, delete, capture,
    /// maintenance, OCR and embedding - covering every distinct shape of body in
    /// the service. Not all 37: building arguments for the rest reflectively
    /// would be brittle, and the shape test above already covers them.
    /// </summary>
    public static TheoryData<string> SampledStoreCalls() =>
        ["SearchAsync", "GetOcrCoverageAsync", "SetFavoriteAsync", "DeleteAsync",
         "CaptureAsync", "ApplyMaintenanceAsync", "GetEmbeddingCoverageAsync", "PrewarmAsync"];

    /// <summary>
    /// The property itself: the body must not execute on the thread that called it.
    ///
    /// <see cref="SqliteConnectionFactory"/> reads <c>IStorageOptionsService.Current</c>
    /// once per connection, on the thread that opens it, and every method in the
    /// service opens a connection as its first act. That gives an observation
    /// point *inside* the body with no production change at all.
    ///
    /// The call is made from a thread this test creates, because a manually
    /// created thread is never a thread-pool thread. So "the connection opened
    /// somewhere else" is decidable rather than a coincidence: with the hop the
    /// two ids always differ, and without it they are always equal, since the
    /// fake-async ADO calls complete synchronously on the caller.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampledStoreCalls))]
    public async Task StoreMethods_DoNotOpenTheirConnectionOnTheCallingThread(string methodName)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        // Only the store's own factory records, so sensitivity and settings
        // traffic cannot contribute a thread id and mask an inline open.
        var recording = new ThreadRecordingStorageOptions(scope.StorageOptionsService);
        var store = new ClipStoreService(
            new SqliteConnectionFactory(recording),
            scope.SensitivityService,
            scope.SettingsService,
            scope.NotificationService);

        var seeded = await store.CaptureAsync(new ClipCaptureRequest
        {
            ContentText = "clipthrough-threading-probe",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("clipthrough-threading-probe"),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
        });
        Assert.NotNull(seeded);

        recording.Clear();
        var callerThreadId = InvokeOnADedicatedThread(() => Invoke(store, methodName, seeded!.Id));

        var observed = recording.ObservedThreads;
        Assert.True(observed.Count > 0, $"{methodName} never opened a connection, so this test proves nothing about it.");
        Assert.DoesNotContain(callerThreadId, observed);
    }

    private static Task Invoke(IClipStoreService store, string methodName, long clipId) => methodName switch
    {
        "SearchAsync" => store.SearchAsync(new ClipSearchFilters()),
        "GetOcrCoverageAsync" => store.GetOcrCoverageAsync(),
        "SetFavoriteAsync" => store.SetFavoriteAsync(clipId, true),
        "DeleteAsync" => store.DeleteAsync(clipId),
        "CaptureAsync" => store.CaptureAsync(new ClipCaptureRequest
        {
            ContentText = "second",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("second"),
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
        }),
        "ApplyMaintenanceAsync" => store.ApplyMaintenanceAsync(),
        "GetEmbeddingCoverageAsync" => store.GetEmbeddingCoverageAsync(),
        "PrewarmAsync" => store.PrewarmAsync(),
        _ => throw new InvalidOperationException($"No invocation defined for {methodName}."),
    };

    /// <summary>
    /// Runs <paramref name="call"/> on a fresh thread and returns that thread's id.
    /// Blocking it for the duration is the point: with the hop the body proceeds
    /// on the pool regardless, and without it the body has nowhere else to run.
    /// </summary>
    private static int InvokeOnADedicatedThread(Func<Task> call)
    {
        var threadId = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            try
            {
                call().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the store call never completed");
        failure?.Throw();
        return threadId;
    }

    /// <summary>
    /// Records the thread each connection is opened on, by observing the one
    /// property <see cref="SqliteConnectionFactory"/> reads while opening it.
    /// </summary>
    private sealed class ThreadRecordingStorageOptions(IStorageOptionsService inner) : IStorageOptionsService
    {
        private readonly ConcurrentQueue<int> _threads = new();

        public IReadOnlyCollection<int> ObservedThreads => _threads.ToArray();

        public void Clear() => _threads.Clear();

        public StorageOptions Current
        {
            get
            {
                _threads.Enqueue(Environment.CurrentManagedThreadId);
                return inner.Current;
            }
        }

        public bool HasSavedConfig => inner.HasSavedConfig;

        public bool DatabaseExists => inner.DatabaseExists;

        public Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default)
            => inner.SaveAsync(options, cancellationToken);

        public void SetInMemoryPassword(string password) => inner.SetInMemoryPassword(password);

        public Task<Microsoft.Data.Sqlite.SqliteException?> TryOpenWithPasswordAsync(string password, CancellationToken cancellationToken = default)
            => inner.TryOpenWithPasswordAsync(password, cancellationToken);

        public Task RekeyAsync(string currentPassword, string newPassword, bool rememberNewPassword, CancellationToken cancellationToken = default)
            => inner.RekeyAsync(currentPassword, newPassword, rememberNewPassword, cancellationToken);
    }
}
