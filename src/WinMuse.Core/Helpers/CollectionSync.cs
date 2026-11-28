using System.Collections.ObjectModel;

namespace WinMuse.Helpers;

/// <summary>
/// Updates an ObservableCollection in place to match a new list, so bound lists only animate the items that actually
/// changed instead of re-rendering everything (no "page refresh" flash while metadata is being updated).
/// </summary>
public static class CollectionSync
{
    /// <param name="key">Identity of an item (e.g. album key).</param>
    /// <param name="same">Whether an existing item still shows the same content; if not, it is replaced in place.</param>
    /// <returns>False when the change is too large and the caller should simply replace the collection.</returns>
    public static bool Sync<T>(ObservableCollection<T> target, IList<T> source, Func<T, object> key, Func<T, T, bool>? same = null, int maxMoves = 300)
    {
        var sourceKeys = new HashSet<object>(source.Select(key));
        for (int i = target.Count - 1; i >= 0; i--)
            if (!sourceKeys.Contains(key(target[i]))) target.RemoveAt(i);

        int moves = 0;
        for (int i = 0; i < source.Count; i++)
        {
            var k = key(source[i]);
            if (i < target.Count && Equals(key(target[i]), k))
            {
                if (same != null && !same(target[i], source[i])) target[i] = source[i];
                continue;
            }
            int found = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (Equals(key(target[j]), k)) { found = j; break; }
            if (found >= 0)
            {
                if (++moves > maxMoves) return false;
                target.Move(found, i);
                if (same != null && !same(target[i], source[i])) target[i] = source[i];
            }
            else target.Insert(i, source[i]);
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
        return true;
    }

    /// <summary>Syncs, or replaces the collection when the change is large. Returns the collection to bind.</summary>
    public static ObservableCollection<T> SyncOrReplace<T>(ObservableCollection<T>? target, IList<T> source, Func<T, object> key, Func<T, T, bool>? same = null)
    {
        if (target == null || !Sync(target, source, key, same)) return new ObservableCollection<T>(source);
        return target;
    }
}
