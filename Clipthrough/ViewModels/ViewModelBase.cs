using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public abstract class ViewModelBase : ReactiveObject
{
    private static Action<string, Exception>? s_commandErrorSink;

    private readonly object _trackedCommandGate = new();
    private List<IDisposable>? _trackedCommandErrors;

    /// <summary>
    /// Routes every command failure reported by <see cref="ObserveCommandErrors"/>
    /// to <paramref name="sink"/> until the returned handle is disposed, at which
    /// point the previously installed sink is restored.
    ///
    /// The sink is static because failures come from per-item view models -- a
    /// clip row, a file row, a sensitivity rule -- that are created and discarded
    /// constantly and hold no reference to whatever is currently able to show the
    /// user a message.
    /// </summary>
    public static IDisposable UseCommandErrorSink(Action<string, Exception> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var previous = s_commandErrorSink;
        s_commandErrorSink = sink;
        return new SinkRegistration(sink, previous);
    }

    /// <summary>
    /// Subscribes to <c>ThrownExceptions</c> on every command this view model
    /// exposes, so an exception inside a command is reported instead of vanishing.
    ///
    /// An unobserved ReactiveCommand hands its exception to ReactiveUI's default
    /// handler, which rethrows it on the main thread scheduler; that lands in the
    /// dispatcher's unhandled-exception hook, so the button simply does nothing
    /// and the user is told nothing. This used to be wired by hand as a chain of
    /// <c>.Merge(SomeCommand.ThrownExceptions)</c> calls covering 20 of the
    /// application's 67 commands, and every command added since had to be
    /// remembered. Reflection cannot forget one.
    ///
    /// Call this at the *end* of a constructor, after every command is assigned;
    /// a command left null throws here rather than silently dropping out of the
    /// merge. <c>ViewModelCommandCoverageTests</c> fails for any view model that
    /// exposes commands and never calls this.
    /// </summary>
    protected IDisposable ObserveCommandErrors()
    {
        var owner = GetType();
        var failures = new List<IObservable<Exception>>();

        foreach (var property in DiscoverCommandProperties(owner))
        {
            var command = (IHandleObservableErrors?)property.GetValue(this)
                ?? throw new InvalidOperationException(
                    $"{owner.Name}.{property.Name} was still null when {nameof(ObserveCommandErrors)} ran. " +
                    "Call it after every command has been assigned, or its failures go unreported.");

            var context = $"{owner.Name}.{property.Name}";
            failures.Add(command.ThrownExceptions.Do(ex => Report(context, ex)));
        }

        return failures.Merge().Subscribe(_ => { });
    }

    /// <summary>
    /// Wires one command's failures into the same reporter as
    /// <see cref="ObserveCommandErrors"/>, and returns the command so it can be
    /// assigned in place.
    ///
    /// <see cref="ObserveCommandErrors"/> reflects over every command property and
    /// reads each one, which forces a view model that builds its commands lazily to
    /// build all of them. Per-row view models want the opposite: a
    /// <c>ReactiveCommand</c> costs around 30 microseconds to construct, so six of
    /// them across a 200-row page is tens of milliseconds on the UI thread for
    /// commands that only the selected row is ever bound to. Such a view model
    /// creates each command inside its getter and calls this instead.
    ///
    /// The default <paramref name="name"/> is the calling property, which makes the
    /// reported context identical to what <see cref="ObserveCommandErrors"/> would
    /// have produced.
    /// </summary>
    protected TCommand TrackCommandErrors<TCommand>(
        TCommand command,
        [CallerMemberName] string name = "")
        where TCommand : IHandleObservableErrors
    {
        ArgumentNullException.ThrowIfNull(command);

        var context = $"{GetType().Name}.{name}";
        var subscription = command.ThrownExceptions.Subscribe(ex => Report(context, ex));

        lock (_trackedCommandGate)
        {
            (_trackedCommandErrors ??= []).Add(subscription);
        }

        return command;
    }

    /// <summary>
    /// Releases every subscription made by <see cref="TrackCommandErrors"/>. Safe to
    /// call when none were made, and safe to call twice.
    /// </summary>
    protected void DisposeTrackedCommandErrors()
    {
        List<IDisposable>? subscriptions;
        lock (_trackedCommandGate)
        {
            subscriptions = _trackedCommandErrors;
            _trackedCommandErrors = null;
        }

        if (subscriptions is null)
        {
            return;
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// The public commands <paramref name="owner"/> exposes. The filter is
    /// <see cref="IReactiveCommand"/> and not <c>IHandleObservableErrors</c>:
    /// <c>ReactiveObject</c> implements the latter too, so the looser test also
    /// matched every nested view-model property (<c>SelectedClip</c>, <c>Update</c>)
    /// and would have double-reported their failures. Filtering on the declared
    /// property type also means only command getters are ever invoked, so this
    /// never runs an unrelated computed property during construction.
    /// </summary>
    internal static IEnumerable<PropertyInfo> DiscoverCommandProperties(Type owner)
        => owner.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                && p.GetMethod is not null
                && typeof(IReactiveCommand).IsAssignableFrom(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    private static void Report(string context, Exception ex)
    {
        var sink = s_commandErrorSink;
        if (sink is null)
        {
            // No UI is listening (early startup, or a unit test). Still never silent.
            Trace.TraceError($"{context} failed: {ex}");
            return;
        }

        sink(context, ex);
    }

    private sealed class SinkRegistration(Action<string, Exception> sink, Action<string, Exception>? previous) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(s_commandErrorSink, sink))
            {
                s_commandErrorSink = previous;
            }
        }
    }
}
