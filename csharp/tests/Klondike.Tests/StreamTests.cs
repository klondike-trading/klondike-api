namespace Klondike.Tests;

public sealed class StreamTests : IAsyncLifetime
{
    private FakeServer _server = null!;
    private KlondikeClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await FakeServer.StartAsync();
        _client = new KlondikeClient(FakeServer.Key, new KlondikeClientOptions { BaseUrl = _server.Url });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task SendsTheKeyAndTheFormat()
    {
        await using var json = await _client.OpenStreamAsync();
        Assert.Equal("json", (await _server.NextConnectionAsync()).Format);
        await using var capnp = await _client.OpenStreamAsync(new StreamOptions { Format = StreamFormat.Capnp });
        Assert.Equal("capnp", (await _server.NextConnectionAsync()).Format);
        Assert.All(_server.Requests, r => Assert.Equal(FakeServer.Key, r.ApiKey));
        Assert.True(json.IsConnected);
    }

    [Fact]
    public async Task ClientMessagesAreThoseOfTheDocumentation()
    {
        await using var stream = await _client.OpenStreamAsync();
        var c = await _server.NextConnectionAsync();

        await stream.SubscribeTradesAsync("BTC/USDT");
        Assert.Equal("""{"op":"subscribe","channel":"trades","symbol":"BTC/USDT"}""", await c.ReceiveTextAsync());
        await stream.SubscribeTradesAsync("ETH/USDT:USDT", ["binanceusdm", "bybit"]);
        Assert.Equal("""{"op":"subscribe","channel":"trades","symbol":"ETH/USDT:USDT","venues":["binanceusdm","bybit"]}""", await c.ReceiveTextAsync());
        await stream.SubscribeOrderbookAsync("BTC/USDT", 20);
        Assert.Equal("""{"op":"subscribe","channel":"orderbook","symbol":"BTC/USDT","depth":20}""", await c.ReceiveTextAsync());
        await stream.SubscribeOrderbookAsync("BTC/USDT", 50, ["coinbase", "kraken"]);
        Assert.Equal("""{"op":"subscribe","channel":"orderbook","symbol":"BTC/USDT","venues":["coinbase","kraken"],"depth":50}""", await c.ReceiveTextAsync());
        await stream.SubscribeOverviewAsync();
        Assert.Equal("""{"op":"subscribe","channel":"overview"}""", await c.ReceiveTextAsync());
        await stream.UnsubscribeAsync(Channels.Trades, "BTC/USDT");
        Assert.Equal("""{"op":"unsubscribe","channel":"trades","symbol":"BTC/USDT"}""", await c.ReceiveTextAsync());
        await stream.UnsubscribeAsync(Channels.Overview);
        Assert.Equal("""{"op":"unsubscribe","channel":"overview"}""", await c.ReceiveTextAsync());
        await stream.PingAsync();
        Assert.Equal("""{"op":"ping"}""", await c.ReceiveTextAsync());
    }

    [Theory]
    [InlineData(StreamFormat.Json)]
    [InlineData(StreamFormat.Capnp)]
    public async Task ReadsEveryMessage(StreamFormat format)
    {
        await using var stream = await _client.OpenStreamAsync(new StreamOptions { Format = format });
        var c = await _server.NextConnectionAsync();
        foreach (var name in new[] { "0-trades", "1-orderbook", "2-overview", "3-subscribed", "4-unsubscribed", "5-error", "6-pong" })
        {
            if (format == StreamFormat.Capnp)
            {
                await c.SendBinaryAsync(Fixtures.Capnp(name));
            }
            else
            {
                await c.SendTextAsync(System.Text.Encoding.UTF8.GetString(Fixtures.Json(name)));
            }
        }
        await using var messages = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsType<TradesMessage>(await Wait.NextAsync(messages));
        Assert.IsType<OrderbookMessage>(await Wait.NextAsync(messages));
        Assert.IsType<OverviewMessage>(await Wait.NextAsync(messages));
        Assert.IsType<SubscribedMessage>(await Wait.NextAsync(messages));
        Assert.IsType<UnsubscribedMessage>(await Wait.NextAsync(messages));
        Assert.IsType<ErrorMessage>(await Wait.NextAsync(messages));
        Assert.IsType<PongMessage>(await Wait.NextAsync(messages));
    }

