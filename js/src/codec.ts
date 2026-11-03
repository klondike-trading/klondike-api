// Decoding of the server messages of the WebSocket, in both formats, to the
// same objects.

import { Message as CapnpMessage, type List } from "capnp-es";
import {
  ServerMessage,
  ServerMessage_Which,
  Side as CapnpSide,
  type Level as CapnpLevel,
} from "./capnp/klondike_api.js";
import type {
  Channel,
  ErrorMessage,
  Level,
  Overview,
  Side,
  StreamMessage,
  SubscribedMessage,
  Trade,
  UnsubscribedMessage,
  VenueQuote,
} from "./types.js";

/** A server message could not be decoded. */
export class KlondikeDecodeError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "KlondikeDecodeError";
  }
}

/**
 * Decodes a server message: a string is a JSON message (`format=json`),
 * bytes a packed Cap'n Proto `ServerMessage` (`format=capnp`).
 *
 * A message of a type this version does not know (a newer server) gives
 * `undefined`: skip it. Only a corrupt frame throws a {@link KlondikeDecodeError}.
 */
export function decodeMessage(data: string | ArrayBuffer | ArrayBufferView): StreamMessage | undefined {
  return typeof data === "string" ? decodeJsonMessage(data) : decodeCapnpMessage(data);
}

/** Decodes a server message of `format=json`; `undefined` for an unknown type. */
export function decodeJsonMessage(text: string): StreamMessage | undefined {
  let m: any;
  try {
    m = JSON.parse(text);
  } catch (err) {
    throw new KlondikeDecodeError(`invalid JSON message: ${(err as Error).message}`);
  }
  if (m === null || typeof m !== "object") {
    throw new KlondikeDecodeError("JSON message is not an object");
  }
  switch (m.type) {
    case "trades":
      m.trades ??= [];
      return m;
    case "orderbook":
      m.venues ??= [];
      m.bids ??= [];
      m.asks ??= [];
      return m;
    case "overview":
      return { type: "overview", overview: m.overview as Overview };
    case "subscribed":
    case "unsubscribed":
      return subscription(m.type, m.channel, m.symbol);
    case "error":
      return errorMessage(m.code, m.message, m.channel, m.symbol);
    case "pong":
      return { type: "pong" };
  }
  if (typeof m.type !== "string") throw new KlondikeDecodeError("JSON message without type");
  return undefined; // a type of a newer server
}

const SIDES: Record<number, Side> = {
  [CapnpSide.BUY]: "buy",
  [CapnpSide.SELL]: "sell",
};

/** Decodes a server message of `format=capnp` (packed `ServerMessage`); `undefined` for an unknown union case. */
export function decodeCapnpMessage(data: ArrayBuffer | ArrayBufferView): StreamMessage | undefined {
  try {
    return decodeCapnpRoot(data);
  } catch (err) {
    if (err instanceof KlondikeDecodeError) throw err;
    throw new KlondikeDecodeError(`invalid Cap'n Proto message: ${(err as Error).message}`);
  }
}

function decodeCapnpRoot(data: ArrayBuffer | ArrayBufferView): StreamMessage | undefined {
  let root: ServerMessage;
  try {
    root = new CapnpMessage(data, true).getRoot(ServerMessage);
  } catch (err) {
    throw new KlondikeDecodeError(`invalid Cap'n Proto message: ${(err as Error).message}`);
  }
  switch (root.which()) {
    case ServerMessage_Which.TRADES: {
      const t = root.trades;
      const list = t.trades;
      const trades: Trade[] = new Array(list.length);
      for (let i = 0; i < trades.length; i++) {
        const c = list.get(i);
        trades[i] = {
          ts: Number(c.ts),
          venue: c.venue,
          price: c.price,
          qty: c.qty,
          side: SIDES[c.side] ?? "unknown",
        };
      }
      return { type: "trades", symbol: t.symbol, trades };
    }
    case ServerMessage_Which.ORDERBOOK: {
      const b = root.orderbook;
      const vl = b.venues;
      const venues: VenueQuote[] = new Array(vl.length);
      for (let i = 0; i < venues.length; i++) {
        const c = vl.get(i);
        const ts = Number(c.ts);
        venues[i] = {
          venue: c.venue,
          ts: ts === 0 ? null : ts,
          bid: nanToNull(c.bid),
          bidQty: nanToNull(c.bidQty),
          ask: nanToNull(c.ask),
          askQty: nanToNull(c.askQty),
        };
      }
      return {
        type: "orderbook",
        symbol: b.symbol,
        ts: Number(b.ts),
        venues,
        bids: levels(b.bids, venues),
        asks: levels(b.asks, venues),
      };
    }
    case ServerMessage_Which.OVERVIEW_JSON: {
      let overview: Overview;
      try {
        overview = JSON.parse(root.overviewJson);
      } catch (err) {
        throw new KlondikeDecodeError(`invalid overview JSON: ${(err as Error).message}`);
      }
      return { type: "overview", overview };
    }
    case ServerMessage_Which.SUBSCRIBED: {
      const s = root.subscribed;
      return subscription("subscribed", s.channel, s.symbol);
    }
    case ServerMessage_Which.UNSUBSCRIBED: {
      const s = root.unsubscribed;
      return subscription("unsubscribed", s.channel, s.symbol);
    }
    case ServerMessage_Which.ERROR: {
      const e = root.error;
      return errorMessage(e.code, e.message, e.channel, e.symbol);
    }
    case ServerMessage_Which.PONG:
      return { type: "pong" };
  }
  return undefined; // a union case of a newer server
}

function levels(list: List<CapnpLevel>, venues: VenueQuote[]): Level[] {
  const out: Level[] = new Array(list.length);
  for (let i = 0; i < out.length; i++) {
    const c = list.get(i);
    const v = venues[c.venue];
    if (v === undefined) {
      throw new KlondikeDecodeError(`level venue index ${c.venue} out of ${venues.length} venues`);
    }
    out[i] = { price: c.price, qty: c.qty, venue: v.venue };
  }
  return out;
}

function nanToNull(v: number): number | null {
  return Number.isNaN(v) ? null : v;
}

// An empty symbol (the overview) is left out, as the JSON of the server does;
// Cap'n Proto has "".
function subscription<T extends "subscribed" | "unsubscribed">(
  type: T,
  channel: unknown,
  symbol: unknown,
): T extends "subscribed" ? SubscribedMessage : UnsubscribedMessage {
  const m: any = { type, channel: (channel ?? "") as Channel };
  if (symbol) m.symbol = symbol as string;
  return m;
}

// channel and symbol are left out when empty: the JSON of the server omits
// them when the error is not about a subscription, Cap'n Proto has "".
function errorMessage(code: unknown, message: unknown, channel: unknown, symbol: unknown): ErrorMessage {
  const e: ErrorMessage = { type: "error", code: String(code ?? ""), message: String(message ?? "") };
  if (channel) e.channel = channel as Channel;
  if (symbol) e.symbol = symbol as string;
  return e;
}
