using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The workspace grid is bound to <see cref="MainWindowViewModel.IsMainWorkspaceVisible"/>,
/// which is computed from the welcome and password-prompt flags and is only
/// ever re-read when one of their setters raises it. Both setters used to guard
/// that raise with <c>if (!this.RaiseAndSetIfChanged(...)) return;</c>, but
/// ReactiveUI returns the new value rather than a changed flag - so closing
/// either overlay (setting it to false) returned early and the window stayed
/// blank: after first-run "Create library", and after every manual unlock of an
/// encrypted library. (audit F-46)
/// </summary>
/// <remarks>
/// These go from the overlay to the workspace through the real command and
/// assert on the rendered grid, not on the view-model property: the property
/// read <c>true</c> the whole time the window was blank.
/// </remarks>
public sealed class MainWorkspaceVisibilityHeadlessTests
{
    [AvaloniaFact]
    public async Task CreatingTheLibraryFromTheWelcomeScreen_ShowsTheWorkspace()
    {
        using var harness = MainWindowTestHarness.Create();
        await harness.ViewModel.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.ViewModel.IsWelcomeOpen, "the welcome screen did not open, so this is not the first-run path");
        Assert.False(Workspace(harness).IsEffectivelyVisible, "the workspace was already showing behind the welcome screen");
        var raised = RecordPropertyChanges(harness.ViewModel);

        harness.ViewModel.SaveSettingsCommand.Execute().Subscribe();
        await WaitUntilAsync(() => !harness.ViewModel.IsWelcomeOpen && !harness.ViewModel.IsLoadingDatabase);

        AssertWorkspaceShowing(harness, raised);
    }

    [AvaloniaFact]
    public async Task UnlockingWithTheCorrectPassword_ShowsTheWorkspace()
    {
        using var harness = MainWindowTestHarness.Create();
        harness.SeedClips(3);
        harness.ViewModel.OpenPasswordPromptForTests();
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.ViewModel.IsPasswordPromptOpen, "the prompt did not open, so this is not the unlock path");
        Assert.False(Workspace(harness).IsEffectivelyVisible, "the workspace was already showing behind the prompt");
        var raised = RecordPropertyChanges(harness.ViewModel);

        harness.ViewModel.PasswordPromptInput = "correct horse";
        harness.ViewModel.UnlockDatabaseCommand.Execute().Subscribe();
        await WaitUntilAsync(() => !harness.ViewModel.IsPasswordPromptOpen && !harness.ViewModel.IsLoadingDatabase);

        Assert.Equal(string.Empty, harness.ViewModel.PasswordPromptError);
        AssertWorkspaceShowing(harness, raised);
    }

    private static Control Workspace(MainWindowTestHarness harness)
        => (Control)harness.Window.FindControl<Menu>("TopMenu")!.GetVisualParent()!;

    // Concurrent because the view model also raises from the thread pool while
    // the database starts, and a plain list throws when enumerated mid-add.
    private static ConcurrentQueue<string?> RecordPropertyChanges(MainWindowViewModel viewModel)
    {
        var raised = new ConcurrentQueue<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);
        return raised;
    }

    private static void AssertWorkspaceShowing(MainWindowTestHarness harness, ConcurrentQueue<string?> raised)
    {
        Dispatcher.UIThread.RunJobs();

        Assert.True(Workspace(harness).IsEffectivelyVisible, "the workspace grid is still hidden: the window is blank");
        Assert.True(harness.Window.FindControl<Menu>("TopMenu")!.IsEffectivelyVisible, "the top menu is not showing");
        Assert.True(harness.SearchBox.IsEffectivelyVisible, "the search box is not showing");

        // The view model claimed the workspace was visible all along; what was
        // missing is the notification that makes the binding re-read it.
        Assert.True(harness.ViewModel.IsMainWorkspaceVisible);
        Assert.Contains(nameof(MainWindowViewModel.IsMainWorkspaceVisible), raised);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "the overlay never closed; the transition under test did not happen");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
