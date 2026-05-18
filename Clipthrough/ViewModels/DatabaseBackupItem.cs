using System;
using Clipthrough.Services;

namespace Clipthrough.ViewModels;

public sealed class DatabaseBackupItem
{
    public DatabaseBackupItem(DatabaseBackupInfo info)
    {
        Path = info.Path;
        Timestamp = info.Timestamp.ToLocalTime();
        Size = info.Size;
    }

    public string Path { get; }
    public DateTimeOffset Timestamp { get; }
    public long Size { get; }

    public string DisplayLabel
    {
        get
        {
            var sizeKb = Size / 1024.0;
            var sizeText = sizeKb < 1024
                ? $"{sizeKb:F0} KB"
                : $"{sizeKb / 1024:F1} MB";
            var name = System.IO.Path.GetFileName(Path);
            return $"{Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}  -  {name}  -  {sizeText}";
        }
    }
}
