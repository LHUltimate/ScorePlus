using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Scoreboard.Desktop;

/// <summary>
/// A UniformGrid that honours a per-child span. The wall needs a quarter-screen tile beside a
/// quarter-of-a-quarter tile, which a plain UniformGrid cannot express because every cell is the
/// same size. Children still report Columns and Rows like a UniformGrid, so the rest of the wall
/// (including the size picker) keeps working.
/// </summary>
public sealed class SpanningUniformGrid : UniformGrid
{
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
        "Span", typeof(int), typeof(SpanningUniformGrid),
        new FrameworkPropertyMetadata(1,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static int GetSpan(DependencyObject element) => (int)element.GetValue(SpanProperty);

    public static void SetSpan(DependencyObject element, int value) =>
        element.SetValue(SpanProperty, Math.Clamp(value, 1, 4));

    static int SpanOf(UIElement child) => Math.Clamp(GetSpan(child), 1, 4);

    List<(UIElement Child, int Row, int Column, int Span)> Place(int columns, int rowLimit)
    {
        var placed = new List<(UIElement, int, int, int)>();
        var occupied = new HashSet<(int Row, int Column)>();
        foreach (UIElement child in InternalChildren)
        {
            var span = Math.Min(SpanOf(child), Math.Max(1, columns));
            (int Row, int Column)? found = null;
            for (var row = 0; found is null && row < rowLimit; row++)
            {
                // Shrink a 2×2 block at the very bottom of a fixed wall so it still lands on the wall.
                var useSpan = Math.Min(span, Math.Max(1, rowLimit - row));
                for (var column = 0; column + useSpan <= columns; column++)
                {
                    var cells = (from r in Enumerable.Range(row, useSpan)
                                 from c in Enumerable.Range(column, useSpan)
                                 select (r, c)).ToArray();
                    if (cells.Any(cell => occupied.Contains(cell))) continue;
                    foreach (var cell in cells) occupied.Add(cell);
                    found = (row, column);
                    span = useSpan;
                    break;
                }
            }
            if (found is null)
            {
                // The wall is completely full. Keep the last tile visible instead of looping.
                found = (Math.Max(0, rowLimit - 1), Math.Max(0, columns - span));
            }
            placed.Add((child, found.Value.Row, found.Value.Column, span));
        }
        return placed;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0) return default;
        var columns = Math.Max(1, Columns > 0 ? Columns : (int)Math.Ceiling(Math.Sqrt(count)));
        var rows = Rows > 0 ? Rows : Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? width * rows / columns : availableSize.Height;
        var cellWidth = width / columns;
        var cellHeight = height / Math.Max(1, rows);
        foreach (UIElement child in InternalChildren)
        {
            var span = Math.Min(SpanOf(child), columns);
            child.Measure(new Size(cellWidth * span, cellHeight * span));
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0) return finalSize;
        var columns = Math.Max(1, Columns > 0 ? Columns : (int)Math.Ceiling(Math.Sqrt(count)));
        var placement = Place(columns, Rows > 0 ? Rows : 1000);
        var rows = Rows > 0 ? Rows
            : Math.Max(1, placement.Count == 0 ? 1 : placement.Max(item => item.Row + item.Span));
        var cellWidth = finalSize.Width / columns;
        var cellHeight = finalSize.Height / Math.Max(1, rows);
        foreach (var (child, row, column, span) in placement)
        {
            var rect = new Rect(column * cellWidth, row * cellHeight, cellWidth * span, cellHeight * span);
            child.Arrange(rect);
        }
        return finalSize;
    }
}
