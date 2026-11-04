# Klondike API — C# client

```sh
dotnet add package Klondike
```

.NET 8 or later, no dependencies. Create an API key on the [portal](https://api.klondike-crypto.com/portal/).
To read the data straight into a worksheet, see the [Excel add-in](https://klondike-trading.github.io/klondike-api/clients/excel/).

## REST

```csharp
using Klondike;

using var client = new KlondikeClient(Environment.GetEnvironmentVariable("KLONDIKE_API_KEY")!);

// Merged order book: best bid/ask of every venue, 10 levels per side.
var book = await client.GetOrderbookAsync("BTC/USDT", new OrderbookOptions { Depth = 10 });
foreach (var level in book.Bids)
{
    Console.WriteLine($"{level.Venue} {level.Price} {level.Qty}");
}

// The last 100 trades of every venue, or of some of them.
var trades = await client.GetTradesAsync("ETH/USDT:USDT", new TradesOptions { Limit = 100, Venues = ["binanceusdm", "bybit"] });

// Market overview: cost of 100 k$ / 1 M$ / 10 M$ orders, depth, crosses, implied prices.
var overview = await client.GetOverviewAsync();
foreach (var pair in overview.Pairs)
{
    Console.WriteLine($"{pair.Symbol} {pair.Mid} {pair.Depth10Usd}");
}

// Symbols with recent trades.
var symbols = await client.GetSymbolsAsync("usdc");
```

Errors of the API are `KlondikeApiException` (`Status`, `Code`, `ErrorMessage`, `RetryAfter` on a 429);
`IsNotFound` tells a symbol nobody streams, `IsUnauthorized` a refused key. A missing number is `null`.
All strings and lists are non-null, even when the server leaves them out.

## WebSocket

```csharp
await using var stream = await client.OpenStreamAsync(new StreamOptions { Format = StreamFormat.Capnp }); // or Json

await stream.SubscribeTradesAsync("BTC/USDT");
await stream.SubscribeOrderbookAsync("BTC/USDT", 20, ["binance", "coinbase", "kraken"]);
await stream.SubscribeOverviewAsync();

await foreach (var message in stream.ReadAllAsync())
{
    switch (message)
    {
        case TradesMessage m:
            Console.WriteLine($"{m.Trades.Trades.Count} trades");
            break;
        case OrderbookMessage m:
            Console.WriteLine($"{m.Orderbook.Venues.Count} venues, {m.Orderbook.Bids.Count} bids");
            break;
        case OverviewMessage m:
            Console.WriteLine($"{m.Overview.Pairs.Count} pairs");
            break;
        case ErrorMessage m:
            Console.Error.WriteLine(m.Error.Message);
            break;
    }
}
Console.Error.WriteLine(stream.Error);
```

`OpenStreamAsync` throws a `KlondikeApiException` when the key is refused. The messages are the same
whatever the format. A lost connection is reopened with a backoff (0.5 s to 30 s) and the subscriptions
are sent again; `ReadAllAsync` only ends on `DisposeAsync` or a refused key, then `Error` says why.
`IsConnected` and `ConnectionChanged` follow the connection. Read without blocking: a client too slow
for the server is disconnected. See
[`examples/StreamTrades`](https://github.com/klondike-trading/klondike-api/blob/main/csharp/examples/StreamTrades/Program.cs).

## Development

The .NET 10 SDK builds everything, on any OS; the tests run on .NET 8 and 10.

```sh
dotnet test                         # decodes ../testdata/messages, against a local server
KLONDIKE_TEST_URL=http://localhost:8093 KLONDIKE_TEST_KEY=kl_dev0dev0dev0dev0dev0dev0dev0dev0 \
  dotnet test --filter E2E          # against api-go/dev.sh
```

`src/Klondike` is the library. Cap'n Proto has no maintained .NET runtime reading the packed encoding:
`CapnpReader.cs` reads what the API needs, and `MessageDecoder.cs` holds the offsets of
`../schema/klondike_api.capnp` by hand. After a change of the schema, update them and regenerate the fixtures.
`src/Klondike.Excel.Core` and `src/Klondike.Excel` are the Excel add-in.
