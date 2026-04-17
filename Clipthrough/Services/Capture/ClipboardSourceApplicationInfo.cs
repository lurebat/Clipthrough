namespace Clipthrough.Services;

public sealed record ClipboardSourceApplicationInfo(string? Name, string? Path, byte[]? IconBytes, string? WindowTitle = null);
