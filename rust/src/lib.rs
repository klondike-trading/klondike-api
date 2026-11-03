//! Client of the [Klondike](https://klondike-trading.github.io/klondike-api/)
//! market data API: trades and merged order books of the crypto pairs
//! Klondike streams from about forty venues, and the market overview, over
//! REST and WebSocket.
//!
//! ```no_run
//! # async fn run() -> klondike::Result<()> {
//! use klondike::{Client, Message, OrderbookOptions, StreamOptions};
//!
//! let client = Client::new(std::env::var("KLONDIKE_API_KEY").unwrap());
//!
//! // REST
//! let book = client
//!     .orderbook("BTC/USDT", OrderbookOptions { depth: Some(10), ..Default::default() })
//!     .await?;
//! println!("{} venues, best bid {:?}", book.venues.len(), book.bids.first());
//!
//! // WebSocket, reconnecting on its own
//! let mut stream = client.stream(StreamOptions::default()).await?;
//! stream.subscribe_orderbook("BTC/USDT", 20, &[]).await?;
//! while let Some(msg) = stream.next().await {
//!     if let Message::Orderbook(book) = msg? {
//!         println!("{} {:?} {:?}", book.symbol, book.bids.first(), book.asks.first());
//!     }
//! }
//! # Ok(()) }
//! ```
//!
//! API keys are created on the [portal](https://api.klondike-crypto.com/portal/).
//! The client needs a [Tokio](https://tokio.rs) runtime.
#![warn(missing_docs)]

mod client;
mod error;
mod message;
mod stream;
mod types;

#[allow(clippy::all, clippy::pedantic, missing_docs, unused_qualifications)]
mod klondike_api_capnp;

pub use client::{
    Client, ClientBuilder, OrderbookOptions, TradesOptions, DEFAULT_BASE_URL, VERSION,
};
pub use error::{Error, Result};
pub use message::{Channel, Format, Message, StreamError, Subscription};
pub use stream::{Stream, StreamHandle, StreamOptions};
pub use types::*;

/// The HTTP client crate, to build the client given to
/// [`ClientBuilder::http_client`] with the same version.
pub use reqwest;
