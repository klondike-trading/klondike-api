namespace Klondike.Excel;

/// <summary>The tables the functions spill, as arrays of doubles and strings. A missing value is an empty text.</summary>
public static class Tables
{
    /// <summary>The sections of <c>KLONDIKE.OVERVIEW</c>.</summary>
    public static IReadOnlyList<string> OverviewSections { get; } = ["pairs", "impact", "feeds", "leaders", "pegs", "fx", "triangles", "paths"];

    /// <summary>Best bids and asks side by side, best first.</summary>
    public static object[,] Orderbook(Orderbook book, int depth, bool headers)
    {
        var rows = Math.Min(depth, Math.Max(book.Bids.Count, book.Asks.Count));
        return Build(headers, ["Bid venue", "Bid qty", "Bid", "Ask", "Ask qty", "Ask venue"], Enumerable.Range(0, rows), i =>
        {
            var bid = i < book.Bids.Count ? book.Bids[i] : null;
            var ask = i < book.Asks.Count ? book.Asks[i] : null;
            return [bid?.Venue, bid?.Qty, bid?.Price, ask?.Price, ask?.Qty, ask?.Venue];
        });
    }

    /// <summary>The best bid and ask of every venue.</summary>
    public static object[,] Venues(Orderbook book, bool headers) =>
        Build(headers, ["Venue", "Bid", "Bid qty", "Ask", "Ask qty", "Time (UTC)"], book.Venues,
            v => [v.Venue, v.Bid, v.BidQty, v.Ask, v.AskQty, v.Ts is { } ts ? Cells.Date(ts) : null]);

    /// <summary>The last trades, newest first: the top of a table is what moves.</summary>
    public static object[,] Trades(IReadOnlyList<Trade> trades, int limit, bool headers) =>
        Build(headers, ["Time (UTC)", "Venue", "Side", "Price", "Qty"], trades.Reverse().Take(limit),
            t => [Cells.Date(t.Ts), t.Venue, t.Side, t.Price, t.Qty]);

    /// <summary>The symbols with recent trades.</summary>
    public static object[,] Symbols(IReadOnlyList<SymbolInfo> symbols, bool headers) =>
        Build(headers, ["Symbol", "Market", "Venues", "Last trade (UTC)"], symbols,
            s => [s.Symbol, s.Market, string.Join(",", s.Venues), Cells.Date(s.LastTradeTs)]);

    /// <summary>A section of the overview, null when it does not exist.</summary>
    public static object[,]? Overview(Overview overview, string section, bool headers)
    {
        switch (section.ToLowerInvariant())
        {
            case "pairs":
                return Build(headers,
                    ["Symbol", "Base", "Quote", "Market", "Status", "Exchanges", "Live", "Mid", "Volume 1h USD", "Top exchange", "Top share", "Depth ±10 bps USD", "Partial share", "Imbalance", "Crosses 1h", "Net crosses 1h", "Max net bps 1h", "Basis bps", "Reasons"],
                    overview.Pairs,
                    p => [p.Symbol, p.Base, p.Quote, p.Market, p.Status, p.Exchanges, p.Live, p.Mid, p.Volume1hUsd, p.VolumeTopExchange, p.VolumeTopShare, p.Depth10Usd, p.Depth10PartialShare, p.Imbalance, p.Crosses1h, p.CrossesPositive1h, p.MaxNetBps1h, p.BasisBps, string.Join(",", p.Reasons)]);
            case "impact":
                return Build(headers,
                    ["Symbol", "Notional USD", "Buy bps", "Sell bps", "Buy filled", "Sell filled", "Buy venues", "Sell venues", "Best single buy bps", "Best single sell bps", "Best single buy", "Best single sell"],
                    overview.Pairs.SelectMany(p => p.Impact.Select(i => (p.Symbol, Impact: i))),
                    r => [r.Symbol, r.Impact.NotionalUsd, r.Impact.BuyAllInBps, r.Impact.SellAllInBps, r.Impact.BuyFilled, r.Impact.SellFilled, r.Impact.BuyVenues, r.Impact.SellVenues, r.Impact.BestSingleBuyAllInBps, r.Impact.BestSingleSellAllInBps, r.Impact.BestSingleBuyExchange, r.Impact.BestSingleSellExchange]);
            case "feeds":
                return Build(headers, ["Time (UTC)", "Venues", "Venues known", "Live books", "Median lag ms"], [overview.Feeds],
                    f => [Cells.Date(overview.Ts), f.Venues, f.VenuesKnown, f.BooksLive, f.LagMsP50]);
            case "leaders":
                return Build(headers, ["Exchange", "Share"], overview.Leaders, l => [l.Exchange, l.Share]);
            case "pegs":
                return Build(headers, ["Symbol", "Bps", "Venue spread bps", "Venues"], overview.Implied.Pegs,
                    p => [p.Symbol, p.Bps, p.VenueSpreadBps, p.Venues]);
            case "fx":
                return Build(headers, ["Currency", "Implied", "Direct", "Deviation bps"], overview.Implied.Fx,
                    f => [f.Ccy, f.Implied, f.Direct, f.DevBps]);
            case "triangles":
                return Build(headers, ["Cross", "Cycle", "Best net bps"], overview.Implied.Triangles,
                    t => [t.Cross, t.Cycle, t.BestNetBps]);
            case "paths":
                return Build(headers, ["Sell", "Buy", "Quantity", "Path", "Cost bps", "Direct cost bps", "Time (UTC)"], overview.Paths,
                    p => [p.Sell, p.Buy, p.Quantity, string.Join(" > ", p.Tokens), p.CostBps, p.DirectCostBps, Cells.Date(p.Ts)]);
            default:
                return null;
        }
    }

    private static object[,] Build<T>(bool headers, string[] columns, IEnumerable<T> rows, Func<T, object?[]> row)
    {
        var values = rows.Select(row).ToList();
        var top = headers ? 1 : 0;
        if (values.Count + top == 0)
        {
            return new object[,] { { "" } };
        }
        var table = new object[values.Count + top, columns.Length];
        for (var c = 0; c < columns.Length; c++)
        {
            if (headers)
            {
                table[0, c] = columns[c];
            }
            for (var r = 0; r < values.Count; r++)
            {
                table[r + top, c] = Cells.Value(values[r][c], "");
            }
        }
        return table;
    }
}
