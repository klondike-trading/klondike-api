namespace Klondike.Excel;

/// <summary>
/// What each function of the add-in shows: subscribes to the hub and gives the values of the cell, a
/// <see cref="CellError"/>, a table or the text of an error. Arguments are checked by the caller
/// (<see cref="Args"/>), except the fields and sections, checked here before subscribing.
/// </summary>
public static class CellFeeds
{
    private static readonly TimeSpan SymbolsRefresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SymbolsRetry = TimeSpan.FromSeconds(5);

    /// <summary><c>KLONDIKE.ORDERBOOK</c>: the merged book side by side.</summary>
    public static IDisposable Orderbook(MarketHub hub, string symbol, int depth, string? venues, bool headers, Action<object> push)
    {
        push(CellError.GettingData);
        return hub.WatchOrderbook(symbol, depth, venues, Show<Orderbook>(push, b => Tables.Orderbook(b, depth, headers)));
    }

    /// <summary><c>KLONDIKE.VENUES</c>: the best bid and ask of every venue.</summary>
    public static IDisposable Venues(MarketHub hub, string symbol, bool headers, Action<object> push)
    {
        push(CellError.GettingData);
        return hub.WatchOrderbook(symbol, 1, null, Show<Orderbook>(push, b => Tables.Venues(b, headers)));
    }

    /// <summary><c>KLONDIKE.QUOTE</c>: a field of the merged book, or of one venue.</summary>
    public static IDisposable Quote(MarketHub hub, string symbol, string field, string? venue, Action<object> push)
    {
        var scalar = Distinct(push);
        scalar(CellError.GettingData);
        if (venue is null)
        {
            var get = Fields.Get(Fields.Book, field);
            return hub.WatchOrderbook(symbol, 1, null, Show<Orderbook>(scalar, b => Cells.Value(get(b), CellError.NotAvailable)));
        }
        var getVenue = Fields.Get(Fields.Venue, field);
        return hub.WatchOrderbook(symbol, 1, null, Show<Orderbook>(scalar, b =>
            b.Venues.FirstOrDefault(v => string.Equals(v.Venue, venue, StringComparison.OrdinalIgnoreCase)) is { } quote
                ? Cells.Value(getVenue(quote), CellError.NotAvailable)
                : CellError.NotAvailable));
    }

    /// <summary><c>KLONDIKE.TRADES</c>: the last trades, newest first.</summary>
    public static IDisposable Trades(MarketHub hub, string symbol, int limit, string? venues, bool headers, Action<object> push)
    {
        push(CellError.GettingData);
        return hub.WatchTrades(symbol, limit, venues, Show<IReadOnlyList<Trade>>(push, t => Tables.Trades(t, limit, headers)));
    }

    /// <summary><c>KLONDIKE.LAST</c>: a field of the last trade, on all venues or on one.</summary>
    public static IDisposable Last(MarketHub hub, string symbol, string field, string? venue, Action<object> push)
    {
        var get = Fields.Get(Fields.Trade, field);
        var scalar = Distinct(push);
        scalar(CellError.GettingData);
        return hub.WatchTrades(symbol, 1, venue, Show<IReadOnlyList<Trade>>(scalar, t =>
            t.Count > 0 ? Cells.Value(get(t[^1]), CellError.NotAvailable) : CellError.GettingData));
    }

    /// <summary><c>KLONDIKE.OVERVIEW</c>: a section of the overview.</summary>
    public static IDisposable Overview(MarketHub hub, string section, bool headers, Action<object> push)
    {
        if (!Tables.OverviewSections.Contains(section, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"unknown section \"{section}\": {string.Join(", ", Tables.OverviewSections)}");
        }
        push(CellError.GettingData);
        return hub.WatchOverview(Show<Overview>(push, o => Tables.Overview(o, section, headers)!));
    }

    /// <summary><c>KLONDIKE.PAIR</c>: a field of a pair of the overview.</summary>
    public static IDisposable Pair(MarketHub hub, string symbol, string field, Action<object> push)
    {
        var get = Fields.Get(Fields.Pair, field);
        var scalar = Distinct(push);
        scalar(CellError.GettingData);
        return hub.WatchOverview(Show<Overview>(scalar, o =>
            o.Pairs.FirstOrDefault(p => p.Symbol == symbol) is { } pair
                ? Cells.Value(get(pair), CellError.NotAvailable)
                : CellError.NotAvailable));
    }

    /// <summary><c>KLONDIKE.IMPACT</c>: what an order of that size costs, a size of the overview.</summary>
    public static IDisposable Impact(MarketHub hub, string symbol, double notional, string field, Action<object> push)
    {
        var get = Fields.Get(Fields.Impact, field);
        var scalar = Distinct(push);
        scalar(CellError.GettingData);
        return hub.WatchOverview(Show<Overview>(scalar, o =>
        {
            var impact = o.Pairs.FirstOrDefault(p => p.Symbol == symbol)?.Impact.FirstOrDefault(i => i.NotionalUsd == notional);
            return impact is null ? CellError.NotAvailable : Cells.Value(get(impact), CellError.NotAvailable);
        }));
    }

    /// <summary><c>KLONDIKE.STATUS</c>: the state of the connections.</summary>
    public static IDisposable Status(MarketHub hub, Action<object> push) => hub.WatchStatus(push);

    /// <summary><c>KLONDIKE.SYMBOLS</c>: the symbols with recent trades, read again every minute.</summary>
    public static IDisposable Symbols(MarketHub hub, string? query, bool headers, Action<object> push)
    {
        push(CellError.GettingData);
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var wait = SymbolsRefresh;
                try
                {
                    push(Tables.Symbols(await hub.GetSymbolsAsync(query, stop.Token).ConfigureAwait(false), headers));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    push(Cells.Error(e is KlondikeApiException api ? api.ErrorMessage : e.Message));
                    wait = SymbolsRetry; // the key may just have been set
                }
                try
                {
                    await Task.Delay(wait, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
        return new CancelOnDispose(stop);
    }

    private static Action<FeedUpdate<T>> Show<T>(Action<object> push, Func<T, object> view) where T : class => update =>
    {
        if (update.Error is { } error)
        {
            push(Cells.Error(error));
        }
        else if (update.Value is { } value)
        {
            push(view(value));
        }
    };

    // A cell giving one value is only pushed when it changes: formulas depending on it are not recalculated.
    private static Action<object> Distinct(Action<object> push)
    {
        object? last = null;
        return value =>
        {
            if (!Equals(last, value))
            {
                last = value;
                push(value);
            }
        };
    }

    private sealed class CancelOnDispose(CancellationTokenSource source) : IDisposable
    {
        public void Dispose() => source.Cancel();
    }
}
