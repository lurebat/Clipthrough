using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IClipSampleDataService
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}
