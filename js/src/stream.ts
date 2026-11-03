import type { ClientConfig } from "./client.js";
import { decodeCapnpMessage, decodeJsonMessage } from "./codec.js";
import { KlondikeApiError, KlondikeStreamError, apiErrorFrom } from "./errors.js";
import type {
  Channel,
  ClientMessage,
  ErrorMessage,
  Format,
  OrderbookMessage,
  OverviewMessage,
  PongMessage,
  StreamMessage,
  SubscribedMessage,
  Subscription,
  TradesMessage,
  UnsubscribedMessage,
} from "./types.js";

/** The part of the WHATWG WebSocket the stream uses (browsers, Node 22, the `ws` package). */
export interface WebSocketLike {
  readonly readyState: number;
  binaryType: string;
  onopen: ((ev: any) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  onerror: ((ev: any) => void) | null;
  onclose: ((ev: { code: number; reason: string }) => void) | null;
  send(data: string): void;
  close(code?: number, reason?: string): void;
}

/** A WebSocket class: the global `WebSocket`, or the default export of the `ws` package. */
export type WebSocketConstructor = new (url: string, ...args: any[]) => WebSocketLike;

export interface StreamOptions {
  /** Encoding of the server messages, `json` by default. The messages are the same objects in both. */
  format?: Format;
  /** Reopen a lost connection and subscribe again (default true). */
  reconnect?: boolean;
  /** Longest wait between two reconnections, ms (default 30 000; the first wait is 500 ms). */
  maxBackoffMs?: number;
  /**
   * WebSocket implementation. By default the global `WebSocket` (browsers,
   * Node >= 22), else the `ws` package. With `ws` the key goes in the
   * `X-API-Key` header, with any other in the `api_key` query parameter.
   */
  WebSocket?: WebSocketConstructor;
  /** Messages an async iterator buffers before it fails as too slow (default 10 000). */
  maxBuffered?: number;
}

/** Events of a stream and the argument of their listeners. */
export interface StreamEvents {
  trades: TradesMessage;
  orderbook: OrderbookMessage;
  overview: OverviewMessage;
  subscribed: SubscribedMessage;
  unsubscribed: UnsubscribedMessage;
  /** An error message of the server: it does not close the connection. */
  error: ErrorMessage;
  pong: PongMessage;
  /** Every server message. */
  message: StreamMessage;
  /** A connection is open (the first one, or after a reconnection); subscriptions were sent. */
  open: void;
  /** The connection was lost; the next attempt is in `delayMs`. */
  reconnecting: { delayMs: number; error: Error };
  /** The stream ended: `undefined` after close(), else the error that ended it (a refused key, for instance). */
  close: Error | undefined;
}

export type StreamEvent = keyof StreamEvents;
type Listener<K extends StreamEvent> = (arg: StreamEvents[K]) => void;

const INITIAL_BACKOFF_MS = 500;
/** Error codes refusing a subscription. */
const REFUSED_SUBSCRIPTION = new Set(["bad_request", "unknown_channel", "too_many_subscriptions"]);
const OPEN = 1;

interface Conn {
  ws: WebSocketLike;
  nodeWs: boolean;
  opened: boolean;
  /** Set once the connection is being dropped: its frames are ignored. */
  dead: boolean;
  /** Refused handshake (ws package only). */
  refused?: KlondikeApiError;
  error?: Error;
}

/**
 * A WebSocket stream of the API. It reconnects on its own with an exponential
 * backoff (0.5 s to 30 s) and subscribes again; a refused key (401) ends it.
 *
 * Read the messages with `for await (const m of stream)` or with listeners
 * (`stream.on("trades", m => ...)`); both get the same objects whatever the
 * format.
 */
export class KlondikeStream implements AsyncIterable<StreamMessage> {
  /** Encoding of the server messages. */
  readonly format: Format;

  readonly #config: ClientConfig;
  readonly #url: string;
  readonly #reconnect: boolean;
  readonly #maxBackoffMs: number;
  readonly #maxBuffered: number;
  readonly #ctor: WebSocketConstructor | undefined;

  readonly #subs = new Map<string, ClientMessage>();
  readonly #listeners = new Map<StreamEvent, Set<Listener<any>>>();
  readonly #queues = new Set<MessageQueue>();
  #readyWaiters: { resolve: () => void; reject: (err: Error) => void }[] = [];
  #closeWaiters: (() => void)[] = [];

