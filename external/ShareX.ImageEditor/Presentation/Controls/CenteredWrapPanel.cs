using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace ShareX.ImageEditor.Presentation.Controls;

public sealed class CenteredWrapPanel : Panel
{
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<CenteredWrapPanel, double>(nameof(RowSpacing), 0d);

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = BuildRows(availableSize.Width, availableSize.Height);
        var width = 0d;
        var height = 0d;

        foreach (var row in rows)
        {
            width = Math.Max(width, row.Width);
            height += row.Height;
        }

        if (rows.Count > 1)
        {
            height += (rows.Count - 1) * RowSpacing;
        }

        if (!double.IsInfinity(availableSize.Width))
        {
            width = Math.Min(width, availableSize.Width);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = BuildRows(finalSize.Width, finalSize.Height);
        var y = 0d;

        foreach (var row in rows)
        {
            var x = Math.Max(0d, (finalSize.Width - row.Width) / 2d);
            foreach (var child in row.Children)
            {
                var childY = y + Math.Max(0d, (row.Height - child.Size.Height) / 2d);
                child.Control.Arrange(new Rect(x, childY, child.Size.Width, child.Size.Height));
                x += child.Size.Width;
            }

            y += row.Height + RowSpacing;
        }

        return finalSize;
    }

    private List<RowInfo> BuildRows(double availableWidth, double availableHeight)
    {
        var maxWidth = double.IsInfinity(availableWidth) || availableWidth <= 0d
            ? double.PositiveInfinity
            : availableWidth;
        var childConstraint = new Size(double.PositiveInfinity, availableHeight);
        var rows = new List<RowInfo>();
        var current = new RowBuilder();

        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            child.Measure(childConstraint);
            var desired = child.DesiredSize;
            if (desired.Width <= 0d || desired.Height <= 0d)
            {
                continue;
            }

            if (current.HasItems && current.Width + desired.Width > maxWidth)
            {
                rows.Add(current.Build());
                current = new RowBuilder();
            }

            current.Add(child, desired);
        }

        if (current.HasItems)
        {
            rows.Add(current.Build());
        }

        return rows;
    }

    private sealed record ChildInfo(Control Control, Size Size);

    private sealed record RowInfo(IReadOnlyList<ChildInfo> Children, double Width, double Height);

    private sealed class RowBuilder
    {
        private readonly List<ChildInfo> _children = [];

        public bool HasItems => _children.Count > 0;

        public double Width { get; private set; }

        public double Height { get; private set; }

        public void Add(Control control, Size size)
        {
            _children.Add(new ChildInfo(control, size));
            Width += size.Width;
            Height = Math.Max(Height, size.Height);
        }

        public RowInfo Build() => new(_children.ToArray(), Width, Height);
    }
}
