using System;
using System.Collections.Generic;
using System.Globalization;
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
            c.pinned_at
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISensitivityService _sensitivityService;
    private readonly ISettingsService _settingsService;
    private readonly IAppNotificationService _notificationService;

    public ClipStoreService(SqliteConnectionFactory connectionFactory, ISensitivityService sensitivityService, ISettingsService settingsService, IAppNotificationService notificationService)
    {
        _connectionFactory = connectionFactory;
        _sensitivityService = sensitivityService;
        _settingsService = settingsService;
        _notificationService = notificationService;
    }

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

    public async Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var hasSearch = !string.IsNullOrWhiteSpace(filters.SearchText);
        if (hasSearch && (filters.UseRegex || filters.CaseSensitive || filters.UseWildcard || filters.WholeWord))
        {
            return await SearchInMemoryAsync(connection, filters, cancellationToken);
        }

        var fromClause = hasSearch
            ? "FROM clips c JOIN clips_fts ON clips_fts.rowid = c.id"
            : "FROM clips c";
        var whereClauses = BuildWhereClauses(filters, hasSearch);
        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var orderClause = hasSearch
            ? "ORDER BY CASE WHEN c.pinned_at IS NULL THEN 1 ELSE 0 END, c.pinned_at DESC, bm25(clips_fts), COALESCE(c.last_copied_at, c.captured_at) DESC"
            : "ORDER BY CASE WHEN c.pinned_at IS NULL THEN 1 ELSE 0 END, c.pinned_at DESC, COALESCE(c.last_copied_at, c.captured_at) DESC";

        var items = new List<ClipEntry>();

        await using (var queryCommand = connection.CreateCommand())
        {
            queryCommand.CommandText = $"""
                SELECT {ClipSelectColumns}
                {fromClause}
                {whereClause}
                {orderClause}
                LIMIT $limit OFFSET $offset;
                """;
            AddSearchParameters(queryCommand, filters, hasSearch);

            await using var reader = await queryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadClip(reader));
            }
        }

        await LoadSensitivityMatchesAsync(connection, items, cancellationToken);

        var totalMatchingCount = await ExecuteCountAsync(connection, $"SELECT COUNT(*) {fromClause} {whereClause};", filters, hasSearch, cancellationToken);
        var totalClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken);
        var sensitiveClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips WHERE is_sensitive = 1;", cancellationToken);
        var totalStoredBytes = await ExecuteScalarLongAsync(connection, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;", cancellationToken);
        var lastCapturedAt = await ExecuteScalarStringAsync(connection, "SELECT MAX(COALESCE(last_copied_at, captured_at)) FROM clips;", cancellationToken);

        return new ClipSearchResult
        {
            Items = items,
            TotalMatchingCount = totalMatchingCount,
            TotalClipCount = totalClipCount,
            SensitiveClipCount = sensitiveClipCount,
            TotalStoredBytes = totalStoredBytes,
            LastCapturedAt = ParseTimestamp(lastCapturedAt),
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
            purgedClipCount += await DeleteOlderThanAsync(connection, transaction, isSensitive: true, now.AddMinutes(-settings.SensitiveClipLifetimeMinutes), cancellationToken);
        }

        if (settings.EnableNormalClipLifetime)
        {
            purgedClipCount += await DeleteOlderThanAsync(connection, transaction, isSensitive: false, now.AddDays(-settings.NormalClipLifetimeDays), cancellationToken);
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

        return new ClipMaintenanceResult
        {
            PurgedClipCount = purgedClipCount,
            TotalClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken),
            TotalStoredBytes = await ExecuteScalarLongAsync(connection, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;", cancellationToken),
        };
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
            ORDER BY COALESCE(c.last_copied_at, c.captured_at) DESC
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

    private async Task<ClipEntry?> InsertClipAsync(ClipCaptureRequest request, CancellationToken cancellationToken)
    {
        var contentText = BuildStoredContentText(request);
        var hash = ComputeHash(request.ContentType, request.ContentFormat, request.ContentBytes);
        var matches = _sensitivityService.Scan(contentText);
        var capturedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
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
                            image_height = COALESCE($imageHeight, image_height)
                        WHERE id = $id;
                        """;
                    AddUpsertParameters(updateCommand, request, contentText, hash, matches.Count > 0, capturedAt, byteSize, clipId);
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
                        image_height)
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
                        $imageHeight);
                    SELECT last_insert_rowid();
                    """;
                AddUpsertParameters(insertCommand, request, contentText, hash, matches.Count > 0, capturedAt, byteSize);
                clipId = (long)(await insertCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }
        }

        await AddSensitivityMatchesAsync(connection, transaction, clipId, matches, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await ApplyMaintenanceAsync(cancellationToken);
        return await GetClipByIdAsync(connection, clipId, cancellationToken);
    }

    private async Task<ClipSearchResult> SearchInMemoryAsync(SqliteConnection connection, ClipSearchFilters filters, CancellationToken cancellationToken)
    {
        var regex = filters.UseRegex ? BuildSearchRegex(filters.SearchText, filters.CaseSensitive) : null;
        var items = new List<ClipEntry>();
        var whereClauses = BuildWhereClauses(filters, hasSearch: false);
        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var totalMatchingCount = 0;

        await using (var queryCommand = connection.CreateCommand())
        {
            queryCommand.CommandText = $"""
                SELECT {ClipSelectColumns}
                FROM clips c
                {whereClause}
                ORDER BY COALESCE(c.last_copied_at, c.captured_at) DESC;
                """;
            AddSearchParameters(queryCommand, filters, hasSearch: false);

            await using var reader = await queryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var clip = ReadClip(reader);
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
            }
        }

        await LoadSensitivityMatchesAsync(connection, items, cancellationToken);

        var totalClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips;", cancellationToken);
        var sensitiveClipCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM clips WHERE is_sensitive = 1;", cancellationToken);
        var totalStoredBytes = await ExecuteScalarLongAsync(connection, "SELECT COALESCE(SUM(byte_size), 0) FROM clips;", cancellationToken);
        var lastCapturedAt = await ExecuteScalarStringAsync(connection, "SELECT MAX(COALESCE(last_copied_at, captured_at)) FROM clips;", cancellationToken);

        return new ClipSearchResult
        {
            Items = items,
            TotalMatchingCount = totalMatchingCount,
            TotalClipCount = totalClipCount,
            SensitiveClipCount = sensitiveClipCount,
            TotalStoredBytes = totalStoredBytes,
            LastCapturedAt = ParseTimestamp(lastCapturedAt),
        };
    }

    private static bool MatchesSearch(ClipEntry clip, ClipSearchFilters filters, Regex? regex)
    {
        if (string.IsNullOrWhiteSpace(filters.SearchText))
        {
            return true;
        }

        if (regex is not null)
        {
            return IsRegexMatch(clip, regex);
        }

        if (filters.UseWildcard)
        {
            var wildcardRegex = BuildWildcardRegex(filters.SearchText, filters.CaseSensitive, filters.WholeWord);
            return IsRegexMatch(clip, wildcardRegex);
        }

        if (filters.WholeWord)
        {
            var wholeWordRegex = BuildWholeWordRegex(filters.SearchText, filters.CaseSensitive);
            return IsRegexMatch(clip, wholeWordRegex);
        }

        var comparison = filters.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var tokens = filters.SearchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens.All(token =>
            clip.Content.Contains(token, comparison) ||
            (!string.IsNullOrWhiteSpace(clip.SourceApp) && clip.SourceApp.Contains(token, comparison)) ||
            (!string.IsNullOrWhiteSpace(clip.SourceWindowTitle) && clip.SourceWindowTitle.Contains(token, comparison)) ||
            (!string.IsNullOrWhiteSpace(clip.SourceUrl) && clip.SourceUrl.Contains(token, comparison)));
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

    private static List<string> BuildWhereClauses(ClipSearchFilters filters, bool hasSearch)
    {
        var clauses = new List<string>();

        if (hasSearch && !filters.UseRegex)
        {
            clauses.Add("clips_fts MATCH $search");
        }

        if (filters.ContentType is not null)
        {
            clauses.Add("c.content_type = $contentType");
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

    private static void AddSearchParameters(SqliteCommand command, ClipSearchFilters filters, bool hasSearch)
    {
        if (hasSearch && !filters.UseRegex)
        {
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText, filters.UseFuzzy));
        }

        if (filters.ContentType is { } contentType)
        {
            command.Parameters.AddWithValue("$contentType", contentType.ToStorageValue());
        }

        command.Parameters.AddWithValue("$limit", filters.Limit);
        command.Parameters.AddWithValue("$offset", filters.Offset);
    }

    private static string BuildFtsExpression(string searchText, bool useFuzzy = false)
    {
        var tokens = searchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return "*";
        }

        string Quote(string t) => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*";

        if (!useFuzzy)
        {
            return string.Join(" AND ", tokens.Select(Quote));
        }

        // Fuzzy: OR between tokens and between 1-char deletion variants so that
        // "exammple" can still match "example". Each token becomes
        // ("tok"* OR "tk"* OR "ok"* OR ...).
        var parts = new List<string>();
        foreach (var token in tokens)
        {
            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { token };
            if (token.Length > 3)
            {
                for (var i = 0; i < token.Length; i++)
                {
                    variants.Add(token.Remove(i, 1));
                }
            }

            parts.Add("(" + string.Join(" OR ", variants.Select(Quote)) + ")");
        }

        return string.Join(" AND ", parts);
    }

    private static ClipEntry ReadClip(SqliteDataReader reader)
    {
        var lastCopiedAt = ParseTimestamp(reader.IsDBNull(13) ? null : reader.GetString(13))
            ?? DateTimeOffset.UtcNow;
        var firstCopiedAt = ParseTimestamp(reader.IsDBNull(12) ? null : reader.GetString(12))
            ?? lastCopiedAt;

        return new ClipEntry
        {
            Id = reader.GetInt64(0),
            Content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            ContentBytes = ReadBytes(reader, 2),
            ContentType = ContentTypeExtensions.FromStorageValue(reader.GetString(3)),
            ContentFormat = ClipContentFormatExtensions.FromStorageValue(reader.GetString(4)),
            SourceApp = reader.IsDBNull(5) ? null : reader.GetString(5),
            SourceAppPath = reader.IsDBNull(6) ? null : reader.GetString(6),
            SourceAppIconBytes = ReadBytes(reader, 7),
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

        if (filters.ContentType is { } contentType)
        {
            command.Parameters.AddWithValue("$contentType", contentType.ToStorageValue());
        }

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
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

    private static async Task<int> DeleteOlderThanAsync(SqliteConnection connection, SqliteTransaction transaction, bool isSensitive, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM clips
            WHERE is_sensitive = $isSensitive
              AND COALESCE(last_copied_at, captured_at) < $cutoff;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$isSensitive", isSensitive ? 1 : 0);
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
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
        command.CommandText = """
            DELETE FROM clips
            WHERE id IN (
                SELECT id
                FROM clips
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
            command.CommandText = """
                SELECT id, byte_size
                FROM clips
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

    private static bool IsRegexMatch(ClipEntry clip, Regex regex)
        => regex.IsMatch(clip.Content) ||
           (!string.IsNullOrWhiteSpace(clip.SourceApp) && regex.IsMatch(clip.SourceApp));

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
}
