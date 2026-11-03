// Models of the Klondike market data API (see ../openapi.yaml). Field names
// are those of the JSON documents. Timestamps are milliseconds since the Unix
// epoch (UTC); prices are in the quote currency, quantities in the base
// currency (in USD for inverse perpetuals such as BTC/USD:BTC, as on the venues).

/** Aggressor (taker) side of a trade. */
export type Side = "buy" | "sell" | "unknown";

/** Spot pair or perpetual swap. */
export type Market = "spot" | "perp";

/** One trade of a venue. */
export interface Trade {
  /** Exchange time, ms (receive time when the venue gives none). */
  ts: number;
  /** CCXT exchange id: binance, binanceusdm, krakenfutures... */
  venue: string;
  price: number;
  qty: number;
  side: Side;
}

/** Trades of one symbol, oldest first. */
export interface Trades {
  symbol: string;
  trades: Trade[];
}

/** A symbol the API received trades for recently. */
export interface SymbolInfo {
  symbol: string;
  market: Market;
  venues: string[];
  lastTradeTs: number;
}

/** Best bid and ask of one venue; a field is null when the venue has none. */
export interface VenueQuote {
  venue: string;
  /** Last update of the venue book, ms. */
  ts: number | null;
  bid: number | null;
  bidQty: number | null;
  ask: number | null;
  askQty: number | null;
}

/** A level of the merged book, with the venue it comes from. */
export interface Level {
  price: number;
  qty: number;
  venue: string;
}

/**
 * The merged order book of a symbol: the best bid and ask of every venue (on
 * its whole book) and the first levels of each side, best first.
 */
export interface Orderbook {
  symbol: string;
  ts: number;
  venues: VenueQuote[];
  /** Best (highest) first. */
  bids: Level[];
  /** Best (lowest) first. */
  asks: Level[];
}

/**
 * The market overview: the pairs Klondike follows permanently, what an order
 * costs on the merged book and on the best single venue, crosses between
 * venues, the venues setting the price, implied prices and conversion paths.
 */
export interface Overview {
  ts: number;
  /** Oldest minute of the 1 h counters. */
  windowsFromTs: number;
  feeds: Feeds;
  pairs: Pair[];
  /** Share of the time each venue holds the best consolidated bid or ask over 1 h (BTC, ETH, SOL spot); the shares sum to 1. */
  leaders: Leader[];
  implied: Implied;
  paths: Path[];
}

/** Health of the market data. */
export interface Feeds {
  /** Venues with at least one live book. */
  venues: number;
  venuesKnown: number;
  booksLive: number;
  /** Median latency of the live books, ms. */
  lagMsP50: number;
}

/** A pair of the overview. Amounts are in USD, costs in bps of the consolidated mid. */
export interface Pair {
  symbol: string;
  base: string;
  /** USDT, USDT:USDT... */
  quote: string;
  market: Market;
  /** Data quality only. */
  status: "ok" | "warn" | "alert" | "down";
  /** Why the status is not ok: stale:<venue>, not_live:<venue>, cross_stale:<venue>... Absent when ok. */
  reasons?: string[];
  /** Venues listing the pair. */
  exchanges: number;
  /** Venues with a live book. */
  live: number;
  /** Consolidated mid (consensus mid when the merged book is crossed). */
  mid: number;
  /** Notional traded over 1 h, measured on the trades received. */
  volume1hUsd: number;
  volumeTopExchange: string;
  volumeTopShare: number;
  /** Bid + ask within ±10 bps of the mid. */
  depth10Usd: number;
  /** Share of depth10Usd from books stopping before ±10 bps: depth10Usd is then a lower bound. */
  depth10PartialShare: number | null;
  /** (bid − ask) / (bid + ask) within ±50 bps. */
  imbalance: number;
  impact: Impact[];
  /** Crosses between venues over 1 h, transient ones included. */
  crosses1h: number;
  /** Episodes net positive after taker fees. */
  crossesPositive1h: number;
  maxNetBps1h: number | null;
  /** Perpetuals: (mid perp − mid spot) / mid spot, bps. */
  basisBps: number | null;
}

