using System.Collections.Concurrent;
using System.Text.Json;
using Klondike.Excel;
using Microsoft.AspNetCore.Http;

namespace Klondike.Tests;

public sealed class HubTests : IAsyncLifetime
{
    private const string Book = """{"type":"orderbook","symbol":"BTC/USDT","ts":1793706479000,"venues":[{"venue":"binance","ts":1793706478950,"bid":100,"bidQty":1,"ask":101,"askQty":2},{"venue":"kraken","ts":null,"bid":99,"bidQty":3,"ask":null,"askQty":null}],"bids":[{"price":100,"qty":1,"venue":"binance"},{"price":99,"qty":3,"venue":"kraken"}],"asks":[{"price":101,"qty":2,"venue":"binance"}]}""";

    private FakeServer _server = null!;
    private MarketHub _hub = null!;

    public async Task InitializeAsync()
    {
        _server = await FakeServer.StartAsync();
        _server.RouteJson("/v1/trades", """{"symbol":"BTC/USDT","trades":[]}""");
        _hub = NewHub();
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _server.DisposeAsync();
    }

    private MarketHub NewHub(string? key = FakeServer.Key, int maxConnections = 4, int maxSubscriptions = 50) => new(new MarketHubOptions
    {
        ApiKey = key,
        BaseUrl = _server.Url,
        MaxConnections = maxConnections,
        MaxSubscriptionsPerConnection = maxSubscriptions,
        RestInterval = TimeSpan.FromMilliseconds(1),
        MaxBackoff = TimeSpan.FromMilliseconds(100),
    });

    private sealed class Cell
    {
        private readonly ConcurrentQueue<object> _values = new();

        public void Push(object value) => _values.Enqueue(value);

        public object? Latest => _values.LastOrDefault();

        public int Count => _values.Count;

        public object[,] Table => Assert.IsType<object[,]>(Latest);

        public Task HasAsync(Func<object?, bool> condition) => Wait.UntilAsync(() => condition(Latest), "a value of the cell");

        public Task HasTableAsync() => HasAsync(v => v is object[,]);
    }

    private static string[] Row(object[,] table, int row) =>
        Enumerable.Range(0, table.GetLength(1)).Select(c => Convert.ToString(table[row, c], System.Globalization.CultureInfo.InvariantCulture)!).ToArray();

    [Fact]
    public async Task OrderbookTable()
    {
        var cell = new Cell();
        using var feed = CellFeeds.Orderbook(_hub, "BTC/USDT", 2, null, true, cell.Push);
        Assert.Equal(CellError.GettingData, cell.Latest);

        var c = await _server.NextConnectionAsync();
        Assert.Equal("""{"op":"subscribe","channel":"orderbook","symbol":"BTC/USDT","depth":2}""", await c.ReceiveTextAsync());
        await c.SendTextAsync(Book);
        await cell.HasTableAsync();

        Assert.Equal(["Bid venue", "Bid qty", "Bid", "Ask", "Ask qty", "Ask venue"], Row(cell.Table, 0));
        Assert.Equal(["binance", "1", "100", "101", "2", "binance"], Row(cell.Table, 1));
        Assert.Equal(["kraken", "3", "99", "", "", ""], Row(cell.Table, 2));
        Assert.IsType<double>(cell.Table[1, 2]);
        c.Release();
    }

    [Fact]
    public async Task CellsShareOneSubscriptionWithTheLargestDepth()
    {
        var small = new Cell();
        using var smallFeed = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, false, small.Push);
        var c = await _server.NextConnectionAsync();
        Assert.Contains("\"depth\":1", await c.ReceiveTextAsync());
        await c.SendTextAsync(Book);
        await small.HasTableAsync();
        Assert.Equal(1, small.Table.GetLength(0));

