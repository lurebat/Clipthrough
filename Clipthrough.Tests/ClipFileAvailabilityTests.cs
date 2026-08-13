using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// Availability used to be probed from property getters, which an Avalonia binding
/// re-evaluates on the UI thread. Measured on this machine: File.Exists against a
/// UNC path whose host does not answer blocks for 51 seconds, and a missing path
/// cost two of those probes plus a third from DirectoryPath. The contract these
/// tests defend is that no getter touches the filesystem at all.
/// </summary>
public class ClipFileAvailabilityTests
{
    private static readonly MethodInfo FileExists =
        typeof(File).GetMethod(nameof(File.Exists), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo DirectoryExists =
        typeof(Directory).GetMethod(nameof(Directory.Exists), BindingFlags.Public | BindingFlags.Static)!;

    [Theory]
    [InlineData(nameof(ClipFileItemViewModel.Exists))]
    [InlineData(nameof(ClipFileItemViewModel.AvailabilityText))]
    [InlineData(nameof(ClipFileItemViewModel.DirectoryPath))]
    public void PropertyGetters_DoNoFilesystemIo(string propertyName)
    {
        var getter = typeof(ClipFileItemViewModel).GetProperty(propertyName)!.GetGetMethod()!;

        Assert.Equal(0, IlCallScanner.CountCallsIn(getter, FileExists));
        Assert.Equal(0, IlCallScanner.CountCallsIn(getter, DirectoryExists));
    }

    /// <summary>
    /// The probe itself must leave the calling thread, or moving it out of the getters
    /// would only relocate the freeze into whoever starts it.
    /// </summary>
    [Fact]
    public void RefreshAvailability_RunsTheProbeOffTheCallingThread()
    {
        var taskRunOverloads = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(Task.Run))
            .Select(m => m.IsGenericMethodDefinition ? m.GetGenericMethodDefinition() : m)
            .ToArray();

        var refresh = typeof(ClipFileItemViewModel)
            .GetMethod(nameof(ClipFileItemViewModel.RefreshAvailabilityAsync))!;

        // Across all overloads: which one the compiler picks is not the contract.
        Assert.True(taskRunOverloads.Sum(m => IlCallScanner.CountCallsIn(refresh, m)) > 0);
    }

    /// <summary>
    /// A probe that cannot be cancelled and is not serialized turns a run of arrow-key
    /// selections over network paths into one blocked pool thread per selection.
    /// </summary>
    [Fact]
    public void FileAvailabilityBatch_IsSerializedAndSupersedable()
    {
        var vm = typeof(Clipthrough.ViewModels.MainWindowViewModel);
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        var batch = vm.GetMethod("RefreshFileAvailabilityAsync", Any)!;
        var waitAsync = typeof(SemaphoreSlim).GetMethods()
            .Where(m => m.Name == nameof(SemaphoreSlim.WaitAsync))
            .ToArray();
        Assert.True(waitAsync.Sum(m => IlCallScanner.CountCallsIn(batch, m)) > 0);

        var replace = vm.GetMethod("ReplaceSelectedClipFiles", Any)!;
        var cancel = typeof(CancellationTokenSource).GetMethod(nameof(CancellationTokenSource.Cancel), Type.EmptyTypes)!;
        Assert.True(IlCallScanner.CountCallsIn(replace, cancel) > 0);
    }

    [Fact]
    public async Task RefreshAvailability_ReportsAMissingFileAsMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "clipthrough-missing-" + Guid.NewGuid().ToString("N"));
        var item = Create(missing);

        // Optimistic until the probe lands, so a healthy local file never flickers
        // through a "missing" state on selection.
        Assert.True(item.Exists);

        var changed = new List<string>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        await item.RefreshAvailabilityAsync();

        Assert.False(item.Exists);
        Assert.Contains(nameof(ClipFileItemViewModel.Exists), changed);
        Assert.Contains(nameof(ClipFileItemViewModel.AvailabilityText), changed);
    }

    [Fact]
    public async Task RefreshAvailability_ReportsAnExistingFileAsAvailable()
    {
        var path = Path.Combine(Path.GetTempPath(), "clipthrough-present-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "x");
        try
        {
            var item = Create(path);
            await item.RefreshAvailabilityAsync();

            Assert.True(item.Exists);
            Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), item.DirectoryPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A directory is its own containing folder. Getting this from the cached probe
    /// rather than a fresh Directory.Exists is what keeps the getter free.
    /// </summary>
    [Fact]
    public async Task RefreshAvailability_TreatsADirectoryAsItsOwnFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipthrough-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var item = Create(dir);
            await item.RefreshAvailabilityAsync();

            Assert.True(item.Exists);
            Assert.Equal(dir, item.DirectoryPath);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    private static ClipFileItemViewModel Create(string path)
        => new(path, new TestSystemInteractionService(), _ => { });
}