    [Fact]
    public async Task MessagesOfUnknownTypesAreSkipped()
    {
        await using var stream = await _client.OpenStreamAsync();
        var c = await _server.NextConnectionAsync();
        await c.SendTextAsync("""{"type":"from_the_future"}""");
        await c.SendTextAsync("""{"type":"pong"}""");
        await using var messages = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsType<PongMessage>(await Wait.NextAsync(messages));
    }

    [Fact]
    public async Task ALargeMessageIsReassembled()
    {
        await using var stream = await _client.OpenStreamAsync();
        var c = await _server.NextConnectionAsync();
        var trades = string.Join(',', Enumerable.Repeat("""{"ts":1,"venue":"binance","price":1,"qty":1,"side":"buy"}""", 20_000));
        await c.SendTextAsync($$"""{"type":"trades","symbol":"A/B","trades":[{{trades}}]}""");
        await using var messages = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.Equal(20_000, Assert.IsType<TradesMessage>(await Wait.NextAsync(messages)).Trades.Trades.Count);
    }

    [Fact]
    public async Task ARefusedKeyIsAnErrorOnOpen()
    {
        _server.RefuseWebSockets = 401;
        var e = await Assert.ThrowsAsync<KlondikeApiException>(() => _client.OpenStreamAsync());
        Assert.True(e.IsUnauthorized);
    }

    [Fact]
    public async Task TooManyConnectionsSaysHowLongToWait()
    {
        _server.RefuseWebSockets = 429;
        var e = await Assert.ThrowsAsync<KlondikeApiException>(() => _client.OpenStreamAsync());
        Assert.Equal(429, e.Status);
        Assert.Equal("rate_limited", e.Code);
        Assert.Equal(TimeSpan.FromSeconds(7), e.RetryAfter);
    }

    [Fact]
    public async Task AnUnreachableServerIsAnErrorOnOpen()
    {
        using var client = new KlondikeClient("k", new KlondikeClientOptions { BaseUrl = "http://127.0.0.1:1" });
        await Assert.ThrowsAnyAsync<Exception>(() => client.OpenStreamAsync());
    }

    [Fact]
    public async Task ReconnectsAndSubscribesAgain()
    {
        await using var stream = await _client.OpenStreamAsync();
        var changes = new List<bool>();
        stream.ConnectionChanged += changes.Add;
        var first = await _server.NextConnectionAsync();
        await stream.SubscribeTradesAsync("BTC/USDT", ["binance"]);
        await stream.SubscribeOrderbookAsync("ETH/USDT", 5);
        await stream.SubscribeOverviewAsync();
        await stream.UnsubscribeAsync(Channels.Overview);
        for (var i = 0; i < 4; i++)
        {
            await first.ReceiveAsync();
        }

        await first.CloseAsync(1001, "server restart");
        var second = await _server.NextConnectionAsync();
        var again = new[] { await second.ReceiveTextAsync(), await second.ReceiveTextAsync() };
        Assert.Contains("""{"op":"subscribe","channel":"trades","symbol":"BTC/USDT","venues":["binance"]}""", again);
        Assert.Contains("""{"op":"subscribe","channel":"orderbook","symbol":"ETH/USDT","depth":5}""", again);

        await second.SendTextAsync("""{"type":"pong"}""");
        await using var messages = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsType<PongMessage>(await Wait.NextAsync(messages));
        Assert.Equal([false, true], changes);
        Assert.True(stream.IsConnected);
    }

    [Theory]
    [InlineData("bad_request")]
    [InlineData("unknown_channel")]
    [InlineData("too_many_subscriptions")]
    public async Task ARefusedSubscriptionIsNotSentAgain(string code)
    {
        await using var stream = await _client.OpenStreamAsync();
        var first = await _server.NextConnectionAsync();
        await stream.SubscribeTradesAsync("A/B");
        await stream.SubscribeTradesAsync("C/D");
        await first.ReceiveAsync();
        await first.ReceiveAsync();
        await first.SendTextAsync($$"""{"type":"error","code":"{{code}}","message":"no","channel":"trades","symbol":"A/B"}""");
        await using var messages = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsType<ErrorMessage>(await Wait.NextAsync(messages));

        await first.CloseAsync(1001);
        var second = await _server.NextConnectionAsync();
        Assert.Equal("""{"op":"subscribe","channel":"trades","symbol":"C/D"}""", await second.ReceiveTextAsync());
    }