        // A cell asking for more: subscribing again replaces the depth, and what is known comes at once.
        var large = new Cell();
        using var largeFeed = CellFeeds.Orderbook(_hub, "BTC/USDT", 2, null, false, large.Push);
        await large.HasTableAsync();
        Assert.Contains("\"depth\":2", await c.ReceiveTextAsync());

        // A third asking for less changes nothing on the wire.
        var quote = new Cell();
        using var quoteFeed = CellFeeds.Quote(_hub, "BTC/USDT", "bid", null, quote.Push);
        await quote.HasAsync(v => v is double);
        Assert.Equal(100d, quote.Latest);

        await c.SendTextAsync(Book.Replace("\"ts\":1793706479000", "\"ts\":1793706480000"));
        await Wait.UntilAsync(() => large.Count >= 3);
        Assert.Equal(2, large.Table.GetLength(0));
        Assert.Equal(1, small.Table.GetLength(0));
        Assert.Equal(1, _server.ConnectionCount);
        c.Release();
    }

    [Fact]
    public async Task TheSameSymbolWithOtherVenuesGoesOnAnotherConnection()
    {
        // Subscribing again on a connection would replace the venues of the first cell.
        var all = new Cell();
        var some = new Cell();
        using var allFeed = CellFeeds.Orderbook(_hub, "BTC/USDT", 5, null, true, all.Push);
        using var someFeed = CellFeeds.Orderbook(_hub, "BTC/USDT", 5, "kraken,BINANCE", true, some.Push);

        var first = await _server.NextConnectionAsync();
        var second = await _server.NextConnectionAsync();
        var messages = new[] { await first.ReceiveTextAsync(), await second.ReceiveTextAsync() };
        Assert.Contains(messages, m => !m.Contains("venues"));
        Assert.Contains(messages, m => m.Contains("""["binance","kraken"]"""));
        first.Release();
        second.Release();
    }

    [Fact]
    public async Task SameVenuesInAnotherOrderAreTheSameStream()
    {
        var a = new Cell();
        var b = new Cell();
        using var feedA = CellFeeds.Orderbook(_hub, "BTC/USDT", 5, "okx,binance", true, a.Push);
        using var feedB = CellFeeds.Orderbook(_hub, "BTC/USDT", 5, "binance, OKX", true, b.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync(Book);
        await a.HasTableAsync();
        await b.HasTableAsync();
        Assert.Equal(1, _server.ConnectionCount);
        c.Release();
    }

    [Fact]
    public async Task SubscriptionsSpreadOverConnectionsAtTheLimit()
    {
        await using var hub = NewHub(maxConnections: 2, maxSubscriptions: 2);
        var feeds = new List<IDisposable>();
        var cells = new Dictionary<string, Cell>();
        foreach (var symbol in new[] { "A/U", "B/U", "C/U", "D/U", "E/U" })
        {
            cells[symbol] = new Cell();
            feeds.Add(CellFeeds.Orderbook(hub, symbol, 1, null, true, cells[symbol].Push));
        }
        var first = await _server.NextConnectionAsync();
        var second = await _server.NextConnectionAsync();
        var onFirst = new[] { await first.ReceiveTextAsync(), await first.ReceiveTextAsync() };
        var onSecond = new[] { await second.ReceiveTextAsync(), await second.ReceiveTextAsync() };
        Assert.Equal(4, onFirst.Concat(onSecond).Distinct().Count());
        // The two connections open together: the server may accept either first.
        var owner = onFirst.Any(m => m.Contains("\"A/U\"")) ? first : second;

        // The fifth has no room: it says so.
        await cells["E/U"].HasAsync(v => v is string s && s.Contains("too many subscriptions"));
        Assert.Equal(2, _server.ConnectionCount);

        // It takes the place of one that goes.
        feeds[0].Dispose();
        var unsubscribed = await owner.ReceiveAsync();
        Assert.Equal("unsubscribe", unsubscribed.GetProperty("op").GetString());
        var next = await owner.ReceiveAsync();
        Assert.Equal("E/U", next.GetProperty("symbol").GetString());
        await owner.SendTextAsync(Book.Replace("BTC/USDT", "E/U"));
        await cells["E/U"].HasTableAsync();

        feeds.ForEach(f => f.Dispose());
        first.Release();
        second.Release();
    }

    [Fact]
    public async Task TheLastCellUnsubscribes()
    {
        var a = new Cell();
        var b = new Cell();
        var feedA = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, true, a.Push);
        var feedB = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, true, b.Push);
        var c = await _server.NextConnectionAsync();
        Assert.Equal("subscribe", (await c.ReceiveAsync()).GetProperty("op").GetString());
        feedA.Dispose();
        feedA.Dispose();
        feedB.Dispose();
        Assert.Equal("""{"op":"unsubscribe","channel":"orderbook","symbol":"BTC/USDT"}""", await c.ReceiveTextAsync());
        c.Release();
    }

    [Fact]
    public async Task AnIdleConnectionIsClosed()
    {
        await using var hub = new MarketHub(new MarketHubOptions { ApiKey = FakeServer.Key, BaseUrl = _server.Url, IdleClose = TimeSpan.FromMilliseconds(50) });
        var status = new Cell();
        using var statusFeed = CellFeeds.Status(hub, status.Push);
        var cell = new Cell();
        var feed = CellFeeds.Orderbook(hub, "BTC/USDT", 1, null, true, cell.Push);
        var c = await _server.NextConnectionAsync();
        await status.HasAsync(v => (string?)v == "connected");
        feed.Dispose();
        await status.HasAsync(v => (string?)v == "idle");
        c.Release();
    }

    [Fact]
    public async Task VenuesTable()
    {
        var cell = new Cell();
        using var feed = CellFeeds.Venues(_hub, "BTC/USDT", true, cell.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync(Book);
        await cell.HasTableAsync();
        Assert.Equal("kraken", cell.Table[2, 0]);
        Assert.Equal(99d, cell.Table[2, 1]);
        Assert.Equal("", cell.Table[2, 3]);
        Assert.Equal(Cells.Date(1793706478950), cell.Table[1, 5]);
        Assert.Equal("", cell.Table[2, 5]);
        c.Release();
    }

    [Theory]
    [InlineData("bid", 100d)]
    [InlineData("BID", 100d)]
    [InlineData("ask", 101d)]
    [InlineData("mid", 100.5d)]
    [InlineData("spread", 1d)]
    [InlineData("bidQty", 1d)]
    [InlineData("askVenue", "binance")]
    public async Task QuoteOfTheMergedBook(string field, object expected)
    {
        var cell = new Cell();
        using var feed = CellFeeds.Quote(_hub, "BTC/USDT", field, null, cell.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync(Book);
        await cell.HasAsync(v => v is not CellError);
        Assert.Equal(expected, cell.Latest);
        c.Release();
    }

    [Fact]
    public async Task QuoteOfAVenue()
    {
        var bid = new Cell();
        var ask = new Cell();
        var unknown = new Cell();
        using var bidFeed = CellFeeds.Quote(_hub, "BTC/USDT", "bid", "KRAKEN", bid.Push);
        using var askFeed = CellFeeds.Quote(_hub, "BTC/USDT", "ask", "kraken", ask.Push);
        using var unknownFeed = CellFeeds.Quote(_hub, "BTC/USDT", "bid", "nowhere", unknown.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync(Book);
        await bid.HasAsync(v => v is double);
        Assert.Equal(99d, bid.Latest);
        // No ask on this venue, no such venue: #N/A, not a number.
        await ask.HasAsync(v => v is CellError.NotAvailable);
        await unknown.HasAsync(v => v is CellError.NotAvailable);
        c.Release();
    }

    [Fact]
    public async Task AScalarIsOnlyPushedWhenItChanges()
    {
        var cell = new Cell();
        using var feed = CellFeeds.Quote(_hub, "BTC/USDT", "bid", null, cell.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync(Book);
        await c.SendTextAsync(Book);
        await c.SendTextAsync(Book.Replace("\"bids\":[{\"price\":100", "\"bids\":[{\"price\":200"));
        await cell.HasAsync(v => v is 200d);
        Assert.Equal(3, cell.Count); // getting data, 100, 200
        c.Release();
    }

    [Fact]
    public void UnknownFieldsAndSectionsListWhatExists()
    {
        var e = Assert.Throws<ArgumentException>(() => CellFeeds.Quote(_hub, "A/B", "nope", null, _ => { }));
        Assert.Contains("spreadBps", e.Message);
        e = Assert.Throws<ArgumentException>(() => CellFeeds.Overview(_hub, "nope", true, _ => { }));
        Assert.Contains("triangles", e.Message);
        Assert.Throws<ArgumentException>(() => CellFeeds.Pair(_hub, "A/B", "nope", _ => { }));
        Assert.Throws<ArgumentException>(() => CellFeeds.Impact(_hub, "A/B", 1, "nope", _ => { }));
        Assert.Throws<ArgumentException>(() => CellFeeds.Last(_hub, "A/B", "nope", null, _ => { }));
    }

    private static string TradeJson(long ts, double price) => $$"""{"ts":{{ts}},"venue":"binance","price":{{price}},"qty":1,"side":"buy"}""";

    [Fact]
    public async Task TradesStartFromTheRestApiAndFollowTheStream()
    {
        _server.RouteJson("/v1/trades", $$"""{"symbol":"BTC/USDT","trades":[{{TradeJson(1, 10)}},{{TradeJson(2, 20)}}]}""");
        var cell = new Cell();
        using var feed = CellFeeds.Trades(_hub, "BTC/USDT", 3, "okx", true, cell.Push);
        var c = await _server.NextConnectionAsync();
        Assert.Equal("""{"op":"subscribe","channel":"trades","symbol":"BTC/USDT","venues":["okx"]}""", await c.ReceiveTextAsync());
        await cell.HasTableAsync();
        Assert.Contains(_server.Requests, r => r.Path == "/v1/trades" && r.Query == "?symbol=BTC%2FUSDT&limit=3&venues=okx");

        // Newest first.
        Assert.Equal(["Time (UTC)", "Venue", "Side", "Price", "Qty"], Row(cell.Table, 0));
        Assert.Equal(20d, cell.Table[1, 3]);
        Assert.Equal(10d, cell.Table[2, 3]);
        Assert.Equal("buy", cell.Table[1, 2]);

        // The window keeps the limit.
        await c.SendTextAsync($$"""{"type":"trades","symbol":"BTC/USDT","trades":[{{TradeJson(3, 30)}},{{TradeJson(4, 40)}}]}""");
        await cell.HasAsync(v => v is object[,] t && t.GetLength(0) == 4 && t[1, 3] is 40d);
        Assert.Equal(20d, cell.Table[3, 3]);
        c.Release();
    }

    [Fact]
    public async Task ASnapshotOverlappingTheStreamCountsATradeOnce()
    {
        var answer = new TaskCompletionSource();
        _server.Route("/v1/trades", async context =>
        {
            await answer.Task;
            await FakeServer.WriteJsonAsync(context, 200, $$"""{"symbol":"BTC/USDT","trades":[{{TradeJson(1, 10)}},{{TradeJson(2, 20)}}]}""");
        });
        var cell = new Cell();
        using var feed = CellFeeds.Trades(_hub, "BTC/USDT", 10, null, false, cell.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync($$"""{"type":"trades","symbol":"BTC/USDT","trades":[{{TradeJson(2, 20)}},{{TradeJson(3, 30)}}]}""");
        await cell.HasAsync(v => v is object[,] t && t.GetLength(0) == 2);
        answer.SetResult();
        await cell.HasAsync(v => v is object[,] t && t.GetLength(0) == 3);
        Assert.Equal(new object[] { 30d, 20d, 10d }, new[] { cell.Table[0, 3], cell.Table[1, 3], cell.Table[2, 3] });
        c.Release();
    }

    [Fact]
    public async Task TradesOfAnOutageComeFromTheRestApi()
    {
        var calls = 0;
        _server.Route("/v1/trades", context =>
        {
            var call = Interlocked.Increment(ref calls);
            var trades = call == 1 ? TradeJson(1, 10) : $"{TradeJson(1, 10)},{TradeJson(2, 20)}";
            return FakeServer.WriteJsonAsync(context, 200, $$"""{"symbol":"BTC/USDT","trades":[{{trades}}]}""");
        });
        var cell = new Cell();
        using var feed = CellFeeds.Trades(_hub, "BTC/USDT", 10, null, false, cell.Push);
        var c = await _server.NextConnectionAsync();
        await cell.HasTableAsync();
        await c.CloseAsync(1001);
        await _server.NextConnectionAsync();
        await cell.HasAsync(v => v is object[,] t && t.GetLength(0) == 2);
        Assert.Equal(20d, cell.Table[0, 3]);
    }

    [Fact]
    public async Task AFailedSnapshotLeavesTheStream()
    {
        _server.Route("/v1/trades", context => FakeServer.WriteJsonAsync(context, 500, """{"code":"boom","error":"boom"}"""));
        var cell = new Cell();
        using var feed = CellFeeds.Trades(_hub, "BTC/USDT", 10, null, true, cell.Push);
        var c = await _server.NextConnectionAsync();
        await cell.HasAsync(v => v is object[,] t && t.GetLength(0) == 1); // the headers
        await c.SendTextAsync($$"""{"type":"trades","symbol":"BTC/USDT","trades":[{{TradeJson(1, 10)}}]}""");
        await cell.HasAsync(v => v is object[,] t && t.GetLength(0) == 2);
        c.Release();
    }

    [Fact]
    public async Task RestCallsWaitWhenTheApiSaysSo()
    {
        var calls = 0;
        _server.Route("/v1/symbols", context =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                context.Response.Headers.RetryAfter = "1";
                return FakeServer.WriteJsonAsync(context, 429, """{"code":"rate_limited","error":"slow down"}""");
            }
            return FakeServer.WriteJsonAsync(context, 200, """{"symbols":[]}""");
        });
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(await _hub.GetSymbolsAsync(null));
        Assert.Equal(2, calls);
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(0.9), $"waited {started.Elapsed}");
    }

    [Fact]
    public async Task LastTrade()
    {
        var price = new Cell();
        var side = new Cell();
        using var priceFeed = CellFeeds.Last(_hub, "BTC/USDT", "price", null, price.Push);
        using var sideFeed = CellFeeds.Last(_hub, "BTC/USDT", "side", null, side.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync($$"""{"type":"trades","symbol":"BTC/USDT","trades":[{{TradeJson(1, 10)}},{{TradeJson(2, 20)}}]}""");
        await price.HasAsync(v => v is 20d);
        await side.HasAsync(v => (v as string) == "buy");
        c.Release();
    }

    private static string OverviewMessage => System.Text.Encoding.UTF8.GetString(Fixtures.Json("2-overview")).Trim();

    [Fact]
    public async Task OverviewSectionsAndFields()
    {
        var pairs = new Cell();
        var impact = new Cell();
        var leaders = new Cell();
        var mid = new Cell();
        var cost = new Cell();
        var missingSize = new Cell();
        var missingPair = new Cell();
        var nullField = new Cell();
        using var a = CellFeeds.Overview(_hub, "pairs", true, pairs.Push);
        using var b = CellFeeds.Overview(_hub, "IMPACT", false, impact.Push);
        using var d = CellFeeds.Overview(_hub, "leaders", true, leaders.Push);
        using var e = CellFeeds.Pair(_hub, "BTC/USDT", "mid", mid.Push);
        using var f = CellFeeds.Impact(_hub, "BTC/USDT", 100000, "buyAllInBps", cost.Push);
        using var g = CellFeeds.Impact(_hub, "BTC/USDT", 42, "buyAllInBps", missingSize.Push);
        using var h = CellFeeds.Pair(_hub, "NOPE/USDT", "mid", missingPair.Push);
        using var i = CellFeeds.Pair(_hub, "BTC/USDT", "maxNetBps1h", nullField.Push);

        var c = await _server.NextConnectionAsync();
        Assert.Equal("""{"op":"subscribe","channel":"overview"}""", await c.ReceiveTextAsync());
        await c.SendTextAsync(OverviewMessage);

        await pairs.HasTableAsync();
        Assert.Equal("Symbol", pairs.Table[0, 0]);
        Assert.Equal("BTC/USDT", pairs.Table[1, 0]);
        Assert.Equal(104250d, pairs.Table[1, 7]);
        Assert.Equal(1.5d, pairs.Table[1, 17]);
        Assert.Equal("", pairs.Table[1, 16]);

        await impact.HasTableAsync();
        Assert.Equal(1, impact.Table.GetLength(0));
        Assert.Equal(100000d, impact.Table[0, 1]);

        await leaders.HasTableAsync();
        Assert.Equal(new object[] { "binance", 1d }, new[] { leaders.Table[1, 0], leaders.Table[1, 1] });

        await mid.HasAsync(v => v is 104250d);
        await cost.HasAsync(v => v is 4.2d);
        await missingSize.HasAsync(v => v is CellError.NotAvailable);
        await missingPair.HasAsync(v => v is CellError.NotAvailable);
        await nullField.HasAsync(v => v is CellError.NotAvailable);
        Assert.Equal(1, _server.ConnectionCount);
        c.Release();
    }

    [Fact]
    public async Task SymbolsTable()
    {
        _server.RouteJson("/v1/symbols", """{"symbols":[{"symbol":"BTC/USDT","market":"spot","venues":["binance","okx"],"lastTradeTs":1793706479654}]}""");
        var cell = new Cell();
        using var feed = CellFeeds.Symbols(_hub, "btc", true, cell.Push);
        await cell.HasTableAsync();
        Assert.Equal(new object[] { "BTC/USDT", "spot", "binance,okx", Cells.Date(1793706479654) },
            new[] { cell.Table[1, 0], cell.Table[1, 1], cell.Table[1, 2], cell.Table[1, 3] });
    }

    [Fact]
    public async Task WithoutAKeyTheCellsSaySo()
    {
        await using var hub = NewHub(key: null);
        var cell = new Cell();
        var status = new Cell();
        var symbols = new Cell();
        using var feed = CellFeeds.Orderbook(hub, "BTC/USDT", 1, null, true, cell.Push);
        using var statusFeed = CellFeeds.Status(hub, status.Push);
        using var symbolsFeed = CellFeeds.Symbols(hub, null, true, symbols.Push);
        await cell.HasAsync(v => v is string s && s.StartsWith("klondike: no API key", StringComparison.Ordinal));
        await status.HasAsync(v => (string?)v == "no API key");
        await symbols.HasAsync(v => v is string s && s.StartsWith("klondike: no API key", StringComparison.Ordinal));
        Assert.Equal(0, _server.ConnectionCount);

        // A key arrives: the cells that were waiting get their data.
        hub.SetApiKey(FakeServer.Key);
        var c = await _server.NextConnectionAsync();
        await c.ReceiveAsync();
        await c.SendTextAsync(Book);
        await cell.HasTableAsync();
        await status.HasAsync(v => (string?)v == "connected");
        Assert.Equal(FakeServer.Key, _server.Requests.Last(r => r.Path == "/v1/ws").ApiKey);
        c.Release();
    }

    [Fact]
    public async Task ARefusedKeyStopsEveryCellUntilAnotherKey()
    {
        _server.RefuseWebSockets = 401;
        var cell = new Cell();
        var status = new Cell();
        using var feed = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, true, cell.Push);
        using var statusFeed = CellFeeds.Status(_hub, status.Push);
        await cell.HasAsync(v => (v as string) == "klondike: API key refused");
        await status.HasAsync(v => (string?)v == "API key refused");

        // Another cell does not try again with the same key.
        var other = new Cell();
        using var otherFeed = CellFeeds.Orderbook(_hub, "ETH/USDT", 1, null, true, other.Push);
        await other.HasAsync(v => (v as string) == "klondike: API key refused");

        _server.RefuseWebSockets = null;
        _hub.SetApiKey("kl_other");
        var c = await _server.NextConnectionAsync();
        var messages = new[] { await c.ReceiveTextAsync(), await c.ReceiveTextAsync() };
        Assert.Contains(messages, m => m.Contains("ETH/USDT"));
        await c.SendTextAsync(Book);
        await cell.HasTableAsync();
        c.Release();
    }

    [Fact]
    public async Task ReplacingTheKeyReopensTheStreams()
    {
        var cell = new Cell();
        using var feed = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, true, cell.Push);
        var first = await _server.NextConnectionAsync();
        await first.ReceiveAsync();
        _hub.SetApiKey("kl_new");
        var second = await _server.NextConnectionAsync();
        await second.ReceiveAsync();
        Assert.Equal("kl_new", _server.Requests.Last(r => r.Path == "/v1/ws").ApiKey);
        first.Release();
        second.Release();
    }

    [Fact]
    public async Task ServerUnreachableAtFirstThenBack()
    {
        _server.RefuseWebSockets = 429; // the connections of the key are in use elsewhere
        var cell = new Cell();
        var status = new Cell();
        using var feed = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, true, cell.Push);
        using var statusFeed = CellFeeds.Status(_hub, status.Push);
        await status.HasAsync(v => (string?)v == "reconnecting");
        Assert.Equal(CellError.GettingData, cell.Latest);
        _server.RefuseWebSockets = null;
        var c = await _server.NextConnectionAsync();
        await c.ReceiveAsync();
        await c.SendTextAsync(Book);
        await cell.HasTableAsync();
        await status.HasAsync(v => (string?)v == "connected");
        c.Release();
    }

    [Fact]
    public async Task ASubscriptionRefusedByTheServerSaysWhy()
    {
        var cell = new Cell();
        using var feed = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, true, cell.Push);
        var c = await _server.NextConnectionAsync();
        await c.ReceiveAsync();
        await c.SendTextAsync("""{"type":"error","code":"bad_request","message":"depth out of range","channel":"orderbook","symbol":"BTC/USDT"}""");
        await cell.HasAsync(v => (v as string) == "klondike: depth out of range");
        c.Release();
    }

    [Fact]
    public async Task AListenerThatThrowsDoesNotStopTheOthers()
    {
        var cell = new Cell();
        using var bad = _hub.WatchOrderbook("BTC/USDT", 1, null, _ => throw new InvalidOperationException("boom"));
        using var good = CellFeeds.Orderbook(_hub, "BTC/USDT", 1, null, true, cell.Push);
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync(Book);
        await cell.HasTableAsync();
        c.Release();
    }

    [Fact]
    public async Task DisposeIsSafeAtAnyTime()
    {
        var hub = NewHub();
        var cell = new Cell();
        var feed = CellFeeds.Orderbook(hub, "BTC/USDT", 1, null, true, cell.Push);
        var c = await _server.NextConnectionAsync();
        await hub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await hub.DisposeAsync();
        feed.Dispose();
        c.Release();
    }
}
