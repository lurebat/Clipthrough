using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Clipthrough.Services;
using Clipthrough.ViewModels;

namespace Clipthrough.Views;

/// <summary>
/// The motion policy, applied: the <c>motion</c> class on the popup and on the
/// windows it owns. Every transition and animation in ModernTheme.axaml is
/// scoped under that class, so a window without it does not animate at all.
/// </summary>
public partial class MainWindow
{
    internal const string MotionClass = "motion";

    /// <summary>
    /// On the clip list while an arrow key is auto-repeating: the selection then
    /// moves every ~33 ms, and an 83 ms crossfade would trail behind the key.
    /// </summary>
    internal const string KeyRepeatClass = "key-repeat";

    /// <summary>
    /// On the clip list while it is being driven from the keyboard, for the
    /// focus ring; a pointer press takes it off.
    /// </summary>
    internal const string KeyboardNavigationClass = "keyboard-nav";

    /// <summary>
    /// The longest gap between two KeyDowns of one key that still counts as
    /// auto-repeat. Repeats come every ~33 ms; a deliberate second press, or one
    /// after a KeyUp that went to another window, comes later than this.
    /// </summary>
    private static readonly TimeSpan KeyRepeatWindow = TimeSpan.FromMilliseconds(150);

    private Key? m_heldNavigationKey;
    private long m_lastNavigationKeyDown;

    /// <summary>How long a single clip arriving or leaving takes to settle in (O4).</summary>
    internal static readonly TimeSpan ListChangeDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>
    /// How long the slide stays armed after a change: past the animation, but
    /// short enough that scrolling straight afterwards does not animate rows
    /// being recycled.
    /// </summary>
    private static readonly TimeSpan ListChangeArmedFor = TimeSpan.FromMilliseconds(300);

    private long m_listChangeGeneration;
    private readonly List<Control> m_raisedRows = [];
    private readonly List<Window> m_motionWindows = [];
    private IMotionService? m_motionService;
    private IDisposable? m_motionSubscription;

    private void InitializeMotion(IMotionService? motionService)
    {
        m_motionService = motionService;
        m_motionWindows.Add(this);
        AddHandler(KeyDownEvent, TrackNavigationKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, TrackNavigationKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressedForMotion, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (this.FindControl<ListBox>("ClipsListBox") is { } list)
        {
            list.Items.CollectionChanged += (_, e) => OnClipItemsChanged(list, e);
        }

        if (motionService is null)
        {
            return;
        }

        m_motionSubscription = motionService.MotionEnabled.Subscribe(ApplyMotion);
        Opened += (_, _) => motionService.Attach(this);
        Closed += (_, _) => m_motionSubscription?.Dispose();
    }

    /// <summary>Whether this popup's UI animates right now.</summary>
    internal bool IsMotionEnabled => m_motionService?.IsMotionEnabled == true;

    private static bool IsNavigationKey(Key key)
        => key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    /// <summary>
    /// The pointer has taken over from the keyboard: no focus ring, and the next
    /// selection change fades even if an arrow key's KeyUp never arrived.
    /// </summary>
    private void OnPointerPressedForMotion(object? sender, PointerPressedEventArgs e)
    {
        m_heldNavigationKey = null;
        if (this.FindControl<ListBox>("ClipsListBox") is { } list)
        {
            list.Classes.Set(KeyboardNavigationClass, false);
            list.Classes.Set(KeyRepeatClass, false);
        }
    }

    /// <summary>
    /// A KeyDown of the same navigation key, with no KeyUp between and within
    /// <see cref="KeyRepeatWindow"/> of the last, is the keyboard's auto-repeat.
    /// </summary>
    private void TrackNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        var list = this.FindControl<ListBox>("ClipsListBox");
        if (e.Key == Key.Tab)
        {
            list?.Classes.Set(KeyboardNavigationClass, true);
        }

        if (!IsNavigationKey(e.Key))
        {
            return;
        }

        list?.Classes.Set(KeyboardNavigationClass, true);
        var repeating = m_heldNavigationKey == e.Key
            && Stopwatch.GetElapsedTime(m_lastNavigationKeyDown) <= KeyRepeatWindow;
        m_heldNavigationKey = e.Key;
        m_lastNavigationKeyDown = Stopwatch.GetTimestamp();
        list?.Classes.Set(KeyRepeatClass, repeating);
    }

