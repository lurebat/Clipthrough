using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// F-34. ClipAngel import was tested only on rejection paths - no fixture of a
/// real (RC4-encrypted) database existed, so no test reached the row loop and
/// the per-blob cap from closed finding #19 could be raised a thousandfold
/// without a test noticing. The import now runs over a plain copy a caller
/// supplies, so a plain fixture exercises it.
/// </summary>
public sealed partial class ClipAngelImportServiceTests
{
    private const long BlobCap = AppSettings.MaxMaxClipSizeBytes;

    /// <summary>Records what the importer asks the store to capture.</summary>
    public class RecordingStore : DispatchProxy
    {
        public List<ClipCaptureRequest> Captured { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            nameof(IClipStoreService.CaptureBatchAsync) => Record((IReadOnlyList<ClipCaptureRequest>)args![0]!),
            nameof(IClipStoreService.ApplyMaintenanceAsync) => Task.FromResult(new ClipMaintenanceResult()),
            _ => throw new NotSupportedException(targetMethod.Name),
        };

        private Task<BulkCaptureResult> Record(IReadOnlyList<ClipCaptureRequest> batch)
        {
            Captured.AddRange(batch);
            return Task.FromResult(new BulkCaptureResult(batch.Count, 0));
        }
    }

    private static string BuildPlainClipAngelDatabase(string directory)
    {
        var path = Path.Combine(directory, "clipangel-plain.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE Clips (Type TEXT, Text TEXT, Title TEXT, Application TEXT, Window TEXT, Created TEXT,
                                Binary BLOB, RichText TEXT, HtmlText TEXT, Url TEXT, Favorite INTEGER, AppPath TEXT);
            INSERT INTO Clips VALUES ('txt',  'plain words', 'Title', 'notepad.exe', 'Untitled', '2024-01-02 10:00:00', NULL, NULL, NULL, NULL, 1, 'C:\\n.exe');
            INSERT INTO Clips VALUES ('html', 'rich words',  NULL,   'edge.exe',    'Page',     '2024-01-02 10:01:00', NULL, NULL, '<b>rich words</b>', 'https://example.com', 0, NULL);
            INSERT INTO Clips VALUES ('img',  NULL,          NULL,   'paint.exe',   'Canvas',   '2024-01-02 10:02:00', x'89504E47010203', NULL, NULL, NULL, 0, NULL);
            INSERT INTO Clips VALUES ('img',  NULL,          NULL,   'paint.exe',   'Huge',     '2024-01-02 10:03:00', zeroblob({BlobCap + 1}), NULL, NULL, NULL, 0, NULL);
            INSERT INTO Clips VALUES ('txt',  '',            NULL,   NULL,          NULL,       '2024-01-02 10:04:00', NULL, NULL, NULL, NULL, 0, NULL);
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static (ClipAngelImportService Service, RecordingStore Store) Recording()
    {
        var proxy = DispatchProxy.Create<IClipStoreService, RecordingStore>();
        return (new ClipAngelImportService(proxy), (RecordingStore)(object)proxy);
    }

    [Fact]
    public async Task APlainDatabase_ImportsEachKindOfRow_AndDropsTheOverCapBlob()
    {
        var dir = Directory.CreateTempSubdirectory("ct-clipangel-").FullName;
        try
        {
            var fixture = BuildPlainClipAngelDatabase(dir);
            var (service, store) = Recording();
            string? temp = null;

            var result = await service.ImportCoreAsync(
                _ => { temp = Path.Combine(dir, "temp-copy.db"); File.Copy(fixture, temp); return Task.FromResult(temp); },
                progress: null,
                CancellationToken.None);

            Assert.Equal(3, result.Imported);
            Assert.Equal(2, result.Skipped);
            Assert.Equal(0, result.Failed);
            Assert.All(store.Captured, r => Assert.True((r.ContentBytes?.LongLength ?? 0) <= BlobCap));
            Assert.DoesNotContain(store.Captured, r => r.SourceWindowTitle == "Huge");

            var text = Assert.Single(store.Captured, r => r.ContentType == ContentType.Text);
            Assert.Equal("plain words", text.ContentText);
            Assert.True(text.IsFavorite);
            Assert.Equal("notepad.exe", text.SourceApp);
            Assert.NotNull(text.CapturedAtOverride);

            var rich = Assert.Single(store.Captured, r => r.ContentType == ContentType.RichText);
            Assert.Equal(ClipContentFormat.Html, rich.ContentFormat);
            Assert.Equal("https://example.com", rich.SourceUrl);

            var image = Assert.Single(store.Captured, r => r.ContentType == ContentType.Image);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, image.ContentBytes);

            Assert.False(File.Exists(temp), "the decrypted temp copy outlived a successful import");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AnImportThatThrowsMidway_StillDeletesTheTempCopy()
    {
        var dir = Directory.CreateTempSubdirectory("ct-clipangel-").FullName;
        try
        {
            var fixture = BuildPlainClipAngelDatabase(dir);
            var service = new ClipAngelImportService(new ThrowingClipStore());
            var temp = Path.Combine(dir, "temp-copy.db");

            await Assert.ThrowsAnyAsync<Exception>(() => service.ImportCoreAsync(
                _ => { File.Copy(fixture, temp); return Task.FromResult(temp); },
                progress: null,
                CancellationToken.None));

            SqliteConnection.ClearAllPools();
            Assert.False(File.Exists(temp), "the decrypted temp copy outlived a failed import");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}