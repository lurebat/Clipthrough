using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Builds drag-and-drop payloads from stored clips (drag OUT of the popup)
/// and turns incoming <see cref="IDataTransfer"/> drops into capture requests
/// (drag IN to the popup).
/// </summary>
public interface IDragDropService
{
    /// <summary>
    /// Build a multi-format <see cref="IDataTransfer"/> carrying the supplied
    /// clips so any drop target can pick the representation it understands
    /// (text, HTML/RTF, image as file + PNG bytes, or real file references).
    /// </summary>
    Task<IDataTransfer> BuildDragPayloadAsync(IReadOnlyList<ClipEntry> clips, IStorageProvider storageProvider);

    /// <summary>
    /// Parse an incoming drop into one or more <see cref="ClipCaptureRequest"/>
    /// instances ready to feed through <c>IClipStoreService.CaptureFastAsync</c>.
    /// Each returned request has <see cref="ClipCaptureRequest.ImportKind"/>
    /// stamped with <c>"drag_drop"</c> so the UI can tag it.
    /// </summary>
    Task<IReadOnlyList<ClipCaptureRequest>> TryBuildCaptureRequestsAsync(IDataTransfer drop, ClipboardSourceApplicationInfo? sourceInfo);
}
