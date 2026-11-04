using Microsoft.AspNetCore.Http;

namespace Klondike.Tests;

public sealed class RestTests : IAsyncLifetime
{
    private FakeServer _server = null!;
    private KlondikeClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await FakeServer.StartAsync();
        _client = new KlondikeClient(FakeServer.Key, new KlondikeClientOptions { BaseUrl = _server.Url + "/" });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task SendsTheKeyAndTheUserAgent()
    {
        _server.RouteJson("/v1/symbols", """{"symbols":[]}""");
        await _client.GetSymbolsAsync();
        var request = Assert.Single(_server.Requests);
        Assert.Equal(FakeServer.Key, request.ApiKey);
        Assert.Equal("klondike-csharp/" + KlondikeClient.Version, request.UserAgent);
        Assert.Equal("", request.Query);
    }

    [Fact]
    public async Task Symbols()
    {
        _server.RouteJson("/v1/symbols", """{"symbols":[{"symbol":"BTC/USDT","market":"spot","venues":["binance","okx"],"lastTradeTs":1793706479654}]}""");
        var symbol = Assert.Single(await _client.GetSymbolsAsync("btc/"));
        Assert.Equal("BTC/USDT", symbol.Symbol);
        Assert.Equal(["binance", "okx"], symbol.Venues);
        Assert.Equal(1793706479654, symbol.LastTradeTs);
        Assert.Equal("?q=btc%2F", _server.Requests.Single().Query);
    }

    [Fact]
    public async Task TradesOptionsGoInTheQuery()
    {
        _server.RouteJson("/v1/trades", """{"symbol":"ETH/USDT:USDT","trades":[{"ts":5,"venue":"bybit","price":2,"qty":3,"side":"sell"}]}""");
        var batch = await _client.GetTradesAsync("ETH/USDT:USDT", new TradesOptions { Limit = 5, Since = 10, Venues = ["binanceusdm", "bybit"] });
        Assert.Equal(Side.Sell, Assert.Single(batch.Trades).Side);
        Assert.Equal("?symbol=ETH%2FUSDT%3AUSDT&limit=5&since=10&venues=binanceusdm%2Cbybit", _server.Requests.Single().Query);
    }

    [Fact]
    public async Task OrderbookOptionsGoInTheQuery()
    {
        _server.RouteJson("/v1/orderbook", """{"symbol":"BTC/USDT","ts":1,"venues":[{"venue":"a","ts":null,"bid":null,"bidQty":null,"ask":2,"askQty":3}],"bids":[],"asks":[{"price":2,"qty":3,"venue":"a"}]}""");
        var book = await _client.GetOrderbookAsync("BTC/USDT", new OrderbookOptions { Depth = 5, Venues = ["a"] });
        Assert.Null(book.Venues[0].Bid);
        Assert.Null(book.Venues[0].Time);
        Assert.Equal(2, book.Asks[0].Price);
        Assert.Equal("?symbol=BTC%2FUSDT&depth=5&venues=a", _server.Requests.Single().Query);
    }

    [Fact]
    public async Task Overview()
    {
        _server.RouteJson("/v1/overview", System.Text.Encoding.UTF8.GetString(Fixtures.Json("2-overview"))
            .Replace("""{"type":"overview","overview":""", "").TrimEnd()[..^1]);
        var overview = await _client.GetOverviewAsync();
        Assert.Equal("BTC/USDT", Assert.Single(overview.Pairs).Symbol);
    }

    [Theory]
    [InlineData(400, "bad_request", false, false)]
    [InlineData(401, "unauthorized", false, true)]
    [InlineData(404, "not_found", true, false)]
    [InlineData(503, "unavailable", false, false)]
    public async Task ErrorsOfTheApi(int status, string code, bool notFound, bool unauthorized)
    {
        _server.Route("/v1/orderbook", c => FakeServer.WriteJsonAsync(c, status, $$"""{"code":"{{code}}","error":"the message"}"""));
        var e = await Assert.ThrowsAsync<KlondikeApiException>(() => _client.GetOrderbookAsync("X/Y"));
        Assert.Equal(status, e.Status);
        Assert.Equal(code, e.Code);
        Assert.Equal("the message", e.ErrorMessage);
        Assert.Equal(notFound, e.IsNotFound);
        Assert.Equal(unauthorized, e.IsUnauthorized);
        Assert.Null(e.RetryAfter);
    }

    [Fact]
    public async Task RateLimitSaysHowLongToWait()
    {
        _server.Route("/v1/symbols", c =>
        {
            c.Response.Headers.RetryAfter = "3";
            return FakeServer.WriteJsonAsync(c, 429, """{"code":"rate_limited","error":"slow down"}""");
        });
        var e = await Assert.ThrowsAsync<KlondikeApiException>(() => _client.GetSymbolsAsync());
        Assert.Equal(TimeSpan.FromSeconds(3), e.RetryAfter);
    }

    [Fact]
    public async Task AnErrorThatIsNotJsonKeepsItsBody()
    {
        _server.Route("/v1/symbols", async c =>
        {
            c.Response.StatusCode = 502;
            await c.Response.WriteAsync("<html>bad gateway</html>");
        });
        var e = await Assert.ThrowsAsync<KlondikeApiException>(() => _client.GetSymbolsAsync());
        Assert.Equal(502, e.Status);
        Assert.Equal("<html>bad gateway</html>", e.ErrorMessage);
    }

    [Fact]
    public async Task ACancelledCallIsCancelled()
    {
        _server.Route("/v1/symbols", async c => await Task.Delay(5000, c.RequestAborted));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.GetSymbolsAsync(cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task AnHttpClientGivenIsNotDisposed()
    {
        _server.RouteJson("/v1/symbols", """{"symbols":[]}""");
        using var http = new HttpClient();
        new KlondikeClient(FakeServer.Key, new KlondikeClientOptions { BaseUrl = _server.Url, HttpClient = http }).Dispose();
        using var client = new KlondikeClient(FakeServer.Key, new KlondikeClientOptions { BaseUrl = _server.Url, HttpClient = http });
        Assert.Empty(await client.GetSymbolsAsync());
    }
}
