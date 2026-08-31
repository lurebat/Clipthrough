using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// The clip library.
///
/// <para>
/// <b>Implementations must not run their work on the calling thread.</b> SQLite
/// has no asynchronous I/O, so a body that simply awaits the ADO.NET methods
/// runs start to finish on whoever called it — and these methods are called
/// from UI-thread command handlers. <see cref="ClipStoreService"/> satisfies
/// this by giving every method a <c>Task.Run</c> hop; callers therefore just
/// <c>await</c> and must not add one of their own.
/// </para>
/// <para>
/// Test doubles that complete synchronously are fine: nothing blocks, so there
/// is nothing to move. The contract binds implementations that actually touch
/// the database.
/// </para>
/// </summary>
public interface IClipStoreService
{
    /// <summary>
    /// The ids of clips that have just left the database, batched per operation
    /// and published only after the deleting transaction has committed.
    ///
    /// Deletion is not only user-initiated: the retention sweep runs after every
    /// capture and both capacity caps evict silently. Anything holding clip state
    /// outside the database - the in-memory semantic cache above all - has no
    /// other way to learn that rows disappeared.
    /// </summary>
    IObservable<IReadOnlyList<long>> ClipsRemoved { get; }

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

    /// <summary>
    /// Reads just the source-application icon blob for one clip.
    /// </summary>
    /// <remarks>
    /// List reads omit the icon (U12), so the list has to fetch it back for nearly every
    /// visible row - icons exist for almost every clip. Doing that through
    /// <see cref="GetByIdAsync"/> pulls all thirty columns including the image blob, so a
    /// page of text clips dragged megabytes of unrelated data across one connection per
    /// row. This reads one column.
    /// </remarks>
    Task<byte[]?> GetSourceAppIconAsync(long clipId, CancellationToken cancellationToken = default);

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
