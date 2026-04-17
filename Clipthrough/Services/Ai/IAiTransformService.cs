using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IAiTransformService
{
    bool IsConfigured { get; }

    Task<string> TransformAsync(string systemPrompt, string input, CancellationToken cancellationToken = default);

    Task<string> DescribeImageAsync(string systemPrompt, byte[] imageBytes, string mediaType, CancellationToken cancellationToken = default);

    Task<byte[]> EditImageAsync(string prompt, byte[] imageBytes, string mediaType, CancellationToken cancellationToken = default);
}
