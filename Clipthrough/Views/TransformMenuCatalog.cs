using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Clipthrough.Models;

namespace Clipthrough.Views;

/// <summary>
/// The catalogue of text transformations offered by the Edit menu and the
/// toolbar flyout, and the grouping rule that turns it into menu items.
/// </summary>
/// <remarks>
/// Separated from <see cref="MainWindow"/> because it is neither window
/// lifecycle nor input routing, which is what the rest of that file is: this is
/// a list and a rule for shaping it, and it needs nothing from the window to do
/// its job except somewhere to send a click.
///
/// The clip context menu in <c>MainWindow.axaml</c> lists the same transforms
/// separately. That is not an oversight and cannot be collapsed into this: the
/// context menu applies a transform to the clip that was right-clicked rather
/// than to the current selection, so its items bind a different command on a
/// different view model. The two lists have to agree without being the same
/// items, which is what <c>TransformMenuParityHeadlessTests</c> enforces.
/// </remarks>
internal static class TransformMenuCatalog
{
    /// <summary>
    /// Every transform offered, in menu order, tagged with the submenu it
    /// belongs to. A group holding one entry is flattened rather than given a
    /// submenu of its own - see <see cref="BuildItems"/>.
    /// </summary>
    internal static readonly (string Group, string Header, TextTransformation Kind)[] Entries =
    {
        ("Case", "UPPERCASE", TextTransformation.UpperCase),
        ("Case", "lowercase", TextTransformation.LowerCase),
        ("Case", "Title Case", TextTransformation.TitleCase),
        ("Case", "Sentence case", TextTransformation.SentenceCase),
        ("Case", "UpperCamelCase", TextTransformation.UpperCamelCase),
        ("Case", "lowerCamelCase", TextTransformation.LowerCamelCase),
        ("Case", "From camelCase", TextTransformation.FromCamelCase),
        ("Whitespace", "Trim whitespace", TextTransformation.TrimWhitespace),
        ("Whitespace", "Collapse whitespace", TextTransformation.CollapseWhitespace),
        ("Whitespace", "Tabs \u2192 Spaces", TextTransformation.TabsToSpaces),
        ("Whitespace", "Spaces \u2192 Tabs", TextTransformation.SpacesToTabs),
        ("Lines", "Normalize line endings", TextTransformation.NormalizeEol),
        ("Lines", "Sort lines", TextTransformation.SortLines),
        ("Lines", "Reverse lines", TextTransformation.ReverseLines),
        ("Lines", "Remove empty lines", TextTransformation.RemoveEmptyLines),
        ("Lines", "Remove duplicate lines", TextTransformation.RemoveDuplicateLines),
        ("JSON", "JSON quote", TextTransformation.JsonQuote),
        ("JSON", "JSON unquote", TextTransformation.JsonUnquote),
        ("JSON", "JSON minify", TextTransformation.JsonMinify),
        ("JSON", "JSON pretty", TextTransformation.JsonPretty),
        ("JSON", "Lines \u2192 JSON array", TextTransformation.LinesToJsonArray),
        ("Encoding", "URL encode", TextTransformation.UrlEncode),
        ("Encoding", "URL decode", TextTransformation.UrlDecode),
        ("Encoding", "Base64 encode", TextTransformation.Base64Encode),
        ("Encoding", "Base64 decode", TextTransformation.Base64Decode),
        ("Cleanup", "Clean terminal formatting", TextTransformation.CleanTerminalFormatting),
        ("Convert", "Text table \u2192 HTML", TextTransformation.BoxTableToHtml),
    };

    /// <summary>
    /// Builds the transform items, grouped by <c>Group</c>. A group with a single
    /// entry becomes a top-level item rather than a submenu holding one thing.
    /// </summary>
    /// <param name="onClick">
    /// Handler attached to every leaf item. The transform it should apply is on
    /// the item's <c>CommandParameter</c>, so one handler serves them all.
    /// </param>
    internal static IEnumerable<Control> BuildItems(EventHandler<RoutedEventArgs> onClick)
    {
        ArgumentNullException.ThrowIfNull(onClick);

        var controls = new List<Control>();
        foreach (var grouping in Entries.GroupBy(e => e.Group))
        {
            var entries = grouping.ToList();
            if (entries.Count == 1)
            {
                controls.Add(Leaf(entries[0].Header, entries[0].Kind, onClick));
                continue;
            }

            var groupRoot = new MenuItem { Header = grouping.Key };
            foreach (var (_, header, kind) in entries)
            {
                groupRoot.Items.Add(Leaf(header, kind, onClick));
            }

            controls.Add(groupRoot);
        }

        return controls;
    }

    private static MenuItem Leaf(string header, TextTransformation kind, EventHandler<RoutedEventArgs> onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            CommandParameter = kind,
        };
        item.Click += onClick;
        return item;
    }
}
