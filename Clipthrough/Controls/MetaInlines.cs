using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Clipthrough.Controls;

/// <summary>
/// Renders a list of (text, color) meta segments as colored <see cref="Run"/> inlines
/// in a <see cref="TextBlock"/>, separated by a muted "·". Lets a clip-list row show a
/// per-token-colored meta line without a chip control (Border + TextBlock) per token —
/// inlines are lightweight, so row realization on scroll stays cheap while keeping color.
/// </summary>
public static class MetaInlines
{
    private static readonly IBrush s_separatorBrush = new SolidColorBrush(Color.Parse("#475569"));

    public static readonly AttachedProperty<IReadOnlyList<(string Text, IBrush Foreground)>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<(string Text, IBrush Foreground)>?>(
            "Segments", typeof(MetaInlines));

    static MetaInlines()
    {
        SegmentsProperty.Changed.AddClassHandler<TextBlock>(
            (target, args) => Apply(target, args.NewValue as IReadOnlyList<(string Text, IBrush Foreground)>));
    }

    public static void SetSegments(TextBlock target, IReadOnlyList<(string Text, IBrush Foreground)>? value)
        => target.SetValue(SegmentsProperty, value);

    public static IReadOnlyList<(string Text, IBrush Foreground)>? GetSegments(TextBlock target)
        => target.GetValue(SegmentsProperty);

    private static void Apply(TextBlock target, IReadOnlyList<(string Text, IBrush Foreground)>? segments)
    {
        var inlines = target.Inlines;
        if (inlines is null)
        {
            // A freshly realized/recycled TextBlock can start with no collection;
            // create one so the segments actually render.
            inlines = new InlineCollection();
            target.Inlines = inlines;
        }

        // Rebuild on every change so virtualization container recycling (new DataContext)
        // replaces the previous row's inlines.
        inlines.Clear();
        if (segments is null || segments.Count == 0)
        {
            return;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                inlines.Add(new Run(" · ") { Foreground = s_separatorBrush });
            }

            var segment = segments[i];
            inlines.Add(new Run(segment.Text) { Foreground = segment.Foreground });
        }
    }
}
