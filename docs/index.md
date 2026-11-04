# Klondike Market Data API

Real-time crypto market data consolidated across about forty centralized venues
(Binance, OKX, Bybit, Coinbase, Kraken, Bitstamp, Hyperliquid...):

* **Trades** of every venue for a symbol, each with its venue and aggressor side.
* **Merged order books**: the books of every venue merged into one, every level
  with the venue it comes from, and the best bid and ask of each venue.
* **Market overview**: for the pairs Klondike follows permanently, what a
  market order of 100 k$, 1 M$ or 10 M$ really costs on the merged book and on
  the best single venue (taker fees included), the depth within ±10 bps, the
  crosses between venues net of fees, the basis of perpetuals, the venues
  setting the best price, stablecoin pegs, implied FX rates and triangles.

Snapshots over REST, streams over WebSocket in JSON or
[Cap'n Proto](capnp.md), with client libraries for
[Python](clients/python.md), [JavaScript](clients/javascript.md),
[Go](clients/go.md), [Rust](clients/rust.md) and [C#](clients/csharp.md), and an
[Excel add-in](clients/excel.md) for live data in a worksheet.

## Quick start

1. Sign in on the [portal](https://api.klondike-crypto.com/portal/) and create
   an API key (see [Authentication](authentication.md)).
2. Call the API with the key in the `X-API-Key` header:

```sh
export KLONDIKE_API_KEY=kl_...
curl -H "X-API-Key: $KLONDIKE_API_KEY" \
  "https://api.klondike-crypto.com/v1/orderbook?symbol=BTC/USDT&depth=5"
```

```json
{"symbol": "BTC/USDT", "ts": 1793706479000,
 "venues": [{"venue": "binance", "ts": 1793706478950, "bid": 104250.0, "bidQty": 1.2, "ask": 104250.1, "askQty": 0.8}, ...],
 "bids": [{"price": 104250.0, "qty": 1.2, "venue": "binance"}, ...],
 "asks": [{"price": 104250.1, "qty": 0.8, "venue": "binance"}, ...]}
```

3. Or use a library:

=== "Python"

    ```python
    from klondike import Client

    client = Client("kl_...")
    book = client.orderbook("BTC/USDT", depth=5)
    print(book.bids[0].venue, book.bids[0].price)
    ```

=== "JavaScript"

    ```js
    import { KlondikeClient } from "@klondike-trading/klondike";

    const client = new KlondikeClient({ apiKey: "kl_..." });
    const book = await client.orderbook("BTC/USDT", { depth: 5 });
    console.log(book.bids[0].venue, book.bids[0].price);
    ```

=== "Go"

    ```go
    c := klondike.New("kl_...")
    book, err := c.Orderbook(ctx, "BTC/USDT", &klondike.OrderbookOptions{Depth: 5})
    fmt.Println(book.Bids[0].Venue, book.Bids[0].Price)
    ```

=== "Rust"

    ```rust
    use klondike::{Client, OrderbookOptions};

    let client = Client::new("kl_...");
    let opts = OrderbookOptions { depth: Some(5), ..Default::default() };
    let book = client.orderbook("BTC/USDT", opts).await?;
    println!("{} {}", book.bids[0].venue, book.bids[0].price);
    ```

=== "C#"

    ```csharp
    using var client = new KlondikeClient("kl_...");
    var book = await client.GetOrderbookAsync("BTC/USDT", new OrderbookOptions { Depth = 5 });
    Console.WriteLine($"{book.Bids[0].Venue} {book.Bids[0].Price}");
    ```

=== "Excel"

    ```
    =KLONDIKE.ORDERBOOK("BTC/USDT",5)
    ```

## Conventions

| | |
|---|---|
| Symbols | unified [CCXT](https://docs.ccxt.com/#/README?id=contract-naming-conventions) symbols: `BTC/USDT` (spot), `BTC/USDT:USDT` (linear perpetual), `BTC/USD:BTC` (inverse perpetual) |
| Venues | CCXT exchange ids: `binance` (spot), `binanceusdm` (USDⓈ-M futures), `krakenfutures`, `kucoinfutures`... |
| Timestamps | milliseconds since the Unix epoch, UTC; the exchange time when the venue gives one |
| Prices | in the quote currency of the symbol |
| Quantities | in the base currency; in USD for inverse perpetuals, as on the venues |
| Costs | in basis points (bps) of the consolidated mid |

The data is what the venues publish, normalized, not filtered: a book can be
stale or crossed. The overview flags the data quality of each pair (`status`,
`reasons`) and tells a cross between venues from a stale book.
