using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Clipthrough.Models;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Guards the bindings the XAML compiler cannot.
///
/// <c>x:CompileBindings</c> proves every binding path exists on the type the
/// scope declares, which is most of the risk and all of it for a form with one
/// data context. What it cannot prove is that the declared type is the type
/// actually there at runtime. A <c>Style</c> carrying <c>x:DataType</c> is the
/// case that matters: unlike a <c>DataTemplate</c>, which only ever sees the
/// items it was written for, a style applies to every control its selector
/// matches -- including, for a style declared inside a control's own
/// <c>Styles</c>, that control itself.
///
/// A mismatch there does not throw. It logs and yields nothing, so the menu item
/// is simply blank and every assertion about view-model state still passes.
/// </summary>
public sealed class BindingErrorHeadlessTests
{
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public override void Write(string? message) => Capture(message);

        public override void WriteLine(string? message) => Capture(message);

        /// <summary>
        /// Trace carries everything the app writes, including its own startup
        /// timings, so the area tag is what separates a binding failure from
        /// ordinary diagnostics.
        ///
        /// A null part-way along a path is excluded. Binding through a selection
        /// that is currently nothing is normal in MVVM and Avalonia logs it
        /// anyway, so keeping those would mean asserting against a list of
        /// tolerated messages, which stops being read. What is left is the class
        /// this exists for: a path that does not resolve against the type the
        /// scope declares. <see cref="ABrokenBindingIsActuallyObserved"/> is what
        /// proves the filter does not also discard those.
        /// </summary>
        private void Capture(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)
                || !message.Contains("Binding", StringComparison.Ordinal)
                || message.Contains("'Value is null.'", StringComparison.Ordinal))
            {
                return;
            }

            _messages.Add(message);
        }
    }

    private static (T Result, IReadOnlyList<string> Errors) WhileWatchingBindings<T>(Func<T> action)
    {
        var listener = new BindingErrorListener();
        Trace.Listeners.Add(listener);
        try
        {
            var result = action();
            Dispatcher.UIThread.RunJobs();
            return (result, listener.Messages);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [AvaloniaFact]
    public void ShowingTheMainWindowWithClipsLogsNoBindingErrors()
    {
        var (harness, errors) = WhileWatchingBindings(() =>
        {
            var h = MainWindowTestHarness.Create();
            h.SeedClips(3);
            return h;
        });

        using (harness)
        {
            Assert.Empty(errors);
        }
    }

    /// <summary>
    /// The clip context menu, opened rather than merely declared. Menus build
    /// lazily, so nothing else in the suite realises them.
    ///
    /// Limit worth knowing: this covers the menu's own items, not the AI
    /// submenu's generated entries. A nested submenu does not realise under the
    /// headless platform even with the menu open and the entries populated, so
    /// asserting on its children only ever produced an empty collection. The
    /// style that generates those entries is therefore still uncovered - though
    /// the AI item itself is realised and styled, which is what lets this test
    /// speak to the one compiled-binding question that mattered about it.
    /// </summary>
    [AvaloniaFact]
    public void OpeningTheClipContextMenuLogsNoBindingErrors()
    {
        using var harness = MainWindowTestHarness.Create(aiConfigured: true);
        harness.SeedClips(3);
        harness.ViewModel.SelectedClip = harness.ViewModel.Clips[0];
        harness.ViewModel.VisibleAiMenuEntries.Add(new AiMenuEntry("Summarise", null, IsCustomPrompt: true));
        Dispatcher.UIThread.RunJobs();

        var menu = harness.ClipList.ContextMenu;
        Assert.NotNull(menu);

        var (items, errors) = WhileWatchingBindings(() =>
        {
            menu!.Open(harness.ClipList);
            Dispatcher.UIThread.RunJobs();
            return menu.GetLogicalDescendants().OfType<MenuItem>().ToList();
        });

        try
        {
            // Without this the test would pass on a menu that never opened.
            Assert.NotEmpty(items);
            Assert.Empty(errors);
        }
        finally
        {
            menu!.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Anti-vacuity. Both tests above assert on an empty list, which is what a
    /// listener that never receives anything also produces -- if the app builder
    /// stopped routing binding failures to Trace, or Avalonia stopped logging
    /// them, they would keep passing while watching nothing. A binding that is
    /// definitely broken has to be seen.
    /// </summary>
    [AvaloniaFact]
    public void ABrokenBindingIsActuallyObserved()
    {
        var (_, errors) = WhileWatchingBindings<object?>(() =>
        {
            var window = new Window
            {
                DataContext = new object(),
                Content = new TextBlock
                {
                    [!TextBlock.TextProperty] = new Avalonia.Data.Binding("NoSuchPropertyAnywhere"),
                },
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Close();
            return null;
        });

        Assert.Contains(errors, m => m.Contains("NoSuchPropertyAnywhere", StringComparison.Ordinal));
    }
}
