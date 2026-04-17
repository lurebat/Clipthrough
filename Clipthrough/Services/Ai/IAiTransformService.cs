using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IAiTransformService
{
    bool IsConfigured { get; }

    Task<string> TransformAsync(string systemPrompt, string input, CancellationToken cancellationToken = default);
}
