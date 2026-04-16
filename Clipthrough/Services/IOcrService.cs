using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IOcrService
{
    bool IsAvailable { get; }

    Task<OcrResult> ExtractTextAsync(byte[] imageBytes, string languages, CancellationToken cancellationToken = default);
}

public sealed record OcrResult(bool Success, string Text, string? Error);