    [Fact]
    public async Task AnotherErrorKeepsTheSubscription()
    {
        await using var stream = await _client.OpenStreamAsync();
        var first = await _server.NextConnectionAsync();
        await stream.SubscribeTradesAsync("A/B");
        await first.ReceiveAsync();
        await first.SendTextAsync("""{"type":"error","code":"unavailable","message":"later","channel":"trades","symbol":"A/B"}""");
        await using var messages = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsType<ErrorMessage>(await Wait.NextAsync(messages));
        await first.CloseAsync(1001);
        var second = await _server.NextConnectionAsync();
        Assert.Equal("""{"op":"subscribe","channel":"trades","symbol":"A/B"}""", await second.ReceiveTextAsync());
    }

    [Fact]
    public async Task APolicyViolationEndsTheStream()
    {
        var stream = await _client.OpenStreamAsync();
        var c = await _server.NextConnectionAsync();
        await c.CloseAsync(1008, "no");
        await Wait.UntilAsync(() => stream.Error is not null);
        await foreach (var _ in stream.ReadAllAsync())
        {
            Assert.Fail("no message was sent");
        }
        Assert.False(stream.IsConnected);
        Assert.Equal(1, _server.ConnectionCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.SubscribeOverviewAsync());
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task AKeyRevokedDuringAnOutageEndsTheStream()
    {
        var stream = await _client.OpenStreamAsync();
        var c = await _server.NextConnectionAsync();
        _server.RefuseWebSockets = 401;
        await c.CloseAsync(1001);
        await Wait.UntilAsync(() => stream.Error is not null);
        Assert.True(Assert.IsType<KlondikeApiException>(stream.Error).IsUnauthorized);
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task WithoutReconnectionTheStreamEnds()
    {
        var stream = await _client.OpenStreamAsync(new StreamOptions { Reconnect = false });
        var c = await _server.NextConnectionAsync();
        await c.CloseAsync(1001);
        await Wait.UntilAsync(() => stream.Error is not null);
        Assert.Equal(1, _server.ConnectionCount);
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task ASilentDeadConnectionIsReplaced()
    {
        await using var stream = await _client.OpenStreamAsync(new StreamOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(100),
            PingTimeout = TimeSpan.FromMilliseconds(100),
        });
        var first = await _server.NextConnectionAsync();
        Assert.Equal("""{"op":"ping"}""", await first.ReceiveTextAsync());
        await _server.NextConnectionAsync(); // the server never answered
        Assert.Equal(2, _server.ConnectionCount);
    }

    [Fact]
    public async Task KeepAlivePongsAreNotGivenToTheReader()
    {
        await using var stream = await _client.OpenStreamAsync(new StreamOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(50),
            PingTimeout = TimeSpan.FromSeconds(5),
        });
        var c = await _server.NextConnectionAsync();
        c.AnswerPings = true;
        await c.ReceiveAsync();
        await c.ReceiveAsync();
        await c.SendTextAsync("""{"type":"subscribed","channel":"overview"}""");
        await using var messages = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsType<SubscribedMessage>(await Wait.NextAsync(messages));
        Assert.Equal(1, _server.ConnectionCount);
    }

    [Fact]
    public async Task DisposeEndsTheStream()
    {
        var stream = await _client.OpenStreamAsync();
        await _server.NextConnectionAsync();
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in stream.ReadAllAsync())
            {
            }
        });
        await stream.DisposeAsync();
        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(stream.Error);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.SubscribeOverviewAsync());
        await stream.DisposeAsync(); // twice is fine
    }

    [Fact]
    public async Task DisposeWhileReconnecting()
    {
        var stream = await _client.OpenStreamAsync();
        var c = await _server.NextConnectionAsync();
        _server.RefuseWebSockets = 503;
        await c.CloseAsync(1001);
        await Wait.UntilAsync(() => !stream.IsConnected);
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(stream.Error);
    }

    [Fact]
    public async Task AMalformedFrameReconnects()
    {
        await using var stream = await _client.OpenStreamAsync();
        var c = await _server.NextConnectionAsync();
        await c.SendBinaryAsync([0xFF, 1]);
        await _server.NextConnectionAsync();
        Assert.Null(stream.Error);
    }
}
