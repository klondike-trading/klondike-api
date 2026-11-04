using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klondike;

// The strings, lists and objects of these types are never null. The server may leave a field out or send
// null for an empty list (a nil slice in Go), and the JSON source generator gives null to an init property
// missing from the document, whatever its initial value: hence the setters.

/// <summary>Aggressor (taker) side of a trade.</summary>
[JsonConverter(typeof(SideJsonConverter))]
public enum Side
{
    /// <summary>The venue does not say.</summary>
    Unknown,
    /// <summary>The buyer was the taker.</summary>
    Buy,
    /// <summary>The seller was the taker.</summary>
    Sell,
}

internal sealed class SideJsonConverter : JsonConverter<Side>
{
    public override Side Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.ValueTextEquals("buy"u8) ? Side.Buy
        : reader.ValueTextEquals("sell"u8) ? Side.Sell
        : Side.Unknown;

    public override void Write(Utf8JsonWriter writer, Side value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { Side.Buy => "buy", Side.Sell => "sell", _ => "unknown" });
}

/// <summary>
/// One trade of a venue. <see cref="Ts"/> is the exchange time in milliseconds (receive time when the
/// venue gives none); <see cref="Qty"/> is in the base currency, in USD for inverse perpetuals.
/// </summary>
public sealed record Trade
{
    /// <summary>Milliseconds since the Unix epoch.</summary>
    public long Ts { get; init; }
    /// <summary>CCXT exchange id.</summary>
    public string Venue { get; init => field = value ?? ""; } = "";
    /// <summary>Price, in the quote currency.</summary>
    public double Price { get; init; }
    /// <summary>Quantity.</summary>
    public double Qty { get; init; }
    /// <summary>Aggressor side.</summary>
    public Side Side { get; init; }
    /// <summary><see cref="Ts"/> as a date.</summary>
    [JsonIgnore]
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Ts);
}

/// <summary>Trades of one symbol, oldest first.</summary>
public sealed record TradeBatch
{
    /// <summary>Unified CCXT symbol.</summary>
    public string Symbol { get; init => field = value ?? ""; } = "";
    /// <summary>The trades, oldest first.</summary>
    public IReadOnlyList<Trade> Trades { get; init => field = value ?? []; } = [];
}

/// <summary>A symbol the API received trades for recently.</summary>
public sealed record SymbolInfo
{
    /// <summary>Unified CCXT symbol.</summary>
    public string Symbol { get; init => field = value ?? ""; } = "";
    /// <summary><c>spot</c> or <c>perp</c>.</summary>
    public string Market { get; init => field = value ?? ""; } = "";
    /// <summary>The venues it traded on.</summary>
    public IReadOnlyList<string> Venues { get; init => field = value ?? []; } = [];
    /// <summary>Time of the last trade, milliseconds since the Unix epoch.</summary>
    public long LastTradeTs { get; init; }
    /// <summary><see cref="LastTradeTs"/> as a date.</summary>
    [JsonIgnore]
    public DateTimeOffset LastTradeTime => DateTimeOffset.FromUnixTimeMilliseconds(LastTradeTs);
}

/// <summary>The best bid and ask of one venue, null when the venue has none.</summary>
public sealed record VenueQuote
{
    /// <summary>CCXT exchange id.</summary>
    public string Venue { get; init => field = value ?? ""; } = "";
    /// <summary>Last update of the venue book, milliseconds since the Unix epoch.</summary>
    public long? Ts { get; init; }
    /// <summary>Best bid.</summary>
    public double? Bid { get; init; }
    /// <summary>Quantity at the best bid.</summary>
    public double? BidQty { get; init; }
    /// <summary>Best ask.</summary>
    public double? Ask { get; init; }
    /// <summary>Quantity at the best ask.</summary>
    public double? AskQty { get; init; }
    /// <summary><see cref="Ts"/> as a date.</summary>
    [JsonIgnore]
    public DateTimeOffset? Time => Ts is { } ts ? DateTimeOffset.FromUnixTimeMilliseconds(ts) : null;
}

/// <summary>A level of the merged book with the venue it comes from.</summary>
public sealed record Level
{
    /// <summary>Price, in the quote currency.</summary>
    public double Price { get; init; }
    /// <summary>Quantity.</summary>
    public double Qty { get; init; }
    /// <summary>CCXT exchange id.</summary>
    public string Venue { get; init => field = value ?? ""; } = "";
}

/// <summary>
/// The merged order book of a symbol: the best bid and ask of every venue and the first levels of each
/// side, best first.
/// </summary>
public sealed record Orderbook
{
    /// <summary>Unified CCXT symbol.</summary>
    public string Symbol { get; init => field = value ?? ""; } = "";
    /// <summary>Milliseconds since the Unix epoch.</summary>
    public long Ts { get; init; }
    /// <summary>Best bid and ask of each venue, on its whole book.</summary>
    public IReadOnlyList<VenueQuote> Venues { get; init => field = value ?? []; } = [];
    /// <summary>Best (highest) first.</summary>
    public IReadOnlyList<Level> Bids { get; init => field = value ?? []; } = [];
    /// <summary>Best (lowest) first.</summary>
    public IReadOnlyList<Level> Asks { get; init => field = value ?? []; } = [];
    /// <summary><see cref="Ts"/> as a date.</summary>
    [JsonIgnore]
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Ts);
}

