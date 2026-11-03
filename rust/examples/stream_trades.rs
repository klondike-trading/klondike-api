//! Prints the trades of a symbol, all venues, as they happen.
//!
//! ```sh
//! KLONDIKE_API_KEY=kl_... cargo run --example stream_trades -- BTC/USDT capnp
//! ```
//!
//! `KLONDIKE_BASE_URL` points to another server (http://localhost:8093...).

use klondike::{Client, Format, Message, StreamOptions};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = std::env::args().skip(1);
    let symbol = args.next().unwrap_or_else(|| "BTC/USDT".into());
    let format = match args.next().as_deref() {
        Some("capnp") => Format::Capnp,
        _ => Format::Json,
    };

    let key = std::env::var("KLONDIKE_API_KEY").map_err(|_| "KLONDIKE_API_KEY not set")?;
    let mut builder = Client::builder().api_key(key);
    if let Ok(url) = std::env::var("KLONDIKE_BASE_URL") {
        builder = builder.base_url(url);
    }
    let client = builder.build()?;

    let mut stream = client
        .stream(StreamOptions {
            format,
            ..Default::default()
        })
        .await?;
    stream.subscribe_trades(&symbol, &[]).await?;

    loop {
        tokio::select! {
            msg = stream.next() => match msg {
                Some(Ok(Message::Trades(t))) => {
                    for tr in &t.trades {
                        println!("{} {:>14} {:<7} {:>12} @ {}", tr.ts, tr.venue, format!("{:?}", tr.side), tr.qty, tr.price);
                    }
                }
                Some(Ok(Message::Subscribed(s))) => eprintln!("subscribed to {} {}", s.channel, s.symbol),
                Some(Ok(Message::Error(e))) => eprintln!("server error: {e}"),
                Some(Ok(_)) => {}
                Some(Err(e)) => return Err(e.into()),
                None => return Ok(()),
            },
            _ = tokio::signal::ctrl_c() => {
                stream.close().await;
                return Ok(());
            }
        }
    }
}
