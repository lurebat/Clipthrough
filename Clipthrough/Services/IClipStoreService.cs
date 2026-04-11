using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipStoreService
{
    Task SeedSampleDataAsync(CancellationToken cancellationToken = default);

    Task<ClipEntry?> CaptureAsync(string content, ContentType contentType, string? sourceApp = null, CancellationToken cancellationToken = default);

    Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default);

    Task DeleteAsync(long clipId, CancellationToken cancellationToken = default);
}

