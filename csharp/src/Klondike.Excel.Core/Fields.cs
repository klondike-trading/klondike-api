namespace Klondike.Excel;

/// <summary>The fields of the functions giving one value: names are those of the API, whatever their case.</summary>
public static class Fields
{
    private static Dictionary<string, TValue> Table<TValue>(params (string Name, TValue Get)[] fields) =>
        fields.ToDictionary(f => f.Name, f => f.Get, StringComparer.OrdinalIgnoreCase);

    private static double? Mid(double? bid, double? ask) => bid + ask is { } sum ? sum / 2 : null;

    private static double? SpreadBps(double? bid, double? ask) =>
        Mid(bid, ask) is { } mid && mid != 0 ? (ask - bid) / mid * 10_000 : null;

    private static double? Spread(double? bid, double? ask) => ask - bid;

    /// <summary>The fields of a merged book.</summary>
    public static IReadOnlyDictionary<string, Func<Orderbook, object?>> Book { get; } = Table<Func<Orderbook, object?>>(
        ("bid", b => b.Bids.FirstOrDefault()?.Price),
        ("bidQty", b => b.Bids.FirstOrDefault()?.Qty),
        ("bidVenue", b => b.Bids.FirstOrDefault()?.Venue),
        ("ask", b => b.Asks.FirstOrDefault()?.Price),
        ("askQty", b => b.Asks.FirstOrDefault()?.Qty),
        ("askVenue", b => b.Asks.FirstOrDefault()?.Venue),
        ("mid", b => Mid(b.Bids.FirstOrDefault()?.Price, b.Asks.FirstOrDefault()?.Price)),
        ("spread", b => Spread(b.Bids.FirstOrDefault()?.Price, b.Asks.FirstOrDefault()?.Price)),
        ("spreadBps", b => SpreadBps(b.Bids.FirstOrDefault()?.Price, b.Asks.FirstOrDefault()?.Price)),
        ("time", b => Cells.Date(b.Ts)));

    /// <summary>The fields of the best bid and ask of one venue.</summary>
    public static IReadOnlyDictionary<string, Func<VenueQuote, object?>> Venue { get; } = Table<Func<VenueQuote, object?>>(
        ("bid", q => q.Bid),
        ("bidQty", q => q.BidQty),
        ("ask", q => q.Ask),
        ("askQty", q => q.AskQty),
        ("mid", q => Mid(q.Bid, q.Ask)),
        ("spread", q => Spread(q.Bid, q.Ask)),
        ("spreadBps", q => SpreadBps(q.Bid, q.Ask)),
        ("time", q => q.Ts is { } ts ? Cells.Date(ts) : null));

    /// <summary>The fields of a trade.</summary>
    public static IReadOnlyDictionary<string, Func<Trade, object?>> Trade { get; } = Table<Func<Trade, object?>>(
        ("price", t => t.Price),
        ("qty", t => t.Qty),
        ("side", t => t.Side),
        ("venue", t => t.Venue),
        ("time", t => Cells.Date(t.Ts)));

    /// <summary>The fields of a pair of the overview.</summary>
    public static IReadOnlyDictionary<string, Func<Pair, object?>> Pair { get; } = Table<Func<Pair, object?>>(
        ("base", p => p.Base),
        ("quote", p => p.Quote),
        ("market", p => p.Market),
        ("status", p => p.Status),
        ("reasons", p => string.Join(",", p.Reasons)),
        ("exchanges", p => p.Exchanges),
        ("live", p => p.Live),
        ("mid", p => p.Mid),
        ("volume1hUsd", p => p.Volume1hUsd),
        ("volumeTopExchange", p => p.VolumeTopExchange),
        ("volumeTopShare", p => p.VolumeTopShare),
        ("depth10Usd", p => p.Depth10Usd),
        ("depth10PartialShare", p => p.Depth10PartialShare),
        ("imbalance", p => p.Imbalance),
        ("crosses1h", p => p.Crosses1h),
        ("crossesPositive1h", p => p.CrossesPositive1h),
        ("maxNetBps1h", p => p.MaxNetBps1h),
        ("basisBps", p => p.BasisBps));

    /// <summary>The fields of the cost of an order.</summary>
    public static IReadOnlyDictionary<string, Func<Impact, object?>> Impact { get; } = Table<Func<Impact, object?>>(
        ("buyAllInBps", i => i.BuyAllInBps),
        ("sellAllInBps", i => i.SellAllInBps),
        ("buyFilled", i => i.BuyFilled),
        ("sellFilled", i => i.SellFilled),
        ("buyVenues", i => i.BuyVenues),
        ("sellVenues", i => i.SellVenues),
        ("bestSingleBuyAllInBps", i => i.BestSingleBuyAllInBps),
        ("bestSingleSellAllInBps", i => i.BestSingleSellAllInBps),
        ("bestSingleBuyExchange", i => i.BestSingleBuyExchange),
        ("bestSingleSellExchange", i => i.BestSingleSellExchange));

    /// <summary>The field of that name; an unknown one is an error listing them.</summary>
    public static TValue Get<TValue>(IReadOnlyDictionary<string, TValue> fields, string name) =>
        fields.TryGetValue(name, out var field)
            ? field
            : throw new ArgumentException($"unknown field \"{name}\": {string.Join(", ", fields.Keys)}");
}
