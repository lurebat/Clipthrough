namespace Clipthrough.Services;

/// <summary>
/// Resolves information about the application that currently owns the clipboard.
/// Implementations are platform-specific.
/// </summary>
public interface ISourceApplicationResolver
{
    ClipboardSourceApplicationInfo? TryResolve(bool includeIcon = true);

    byte[]? TryResolveIcon(string? processPath);

    /// <summary>
    /// The id of the process that owns the clipboard, or null when there is no
    /// owner or it cannot be determined.
    /// </summary>
    int? TryGetClipboardOwnerProcessId();
}
