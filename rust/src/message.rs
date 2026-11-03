//! Server messages of the WebSocket and their two encodings: JSON text frames
//! (`format=json`) and packed Cap'n Proto binary frames (`format=capnp`).

use std::collections::HashMap;
use std::fmt;

use capnp::message::{Builder, ReaderOptions};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use crate::klondike_api_capnp as cp;
use crate::{Error, Level, Orderbook, Overview, Result, Side, Trade, Trades, VenueQuote};

/// A stream of the WebSocket API.
#[derive(Debug, Clone, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Channel {
    /// The trades of a symbol.
    Trades,
    /// The merged order book of a symbol.
    Orderbook,
    /// The market overview.
    Overview,
    /// A channel this version does not know (in an `unknown_channel` error,
    /// for instance).
    Other(String),
}

impl Channel {
    /// The name of the channel on the wire.
    pub fn as_str(&self) -> &str {
        match self {
            Channel::Trades => "trades",
            Channel::Orderbook => "orderbook",
            Channel::Overview => "overview",
            Channel::Other(s) => s,
        }
    }
}

impl From<&str> for Channel {
    fn from(s: &str) -> Self {
        match s {
            "trades" => Channel::Trades,
            "orderbook" => Channel::Orderbook,
            "overview" => Channel::Overview,
            other => Channel::Other(other.to_string()),
        }
    }
}

impl fmt::Display for Channel {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

impl Serialize for Channel {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        s.serialize_str(self.as_str())
    }
}

impl<'de> Deserialize<'de> for Channel {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        let s = std::borrow::Cow::<'de, str>::deserialize(d)?;
        Ok(Channel::from(s.as_ref()))
    }
}

/// Encoding of the server messages of a WebSocket. The messages are the same
/// whatever the format.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum Format {
    /// Text frames, one JSON object per frame.
    #[default]
    Json,
    /// Binary frames, one packed Cap'n Proto `ServerMessage` per frame: about
    /// three times smaller than JSON on order books, and decoded without
    /// parsing text.
    Capnp,
}

impl Format {
    /// The value of the `format` query parameter.
    pub fn as_str(self) -> &'static str {
        match self {
            Format::Json => "json",
            Format::Capnp => "capnp",
        }
    }
}

/// A subscription: a channel and a symbol (empty for the overview).
#[derive(Debug, Clone, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub struct Subscription {
    /// The channel.
    pub channel: Channel,
    /// Unified CCXT symbol, empty for the overview.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub symbol: String,
}

/// An error message of the server. It does not close the connection.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StreamError {
    /// `bad_request`, `unknown_channel`, `too_many_subscriptions`...
    #[serde(default)]
    pub code: String,
    /// Human readable message.
    #[serde(default)]
    pub message: String,
    /// The channel of the subscription the error is about, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub channel: Option<Channel>,
    /// The symbol of the subscription the error is about, empty otherwise.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub symbol: String,
}

impl fmt::Display for StreamError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match &self.channel {
            Some(c) => write!(f, "{} ({} {} {})", self.message, self.code, c, self.symbol),
            None => write!(f, "{} ({})", self.message, self.code),
        }
    }
}

impl std::error::Error for StreamError {}

/// A server message of the WebSocket. It is the same whatever the
/// [`Format`] of the stream.
#[derive(Debug, Clone, PartialEq)]
pub enum Message {
    /// Trades received since the previous message (batched every 100 ms),
    /// oldest first.
    Trades(Trades),
    /// The merged order book, whole.
    Orderbook(Orderbook),
    /// The market overview, whole.
    Overview(Box<Overview>),
    /// Acknowledgement of a subscription.
    Subscribed(Subscription),
    /// Acknowledgement of an unsubscription.
    Unsubscribed(Subscription),
    /// An error of the server about a client message; the connection stays
    /// open.
    Error(StreamError),
    /// Answer to [`Stream::ping`](crate::Stream::ping).
    Pong,
}

const KNOWN_TYPES: [&str; 7] = [
    "trades",
    "orderbook",
    "overview",
    "subscribed",
    "unsubscribed",
    "error",
    "pong",
];

