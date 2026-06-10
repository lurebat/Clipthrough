using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Tests for U20 (bound + secure ClipAngel import) — file-size caps and early-rejection paths.
/// These tests exercise the import service's defensive bounds without requiring a real
/// ClipAngel database; they verify that hostile/oversized inputs are rejected before any
/// allocation or decryption attempt.
/// </summary>
public sealed class ClipAngelImportServiceTests
{
    // -----------------------------------------------------------------------
    // File-size cap tests (U20: cap file + decrypted size)
    // -----------------------------------------------------------------------

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ImportAsync_FileExceedsSizeCap_ThrowsInvalidDataException()
    {
        if (!OperatingSystem.IsWindows())
            return; // Import is Windows-only

        // 513 MB > 512 MB cap
        const long oversizedBytes = 513L * 1024 * 1024;
        var path = Path.Combine(Path.GetTempPath(), $"clipangel-test-{Guid.NewGuid():N}.bin");
        try
        {
            using (var fs = File.Create(path))
                fs.SetLength(oversizedBytes);

            var service = new ClipAngelImportService(new ThrowingClipStore());
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(path, progress: null, CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ImportAsync_FileTooSmall_ThrowsInvalidDataException()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A file shorter than 1024 bytes cannot contain a valid SQLite header.
        var path = Path.Combine(Path.GetTempPath(), $"clipangel-test-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[512]);
            var service = new ClipAngelImportService(new ThrowingClipStore());
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(path, progress: null, CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ImportAsync_MissingFile_ThrowsFileNotFoundException()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new ClipAngelImportService(new ThrowingClipStore());
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.ImportAsync(
                Path.Combine(Path.GetTempPath(), $"clipangel-does-not-exist-{Guid.NewGuid():N}.db"),
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ImportAsync_NotAClipAngelDatabase_ThrowsInvalidDataException()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A 2 KB file full of zeros: decrypts to something that does not match the
        // SQLite magic header ('S' at offset 0) so the service rejects it.
        var path = Path.Combine(Path.GetTempPath(), $"clipangel-test-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[2048]);
            var service = new ClipAngelImportService(new ThrowingClipStore());
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(path, progress: null, CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // -----------------------------------------------------------------------
    // Stub: all clip-store calls throw — ensures no batch is committed even
    // if size validation somehow passes in an unexpected code path.
    // -----------------------------------------------------------------------

    private sealed class ThrowingClipStore : IClipStoreService
    {
        public Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task PrewarmAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
