namespace ISpy.Core.Media;

/// <summary>
/// Restores the user's tile arrangement across runs.
/// </summary>
public static class TileOrder
{
    /// <summary>
    /// Orders <paramref name="items"/> to match a previously saved key sequence. Items whose keys
    /// are in the saved order come first, in that order; anything new keeps its natural position
    /// after them - so adding a ninth camera does not scramble the eight already arranged.
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(
        IReadOnlyList<T> items, Func<T, string> keyOf, IReadOnlyList<string>? savedOrder)
    {
        if (savedOrder is null || savedOrder.Count == 0) return items;

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < savedOrder.Count; i++) rank.TryAdd(savedOrder[i], i);

        return items
            .Select((item, index) => (Item: item, Index: index))
            .OrderBy(entry => rank.TryGetValue(keyOf(entry.Item), out var r) ? r : int.MaxValue)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToArray();
    }
}
