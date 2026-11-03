# klondike

Rust client of the [Klondike market data API](https://klondike-trading.github.io/klondike-api/):
trades and merged order books of the crypto pairs Klondike streams from about
forty centralized venues, and the market overview (what an order really costs
on the merged book, dislocations between venues, implied prices), over REST
and WebSocket.

* async, on [Tokio](https://tokio.rs); `reqwest` and `tokio-tungstenite` with rustls (no OpenSSL);
* WebSocket streams in JSON or Cap'n Proto, decoded to the same types;
* reconnection with exponential backoff, subscriptions sent again;
* the Cap'n Proto code is generated in advance: no `capnp` compiler needed to build.

## Install

```sh
cargo add klondike
cargo add tokio --features rt-multi-thread,macros
```

## API key

Sign in on the [portal](https://api.klondike-crypto.com/portal/) and create a
key (`kl_` followed by 32 characters). It is shown once: keep it in an
environment variable or a secret store, not in your code.

```sh
export KLONDIKE_API_KEY=kl_...
```

## REST

```rust
use klondike::{Client, OrderbookOptions, TradesOptions};

#[tokio::main]
async fn main() -> klondike::Result<()> {
    let client = Client::new(std::env::var("KLONDIKE_API_KEY").unwrap());

    // Symbols with recent trades, filtered on a substring.
    for s in client.symbols("btc").await? {
        println!("{} {} {:?}", s.symbol, s.market, s.venues);
    }

    // The last trades of every venue, oldest first.
    let trades = client
        .trades("BTC/USDT", TradesOptions { limit: Some(10), ..Default::default() })
        .await?;
    for t in &trades.trades {
        println!("{} {} {:?} {} @ {}", t.ts, t.venue, t.side, t.qty, t.price);
    }

    // The merged order book: best quote of each venue, levels with their venue.
    let book = client
        .orderbook(
            "BTC/USDT",
            OrderbookOptions { depth: Some(10), venues: vec!["binance".into(), "okx".into()] },
        )
        .await?;
    println!("best bid {:?}, best ask {:?}", book.bids.first(), book.asks.first());

    // The market overview: cost of 100 k$, 1 M$, 10 M$ orders, crosses, basis...
    let overview = client.overview().await?;
    for p in &overview.pairs {
        println!("{} mid {} depth ±10 bps {:.0} $", p.symbol, p.mid, p.depth10_usd);
    }
    Ok(())
}
```

Errors of the API are `Error::Api { status, code, message, retry_after }`
(`code` is `bad_request`, `unauthorized`, `not_found`, `rate_limited`...):

```rust
match client.orderbook("FOO/BAR", Default::default()).await {
    Err(e) if e.is_not_found() => println!("no venue streams FOO/BAR"),
    Err(e) if e.is_rate_limited() => tokio::time::sleep(e.retry_after().unwrap_or_default()).await,
    Err(e) => return Err(e),
    Ok(book) => println!("{book:?}"),
}
```

`Client::builder()` points the client to another server or takes your own
`reqwest::Client` (proxy, timeouts):

```rust
let client = klondike::Client::builder()
    .api_key(key)
    .base_url("http://localhost:8093")
    .http_client(klondike::reqwest::Client::builder().timeout(Duration::from_secs(5)).build()?)
    .build()?;
```

## Streaming

One WebSocket carries any number of subscriptions (50 at most): the trades of
a symbol, its merged order book, the overview.

```rust
use klondike::{Client, Format, Message, StreamOptions};

#[tokio::main]
async fn main() -> klondike::Result<()> {
    let client = Client::new(std::env::var("KLONDIKE_API_KEY").unwrap());
    let mut stream = client.stream(StreamOptions::default()).await?; // JSON frames

    stream.subscribe_trades("BTC/USDT", &[]).await?;
    stream.subscribe_trades("ETH/USDT:USDT", &["binanceusdm", "bybit"]).await?;
    stream.subscribe_orderbook("BTC/USDT", 20, &[]).await?; // 20 levels per side
    stream.subscribe_overview().await?;

    while let Some(msg) = stream.next().await {
        match msg? {
            Message::Trades(t) => println!("{}: {} trades", t.symbol, t.trades.len()),
            Message::Orderbook(b) => println!("{} {:?} / {:?}", b.symbol, b.bids.first(), b.asks.first()),
            Message::Overview(o) => println!("overview of {} pairs", o.pairs.len()),
            Message::Subscribed(s) => println!("subscribed to {} {}", s.channel, s.symbol),
            Message::Error(e) => eprintln!("refused: {e}"), // the connection stays open
            Message::Unsubscribed(_) | Message::Pong => {}
        }
    }
    Ok(())
}
```

### Formats

```rust
let stream = client
    .stream(StreamOptions { format: Format::Capnp, ..Default::default() })
    .await?;
```

`Format::Json` (default) receives text frames, `Format::Capnp` packed Cap'n
Proto binary frames, about three times smaller on order books and decoded
without parsing text. The messages are the same `Message` values whatever the
format; subscriptions are always sent as JSON.

### Reconnection

`client.stream(...)` opens the first connection before returning: a refused
key is an error there (`e.is_unauthorized()`), as are too many connections
for the key (429, five at most) or an unreachable server.

A lost connection (network, server restart, slow consumer) is reopened in the
background, waiting 0.5 s and then twice as long after each failure, up to
`StreamOptions::max_backoff` (30 s), and the subscriptions are sent again;
the messages sent while disconnected are lost (the order book and the
overview are sent whole again on subscription). Subscriptions the server
refused are not sent again. The stream ends (`next()` returns the error, then
`None`) when the key is refused while reconnecting, or on the first loss with
`reconnect: false`. Messages of a type this version does not know are skipped.

`stream.close().await` closes the connection; `stream.handle()` gives a
cloneable `StreamHandle` to subscribe from other tasks while one reads.
`Stream` also implements `futures::Stream`.

## Example

```sh
KLONDIKE_API_KEY=kl_... cargo run --example stream_trades -- BTC/USDT capnp
```

## Development

```sh
cargo test                                    # fixtures, mock REST and WebSocket servers
cargo clippy --all-targets -- -D warnings
cargo fmt --check
KLONDIKE_TEST_URL=http://localhost:8093 KLONDIKE_TEST_KEY=kl_... cargo test --test e2e
```

The fixture tests decode each server message of `../testdata/messages` from
both its JSON and its Cap'n Proto encoding and check they are equal.
`src/klondike_api_capnp.rs` is generated from `../schema/klondike_api.capnp`
by `./gen.sh` (needs `capnp` and `cargo install capnpc --version 0.27.0`); a
test fails when the schema changed and the code was not generated again.

## License

MIT