// JSON wire format: `{"type": "...", ...fields}`; the overview is nested in
// an `overview` field.
#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "lowercase")]
enum Wire {
    Trades(Trades),
    Orderbook(Orderbook),
    Overview { overview: Box<Overview> },
    Subscribed(Subscription),
    Unsubscribed(Subscription),
    Error(StreamError),
    Pong,
}

#[derive(Serialize)]
#[serde(tag = "type", rename_all = "lowercase")]
enum WireRef<'a> {
    Trades(&'a Trades),
    Orderbook(&'a Orderbook),
    Overview { overview: &'a Overview },
    Subscribed(&'a Subscription),
    Unsubscribed(&'a Subscription),
    Error(&'a StreamError),
    Pong,
}

impl Serialize for Message {
    fn serialize<S: Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        match self {
            Message::Trades(t) => WireRef::Trades(t),
            Message::Orderbook(b) => WireRef::Orderbook(b),
            Message::Overview(o) => WireRef::Overview { overview: o },
            Message::Subscribed(s) => WireRef::Subscribed(s),
            Message::Unsubscribed(s) => WireRef::Unsubscribed(s),
            Message::Error(e) => WireRef::Error(e),
            Message::Pong => WireRef::Pong,
        }
        .serialize(s)
    }
}

impl<'de> Deserialize<'de> for Message {
    fn deserialize<D: Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        Ok(match Wire::deserialize(d)? {
            Wire::Trades(t) => Message::Trades(t),
            Wire::Orderbook(b) => Message::Orderbook(b),
            Wire::Overview { overview } => Message::Overview(overview),
            Wire::Subscribed(s) => Message::Subscribed(s),
            Wire::Unsubscribed(s) => Message::Unsubscribed(s),
            Wire::Error(e) => Message::Error(e),
            Wire::Pong => Message::Pong,
        })
    }
}

impl Message {
    /// Decodes a text frame of `format=json`. A message of a type this
    /// version does not know is an [`Error::UnknownMessage`] (streams skip
    /// them: a newer server may add types).
    pub fn from_json(data: &[u8]) -> Result<Message> {
        serde_json::from_slice(data).map_err(|e| {
            #[derive(Deserialize)]
            struct Tag<'a> {
                #[serde(borrow, rename = "type")]
                kind: std::borrow::Cow<'a, str>,
            }
            match serde_json::from_slice::<Tag<'_>>(data) {
                Ok(t) if !KNOWN_TYPES.contains(&t.kind.as_ref()) => {
                    Error::UnknownMessage(t.kind.into_owned())
                }
                _ => Error::Json(e),
            }
        })
    }

    /// Encodes the message as the server sends it with `format=json`.
    pub fn to_json(&self) -> Result<Vec<u8>> {
        Ok(serde_json::to_vec(self)?)
    }

    /// Decodes a binary frame of `format=capnp`: one packed `ServerMessage`.
    /// A union case this version does not know is an
    /// [`Error::UnknownMessage`] (streams skip them).
    pub fn from_capnp(data: &[u8]) -> Result<Message> {
        let mut options = ReaderOptions::new();
        // The overview is a few hundred kB; 64 MiB is the most a frame holds.
        options.traversal_limit_in_words(Some(8 << 20));
        let reader = capnp::serialize_packed::read_message(data, options)?;
        let root: cp::server_message::Reader = reader.get_root()?;
        let which = root.which().map_err(|capnp::NotInSchema(n)| {
            Error::UnknownMessage(format!("capnp union case {n}"))
        })?;
        Ok(match which {
            cp::server_message::Trades(t) => Message::Trades(decode_trades(t?)?),
            cp::server_message::Orderbook(b) => Message::Orderbook(decode_orderbook(b?)?),
            cp::server_message::OverviewJson(text) => {
                Message::Overview(serde_json::from_slice(text?.as_bytes())?)
            }
            cp::server_message::Subscribed(s) => Message::Subscribed(decode_subscription(s?)?),
            cp::server_message::Unsubscribed(s) => Message::Unsubscribed(decode_subscription(s?)?),
            cp::server_message::Error(e) => {
                let e = e?;
                let channel = text(e.get_channel())?;
                Message::Error(StreamError {
                    code: text(e.get_code())?,
                    message: text(e.get_message())?,
                    channel: (!channel.is_empty()).then(|| Channel::from(channel.as_str())),
                    symbol: text(e.get_symbol())?,
                })
            }
            cp::server_message::Pong(()) => Message::Pong,
        })
    }

    /// Encodes the message as the server sends it with `format=capnp`: a
    /// packed `ServerMessage`.
    pub fn to_capnp(&self) -> Result<Vec<u8>> {
        let mut msg = Builder::new_default();
        let mut root = msg.init_root::<cp::server_message::Builder>();
        match self {
            Message::Trades(t) => encode_trades(root.init_trades(), t)?,
            Message::Orderbook(b) => encode_orderbook(root.init_orderbook(), b)?,
            Message::Overview(o) => root.set_overview_json(serde_json::to_string(o)?.as_str()),
            Message::Subscribed(s) => encode_subscription(root.init_subscribed(), s),
            Message::Unsubscribed(s) => encode_subscription(root.init_unsubscribed(), s),
            Message::Error(e) => {
                let mut c = root.init_error();
                c.set_code(e.code.as_str());
                c.set_message(e.message.as_str());
                c.set_channel(e.channel.as_ref().map_or("", Channel::as_str));
                c.set_symbol(e.symbol.as_str());
            }
            Message::Pong => root.set_pong(()),
        }
        let mut out = Vec::new();
        capnp::serialize_packed::write_message(&mut out, &msg)?;
        Ok(out)
    }
}

fn text(r: capnp::Result<capnp::text::Reader<'_>>) -> Result<String> {
    Ok(r?.to_string().map_err(capnp::Error::from)?)
}

fn decode_subscription(s: cp::subscription::Reader<'_>) -> Result<Subscription> {
    Ok(Subscription {
        channel: Channel::from(text(s.get_channel())?.as_str()),
        symbol: text(s.get_symbol())?,
    })
}

fn encode_subscription(mut c: cp::subscription::Builder<'_>, s: &Subscription) {
    c.set_channel(s.channel.as_str());
    c.set_symbol(s.symbol.as_str());
}

fn decode_trades(t: cp::trades::Reader<'_>) -> Result<Trades> {
    let list = t.get_trades()?;
    let mut trades = Vec::with_capacity(list.len() as usize);
    for c in list.iter() {
        trades.push(Trade {
            ts: c.get_ts(),
            venue: text(c.get_venue())?,
            price: c.get_price(),
            qty: c.get_qty(),
            side: match c.get_side() {
                Ok(cp::Side::Buy) => Side::Buy,
                Ok(cp::Side::Sell) => Side::Sell,
                _ => Side::Unknown,
            },
        });
    }
    Ok(Trades {
        symbol: text(t.get_symbol())?,
        trades,
    })
}

fn encode_trades(mut c: cp::trades::Builder<'_>, t: &Trades) -> Result<()> {
    c.set_symbol(t.symbol.as_str());
    let mut list = c.init_trades(len_u32(t.trades.len())?);
    for (i, tr) in t.trades.iter().enumerate() {
        let mut c = list.reborrow().get(i as u32);
        c.set_ts(tr.ts);
        c.set_venue(tr.venue.as_str());
        c.set_price(tr.price);
        c.set_qty(tr.qty);
        c.set_side(match tr.side {
            Side::Buy => cp::Side::Buy,
            Side::Sell => cp::Side::Sell,
            Side::Unknown => cp::Side::Unknown,
        });
    }
    Ok(())
}

fn decode_orderbook(b: cp::orderbook::Reader<'_>) -> Result<Orderbook> {
    let list = b.get_venues()?;
    let mut venues = Vec::with_capacity(list.len() as usize);
    for c in list.iter() {
        let ts = c.get_ts();
        venues.push(VenueQuote {
            venue: text(c.get_venue())?,
            ts: (ts != 0).then_some(ts),
            bid: unless_nan(c.get_bid()),
            bid_qty: unless_nan(c.get_bid_qty()),
            ask: unless_nan(c.get_ask()),
            ask_qty: unless_nan(c.get_ask_qty()),
        });
    }
    let side = |list: capnp::struct_list::Reader<'_, cp::level::Owned>| -> Result<Vec<Level>> {
        let mut levels = Vec::with_capacity(list.len() as usize);
        for c in list.iter() {
            let vi = usize::from(c.get_venue());
            let venue = venues.get(vi).ok_or_else(|| {
                Error::Capnp(capnp::Error::failed(format!(
                    "level venue index {vi} out of {} venues",
                    venues.len()
                )))
            })?;
            levels.push(Level {
                price: c.get_price(),
                qty: c.get_qty(),
                venue: venue.venue.clone(),
            });
        }
        Ok(levels)
    };
    let bids = side(b.get_bids()?)?;
    let asks = side(b.get_asks()?)?;
    Ok(Orderbook {
        symbol: text(b.get_symbol())?,
        ts: b.get_ts(),
        venues,
        bids,
        asks,
    })
}

