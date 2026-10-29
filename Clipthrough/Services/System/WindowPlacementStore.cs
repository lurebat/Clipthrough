using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Stores the main window's placement as window.json beside settings.json.
/// </summary>
public sealed class WindowPlacementStore : IWindowPlacementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly object _gate = new();
    private SavedWindowPlacement? _lastWritten;
    private Task _pendingWrite = Task.CompletedTask;

    public WindowPlacementStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipthrough",
            "window.json"))
    {
    }

    public WindowPlacementStore(string path)
    {
        _path = path;
    }

    public SavedWindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var placement = JsonSerializer.Deserialize<SavedWindowPlacement>(File.ReadAllText(_path), JsonOptions);
            // A hand-edited or truncated file must not size the window to nothing.
            if (placement is null || !(placement.Width > 0) || !(placement.Height > 0))
            {
                return null;
            }

            lock (_gate)
            {
                _lastWritten = placement;
            }

            return placement;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"Could not read the window placement from {_path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Completes when the last <see cref="Save"/> has reached the disk.</summary>
    internal Task FlushAsync() => _pendingWrite;

    public void Save(SavedWindowPlacement placement)
    {
        lock (_gate)
        {
            // The window is hidden after every paste; most of those leave it
            // where it was.
            if (placement == _lastWritten)
            {
                return;
            }

            _lastWritten = placement;
        }

        // Off the UI thread: this is called as the window hides.
        _pendingWrite = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                lock (_gate)
                {
                    if (placement != _lastWritten)
                    {
                        return;
                    }

                    File.WriteAllText(_path, JsonSerializer.Serialize(placement, JsonOptions));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not save the window placement to {_path}: {ex.Message}");
            }
        });
    }
}