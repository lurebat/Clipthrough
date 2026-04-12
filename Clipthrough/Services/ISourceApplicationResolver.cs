namespace Clipthrough.Services;

/// <summary>
/// Resolves information about the application that currently owns the clipboard.
/// Implementations are platform-specific.
/// </summary>
public interface ISourceApplicationResolver
{
    ClipboardSourceApplicationInfo? TryResolve();
}