fn encode_orderbook(mut c: cp::orderbook::Builder<'_>, b: &Orderbook) -> Result<()> {
    c.set_symbol(b.symbol.as_str());
    c.set_ts(b.ts);
    let mut index = HashMap::with_capacity(b.venues.len());
    {
        let mut list = c.reborrow().init_venues(len_u32(b.venues.len())?);
        for (i, v) in b.venues.iter().enumerate() {
            let vi = u16::try_from(i)
                .map_err(|_| Error::Capnp(capnp::Error::failed("too many venues".into())))?;
            index.insert(v.venue.as_str(), vi);
            let mut q = list.reborrow().get(i as u32);
            q.set_venue(v.venue.as_str());
            q.set_ts(v.ts.unwrap_or(0));
            q.set_bid(v.bid.unwrap_or(f64::NAN));
            q.set_bid_qty(v.bid_qty.unwrap_or(f64::NAN));
            q.set_ask(v.ask.unwrap_or(f64::NAN));
            q.set_ask_qty(v.ask_qty.unwrap_or(f64::NAN));
        }
    }
    let encode = |mut list: capnp::struct_list::Builder<'_, cp::level::Owned>,
                  levels: &[Level]|
     -> Result<()> {
        for (i, l) in levels.iter().enumerate() {
            let vi = *index.get(l.venue.as_str()).ok_or_else(|| {
                Error::Capnp(capnp::Error::failed(format!(
                    "level of venue {:?} not in the venues of the book",
                    l.venue
                )))
            })?;
            let mut c = list.reborrow().get(i as u32);
            c.set_price(l.price);
            c.set_qty(l.qty);
            c.set_venue(vi);
        }
        Ok(())
    };
    encode(c.reborrow().init_bids(len_u32(b.bids.len())?), &b.bids)?;
    encode(c.init_asks(len_u32(b.asks.len())?), &b.asks)
}

