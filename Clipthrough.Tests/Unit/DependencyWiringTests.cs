using System.Linq;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Regression guards for DI wiring invariants that the container only enforces
/// at runtime (a violation is a hard startup crash, not a compile error).
/// </summary>
public sealed class DependencyWiringTests
{
    /// <summary>
    /// <see cref="StorageOptionsService"/> sits BENEATH <c>SqliteConnectionFactory</c>
    /// in the DI graph (the factory depends on it). The lifecycle operations
    /// (rekey/move/restore) need to quiesce the background workers, but taking
    /// those workers as constructor dependencies creates a cycle:
    /// StorageOptionsService -> IEmbeddingWorker -> IClipStoreService ->
    /// SqliteConnectionFactory -> StorageOptionsService, which MS.DI throws on
    /// while building the provider at startup. The service must instead resolve
    /// the workers lazily (via IServiceProvider) at call time.
    /// </summary>
    [Fact]
    public void StorageOptionsService_Constructors_DoNotDependOnWorkerServices()
    {
        var workerTypes = new[]
        {
            typeof(IClipboardMonitorService),
            typeof(IBackgroundOcrQueue),
            typeof(IEmbeddingWorker),
        };

        var offending = typeof(StorageOptionsService).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => workerTypes.Contains(t))
            .Distinct()
            .ToList();

        Assert.Empty(offending);
    }
}
