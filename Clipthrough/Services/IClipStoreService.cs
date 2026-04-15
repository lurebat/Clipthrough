using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipStoreService
{
    Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default);

    Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default);

    Task DeleteAsync(long clipId, CancellationToken cancellationToken = default);

    Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default);

    Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default);

    Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default);

    Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default);
}
