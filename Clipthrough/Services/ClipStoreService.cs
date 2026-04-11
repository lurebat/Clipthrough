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
using Clipthrough.Database;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

public sealed class ClipStoreService : IClipStoreService
{
    private const string SampleDataSeedMarkerKey = "seed:sample-data:v1";
    private const int TargetSampleSeedCount = 250;
    private const string ClipSelectColumns = """
            c.id,
            c.content,
            c.content_type,
            c.source_app,
            c.hash,
            c.is_favorite,
            c.is_sensitive,
            c.copy_count,
            c.first_copied_at,
            c.last_copied_at,
            c.byte_size
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISensitivityService _sensitivityService;
    private readonly ISettingsService _settingsService;

    public ClipStoreService(SqliteConnectionFactory connectionFactory, ISensitivityService sensitivityService, ISettingsService settingsService)
    {
        _connectionFactory = connectionFactory;
        _sensitivityService = sensitivityService;
        _settingsService = settingsService;
    }

    public async Task SeedSampleDataAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFeaturedSamplesAsync(cancellationToken);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        if (await HasSeedMarkerAsync(connection, cancellationToken))
        {
            return;
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM clips;";
        var existingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (existingCount >= TargetSampleSeedCount)
        {
            await SetSeedMarkerAsync(connection, cancellationToken);
            return;
        }

        var templates = new[]
        {
            new { Content = "SELECT * FROM users WHERE email LIKE '%@corp.com' ORDER BY created_at DESC LIMIT 100;", Source = "DBeaver", Type = ContentType.Text, Favorite = true },
            new { Content = "server=prod-sql;user id=report_user;password=Sup3rSecret!;database=warehouse;", Source = "Azure Data Studio", Type = ContentType.Text, Favorite = false },
            new { Content = "AKIA0EXAMPLEKEY123456", Source = "Visual Studio Code", Type = ContentType.Text, Favorite = false },
            new { Content = "npm install AvaloniaUI.DiagnosticsSupport --prerelease", Source = "PowerShell", Type = ContentType.Text, Favorite = false },
            new { Content = "https://github.com/AvaloniaUI/Avalonia", Source = "Chrome", Type = ContentType.Text, Favorite = true },
            new { Content = "password = Tr0ub4dor&3", Source = "Rider", Type = ContentType.Text, Favorite = false },
            new { Content = "Quarterly customer call notes and follow-up actions.", Source = "Notion", Type = ContentType.Text, Favorite = false },
            new { Content = "Files copied: Budget.xlsx; Strategy.pptx; Notes.docx", Source = "Explorer", Type = ContentType.Files, Favorite = false },
            new { Content = "RTF snippet copied from Outlook signature block", Source = "Outlook", Type = ContentType.RichText, Favorite = false },
            new { Content = "Image placeholder: screenshot captured from design review", Source = "Snipping Tool", Type = ContentType.Image, Favorite = false },
        };

        var clipsToSeed = TargetSampleSeedCount - existingCount;
        var seedRunTag = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        using var transaction = connection.BeginTransaction();
        var ruleIds = new Dictionary<string, long>(StringComparer.Ordinal);

        await using var insertClipCommand = connection.CreateCommand();
        insertClipCommand.Transaction = transaction;
        insertClipCommand.CommandText = """
            INSERT INTO clips (content, content_type, source_app, hash, is_favorite, is_sensitive, captured_at, copy_count, first_copied_at, last_copied_at, byte_size)
            VALUES ($content, $contentType, $sourceApp, $hash, $isFavorite, $isSensitive, $capturedAt, 1, $capturedAt, $capturedAt, $byteSize);
            SELECT last_insert_rowid();
            """;

        await using var insertMatchCommand = connection.CreateCommand();
        insertMatchCommand.Transaction = transaction;
        insertMatchCommand.CommandText = "INSERT OR IGNORE INTO clip_sensitivity_matches (clip_id, rule_id) VALUES ($clipId, $ruleId);";

        for (var i = 0; i < clipsToSeed; i++)
        {
            if (i % 500 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var template = templates[(existingCount + i) % templates.Length];
            var sequence = existingCount + i + 1;
            var content = $"{template.Content} [seed:{seedRunTag}:{sequence:D6}]";
            var hash = ComputeHash(template.Type, content);
            var matches = _sensitivityService.Scan(content);
            var capturedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var byteSize = Encoding.UTF8.GetByteCount(content);

            insertClipCommand.Parameters.Clear();
            insertClipCommand.Parameters.AddWithValue("$content", content);
            insertClipCommand.Parameters.AddWithValue("$contentType", template.Type.ToStorageValue());
            insertClipCommand.Parameters.AddWithValue("$sourceApp", template.Source);
            insertClipCommand.Parameters.AddWithValue("$hash", hash);
            insertClipCommand.Parameters.AddWithValue("$isFavorite", template.Favorite ? 1 : 0);
            insertClipCommand.Parameters.AddWithValue("$isSensitive", matches.Count > 0 ? 1 : 0);
            insertClipCommand.Parameters.AddWithValue("$capturedAt", capturedAt);
            insertClipCommand.Parameters.AddWithValue("$byteSize", byteSize);
            var clipId = (long)(await insertClipCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

            foreach (var match in matches)
            {
                if (!ruleIds.TryGetValue(match.RuleName, out var ruleId))
                {
                    ruleId = await EnsureRuleAsync(connection, transaction, match, cancellationToken);
                    ruleIds[match.RuleName] = ruleId;
                }

                insertMatchCommand.Parameters.Clear();
                insertMatchCommand.Parameters.AddWithValue("$clipId", clipId);
                insertMatchCommand.Parameters.AddWithValue("$ruleId", ruleId);
                await insertMatchCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        await SetSeedMarkerAsync(connection, cancellationToken);
    }

    public async Task<ClipSearchResult> SearchAsync(ClipSearchFilters filters, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var hasSearch = !string.IsNullOrWhiteSpace(filters.SearchText);
        if (hasSearch && (filters.UseRegex || filters.CaseSensitive))
        {
            return await SearchInMemoryAsync(connection, filters, cancellationToken);
        }

        var fromClause = hasSearch
            ? "FROM clips c JOIN clips_fts ON clips_fts.rowid = c.id"
            : "FROM clips c";
        var whereClauses = BuildWhereClauses(filters, hasSearch);
        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var orderClause = hasSearch
            ? "ORDER BY bm25(clips_fts), COALESCE(c.last_copied_at, c.captured_at) DESC"
            : "ORDER BY COALESCE(c.last_copied_at, c.captured_at) DESC";

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
        var lastCapturedAt = await ExecuteScalarStringAsync(connection, "SELECT MAX(COALESCE(last_copied_at, captured_at)) FROM clips;", cancellationToken);

        return new ClipSearchResult
        {
            Items = items,
            TotalMatchingCount = totalMatchingCount,
            TotalClipCount = totalClipCount,
            SensitiveClipCount = sensitiveClipCount,
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

    public async Task DeleteAsync(long clipId, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", clipId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<ClipEntry?> CaptureAsync(string content, ContentType contentType, string? sourceApp = null, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(content) > _settingsService.Current.MaxClipSizeBytes)
        {
            return Task.FromResult<ClipEntry?>(null);
        }

        return InsertClipAsync(content, contentType, sourceApp, isFavorite: false, incrementExistingCopyCount: true, cancellationToken);
    }

    private async Task<ClipEntry?> InsertClipAsync(string content, ContentType contentType, string? sourceApp, bool isFavorite, bool incrementExistingCopyCount, CancellationToken cancellationToken)
    {
        var hash = ComputeHash(contentType, content);
        var matches = _sensitivityService.Scan(content);
        var capturedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var byteSize = Encoding.UTF8.GetByteCount(content);

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

                if (incrementExistingCopyCount)
                {
                    await using var updateCommand = connection.CreateCommand();
                    updateCommand.Transaction = transaction;
                    updateCommand.CommandText = """
                        UPDATE clips
                        SET source_app = CASE WHEN $sourceApp IS NULL OR TRIM($sourceApp) = '' THEN source_app ELSE $sourceApp END,
                            is_favorite = CASE WHEN is_favorite = 1 OR $isFavorite = 1 THEN 1 ELSE 0 END,
                            is_sensitive = CASE WHEN is_sensitive = 1 OR $isSensitive = 1 THEN 1 ELSE 0 END,
                            captured_at = $lastCopiedAt,
                            first_copied_at = COALESCE(first_copied_at, captured_at, $lastCopiedAt),
                            last_copied_at = $lastCopiedAt,
                            copy_count = copy_count + 1,
                            byte_size = $byteSize
                        WHERE id = $id;
                        """;
                    updateCommand.Parameters.AddWithValue("$id", clipId);
                    updateCommand.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
                    updateCommand.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
                    updateCommand.Parameters.AddWithValue("$isSensitive", matches.Count > 0 ? 1 : 0);
                    updateCommand.Parameters.AddWithValue("$lastCopiedAt", capturedAt);
                    updateCommand.Parameters.AddWithValue("$byteSize", byteSize);
                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            else
            {
                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO clips (content, content_type, source_app, hash, is_favorite, is_sensitive, captured_at, copy_count, first_copied_at, last_copied_at, byte_size)
                    VALUES ($content, $contentType, $sourceApp, $hash, $isFavorite, $isSensitive, $capturedAt, 1, $capturedAt, $capturedAt, $byteSize);
                    SELECT last_insert_rowid();
                    """;
                insertCommand.Parameters.AddWithValue("$content", content);
                insertCommand.Parameters.AddWithValue("$contentType", contentType.ToStorageValue());
                insertCommand.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("$hash", hash);
                insertCommand.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
                insertCommand.Parameters.AddWithValue("$isSensitive", matches.Count > 0 ? 1 : 0);
                insertCommand.Parameters.AddWithValue("$capturedAt", capturedAt);
                insertCommand.Parameters.AddWithValue("$byteSize", byteSize);
                clipId = (long)(await insertCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }
        }

        await AddSensitivityMatchesAsync(connection, transaction, clipId, matches, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return await GetClipByIdAsync(connection, clipId, cancellationToken);
    }

    private async Task EnsureFeaturedSamplesAsync(CancellationToken cancellationToken)
    {
        await InsertClipAsync(BuildRichTextSampleContent(), ContentType.RichText, "Outlook", false, incrementExistingCopyCount: false, cancellationToken);
        await InsertClipAsync(BuildImageSampleContent(), ContentType.Image, "Snipping Tool", false, incrementExistingCopyCount: false, cancellationToken);
        await InsertClipAsync(await BuildFileSampleContentAsync(cancellationToken), ContentType.Files, "Explorer", false, incrementExistingCopyCount: false, cancellationToken);
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
        var lastCapturedAt = await ExecuteScalarStringAsync(connection, "SELECT MAX(COALESCE(last_copied_at, captured_at)) FROM clips;", cancellationToken);

        return new ClipSearchResult
        {
            Items = items,
            TotalMatchingCount = totalMatchingCount,
            TotalClipCount = totalClipCount,
            SensitiveClipCount = sensitiveClipCount,
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

        var comparison = filters.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var tokens = filters.SearchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens.All(token =>
            clip.Content.Contains(token, comparison) ||
            (!string.IsNullOrWhiteSpace(clip.SourceApp) && clip.SourceApp.Contains(token, comparison)));
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

        return clauses;
    }

    private static void AddSearchParameters(SqliteCommand command, ClipSearchFilters filters, bool hasSearch)
    {
        if (hasSearch && !filters.UseRegex)
        {
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText));
        }

        if (filters.ContentType is { } contentType)
        {
            command.Parameters.AddWithValue("$contentType", contentType.ToStorageValue());
        }

        command.Parameters.AddWithValue("$limit", filters.Limit);
        command.Parameters.AddWithValue("$offset", filters.Offset);
    }

    private static string BuildFtsExpression(string searchText)
    {
        var tokens = searchText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Select(static token => $"\"{token}\"*");

        var expression = string.Join(" AND ", tokens);
        return string.IsNullOrWhiteSpace(expression) ? "*" : expression;
    }

    private static ClipEntry ReadClip(SqliteDataReader reader)
    {
        var lastCopiedAt = ParseTimestamp(reader.IsDBNull(9) ? null : reader.GetString(9))
            ?? DateTimeOffset.UtcNow;
        var firstCopiedAt = ParseTimestamp(reader.IsDBNull(8) ? null : reader.GetString(8))
            ?? lastCopiedAt;

        return new ClipEntry
        {
            Id = reader.GetInt64(0),
            Content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            ContentType = ContentTypeExtensions.FromStorageValue(reader.GetString(2)),
            SourceApp = reader.IsDBNull(3) ? null : reader.GetString(3),
            Hash = reader.GetString(4),
            IsFavorite = reader.GetInt64(5) == 1,
            IsSensitive = reader.GetInt64(6) == 1,
            CopyCount = reader.IsDBNull(7) ? 1 : Convert.ToInt32(reader.GetInt64(7), CultureInfo.InvariantCulture),
            FirstCopiedAt = firstCopiedAt,
            LastCopiedAt = lastCopiedAt,
            ByteSize = reader.GetInt64(10),
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
            command.Parameters.AddWithValue("$search", BuildFtsExpression(filters.SearchText));
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

    private static async Task<string?> ExecuteScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is DBNull or null ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

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

    private static string ComputeHash(ContentType contentType, string content)
    {
        var input = $"{contentType.ToStorageValue()}::{content}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static Regex BuildSearchRegex(string searchText, bool caseSensitive)
        => new(searchText, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsRegexMatch(ClipEntry clip, Regex regex) =>
        regex.IsMatch(clip.Content) ||
        (!string.IsNullOrWhiteSpace(clip.SourceApp) && regex.IsMatch(clip.SourceApp));

    private static string BuildRichTextSampleContent() => """
        <html>
          <body>
            <h2>Quarterly launch notes</h2>
            <p>Prepared for <strong>Clipthrough</strong> design review.</p>
            <ul>
              <li>Hero layout simplified to a compact toolbar</li>
              <li>Sensitive clips receive a high-contrast border</li>
              <li>File previews now support copy and open actions</li>
            </ul>
            <p><em>Next step:</em> finalize the interaction polish.</p>
          </body>
        </html>
        """;

    private static string BuildImageSampleContent()
    {
        const int width = 32;
        const int height = 32;
        const int bytesPerPixel = 3;
        var rowSize = ((width * bytesPerPixel + 3) / 4) * 4;
        var pixelDataSize = rowSize * height;
        var fileSize = 54 + pixelDataSize;
        var bytes = new byte[fileSize];

        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(pixelDataSize).CopyTo(bytes, 34);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = 54 + ((height - 1 - y) * rowSize) + (x * bytesPerPixel);
                var isUpperHalf = y < height / 2;
                var isLeftHalf = x < width / 2;

                bytes[pixelIndex] = isUpperHalf ? (byte)0xA3 : (byte)0x33;
                bytes[pixelIndex + 1] = isLeftHalf ? (byte)0x7C : (byte)0xD1;
                bytes[pixelIndex + 2] = (byte)(0x40 + ((x + y) % 64));
            }
        }

        return $"data:image/bmp;base64,{Convert.ToBase64String(bytes)}";
    }

    private static async Task<string> BuildFileSampleContentAsync(CancellationToken cancellationToken)
    {
        var sampleDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipthrough", "SampleFiles");
        Directory.CreateDirectory(sampleDirectory);

        var files = new[]
        {
            Path.Combine(sampleDirectory, "Budget.txt"),
            Path.Combine(sampleDirectory, "Launch Notes.md"),
            Path.Combine(sampleDirectory, "Action Items.csv"),
        };

        var contents = new[]
        {
            "Quarterly budget draft\nMarketing,25000\nEngineering,42000\nOps,18000\n",
            "# Launch Notes\n\n- Toolbar condensed\n- Infinite scroll enabled\n- File actions added\n",
            "Owner,Task,Status\nAlex,Review favorites,Done\nSam,Verify regex,In Progress\n",
        };

        for (var index = 0; index < files.Length; index++)
        {
            await File.WriteAllTextAsync(files[index], contents[index], cancellationToken);
        }

        return string.Join(Environment.NewLine, files);
    }

    private static async Task<bool> HasSeedMarkerAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM app_metadata WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", SampleDataSeedMarkerKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    private static async Task SetSeedMarkerAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_metadata (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", SampleDataSeedMarkerKey);
        command.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> EnsureRuleAsync(SqliteConnection connection, SqliteTransaction transaction, SensitivityMatch match, CancellationToken cancellationToken)
    {
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

