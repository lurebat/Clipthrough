namespace Clipthrough.Services.Platform;

/// <summary>
/// No-op source application resolver for non-Windows platforms.
/// </summary>
public sealed class NullSourceApplicationResolver : ISourceApplicationResolver
{
    public ClipboardSourceApplicationInfo? TryResolve(bool includeIcon = true) => null;

    public byte[]? TryResolveIcon(string? processPath) => null;
}
