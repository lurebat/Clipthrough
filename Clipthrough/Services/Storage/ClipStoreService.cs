using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Clipthrough.Database;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

public sealed class ClipStoreService : IClipStoreService
{
    private const string ClipSelectColumns = """
            c.id,
            c.content,
            c.content_bytes,
            c.content_type,
            c.content_format,
            c.source_app,
            c.source_app_path,
            c.source_app_icon,
            c.hash,
            c.is_favorite,
            c.is_sensitive,
            c.copy_count,
            c.first_copied_at,
            c.last_copied_at,
            c.byte_size,
            c.image_width,
            c.image_height,
            c.source_window_title,
            c.source_url,
            c.is_pasted,
            c.paste_count,
            c.last_pasted_at,
            c.pinned_at,
            c.ocr_text,
            c.ocr_status,
            c.ocr_attempted_at,
            c.ocr_error,
            c.source_clip_id,
            c.transform_kind,
            c.import_kind
        """;

    /// <summary>
    /// Metadata-only column list for list/search queries. Omits <c>content_bytes</c> and
    /// <c>source_app_icon</c> (the two large BLOBs) to avoid materialising image data for
    /// every row. Column count and ordinal positions match <see cref="ClipSelectColumns"/>:
    /// index 2 is always <c>NULL</c> (ContentBytes), index 7 is a 0/1 presence flag
    /// (SourceAppIconAvailable). Use with <see cref="ReadClipMeta"/>.  (U12)
    /// </summary>
    private const string ClipListSelectColumns = """
            c.id,
            c.content,
            NULL,
            c.content_type,
            c.content_format,
            c.source_app,
            c.source_app_path,
            (CASE WHEN c.source_app_icon IS NOT NULL AND LENGTH(c.source_app_icon) > 0 THEN 1 ELSE 0 END),
            c.hash,
            c.is_favorite,
            c.is_sensitive,
            c.copy_count,
            c.first_copied_at,
            c.last_copied_at,
            c.byte_size,
            c.image_width,
            c.image_height,
            c.source_window_title,
            c.source_url,
            c.is_pasted,
            c.paste_count,
            c.last_pasted_at,
            c.pinned_at,
            c.ocr_text,
            c.ocr_status,
            c.ocr_attempted_at,
            c.ocr_error,
            c.source_clip_id,
            c.transform_kind,
            c.import_kind
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISensitivityService _sensitivityService;
    private readonly ISettingsService _settingsService;
    private readonly IAppNotificationService _notificationService;

    // Cached aggregate stats to avoid running COUNT/SUM on every refresh.
    private int _cachedTotalClipCount = -1;
    private int _cachedSensitiveClipCount = -1;
    private long _cachedTotalStoredBytes = -1;
    private DateTimeOffset? _cachedLastCapturedAt;
    private long _statsVersion;
    private long _cachedStatsSnapshotVersion = -1;

    public ClipStoreService(SqliteConnectionFactory connectionFactory, ISensitivityService sensitivityService, ISettingsService settingsService, IAppNotificationService notificationService)
    {
        _connectionFactory = connectionFactory;
        _sensitivityService = sensitivityService;
        _settingsService = settingsService;
        _notificationService = notificationService;
    }

    private void InvalidateStatsCache() => Interlocked.Increment(ref _statsVersion);

    public async Task<ClipEntry?> CaptureAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentBytes.Length == 0)
        {
            var reason = AppText.ClipCaptureFailedEmptyPayload;
            Trace.TraceWarning($"Skipped clipboard capture because payload was empty. type={request.ContentType} format={request.ContentFormat} source={request.SourceApp ?? "Unknown"}");
            _notificationService.PublishWarning(AppText.ClipCaptureFailedTitle, reason);
            return null;
        }

        if (request.ContentBytes.Length > _settingsService.Current.MaxClipSizeBytes)
        {
            var reason = AppText.FormatClipCaptureFailedTooLarge(request.ContentBytes.Length, _settingsService.Current.MaxClipSizeBytes);
            Trace.TraceWarning($"Skipped clipboard capture because payload exceeded limit. type={request.ContentType} format={request.ContentFormat} size={request.ContentBytes.Length} limit={_settingsService.Current.MaxClipSizeBytes} source={request.SourceApp ?? "Unknown"}");
            _notificationService.PublishWarning(AppText.ClipCaptureFailedTitle, reason);
            return null;
        }

        return await InsertClipAsync(request, cancellationToken);
    }

    public async Task<ClipEntry?> CaptureFastAsync(ClipCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentBytes.Length == 0)
        {
            var reason = AppText.ClipCaptureFailedEmptyPayload;
            Trace.TraceWarning($"Skipped fast clipboard capture because payload was empty. type={request.ContentType} format={request.ContentFormat} source={request.SourceApp ?? "Unknown"}");
            _notificationService.PublishWarning(AppText.ClipCaptureFailedTitle, reason);
            return null;
        }

        if (request.ContentBytes.Length > _settingsService.Current.MaxClipSizeBytes)
        {
            var reason = AppText.FormatClipCaptureFailedTooLarge(request.ContentBytes.Length, _settingsService.Current.MaxClipSizeBytes);
            Trace.TraceWarning($"Skipped fast clipboard capture because payload exceeded limit. type={request.ContentType} format={request.ContentFormat} size={request.ContentBytes.Length} limit={_settingsService.Current.MaxClipSizeBytes} source={request.SourceApp ?? "Unknown"}");
            _notificationService.PublishWarning(AppText.ClipCaptureFailedTitle, reason);
            return null;
        }

        return await InsertClipAsync(request, cancellationToken, scanSensitivity: false, applyMaintenance: false);
    }

    public async Task<ClipEntry?> UpdateDeferredContentAsync(long clipId, ClipCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentBytes.Length == 0 || request.ContentBytes.Length > _settingsService.Current.MaxClipSizeBytes)
        {
            return await GetByIdAsync(clipId, cancellationToken);
        }

        var contentText = BuildStoredContentText(request);
        var byteSize = request.ContentBytes.LongLength;

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE clips
            SET content = CASE WHEN $content IS NULL OR TRIM($content) = '' THEN content ELSE $content END,
                content_bytes = $contentBytes,
                content_type = $contentType,
                content_format = $contentFormat,
                byte_size = $byteSize,
                image_width = COALESCE($imageWidth, image_width),
                image_height = COALESCE($imageHeight, image_height),
                source_window_title = CASE WHEN $sourceWindowTitle IS NULL OR TRIM($sourceWindowTitle) = '' THEN source_window_title ELSE $sourceWindowTitle END,
                source_url = CASE WHEN $sourceUrl IS NULL OR TRIM($sourceUrl) = '' THEN source_url ELSE $sourceUrl END,
                embedding_status = CASE
                    WHEN embedding_status = 'excluded' THEN embedding_status
                    ELSE NULL
                END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue("$content", string.IsNullOrWhiteSpace(contentText) ? DBNull.Value : contentText);
        command.Parameters.AddWithValue("$contentBytes", request.ContentBytes);
        command.Parameters.AddWithValue("$contentType", request.ContentType.ToStorageValue());
        command.Parameters.AddWithValue("$contentFormat", request.ContentFormat.ToStorageValue());
        command.Parameters.AddWithValue("$byteSize", byteSize);
        command.Parameters.AddWithValue("$imageWidth", request.ImageWidth is { } width ? width : DBNull.Value);
        command.Parameters.AddWithValue("$imageHeight", request.ImageHeight is { } height ? height : DBNull.Value);
        command.Parameters.AddWithValue("$sourceWindowTitle", string.IsNullOrWhiteSpace(request.SourceWindowTitle) ? DBNull.Value : request.SourceWindowTitle);
        command.Parameters.AddWithValue("$sourceUrl", string.IsNullOrWhiteSpace(request.SourceUrl) ? DBNull.Value : request.SourceUrl);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed > 0)
        {
            InvalidateStatsCache();
        }

        return await GetClipByIdAsync(connection, clipId, cancellationToken);
    }

    public async Task<ClipEntry?> UpdateSourceAppIconAsync(long clipId, byte[] iconBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(iconBytes);
        if (iconBytes.Length == 0)
        {
            return await GetByIdAsync(clipId, cancellationToken);
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET source_app_icon = $sourceAppIcon WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue("$sourceAppIcon", iconBytes);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetClipByIdAsync(connection, clipId, cancellationToken);
    }

    /// <summary>
    /// Classifies every clip whose deferred sensitivity scan never completed.
    /// <para>
    /// <see cref="CaptureFastAsync"/> writes content to disk (and to the FTS
    /// index) before classification so the capture stays fast, relying on a
    /// follow-up <see cref="ApplySensitivityAsync"/>. If the app crashed, the
    /// write lost a race with SQLITE_BUSY, or the enrichment task faulted, that
    /// follow-up never ran and the clip stayed unflagged permanently. This
    /// recovery pass runs at startup and closes that window.
    /// </para>
    /// </summary>
    /// <returns>The number of clips that were classified.</returns>
    public async Task<int> ApplyPendingSensitivityAsync(CancellationToken cancellationToken = default)
    {
        List<long> pending;
        await using (var connection = _connectionFactory.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM clips WHERE sensitivity_scanned_at IS NULL ORDER BY id;";

            pending = [];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add(reader.GetInt64(0));
            }
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        Trace.TraceWarning($"Found {pending.Count} clip(s) whose sensitivity scan never completed; classifying now.");

        var classified = 0;
        foreach (var clipId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ApplySensitivityAsync(clipId, cancellationToken);
                classified++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave the marker null so the next startup retries this clip.
                Trace.TraceError($"Deferred sensitivity scan failed for clip {clipId}: {ex.Message}");
            }
        }

        return classified;
    }

    public async Task<ClipEntry?> ApplySensitivityAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        string? content;
        string? ocrText;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT content, ocr_text FROM clips WHERE id = $id;";
            read.Parameters.AddWithValue("$id", clipId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            content = reader.IsDBNull(0) ? null : reader.GetString(0);
            ocrText = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        // Image clips only ever store an "Image (WxH)" summary in `content`, so
        // anything sensitive in a screenshot lives in `ocr_text`. SetOcrResultAsync
        // already scans it; if this re-scan didn't, it would clear the matches and
        // is_sensitive flag OCR had set - silently declassifying the clip and
        // re-enabling embedding for it.
        var matches = new List<SensitivityMatch>();
        if (!string.IsNullOrWhiteSpace(content))
        {
            matches.AddRange(_sensitivityService.Scan(content));
        }

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            matches.AddRange(_sensitivityService.Scan(ocrText));
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM clip_sensitivity_matches WHERE clip_id = $id;";
            clear.Parameters.AddWithValue("$id", clipId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE clips
                SET is_sensitive = $isSensitive,
                    sensitivity_scanned_at = $scannedAt,
                    embedding_status = CASE
                        WHEN $isSensitive = 1 THEN 'excluded'
                        WHEN embedding_status = 'excluded' THEN NULL
                        ELSE embedding_status
                    END
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", clipId);
            update.Parameters.AddWithValue("$isSensitive", matches.Count > 0 ? 1 : 0);
            update.Parameters.AddWithValue("$scannedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddSensitivityMatchesAsync(connection, transaction, clipId, matches, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        InvalidateStatsCache();

        return await GetClipByIdAsync(connection, clipId, cancellationToken);
    }

    public async Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var hasSearch = !string.IsNullOrWhiteSpace(filters.SearchText);
        if (hasSearch && (filters.UseRegex || filters.CaseSensitive || filters.UseWildcard || filters.WholeWord || !HasFtsCompatibleSearchTerm(filters.SearchText)))
        {
            return await SearchInMemoryAsync(connection, filters, cancellationToken);
        }

        var fromClause = hasSearch
            ? "FROM clips c JOIN clips_fts ON clips_fts.rowid = c.id"
            : "FROM clips c";
        var whereClauses = BuildWhereClauses(filters, hasSearch);
        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var orderClause = hasSearch && filters.SortOption == ClipSortOption.BestMatching
            ? $"ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, bm25(clips_fts), COALESCE(c.last_copied_at, c.captured_at) DESC, c.id DESC"
            : BuildOrderClause(filters.SortOption);

        var items = new List<ClipEntry>(filters.Limit);

        // Fetch Limit+1 rows so we can detect "there are more" without a separate COUNT query. (U15)
        await using (var queryCommand = connection.CreateCommand())
        {
            queryCommand.CommandText = $"""
                SELECT {ClipListSelectColumns}
                {fromClause}
                {whereClause}
                {orderClause}
                LIMIT $limit OFFSET $offset;
                """;
            // Pass Limit+1 so we can detect whether more results exist.
            AddSearchParametersWithOvercount(queryCommand, filters, hasSearch);

            await using var reader = await queryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadClipMeta(reader));
            }
        }

        // If we fetched limit+1 items there are more beyond this page; trim the extra.
        var hasMore = items.Count > filters.Limit;
        if (hasMore) items.RemoveAt(items.Count - 1);

        await LoadSensitivityMatchesAsync(connection, items, cancellationToken);

        // Approximate total: exact when the page is partial; "at least Offset+Limit+1" when full. (U15)
        var totalMatchingCount = hasMore
            ? filters.Offset + filters.Limit + 1
            : filters.Offset + items.Count;

        // Use cached aggregate stats when available; refresh them only when version has changed.
        var versionBefore = Interlocked.Read(ref _statsVersion);
        if (_cachedTotalClipCount < 0 || versionBefore != _cachedStatsSnapshotVersion)
        {
            var totalClips = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken);
            var sensitiveClips = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips WHERE is_sensitive = 1;", cancellationToken);
            var storedBytes = await ExecuteScalarLongAsync(connection, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;", cancellationToken);
            var lastCapturedAtStr = await ExecuteScalarStringAsync(connection, "SELECT MAX(COALESCE(last_copied_at, captured_at)) FROM clips;", cancellationToken);

            // Only publish if no concurrent invalidation happened during the queries.
            if (Interlocked.Read(ref _statsVersion) == versionBefore)
            {
                _cachedTotalClipCount = totalClips;
                _cachedSensitiveClipCount = sensitiveClips;
                _cachedTotalStoredBytes = storedBytes;
                _cachedLastCapturedAt = ParseTimestamp(lastCapturedAtStr);
                _cachedStatsSnapshotVersion = versionBefore;
            }
        }

        return new ClipSearchResult
        {
            Items = items,
            TotalMatchingCount = totalMatchingCount,
            TotalClipCount = _cachedTotalClipCount,
            SensitiveClipCount = _cachedSensitiveClipCount,
            TotalStoredBytes = _cachedTotalStoredBytes,
            LastCapturedAt = _cachedLastCapturedAt,
        };
    }

    public async Task SetFavoriteAsync(long clipId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET is_favorite = $isFavorite WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetPinnedAsync(long clipId, bool isPinned, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET pinned_at = $pinnedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue(
            "$pinnedAt",
            isPinned
                ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        InvalidateStatsCache();
    }

    public async Task ClearSensitivityAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var clearMatchesCommand = connection.CreateCommand())
        {
            clearMatchesCommand.Transaction = transaction;
            clearMatchesCommand.CommandText = "DELETE FROM clip_sensitivity_matches WHERE clip_id = $id;";
            clearMatchesCommand.Parameters.AddWithValue("$id", clipId);
            await clearMatchesCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateClipCommand = connection.CreateCommand())
        {
            updateClipCommand.Transaction = transaction;
            updateClipCommand.CommandText = "UPDATE clips SET is_sensitive = 0 WHERE id = $id;";
            updateClipCommand.Parameters.AddWithValue("$id", clipId);
            await updateClipCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateStatsCache();
    }

    public async Task SetSensitiveAsync(long clipId, bool isSensitive, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE clips SET is_sensitive = $s WHERE id = $id;";
            command.Parameters.AddWithValue("$s", isSensitive ? 1 : 0);
            command.Parameters.AddWithValue("$id", clipId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (isSensitive)
        {
            // Purge any embedding and mark excluded so the worker won't re-queue.
            await using var purge = connection.CreateCommand();
            purge.Transaction = transaction;
            purge.CommandText = """
                DELETE FROM clip_embeddings WHERE clip_id = $id;
                UPDATE clips SET embedding_status = 'excluded' WHERE id = $id;
                """;
            purge.Parameters.AddWithValue("$id", clipId);
            await purge.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            // Allow re-embedding: clear excluded status so the worker picks it up again.
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE clips SET embedding_status = NULL WHERE id = $id AND embedding_status = 'excluded';";
            clear.Parameters.AddWithValue("$id", clipId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateStatsCache();
    }

    public async Task MarkPastedAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE clips
            SET is_pasted = 1,
                paste_count = paste_count + 1,
                last_pasted_at = $pastedAt,
                last_copied_at = $pastedAt,
                copy_count = copy_count + 1
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue("$pastedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        InvalidateStatsCache();
    }

    public async Task<bool> TryClaimForOcrAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE clips
            SET ocr_status = 'running'
            WHERE id = $id
              AND content_type = 'image'
              AND content_bytes IS NOT NULL
              AND (ocr_status IS NULL OR ocr_status = 'pending' OR ocr_status = 'failed' OR ocr_status = 'rerun');
            """;
        command.Parameters.AddWithValue("$id", clipId);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<bool> SetOcrResultAsync(long clipId, string ocrText, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int rows;
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE clips
                SET ocr_text = $text,
                    ocr_status = 'succeeded',
                    ocr_attempted_at = $at,
                    ocr_error = NULL
                WHERE id = $id;
                """;
            updateCommand.Parameters.AddWithValue("$id", clipId);
            updateCommand.Parameters.AddWithValue("$text", (object?)ocrText ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            rows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (rows > 0 && !string.IsNullOrWhiteSpace(ocrText))
        {
            var matches = _sensitivityService.Scan(ocrText);
            if (matches.Count > 0)
            {
                await using (var markCommand = connection.CreateCommand())
                {
                    markCommand.Transaction = transaction;
                    markCommand.CommandText = "UPDATE clips SET is_sensitive = 1 WHERE id = $id;";
                    markCommand.Parameters.AddWithValue("$id", clipId);
                    await markCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                await AddSensitivityMatchesAsync(connection, transaction, clipId, matches, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        if (rows > 0) InvalidateStatsCache();
        return rows > 0;
    }

    public async Task<bool> SetOcrFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE clips
            SET ocr_status = 'failed',
                ocr_attempted_at = $at,
                ocr_error = $err
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", clipId);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<IReadOnlyList<long>> GetPendingOcrClipIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM clips
            WHERE content_type = 'image'
              AND content_bytes IS NOT NULL
              AND (ocr_status IS NULL OR ocr_status = 'pending' OR ocr_status = 'failed' OR ocr_status = 'running' OR ocr_status = 'rerun')
            ORDER BY COALESCE(last_copied_at, captured_at) DESC
            LIMIT 500;
            """;
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<OcrCoverage> GetOcrCoverageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) AS eligible_total,
                COALESCE(SUM(CASE WHEN ocr_status = 'succeeded' THEN 1 ELSE 0 END), 0) AS succeeded_count,
                COALESCE(SUM(CASE WHEN ocr_status IS NULL OR ocr_status = 'pending' OR ocr_status = 'rerun' THEN 1 ELSE 0 END), 0) AS pending_count,
                COALESCE(SUM(CASE WHEN ocr_status = 'running' THEN 1 ELSE 0 END), 0) AS running_count,
                COALESCE(SUM(CASE WHEN ocr_status = 'failed' THEN 1 ELSE 0 END), 0) AS failed_count
            FROM clips
            WHERE content_type = 'image'
              AND content_bytes IS NOT NULL;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new OcrCoverage(0, 0, 0, 0, 0);
        }

        return new OcrCoverage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    public async Task<bool> MarkOcrForRerunAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE clips
            SET ocr_status = 'rerun'
            WHERE id = $id
              AND content_type = 'image'
              AND content_bytes IS NOT NULL
              AND ocr_status = 'succeeded';
            """;
        command.Parameters.AddWithValue("$id", clipId);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<IReadOnlyList<long>> MarkAllSucceededForRerunAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE clips
            SET ocr_status = 'rerun'
            WHERE content_type = 'image'
              AND content_bytes IS NOT NULL
              AND ocr_status = 'succeeded'
            RETURNING id;
            """;
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<ClipMaintenanceResult> ApplyMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        var purgedClipCount = 0;
        var settings = _settingsService.Current;
        var now = DateTimeOffset.UtcNow;

        if (settings.EnableSensitiveClipLifetime)
        {
            // Deliberately not preserving pinned/favorite clips: an expiring secret
            // outranks the user's intent to keep it around.
            purgedClipCount += await DeleteOlderThanAsync(connection, transaction, isSensitive: true, now.AddMinutes(-settings.SensitiveClipLifetimeMinutes), preserveUserKeptClips: false, cancellationToken);
        }

        if (settings.EnableNormalClipLifetime)
        {
            purgedClipCount += await DeleteOlderThanAsync(connection, transaction, isSensitive: false, now.AddDays(-settings.NormalClipLifetimeDays), preserveUserKeptClips: true, cancellationToken);
        }

        if (settings.EnableMaxEntryCount)
        {
            var totalClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken, transaction);
            var overflowCount = totalClipCount - settings.MaxEntryCount;
            if (overflowCount > 0)
            {
                purgedClipCount += await DeleteOldestAsync(connection, transaction, overflowCount, cancellationToken);
            }
        }

        if (settings.EnableMaxLibrarySize)
        {
            var totalStoredBytes = await ExecuteScalarLongAsync(connection, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;", cancellationToken, transaction);
            var maxBytes = settings.MaxLibrarySizeMegabytes * 1024L * 1024L;
            if (totalStoredBytes > maxBytes)
            {
                purgedClipCount += await DeleteUntilWithinSizeAsync(connection, transaction, totalStoredBytes, maxBytes, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateStatsCache();

        SweepDragTempFiles();

        return new ClipMaintenanceResult
        {
            PurgedClipCount = purgedClipCount,
            TotalClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken),
            TotalStoredBytes = await ExecuteScalarLongAsync(connection, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;", cancellationToken),
        };
    }

    // Drag-out writes temp PNG files under %TEMP%/Clipthrough/drag/ so drop
    // targets can read them after the source process returns from
    // DoDragDropAsync. We can't delete them synchronously without races, so
    // sweep here as part of routine maintenance. Files older than 1 hour are
    // safe to remove — any drop target will have read them long before then.
    private static void SweepDragTempFiles()
    {
        try
        {
            if (!Directory.Exists(DragDropService.DragTempDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var path in Directory.EnumerateFiles(DragDropService.DragTempDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    // Drop target may still hold the handle — skip and retry next sweep.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Drag temp sweep failed: {ex.Message}");
        }
    }

    public async Task RebuildSensitivityMatchesAsync(CancellationToken cancellationToken = default)
    {
        await _sensitivityService.ReloadAsync(cancellationToken);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var deleteMatchesCommand = connection.CreateCommand())
        {
            deleteMatchesCommand.Transaction = transaction;
            deleteMatchesCommand.CommandText = "DELETE FROM clip_sensitivity_matches;";
            await deleteMatchesCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var resetSensitiveCommand = connection.CreateCommand())
        {
            resetSensitiveCommand.Transaction = transaction;
            resetSensitiveCommand.CommandText = "UPDATE clips SET is_sensitive = 0;";
            await resetSensitiveCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var clips = new List<(long Id, string Content)>();
        await using (var clipsCommand = connection.CreateCommand())
        {
            clipsCommand.Transaction = transaction;
            clipsCommand.CommandText = "SELECT id, content FROM clips;";
            await using var reader = await clipsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                clips.Add((reader.GetInt64(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }

        foreach (var clip in clips)
        {
            var matches = _sensitivityService.Scan(clip.Content);
            if (matches.Count == 0)
            {
                continue;
            }

            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = "UPDATE clips SET is_sensitive = 1 WHERE id = $id;";
                updateCommand.Parameters.AddWithValue("$id", clip.Id);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await AddSensitivityMatchesAsync(connection, transaction, clip.Id, matches, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateStatsCache();
    }

    public async Task<ClipEntry?> GetByIdAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ClipSelectColumns}
            FROM clips c
            WHERE c.id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", clipId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadClip(reader);
    }

    public async Task<IReadOnlyList<ClipEntry>> GetByIdsAsync(IReadOnlyList<long> clipIds, CancellationToken cancellationToken = default)
    {
        if (clipIds is null || clipIds.Count == 0)
        {
            return Array.Empty<ClipEntry>();
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var placeholders = new string[clipIds.Count];
        await using var command = connection.CreateCommand();
        for (var i = 0; i < clipIds.Count; i++)
        {
            var name = $"$id{i}";
            placeholders[i] = name;
            command.Parameters.AddWithValue(name, clipIds[i]);
        }

        command.CommandText = $"""
            SELECT {ClipListSelectColumns}
            FROM clips c
            WHERE c.id IN ({string.Join(",", placeholders)});
            """;

        var map = new Dictionary<long, ClipEntry>(clipIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var clip = ReadClipMeta(reader);
            map[clip.Id] = clip;
        }

        await LoadSensitivityMatchesAsync(connection, map.Values.ToList(), cancellationToken);

        var ordered = new List<ClipEntry>(map.Count);
        foreach (var id in clipIds)
        {
            if (map.TryGetValue(id, out var entry))
            {
                ordered.Add(entry);
            }
        }
        return ordered;
    }

    public async Task<ClipEntry?> GetClipAtOffsetAsync(int offset, CancellationToken cancellationToken = default)
    {
        if (offset < 0)
        {
            return null;
        }

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ClipSelectColumns}
            FROM clips c
            ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, COALESCE(c.last_copied_at, c.captured_at) DESC, c.id DESC
            LIMIT 1 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$offset", offset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadClip(reader);
    }

    public async Task<BulkCaptureResult> CaptureBatchAsync(IReadOnlyList<ClipCaptureRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
            return new BulkCaptureResult(0, 0);

        int imported = 0, skipped = 0;
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.ContentBytes.Length == 0 || request.ContentBytes.Length > _settingsService.Current.MaxClipSizeBytes)
            {
                skipped++;
                continue;
            }

            var contentText = BuildStoredContentText(request);
            var hash = ComputeHash(request.ContentType, request.ContentFormat, request.ContentBytes);
            var matches = _sensitivityService.Scan(contentText);
            var capturedAt = (request.CapturedAtOverride ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
            var byteSize = request.ContentBytes.LongLength;

            long clipId;
            await using (var existingCommand = connection.CreateCommand())
            {
                existingCommand.Transaction = transaction;
                existingCommand.CommandText = "SELECT id FROM clips WHERE hash = $hash LIMIT 1;";
                existingCommand.Parameters.AddWithValue("$hash", hash);
                var existingId = await existingCommand.ExecuteScalarAsync(cancellationToken);

                if (existingId is not null && existingId != DBNull.Value)
                {
                    if (request.IncrementExistingCopyCount)
                    {
                        clipId = (long)existingId;
                        await using var updateCommand = connection.CreateCommand();
                        updateCommand.Transaction = transaction;
                        updateCommand.CommandText = """
                            UPDATE clips
                            SET content = CASE WHEN $content IS NULL OR TRIM($content) = '' THEN content ELSE $content END,
                                content_bytes = CASE WHEN $contentBytes IS NULL THEN content_bytes ELSE $contentBytes END,
                                content_format = $contentFormat,
                                source_app = CASE WHEN $sourceApp IS NULL OR TRIM($sourceApp) = '' THEN source_app ELSE $sourceApp END,
                                source_app_path = CASE WHEN $sourceAppPath IS NULL OR TRIM($sourceAppPath) = '' THEN source_app_path ELSE $sourceAppPath END,
                                source_app_icon = CASE WHEN $sourceAppIcon IS NULL THEN source_app_icon ELSE $sourceAppIcon END,
                                source_window_title = CASE WHEN $sourceWindowTitle IS NULL OR TRIM($sourceWindowTitle) = '' THEN source_window_title ELSE $sourceWindowTitle END,
                                source_url = CASE WHEN $sourceUrl IS NULL OR TRIM($sourceUrl) = '' THEN source_url ELSE $sourceUrl END,
                                is_favorite = CASE WHEN is_favorite = 1 OR $isFavorite = 1 THEN 1 ELSE 0 END,
                                is_sensitive = CASE WHEN is_sensitive = 1 OR $isSensitive = 1 THEN 1 ELSE 0 END,
                                sensitivity_scanned_at = $capturedAt,
                                captured_at = $lastCopiedAt,
                                first_copied_at = COALESCE(first_copied_at, captured_at, $lastCopiedAt),
                                last_copied_at = $lastCopiedAt,
                                copy_count = copy_count + 1,
                                byte_size = $byteSize,
                                image_width = COALESCE($imageWidth, image_width),
                                image_height = COALESCE($imageHeight, image_height)
                            WHERE id = $id;
                            """;
                        AddUpsertParameters(updateCommand, request, contentText, hash, matches.Count > 0, capturedAt, byteSize, clipId);
                        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                        imported++;
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }

                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO clips (
                        content, content_bytes, content_type, content_format,
                        source_app, source_app_path, source_app_icon, source_window_title, source_url,
                        hash, is_favorite, is_sensitive, captured_at, copy_count,
                        first_copied_at, last_copied_at, byte_size,
                        image_width, image_height, source_clip_id, transform_kind, import_kind,
                        sensitivity_scanned_at)
                    VALUES (
                        $content, $contentBytes, $contentType, $contentFormat,
                        $sourceApp, $sourceAppPath, $sourceAppIcon, $sourceWindowTitle, $sourceUrl,
                        $hash, $isFavorite, $isSensitive, $capturedAt, 1,
                        $capturedAt, $capturedAt, $byteSize,
                        $imageWidth, $imageHeight, $sourceClipId, $transformKind, $importKind,
                        $capturedAt);
                    SELECT last_insert_rowid();
                    """;
                AddUpsertParameters(insertCommand, request, contentText, hash, matches.Count > 0, capturedAt, byteSize);
                clipId = (long)(await insertCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }

            await AddSensitivityMatchesAsync(connection, transaction, clipId, matches, cancellationToken);
            imported++;
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateStatsCache();
        return new BulkCaptureResult(imported, skipped);
    }

    private async Task<ClipEntry?> InsertClipAsync(
        ClipCaptureRequest request,
        CancellationToken cancellationToken,
        bool scanSensitivity = true,
        bool applyMaintenance = true)
    {
        var contentText = BuildStoredContentText(request);
        var hash = ComputeHash(request.ContentType, request.ContentFormat, request.ContentBytes);
        var matches = scanSensitivity ? _sensitivityService.Scan(contentText) : Array.Empty<SensitivityMatch>();
        var capturedAt = (request.CapturedAtOverride ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
        var byteSize = request.ContentBytes.LongLength;

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();

        long clipId;
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = "SELECT id FROM clips WHERE hash = $hash LIMIT 1;";
            existingCommand.Parameters.AddWithValue("$hash", hash);
            var existingId = await existingCommand.ExecuteScalarAsync(cancellationToken);

            if (existingId is not null && existingId != DBNull.Value)
            {
                clipId = (long)existingId;

                if (request.IncrementExistingCopyCount)
                {
                    await using var updateCommand = connection.CreateCommand();
                    updateCommand.Transaction = transaction;
                    updateCommand.CommandText = """
                        UPDATE clips
                        SET content = CASE WHEN $content IS NULL OR TRIM($content) = '' THEN content ELSE $content END,
                            content_bytes = CASE WHEN $contentBytes IS NULL THEN content_bytes ELSE $contentBytes END,
                            content_format = $contentFormat,
                            source_app = CASE WHEN $sourceApp IS NULL OR TRIM($sourceApp) = '' THEN source_app ELSE $sourceApp END,
                            source_app_path = CASE WHEN $sourceAppPath IS NULL OR TRIM($sourceAppPath) = '' THEN source_app_path ELSE $sourceAppPath END,
                            source_app_icon = CASE WHEN $sourceAppIcon IS NULL THEN source_app_icon ELSE $sourceAppIcon END,
                            source_window_title = CASE WHEN $sourceWindowTitle IS NULL OR TRIM($sourceWindowTitle) = '' THEN source_window_title ELSE $sourceWindowTitle END,
                            source_url = CASE WHEN $sourceUrl IS NULL OR TRIM($sourceUrl) = '' THEN source_url ELSE $sourceUrl END,
                            is_favorite = CASE WHEN is_favorite = 1 OR $isFavorite = 1 THEN 1 ELSE 0 END,
                            is_sensitive = CASE WHEN is_sensitive = 1 OR $isSensitive = 1 THEN 1 ELSE 0 END,
                            captured_at = $lastCopiedAt,
                            first_copied_at = COALESCE(first_copied_at, captured_at, $lastCopiedAt),
                            last_copied_at = $lastCopiedAt,
                            copy_count = copy_count + 1,
                            byte_size = $byteSize,
                            image_width = COALESCE($imageWidth, image_width),
                            image_height = COALESCE($imageHeight, image_height),
                            sensitivity_scanned_at = COALESCE($sensitivityScannedAt, sensitivity_scanned_at)
                        WHERE id = $id;
                        """;
                    AddUpsertParameters(updateCommand, request, contentText, hash, matches.Count > 0, capturedAt, byteSize, clipId);
                    updateCommand.Parameters.AddWithValue("$sensitivityScannedAt", scanSensitivity ? capturedAt : (object)DBNull.Value);
                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            else
            {
                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO clips (
                        content,
                        content_bytes,
                        content_type,
                        content_format,
                        source_app,
                        source_app_path,
                        source_app_icon,
                        source_window_title,
                        source_url,
                        hash,
                        is_favorite,
                        is_sensitive,
                        captured_at,
                        copy_count,
                        first_copied_at,
                        last_copied_at,
                        byte_size,
                        image_width,
                        image_height,
                        source_clip_id,
                        transform_kind,
                        import_kind,
                        sensitivity_scanned_at)
                    VALUES (
                        $content,
                        $contentBytes,
                        $contentType,
                        $contentFormat,
                        $sourceApp,
                        $sourceAppPath,
                        $sourceAppIcon,
                        $sourceWindowTitle,
                        $sourceUrl,
                        $hash,
                        $isFavorite,
                        $isSensitive,
                        $capturedAt,
                        1,
                        $capturedAt,
                        $capturedAt,
                        $byteSize,
                        $imageWidth,
                        $imageHeight,
                        $sourceClipId,
                        $transformKind,
                        $importKind,
                        $sensitivityScannedAt);
                    SELECT last_insert_rowid();
                    """;
                AddUpsertParameters(insertCommand, request, contentText, hash, matches.Count > 0, capturedAt, byteSize);
                insertCommand.Parameters.AddWithValue("$sensitivityScannedAt", scanSensitivity ? capturedAt : (object)DBNull.Value);
                clipId = (long)(await insertCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }
        }

        await AddSensitivityMatchesAsync(connection, transaction, clipId, matches, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        InvalidateStatsCache();
        if (applyMaintenance && !request.SkipPostInsertMaintenance)
        {
            await ApplyMaintenanceAsync(cancellationToken);
        }
        return await GetClipByIdAsync(connection, clipId, cancellationToken);
    }

    private async Task<ClipSearchResult> SearchInMemoryAsync(SqliteConnection connection, ClipSearchFilters filters, CancellationToken cancellationToken)
    {
        // Build the single regex for this search ONCE here — not per row. (U13)
        // UseRegex / UseWildcard / WholeWord all become a single Regex reference.
        var regex = filters.UseRegex
            ? BuildSearchRegex(filters.SearchText, filters.CaseSensitive)
            : filters.UseWildcard
                ? BuildWildcardRegex(filters.SearchText, filters.CaseSensitive, filters.WholeWord)
                : filters.WholeWord
                    ? BuildWholeWordRegex(filters.SearchText, filters.CaseSensitive)
                    : null;

        var items = new List<ClipEntry>(filters.Limit);
        var whereClauses = BuildWhereClauses(filters, hasSearch: false);
        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var totalMatchingCount = 0;
        // Stop counting at Offset+Limit+1 so we know there are more without a full table scan. (U15)
        var countCap = filters.Offset + filters.Limit + 1;

        await using (var queryCommand = connection.CreateCommand())
        {
            // Use metadata-only columns (no content_bytes / source_app_icon BLOBs). (U12)
            // Ordering has exactly one authority - BuildOrderClause. This path
            // used to hardcode the MostRecent clause, so the sort dropdown
            // silently stopped working the moment the user ticked Regex or Case
            // (which is what diverts us here in the first place).
            queryCommand.CommandText = $"""
                SELECT {ClipListSelectColumns}
                FROM clips c
                {whereClause}
                {BuildOrderClause(filters.SortOption)};
                """;
            AddSearchParameters(queryCommand, filters, hasSearch: false);

            await using var reader = await queryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var clip = ReadClipMeta(reader);
                if (!MatchesSearch(clip, filters, regex))
                {
                    continue;
                }

                totalMatchingCount++;
                if (totalMatchingCount <= filters.Offset)
                {
                    continue;
                }

                if (items.Count < filters.Limit)
                {
                    items.Add(clip);
                }

                // Stop scanning once we've confirmed "at least one more beyond this page". (U15)
                if (totalMatchingCount >= countCap) break;
            }
        }

        await LoadSensitivityMatchesAsync(connection, items, cancellationToken);

        return new ClipSearchResult
        {
            Items = items,
            TotalMatchingCount = totalMatchingCount,
            TotalClipCount = _cachedTotalClipCount >= 0 ? _cachedTotalClipCount : await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken),
            SensitiveClipCount = _cachedSensitiveClipCount >= 0 ? _cachedSensitiveClipCount : await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips WHERE is_sensitive = 1;", cancellationToken),
            TotalStoredBytes = _cachedTotalStoredBytes >= 0 ? _cachedTotalStoredBytes : await ExecuteScalarLongAsync(connection, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;", cancellationToken),
            LastCapturedAt = _cachedLastCapturedAt,
        };
    }

    /// <summary>
    /// Returns true when <paramref name="clip"/> matches the current search filters.
    /// <paramref name="regex"/> must already be built by the caller ONCE per search —
    /// never build a new <see cref="Regex"/> inside this method. (U13)
    /// Covers the same five columns as the FTS index: content, source_app,
    /// source_window_title, source_url, ocr_text. (U13)
    /// </summary>
    private static bool MatchesSearch(ClipEntry clip, ClipSearchFilters filters, Regex? regex)
    {
        if (string.IsNullOrWhiteSpace(filters.SearchText))
        {
            return true;
        }

        // regex covers UseRegex, UseWildcard, and WholeWord — all pre-built by the caller.
        if (regex is not null)
        {
            return IsRegexMatch(clip, regex);
        }

        // Plain-text token search over the same 5 columns as FTS.
        var comparison = filters.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var tokens = filters.SearchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens.All(token =>
            clip.Content.Contains(token, comparison) ||
            (!string.IsNullOrWhiteSpace(clip.SourceApp) && clip.SourceApp.Contains(token, comparison)) ||
            (!string.IsNullOrWhiteSpace(clip.SourceWindowTitle) && clip.SourceWindowTitle.Contains(token, comparison)) ||
            (!string.IsNullOrWhiteSpace(clip.SourceUrl) && clip.SourceUrl.Contains(token, comparison)) ||
            (!string.IsNullOrWhiteSpace(clip.OcrText) && clip.OcrText.Contains(token, comparison)));
    }

    private static Regex BuildWildcardRegex(string pattern, bool caseSensitive, bool wholeWord)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);
        if (wholeWord)
        {
            escaped = $@"\b{escaped}\b";
        }

        var options = RegexOptions.Singleline | RegexOptions.NonBacktracking;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(escaped, options);
    }

    private static Regex BuildWholeWordRegex(string searchText, bool caseSensitive)
    {
        var tokens = searchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pattern = string.Join("|", tokens.Select(static t => $@"\b{Regex.Escape(t)}\b"));
        var options = RegexOptions.Singleline | RegexOptions.NonBacktracking;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options);
    }

    /// <summary>
    /// Builds the SQL WHERE clauses for a filter set. The structural subset
    /// here (content types, favorites, sensitive, pasted) is mirrored in
    /// <see cref="Clipthrough.Models.ClipStructuralFilter"/> so the UI can test
    /// a single clip without a round trip - keep the two in step.
    /// </summary>
    private static List<string> BuildWhereClauses(ClipSearchFilters filters, bool hasSearch)
    {
        var clauses = new List<string>();

        if (hasSearch && !filters.UseRegex)
        {
            clauses.Add("clips_fts MATCH $search");
        }

        if (filters.ContentTypes is { Count: > 0 } types)
        {
            var placeholders = new System.Collections.Generic.List<string>(types.Count);
            var i = 0;
            foreach (var _ in types)
            {
                placeholders.Add($"$contentType{i++}");
            }
            clauses.Add("c.content_type IN (" + string.Join(", ", placeholders) + ")");
        }

        if (filters.FavoritesOnly)
        {
            clauses.Add("c.is_favorite = 1");
        }

        if (filters.SensitiveOnly)
        {
            clauses.Add("c.is_sensitive = 1");
        }

        if (filters.PastedOnly)
        {
            clauses.Add("c.is_pasted = 1");
        }

        return clauses;
    }

    /// <summary>
    /// The single authority for list ordering. Internal rather than private so
    /// the index-coverage test can assert each arm's query plan against the
    /// real clause instead of a copy that could drift out of sync.
    /// </summary>
    internal static string BuildOrderClause(ClipSortOption sortOption) => sortOption switch
    {
        ClipSortOption.MostRecent => "ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, COALESCE(c.last_copied_at, c.captured_at) DESC, c.id DESC",
        ClipSortOption.OldestFirst => "ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, COALESCE(c.last_copied_at, c.captured_at) ASC, c.id ASC",
        ClipSortOption.MostPasted => "ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, c.paste_count DESC, c.id DESC",
        // Leading with the same bounded prefix that idx_clips_alpha_order
        // stores lets SQLite order from the index instead of sorting the whole
        // library. It is order-equivalent to plain "c.content ASC": when two
        // prefixes differ, the first differing character is inside the prefix,
        // so the prefix comparison already agrees with the full one; when they
        // match, c.content breaks the tie exactly as before.
        ClipSortOption.Alphabetical => "ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, substr(c.content, 1, 64) ASC, c.content ASC, c.id ASC",
        ClipSortOption.LargestFirst => "ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, c.byte_size DESC, c.id DESC",
        ClipSortOption.BestMatching => "ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, COALESCE(c.last_copied_at, c.captured_at) DESC, c.id DESC",
        _ => "ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, COALESCE(c.last_copied_at, c.captured_at) DESC, c.id DESC",
    };

    private static void AddSearchParameters(SqliteCommand command, ClipSearchFilters filters, bool hasSearch)
    {
        if (hasSearch && !filters.UseRegex)
        {
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText, filters.UseFuzzy));
        }

        AddContentTypeParameters(command, filters);

        command.Parameters.AddWithValue("$limit", filters.Limit);
        command.Parameters.AddWithValue("$offset", filters.Offset);
    }

    /// <summary>
    /// Like <see cref="AddSearchParameters"/> but adds <c>Limit+1</c> as the SQL LIMIT so the
    /// caller can detect "has more results beyond this page" without a separate COUNT query. (U15)
    /// </summary>
    private static void AddSearchParametersWithOvercount(SqliteCommand command, ClipSearchFilters filters, bool hasSearch)
    {
        if (hasSearch && !filters.UseRegex)
        {
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText, filters.UseFuzzy));
        }

        AddContentTypeParameters(command, filters);

        command.Parameters.AddWithValue("$limit", filters.Limit + 1);
        command.Parameters.AddWithValue("$offset", filters.Offset);
    }

    private static bool HasFtsCompatibleSearchTerm(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return false;
        }

        var tokens = searchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(static t => t.Length >= 3);
    }

    private static string BuildFtsExpression(string searchText, bool useFuzzy = false)
    {
        var tokens = searchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static t => t.Length >= 3)
            .ToArray();

        if (tokens.Length == 0)
        {
            return "*";
        }

        // The trigram tokenizer indexes 3-char shingles, so a phrase query matches
        // any substring of the indexed text. No prefix '*' suffix is needed (and it
        // wouldn't be meaningful against 3-gram shingles anyway).
        string Quote(string t) => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        if (!useFuzzy)
        {
            return string.Join(" AND ", tokens.Select(Quote));
        }

        // Fuzzy: OR between tokens and between 1-char deletion variants so that
        // "exammple" can still match "example". Each token becomes
        // ("tok" OR "tk" OR "ok" OR ...).
        var parts = new List<string>();
        foreach (var token in tokens)
        {
            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { token };
            if (token.Length > 3)
            {
                for (var i = 0; i < token.Length; i++)
                {
                    var v = token.Remove(i, 1);
                    if (v.Length >= 3)
                    {
                        variants.Add(v);
                    }
                }
            }

            parts.Add("(" + string.Join(" OR ", variants.Select(Quote)) + ")");
        }

        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// Reads a full <see cref="ClipEntry"/> from a row produced by <see cref="ClipSelectColumns"/>.
    /// Sets <see cref="ClipEntry.SourceAppIconAvailable"/> based on whether icon bytes were loaded.
    /// </summary>
    private static ClipEntry ReadClip(SqliteDataReader reader)
    {
        var lastCopiedAt = ParseTimestamp(reader.IsDBNull(13) ? null : reader.GetString(13))
            ?? DateTimeOffset.UtcNow;
        var firstCopiedAt = ParseTimestamp(reader.IsDBNull(12) ? null : reader.GetString(12))
            ?? lastCopiedAt;

        var iconBytes = ReadBytes(reader, 7);
        return new ClipEntry
        {
            Id = reader.GetInt64(0),
            Content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            ContentBytes = ReadBytes(reader, 2),
            ContentType = ContentTypeExtensions.FromStorageValue(reader.GetString(3)),
            ContentFormat = ClipContentFormatExtensions.FromStorageValue(reader.GetString(4)),
            SourceApp = reader.IsDBNull(5) ? null : reader.GetString(5),
            SourceAppPath = reader.IsDBNull(6) ? null : reader.GetString(6),
            SourceAppIconBytes = iconBytes,
            SourceAppIconAvailable = iconBytes is { Length: > 0 },
            Hash = reader.GetString(8),
            IsFavorite = reader.GetInt64(9) == 1,
            IsSensitive = reader.GetInt64(10) == 1,
            CopyCount = reader.IsDBNull(11) ? 1 : Convert.ToInt32(reader.GetInt64(11), CultureInfo.InvariantCulture),
            FirstCopiedAt = firstCopiedAt,
            LastCopiedAt = lastCopiedAt,
            ByteSize = reader.GetInt64(14),
            ImageWidth = reader.IsDBNull(15) ? null : reader.GetInt32(15),
            ImageHeight = reader.IsDBNull(16) ? null : reader.GetInt32(16),
            SourceWindowTitle = reader.IsDBNull(17) ? null : reader.GetString(17),
            SourceUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
            IsPasted = !reader.IsDBNull(19) && reader.GetInt64(19) == 1,
            PasteCount = reader.IsDBNull(20) ? 0 : Convert.ToInt32(reader.GetInt64(20), CultureInfo.InvariantCulture),
            LastPastedAt = ParseTimestamp(reader.IsDBNull(21) ? null : reader.GetString(21)),
            PinnedAt = ParseTimestamp(reader.IsDBNull(22) ? null : reader.GetString(22)),
            OcrText = reader.IsDBNull(23) ? null : reader.GetString(23),
            OcrStatus = reader.IsDBNull(24) ? null : reader.GetString(24),
            OcrAttemptedAt = ParseTimestamp(reader.IsDBNull(25) ? null : reader.GetString(25)),
            OcrError = reader.IsDBNull(26) ? null : reader.GetString(26),
            SourceClipId = reader.IsDBNull(27) ? null : reader.GetInt64(27),
            TransformKind = reader.IsDBNull(28) ? null : reader.GetString(28),
            ImportKind = reader.IsDBNull(29) ? null : reader.GetString(29),
        };
    }

    /// <summary>
    /// Reads a metadata-only <see cref="ClipEntry"/> from a row produced by <see cref="ClipListSelectColumns"/>.
    /// <c>ContentBytes</c> and <c>SourceAppIconBytes</c> are always <c>null</c>; the presence flag
    /// <see cref="ClipEntry.SourceAppIconAvailable"/> is read from the integer at column index 7. (U12)
    /// </summary>
    private static ClipEntry ReadClipMeta(SqliteDataReader reader)
    {
        var lastCopiedAt = ParseTimestamp(reader.IsDBNull(13) ? null : reader.GetString(13))
            ?? DateTimeOffset.UtcNow;
        var firstCopiedAt = ParseTimestamp(reader.IsDBNull(12) ? null : reader.GetString(12))
            ?? lastCopiedAt;

        return new ClipEntry
        {
            Id = reader.GetInt64(0),
            Content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            ContentBytes = null,   // intentionally omitted for list/search scans (U12)
            ContentType = ContentTypeExtensions.FromStorageValue(reader.GetString(3)),
            ContentFormat = ClipContentFormatExtensions.FromStorageValue(reader.GetString(4)),
            SourceApp = reader.IsDBNull(5) ? null : reader.GetString(5),
            SourceAppPath = reader.IsDBNull(6) ? null : reader.GetString(6),
            SourceAppIconBytes = null,   // intentionally omitted (U12)
            SourceAppIconAvailable = !reader.IsDBNull(7) && reader.GetInt64(7) == 1,
            Hash = reader.GetString(8),
            IsFavorite = reader.GetInt64(9) == 1,
            IsSensitive = reader.GetInt64(10) == 1,
            CopyCount = reader.IsDBNull(11) ? 1 : Convert.ToInt32(reader.GetInt64(11), CultureInfo.InvariantCulture),
            FirstCopiedAt = firstCopiedAt,
            LastCopiedAt = lastCopiedAt,
            ByteSize = reader.GetInt64(14),
            ImageWidth = reader.IsDBNull(15) ? null : reader.GetInt32(15),
            ImageHeight = reader.IsDBNull(16) ? null : reader.GetInt32(16),
            SourceWindowTitle = reader.IsDBNull(17) ? null : reader.GetString(17),
            SourceUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
            IsPasted = !reader.IsDBNull(19) && reader.GetInt64(19) == 1,
            PasteCount = reader.IsDBNull(20) ? 0 : Convert.ToInt32(reader.GetInt64(20), CultureInfo.InvariantCulture),
            LastPastedAt = ParseTimestamp(reader.IsDBNull(21) ? null : reader.GetString(21)),
            PinnedAt = ParseTimestamp(reader.IsDBNull(22) ? null : reader.GetString(22)),
            OcrText = reader.IsDBNull(23) ? null : reader.GetString(23),
            OcrStatus = reader.IsDBNull(24) ? null : reader.GetString(24),
            OcrAttemptedAt = ParseTimestamp(reader.IsDBNull(25) ? null : reader.GetString(25)),
            OcrError = reader.IsDBNull(26) ? null : reader.GetString(26),
            SourceClipId = reader.IsDBNull(27) ? null : reader.GetInt64(27),
            TransformKind = reader.IsDBNull(28) ? null : reader.GetString(28),
            ImportKind = reader.IsDBNull(29) ? null : reader.GetString(29),
        };
    }

    private async Task<ClipEntry?> GetClipByIdAsync(SqliteConnection connection, long clipId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ClipSelectColumns}
            FROM clips c
            WHERE c.id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", clipId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var clip = ReadClip(reader);
        var items = new List<ClipEntry> { clip };
        await LoadSensitivityMatchesAsync(connection, items, cancellationToken);
        return items[0];
    }

    private async Task AddSensitivityMatchesAsync(SqliteConnection connection, SqliteTransaction transaction, long clipId, IReadOnlyList<SensitivityMatch> matches, CancellationToken cancellationToken)
    {
        foreach (var match in matches)
        {
            var ruleId = await EnsureRuleAsync(connection, transaction, match, cancellationToken);

            await using var matchCommand = connection.CreateCommand();
            matchCommand.Transaction = transaction;
            matchCommand.CommandText = "INSERT OR IGNORE INTO clip_sensitivity_matches (clip_id, rule_id) VALUES ($clipId, $ruleId);";
            matchCommand.Parameters.AddWithValue("$clipId", clipId);
            matchCommand.Parameters.AddWithValue("$ruleId", ruleId);
            await matchCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task LoadSensitivityMatchesAsync(SqliteConnection connection, IList<ClipEntry> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        for (var index = 0; index < items.Count; index++)
        {
            var parameterName = $"$id{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, items[index].Id);
        }

        command.CommandText = $"""
            SELECT csm.clip_id, sr.id, sr.name, sr.severity
            FROM clip_sensitivity_matches csm
            JOIN sensitivity_rules sr ON sr.id = csm.rule_id
            WHERE csm.clip_id IN ({string.Join(", ", parameterNames)});
            """;

        var lookup = items.ToDictionary(static item => item.Id, static _ => new List<SensitivityMatch>());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var clipId = reader.GetInt64(0);
            lookup[clipId].Add(new SensitivityMatch
            {
                RuleId = reader.GetInt64(1),
                RuleName = reader.GetString(2),
                Severity = reader.GetString(3),
            });
        }

        foreach (var item in items)
        {
            item.SensitivityMatches = lookup[item.Id];
        }
    }

    private static async Task<int> ExecuteCountAsync(SqliteConnection connection, string sql, ClipSearchFilters filters, bool hasSearch, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (hasSearch && !filters.UseRegex)
        {
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText, filters.UseFuzzy));
        }

        AddContentTypeParameters(command, filters);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static void AddContentTypeParameters(SqliteCommand command, ClipSearchFilters filters)
    {
        if (filters.ContentTypes is not { Count: > 0 } types)
        {
            return;
        }

        var i = 0;
        foreach (var contentType in types)
        {
            command.Parameters.AddWithValue($"$contentType{i++}", contentType.ToStorageValue());
        }
    }

    private static async Task<int> ExecuteScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is DBNull or null ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is DBNull or null ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is DBNull or null ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is DBNull or null ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Pinning or favoriting a clip is an explicit "keep this" from the user, so
    /// capacity- and age-based pruning must skip it. The sensitive-clip timer
    /// deliberately does not apply this: expiring a secret outranks convenience.
    /// </summary>
    private const string UserKeptClipPredicate = "(is_favorite = 1 OR pinned_at IS NOT NULL)";

    private static async Task<int> DeleteOlderThanAsync(SqliteConnection connection, SqliteTransaction transaction, bool isSensitive, DateTimeOffset cutoff, bool preserveUserKeptClips, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM clips
            WHERE is_sensitive = $isSensitive
              AND COALESCE(last_copied_at, captured_at) < $cutoff
              AND ($keepUserKept = 0 OR NOT {UserKeptClipPredicate});
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$isSensitive", isSensitive ? 1 : 0);
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$keepUserKept", preserveUserKeptClips ? 1 : 0);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> DeleteOldestAsync(SqliteConnection connection, SqliteTransaction transaction, int deleteCount, CancellationToken cancellationToken)
    {
        if (deleteCount <= 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM clips
            WHERE id IN (
                SELECT id
                FROM clips
                WHERE NOT {UserKeptClipPredicate}
                ORDER BY COALESCE(last_copied_at, captured_at) ASC, id ASC
                LIMIT $limit
            );
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$limit", deleteCount);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> DeleteUntilWithinSizeAsync(SqliteConnection connection, SqliteTransaction transaction, long totalStoredBytes, long maxBytes, CancellationToken cancellationToken)
    {
        var rows = new List<(long Id, long ByteSize)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT id, byte_size
                FROM clips
                WHERE NOT {UserKeptClipPredicate}
                ORDER BY COALESCE(last_copied_at, captured_at) ASC, id ASC;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? 0L : reader.GetInt64(1)));
            }
        }

        var idsToDelete = new List<long>();
        foreach (var row in rows)
        {
            if (totalStoredBytes <= maxBytes)
            {
                break;
            }

            idsToDelete.Add(row.Id);
            totalStoredBytes -= Math.Max(0L, row.ByteSize);
        }

        if (idsToDelete.Count == 0)
        {
            return 0;
        }

        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        for (var index = 0; index < idsToDelete.Count; index++)
        {
            deleteCommand.Parameters.AddWithValue($"$id{index}", idsToDelete[index]);
        }

        deleteCommand.CommandText = $"DELETE FROM clips WHERE id IN ({string.Join(", ", idsToDelete.Select((_, index) => $"$id{index}"))}); SELECT changes();";
        return Convert.ToInt32(await deleteCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static void AddUpsertParameters(
        SqliteCommand command,
        ClipCaptureRequest request,
        string contentText,
        string hash,
        bool isSensitive,
        string capturedAt,
        long byteSize,
        long? clipId = null)
    {
        if (clipId is not null)
        {
            command.Parameters.AddWithValue("$id", clipId.Value);
        }

        command.Parameters.AddWithValue("$content", string.IsNullOrWhiteSpace(contentText) ? DBNull.Value : contentText);
        command.Parameters.AddWithValue("$contentBytes", request.ContentBytes.Length == 0 ? DBNull.Value : request.ContentBytes);
        command.Parameters.AddWithValue("$contentType", request.ContentType.ToStorageValue());
        command.Parameters.AddWithValue("$contentFormat", request.ContentFormat.ToStorageValue());
        command.Parameters.AddWithValue("$sourceApp", string.IsNullOrWhiteSpace(request.SourceApp) ? DBNull.Value : request.SourceApp);
        command.Parameters.AddWithValue("$sourceAppPath", string.IsNullOrWhiteSpace(request.SourceAppPath) ? DBNull.Value : request.SourceAppPath);
        command.Parameters.AddWithValue("$sourceAppIcon", request.SourceAppIconBytes is { Length: > 0 } iconBytes ? iconBytes : DBNull.Value);
        command.Parameters.AddWithValue("$sourceWindowTitle", string.IsNullOrWhiteSpace(request.SourceWindowTitle) ? DBNull.Value : request.SourceWindowTitle);
        command.Parameters.AddWithValue("$sourceUrl", string.IsNullOrWhiteSpace(request.SourceUrl) ? DBNull.Value : request.SourceUrl);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$isFavorite", request.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$isSensitive", isSensitive ? 1 : 0);
        command.Parameters.AddWithValue("$capturedAt", capturedAt);
        command.Parameters.AddWithValue("$lastCopiedAt", capturedAt);
        command.Parameters.AddWithValue("$byteSize", byteSize);
        command.Parameters.AddWithValue("$imageWidth", request.ImageWidth is { } width ? width : DBNull.Value);
        command.Parameters.AddWithValue("$imageHeight", request.ImageHeight is { } height ? height : DBNull.Value);
        command.Parameters.AddWithValue("$sourceClipId", request.SourceClipId is { } sourceClipId ? sourceClipId : DBNull.Value);
        command.Parameters.AddWithValue("$transformKind", string.IsNullOrWhiteSpace(request.TransformKind) ? DBNull.Value : request.TransformKind);
        command.Parameters.AddWithValue("$importKind", string.IsNullOrWhiteSpace(request.ImportKind) ? DBNull.Value : request.ImportKind);
    }

    private static string BuildStoredContentText(ClipCaptureRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ContentText))
        {
            return request.ContentText;
        }

        if ((request.ContentFormat == ClipContentFormat.Html || request.ContentFormat == ClipContentFormat.Rtf)
            && request.ContentBytes.Length > 0)
        {
            return ClipDisplayFormatter.RenderRichContent(Encoding.UTF8.GetString(request.ContentBytes));
        }

        if (request.ContentType == ContentType.Image
            && request.ImageWidth is { } width
            && request.ImageHeight is { } height)
        {
            return AppText.FormatImageSummary(AppText.FormatImageDimensions(width, height));
        }

        return string.Empty;
    }

    private static byte[]? ReadBytes(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetFieldValue<byte[]>(index);

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string ComputeHash(ContentType contentType, ClipContentFormat contentFormat, byte[] contentBytes)
    {
        var typeBytes = Encoding.UTF8.GetBytes($"{contentType.ToStorageValue()}::{contentFormat.ToStorageValue()}");
        var input = new byte[typeBytes.Length + 1 + contentBytes.Length];
        Buffer.BlockCopy(typeBytes, 0, input, 0, typeBytes.Length);
        Buffer.BlockCopy(contentBytes, 0, input, typeBytes.Length + 1, contentBytes.Length);
        var bytes = SHA256.HashData(input);
        return Convert.ToHexString(bytes);
    }

    private static Regex BuildSearchRegex(string searchText, bool caseSensitive)
        => new(searchText, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Checks all five columns indexed by FTS: content, source_app, source_window_title, source_url, ocr_text. (U13)</summary>
    private static bool IsRegexMatch(ClipEntry clip, Regex regex)
        => regex.IsMatch(clip.Content) ||
           (!string.IsNullOrWhiteSpace(clip.SourceApp) && regex.IsMatch(clip.SourceApp)) ||
           (!string.IsNullOrWhiteSpace(clip.SourceWindowTitle) && regex.IsMatch(clip.SourceWindowTitle)) ||
           (!string.IsNullOrWhiteSpace(clip.SourceUrl) && regex.IsMatch(clip.SourceUrl)) ||
           (!string.IsNullOrWhiteSpace(clip.OcrText) && regex.IsMatch(clip.OcrText));

    private static async Task<long> EnsureRuleAsync(SqliteConnection connection, SqliteTransaction transaction, SensitivityMatch match, CancellationToken cancellationToken)
    {
        if (match.RuleId > 0)
        {
            return match.RuleId;
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO sensitivity_rules (name, pattern, severity, is_enabled, is_builtin)
                VALUES ($name, $pattern, $severity, 1, 1)
                ON CONFLICT(name) DO UPDATE SET severity = excluded.severity;
                """;
            insertCommand.Parameters.AddWithValue("$name", match.RuleName);
            insertCommand.Parameters.AddWithValue("$pattern", match.RuleName);
            insertCommand.Parameters.AddWithValue("$severity", match.Severity);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT id FROM sensitivity_rules WHERE name = $name;";
        idCommand.Parameters.AddWithValue("$name", match.RuleName);
        return (long)(await idCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    // ============================ Semantic embeddings (sem-02) ============================

    private const string EmbeddingEligibilityClause = """
        is_sensitive = 0
        AND sensitivity_scanned_at IS NOT NULL
        AND (
            (content_type IN ('text','richtext','files') AND content IS NOT NULL AND TRIM(content) <> '')
            OR (content_type = 'image' AND ocr_status = 'succeeded' AND ocr_text IS NOT NULL AND TRIM(ocr_text) <> '')
        )
        """;

    private const string EmbeddingTextExpression = "CASE WHEN content_type = 'image' THEN ocr_text ELSE content END";

    // After this many failed attempts a clip is no longer re-claimed by
    // ClaimPendingEmbeddingsAsync, so a poison clip (content that always crashes
    // inference) can't pin the worker re-embedding it every idle cycle forever.
    // A RerunAll resets the counter to give every clip a fresh set of attempts.
    private const int MaxEmbeddingAttempts = 3;

    public async Task<IReadOnlyList<ClipEmbeddingCandidate>> ClaimPendingEmbeddingsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) return Array.Empty<ClipEmbeddingCandidate>();

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var results = new List<ClipEmbeddingCandidate>(batchSize);

        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = $"""
                SELECT id, ({EmbeddingTextExpression}) AS etext
                FROM clips
                WHERE (embedding_status IS NULL OR embedding_status IN ('pending','rerun')
                       OR (embedding_status = 'failed' AND embedding_attempts < $maxAttempts))
                  AND {EmbeddingEligibilityClause}
                ORDER BY COALESCE(last_copied_at, captured_at) DESC
                LIMIT $limit;
                """;
            selectCommand.Parameters.AddWithValue("$limit", batchSize);
            selectCommand.Parameters.AddWithValue("$maxAttempts", MaxEmbeddingAttempts);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var text = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add(new ClipEmbeddingCandidate(id, text));
                }
            }
        }

        if (results.Count > 0)
        {
            await using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            var paramNames = new List<string>(results.Count);
            for (var i = 0; i < results.Count; i++)
            {
                var p = "$id" + i;
                paramNames.Add(p);
                claim.Parameters.AddWithValue(p, results[i].ClipId);
            }
            claim.CommandText = $"UPDATE clips SET embedding_status = 'processing' WHERE id IN ({string.Join(",", paramNames)});";
            await claim.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task SaveEmbeddingBatchAsync(IReadOnlyList<ClipEmbeddingRecord> records, string modelVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        if (records.Count == 0) return;

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        foreach (var record in records)
        {
            if (record.Vector is null || record.Vector.Length == 0) continue;

            var bytes = new byte[record.Vector.Length * sizeof(float)];
            Buffer.BlockCopy(record.Vector, 0, bytes, 0, bytes.Length);

            var embeddingSaved = false;
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO clip_embeddings (clip_id, model_version, dimensions, vector, created_at)
                    SELECT $id, $model, $dim, $vec, $at
                    WHERE EXISTS (SELECT 1 FROM clips WHERE id = $id)
                    ON CONFLICT(clip_id) DO UPDATE SET
                        model_version = excluded.model_version,
                        dimensions    = excluded.dimensions,
                        vector        = excluded.vector,
                        created_at    = excluded.created_at;
                    """;
                upsert.Parameters.AddWithValue("$id", record.ClipId);
                upsert.Parameters.AddWithValue("$model", modelVersion);
                upsert.Parameters.AddWithValue("$dim", record.Vector.Length);
                upsert.Parameters.AddWithValue("$vec", bytes);
                upsert.Parameters.AddWithValue("$at", now);
                embeddingSaved = await upsert.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            if (!embeddingSaved)
            {
                continue;
            }

            await using (var status = connection.CreateCommand())
            {
                status.Transaction = transaction;
                status.CommandText = "UPDATE clips SET embedding_status = 'succeeded' WHERE id = $id;";
                status.Parameters.AddWithValue("$id", record.ClipId);
                await status.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> SetEmbeddingFailureAsync(long clipId, string? error, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET embedding_status = 'failed', embedding_attempts = embedding_attempts + 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        // error message is logged by the caller; no dedicated column yet.
        _ = error;
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<IReadOnlyList<long>> MarkAllEmbeddingsForRerunAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var purge = connection.CreateCommand())
        {
            purge.Transaction = transaction;
            purge.CommandText = "DELETE FROM clip_embeddings;";
            await purge.ExecuteNonQueryAsync(cancellationToken);
        }

        var ids = new List<long>();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE clips
                SET embedding_status = 'rerun', embedding_attempts = 0
                WHERE {EmbeddingEligibilityClause}
                RETURNING id;
                """;
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    public async Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                SUM(CASE WHEN {EmbeddingEligibilityClause} THEN 1 ELSE 0 END) AS eligible,
                SUM(CASE WHEN {EmbeddingEligibilityClause} AND embedding_status = 'succeeded' THEN 1 ELSE 0 END) AS embedded,
                SUM(CASE WHEN {EmbeddingEligibilityClause} AND (embedding_status IS NULL OR embedding_status IN ('pending','rerun','processing')) THEN 1 ELSE 0 END) AS pending,
                SUM(CASE WHEN {EmbeddingEligibilityClause} AND embedding_status = 'failed' THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN embedding_status = 'excluded' THEN 1 ELSE 0 END) AS excluded
            FROM clips;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new EmbeddingCoverage(0, 0, 0, 0, 0);
        }

        long Get(int i) => reader.IsDBNull(i) ? 0L : reader.GetInt64(i);
        return new EmbeddingCoverage(Get(0), Get(1), Get(2), Get(3), Get(4));
    }

    public async Task<IReadOnlyList<ClipEmbedding>> LoadAllEmbeddingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.clip_id, e.model_version, e.dimensions, e.vector
            FROM clip_embeddings e
            INNER JOIN clips c ON c.id = e.clip_id
            WHERE c.is_sensitive = 0;
            """;

        var result = new List<ClipEmbedding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var model = reader.GetString(1);
            var dim = reader.GetInt32(2);
            var blob = (byte[])reader[3];
            if (blob.Length != dim * sizeof(float)) continue;
            var vec = new float[dim];
            Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
            result.Add(new ClipEmbedding(id, vec, model));
        }
        return result;
    }

    public async Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        // Cheap-but-comprehensive warmup. The three queries touch:
        //   1. the clips table b-tree (paged in by COUNT)
        //   2. the captured_at sorted index (used by every default refresh)
        //   3. the FTS5 index header (used by every search-with-text)
        // Together they pay the SQLCipher key derivation, the SQLite page-cache
        // initial population, and the FTS5 index header read up front, so the
        // user's first refresh after startup doesn't.
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var c = connection.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM clips;";
            await c.ExecuteScalarAsync(cancellationToken);
        }
        await using (var c = connection.CreateCommand())
        {
            c.CommandText = "SELECT id FROM clips ORDER BY captured_at DESC LIMIT 1;";
            await c.ExecuteScalarAsync(cancellationToken);
        }
        await using (var c = connection.CreateCommand())
        {
            c.CommandText = "SELECT rowid FROM clips_fts WHERE clips_fts MATCH 'clipthrough_prewarm_token' LIMIT 1;";
            try { await c.ExecuteScalarAsync(cancellationToken); }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* expected if FTS rejects the token */ }
        }
    }
}
