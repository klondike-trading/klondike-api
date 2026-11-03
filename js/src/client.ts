import { apiErrorFrom } from "./errors.js";
import { KlondikeStream, type StreamOptions } from "./stream.js";
import type { Orderbook, Overview, SymbolInfo, Trades } from "./types.js";
import { VERSION } from "./version.js";

/** The production API. */
export const DEFAULT_BASE_URL = "https://api.klondike-crypto.com";

/** The `fetch` function the client calls (the global one by default). */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

export interface ClientOptions {
  /** API key (`kl_` + 32 characters), created on https://api.klondike-crypto.com/portal/. */
  apiKey: string;
  /** Server, `https://api.klondike-crypto.com` by default. */
  baseUrl?: string;
  /** Replaces the global `fetch` (Node < 18, tests, proxies...). */
  fetch?: FetchLike;
  /** Timeout of a REST request in ms (30 s by default, 0 for none). */
  timeoutMs?: number;
}

/** Options common to every REST call. */
export interface RequestOptions {
  /** Aborts the request. */
  signal?: AbortSignal;
}

export interface TradesOptions extends RequestOptions {
  /** Number of trades, the most recent ones: 1 to 1000 (100 by default). */
  limit?: number;
  /** Only trades strictly after this timestamp, ms. */
  since?: number;
  /** CCXT exchange ids; all venues when absent. */
  venues?: string[];
}

export interface OrderbookOptions extends RequestOptions {
  /** Levels per side, 1 to 1000 (50 by default). */
  depth?: number;
  /** CCXT exchange ids; all venues when absent. */
  venues?: string[];
}

/** @internal What a stream needs from its client. */
export interface ClientConfig {
  apiKey: string;
  baseUrl: string;
  fetch: FetchLike;
  userAgent?: string;
}

const isNode = typeof (globalThis as any).process?.versions?.node === "string";

/**
 * Client of the Klondike market data API: REST calls, and WebSocket streams
 * with {@link KlondikeClient.stream}.
 *
 * ```ts
 * const client = new KlondikeClient({ apiKey: process.env.KLONDIKE_API_KEY! });
 * const book = await client.orderbook("BTC/USDT", { depth: 10 });
 * ```
 */
export class KlondikeClient {
  readonly baseUrl: string;
  readonly #config: ClientConfig;
  readonly #timeoutMs: number;

  constructor(options: ClientOptions) {
    if (!options || !options.apiKey) {
      throw new TypeError("KlondikeClient: apiKey is required");
    }
    this.baseUrl = (options.baseUrl ?? DEFAULT_BASE_URL).replace(/\/+$/, "");
    const f = options.fetch;
    this.#config = {
      apiKey: options.apiKey,
      baseUrl: this.baseUrl,
      // Called as a plain function: browsers refuse fetch called on another object.
      fetch: f ?? ((input, init) => globalThis.fetch(input, init)),
      // Browsers forbid the header (and it would need a CORS preflight).
      userAgent: isNode ? `klondike-js/${VERSION}` : undefined,
    };
    this.#timeoutMs = options.timeoutMs ?? 30_000;
  }

  /**
   * Symbols with recent trades, sorted, with the venues they traded on.
   * @param q case insensitive substring of the symbol (`btc`, `/usdc`)
   */
  async symbols(q?: string, options?: RequestOptions): Promise<SymbolInfo[]> {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    const out = await this.#get<{ symbols: SymbolInfo[] }>("/v1/symbols", params, options?.signal);
    return out.symbols ?? [];
  }

  /**
   * The last trades of the symbol on every venue, oldest first. The API keeps
   * the last 1,000 trades of each symbol: stream them to get them all.
   */
  async trades(symbol: string, options: TradesOptions = {}): Promise<Trades> {
    const params = new URLSearchParams({ symbol });
    if (options.limit !== undefined && options.limit > 0) params.set("limit", String(options.limit));
    if (options.since !== undefined && options.since > 0) params.set("since", String(options.since));
    if (options.venues?.length) params.set("venues", options.venues.join(","));
    const out = await this.#get<Trades>("/v1/trades", params, options.signal);
    out.trades ??= [];
    return out;
  }

  /**
   * The merged order book of the symbol. The first request for a symbol nobody
   * follows can take up to two seconds; a symbol no venue streams is a 404.
   */
  async orderbook(symbol: string, options: OrderbookOptions = {}): Promise<Orderbook> {
    const params = new URLSearchParams({ symbol });
    if (options.depth !== undefined && options.depth > 0) params.set("depth", String(options.depth));
    if (options.venues?.length) params.set("venues", options.venues.join(","));
    const out = await this.#get<Orderbook>("/v1/orderbook", params, options.signal);
    out.venues ??= [];
    out.bids ??= [];
    out.asks ??= [];
    return out;
  }

  /** The market overview (503 until the server received one). */
  overview(options?: RequestOptions): Promise<Overview> {
    return this.#get<Overview>("/v1/overview", undefined, options?.signal);
  }

  /**
   * Opens a WebSocket stream. The connection starts at once; subscriptions
   * made before it is open are sent when it opens, and again after each
   * reconnection.
   */
  stream(options: StreamOptions = {}): KlondikeStream {
    return new KlondikeStream(this.#config, options);
  }

  async #get<T>(path: string, params: URLSearchParams | undefined, signal: AbortSignal | undefined): Promise<T> {
    const qs = params?.toString();
    const url = this.baseUrl + path + (qs ? `?${qs}` : "");
    const headers: Record<string, string> = { "X-API-Key": this.#config.apiKey, Accept: "application/json" };
    if (this.#config.userAgent) headers["User-Agent"] = this.#config.userAgent;

    const ctrl = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const onAbort = () => ctrl.abort(signal?.reason);
    if (signal) {
      if (signal.aborted) onAbort();
      else signal.addEventListener("abort", onAbort, { once: true });
    }
    if (this.#timeoutMs > 0) {
      timer = setTimeout(
        () => ctrl.abort(new KlondikeTimeoutError(`klondike: ${path} timed out after ${this.#timeoutMs} ms`)),
        this.#timeoutMs,
      );
    }
    try {
      const res = await this.#config.fetch(url, { method: "GET", headers, signal: ctrl.signal });
      if (res.status !== 200) {
        const body = await res.text().catch(() => "");
        throw apiErrorFrom(res.status, "", body, res.headers.get("Retry-After"));
      }
      return (await res.json()) as T;
    } catch (err) {
      // fetch rejects with a generic AbortError: give the reason instead.
      if (ctrl.signal.aborted && ctrl.signal.reason !== undefined && err !== ctrl.signal.reason) {
        throw ctrl.signal.reason;
      }
      throw err;
    } finally {
      if (timer !== undefined) clearTimeout(timer);
      signal?.removeEventListener("abort", onAbort);
    }
  }
}

/** A REST request took longer than `timeoutMs`. */
export class KlondikeTimeoutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "TimeoutError";
  }
}
