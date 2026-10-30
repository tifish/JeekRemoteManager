using Avalonia;

namespace JeekRemoteManager.Services;

/// <summary>Wrap headers into rows, then pages; a height budget keeps terminal space usable.</summary>
internal static class TabPagination
{
    internal sealed record Placement(int Page, Rect Bounds);
    internal sealed record Layout(Placement[] Items, double Height, int[] RowsPerPage);

    internal static Layout Calculate(IReadOnlyList<Size> sizes, double width, int maxRows, double heightBudget)
    {
        width = Math.Max(1, width);
        maxRows = Math.Clamp(maxRows, 1, 20);
        var rows = new List<List<int>>();
        var row = new List<int>();
        var rowWidth = 0d;
        foreach (var i in Enumerable.Range(0, sizes.Count))
        {
            var itemWidth = Math.Min(width, sizes[i].Width);
            if (row.Count > 0 && rowWidth + itemWidth > width + 0.01)
            {
                rows.Add(row);
                row = [];
                rowWidth = 0;
            }
            row.Add(i);
            rowWidth += itemWidth;
        }
        if (row.Count > 0)
            rows.Add(row);

        var placements = new Placement[sizes.Count];
        var pageRows = new List<int> { 0 };
        var page = 0;
        var y = 0d;
        var height = 0d;
        foreach (var indices in rows)
        {
            var rowHeight = indices.Max(i => sizes[i].Height);
            if (pageRows[page] > 0 && (pageRows[page] >= maxRows || y + rowHeight > heightBudget))
            {
                height = Math.Max(height, y);
                page++;
                pageRows.Add(0);
                y = 0;
            }
            var x = 0d;
            foreach (var i in indices)
            {
                var itemWidth = Math.Min(width, sizes[i].Width);
                placements[i] = new Placement(page, new Rect(x, y, itemWidth, rowHeight));
                x += itemWidth;
            }
            y += rowHeight;
            pageRows[page]++;
        }
        return new Layout(placements, Math.Max(height, y), pageRows.ToArray());
    }
}