/// <summary>
/// The market overview: the pairs followed permanently, what an order costs on the merged book and on
/// the best single venue, crosses between venues, venues setting the price, implied prices.
/// </summary>
public sealed record Overview
{
    /// <summary>Milliseconds since the Unix epoch.</summary>
    public long Ts { get; init; }
    /// <summary>Oldest minute of the 1 h counters.</summary>
    public long WindowsFromTs { get; init; }
    /// <summary>Health of the market data.</summary>
    public Feeds Feeds { get; init => field = value ?? new(); } = new();
    /// <summary>The pairs.</summary>
    public IReadOnlyList<Pair> Pairs { get; init => field = value ?? []; } = [];
    /// <summary>Share of the time each venue holds the best consolidated bid or ask over 1 h.</summary>
    public IReadOnlyList<Leader> Leaders { get; init => field = value ?? []; } = [];
    /// <summary>Stablecoin pegs, implied FX rates, triangles.</summary>
    public Implied Implied { get; init => field = value ?? new(); } = new();
    /// <summary>Best conversion paths.</summary>
    public IReadOnlyList<ConversionPath> Paths { get; init => field = value ?? []; } = [];
    /// <summary><see cref="Ts"/> as a date.</summary>
    [JsonIgnore]
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Ts);
}

/// <summary>Health of the market data.</summary>
public sealed record Feeds
{
    /// <summary>Venues with at least one live book.</summary>
    public int Venues { get; init; }
    /// <summary>Venues known.</summary>
    public int VenuesKnown { get; init; }
    /// <summary>Live books.</summary>
    public int BooksLive { get; init; }
    /// <summary>Median latency of the live books, ms.</summary>
    public double LagMsP50 { get; init; }
}

/// <summary>A pair of the overview. Amounts are in USD, costs in bps of the consolidated mid.</summary>
public sealed record Pair
{
    /// <summary>Unified CCXT symbol.</summary>
    public string Symbol { get; init => field = value ?? ""; } = "";
    /// <summary>Base currency.</summary>
    public string Base { get; init => field = value ?? ""; } = "";
    /// <summary><c>USDT</c>, <c>USDT:USDT</c>...</summary>
    public string Quote { get; init => field = value ?? ""; } = "";
    /// <summary><c>spot</c> or <c>perp</c>.</summary>
    public string Market { get; init => field = value ?? ""; } = "";
    /// <summary><c>ok</c>, <c>warn</c>, <c>alert</c> or <c>down</c>: data quality only.</summary>
    public string Status { get; init => field = value ?? ""; } = "";
    /// <summary>Why the status is not ok: <c>stale:&lt;venue&gt;</c>, <c>not_live:&lt;venue&gt;</c>...</summary>
    public IReadOnlyList<string> Reasons { get; init => field = value ?? []; } = [];
    /// <summary>Venues listing the pair.</summary>
    public int Exchanges { get; init; }
    /// <summary>Venues with a live book.</summary>
    public int Live { get; init; }
    /// <summary>Consolidated mid (consensus mid when the merged book is crossed).</summary>
    public double Mid { get; init; }
    /// <summary>Notional traded over 1 h, measured on the trades received.</summary>
    public double Volume1hUsd { get; init; }
    /// <summary>Venue with the largest volume over 1 h.</summary>
    public string VolumeTopExchange { get; init => field = value ?? ""; } = "";
    /// <summary>Share of that venue.</summary>
    public double VolumeTopShare { get; init; }
    /// <summary>Bid + ask within ±10 bps of the mid.</summary>
    public double Depth10Usd { get; init; }
    /// <summary>Share of the depth from books stopping before ±10 bps: the depth is then a lower bound.</summary>
    public double? Depth10PartialShare { get; init; }
    /// <summary>(bid − ask) / (bid + ask) within ±50 bps.</summary>
    public double Imbalance { get; init; }
    /// <summary>Cost of market orders of several sizes.</summary>
    public IReadOnlyList<Impact> Impact { get; init => field = value ?? []; } = [];
    /// <summary>Crosses between venues over 1 h, transient ones included.</summary>
    public int Crosses1h { get; init; }
    /// <summary>Episodes net positive after taker fees.</summary>
    public int CrossesPositive1h { get; init; }
    /// <summary>Best net gain of a cross over 1 h, bps.</summary>
    public double? MaxNetBps1h { get; init; }
    /// <summary>Perpetuals: (mid perp − mid spot) / mid spot.</summary>
    public double? BasisBps { get; init; }
}