  #conn: Conn | null = null;
  #timer: ReturnType<typeof setTimeout> | undefined;
  #backoffMs = INITIAL_BACKOFF_MS;
  #everOpened = false;
  #closed = false;
  #error: Error | undefined;

  /** @internal Use {@link KlondikeClient.stream}. */
  constructor(config: ClientConfig, options: StreamOptions = {}) {
    this.#config = config;
    this.format = options.format ?? "json";
    if (this.format !== "json" && this.format !== "capnp") {
      throw new TypeError(`KlondikeStream: format must be json or capnp, not ${String(this.format)}`);
    }
    this.#reconnect = options.reconnect ?? true;
    this.#maxBackoffMs = options.maxBackoffMs ?? 30_000;
    this.#maxBuffered = options.maxBuffered ?? 10_000;
    this.#ctor = options.WebSocket;
    this.#url = `${config.baseUrl.replace(/^http/, "ws")}/v1/ws?format=${this.format}`;
    this.#connect();
  }

  // --- public API -------------------------------------------------------------

  /** Streams the trades of the symbol, of the venues given (all when none). */
  subscribeTrades(symbol: string, options: { venues?: string[] } = {}): void {
    this.#subscribe({ op: "subscribe", channel: "trades", symbol, ...venues(options.venues) });
  }

  /** Streams the merged order book of the symbol: `depth` levels per side (1 to 200, 20 by default), of the venues given (all when none). */
  subscribeOrderbook(symbol: string, options: { depth?: number; venues?: string[] } = {}): void {
    const m: ClientMessage = { op: "subscribe", channel: "orderbook", symbol };
    if (options.depth !== undefined && options.depth > 0) m.depth = options.depth;
    this.#subscribe({ ...m, ...venues(options.venues) });
  }

  /** Streams the market overview. */
  subscribeOverview(): void {
    this.#subscribe({ op: "subscribe", channel: "overview" });
  }

  /** Stops a stream (no symbol for the overview). */
  unsubscribe(channel: Channel, symbol = ""): void {
    this.#checkOpen();
    this.#subs.delete(subKey(channel, symbol));
    const m: ClientMessage = { op: "unsubscribe", channel };
    if (symbol) m.symbol = symbol;
    this.#send(m);
  }

  /** Asks the server for a `pong` message (nothing is sent while reconnecting). */
  ping(): void {
    this.#checkOpen();
    this.#send({ op: "ping" });
  }

