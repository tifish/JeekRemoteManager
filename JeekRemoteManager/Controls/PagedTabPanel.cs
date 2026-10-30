using Avalonia;
using Avalonia.Controls;
using JeekRemoteManager.Services;

namespace JeekRemoteManager.Controls;

/// <summary>Pages headers only. Every TabItem remains in the selector with its session intact.</summary>
public sealed class PagedTabPanel : Panel
{
    private int _maxRows = 5;
    private double _heightBudget = double.PositiveInfinity;
    private Func<Control, bool>? _filter;
    private Control? _anchor;
    private Control[] _matching = [];
    private TabPagination.Layout _layout = TabPagination.Calculate([], 1, 5, double.PositiveInfinity);

    public PagedTabPanel() => ClipToBounds = true;

    public event EventHandler? PaginationChanged;
    public int CurrentPage { get; private set; }
    public int PageCount => _layout.RowsPerPage.Length;
    public int MatchingCount => _matching.Length;
    public int VisibleRows => _layout.RowsPerPage[CurrentPage];
    public int MaxRows
    {
        get => _maxRows;
        set { if (_maxRows != value) { _maxRows = Math.Clamp(value, 1, 20); InvalidateMeasure(); } }
    }
    public double HeightBudget
    {
        get => _heightBudget;
        set { if (Math.Abs(_heightBudget - value) > 0.5) { _heightBudget = value; InvalidateMeasure(); } }
    }

    public void SetFilter(Func<Control, bool>? filter)
    {
        _filter = filter;
        _anchor = null;
        CurrentPage = 0;
        InvalidateMeasure();
    }

    public void EnsureVisible(Control tab)
    {
        _anchor = tab;
        InvalidateMeasure();
    }

    public void MovePage(int delta)
    {
        CurrentPage = Math.Clamp(CurrentPage + delta, 0, PageCount - 1);
        _anchor = _matching.Where((_, i) => _layout.Items[i].Page == CurrentPage).FirstOrDefault();
        InvalidateMeasure();
    }

    public bool IsOnCurrentPage(Control tab)
    {
        var index = Array.IndexOf(_matching, tab);
        return index >= 0 && _layout.Items[index].Page == CurrentPage;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 1000;
        foreach (var child in Children)
            child.Measure(new Size(width, double.PositiveInfinity));
        _matching = Children.Where(c => c.IsVisible && (_filter?.Invoke(c) ?? true)).ToArray();
        _layout = TabPagination.Calculate(_matching.Select(c => c.DesiredSize).ToArray(), width, MaxRows, HeightBudget);
        var anchorIndex = _anchor is null ? -1 : Array.IndexOf(_matching, _anchor);
        if (anchorIndex < 0)
            _anchor = null;
        CurrentPage = anchorIndex >= 0 ? _layout.Items[anchorIndex].Page : Math.Min(CurrentPage, PageCount - 1);
        return new Size(width, _layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Arrange off-page headers outside the clipped viewport. IsVisible stays true:
        // hiding a TabItem would also hide it from keyboard selection and automation.
        foreach (var child in Children)
            child.Arrange(new Rect(-10000, -10000, child.DesiredSize.Width, child.DesiredSize.Height));
        for (var i = 0; i < _matching.Length; i++)
            if (_layout.Items[i].Page == CurrentPage)
                _matching[i].Arrange(_layout.Items[i].Bounds);
        PaginationChanged?.Invoke(this, EventArgs.Empty);
        return finalSize;
    }
}
