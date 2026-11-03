//! Models of the REST responses and of the WebSocket messages.
//!
//! Every type maps one to one onto the JSON documents of the API (camelCase
//! fields). Missing fields decode to their default value, unknown fields are
//! ignored, so that additions to the API do not break older clients.

use serde::{Deserialize, Serialize};

/// Aggressor (taker) side of a trade.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Side {
    /// The taker bought.
    Buy,
    /// The taker sold.
    Sell,
    /// The venue does not say (any value this version does not know decodes to it).
    #[default]
    #[serde(other)]
    Unknown,
}

/// One trade of a venue.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Trade {
    /// Exchange time in milliseconds since the Unix epoch (receive time when
    /// the venue gives none).
    pub ts: i64,
    /// CCXT exchange id: `binance`, `binanceusdm`, `krakenfutures`...
    pub venue: String,
    /// Price in the quote currency.
    pub price: f64,
    /// Quantity in the base currency (in USD for inverse perpetuals).
    pub qty: f64,
    /// Aggressor side.
    pub side: Side,
}

/// Trades of one symbol, oldest first.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Trades {
    /// Unified CCXT symbol: `BTC/USDT`, `BTC/USDT:USDT`...
    pub symbol: String,
    /// Oldest first.
    pub trades: Vec<Trade>,
}

/// A symbol the API received trades for recently, with its venues.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct SymbolInfo {
    /// Unified CCXT symbol.
    pub symbol: String,
    /// `spot` or `perp`.
    pub market: String,
    /// CCXT exchange ids the symbol traded on.
    pub venues: Vec<String>,
    /// Time of the last trade, ms.
    pub last_trade_ts: i64,
}

/// Best bid and ask of one venue on its whole book; `None` when the venue
/// has none.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct VenueQuote {
    /// CCXT exchange id.
    pub venue: String,
    /// Last update of the venue book, ms.
    pub ts: Option<i64>,
    /// Best bid price.
    pub bid: Option<f64>,
    /// Quantity at the best bid.
    pub bid_qty: Option<f64>,
    /// Best ask price.
    pub ask: Option<f64>,
    /// Quantity at the best ask.
    pub ask_qty: Option<f64>,
}

/// A level of the merged book, with the venue it comes from.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Level {
    /// Price in the quote currency.
    pub price: f64,
    /// Quantity in the base currency (in USD for inverse perpetuals).
    pub qty: f64,
    /// CCXT exchange id.
    pub venue: String,
}

/// Merged order book of a symbol: the best bid and ask of every venue and the
/// first levels of each side, best first. Prices are not netted of fees.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Orderbook {
    /// Unified CCXT symbol.
    pub symbol: String,
    /// Time of the book, ms.
    pub ts: i64,
    /// Best bid and ask of each venue, on its whole book (not filtered by the
    /// venues asked for).
    pub venues: Vec<VenueQuote>,
    /// Best (highest) first.
    pub bids: Vec<Level>,
    /// Best (lowest) first.
    pub asks: Vec<Level>,
}

/// Market overview: the pairs Klondike follows permanently, what an order
/// costs on the merged book and on the best single venue, crosses between
/// venues, the venues setting the price and implied prices.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Overview {
    /// Time of the overview, ms.
    pub ts: i64,
    /// Oldest minute of the 1 h counters, ms.
    pub windows_from_ts: i64,
    /// Health of the market data.
    pub feeds: Feeds,
    /// Pairs followed permanently.
    pub pairs: Vec<Pair>,
    /// Share of the time each venue holds the best consolidated bid or ask
    /// over 1 h (BTC, ETH, SOL spot).
    pub leaders: Vec<Leader>,
    /// Stablecoin pegs, implied FX rates and triangles.
    pub implied: Implied,
    /// Best conversion paths.
    pub paths: Vec<Path>,
}

/// Health of the market data.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Feeds {
    /// Venues with at least one live book.
    pub venues: u32,
    /// Venues known.
    pub venues_known: u32,
    /// Live books.
    pub books_live: u32,
    /// Median latency of the live books, ms.
    pub lag_ms_p50: f64,
}

