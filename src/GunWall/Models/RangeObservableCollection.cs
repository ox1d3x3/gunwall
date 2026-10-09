using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GunWall.Models;

/// <summary>
/// An ObservableCollection that can replace its whole contents with ONE change
/// notification.
///
/// Refilling with Clear() followed by a loop of Add() raises a notification per
/// row, and a bound list does work for each. The traffic breakdown was refilled
/// that way on every connection snapshot - fifty-two notifications per refresh for
/// fifty rows - and the services list with one per Windows service, a few hundred.
///
/// ReplaceAll raises a single Reset. That is exactly what Clear() raised before
/// the first Add, so a bound list ends in the same state it reached before: same
/// items, same order, selection cleared, scroll position handled as it was after
/// Clear.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        // Materialised first: if the source is lazy and throws part-way, the
        // collection is left as it was rather than half-replaced.
        var incoming = new List<T>(items);

        CheckReentrancy();
        Items.Clear();
        foreach (T item in incoming) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }
}