/**
 * Cost of a market order of `notionalUsd`, taker fees included, on the merged
 * book and on the best single venue. A cost is null when the book cannot fill
 * the order; the filled fraction then says how much it can.
 */
export interface Impact {
  notionalUsd: number;
  buyAllInBps: number | null;
  sellAllInBps: number | null;
  buyFilled: number;
  sellFilled: number;
  buyVenues: number;
  sellVenues: number;
  bestSingleBuyAllInBps: number | null;
  bestSingleSellAllInBps: number | null;
  bestSingleBuyExchange: string;
  bestSingleSellExchange: string;
}

/** Share of the time a venue holds the best consolidated bid or ask (ties split). */
export interface Leader {
  exchange: string;
  share: number;
}

export interface Implied {
  pegs: Peg[];
  fx: ImpliedFX[];
  triangles: Triangle[];
}

/** A stablecoin pair: its consolidated mid in bps from 1. */
export interface Peg {
  symbol: string;
  bps: number;
  /** Highest minus lowest venue mid. */
  venueSpreadBps: number;
  venues: number;
}

/** Rate of a fiat currency in USD implied by crypto pairs, against the direct pair when there is one. */
export interface ImpliedFX {
  ccy: string;
  implied: number;
  direct: number | null;
  devBps: number | null;
}

/** Best of the two cycles through a cross pair and USDT, net of the taker fees of its three legs. */
export interface Triangle {
  cross: string;
  cycle: string;
  bestNetBps: number;
}

/** Best conversion path, fees included; costs against the consolidated mid of the direct pair. */
export interface Path {
  sell: string;
  buy: string;
  quantity: number;
  tokens: string[];
  costBps: number;
  directCostBps: number | null;
  ts: number;
}

// --- WebSocket ---------------------------------------------------------------

/** A stream of the WebSocket API. */
export type Channel = "trades" | "orderbook" | "overview";

/** Encoding of the server messages of a WebSocket: the messages are the same. */
export type Format = "json" | "capnp";

/** Names a stream; `symbol` is "" for the overview. */
export interface Subscription {
  channel: Channel;
  symbol: string;
}

/** Trades received since the previous message (batched every 100 ms), oldest first. */
export interface TradesMessage extends Trades {
  type: "trades";
}

/** The merged order book, whole. `venues` is not filtered by the venues of the subscription; `bids` and `asks` are. */
export interface OrderbookMessage extends Orderbook {
  type: "orderbook";
}

/** The overview, whole. */
export interface OverviewMessage {
  type: "overview";
  overview: Overview;
}

/** Acknowledgement of a subscription; no `symbol` for the overview. */
export interface SubscribedMessage {
  type: "subscribed";
  channel: Channel;
  symbol?: string;
}

/** Acknowledgement of an unsubscription; no `symbol` for the overview. */
export interface UnsubscribedMessage {
  type: "unsubscribed";
  channel: Channel;
  symbol?: string;
}

/**
 * An error of the server about a client message. It does not close the
 * connection. `channel` (and `symbol`) are set when the error is about a
 * subscription; the stream then no longer sends that subscription again
 * after a reconnection.
 */
export interface ErrorMessage {
  type: "error";
  /** bad_request, unknown_channel, too_many_subscriptions... */
  code: string;
  message: string;
  channel?: Channel;
  symbol?: string;
}

/** Answer to a ping. */
export interface PongMessage {
  type: "pong";
}

/** A server message of the WebSocket: `type` says which. */
export type StreamMessage =
  | TradesMessage
  | OrderbookMessage
  | OverviewMessage
  | SubscribedMessage
  | UnsubscribedMessage
  | ErrorMessage
  | PongMessage;

/** The type of a server message. */
export type MessageType = StreamMessage["type"];

/** A message of the client: subscribe, unsubscribe or ping. Always sent as JSON text. */
export interface ClientMessage {
  op: "subscribe" | "unsubscribe" | "ping";
  channel?: Channel;
  symbol?: string;
  venues?: string[];
  depth?: number;
}