fn unless_nan(v: f64) -> Option<f64> {
    (!v.is_nan()).then_some(v)
}

fn len_u32(n: usize) -> Result<u32> {
    u32::try_from(n).map_err(|_| Error::Capnp(capnp::Error::failed("list too long".into())))
}

/// A message of the client: subscribe, unsubscribe or ping. Always JSON.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub(crate) struct ClientMessage {
    pub op: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub channel: Option<Channel>,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub symbol: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub venues: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub depth: Option<u32>,
}

impl ClientMessage {
    pub(crate) fn subscribe(
        channel: Channel,
        symbol: &str,
        depth: Option<u32>,
        venues: &[&str],
    ) -> Self {
        ClientMessage {
            op: "subscribe",
            channel: Some(channel),
            symbol: symbol.to_string(),
            venues: venues.iter().map(|v| v.to_string()).collect(),
            depth,
        }
    }

    pub(crate) fn unsubscribe(channel: Channel, symbol: &str) -> Self {
        ClientMessage {
            op: "unsubscribe",
            channel: Some(channel),
            symbol: symbol.to_string(),
            venues: Vec::new(),
            depth: None,
        }
    }

    pub(crate) fn ping() -> Self {
        ClientMessage {
            op: "ping",
            channel: None,
            symbol: String::new(),
            venues: Vec::new(),
            depth: None,
        }
    }

    pub(crate) fn subscription(&self) -> Subscription {
        Subscription {
            channel: self.channel.clone().unwrap_or(Channel::Overview),
            symbol: self.symbol.clone(),
        }
    }
}
