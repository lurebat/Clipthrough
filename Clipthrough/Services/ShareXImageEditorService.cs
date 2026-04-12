using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ShareX.ImageEditor.Hosting;

namespace Clipthrough.Services;

public sealed class ShareXImageEditorService : IImageEditorService
{
    public Task<byte[]?> EditImageAsync(byte[] imageBytes, string? imageFilePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream(imageBytes, writable: false);
        var result = AvaloniaIntegration.ShowEditorDialog(
            stream,
            new ImageEditorOptions
            {
                ZoomToFitOnOpen = true,
                UseSystemTheme = true,
                UseSystemAccentColor = true,
            },
            taskMode: true,
            imageFilePath: imageFilePath);

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}
