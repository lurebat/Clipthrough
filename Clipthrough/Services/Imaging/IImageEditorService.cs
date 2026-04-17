using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IImageEditorService
{
    Task<byte[]?> EditImageAsync(byte[] imageBytes, string? imageFilePath = null, CancellationToken cancellationToken = default);
}
