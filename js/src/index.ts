/**
 * JavaScript/TypeScript client of the Klondike market data API: trades and
 * merged order books of the crypto pairs Klondike streams from about forty
 * venues, and the market overview, over REST and WebSocket.
 *
 * ```ts
 * import { KlondikeClient } from "@klondike-trading/klondike";
 * const client = new KlondikeClient({ apiKey: process.env.KLONDIKE_API_KEY! });
 * const book = await client.orderbook("BTC/USDT", { depth: 10 });
 * ```
 *
 * See https://klondike-trading.github.io/klondike-api/ for the documentation.
 */
export {
  KlondikeClient,
  KlondikeTimeoutError,
  DEFAULT_BASE_URL,
  type ClientOptions,
  type FetchLike,
  type OrderbookOptions,
  type RequestOptions,
  type TradesOptions,
} from "./client.js";
export {
  KlondikeStream,
  type StreamEvent,
  type StreamEvents,
  type StreamOptions,
  type WebSocketConstructor,
  type WebSocketLike,
} from "./stream.js";
export { KlondikeApiError, KlondikeStreamError } from "./errors.js";
export { decodeMessage, decodeJsonMessage, decodeCapnpMessage, KlondikeDecodeError } from "./codec.js";
export { VERSION } from "./version.js";
export type * from "./types.js";