/// A pair of the overview. Amounts are in USD, costs in bps of the
/// consolidated mid.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Pair {
    /// Unified CCXT symbol.
    pub symbol: String,
    /// Base currency.
    pub base: String,
    /// Quote currency: `USDT`, `USDT:USDT`...
    pub quote: String,
    /// `spot` or `perp`.
    pub market: String,
    /// `ok`, `warn`, `alert` or `down`: data quality only.
    pub status: String,
    /// Why the status is not ok: `stale:<venue>`, `not_live:<venue>`...
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub reasons: Vec<String>,
    /// Venues listing the pair.
    pub exchanges: u32,
    /// Venues with a live book.
    pub live: u32,
    /// Consolidated mid (consensus mid when the merged book is crossed).
    pub mid: f64,
    /// Notional traded over 1 h, measured on the trades received.
    pub volume1h_usd: f64,
    /// Venue with the most volume over 1 h.
    pub volume_top_exchange: String,
    /// Share of that venue in the volume.
    pub volume_top_share: f64,
    /// Bid + ask within ±10 bps of the mid.
    pub depth10_usd: f64,
    /// Share of `depth10_usd` from books stopping before ±10 bps:
    /// `depth10_usd` is then a lower bound.
    pub depth10_partial_share: Option<f64>,
    /// (bid − ask) / (bid + ask) within ±50 bps.
    pub imbalance: f64,
    /// Cost of market orders of 100 k$, 1 M$ and 10 M$.
    pub impact: Vec<Impact>,
    /// Crosses between venues over 1 h, transient ones included.
    pub crosses1h: u32,
    /// Episodes net positive after taker fees over 1 h.
    pub crosses_positive1h: u32,
    /// Best net cross over 1 h, bps.
    pub max_net_bps1h: Option<f64>,
    /// Perpetuals: (mid perp − mid spot) / mid spot, bps.
    pub basis_bps: Option<f64>,
}

/// Cost of a market order, taker fees included, on the merged book and on
/// the best single venue. A cost is `None` when the book cannot fill the
/// order; the filled fraction then says how much it can.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Impact {
    /// Notional of the order, USD.
    pub notional_usd: f64,
    /// All-in cost of a buy on the merged book, bps.
    pub buy_all_in_bps: Option<f64>,
    /// All-in cost of a sell on the merged book, bps.
    pub sell_all_in_bps: Option<f64>,
    /// Fraction of the buy the merged book can fill.
    pub buy_filled: f64,
    /// Fraction of the sell the merged book can fill.
    pub sell_filled: f64,
    /// Venues the buy takes liquidity from.
    pub buy_venues: u32,
    /// Venues the sell takes liquidity from.
    pub sell_venues: u32,
    /// All-in cost of a buy on the best single venue, bps.
    pub best_single_buy_all_in_bps: Option<f64>,
    /// All-in cost of a sell on the best single venue, bps.
    pub best_single_sell_all_in_bps: Option<f64>,
    /// Best single venue to buy on.
    pub best_single_buy_exchange: String,
    /// Best single venue to sell on.
    pub best_single_sell_exchange: String,
}

/// Share of the time a venue holds the best consolidated bid or ask; ties
/// split the time, so that the shares sum to 1.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Leader {
    /// CCXT exchange id.
    pub exchange: String,
    /// Share of the time, 0 to 1.
    pub share: f64,
}

/// Implied prices of the overview.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Implied {
    /// Stablecoin pegs.
    pub pegs: Vec<Peg>,
    /// Fiat rates implied by crypto pairs.
    pub fx: Vec<ImpliedFx>,
    /// Triangles through cross pairs.
    pub triangles: Vec<Triangle>,
}

/// A stablecoin pair: its consolidated mid in bps from 1.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Peg {
    /// Unified CCXT symbol.
    pub symbol: String,
    /// Consolidated mid, bps from 1.
    pub bps: f64,
    /// Highest minus lowest venue mid, bps.
    pub venue_spread_bps: f64,
    /// Venues quoting the pair.
    pub venues: u32,
}

/// Rate of a fiat currency in USD implied by crypto pairs (median of
/// mid(X/USD) / mid(X/ccy)), against the direct pair when there is one.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct ImpliedFx {
    /// Currency code.
    pub ccy: String,
    /// Implied rate in USD.
    pub implied: f64,
    /// Rate of the direct pair.
    pub direct: Option<f64>,
    /// Deviation of the implied rate from the direct one, bps.
    pub dev_bps: Option<f64>,
}

/// Best of the two cycles through a cross pair and USDT, net of the taker
/// fees of its three legs.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Triangle {
    /// Cross pair.
    pub cross: String,
    /// The best cycle.
    pub cycle: String,
    /// Its result net of fees, bps.
    pub best_net_bps: f64,
}

/// Best conversion path, fees included; costs are against the consolidated
/// mid of the direct pair.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Path {
    /// Currency sold.
    pub sell: String,
    /// Currency bought.
    pub buy: String,
    /// Quantity sold.
    pub quantity: f64,
    /// Currencies of the path, in order.
    pub tokens: Vec<String>,
    /// Cost of the path, bps.
    pub cost_bps: f64,
    /// Cost of the direct pair, bps.
    pub direct_cost_bps: Option<f64>,
    /// Time of the computation, ms.
    pub ts: i64,
}
