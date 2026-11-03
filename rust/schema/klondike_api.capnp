@0xe79b2a519cd80da8;

# Binary frames of the Klondike WebSocket API (`GET /v1/ws?format=capnp`).
#
# * Every binary frame sent by the server holds exactly one `ServerMessage`,
#   serialized with the Cap'n Proto *packed* encoding (one segment table, no
#   framing beyond the WebSocket frame).
# * Client messages (subscribe, unsubscribe, ping) are always JSON text frames,
#   whatever the format (see the WebSocket documentation).
# * The fields mirror the JSON messages one to one, with two encodings that
#   JSON does not need:
#     - a missing number (no bid on a venue, for instance) is NaN, a missing
#       timestamp is 0;
#     - the venue of an order book level is an index into `Orderbook.venues`.
# * Timestamps are milliseconds since the Unix epoch (UTC). Prices are in the
#   quote currency of the symbol; quantities in its base currency, except for
#   inverse perpetuals (BTC/USD:BTC) where they are in USD, as on the venues.

struct ServerMessage {
  union {
    trades       @0 :Trades;
    orderbook    @1 :Orderbook;
    overviewJson @2 :Text;
    # The overview is pushed every few seconds at most and has many optional
    # fields: it is sent as the JSON document of `GET /v1/overview`.
    subscribed   @3 :Subscription;
    unsubscribed @4 :Subscription;
    error        @5 :Error;
    pong         @6 :Void;
  }
}

struct Subscription {
  channel @0 :Text;   # trades | orderbook | overview
  symbol  @1 :Text;   # empty for overview
}

struct Error {
  code    @0 :Text;   # bad_request, unknown_channel, too_many_subscriptions...
  message @1 :Text;
  channel @2 :Text;   # the subscription the error is about, empty otherwise
  symbol  @3 :Text;
}

enum Side {
  unknown @0;
  buy     @1;
  sell    @2;
}

struct Trade {
  ts    @0 :Int64;    # exchange time, ms (receive time when the venue gives none)
  venue @1 :Text;     # CCXT exchange id: binance, binanceusdm, krakenfutures...
  price @2 :Float64;
  qty   @3 :Float64;
  side  @4 :Side;     # aggressor (taker) side
}

# The trades received since the previous message (batched every 100 ms).
struct Trades {
  symbol @0 :Text;
  trades @1 :List(Trade);   # oldest first
}

struct VenueQuote {
  venue  @0 :Text;
  ts     @1 :Int64;     # last update of the venue book, 0 if unknown
  bid    @2 :Float64;   # NaN when the venue has no bid
  bidQty @3 :Float64;
  ask    @4 :Float64;   # NaN when the venue has no ask
  askQty @5 :Float64;
}

struct Level {
  price @0 :Float64;
  qty   @1 :Float64;
  venue @2 :UInt16;     # index into Orderbook.venues
}

# The merged order book of every venue: the best bid/ask of each venue and the
# first levels of each side, every level with the venue it comes from.
struct Orderbook {
  symbol @0 :Text;
  ts     @1 :Int64;
  venues @2 :List(VenueQuote);
  bids   @3 :List(Level);   # best (highest) first
  asks   @4 :List(Level);   # best (lowest) first
}
