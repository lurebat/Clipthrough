using System;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class ClipFileItemViewModel : ViewModelBase
{
    private readonly ISystemInteractionService _systemInteractionService;
    private readonly Action<string> _statusSink;

    public ClipFileItemViewModel(string filePath, ISystemInteractionService systemInteractionService, Action<string> statusSink)
    {
        FilePath = NormalizePath(filePath);
        _systemInteractionService = systemInteractionService;
        _statusSink = statusSink;

        CopyPathCommand = ReactiveCommand.CreateFromTask(CopyPathAsync);
        OpenCommand = ReactiveCommand.CreateFromTask(OpenAsync);
        OpenContainingFolderCommand = ReactiveCommand.CreateFromTask(OpenContainingFolderAsync);
    }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string DirectoryPath => Directory.Exists(FilePath)
        ? FilePath
        : Path.GetDirectoryName(FilePath) ?? string.Empty;

    public bool Exists => File.Exists(FilePath) || Directory.Exists(FilePath);

    public string AvailabilityText => Exists ? "Available" : "Missing";

    public ReactiveCommand<Unit, Unit> CopyPathCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenContainingFolderCommand { get; }

    private async Task CopyPathAsync()
    {
        try
        {
            await _systemInteractionService.CopyTextAsync(FilePath);
            _statusSink($"Copied path: {FileName}.");
        }
        catch (Exception ex)
        {
            _statusSink($"Copy failed: {ex.Message}");
        }
    }

    private async Task OpenAsync()
    {
        try
        {
            await _systemInteractionService.OpenPathAsync(FilePath);
            _statusSink($"Opened: {FileName}.");
        }
        catch (Exception ex)
        {
            _statusSink($"Open failed: {ex.Message}");
        }
    }

    private async Task OpenContainingFolderAsync()
    {
        try
        {
            await _systemInteractionService.OpenContainingDirectoryAsync(FilePath);
            _statusSink($"Opened containing folder for {FileName}.");
        }
        catch (Exception ex)
        {
            _statusSink($"Folder open failed: {ex.Message}");
        }
    }

    private static string NormalizePath(string filePath) => string.IsNullOrWhiteSpace(filePath)
        ? string.Empty
        : filePath.Trim().Trim('"');
}


