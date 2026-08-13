using System;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Clipthrough.Localization;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class ClipFileItemViewModel : ViewModelBase
{
    private readonly ISystemInteractionService _systemInteractionService;
    private readonly Action<string> _statusSink;
    private bool _exists = true;
    private bool _isDirectory;

    public ClipFileItemViewModel(string filePath, ISystemInteractionService systemInteractionService, Action<string> statusSink)
    {
        FilePath = NormalizePath(filePath);
        _systemInteractionService = systemInteractionService;
        _statusSink = statusSink;

        CopyPathCommand = ReactiveCommand.CreateFromTask(CopyPathAsync);
        OpenCommand = ReactiveCommand.CreateFromTask(OpenAsync);
        OpenContainingFolderCommand = ReactiveCommand.CreateFromTask(OpenContainingFolderAsync);
        ObserveCommandErrors();
    }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string DirectoryPath => _isDirectory
        ? FilePath
        : Path.GetDirectoryName(FilePath) ?? string.Empty;

    /// <summary>
    /// Starts out optimistic and is corrected by <see cref="RefreshAvailabilityAsync"/>.
    ///
    /// This used to be a getter calling File.Exists, which a binding re-evaluates on
    /// the UI thread. Measured: File.Exists on a UNC path whose host does not answer
    /// blocks for 51 seconds - and the getter called it twice, with DirectoryPath
    /// adding a third probe. Copying a file from a share and then losing the VPN was
    /// enough to freeze the window for minutes every time the clip was selected.
    /// </summary>
    public bool Exists
    {
        get => _exists;
        private set
        {
            if (_exists == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _exists, value);
            this.RaisePropertyChanged(nameof(AvailabilityText));
        }
    }

    public string AvailabilityText => Exists ? AppText.AvailabilityAvailable : AppText.AvailabilityMissing;

    public string CopyLabel => AppText.CopyButtonLabel;

    public string OpenLabel => AppText.OpenButtonLabel;

    public string FolderLabel => AppText.FolderButtonLabel;

    public ReactiveCommand<Unit, Unit> CopyPathCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenContainingFolderCommand { get; }

    /// <summary>
    /// Probes the filesystem off the calling thread. Callers must not await this in a
    /// loop-per-item on the thread pool: an unreachable share blocks a whole thread
    /// for its timeout, so the probes are run one at a time.
    /// </summary>
    public async Task RefreshAvailabilityAsync()
    {
        var path = FilePath;
        var probe = await Task.Run(() =>
        {
            // Directory first, so an existing directory costs one probe rather than
            // a failed File.Exists followed by a second full timeout.
            var isDirectory = Directory.Exists(path);
            return (Exists: isDirectory || File.Exists(path), IsDirectory: isDirectory);
        }).ConfigureAwait(true);

        if (_isDirectory != probe.IsDirectory)
        {
            _isDirectory = probe.IsDirectory;
            this.RaisePropertyChanged(nameof(DirectoryPath));
        }

        Exists = probe.Exists;
    }

    private async Task CopyPathAsync()
    {
        try
        {
            await _systemInteractionService.CopyTextAsync(FilePath);
            _statusSink(AppText.FormatCopiedPath(FileName));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copy path failed for '{FilePath}': {ex}");
            _statusSink(AppText.FormatCopyFailed(ex.Message));
        }
    }

    private async Task OpenAsync()
    {
        try
        {
            await _systemInteractionService.OpenPathAsync(FilePath);
            _statusSink(AppText.FormatOpenedFile(FileName));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Open path failed for '{FilePath}': {ex}");
            _statusSink(AppText.FormatOpenFailed(ex.Message));
        }
    }

    private async Task OpenContainingFolderAsync()
    {
        try
        {
            await _systemInteractionService.OpenContainingDirectoryAsync(FilePath);
            _statusSink(AppText.FormatOpenedContainingFolder(FileName));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Open containing folder failed for '{FilePath}': {ex}");
            _statusSink(AppText.FormatFolderOpenFailed(ex.Message));
        }
    }

    private static string NormalizePath(string filePath) => string.IsNullOrWhiteSpace(filePath)
        ? string.Empty
        : filePath.Trim().Trim('"');
}


