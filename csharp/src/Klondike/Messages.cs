using System.Text.Json.Serialization;

namespace Klondike;

/// <summary>The channels of the WebSocket API.</summary>
public static class Channels
{
    /// <summary>The trades of a symbol.</summary>
    public const string Trades = "trades";
    /// <summary>The merged order book of a symbol.</summary>
    public const string Orderbook = "orderbook";
    /// <summary>The market overview.</summary>
    public const string Overview = "overview";
}

/// <summary>A stream of the WebSocket API; <see cref="Symbol"/> is empty for the overview.</summary>
public sealed record Subscription
{
    /// <summary><c>trades</c>, <c>orderbook</c> or <c>overview</c>, see <see cref="Channels"/>.</summary>
    public string Channel { get; init => field = value ?? ""; } = "";
    /// <summary>Unified CCXT symbol.</summary>
    public string Symbol { get; init => field = value ?? ""; } = "";
}

/// <summary>An error message of the server. It does not close the connection.</summary>
public sealed record StreamError
{
    /// <summary><c>bad_request</c>, <c>unknown_channel</c>, <c>too_many_subscriptions</c>...</summary>
    public string Code { get; init => field = value ?? ""; } = "";
    /// <summary>Human readable message.</summary>
    public string Message { get; init => field = value ?? ""; } = "";
    /// <summary>The channel of the subscription the error is about, empty otherwise.</summary>
    public string Channel { get; init => field = value ?? ""; } = "";
    /// <summary>The symbol of the subscription the error is about.</summary>
    public string Symbol { get; init => field = value ?? ""; } = "";
}

/// <summary>A server message of the stream. The messages are the same whatever the format.</summary>
public abstract record StreamMessage;

/// <summary>The trades received since the previous message (batched every 100 ms).</summary>
public sealed record TradesMessage(TradeBatch Trades) : StreamMessage;

/// <summary>The merged book, whole, when it changes and at most four times a second.</summary>
public sealed record OrderbookMessage(Orderbook Orderbook) : StreamMessage;

/// <summary>The overview, whole, after the subscription and each time it changes.</summary>
public sealed record OverviewMessage(Overview Overview) : StreamMessage;

/// <summary>A subscription was accepted.</summary>
public sealed record SubscribedMessage(Subscription Subscription) : StreamMessage;

/// <summary>An unsubscription was accepted.</summary>
public sealed record UnsubscribedMessage(Subscription Subscription) : StreamMessage;

/// <summary>An error of the server. It does not close the connection.</summary>
public sealed record ErrorMessage(StreamError Error) : StreamMessage;

/// <summary>The answer to <see cref="KlondikeStream.PingAsync"/>.</summary>
public sealed record PongMessage : StreamMessage;

internal sealed record ClientMessage
{
    public string Op { get; init => field = value ?? ""; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channel { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Venues { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Depth { get; init; }
}

internal sealed record SymbolsResponse
{
    public IReadOnlyList<SymbolInfo> Symbols { get; init => field = value ?? []; } = [];
}

internal sealed record ApiErrorBody
{
    public string Code { get; init => field = value ?? ""; } = "";
    public string Error { get; init => field = value ?? ""; } = "";
}

internal sealed record OverviewEnvelope
{
    public Overview? Overview { get; init; }
}

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SymbolsResponse))]
[JsonSerializable(typeof(TradeBatch))]
[JsonSerializable(typeof(Orderbook))]
[JsonSerializable(typeof(Overview))]
[JsonSerializable(typeof(OverviewEnvelope))]
[JsonSerializable(typeof(Subscription))]
[JsonSerializable(typeof(StreamError))]
[JsonSerializable(typeof(ClientMessage))]
[JsonSerializable(typeof(ApiErrorBody))]
internal sealed partial class KlondikeJson : JsonSerializerContext;
