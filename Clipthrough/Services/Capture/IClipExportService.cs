using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipExportService
{
    /// <summary>
    /// The user's explicit Export: a folder under Documents\Clipthrough Exports
    /// with the payload, a rendered text copy and metadata.json. It stays.
    /// </summary>
    Task<ClipExportResult> ExportAsync(ClipEntry clip, CancellationToken cancellationToken = default);

    /// <summary>
    /// A working copy for an external tool (Open in editor, Compare): written
    /// under a per-session temp folder, with no metadata.json, and deleted when
    /// the service is disposed at exit (OD-3a).
    /// </summary>
    Task<ClipExportResult> ExportForToolAsync(ClipEntry clip, CancellationToken cancellationToken = default);
}
