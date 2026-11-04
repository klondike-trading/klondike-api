using System.Text.Json;

namespace Klondike;

/// <summary>
/// Decodes the server messages, as JSON (<c>format=json</c>) or packed Cap'n Proto (<c>format=capnp</c>,
/// schema/klondike_api.capnp). Null for a type this version does not know: a newer server may add types.
/// </summary>
internal static class MessageDecoder
{
    public static StreamMessage? DecodeJson(ReadOnlySpan<byte> json)
    {
        var types = KlondikeJson.Default;
        return MessageType(json) switch
        {
            "trades" => new TradesMessage(JsonSerializer.Deserialize(json, types.TradeBatch)!),
            "orderbook" => new OrderbookMessage(JsonSerializer.Deserialize(json, types.Orderbook)!),
            "overview" => new OverviewMessage(JsonSerializer.Deserialize(json, types.OverviewEnvelope)?.Overview
                ?? throw new JsonException("klondike: overview message without overview")),
            "subscribed" => new SubscribedMessage(JsonSerializer.Deserialize(json, types.Subscription)!),
            "unsubscribed" => new UnsubscribedMessage(JsonSerializer.Deserialize(json, types.Subscription)!),
            "error" => new ErrorMessage(JsonSerializer.Deserialize(json, types.StreamError)!),
            "pong" => new PongMessage(),
            _ => null,
        };
    }

    private static string? MessageType(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("klondike: a server message is a JSON object");
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isType = reader.ValueTextEquals("type"u8);
            reader.Read();
            if (isType)
            {
                return reader.GetString();
            }
            reader.Skip();
        }
        return null;
    }

    // Field offsets of schema/klondike_api.capnp, as in the generated code of the other libraries.
    public static StreamMessage? DecodeCapnp(ReadOnlySpan<byte> packed)
    {
        var root = CapnpMessage.ParsePacked(packed).Root;
        switch (root.UInt16(0))
        {
            case 0:
                return new TradesMessage(DecodeTrades(root.Struct(0)));
            case 1:
                return new OrderbookMessage(DecodeOrderbook(root.Struct(0)));
            case 2:
                return new OverviewMessage(JsonSerializer.Deserialize(root.TextBytes(0), KlondikeJson.Default.Overview)
                    ?? throw new JsonException("klondike: overview message without overview"));
            case 3:
                return new SubscribedMessage(DecodeSubscription(root.Struct(0)));
            case 4:
                return new UnsubscribedMessage(DecodeSubscription(root.Struct(0)));
            case 5:
                var e = root.Struct(0);
                return new ErrorMessage(new StreamError { Code = e.Text(0), Message = e.Text(1), Channel = e.Text(2), Symbol = e.Text(3) });
            case 6:
                return new PongMessage();
            default:
                return null;
        }
    }

    private static Subscription DecodeSubscription(CapnpStruct s) => new() { Channel = s.Text(0), Symbol = s.Text(1) };

    private static TradeBatch DecodeTrades(CapnpStruct batch)
    {
        var list = batch.List(1);
        var trades = new Trade[list.Count];
        for (var i = 0; i < trades.Length; i++)
        {
            var t = list[i];
            trades[i] = new Trade
            {
                Ts = t.Int64(0),
                Venue = t.Text(0),
                Price = t.Float64(8),
                Qty = t.Float64(16),
                Side = t.UInt16(24) switch { 1 => Side.Buy, 2 => Side.Sell, _ => Side.Unknown },
            };
        }
        return new TradeBatch { Symbol = batch.Text(0), Trades = trades };
    }

    private static Orderbook DecodeOrderbook(CapnpStruct book)
    {
        var list = book.List(1);
        var venues = new VenueQuote[list.Count];
        for (var i = 0; i < venues.Length; i++)
        {
            var v = list[i];
            var ts = v.Int64(0);
            venues[i] = new VenueQuote
            {
                Venue = v.Text(0),
                Ts = ts != 0 ? ts : null,
                Bid = NullIfNaN(v.Float64(8)),
                BidQty = NullIfNaN(v.Float64(16)),
                Ask = NullIfNaN(v.Float64(24)),
                AskQty = NullIfNaN(v.Float64(32)),
            };
        }
        return new Orderbook
        {
            Symbol = book.Text(0),
            Ts = book.Int64(0),
            Venues = venues,
            Bids = DecodeLevels(book.List(2), venues),
            Asks = DecodeLevels(book.List(3), venues),
        };
    }

    private static Level[] DecodeLevels(CapnpList list, VenueQuote[] venues)
    {
        var levels = new Level[list.Count];
        for (var i = 0; i < levels.Length; i++)
        {
            var l = list[i];
            int venue = l.UInt16(16);
            if (venue >= venues.Length)
            {
                throw new FormatException($"klondike: level venue index {venue} out of {venues.Length} venues");
            }
            levels[i] = new Level { Price = l.Float64(0), Qty = l.Float64(8), Venue = venues[venue].Venue };
        }
        return levels;
    }

    private static double? NullIfNaN(double value) => double.IsNaN(value) ? null : value;
}
