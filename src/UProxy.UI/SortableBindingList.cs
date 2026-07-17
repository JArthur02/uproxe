using System.ComponentModel;

namespace UProxy.UI;

public sealed class SortableBindingList<T> : BindingList<T>
{
    private bool _isSorted;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private PropertyDescriptor? _sortProperty;

    protected override bool SupportsSortingCore => true;

    protected override bool IsSortedCore => _isSorted;

    protected override ListSortDirection SortDirectionCore => _sortDirection;

    protected override PropertyDescriptor? SortPropertyCore => _sortProperty;

    protected override void ApplySortCore(PropertyDescriptor prop, ListSortDirection direction)
    {
        _sortProperty = prop;
        _sortDirection = direction;

        if (Items is not List<T> items)
            return;

        items.Sort((x, y) =>
        {
            var xv = prop.GetValue(x);
            var yv = prop.GetValue(y);

            int cmp;
            if (xv is null && yv is null)
                cmp = 0;
            else if (xv is null)
                cmp = -1;
            else if (yv is null)
                cmp = 1;
            else if (xv is IComparable comparable)
                cmp = comparable.CompareTo(yv);
            else
                cmp = string.Compare(xv.ToString(), yv.ToString(), StringComparison.Ordinal);

            return direction == ListSortDirection.Descending ? -cmp : cmp;
        });

        _isSorted = true;
        OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
    }

    protected override void RemoveSortCore()
    {
        _isSorted = false;
        _sortProperty = null;
    }

    public void NotifyItemChanged(int index) => ResetItem(index);
}
