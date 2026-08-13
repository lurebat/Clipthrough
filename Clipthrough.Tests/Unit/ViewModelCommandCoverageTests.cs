using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Input;
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
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Two ways to satisfy the contract: reflect over every command at the end of
        // the constructor, or - for view models that build commands lazily, where
        // reading them all up front is the cost being avoided - wire each one as it
        // is created.
        var observe = typeof(ViewModelBase).GetMethod("ObserveCommandErrors", Any);
        Assert.NotNull(observe);

        var track = typeof(ViewModelBase)
            .GetMethods(Any)
            .SingleOrDefault(m => m.Name == "TrackCommandErrors");
        Assert.NotNull(track);

        var offenders = ViewModelsExposingCommands()
            .Where(t => !IlCallScanner.CallsMethod(t, observe!) && !IlCallScanner.CallsMethod(t, track!))
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

    /// <summary>
    /// The IL scan above only proves the wiring method is called somewhere. A view
    /// model that builds its commands lazily has one call site per getter, so a
    /// seventh command added later with the call left off would still pass it.
    /// This drives every command the type actually exposes and requires each one to
    /// report, which is the contract the scan is standing in for.
    /// </summary>
    [AvaloniaFact]
    public void EveryLazilyBuiltRowCommand_ReportsItsFailures()
    {
        var reported = new List<string>();
        using var _ = ViewModelBase.UseCommandErrorSink((context, _) => reported.Add(context));

        static Task Explode() => Task.FromException(new InvalidOperationException("boom"));

        using var vm = new ClipItemViewModel(
            NewClip(),
            copyHandler: _ => Explode(),
            toggleFavoriteHandler: _ => Explode(),
            deleteHandler: _ => Explode(),
            exportHandler: _ => Explode(),
            togglePinHandler: _ => Explode(),
            applyTransformHandler: (_, _) => Explode());

        var commands = ViewModelBase.DiscoverCommandProperties(typeof(ClipItemViewModel)).ToArray();
        Assert.Equal(6, commands.Length);

        foreach (var property in commands)
        {
            var command = (ICommand)property.GetValue(vm)!;

            // ICommand.Execute is fire-and-forget on ReactiveCommand: the failure goes
            // to ThrownExceptions rather than to this caller, which is the path under
            // test. The parameter type is whatever the command declares.
            var parameterType = property.PropertyType.GetGenericArguments()[0];
            command.Execute(parameterType == typeof(System.Reactive.Unit) ? null : Activator.CreateInstance(parameterType));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(
            commands.Select(p => $"ClipItemViewModel.{p.Name}").OrderBy(x => x, StringComparer.Ordinal),
            reported.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// A clip row is discarded and rebuilt constantly - every search keystroke
    /// replaces the page - so its error subscriptions have to go with it. The sink
    /// is process-wide and outlives any row, which is exactly what keeps a leaked
    /// subscription alive and reporting.
    ///
    /// Once released, a failure from the orphaned command falls through to
    /// ReactiveUI's default handler. That is the pre-existing contract - disposal
    /// released the same subscriptions before these commands became lazy - and
    /// asserting it here is what distinguishes "released" from "never subscribed".
    /// </summary>
    [AvaloniaFact]
    public void DisposingARow_StopsItsCommandsFromReporting()
    {
        var reported = new List<string>();
        using var _ = ViewModelBase.UseCommandErrorSink((context, _) => reported.Add(context));

        var vm = new ClipItemViewModel(
            NewClip(),
            deleteHandler: _ => Task.FromException(new InvalidOperationException("boom")));

        var command = vm.DeleteCommand;
        ((ICommand)command).Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "ClipItemViewModel.DeleteCommand" }, reported);

        vm.Dispose();
        reported.Clear();

        // With the subscription gone the failure falls through to ReactiveUI's
        // default handler, which rethrows. That throw is the observable proof the
        // subscription was released rather than never made.
        Assert.Throws<UnhandledErrorException>(() =>
        {
            ((ICommand)command).Execute(null);
            Dispatcher.UIThread.RunJobs();
        });

        Assert.Empty(reported);
    }

    /// <summary>
    /// Nothing in the clip row template binds these commands - they are reached only
    /// through the list's shared context menu and the detail pane, both bound to
    /// SelectedClip - so constructing a page of rows should not construct any of
    /// them. Building all six eagerly was measured at roughly 35ms per 200-row page
    /// on the UI thread, repeated on every search keystroke.
    /// </summary>
    [AvaloniaFact]
    public void ConstructingARow_DoesNotBuildItsCommands()
    {
        using var vm = new ClipItemViewModel(NewClip());

        var backingFields = typeof(ClipItemViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => typeof(IReactiveCommand).IsAssignableFrom(f.FieldType))
            .ToArray();

        Assert.Equal(6, backingFields.Length);

        var eager = backingFields.Where(f => f.GetValue(vm) is not null).Select(f => f.Name).ToArray();
        Assert.True(
            eager.Length == 0,
            $"These row commands were built during construction rather than on first use: {string.Join(", ", eager)}");

        // ... and reading one builds exactly that one.
        _ = vm.DeleteCommand;
        var built = backingFields.Where(f => f.GetValue(vm) is not null).Select(f => f.Name).ToArray();
        Assert.Single(built);
    }

    /// <summary>
    /// ObserveCommandErrors is what all 60-odd application commands outside the
    /// clip row still use, but nothing here drives it directly: the two sink tests
    /// above reach it only through whichever view model happens to call it, and
    /// that used to be ClipItemViewModel. When the row moved to
    /// TrackCommandErrors, coverage of the eager path went with it, and a build
    /// with the report deliberately removed from ObserveCommandErrors passed the
    /// whole class. Drive it through a view model that exists for that purpose so
    /// it cannot be orphaned again.
    /// </summary>
    [AvaloniaFact]
    public void AFailingCommandOnAnEagerlyWiredViewModel_IsReportedToTheInstalledSink()
    {
        var reported = new List<(string Context, Exception Error)>();
        using var _ = ViewModelBase.UseCommandErrorSink((context, ex) => reported.Add((context, ex)));

        using var vm = new EagerlyWiredViewModel();
        ((ICommand)vm.ExplodeCommand).Execute(null);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(reported);
        Assert.Equal("EagerlyWiredViewModel.ExplodeCommand", entry.Context);
        Assert.Equal("eager exploded", entry.Error.Message);
    }

    private sealed class EagerlyWiredViewModel : ViewModelBase, IDisposable
    {
        private readonly IDisposable _commandErrors;

        public EagerlyWiredViewModel()
        {
            ExplodeCommand = ReactiveCommand.CreateFromTask(
                () => Task.FromException(new InvalidOperationException("eager exploded")));
            _commandErrors = ObserveCommandErrors();
        }

        public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ExplodeCommand { get; }

        public void Dispose() => _commandErrors.Dispose();
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
}
