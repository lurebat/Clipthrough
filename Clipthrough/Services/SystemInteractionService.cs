using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Clipthrough.Services;

public sealed class SystemInteractionService : ISystemInteractionService
{
    public async Task CopyTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            throw new InvalidOperationException("Clipboard access is not available.");
        }

        await clipboard.SetTextAsync(text);
    }

    public Task OpenPathAsync(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
        {
            throw new FileNotFoundException("The requested file or directory could not be found.", normalizedPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = normalizedPath,
                UseShellExecute = true,
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { normalizedPath },
                UseShellExecute = false,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { normalizedPath },
                UseShellExecute = false,
            });
        }

        return Task.CompletedTask;
    }

    public Task OpenContainingDirectoryAsync(string path)
    {
        var normalizedPath = NormalizePath(path);
        var directoryPath = Directory.Exists(normalizedPath)
            ? normalizedPath
            : Path.GetDirectoryName(normalizedPath);

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException("The containing directory could not be found.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(normalizedPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{normalizedPath}\"",
                UseShellExecute = true,
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { directoryPath },
                UseShellExecute = false,
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directoryPath,
                UseShellExecute = true,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { directoryPath },
                UseShellExecute = false,
            });
        }

        return Task.CompletedTask;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        return path.Trim().Trim('"');
    }

    private static Avalonia.Input.Platform.IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var window = desktop.MainWindow ?? desktop.Windows.FirstOrDefault(static candidate => candidate.IsVisible);
        return window?.Clipboard;
    }
}


