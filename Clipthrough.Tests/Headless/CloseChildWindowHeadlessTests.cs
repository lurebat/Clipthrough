using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// <see cref="MainWindow"/> tears down three child windows when it closes. Each
/// close used to sit inside <c>try { ... } catch { }</c>, so a window whose
/// <c>Closing</c> handler threw both aborted the remaining teardown and left no
/// trace of why. The close still must not abort teardown - it runs arbitrary
/// user handlers and renderer shutdown - but the failure has to reach the
/// session log, which is fed from <see cref="Trace"/>.
/// </summary>
public sealed class CloseChildWindowHeadlessTests
{
    [AvaloniaFact]
    public void CloseChildWindow_SwallowsAFailingCloseSoTeardownContinues()
    {
        var window = ShowWindowThatThrowsOnClosing(out var detach);
        try
        {
            // The exception must not escape: the caller has more windows to
            // close and handlers to detach after this line.
            MainWindow.CloseChildWindow(window, "settings");
        }
        finally
        {
            detach();
            CloseForReal(window);
        }
    }

    [AvaloniaFact]
    public void CloseChildWindow_TracesTheFailureInsteadOfDiscardingIt()
    {
        var window = ShowWindowThatThrowsOnClosing(out var detach);
        var messages = new ConcurrentQueue<string>();
        var listener = new TraceCaptureListener(messages);
        Trace.Listeners.Add(listener);
        try
        {
            MainWindow.CloseChildWindow(window, "session logs");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            detach();
            CloseForReal(window);
        }

        var traced = messages.ToArray();

        // Name the window, so the log says which teardown step failed, and carry
        // the original exception, so it can be diagnosed at all.
        Assert.Contains(traced, m => m.Contains("session logs", StringComparison.Ordinal));
        Assert.Contains(traced, m => m.Contains("boom", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void CloseChildWindow_ClosesAWindowThatDoesNotThrow()
    {
        var window = new Window();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);

        MainWindow.CloseChildWindow(window, "AI prompt");
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
    }

    /// <summary>
    /// A window whose <c>Closing</c> handler throws does not actually close, so
    /// every test here has to detach the handler and close it for real. Leaving
    /// it shown leaks it into the shared headless session, where it fails a
    /// later, unrelated test during cleanup.
    /// </summary>
    private static Window ShowWindowThatThrowsOnClosing(out Action detach)
    {
        var window = new Window();
        void OnClosing(object? sender, WindowClosingEventArgs e) => throw new InvalidOperationException("boom");
        window.Closing += OnClosing;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var target = window;
        detach = () => target.Closing -= OnClosing;
        return window;
    }

    private static void CloseForReal(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    private sealed class TraceCaptureListener(ConcurrentQueue<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }
    }
}
