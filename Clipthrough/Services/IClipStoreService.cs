using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipStoreService
{
    Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default);

    Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default);

    Task DeleteAsync(long clipId, CancellationToken cancellationToken = default);

    Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default);

    Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default);

    Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default);

    Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default);

    Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default);

    Task<System.Collections.Generic.IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default);

    Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default);

    Task<System.Collections.Generic.IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default);

    Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default);

    Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default);

    Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default);

    Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default);
}
