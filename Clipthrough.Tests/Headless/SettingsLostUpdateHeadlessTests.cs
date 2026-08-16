using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using ReactiveUI;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Settings are written from several places concurrently: debounced filter
/// toggles, the content/image view mode, preset seeding, and the settings
/// dialog. Each used to compose a whole record from <c>Current</c> and save it,
/// so a save that started from an older snapshot silently reverted whatever had
/// landed since. Every writer must now express its change as a mutation applied
/// inside the service's gate, touching only the fields it owns.
/// </summary>
public sealed class SettingsLostUpdateHeadlessTests
{
    /// <summary>
    /// Applies the writer's recorded mutation to a settings value it never saw.
    /// A writer that carries a stale record ignores the argument entirely, which
    /// is exactly what this catches.
    /// </summary>
    private static AppSettings ApplyTo(Func<AppSettings, AppSettings> mutation, AppSettings landedMeanwhile)
        => mutation(landedMeanwhile);

    [AvaloniaFact]
    public void FilterPersistence_LeavesSettingsItDoesNotOwnAlone()
    {
        using var harness = MainWindowTestHarness.Create();

        harness.ViewModel.ShowFavoritesOnly = true;
        harness.ViewModel.WholeWordSearch = true;
        harness.ViewModel.Dispose();

        var mutation = harness.Settings.LastMutation;
        Assert.NotNull(mutation);

        var landedMeanwhile = AppSettings.Default with
        {
            ThemeMode = ThemeMode.Dark,
            LastContentDisplayMode = ContentDisplayMode.Raw,
            LastImageViewMode = ImageViewMode.Preview,
            MaxClipSizeBytes = 4096,
        };

        var result = ApplyTo(mutation!, landedMeanwhile);

        // Its own fields are applied...
        Assert.True(result.LastShowFavoritesOnly);
        Assert.True(result.LastWholeWordSearch);

        // ...and nothing else is rolled back.
        Assert.Equal(ThemeMode.Dark, result.ThemeMode);
        Assert.Equal(ContentDisplayMode.Raw, result.LastContentDisplayMode);
        Assert.Equal(ImageViewMode.Preview, result.LastImageViewMode);
        Assert.Equal(4096, result.MaxClipSizeBytes);
    }

    /// <summary>
    /// The settings dialog snapshots the session state when it opens, but the
    /// filter toggles and view modes keep saving while it is up. Its save must
    /// re-read them rather than write back the copy it opened with.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingsDialogSave_DoesNotRevertFiltersChangedWhileItWasOpen()
    {
        using var harness = MainWindowTestHarness.Create();
        await harness.ViewModel.InitializeAsync();

        for (var attempt = 0; attempt < 100 && harness.ViewModel.IsLoadingDatabase; attempt++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(harness.ViewModel.IsLoadingDatabase);
        Assert.False(harness.Settings.Current.LastShowFavoritesOnly);

        // A dialog-owned change, so the save has something of its own to write.
        harness.ViewModel.Settings.ThemeMode = ThemeMode.Dark;

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Settings.HoldNextSave = held;

        var saving = harness.ViewModel.SaveSettingsCommand.Execute().ToTask();

        // Lands while the dialog save sits in the gate, exactly as a debounced
        // filter save does.
        await harness.Settings.UpdateAsync(c => c with { LastShowFavoritesOnly = true });

        held.SetResult();
        await saving;

        Assert.Equal(ThemeMode.Dark, harness.Settings.Current.ThemeMode);
        Assert.True(
            harness.Settings.Current.LastShowFavoritesOnly,
            "The settings dialog wrote back its stale copy of the filter state.");
    }

    [AvaloniaFact]
    public async Task ViewModePersistence_LeavesSettingsItDoesNotOwnAlone()
    {
        using var harness = MainWindowTestHarness.Create();
        await harness.Settings.SaveAsync(harness.Settings.Current);

        var before = harness.Settings.SaveCallCount;
        harness.ViewModel.SelectedContentDisplayMode = ContentDisplayMode.Raw;

        for (var attempt = 0; attempt < 100 && harness.Settings.SaveCallCount == before; attempt++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(harness.Settings.SaveCallCount > before, "The display mode was never persisted.");

        var landedMeanwhile = AppSettings.Default with
        {
            LastShowFavoritesOnly = true,
            ThemeMode = ThemeMode.Dark,
        };

        var result = ApplyTo(harness.Settings.LastMutation!, landedMeanwhile);

        Assert.Equal(ContentDisplayMode.Raw, result.LastContentDisplayMode);
        Assert.True(result.LastShowFavoritesOnly);
        Assert.Equal(ThemeMode.Dark, result.ThemeMode);
    }
}