    private void TrackNavigationKeyUp(object? sender, KeyEventArgs e)
    {
        if (m_heldNavigationKey != e.Key)
        {
            return;
        }

        m_heldNavigationKey = null;
        this.FindControl<ListBox>("ClipsListBox")?.Classes.Set(KeyRepeatClass, false);
    }

    /// <summary>
    /// A single clip arriving or leaving, as the view model marks it: the rows
    /// around it slide into place and an arriving row fades in above them.
    /// Nothing else animates - not while the popup is out of sight, not without
    /// motion, and never a change the view model did not mark (a search, a page
    /// load, a refresh), which would turn every keystroke into motion.
    /// </summary>
    private void OnClipItemsChanged(ListBox list, NotifyCollectionChangedEventArgs e)
    {
        if (!IsMotionEnabled || !IsPresentedToUser || DataContext is not MainWindowViewModel { IsListChangeAnimatable: true })
        {
            return;
        }

        var arriving = e.Action == NotifyCollectionChangedAction.Add && e.NewItems?.Count == 1;
        var leaving = e.Action == NotifyCollectionChangedAction.Remove && e.OldItems?.Count == 1;
        if (!arriving && !leaving)
        {
            return;
        }

        // Armed now, before the layout that moves the rows: an implicit Offset
        // animation runs when the row's composition offset changes.
        foreach (var row in list.GetRealizedContainers())
        {
            if (ElementComposition.GetElementVisual(row) is { } visual)
            {
                var slide = visual.Compositor.CreateVector3DKeyFrameAnimation();
                slide.Target = "Offset";
                slide.InsertExpressionKeyFrame(1f, "this.FinalValue", new CubicEaseOut());
                slide.Duration = ListChangeDuration;
                var implicitAnimations = visual.Compositor.CreateImplicitAnimationCollection();
                implicitAnimations["Offset"] = slide;
                visual.ImplicitAnimations = implicitAnimations;
            }
        }

        if (arriving)
        {
            var index = e.NewStartingIndex;
            Dispatcher.UIThread.Post(() => FadeInArrivingRow(list, index), DispatcherPriority.Loaded);
        }

        var generation = ++m_listChangeGeneration;
        DispatcherTimer.RunOnce(() => DisarmListChange(list, generation), ListChangeArmedFor);
    }

    private void FadeInArrivingRow(ListBox list, int index)
    {
        if (list.ContainerFromIndex(index) is not Control row)
        {
            return;
        }

        // Above its neighbours: they slide through the space it fades into, and
        // drawn on top of it they hid the first half of the fade.
        row.ZIndex = 1;
        m_raisedRows.Add(row);
        if (ElementComposition.GetElementVisual(row) is { } visual)
        {
            visual.ImplicitAnimations = null;
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, new CubicEaseOut());
            fade.Duration = ListChangeDuration;
            visual.StartAnimation("Opacity", fade);
        }
    }

    private void DisarmListChange(ListBox list, long generation)
    {
        if (generation != m_listChangeGeneration)
        {
            return;
        }

        foreach (var row in list.GetRealizedContainers())
        {
            if (ElementComposition.GetElementVisual(row) is { } visual)
            {
                visual.ImplicitAnimations = null;
            }
        }

        // Every row lifted since the last disarm: a second arrival within the
        // armed window lifts another before the first comes back down.
        foreach (var row in m_raisedRows)
        {
            row.ZIndex = 0;
        }

        m_raisedRows.Clear();
    }

    private void TrackMotion(Window window)
    {
        m_motionWindows.Add(window);
        window.Classes.Set(MotionClass, IsMotionEnabled);
        window.Closed += (_, _) => m_motionWindows.Remove(window);
    }

    private void ApplyMotion(bool enabled)
    {
        foreach (var window in m_motionWindows.ToArray())
        {
            window.Classes.Set(MotionClass, enabled);
        }
    }
}