  /** The subscriptions, sent again after each reconnection. */
  get subscriptions(): Subscription[] {
    return [...this.#subs.values()].map((m) => ({ channel: m.channel!, symbol: m.symbol ?? "" }));
  }

  /** Whether the stream ended (close() or an error). */
  get closed(): boolean {
    return this.#closed;
  }

  /** The error that ended the stream, `undefined` while running or after close(). */
  get error(): Error | undefined {
    return this.#error;
  }

  /** Resolves when the first connection is open; rejects if the stream ends before (a refused key, an unreachable server). */
  ready(): Promise<void> {
    if (this.#everOpened) return Promise.resolve();
    if (this.#closed) return Promise.reject(this.#error ?? new KlondikeStreamError("klondike: stream closed"));
    return new Promise((resolve, reject) => this.#readyWaiters.push({ resolve, reject }));
  }

  /** Adds a listener. */
  on<K extends StreamEvent>(event: K, listener: Listener<K>): this {
    let set = this.#listeners.get(event);
    if (!set) this.#listeners.set(event, (set = new Set()));
    set.add(listener);
    return this;
  }

  /** Removes a listener. */
  off<K extends StreamEvent>(event: K, listener: Listener<K>): this {
    this.#listeners.get(event)?.delete(listener);
    return this;
  }

  /** Adds a listener called once. */
  once<K extends StreamEvent>(event: K, listener: Listener<K>): this {
    const wrapper: Listener<K> = (arg) => {
      this.off(event, wrapper);
      listener(arg);
    };
    return this.on(event, wrapper);
  }

  /**
   * Closes the connection and ends the stream: the iterators end, `close` is
   * emitted with `undefined`. Resolves once the socket is closed.
   */
  close(): Promise<void> {
    if (this.#closed) return this.#conn ? new Promise((r) => this.#closeWaiters.push(r)) : Promise.resolve();
    this.#end(undefined);
    return this.#conn ? new Promise((r) => this.#closeWaiters.push(r)) : Promise.resolve();
  }

  /**
   * The server messages. The loop ends after close() and throws the error
   * that ended the stream otherwise; leaving the loop closes the stream.
   */
  [Symbol.asyncIterator](): AsyncIterator<StreamMessage> {
    const q = new MessageQueue(this.#maxBuffered, () => {
      this.#queues.delete(q);
      void this.close();
    });
    if (this.#closed) q.end(this.#error);
    else this.#queues.add(q);
    return q;
  }

  // --- connection -------------------------------------------------------------

  #connect(): void {
    this.#timer = undefined;
    resolveWebSocket(this.#ctor).then(
      (Ctor) => this.#open(Ctor),
      (err) => this.#end(err),
    );
  }

  #open(Ctor: WebSocketConstructor): void {
    if (this.#closed) return;
    // The `ws` package is an EventEmitter and can send headers; the WHATWG
    // WebSocket cannot: the key then goes in the query.
    const nodeWs = typeof (Ctor.prototype as any)?.on === "function";
    let ws: WebSocketLike;
    try {
      if (nodeWs) {
        const headers: Record<string, string> = { "X-API-Key": this.#config.apiKey };
        if (this.#config.userAgent) headers["User-Agent"] = this.#config.userAgent;
        ws = new Ctor(this.#url, { headers });
      } else {
        ws = new Ctor(`${this.#url}&api_key=${encodeURIComponent(this.#config.apiKey)}`);
      }
      ws.binaryType = "arraybuffer";
    } catch (err) {
      this.#end(err as Error);
      return;
    }
    const conn: Conn = { ws, nodeWs, opened: false, dead: false };
    this.#conn = conn;
    if (nodeWs) {
      (ws as any).on("unexpected-response", (_req: any, res: any) => {
        let body = "";
        res.setEncoding("utf8");
        res.on("data", (chunk: string) => {
          if (body.length < 65536) body += chunk;
        });
        const done = () => {
          if (conn.refused) return;
          const retryAfter = res.headers?.["retry-after"];
          conn.refused = apiErrorFrom(res.statusCode, "", body, typeof retryAfter === "string" ? retryAfter : null);
          (ws as any).terminate();
        };
        res.on("end", done);
        res.on("error", done);
      });
    }
    ws.onopen = () => this.#onOpen(conn);
    ws.onmessage = (ev) => this.#onFrame(conn, ev.data);
    ws.onerror = (ev) => {
      conn.error ??= ev?.error instanceof Error ? ev.error : new KlondikeStreamError(ev?.message || "klondike: WebSocket error");
      // Node's WebSocket (undici) fires no close event after a refused
      // handshake: handle the failure now (a later close event is ignored).
      if (!conn.opened && !conn.dead) {
        conn.dead = true;
        try {
          ws.close();
        } catch {
          // not open
        }
        void this.#onClose(conn, 1006, "");
      }
    };
    ws.onclose = (ev) => void this.#onClose(conn, ev.code, ev.reason);
  }

  #onOpen(conn: Conn): void {
    conn.opened = true;
    if (this.#closed) {
      conn.ws.close(1000);
      return;
    }
    this.#everOpened = true;
    this.#backoffMs = INITIAL_BACKOFF_MS;
    for (const m of this.#subs.values()) this.#send(m);
    const waiters = this.#readyWaiters;
    this.#readyWaiters = [];
    for (const w of waiters) w.resolve();
    this.#emit("open", undefined);
  }

  #onFrame(conn: Conn, data: unknown): void {
    if (conn.dead || conn !== this.#conn || this.#closed) return;
    let m: StreamMessage | undefined;
    try {
      if (typeof data === "string") m = decodeJsonMessage(data);
      else if (data instanceof ArrayBuffer || ArrayBuffer.isView(data)) m = decodeCapnpMessage(data as ArrayBuffer);
      else throw new KlondikeStreamError(`klondike: unexpected frame data ${Object.prototype.toString.call(data)}`);
    } catch (err) {
      // As the Go client: an undecodable message drops the connection.
      conn.error = err as Error;
      conn.dead = true;
      conn.ws.close(4000, "decode error");
      return;
    }
    if (m === undefined) return; // a message type of a newer server: skipped
    if (m.type === "error" && m.channel && REFUSED_SUBSCRIPTION.has(m.code)) {
      // The server refused the subscription: do not send it again on reconnection.
      this.#subs.delete(subKey(m.channel, m.symbol ?? ""));
    }
    this.#dispatch(m);
  }

  async #onClose(conn: Conn, code: number, reason: string): Promise<void> {
    if (conn !== this.#conn) return;
    conn.dead = true;
    this.#conn = null;
    if (this.#closed) {
      const waiters = this.#closeWaiters;
      this.#closeWaiters = [];
      for (const w of waiters) w();
      return;
    }
    let err: Error;
    if (!conn.opened) {
      // Refused handshake: the ws package gives the answer; a WHATWG
      // WebSocket does not, a REST call with the key tells a refused key.
      err =
        conn.refused ??
        (conn.nodeWs ? undefined : await this.#probe()) ??
        new KlondikeStreamError(`klondike: WebSocket connection to ${this.#url} failed`, { cause: conn.error });
      if (this.#closed) return;
      // As the Go client, the first connection must succeed.
      if ((err instanceof KlondikeApiError && err.status === 401) || !this.#everOpened || !this.#reconnect) {
        this.#end(err);
        return;
      }
    } else {
      err =
        conn.error ??
        new KlondikeStreamError(`klondike: connection closed (${code}${reason ? ` ${reason}` : ""})`, { closeCode: code });
      // 1008: policy violation, the server will not take the connection back.
      if (!this.#reconnect || code === 1008) {
        this.#end(err);
        return;
      }
    }
    const delayMs = this.#backoffMs;
    this.#backoffMs = Math.min(this.#backoffMs * 2, this.#maxBackoffMs);
    this.#timer = setTimeout(() => this.#connect(), delayMs);
    this.#emit("reconnecting", { delayMs, error: err });
  }

  /** The error of a REST call with the key, undefined if it is accepted or the call fails. */
  async #probe(): Promise<KlondikeApiError | undefined> {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 10_000);
    try {
      const headers: Record<string, string> = { "X-API-Key": this.#config.apiKey };
      if (this.#config.userAgent) headers["User-Agent"] = this.#config.userAgent;
      const res = await this.#config.fetch(`${this.#config.baseUrl}/v1/symbols?q=~`, { headers, signal: ctrl.signal });
      if (res.status === 200) {
        await res.body?.cancel().catch(() => {});
        return undefined;
      }
      return apiErrorFrom(res.status, "", await res.text().catch(() => ""), res.headers.get("Retry-After"));
    } catch {
      return undefined;
    } finally {
      clearTimeout(timer);
    }
  }

  /** Ends the stream, with an error or after close(). */
  #end(err: Error | undefined): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#error = err;
    if (this.#timer !== undefined) {
      clearTimeout(this.#timer);
      this.#timer = undefined;
    }
    const conn = this.#conn;
    if (conn) {
      conn.dead = true;
      // A connection still opening may give no close event (Node's
      // WebSocket): do not wait for it.
      if (!conn.opened) this.#conn = null;
      try {
        conn.ws.close(1000);
      } catch {
        // already closing
      }
      if (!err && conn.nodeWs && conn.opened) {
        // ws waits up to 30 s for the server's close frame.
        const t = setTimeout(() => (conn.ws as any).terminate?.(), 2000);
        (t as any).unref?.();
      }
    }
    for (const q of this.#queues) q.end(err);
    this.#queues.clear();
    const waiters = this.#readyWaiters;
    this.#readyWaiters = [];
    for (const w of waiters) w.reject(err ?? new KlondikeStreamError("klondike: stream closed"));
    this.#emit("close", err);
  }

  // --- messages ---------------------------------------------------------------

  #subscribe(m: ClientMessage): void {
    this.#checkOpen();
    this.#subs.set(subKey(m.channel!, m.symbol ?? ""), m);
    this.#send(m);
  }

  /** Writes on the open connection; while (re)connecting it is a no-op: the subscriptions are sent on open. */
  #send(m: ClientMessage): void {
    const conn = this.#conn;
    if (!conn || !conn.opened || conn.dead || conn.ws.readyState !== OPEN) return;
    try {
      conn.ws.send(JSON.stringify(m));
    } catch {
      // the connection is closing: subscriptions are sent again on reconnection
    }
  }

  #checkOpen(): void {
    if (this.#closed) throw this.#error ?? new KlondikeStreamError("klondike: stream closed");
  }

  #dispatch(m: StreamMessage): void {
    for (const q of this.#queues) {
      if (!q.push(m)) {
        this.#queues.delete(q);
        q.end(new KlondikeStreamError(`klondike: more than ${this.#maxBuffered} messages waiting in the iterator, consumer too slow`));
      }
    }
    this.#emit(m.type, m as any);
    this.#emit("message", m);
  }

  #emit<K extends StreamEvent>(event: K, arg: StreamEvents[K]): void {
    const set = this.#listeners.get(event);
    if (!set) return;
    for (const l of [...set]) {
      try {
        l(arg);
      } catch (err) {
        // A listener's exception must not break the stream: report it as
        // uncaught, outside of the WebSocket callbacks.
        queueMicrotask(() => {
          throw err;
        });
      }
    }
  }
}

/** Buffers the messages of one async iterator. */
class MessageQueue implements AsyncIterator<StreamMessage> {
  #items: StreamMessage[] = [];
  #waiter: { resolve: (r: IteratorResult<StreamMessage>) => void; reject: (err: Error) => void } | null = null;
  #done = false;
  #error: Error | undefined;

  constructor(
    readonly max: number,
    readonly onReturn: () => void,
  ) {}

  /** False when the buffer is full. */
  push(m: StreamMessage): boolean {
    if (this.#done) return true;
    if (this.#waiter) {
      const w = this.#waiter;
      this.#waiter = null;
      w.resolve({ value: m, done: false });
      return true;
    }
    if (this.#items.length >= this.max) return false;
    this.#items.push(m);
    return true;
  }

  end(err: Error | undefined): void {
    if (this.#done) return;
    this.#done = true;
    this.#error = err;
    if (this.#waiter) {
      const w = this.#waiter;
      this.#waiter = null;
      if (err) w.reject(err);
      else w.resolve({ value: undefined, done: true });
    }
  }

  next(): Promise<IteratorResult<StreamMessage>> {
    if (this.#items.length > 0) return Promise.resolve({ value: this.#items.shift()!, done: false });
    if (this.#done) {
      if (this.#error) {
        const err = this.#error;
        this.#error = undefined; // thrown once
        return Promise.reject(err);
      }
      return Promise.resolve({ value: undefined, done: true });
    }
    return new Promise((resolve, reject) => (this.#waiter = { resolve, reject }));
  }

  return(): Promise<IteratorResult<StreamMessage>> {
    this.#items = [];
    this.end(undefined);
    this.onReturn();
    return Promise.resolve({ value: undefined, done: true });
  }
}

function subKey(channel: Channel, symbol: string): string {
  return `${channel}\u0000${symbol}`;
}

function venues(v: string[] | undefined): { venues?: string[] } {
  return v && v.length > 0 ? { venues: [...v] } : {};
}

let wsPackage: Promise<WebSocketConstructor> | undefined;

function resolveWebSocket(ctor: WebSocketConstructor | undefined): Promise<WebSocketConstructor> {
  if (ctor) return Promise.resolve(ctor);
  const global = (globalThis as any).WebSocket as WebSocketConstructor | undefined;
  if (typeof global === "function") return Promise.resolve(global);
  // Node < 22: the optional `ws` package. The specifier is a variable so that
  // bundlers do not try to resolve it for the browser.
  const specifier = "ws";
  wsPackage ??= import(/* @vite-ignore */ /* webpackIgnore: true */ specifier).then(
    (mod: any) => (mod.WebSocket ?? mod.default) as WebSocketConstructor,
    (err: unknown) => {
      wsPackage = undefined;
      throw new KlondikeStreamError(
        "klondike: no WebSocket implementation: install the ws package (npm install ws) or use Node >= 22",
        { cause: err },
      );
    },
  );
  return wsPackage;
}
