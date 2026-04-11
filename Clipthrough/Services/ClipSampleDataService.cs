using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

public sealed class ClipSampleDataService : IClipSampleDataService
{
    private const string SampleDataSeedMarkerKey = "seed:sample-data:v2";
    private const int TargetSampleSeedCount = 250;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClipStoreService _clipStoreService;

    public ClipSampleDataService(SqliteConnectionFactory connectionFactory, IClipStoreService clipStoreService)
    {
        _connectionFactory = connectionFactory;
        _clipStoreService = clipStoreService;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
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
            CreateTextTemplate("SELECT * FROM users WHERE email LIKE '%@corp.com' ORDER BY created_at DESC LIMIT 100;", "DBeaver", true),
            CreateTextTemplate("server=prod-sql;user id=report_user;password=Sup3rSecret!;database=warehouse;", "Azure Data Studio"),
            CreateTextTemplate("AKIA0EXAMPLEKEY123456", "Visual Studio Code"),
            CreateTextTemplate("npm install AvaloniaUI.DiagnosticsSupport --prerelease", "PowerShell"),
            CreateTextTemplate("https://github.com/AvaloniaUI/Avalonia", "Chrome", true),
            CreateTextTemplate("password = Tr0ub4dor&3", "Rider"),
            CreateTextTemplate("Quarterly customer call notes and follow-up actions.", "Notion"),
            CreateFilesTemplate("Files copied: Budget.xlsx; Strategy.pptx; Notes.docx", "Explorer"),
            CreateRichTextTemplate("""
                <html>
                  <body>
                    <h2>Quarterly launch notes</h2>
                    <p>Prepared for <strong>Clipthrough</strong> design review.</p>
                    <ul>
                      <li>Hero layout simplified to a compact toolbar</li>
                      <li>Sensitive clips receive a high-contrast border</li>
                      <li>File previews now support copy and open actions</li>
                    </ul>
                  </body>
                </html>
                """, "Outlook"),
            CreateImageTemplate("Snipping Tool"),
        };

        var clipsToSeed = TargetSampleSeedCount - existingCount;
        var seedRunTag = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        for (var index = 0; index < clipsToSeed; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var template = templates[(existingCount + index) % templates.Length];
            var sequence = existingCount + index + 1;
            await _clipStoreService.CaptureAsync(template(seedRunTag, sequence), cancellationToken);
        }

        await SetSeedMarkerAsync(connection, cancellationToken);
    }

    private async Task EnsureFeaturedSamplesAsync(CancellationToken cancellationToken)
    {
        await _clipStoreService.CaptureAsync(CreateRichTextTemplate("""
            <html>
              <body>
                <h2>Quarterly launch notes</h2>
                <p>Prepared for <strong>Clipthrough</strong> design review.</p>
              </body>
            </html>
            """, "Outlook")(null, null), cancellationToken);

        await _clipStoreService.CaptureAsync(CreateImageTemplate("Snipping Tool")(null, null), cancellationToken);

        var fileContent = await BuildFileSampleContentAsync(cancellationToken);
        await _clipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Files,
            ContentFormat = ClipContentFormat.FileList,
            ContentText = fileContent,
            ContentBytes = Encoding.UTF8.GetBytes(fileContent),
            SourceApp = "Explorer",
            IncrementExistingCopyCount = false,
        }, cancellationToken);
    }

    private static Func<string?, int?, ClipCaptureRequest> CreateTextTemplate(string content, string sourceApp, bool isFavorite = false)
        => (seedRunTag, sequence) =>
        {
            var seededContent = seedRunTag is null || sequence is null
                ? content
                : $"{content} [seed:{seedRunTag}:{sequence.Value:D6}]";

            return new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = seededContent,
                ContentBytes = Encoding.UTF8.GetBytes(seededContent),
                SourceApp = sourceApp,
                IsFavorite = isFavorite,
                IncrementExistingCopyCount = false,
            };
        };

    private static Func<string?, int?, ClipCaptureRequest> CreateFilesTemplate(string content, string sourceApp)
        => (seedRunTag, sequence) =>
        {
            var seededContent = seedRunTag is null || sequence is null
                ? content
                : $"{content} [seed:{seedRunTag}:{sequence.Value:D6}]";

            return new ClipCaptureRequest
            {
                ContentType = ContentType.Files,
                ContentFormat = ClipContentFormat.FileList,
                ContentText = seededContent,
                ContentBytes = Encoding.UTF8.GetBytes(seededContent),
                SourceApp = sourceApp,
                IncrementExistingCopyCount = false,
            };
        };

    private static Func<string?, int?, ClipCaptureRequest> CreateRichTextTemplate(string content, string sourceApp)
        => (seedRunTag, sequence) =>
        {
            var seededContent = seedRunTag is null || sequence is null
                ? content
                : $"{content}<!-- seed:{seedRunTag}:{sequence.Value:D6} -->";

            return new ClipCaptureRequest
            {
                ContentType = ContentType.RichText,
                ContentFormat = ClipContentFormat.Html,
                ContentText = ClipDisplayFormatter.RenderRichContent(seededContent),
                ContentBytes = Encoding.UTF8.GetBytes(seededContent),
                SourceApp = sourceApp,
                IncrementExistingCopyCount = false,
            };
        };

    private static Func<string?, int?, ClipCaptureRequest> CreateImageTemplate(string sourceApp)
        => (seedRunTag, sequence) =>
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

            var colorOffset = sequence.GetValueOrDefault() % 32;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixelIndex = 54 + ((height - 1 - y) * rowSize) + (x * bytesPerPixel);
                    bytes[pixelIndex] = (byte)(0x30 + ((x + colorOffset) % 64));
                    bytes[pixelIndex + 1] = (byte)(0x60 + ((y + colorOffset) % 96));
                    bytes[pixelIndex + 2] = (byte)(0x90 + ((x + y + colorOffset) % 96));
                }
            }

            return new ClipCaptureRequest
            {
                ContentType = ContentType.Image,
                ContentFormat = ClipContentFormat.Bitmap,
                ContentBytes = bytes,
                SourceApp = sourceApp,
                ImageWidth = width,
                ImageHeight = height,
                IncrementExistingCopyCount = false,
            };
        };

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
}
