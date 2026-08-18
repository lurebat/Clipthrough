using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Clipthrough.Models;
using Clipthrough.ViewModels;

using ReactiveUI;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Changing the selected clip has to notify every property that changed with it.
///
/// <c>RaiseSelectionStateProperties</c> is around seventy hand-written
/// <c>RaisePropertyChanged</c> calls. Adding a computed property about the
/// selected clip and forgetting to add a line there produces no error and no
/// failing test: the field simply shows the previous clip's value until some
/// unrelated refresh happens to raise it. It is the largest instance in this
/// codebase of a pattern that has already caused two real bugs.
///
/// This asserts the contract rather than the list, and without assuming a naming
/// convention: select one clip, read every readable property, select another,
/// read them again, and require that anything whose value moved was announced.
/// A property that is not selection-dependent never changes, so it never has to
/// appear here - which is why this cannot be satisfied by raising everything.
/// </summary>
public sealed class SelectionNotificationHeadlessTests
{
    /// <summary>
    /// Two clips that differ in as many dimensions as a clip can, because this
    /// test only guards a property that actually moves. The harness's own seeded
    /// clips differ by one second and a digit, which leaves most of the selection
    /// state identical between them - and a notification is only required for
    /// what changed. A mutant that dropped the captured-at notification survived
    /// against those, because two clips a second apart format to the same string.
    /// </summary>
    private static (ClipItemViewModel First, ClipItemViewModel Second) TwoUnlikeClips()
    {
        var now = DateTimeOffset.UtcNow;

        var first = new ClipEntry
        {
            Id = 101,
            Content = "the first clip",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("the first clip"),
            SourceApp = "Alpha",
            SourceWindowTitle = "Alpha window",
            SourceUrl = "https://example.invalid/first",
            Hash = "hash-first",
            ByteSize = 14,
            CopyCount = 1,
            FirstCopiedAt = now.AddMinutes(-5),
            LastCopiedAt = now.AddMinutes(-5),
        };

        var second = new ClipEntry
        {
            Id = 202,
            Content = "a completely different second clip with more text in it",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("a completely different second clip with more text in it"),
            SourceApp = "Beta",
            SourceWindowTitle = "Beta window",
            SourceUrl = "https://example.invalid/second",
            Hash = "hash-second",
            ByteSize = 55,
            CopyCount = 7,
            IsSensitive = true,
            FirstCopiedAt = now.AddDays(-9),
            LastCopiedAt = now.AddDays(-9),
            IsFavorite = true,
            PinnedAt = now.AddDays(-9),
            IsPasted = true,
            PasteCount = 3,
            LastPastedAt = now.AddDays(-8),
        };

        return (new ClipItemViewModel(first), new ClipItemViewModel(second));
    }
    private static IReadOnlyList<PropertyInfo> ReadableProperties()
        => typeof(MainWindowViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                && p.GetMethod is not null
                && !typeof(IReactiveCommand).IsAssignableFrom(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, object?> Snapshot(
        MainWindowViewModel vm,
        IReadOnlyList<PropertyInfo> properties)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            try
            {
                values[property.Name] = property.GetValue(vm);
            }
            catch (Exception)
            {
                // A getter that throws for a given selection tells us nothing
                // about notification; leaving it out is not the same as passing it.
            }
        }

        return values;
    }

    [AvaloniaFact]
    public async Task EveryPropertyThatChangesWithTheSelectionIsAnnounced()
    {
        using var harness = MainWindowTestHarness.Create();
        var (first, second) = TwoUnlikeClips();
        var vm = harness.ViewModel;
        vm.Clips.Add(first);
        vm.Clips.Add(second);
        Dispatcher.UIThread.RunJobs();
        var properties = ReadableProperties();
        Assert.NotEmpty(properties);

        vm.SelectedClip = first;
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        var before = Snapshot(vm, properties);

        var raised = new HashSet<string>(StringComparer.Ordinal);
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                raised.Add(e.PropertyName!);
            }
        }

        vm.PropertyChanged += OnChanged;
        try
        {
            vm.SelectedClip = second;
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }

        var after = Snapshot(vm, properties);

        var changedSilently = new List<string>();
        foreach (var (name, oldValue) in before)
        {
            if (!after.TryGetValue(name, out var newValue))
            {
                continue;
            }

            if (!Equals(oldValue, newValue) && !raised.Contains(name))
            {
                changedSilently.Add($"{name}: '{oldValue}' -> '{newValue}'");
            }
        }

        Assert.Empty(changedSilently);
    }

    /// <summary>
    /// Anti-vacuity. The test above compares two snapshots, and would pass
    /// perfectly if selecting a different clip changed nothing at all - which is
    /// also what a broken harness looks like. Something has to move.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectingADifferentClipActuallyChangesSomething()
    {
        using var harness = MainWindowTestHarness.Create();
        var (first, second) = TwoUnlikeClips();
        var vm = harness.ViewModel;
        vm.Clips.Add(first);
        vm.Clips.Add(second);
        Dispatcher.UIThread.RunJobs();
        var properties = ReadableProperties();

        vm.SelectedClip = first;
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        var before = Snapshot(vm, properties);

        vm.SelectedClip = second;
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        var after = Snapshot(vm, properties);

        var moved = before.Count(kv => after.TryGetValue(kv.Key, out var v) && !Equals(kv.Value, v));
        Assert.True(moved > 0, "selecting a different clip changed no observable property, so the test above proves nothing");
    }
}
