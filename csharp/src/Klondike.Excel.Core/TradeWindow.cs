namespace Klondike.Excel;

/// <summary>The last trades of a stream, oldest first, completed by snapshots of the REST API.</summary>
internal sealed class TradeWindow
{
    private List<Trade> _trades = [];

    /// <summary>Adds trades of the stream, keeping the last <paramref name="size"/>.</summary>
    public IReadOnlyList<Trade> Append(IReadOnlyList<Trade> trades, int size)
    {
        _trades.AddRange(trades);
        Trim(size);
        return _trades;
    }

    /// <summary>
    /// Adds a snapshot: the stream and the snapshot overlap, a trade of both counts once. Sorted by time,
    /// the order of the snapshot and of the stream.
    /// </summary>
    public IReadOnlyList<Trade> Merge(IReadOnlyList<Trade> snapshot, int size)
    {
        var known = new HashSet<Trade>(snapshot);
        _trades = [.. snapshot.Concat(_trades.Where(t => !known.Contains(t))).OrderBy(t => t.Ts)];
        Trim(size);
        return _trades;
    }

    private void Trim(int size)
    {
        if (_trades.Count > size)
        {
            _trades.RemoveRange(0, _trades.Count - size);
        }
    }
}
