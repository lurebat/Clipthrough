using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipExportService
{
    Task<ClipExportResult> ExportAsync(ClipEntry clip, CancellationToken cancellationToken = default);
}
