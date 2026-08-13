using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using ReactiveUI;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// A ReactiveCommand whose <c>ThrownExceptions</c> nobody subscribes to hands its
/// exception to ReactiveUI's default handler, which rethrows it on the main thread
/// scheduler. That lands in the dispatcher's unhandled-exception hook, so the
/// button does nothing, the status bar says nothing, and nothing is logged.
///
/// Coverage used to be a hand-written <c>.Merge(X.ThrownExceptions)</c> chain in
/// MainWindowViewModel listing 20 of the application's 67 commands. These tests
/// exist so that stays fixed: adding a command to any view model without wiring
/// it up fails here rather than in front of a user.
/// </summary>
public class ViewModelCommandCoverageTests
{
    private static IEnumerable<Type> ViewModelsExposingCommands()
        => typeof(ViewModelBase).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                && typeof(ViewModelBase).IsAssignableFrom(t)
                && ViewModelBase.DiscoverCommandProperties(t).Any())
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void EveryViewModelThatExposesCommands_ObservesTheirExceptions()
    {
        var observe = typeof(ViewModelBase).GetMethod(
            "ObserveCommandErrors",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(observe);

        var offenders = ViewModelsExposingCommands()
            .Where(t => !CallsMethod(t, observe!))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These view models expose ReactiveCommands but never call ObserveCommandErrors(), so their " +
            $"failures are silent: {string.Join(", ", offenders)}. Call it at the end of the constructor.");
    }

    /// <summary>
    /// Guards the discovery itself. If the reflection filter ever stops matching
    /// commands, the test above passes vacuously for every view model.
    /// </summary>
    [Fact]
    public void CommandDiscovery_FindsTheCommandsAViewModelDeclares()
    {
        var found = ViewModelBase.DiscoverCommandProperties(typeof(ClipItemViewModel))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ApplyTextTransformationCommand",
                "CopyCommand",
                "DeleteCommand",
                "ExportCommand",
                "ToggleFavoriteCommand",
                "TogglePinCommand",
            },
            found);

        Assert.True(
            ViewModelsExposingCommands().Count() >= 9,
            "Discovery found suspiciously few command-owning view models; the filter is probably broken.");
    }

    /// <summary>
    /// ReactiveObject implements IHandleObservableErrors as well as ReactiveCommand
    /// does, so filtering on that interface also matched nested view-model
    /// properties such as SelectedClip and Update -- double-reporting their command
    /// failures, and throwing on construction because SelectedClip starts null.
    /// </summary>
    [Fact]
    public void CommandDiscovery_IgnoresNestedViewModelProperties()
    {
        var nested = ViewModelBase.DiscoverCommandProperties(typeof(MainWindowViewModel))
            .Where(p => typeof(ViewModelBase).IsAssignableFrom(p.PropertyType))
            .Select(p => $"{p.Name} ({p.PropertyType.Name})")
            .ToArray();

        Assert.True(
            nested.Length == 0,
            $"Discovery treated nested view models as commands: {string.Join(", ", nested)}");

        var mainWindowCommands = ViewModelBase.DiscoverCommandProperties(typeof(MainWindowViewModel)).ToArray();
        Assert.True(
            mainWindowCommands.Length >= 40,
            $"MainWindowViewModel should expose 40+ commands; discovery found {mainWindowCommands.Length}.");
    }

    /// <summary>
    /// These two run headless deliberately. ReactiveCommand publishes
    /// <c>ThrownExceptions</c> on the scheduler captured at construction, which
    /// once any Avalonia application exists is AvaloniaScheduler - it posts to
    /// the dispatcher. As plain <c>[Fact]</c>s they had no dispatcher to pump, so
    /// the report never arrived: whether they worked depended on running before
    /// the first headless test in the assembly. One failed and the other passed
    /// vacuously once isolation stopped tearing the application down between
    /// tests. Running under a dispatcher and draining it makes both deterministic.
    /// </summary>
    [AvaloniaFact]
    public async Task AFailingItemCommand_IsReportedToTheInstalledSink()
    {
        var reported = new List<(string Context, Exception Error)>();
        using var _ = ViewModelBase.UseCommandErrorSink((context, ex) => reported.Add((context, ex)));

        using var vm = new ClipItemViewModel(
            NewClip(),
            deleteHandler: _ => throw new InvalidOperationException("delete exploded"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await vm.DeleteCommand.Execute());

        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(reported);
        Assert.Equal("ClipItemViewModel.DeleteCommand", entry.Context);
        Assert.Equal("delete exploded", entry.Error.Message);
    }

    /// <inheritdoc cref="AFailingItemCommand_IsReportedToTheInstalledSink"/>
    [AvaloniaFact]
    public async Task DisposingTheSinkRegistration_StopsRoutingToIt()
    {
        // An outer sink stays installed throughout. Without it this test would
        // pass just as happily if command errors stopped being routed anywhere
        // at all - which is exactly how it passed while the report was being
        // posted to a dispatcher nothing pumped.
        var outer = new List<string>();
        using var _ = ViewModelBase.UseCommandErrorSink((context, _) => outer.Add(context));

        var reported = new List<string>();
        var registration = ViewModelBase.UseCommandErrorSink((context, _) => reported.Add(context));
        registration.Dispose();

        using var vm = new ClipItemViewModel(
            NewClip(),
            deleteHandler: _ => throw new InvalidOperationException("delete exploded"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await vm.DeleteCommand.Execute());

        Dispatcher.UIThread.RunJobs();

        Assert.Empty(reported);
        Assert.Equal(new[] { "ClipItemViewModel.DeleteCommand" }, outer);
    }

    private static ClipEntry NewClip() => new()
    {
        Id = 7,
        Content = "boom",
        ContentBytes = Encoding.UTF8.GetBytes("boom"),
        ContentType = ContentType.Text,
        ContentFormat = ClipContentFormat.PlainText,
        SourceApp = "Tests",
        Hash = "hash-7",
        LastCopiedAt = DateTimeOffset.UtcNow,
        FirstCopiedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Scans <paramref name="owner"/>'s own constructors and methods for a call to
    /// <paramref name="target"/>. Both live in the same assembly, so the call site
    /// carries <paramref name="target"/>'s MethodDef token verbatim and a byte
    /// search for <c>call</c>/<c>callvirt</c> plus that token is exact.
    /// </summary>
    private static bool CallsMethod(Type owner, MethodInfo target)
    {
        var token = BitConverter.GetBytes(target.MetadataToken);
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        var bodies = owner.GetConstructors(All).Cast<MethodBase>()
            .Concat(owner.GetMethods(All));

        foreach (var body in bodies)
        {
            var il = body.GetMethodBody()?.GetILAsByteArray();
            if (il is null)
            {
                continue;
            }

            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] is not (0x28 or 0x6F))
                {
                    continue;
                }

                if (il[i + 1] == token[0] && il[i + 2] == token[1]
                    && il[i + 3] == token[2] && il[i + 4] == token[3])
                {
                    return true;
                }
            }
        }

        return false;
    }
}
