using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

// Imports a legacy ClipAngel SQLite database (hard-coded password "Magic67234784",
// SDS 1.0.79 codec: SHA-1 + RC4 via Windows CAPI). The file is decrypted into a
// temporary plain-text SQLite copy, read with Microsoft.Data.Sqlite, and each row
// is replayed through IClipStoreService.CaptureAsync with the original timestamp.
public sealed class ClipAngelImportService : IClipAngelImportService
{
    private const string ClipAngelPassword = "Magic67234784";
    // Cap import file and single-blob sizes to prevent OOM on hostile/malformed inputs.
    private const long MaxImportFileSizeBytes = 512L * 1024 * 1024; // 512 MB
    private const long MaxImportBlobSizeBytes = AppSettings.MaxMaxClipSizeBytes; // 32 MB

    private readonly IClipStoreService _clipStore;

    public ClipAngelImportService(IClipStoreService clipStore)
    {
        _clipStore = clipStore;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<ClipAngelImportPreview> PreviewAsync(string dbPath, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("ClipAngel import is only supported on Windows.");

        var temp = await DecryptToTempAsync(dbPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection($"Data Source={temp};Mode=ReadOnly");
            conn.StateChange += (_, e) =>
            {
                if (e.CurrentState == System.Data.ConnectionState.Open)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA busy_timeout = 5000;";
                    cmd.ExecuteNonQuery();
                }
            };
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            int total;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM Clips;";
                total = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0, CultureInfo.InvariantCulture);
            }

