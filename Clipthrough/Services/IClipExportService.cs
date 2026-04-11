using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IClipExportService
{
    Task<string> ExportAsync(ClipEntry clip, CancellationToken cancellationToken = default);
}
