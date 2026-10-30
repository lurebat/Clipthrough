using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Working copies for external tools (F-18, OD-3a). Open in editor and Compare
/// used to leave a plaintext copy of the clip and a metadata.json holding its
/// raw content in Documents - usually OneDrive-synced - for good, sensitive
/// clips included. The audit host held 30 such files.
/// </summary>
public sealed class ClipExportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ct-export-" + Guid.NewGuid().ToString("N"));

    public ClipExportServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    private static ClipEntry Secret() => new()
    {
        Id = 7,
        Content = "password = NOT-A-REAL-CREDENTIAL",
        ContentBytes = Encoding.UTF8.GetBytes("password = NOT-A-REAL-CREDENTIAL"),
        ContentType = ContentType.Text,
        ContentFormat = ClipContentFormat.PlainText,
        IsSensitive = true,
        Hash = "h",
        FirstCopiedAt = DateTimeOffset.UtcNow,
        LastCopiedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AToolCopy_LandsInTheSessionFolder_WithNoMetadata_AndIsGoneAfterDispose()
    {
        var documents = Path.Combine(_root, "Documents");
        var temp = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(temp);
        var service = new ClipExportService(documents, temp);

        var result = await service.ExportForToolAsync(Secret());

        Assert.StartsWith(service.SessionRoot, result.PrimaryPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.PrimaryPath));
        Assert.Empty(Directory.GetFiles(service.SessionRoot, "metadata.json", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(documents), "A tool copy was written to the export folder.");

        service.Dispose();

        Assert.False(Directory.Exists(service.SessionRoot));
    }

    [Fact]
    public async Task AnExplicitExport_StillGoesToTheExportFolder_WithMetadata()
    {
        var documents = Path.Combine(_root, "Documents");
        using var service = new ClipExportService(documents, Path.Combine(_root, "Temp"));

        var result = await service.ExportAsync(Secret());

        Assert.StartsWith(documents, result.PrimaryPath, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(documents, "metadata.json", SearchOption.AllDirectories));
    }

    /// <summary>A session that crashed leaves its folder; the next start removes it.</summary>
    [Fact]
    public void AFolderLeftByAProcessThatIsGone_IsRemovedAtStartup()
    {
        var temp = Path.Combine(_root, "Temp");
        var abandoned = Path.Combine(temp, "Clipthrough-tool-copies-999999-abc");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "secret.txt"), "x");
        var unrelated = Path.Combine(temp, "SomethingElse-999999");
        Directory.CreateDirectory(unrelated);

        using var service = new ClipExportService(Path.Combine(_root, "Documents"), temp);

        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(unrelated));
    }

    /// <summary>
    /// Rubber-duck F-06-4. A crashed session's PID can be reused by any other
    /// long-running process before the next start; the PID alone then makes
    /// the abandoned folder look live. Here the PID is this test process -
    /// certainly running - but no session holds the folder's lock any more.
    /// </summary>
    [Fact]
    public void AFolderWhosePidWasReused_ButWhoseSessionIsGone_IsRemovedAtStartup()
    {
        var temp = Path.Combine(_root, "Temp");
        var abandoned = Path.Combine(temp, $"Clipthrough-tool-copies-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "session.lock"), string.Empty);
        File.WriteAllText(Path.Combine(abandoned, "secret.txt"), "x");

        using var service = new ClipExportService(Path.Combine(_root, "Documents"), temp);

        Assert.False(Directory.Exists(abandoned));
    }

    /// <summary>A second instance that starts must not sweep a live session's copies.</summary>
    [Fact]
    public async Task ALiveSessionsFolder_IsKept_WhenAnotherInstanceStarts()
    {
        var temp = Path.Combine(_root, "Temp");
        using var live = new ClipExportService(Path.Combine(_root, "Documents"), temp);
        var copy = await live.ExportForToolAsync(Secret());

        using var second = new ClipExportService(Path.Combine(_root, "Documents"), temp);

        Assert.True(File.Exists(copy.PrimaryPath));
    }
}