            var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(Type, ''), COUNT(*) FROM Clips GROUP BY Type;";
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var typeName = r.GetString(0);
                    if (string.IsNullOrWhiteSpace(typeName))
                        typeName = "(unknown)";
                    byType[typeName] = r.GetInt32(1);
                }
            }

            DateTimeOffset? earliest = null, latest = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MIN(Created), MAX(Created) FROM Clips;";
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    earliest = TryParseClipAngelDate(r.IsDBNull(0) ? null : r.GetValue(0));
                    latest = TryParseClipAngelDate(r.IsDBNull(1) ? null : r.GetValue(1));
                }
            }

            return new ClipAngelImportPreview(total, byType, earliest, latest);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public async Task<ClipAngelImportResult> ImportAsync(
        string dbPath,
        IProgress<ClipAngelImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("ClipAngel import is only supported on Windows.");

        var temp = await DecryptToTempAsync(dbPath, cancellationToken).ConfigureAwait(false);
        int imported = 0, skipped = 0, failed = 0;
        var errors = new List<string>();

        const int batchSize = 200;

        try
        {
            await using var conn = new SqliteConnection($"Data Source={temp};Mode=ReadOnly");
            conn.StateChange += (_, e) =>
            {
                if (e.CurrentState == System.Data.ConnectionState.Open)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA busy_timeout = 5000;";
                    cmd.ExecuteNonQuery();
                }
            };
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            int total;
            await using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM Clips;";
                total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0, CultureInfo.InvariantCulture);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    Type, Text, Title, Application, Window, Created,
                    CASE WHEN typeof(Binary) = 'blob' AND LENGTH(Binary) > {MaxImportBlobSizeBytes}
                         THEN NULL ELSE Binary END,
                    RichText, HtmlText, Url, Favorite, AppPath
                FROM Clips
                ORDER BY Created ASC;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            int processed = 0;
            var batch = new List<ClipCaptureRequest>(batchSize);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                processed++;
                var type = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);

                try
                {
                    var request = BuildCaptureRequest(reader, type);
                    if (request is null)
                    {
                        skipped++;
                    }
                    else
                    {
                        batch.Add(request);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Count < 10)
                        errors.Add($"row {processed}: {ex.Message}");
                }

                if (batch.Count >= batchSize)
                {
                    var result = await _clipStore.CaptureBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    imported += result.Imported;
                    skipped += result.Skipped;
                    batch.Clear();
                }

                if (progress is not null && (processed % 100 == 0 || processed == total))
                    progress.Report(new ClipAngelImportProgress(processed, total, type));
            }

            if (batch.Count > 0)
            {
                var result = await _clipStore.CaptureBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                imported += result.Imported;
                skipped += result.Skipped;
            }

            await _clipStore.ApplyMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temp);
        }

        return new ClipAngelImportResult(imported, skipped, failed, errors);
    }

    private static ClipCaptureRequest? BuildCaptureRequest(SqliteDataReader reader, string type)
    {
        string? text = reader.IsDBNull(1) ? null : reader.GetString(1);
        string? title = reader.IsDBNull(2) ? null : reader.GetString(2);
        string? app = reader.IsDBNull(3) ? null : reader.GetString(3);
        string? window = reader.IsDBNull(4) ? null : reader.GetString(4);
        var created = TryParseClipAngelDate(reader.IsDBNull(5) ? null : reader.GetValue(5));
        byte[]? binary = reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6);
        string? richText = reader.IsDBNull(7) ? null : reader.GetString(7);
        string? htmlText = reader.IsDBNull(8) ? null : reader.GetString(8);
        string? url = reader.IsDBNull(9) ? null : reader.GetString(9);
        bool favorite = !reader.IsDBNull(10) && Convert.ToInt64(reader.GetValue(10), CultureInfo.InvariantCulture) != 0;
        string? appPath = reader.IsDBNull(11) ? null : reader.GetString(11);

        var window_ = string.IsNullOrWhiteSpace(window) ? title : window;

        if (string.Equals(type, "img", StringComparison.OrdinalIgnoreCase))
        {
            if (binary is null || binary.Length == 0)
                return null;
            return new ClipCaptureRequest
            {
                ContentBytes = binary,
                ContentText = null,
                ContentType = ContentType.Image,
                ContentFormat = ClipContentFormat.Bitmap,
                SourceApp = app,
                SourceAppPath = appPath,
                SourceWindowTitle = window_,
                SourceUrl = url,
                IsFavorite = favorite,
                IncrementExistingCopyCount = false,
                CapturedAtOverride = created,
                SkipPostInsertMaintenance = true,
            };
        }

        if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "files", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(text))
                return null;
            return new ClipCaptureRequest
            {
                ContentBytes = Encoding.UTF8.GetBytes(text),
                ContentText = text,
                ContentType = ContentType.Files,
                ContentFormat = ClipContentFormat.FileList,
                SourceApp = app,
                SourceAppPath = appPath,
                SourceWindowTitle = window_,
                SourceUrl = url,
                IsFavorite = favorite,
                IncrementExistingCopyCount = false,
                CapturedAtOverride = created,
                SkipPostInsertMaintenance = true,
            };
        }

        // Text-ish types: "txt", "text", "html", "unicode", etc. Prefer HTML > RTF > plain.
        var body = text ?? string.Empty;
        ClipContentFormat format;
        ContentType contentType;
        if (!string.IsNullOrEmpty(htmlText))
        {
            format = ClipContentFormat.Html;
            contentType = ContentType.RichText;
        }
        else if (!string.IsNullOrEmpty(richText))
        {
            format = ClipContentFormat.Rtf;
            contentType = ContentType.RichText;
        }
        else
        {
            format = ClipContentFormat.PlainText;
            contentType = ContentType.Text;
        }

        if (string.IsNullOrEmpty(body))
            return null;

        return new ClipCaptureRequest
        {
            ContentBytes = Encoding.UTF8.GetBytes(body),
            ContentText = body,
            ContentType = contentType,
            ContentFormat = format,
            SourceApp = app,
            SourceAppPath = appPath,
            SourceWindowTitle = window_,
            SourceUrl = url,
            IsFavorite = favorite,
            IncrementExistingCopyCount = false,
            CapturedAtOverride = created,
            SkipPostInsertMaintenance = true,
        };
    }

    /// <summary>
    /// ClipAngel writes <c>DateTime.Now</c> into <c>Clips.Created</c> - local
    /// wall-clock with no offset recorded - and displays it back unconverted
    /// (<c>Main.cs:3099</c> and <c>:1268</c> in tormozit/ClipAngel). So the
    /// value has to be read as local, not UTC.
    ///
    /// Reading it as UTC "for stability" looks tidier and is wrong: it shifts
    /// every imported clip by the importing machine's offset in the normal case,
    /// which is importing your own history on your own machine. Nothing is
    /// lossless when the source timezone was never recorded; local is right
    /// whenever the machine has not moved, and no worse than UTC when it has.
    /// </summary>
    private static DateTimeOffset? TryParseClipAngelDate(object? value)
    {
        if (value is null || value is DBNull)
            return null;
        switch (value)
        {
            case DateTime dt:
                return DateTime.SpecifyKind(dt, DateTimeKind.Local);
            case DateTimeOffset dto:
                return dto;
            case string s when DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsed):
                return parsed;
            case long ticks:
                return DateTimeOffset.FromUnixTimeSeconds(ticks);
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    // ----------------------------- Decryption -----------------------------

    [SupportedOSPlatform("windows")]
    private static async Task<string> DecryptToTempAsync(string src, CancellationToken cancellationToken)
    {
        if (!File.Exists(src))
            throw new FileNotFoundException("ClipAngel database not found.", src);

        var fileInfo = new FileInfo(src);
        if (fileInfo.Length > MaxImportFileSizeBytes)
            throw new InvalidDataException($"ClipAngel database too large to import ({fileInfo.Length / (1024 * 1024)} MB; limit is {MaxImportFileSizeBytes / (1024 * 1024)} MB).");
        if (fileInfo.Length < 1024)
            throw new InvalidDataException("File too small to be a ClipAngel database.");

        // Read just the first 1024 bytes to detect the real page size from the SQLite header.
        var probe = new byte[1024];
        await using (var probeFs = File.OpenRead(src))
        {
            int probeRead = 0;
            while (probeRead < 1024)
            {
                int n = await probeFs.ReadAsync(probe.AsMemory(probeRead, 1024 - probeRead), cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                probeRead += n;
            }
            if (probeRead < 1024)
                throw new InvalidDataException("File too small to be a ClipAngel database.");
        }

        var probePlain = Rc4DecryptPage(probe);
        if (probePlain[0] != (byte)'S' || probePlain[15] != 0)
            throw new InvalidDataException("Decryption failed: file is not a ClipAngel-encrypted SQLite database, or the password does not match.");

        int pageSize = (probePlain[16] << 8) | probePlain[17];
        if (pageSize == 1) pageSize = 65536;
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
            throw new InvalidDataException($"Invalid SQLite page size: {pageSize}.");

        long totalBytes = fileInfo.Length;
        int pages = (int)(totalBytes / pageSize);
        if (pages == 0)
            throw new InvalidDataException("Database has zero pages after detection.");

        var temp = Path.Combine(Path.GetTempPath(), $"clipthrough-clipangel-{Guid.NewGuid():N}.db");
        try
        {
            // Stream-decrypt page-by-page: avoids a full in-memory copy of the entire database.
            await using var srcFs = File.OpenRead(src);
            await using var outFs = File.Create(temp);
            var page = new byte[pageSize];
            for (int p = 0; p < pages; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pageRead = 0;
                while (pageRead < pageSize)
                {
                    int n = await srcFs.ReadAsync(page.AsMemory(pageRead, pageSize - pageRead), cancellationToken).ConfigureAwait(false);
                    if (n == 0) break;
                    pageRead += n;
                }
                if (pageRead < pageSize)
                    break; // Short/partial last page — stop here.
                var plain = Rc4DecryptPage(page);
                await outFs.WriteAsync(plain.AsMemory(0, pageSize), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return temp;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Rc4DecryptPage(byte[] page)
    {
        // Mirrors the SDS 1.0.79 codec: CryptDeriveKey(CALG_RC4, SHA1(password))
        // under Microsoft Enhanced Cryptographic Provider v1.0 (PROV_RSA_FULL).
        // Each page is decrypted with a fresh RC4 keystream (no IV, full-page).
        const uint PROV_RSA_FULL = 1;
        const uint CRYPT_VERIFYCONTEXT = 0xF0000000;
        const uint CALG_SHA1 = 0x00008004;
        const uint CALG_RC4 = 0x00006801;
        const string Enhanced = "Microsoft Enhanced Cryptographic Provider v1.0";

        IntPtr prov = IntPtr.Zero, hash = IntPtr.Zero, key = IntPtr.Zero;
        try
        {
            if (!CryptAcquireContextW(out prov, null, Enhanced, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT))
                throw new InvalidOperationException($"CryptAcquireContext failed: {Marshal.GetLastWin32Error()}");
            if (!CryptCreateHash(prov, CALG_SHA1, IntPtr.Zero, 0, out hash))
                throw new InvalidOperationException($"CryptCreateHash failed: {Marshal.GetLastWin32Error()}");
            var pw = Encoding.UTF8.GetBytes(ClipAngelPassword);
            if (!CryptHashData(hash, pw, (uint)pw.Length, 0))
                throw new InvalidOperationException($"CryptHashData failed: {Marshal.GetLastWin32Error()}");
            if (!CryptDeriveKey(prov, CALG_RC4, hash, 0, out key))
                throw new InvalidOperationException($"CryptDeriveKey failed: {Marshal.GetLastWin32Error()}");

            var buf = new byte[page.Length];
            Buffer.BlockCopy(page, 0, buf, 0, page.Length);
            uint len = (uint)page.Length;
            if (!CryptDecrypt(key, IntPtr.Zero, true, 0, buf, ref len))
                throw new InvalidOperationException($"CryptDecrypt failed: {Marshal.GetLastWin32Error()}");
            return buf;
        }
        finally
        {
            if (key != IntPtr.Zero) CryptDestroyKey(key);
            if (hash != IntPtr.Zero) CryptDestroyHash(hash);
            if (prov != IntPtr.Zero) CryptReleaseContext(prov, 0);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptAcquireContextW(out IntPtr phProv, string? pszContainer, string pszProvider, uint dwProvType, uint dwFlags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptReleaseContext(IntPtr hProv, uint dwFlags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptCreateHash(IntPtr hProv, uint algId, IntPtr hKey, uint dwFlags, out IntPtr phHash);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptHashData(IntPtr hHash, byte[] pbData, uint dwDataLen, uint dwFlags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptDeriveKey(IntPtr hProv, uint algId, IntPtr hBaseData, uint dwFlags, out IntPtr phKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptDecrypt(IntPtr hKey, IntPtr hHash, bool final, uint dwFlags, byte[] pbData, ref uint pdwDataLen);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptDestroyHash(IntPtr hHash);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CryptDestroyKey(IntPtr hKey);
}
