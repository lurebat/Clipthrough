using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IImageEditorService
{
    /// <summary>
    /// Opens the editor for <paramref name="imageBytes"/> and returns the result.
    /// </summary>
    /// <returns>
    /// The edited image, or <see langword="null"/> when the user cancelled.
    /// </returns>
    /// <remarks>
    /// A third answer is representable and every caller must handle it: an
    /// implementation can return a zero-length array, from an editor that saved
    /// nothing or a host that produced an empty buffer. It is not a cancellation
    /// and it is not an image, and a caller that checks only for null will store
    /// an empty clip or fail decoding it later.
    ///
    /// Check <c>is not { Length: &gt; 0 }</c> rather than <c>is not null</c>. Both
    /// current callers already do; this is written down so the next one does too,
    /// since nothing in the signature says it.
    /// </remarks>
    Task<byte[]?> EditImageAsync(byte[] imageBytes, string? imageFilePath = null, CancellationToken cancellationToken = default);
}
