using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IClipAngelImportService
{
    bool IsSupported { get; }

    Task<ClipAngelImportPreview> PreviewAsync(string dbPath, CancellationToken cancellationToken = default);

    Task<ClipAngelImportResult> ImportAsync(
        string dbPath,
        System.IProgress<ClipAngelImportProgress>? progress,
        CancellationToken cancellationToken = default);
}

public sealed record ClipAngelImportPreview(
    int TotalClips,
    IReadOnlyDictionary<string, int> ClipsByType,
    System.DateTimeOffset? EarliestCreated,
    System.DateTimeOffset? LatestCreated);

public sealed record ClipAngelImportProgress(int Processed, int Total, string? CurrentType);

public sealed record ClipAngelImportResult(
    int Imported,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Errors);
