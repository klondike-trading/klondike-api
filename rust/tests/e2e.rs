//! End-to-end tests against a running API, skipped unless KLONDIKE_TEST_URL
//! is set:
//!
//! ```sh
//! KLONDIKE_TEST_URL=http://localhost:8393 \
//! KLONDIKE_TEST_KEY=kl_dev0dev0dev0dev0dev0dev0dev0dev0 cargo test --test e2e
//! ```

use std::time::Duration;

use klondike::{Client, Format, Message, OrderbookOptions, StreamOptions, TradesOptions};
use tokio::time::{timeout, Instant};

fn client() -> Option<Client> {
    let Ok(url) = std::env::var("KLONDIKE_TEST_URL") else {
        eprintln!("skipped: KLONDIKE_TEST_URL not set");
        return None;
    };
    let key = std::env::var("KLONDIKE_TEST_KEY").expect("KLONDIKE_TEST_KEY");
    Some(
        Client::builder()
            .api_key(key)
            .base_url(url)
            .build()
            .unwrap(),
    )
}

#[tokio::test]
async fn rest() {
    let Some(c) = client() else { return };

    let symbols = c.symbols("").await.unwrap();
    assert!(
        symbols.iter().any(|s| s.symbol == "BTC/USDT"),
        "{symbols:?}"
    );
    let usdc = c.symbols("/usdc").await.unwrap();
    assert!(
        !usdc.is_empty() && usdc.iter().all(|s| s.symbol.contains("/USDC")),
        "{usdc:?}"
    );

    let trades = c
        .trades(
            "BTC/USDT",
            TradesOptions {
                limit: Some(5),
                ..Default::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(trades.symbol, "BTC/USDT");
    assert!(!trades.trades.is_empty() && trades.trades.len() <= 5);
    assert!(
        trades.trades.windows(2).all(|w| w[0].ts <= w[1].ts),
        "oldest first"
    );
    let since = trades.trades.last().unwrap().ts;
    let newer = c
        .trades(
            "BTC/USDT",
            TradesOptions {
                since: Some(since),
                ..Default::default()
            },
        )
        .await
        .unwrap();
    assert!(newer.trades.iter().all(|t| t.ts > since));

    let book = c
        .orderbook(
            "BTC/USDT",
            OrderbookOptions {
                depth: Some(5),
                ..Default::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(book.symbol, "BTC/USDT");
    assert!(!book.bids.is_empty() && book.bids.len() <= 5);
    assert!(book.bids.windows(2).all(|w| w[0].price >= w[1].price));
    assert!(book.asks.windows(2).all(|w| w[0].price <= w[1].price));
    assert!(!book.venues.is_empty());

    let venue = book.bids[0].venue.clone();
    let filtered = c
        .orderbook(
            "BTC/USDT",
            OrderbookOptions {
                depth: Some(5),
                venues: vec![venue.clone()],
            },
        )
        .await
        .unwrap();
    assert!(filtered
        .bids
        .iter()
        .chain(&filtered.asks)
        .all(|l| l.venue == venue));

    let ov = c.overview().await.unwrap();
    assert!(ov.ts > 0 && !ov.pairs.is_empty(), "{ov:?}");

    let e = c
        .orderbook("NOPE/NOPE", Default::default())
        .await
        .unwrap_err();
    assert!(e.is_not_found(), "{e:?}");
    let e = c.trades("", Default::default()).await.unwrap_err();
    assert_eq!(e.status(), Some(400), "{e:?}");

    let bad = Client::builder()
        .api_key("kl_badbadbadbadbadbadbadbadbadbadba")
        .base_url(c.base_url())
        .build()
        .unwrap();
    assert!(bad.symbols("").await.unwrap_err().is_unauthorized());
    let e = bad.stream(StreamOptions::default()).await.unwrap_err();
    assert!(e.is_unauthorized(), "{e:?}");
}

async fn stream(format: Format) {
    let Some(c) = client() else { return };
    let mut s = c
        .stream(StreamOptions {
            format,
            ..Default::default()
        })
        .await
        .unwrap();
    s.subscribe_trades("BTC/USDT", &[]).await.unwrap();
    s.subscribe_orderbook("ETH/USDT", 10, &[]).await.unwrap();
    s.subscribe_overview().await.unwrap();
    s.subscribe_trades("", &[]).await.unwrap(); // an error, not fatal
    s.ping().await.unwrap();

    let (mut acks, mut trades, mut books, mut overview, mut errors, mut pong) = (0, 0, 0, 0, 0, 0);
    let deadline = Instant::now() + Duration::from_secs(20);
    while acks < 3 || trades == 0 || books == 0 || overview == 0 || errors == 0 || pong == 0 {
        let left = deadline.saturating_duration_since(Instant::now());
        let msg = timeout(left, s.next())
            .await
            .unwrap_or_else(|_| {
                panic!("{format:?}: acks {acks} trades {trades} books {books} overview {overview} errors {errors} pong {pong}")
            })
            .expect("stream ended")
            .unwrap();
        match msg {
            Message::Subscribed(_) => acks += 1,
            Message::Trades(t) => {
                assert_eq!(t.symbol, "BTC/USDT");
                assert!(!t.trades.is_empty());
                trades += 1;
            }
            Message::Orderbook(b) => {
                assert_eq!(b.symbol, "ETH/USDT");
                assert!(b.bids.len() <= 10 && b.asks.len() <= 10);
                let names: Vec<&str> = b.venues.iter().map(|v| v.venue.as_str()).collect();
                assert!(b.bids.iter().all(|l| names.contains(&l.venue.as_str())));
                books += 1;
            }
            Message::Overview(o) => {
                assert!(!o.pairs.is_empty());
                overview += 1;
            }
            Message::Error(e) => {
                assert_eq!(e.code, "bad_request", "{e:?}");
                errors += 1;
            }
            Message::Pong => pong += 1,
            Message::Unsubscribed(u) => panic!("{u:?}"),
        }
    }
    s.unsubscribe(klondike::Channel::Trades, "BTC/USDT")
        .await
        .unwrap();
    loop {
        match timeout(Duration::from_secs(5), s.next())
            .await
            .unwrap()
            .unwrap()
            .unwrap()
        {
            Message::Unsubscribed(u) => {
                assert_eq!(u.symbol, "BTC/USDT");
                break;
            }
            _ => continue,
        }
    }
    s.close().await;
    while let Some(m) = s.next().await {
        m.unwrap();
    }
}

#[tokio::test]
async fn stream_json() {
    stream(Format::Json).await;
}

#[tokio::test]
async fn stream_capnp() {
    stream(Format::Capnp).await;
}
