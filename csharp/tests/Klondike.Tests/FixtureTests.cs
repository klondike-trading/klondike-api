using System.Text.Json;

namespace Klondike.Tests;

public class FixtureTests
{
    public static TheoryData<string> Names => new()
    {
        "0-trades", "1-orderbook", "2-overview", "3-subscribed", "4-unsubscribed", "5-error", "6-pong",
    };

    private static string Canon(StreamMessage? message) => JsonSerializer.Serialize((object?)message);

    [Theory]
    [MemberData(nameof(Names))]
    public void BothFormatsDecodeToTheSameMessage(string name)
    {
        var json = MessageDecoder.DecodeJson(Fixtures.Json(name));
        var capnp = MessageDecoder.DecodeCapnp(Fixtures.Capnp(name));
        Assert.NotNull(json);
        Assert.Equal(json!.GetType(), capnp?.GetType());
        Assert.Equal(Canon(json), Canon(capnp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trades(bool capnp)
    {
        var batch = Assert.IsType<TradesMessage>(Decode("0-trades", capnp)).Trades;
        Assert.Equal("BTC/USDT", batch.Symbol);
        Assert.Equal(3, batch.Trades.Count);
        Assert.Equal(new Trade { Ts = 1793706479654, Venue = "binance", Price = 104250.1, Qty = 0.0125, Side = Side.Buy }, batch.Trades[0]);
        Assert.Equal(Side.Sell, batch.Trades[1].Side);
        Assert.Equal(Side.Unknown, batch.Trades[2].Side);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793706479654), batch.Trades[0].Time);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Orderbook(bool capnp)
    {
        var book = Assert.IsType<OrderbookMessage>(Decode("1-orderbook", capnp)).Orderbook;
        Assert.Equal(1793706479000, book.Ts);
        Assert.Equal(new VenueQuote { Venue = "binance", Ts = 1793706478950, Bid = 104250, BidQty = 1.2, Ask = 104250.1, AskQty = 0.8 }, book.Venues[0]);
        // Missing numbers: null in JSON, NaN and 0 in Cap'n Proto.
        Assert.Equal(new VenueQuote { Venue = "kraken", Ts = 1793706478890, Bid = 104249, BidQty = 0.5 }, book.Venues[1]);
        Assert.Equal(new VenueQuote { Venue = "gemini" }, book.Venues[2]);
        Assert.Equal(new[] { new Level { Price = 104250, Qty = 1.2, Venue = "binance" }, new Level { Price = 104249, Qty = 0.5, Venue = "kraken" } }, book.Bids);
        Assert.Equal(new[] { new Level { Price = 104250.1, Qty = 0.8, Venue = "binance" } }, book.Asks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overview(bool capnp)
    {
        var overview = Assert.IsType<OverviewMessage>(Decode("2-overview", capnp)).Overview;
        var pair = Assert.Single(overview.Pairs);
        Assert.Equal(104250, pair.Mid);
        Assert.Equal(1.5, pair.BasisBps);
        Assert.Null(pair.MaxNetBps1h);
        Assert.Empty(pair.Reasons);
        var impact = Assert.Single(pair.Impact);
        Assert.Equal(100000, impact.NotionalUsd);
        Assert.Equal(4.2, impact.BuyAllInBps);
        Assert.Null(impact.SellAllInBps);
        Assert.Equal(0.62, impact.SellFilled);
        Assert.Equal("okx", impact.BestSingleSellExchange);
        Assert.Equal("binance", Assert.Single(overview.Leaders).Exchange);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcknowledgementsAndErrors(bool capnp)
    {
        Assert.Equal(new Subscription { Channel = "trades", Symbol = "BTC/USDT" }, Assert.IsType<SubscribedMessage>(Decode("3-subscribed", capnp)).Subscription);
        Assert.Equal(new Subscription { Channel = "overview" }, Assert.IsType<UnsubscribedMessage>(Decode("4-unsubscribed", capnp)).Subscription);
        Assert.Equal(new StreamError { Code = "bad_request", Message = "symbol is required", Channel = "trades" }, Assert.IsType<ErrorMessage>(Decode("5-error", capnp)).Error);
        Assert.IsType<PongMessage>(Decode("6-pong", capnp));
    }

    [Fact]
    public void UnknownJsonTypeIsSkipped()
    {
        Assert.Null(MessageDecoder.DecodeJson("""{"type":"from_the_future","x":[1,{"type":"trades"}]}"""u8));
        Assert.Null(MessageDecoder.DecodeJson("""{"x":1}"""u8));
    }

    [Fact]
    public void TypeNeedNotComeFirst()
    {
        var message = MessageDecoder.DecodeJson("""{"symbol":"A/B","extra":{"nested":[1]},"type":"trades","trades":[]}"""u8);
        Assert.Equal("A/B", Assert.IsType<TradesMessage>(message).Trades.Symbol);
    }

    [Fact]
    public void MissingFieldsAreNotNull()
    {
        var book = Assert.IsType<OrderbookMessage>(MessageDecoder.DecodeJson("""{"type":"orderbook"}"""u8)).Orderbook;
        Assert.Equal("", book.Symbol);
        Assert.Empty(book.Venues);
        Assert.Empty(book.Bids);

        var overview = Assert.IsType<OverviewMessage>(MessageDecoder.DecodeJson("""{"type":"overview","overview":{"pairs":[{"impact":[{}]}],"paths":[{}]}}"""u8)).Overview;
        Assert.Empty(overview.Pairs[0].Reasons);
        Assert.Equal("", overview.Pairs[0].Impact[0].BestSingleBuyExchange);
        Assert.Empty(overview.Paths[0].Tokens);
        Assert.Empty(overview.Implied.Pegs);
        Assert.Empty(overview.Leaders);
        Assert.Equal(0, overview.Feeds.Venues);
    }

    [Fact]
    public void NullListsOfTheServerAreEmpty()
    {
        // A nil slice of Go.
        var book = Assert.IsType<OrderbookMessage>(MessageDecoder.DecodeJson("""{"type":"orderbook","symbol":null,"venues":null,"bids":null,"asks":null}"""u8)).Orderbook;
        Assert.Equal("", book.Symbol);
        Assert.Empty(book.Venues);
        Assert.Empty(book.Bids);
        Assert.Empty(book.Asks);

        var overview = Assert.IsType<OverviewMessage>(MessageDecoder.DecodeJson("""{"type":"overview","overview":{"feeds":null,"pairs":[{"impact":null,"reasons":null}],"leaders":null,"implied":null,"paths":null}}"""u8)).Overview;
        Assert.Empty(overview.Pairs[0].Impact);
        Assert.Empty(overview.Implied.Fx);
    }

    [Fact]
    public void UnknownSideIsUnknown()
    {
        var message = MessageDecoder.DecodeJson("""{"type":"trades","symbol":"A/B","trades":[{"ts":1,"venue":"v","price":1,"qty":1,"side":"auction"}]}"""u8);
        Assert.Equal(Side.Unknown, Assert.IsType<TradesMessage>(message).Trades.Trades[0].Side);
    }

    private static StreamMessage? Decode(string name, bool capnp) =>
        capnp ? MessageDecoder.DecodeCapnp(Fixtures.Capnp(name)) : MessageDecoder.DecodeJson(Fixtures.Json(name));
}
