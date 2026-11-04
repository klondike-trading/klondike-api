using ExcelDna.Integration;

namespace Klondike.Excel;

/// <summary>
/// The worksheet functions. They only read their arguments: <see cref="CellFeeds"/> does the rest and pushes
/// the values, which Excel-DNA turns into RTD updates. Cells with the same function and arguments share one
/// subscription. Tables spill on Excel 365 and 2021.
/// </summary>
public static class Functions
{
    private const string Category = "Klondike";
    private const string SymbolHelp = "Unified CCXT symbol: BTC/USDT, BTC/USDT:USDT (perpetual)";
    private const string VenuesHelp = "CCXT exchange ids, \"binance,okx\" or a range; all venues when empty";
    private const string VenueHelp = "CCXT exchange id; the merged book when empty";
    private const string HeadersHelp = "TRUE (default) to show the column names";

    /// <summary><c>=KLONDIKE.QUOTE("BTC/USDT","bid")</c></summary>
    [ExcelFunction(Name = "KLONDIKE.QUOTE", Category = Category, Description = "Live best bid, ask, mid or spread of the merged book, or of one venue.")]
    public static object Quote(
        [ExcelArgument(Name = "symbol", Description = SymbolHelp)] object symbol,
        [ExcelArgument(Name = "field", Description = "bid (default), ask, mid, spread, spreadBps, bidQty, askQty, time; bidVenue, askVenue on the merged book")] object field,
        [ExcelArgument(Name = "venue", Description = VenueHelp)] object venue) =>
        Live(nameof(Quote), () =>
        {
            var (s, f, v) = (Args.Symbol(Norm(symbol)), Args.Text(Norm(field), "field", "bid")!, Args.Text(Norm(venue), "venue", null));
            return ([s, f, v], push => CellFeeds.Quote(AddIn.Hub, s, f, v, push));
        });

    /// <summary><c>=KLONDIKE.LAST("BTC/USDT")</c></summary>
    [ExcelFunction(Name = "KLONDIKE.LAST", Category = Category, Description = "Live field of the last trade of a symbol, on all venues or on one.")]
    public static object Last(
        [ExcelArgument(Name = "symbol", Description = SymbolHelp)] object symbol,
        [ExcelArgument(Name = "field", Description = "price (default), qty, side, venue, time")] object field,
        [ExcelArgument(Name = "venue", Description = "CCXT exchange id; all venues when empty")] object venue) =>
        Live(nameof(Last), () =>
        {
            var (s, f, v) = (Args.Symbol(Norm(symbol)), Args.Text(Norm(field), "field", "price")!, Args.Text(Norm(venue), "venue", null));
            return ([s, f, v], push => CellFeeds.Last(AddIn.Hub, s, f, v, push));
        });

    /// <summary><c>=KLONDIKE.ORDERBOOK("BTC/USDT",10)</c></summary>
    [ExcelFunction(Name = "KLONDIKE.ORDERBOOK", Category = Category, Description = "Live merged order book, bids and asks side by side, every level with its venue.")]
    public static object Orderbook(
        [ExcelArgument(Name = "symbol", Description = SymbolHelp)] object symbol,
        [ExcelArgument(Name = "depth", Description = "Levels per side, 1 to 200 (10 by default)")] object depth,
        [ExcelArgument(Name = "venues", Description = VenuesHelp)] object venues,
        [ExcelArgument(Name = "headers", Description = HeadersHelp)] object headers) =>
        Live(nameof(Orderbook), () =>
        {
            var (s, d, v, h) = (Args.Symbol(Norm(symbol)), Args.Int(Norm(depth), "depth", 10, 1, MarketHub.MaxStreamDepth), Args.Venues(Norm(venues)), Args.Bool(Norm(headers), "headers", true));
            return ([s, d, v, h], push => CellFeeds.Orderbook(AddIn.Hub, s, d, v, h, push));
        });

    /// <summary><c>=KLONDIKE.VENUES("BTC/USDT")</c></summary>
    [ExcelFunction(Name = "KLONDIKE.VENUES", Category = Category, Description = "Live best bid and ask of every venue of a symbol.")]
    public static object Venues(
        [ExcelArgument(Name = "symbol", Description = SymbolHelp)] object symbol,
        [ExcelArgument(Name = "headers", Description = HeadersHelp)] object headers) =>
        Live(nameof(Venues), () =>
        {
            var (s, h) = (Args.Symbol(Norm(symbol)), Args.Bool(Norm(headers), "headers", true));
            return ([s, h], push => CellFeeds.Venues(AddIn.Hub, s, h, push));
        });

    /// <summary><c>=KLONDIKE.TRADES("BTC/USDT",20)</c></summary>
    [ExcelFunction(Name = "KLONDIKE.TRADES", Category = Category, Description = "Live last trades of a symbol, newest first.")]
    public static object Trades(
        [ExcelArgument(Name = "symbol", Description = SymbolHelp)] object symbol,
        [ExcelArgument(Name = "limit", Description = "Trades shown, 1 to 1000 (20 by default)")] object limit,
        [ExcelArgument(Name = "venues", Description = VenuesHelp)] object venues,
        [ExcelArgument(Name = "headers", Description = HeadersHelp)] object headers) =>
        Live(nameof(Trades), () =>
        {
            var (s, l, v, h) = (Args.Symbol(Norm(symbol)), Args.Int(Norm(limit), "limit", 20, 1, MarketHub.MaxTradesWindow), Args.Venues(Norm(venues)), Args.Bool(Norm(headers), "headers", true));
            return ([s, l, v, h], push => CellFeeds.Trades(AddIn.Hub, s, l, v, h, push));
        });

