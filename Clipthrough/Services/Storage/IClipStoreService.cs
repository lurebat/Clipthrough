using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipStoreService
{
    Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default);

    Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default);

    Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default);

    Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default);

    Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies every clip whose deferred sensitivity scan never completed
    /// (crash, SQLITE_BUSY, faulted enrichment task). Returns how many were
    /// classified. Run at startup so content cannot stay unflagged forever.
    /// </summary>
    Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert multiple clips in a single transaction for bulk import scenarios.</summary>
    Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default);

    Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default);

    Task DeleteAsync(long clipId, CancellationToken cancellationToken = default);

    Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default);

    Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default);

    Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default);

    Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default);

    Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default);

    Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default);

    Task<System.Collections.Generic.IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every clip left in the <c>running</c> OCR state to <c>pending</c>
    /// and reports how many were reset. A clip is marked <c>running</c> while the
    /// OCR queue holds it; if the queue is stopped mid-job (app exit, database
    /// maintenance) the marker outlives the work, and
    /// <see cref="TryClaimForOcrAsync"/> refuses to reclaim it — so the clip
    /// would never be OCR'd again. Called every time the queue enqueues its
    /// backlog, when nothing is in flight by definition.
    /// </summary>
    Task<int> ResetStalledOcrClaimsAsync(CancellationToken cancellationToken = default);

    Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default);

    Task<System.Collections.Generic.IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default);

    Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default);

    Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default);

    Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default);

    Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default);

    Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default);

    Task<System.Collections.Generic.IReadOnlyList<ClipEntry>> GetByIdsAsync(System.Collections.Generic.IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default);

    // -------- Semantic embeddings (sem-02) --------

    Task<System.Collections.Generic.IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every clip left in the <c>processing</c> embedding state to
    /// <c>pending</c> and reports how many were reset. Claiming a batch marks it
    /// <c>processing</c>, and <see cref="ClaimPendingEmbeddingsAsync"/> never
    /// re-selects that state, so a batch interrupted by a worker stop or a crash
    /// would never be embedded again while still counting as pending in the
    /// coverage readout. Called when the worker loop starts, at which point
    /// nothing is in flight.
    /// </summary>
    Task<int> ResetStalledEmbeddingClaimsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the given claimed clips to <c>pending</c> without counting a failed
    /// attempt, for when a batch could not be attempted at all (for example the
    /// ONNX model file is absent) and must stay eligible for a later retry.
    /// </summary>
    Task ReleaseEmbeddingClaimsAsync(System.Collections.Generic.IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default);

    Task SaveEmbeddingBatchAsync(System.Collections.Generic.IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default);

    Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default);

    Task<System.Collections.Generic.IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default);

    Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default);

    Task<System.Collections.Generic.IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a tiny no-op query to warm up the SQLCipher key derivation cache,
    /// the SQLite page cache, and the FTS5 index. Called once during startup
    /// so the first user-visible search isn't paying these one-time costs.
    /// </summary>
    Task PrewarmAsync(CancellationToken cancellationToken = default);
}
