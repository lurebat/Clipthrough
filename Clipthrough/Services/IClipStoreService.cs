using System.Threading;
using System.Threading.Tasks;
using AvaloniaApplication1.Models;

namespace AvaloniaApplication1.Services;

public interface IClipStoreService
{
    Task SeedSampleDataAsync(CancellationToken cancellationToken = default);

    Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default);

    Task DeleteAsync(long clipId, CancellationToken cancellationToken = default);
}