    /// <summary><c>=KLONDIKE.OVERVIEW("impact")</c></summary>
    [ExcelFunction(Name = "KLONDIKE.OVERVIEW", Category = Category, Description = "Live section of the market overview.")]
    public static object Overview(
        [ExcelArgument(Name = "section", Description = "pairs (default), impact, feeds, leaders, pegs, fx, triangles, paths")] object section,
        [ExcelArgument(Name = "headers", Description = HeadersHelp)] object headers) =>
        Live(nameof(Overview), () =>
        {
            var (c, h) = (Args.Text(Norm(section), "section", "pairs")!, Args.Bool(Norm(headers), "headers", true));
            return ([c, h], push => CellFeeds.Overview(AddIn.Hub, c, h, push));
        });

    /// <summary><c>=KLONDIKE.PAIR("BTC/USDT","depth10Usd")</c></summary>
    [ExcelFunction(Name = "KLONDIKE.PAIR", Category = Category, Description = "Live field of a pair of the overview.")]
    public static object Pair(
        [ExcelArgument(Name = "symbol", Description = "A pair of the overview: BTC/USDT, ETH/USDT:USDT")] object symbol,
        [ExcelArgument(Name = "field", Description = "mid (default), status, depth10Usd, volume1hUsd, imbalance, crosses1h, basisBps...")] object field) =>
        Live(nameof(Pair), () =>
        {
            var (s, f) = (Args.Symbol(Norm(symbol)), Args.Text(Norm(field), "field", "mid")!);
            return ([s, f], push => CellFeeds.Pair(AddIn.Hub, s, f, push));
        });

    /// <summary><c>=KLONDIKE.IMPACT("BTC/USDT",1000000)</c></summary>
    [ExcelFunction(Name = "KLONDIKE.IMPACT", Category = Category, Description = "Live cost of a market order, taker fees included: a size of the overview.")]
    public static object Impact(
        [ExcelArgument(Name = "symbol", Description = "A pair of the overview: BTC/USDT, ETH/USDT:USDT")] object symbol,
        [ExcelArgument(Name = "notionalUsd", Description = "100000, 1000000 or 10000000")] object notionalUsd,
        [ExcelArgument(Name = "field", Description = "buyAllInBps (default), sellAllInBps, buyFilled, sellFilled, bestSingleBuyAllInBps, bestSingleBuyExchange...")] object field) =>
        Live(nameof(Impact), () =>
        {
            var (s, n, f) = (Args.Symbol(Norm(symbol)), Args.Number(Norm(notionalUsd), "notionalUsd"), Args.Text(Norm(field), "field", "buyAllInBps")!);
            return ([s, n, f], push => CellFeeds.Impact(AddIn.Hub, s, n, f, push));
        });

    /// <summary><c>=KLONDIKE.SYMBOLS("usdc")</c></summary>
    [ExcelFunction(Name = "KLONDIKE.SYMBOLS", Category = Category, Description = "Symbols with recent trades and their venues, read again every minute.")]
    public static object Symbols(
        [ExcelArgument(Name = "query", Description = "Case insensitive part of the symbol; all when empty")] object query,
        [ExcelArgument(Name = "headers", Description = HeadersHelp)] object headers) =>
        Live(nameof(Symbols), () =>
        {
            var (q, h) = (Args.Text(Norm(query), "query", null), Args.Bool(Norm(headers), "headers", true));
            return ([q, h], push => CellFeeds.Symbols(AddIn.Hub, q, h, push));
        });

    /// <summary><c>=KLONDIKE.STATUS()</c></summary>
    [ExcelFunction(Name = "KLONDIKE.STATUS", Category = Category, Description = "State of the connection: connected, connecting, reconnecting, idle, no API key, API key refused.")]
    public static object Status() =>
        Live(nameof(Status), () => ([], push => CellFeeds.Status(AddIn.Hub, push)));

    private static object Live(string function, Func<(object?[] Key, Func<Action<object>, IDisposable> Subscribe)> read)
    {
        // The wizard calls the function at every keystroke: nothing is subscribed for a half typed symbol.
        if (ExcelDnaUtil.IsInFunctionWizard())
        {
            return "";
        }
        try
        {
            var (key, subscribe) = read();
            // Excel-DNA compares the arguments of the calls: no nulls among them.
            var arguments = Array.ConvertAll(key, k => k ?? "");
            return ExcelAsyncUtil.Observe("KLONDIKE." + function, arguments, () => new FeedObservable(subscribe));
        }
        catch (ArgumentException e)
        {
            return Cells.Error(e.Message);
        }
    }

    private static object? Norm(object? value) => value switch
    {
        ExcelMissing or ExcelEmpty => null,
        object[,] range => NormRange(range),
        _ => value,
    };

    private static object?[,] NormRange(object[,] range)
    {
        var result = new object?[range.GetLength(0), range.GetLength(1)];
        for (var r = 0; r < range.GetLength(0); r++)
        {
            for (var c = 0; c < range.GetLength(1); c++)
            {
                result[r, c] = Norm(range[r, c]);
            }
        }
        return result;
    }

    private sealed class FeedObservable(Func<Action<object>, IDisposable> subscribe) : IExcelObservable
    {
        public IDisposable Subscribe(IExcelObserver observer)
        {
            void Push(object value) => observer.OnNext(value switch
            {
                CellError.NotAvailable => ExcelError.ExcelErrorNA,
                CellError.GettingData => ExcelError.ExcelErrorGettingData,
                _ => value,
            });
            try
            {
                return subscribe(Push);
            }
            catch (ArgumentException e)
            {
                observer.OnNext(Cells.Error(e.Message));
                return new Nothing();
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
