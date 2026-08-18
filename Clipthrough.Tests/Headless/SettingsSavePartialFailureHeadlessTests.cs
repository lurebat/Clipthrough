using System;
using System.IO;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Saving settings writes two files that cannot be written atomically:
/// settings.json, and storage.json - the second of which moves the database and
/// deletes the source when the path changes.
///
/// They cannot be made atomic, because undoing the storage half means moving the
/// database a second time and that can fail in its own right. So the only thing
/// that can be decided is which half is safer to leave undone, and the answer
/// has to be the destructive one. The order shipped the other way round: it
/// moved the library and then discarded every other setting if the second write
/// failed.
/// </summary>
public sealed class SettingsSavePartialFailureHeadlessTests
{
    private static void FillValidDraft(Clipthrough.ViewModels.SettingsViewModel draft, string databasePath)
    {
        draft.MaxClipSizeKilobytes = "64";
        draft.EnableNormalClipLifetime = false;
        draft.EnableSensitiveClipLifetime = false;
        draft.EnableMaxLibrarySize = false;
        draft.EnableMaxEntryCount = false;
        draft.DatabasePath = databasePath;
    }

    /// <summary>
    /// The failure that matters is a plain one - a full disk, a locked file -
    /// not the credential failure the save already tolerates on purpose.
    /// </summary>
    [AvaloniaFact]
    public async Task WhenSettingsCannotBeWrittenTheDatabaseIsNotMoved()
    {
        using var harness = MainWindowTestHarness.Create();
        var newPath = Path.Combine(Path.GetTempPath(), "ct-move-target", "clipthrough.db");
        FillValidDraft(harness.ViewModel.Settings, newPath);

        var before = harness.StorageOptions.SaveCount;
        harness.Settings.ThrowOnNextUpdate = new IOException("disk full");

        // The failure is surfaced rather than swallowed - it reaches the command,
        // which reports it - so the save is expected to throw here. What matters
        // is what it did before throwing.
        await Assert.ThrowsAsync<IOException>(
            () => harness.ViewModel.SaveSettingsCommand.Execute().ToTask());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, harness.StorageOptions.SaveCount);
    }

    /// <summary>
    /// Anti-vacuity, and the reason the assertion above is a count rather than a
    /// flag: a save that never reached storage under any circumstances would pass
    /// it just as well.
    /// </summary>
    [AvaloniaFact]
    public async Task AnOtherwiseIdenticalSaveDoesReachStorage()
    {
        using var harness = MainWindowTestHarness.Create();
        var newPath = Path.Combine(Path.GetTempPath(), "ct-move-target", "clipthrough.db");
        FillValidDraft(harness.ViewModel.Settings, newPath);

        var before = harness.StorageOptions.SaveCount;

        await harness.ViewModel.SaveSettingsCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            harness.StorageOptions.SaveCount > before,
            "the same draft with nothing failing has to reach storage, or the test above proves nothing");
    }
}
