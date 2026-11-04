using Klondike.Excel;

namespace Klondike.Tests;

/// <summary>
/// End to end against a running API, skipped unless KLONDIKE_TEST_URL is set.
/// <code>
/// KLONDIKE_TEST_URL=http://localhost:8093 KLONDIKE_TEST_KEY=kl_dev0dev0dev0dev0dev0dev0dev0dev0 dotnet test --filter E2E
/// </code>
/// (api-go/dev.sh starts a local stack whose fake feed publishes BTC/USDT, ETH/USDT, BTC/USDT:USDT and SOL/USDC.)
/// </summary>
public sealed class E2ETests
{
    private const string Symbol = "BTC/USDT";
    private static readonly string Url = Environment.GetEnvironmentVariable("KLONDIKE_TEST_URL") ?? "";
    private static readonly string Key = Environment.GetEnvironmentVariable("KLONDIKE_TEST_KEY") ?? "kl_dev0dev0dev0dev0dev0dev0dev0dev0";

    private sealed class E2EFactAttribute : FactAttribute
    {
        public E2EFactAttribute()
        {
            if (Url.Length == 0)
            {
                Skip = "KLONDIKE_TEST_URL not set";
            }
        }
    }

    private sealed class E2ETheoryAttribute : TheoryAttribute
    {
        public E2ETheoryAttribute()
        {
            if (Url.Length == 0)
            {
                Skip = "KLONDIKE_TEST_URL not set";
            }
        }
    }

    private static KlondikeClient Client() => new(Key, new KlondikeClientOptions { BaseUrl = Url });

    [E2EFact]
    public async Task Rest()
    {
        using var client = Client();
        var symbols = await client.GetSymbolsAsync();
        var btc = Assert.Single(symbols, s => s.Symbol == Symbol);
        Assert.Equal("spot", btc.Market);
        Assert.NotEmpty(btc.Venues);
        Assert.All(await client.GetSymbolsAsync("usdc"), s => Assert.Contains("usdc", s.Symbol, StringComparison.OrdinalIgnoreCase));

        var trades = await client.GetTradesAsync(Symbol, new TradesOptions { Limit = 5 });
        Assert.InRange(trades.Trades.Count, 1, 5);
        Assert.All(trades.Trades, t => Assert.True(t.Price > 0 && t.Qty > 0));
        var venue = trades.Trades[^1].Venue;
        Assert.All((await client.GetTradesAsync(Symbol, new TradesOptions { Limit = 20, Venues = [venue] })).Trades, t => Assert.Equal(venue, t.Venue));

        var book = await client.GetOrderbookAsync(Symbol, new OrderbookOptions { Depth = 5 });
        Assert.InRange(book.Bids.Count, 1, 5);
        var names = book.Venues.Select(v => v.Venue).ToHashSet();
        Assert.All(book.Bids.Concat(book.Asks), l => Assert.Contains(l.Venue, names));

        var overview = await client.GetOverviewAsync();
        Assert.Contains(overview.Pairs, p => p.Symbol == Symbol);
        Assert.True(overview.Pairs[0].Impact[0].NotionalUsd > 0);

        var bad = await Assert.ThrowsAsync<KlondikeApiException>(() => client.GetTradesAsync(Symbol, new TradesOptions { Limit = 5000 }));
        Assert.Equal("bad_request", bad.Code);

        using var wrong = new KlondikeClient("kl_wrong", new KlondikeClientOptions { BaseUrl = Url });
        Assert.True((await Assert.ThrowsAsync<KlondikeApiException>(() => wrong.GetSymbolsAsync())).IsUnauthorized);
        Assert.True((await Assert.ThrowsAsync<KlondikeApiException>(() => wrong.OpenStreamAsync())).IsUnauthorized);
    }

