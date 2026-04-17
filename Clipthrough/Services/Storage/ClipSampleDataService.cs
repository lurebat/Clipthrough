using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public sealed class ClipSampleDataService : IClipSampleDataService
{
    public Task SeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
