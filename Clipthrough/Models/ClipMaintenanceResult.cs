namespace Clipthrough.Models;

public sealed class ClipMaintenanceResult
{
    public int PurgedClipCount { get; init; }

    public long TotalStoredBytes { get; init; }

    public int TotalClipCount { get; init; }
}