    [E2ETheory]
    [InlineData(StreamFormat.Json)]
    [InlineData(StreamFormat.Capnp)]
    public async Task Stream(StreamFormat format)
    {
        using var client = Client();
        await using var stream = await client.OpenStreamAsync(new StreamOptions { Format = format });
        await stream.SubscribeTradesAsync(Symbol);
        await stream.SubscribeOrderbookAsync(Symbol, 5);
        await stream.SubscribeOverviewAsync();
        await stream.SubscribeOrderbookAsync(Symbol, 999);
        await stream.PingAsync();

        var seen = new HashSet<Type>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var message in stream.ReadAllAsync(timeout.Token))
        {
            seen.Add(message.GetType());
            switch (message)
            {
                case TradesMessage m:
                    Assert.Equal(Symbol, m.Trades.Symbol);
                    Assert.All(m.Trades.Trades, t => Assert.True(t.Price > 0));
                    break;
                case OrderbookMessage m:
                    Assert.NotEmpty(m.Orderbook.Venues);
                    Assert.InRange(m.Orderbook.Bids.Count, 0, 5);
                    Assert.True(m.Orderbook.Ts > 0);
                    break;
                case OverviewMessage m:
                    Assert.NotEmpty(m.Overview.Pairs);
                    break;
                case ErrorMessage m:
                    Assert.Equal("bad_request", m.Error.Code);
                    break;
            }
            if (seen.IsSupersetOf([typeof(TradesMessage), typeof(OrderbookMessage), typeof(OverviewMessage), typeof(SubscribedMessage), typeof(ErrorMessage), typeof(PongMessage)]))
            {
                return;
            }
        }
        Assert.Fail("the stream ended: " + stream.Error);
    }

    [E2EFact]
    public async Task EveryFunctionOfTheAddIn()
    {
        await using var hub = new MarketHub(new MarketHubOptions { ApiKey = Key, BaseUrl = Url });
        var cells = new Dictionary<string, object?>();
        var gate = new object();
        Action<object> Cell(string name) => value =>
        {
            lock (gate)
            {
                cells[name] = value;
            }
        };
        var feeds = new[]
        {
            CellFeeds.Orderbook(hub, Symbol, 5, null, true, Cell("orderbook")),
            CellFeeds.Orderbook(hub, Symbol, 5, "binance,okx", false, Cell("orderbook venues")),
            CellFeeds.Venues(hub, Symbol, true, Cell("venues")),
            CellFeeds.Quote(hub, Symbol, "mid", null, Cell("quote")),
            CellFeeds.Trades(hub, Symbol, 10, null, true, Cell("trades")),
            CellFeeds.Last(hub, Symbol, "price", null, Cell("last")),
            CellFeeds.Overview(hub, "pairs", true, Cell("overview")),
            CellFeeds.Overview(hub, "impact", true, Cell("impact table")),
            CellFeeds.Pair(hub, Symbol, "mid", Cell("pair")),
            CellFeeds.Impact(hub, Symbol, 100000, "buyFilled", Cell("impact")),
            CellFeeds.Symbols(hub, null, true, Cell("symbols")),
            CellFeeds.Status(hub, Cell("status")),
        };
        bool Ready()
        {
            lock (gate)
            {
                return cells.Count == feeds.Length
                    && (string?)cells["status"] == "connected"
                    && cells.Where(c => c.Key != "status").All(c => c.Value is object[,] or double);
            }
        }
        await Wait.UntilAsync(Ready, "every cell to have a value", seconds: 30);
        lock (gate)
        {
            Assert.True((double)cells["quote"]! > 0);
            Assert.True((double)cells["last"]! > 0);
            Assert.Equal("Symbol", ((object[,])cells["overview"]!)[0, 0]);
            Assert.True(((object[,])cells["trades"]!).GetLength(0) > 1);
            // The venues of the levels: the first and last columns.
            Assert.All(((object[,])cells["orderbook venues"]!).Cast<object>().OfType<string>().Where(s => s.Length > 0),
                venue => Assert.Contains(venue, new[] { "binance", "okx" }));
        }
        foreach (var feed in feeds)
        {
            feed.Dispose();
        }
    }
}