/// <summary>
/// The cost of a market order, taker fees included, on the merged book and on the best single venue. A
/// cost is null when the book cannot fill the order; the filled fraction then says how much it can.
/// </summary>
public sealed record Impact
{
    /// <summary>Size of the order, USD.</summary>
    public double NotionalUsd { get; init; }
    /// <summary>Cost of a buy on the merged book, bps.</summary>
    public double? BuyAllInBps { get; init; }
    /// <summary>Cost of a sell on the merged book, bps.</summary>
    public double? SellAllInBps { get; init; }
    /// <summary>Fraction of a buy the merged book can fill.</summary>
    public double BuyFilled { get; init; }
    /// <summary>Fraction of a sell the merged book can fill.</summary>
    public double SellFilled { get; init; }
    /// <summary>Venues a buy goes through.</summary>
    public int BuyVenues { get; init; }
    /// <summary>Venues a sell goes through.</summary>
    public int SellVenues { get; init; }
    /// <summary>Cost of a buy on the best single venue, bps.</summary>
    public double? BestSingleBuyAllInBps { get; init; }
    /// <summary>Cost of a sell on the best single venue, bps.</summary>
    public double? BestSingleSellAllInBps { get; init; }
    /// <summary>Best single venue for a buy.</summary>
    public string BestSingleBuyExchange { get; init => field = value ?? ""; } = "";
    /// <summary>Best single venue for a sell.</summary>
    public string BestSingleSellExchange { get; init => field = value ?? ""; } = "";
}

/// <summary>The share of the time a venue holds the best consolidated bid or ask; the shares sum to 1.</summary>
public sealed record Leader
{
    /// <summary>CCXT exchange id.</summary>
    public string Exchange { get; init => field = value ?? ""; } = "";
    /// <summary>Share of the time, 0 to 1.</summary>
    public double Share { get; init; }
}

/// <summary>Implied prices of the overview.</summary>
public sealed record Implied
{
    /// <summary>Stablecoin pegs.</summary>
    public IReadOnlyList<Peg> Pegs { get; init => field = value ?? []; } = [];
    /// <summary>Implied FX rates.</summary>
    public IReadOnlyList<ImpliedFx> Fx { get; init => field = value ?? []; } = [];
    /// <summary>Triangles through a cross pair and USDT.</summary>
    public IReadOnlyList<Triangle> Triangles { get; init => field = value ?? []; } = [];
}

/// <summary>A stablecoin pair: its consolidated mid in bps from 1.</summary>
public sealed record Peg
{
    /// <summary>Unified CCXT symbol.</summary>
    public string Symbol { get; init => field = value ?? ""; } = "";
    /// <summary>Consolidated mid, bps from 1.</summary>
    public double Bps { get; init; }
    /// <summary>Highest minus lowest venue mid.</summary>
    public double VenueSpreadBps { get; init; }
    /// <summary>Venues.</summary>
    public int Venues { get; init; }
}

/// <summary>The rate of a fiat currency in USD implied by crypto pairs, against the direct pair when there is one.</summary>
public sealed record ImpliedFx
{
    /// <summary>The fiat currency.</summary>
    public string Ccy { get; init => field = value ?? ""; } = "";
    /// <summary>The implied rate.</summary>
    public double Implied { get; init; }
    /// <summary>The rate of the direct pair.</summary>
    public double? Direct { get; init; }
    /// <summary>Deviation of the implied rate from the direct one, bps.</summary>
    public double? DevBps { get; init; }
}

/// <summary>The best of the two cycles through a cross pair and USDT, net of the taker fees of its three legs.</summary>
public sealed record Triangle
{
    /// <summary>The cross pair.</summary>
    public string Cross { get; init => field = value ?? ""; } = "";
    /// <summary>The best cycle.</summary>
    public string Cycle { get; init => field = value ?? ""; } = "";
    /// <summary>Its net gain, bps.</summary>
    public double BestNetBps { get; init; }
}

/// <summary>The best conversion path, fees included; costs are against the consolidated mid of the direct pair.</summary>
public sealed record ConversionPath
{
    /// <summary>Token sold.</summary>
    public string Sell { get; init => field = value ?? ""; } = "";
    /// <summary>Token bought.</summary>
    public string Buy { get; init => field = value ?? ""; } = "";
    /// <summary>Quantity sold.</summary>
    public double Quantity { get; init; }
    /// <summary>Tokens of the path.</summary>
    public IReadOnlyList<string> Tokens { get; init => field = value ?? []; } = [];
    /// <summary>Cost of the path, bps.</summary>
    public double CostBps { get; init; }
    /// <summary>Cost of the direct pair, bps.</summary>
    public double? DirectCostBps { get; init; }
    /// <summary>Milliseconds since the Unix epoch.</summary>
    public long Ts { get; init; }
}
