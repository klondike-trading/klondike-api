// REST calls against a mocked fetch.
import assert from "node:assert/strict";
import { test } from "node:test";

import { KlondikeApiError, KlondikeClient } from "../dist/index.js";

const KEY = "kl_0123456789abcdefghijklmnopqrstuv";

/** A fetch answering `responses` in order and recording the requests. */
function mockFetch(...responses) {
  const calls = [];
  const fetch = async (url, init) => {
    calls.push({ url: new URL(url), init });
    const r = responses[Math.min(calls.length - 1, responses.length - 1)];
    if (r instanceof Error) throw r;
    const { status = 200, body = {}, headers = {} } = r;
    return new Response(typeof body === "string" ? body : JSON.stringify(body), {
      status,
      headers: { "Content-Type": "application/json", ...headers },
    });
  };
  return { fetch, calls };
}

test("symbols", async () => {
  const symbols = [{ symbol: "BTC/USDT", market: "spot", venues: ["binance", "okx"], lastTradeTs: 1793706479654 }];
  const m = mockFetch({ body: { symbols } });
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://localhost:1/", fetch: m.fetch });
  assert.deepStrictEqual(await client.symbols("btc"), symbols);
  const { url, init } = m.calls[0];
  assert.equal(url.origin + url.pathname, "http://localhost:1/v1/symbols");
  assert.equal(url.searchParams.get("q"), "btc");
  assert.equal(init.method, "GET");
  assert.equal(init.headers["X-API-Key"], KEY);
  assert.equal(init.headers.Accept, "application/json");
  assert.match(init.headers["User-Agent"], /^klondike-js\/0\.1\.0$/);

  await client.symbols();
  assert.equal(m.calls[1].url.search, "");
});

test("default base URL", async () => {
  const m = mockFetch({ body: { symbols: [] } });
  await new KlondikeClient({ apiKey: KEY, fetch: m.fetch }).symbols();
  assert.equal(m.calls[0].url.href, "https://api.klondike-crypto.com/v1/symbols");
});

test("trades with options", async () => {
  const trades = { symbol: "BTC/USDT:USDT", trades: [{ ts: 1, venue: "bybit", price: 1.5, qty: 2, side: "sell" }] };
  const m = mockFetch({ body: trades });
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: m.fetch });
  assert.deepStrictEqual(await client.trades("BTC/USDT:USDT", { limit: 10, since: 1793706479000, venues: ["binanceusdm", "bybit"] }), trades);
  const q = m.calls[0].url.searchParams;
  assert.equal(m.calls[0].url.pathname, "/v1/trades");
  assert.equal(q.get("symbol"), "BTC/USDT:USDT");
  assert.equal(q.get("limit"), "10");
  assert.equal(q.get("since"), "1793706479000");
  assert.equal(q.get("venues"), "binanceusdm,bybit");
  assert.match(m.calls[0].url.search, /symbol=BTC%2FUSDT%3AUSDT/);

  await client.trades("ETH/USDT");
  assert.deepStrictEqual([...m.calls[1].url.searchParams.keys()], ["symbol"]);
});

test("orderbook with options", async () => {
  const book = {
    symbol: "BTC/USDT",
    ts: 2,
    venues: [{ venue: "kraken", ts: null, bid: 1, bidQty: 1, ask: null, askQty: null }],
    bids: [{ price: 1, qty: 1, venue: "kraken" }],
    asks: [],
  };
  const m = mockFetch({ body: book });
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: m.fetch });
  assert.deepStrictEqual(await client.orderbook("BTC/USDT", { depth: 5, venues: ["kraken"] }), book);
  const q = m.calls[0].url.searchParams;
  assert.equal(m.calls[0].url.pathname, "/v1/orderbook");
  assert.equal(q.get("depth"), "5");
  assert.equal(q.get("venues"), "kraken");
});

test("overview", async () => {
  const overview = { ts: 1, windowsFromTs: 0, feeds: { venues: 1, venuesKnown: 2, booksLive: 3, lagMsP50: 4 }, pairs: [], leaders: [], implied: { pegs: [], fx: [], triangles: [] }, paths: [] };
  const m = mockFetch({ body: overview });
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: m.fetch });
  assert.deepStrictEqual(await client.overview(), overview);
  assert.equal(m.calls[0].url.pathname, "/v1/overview");
});

test("error mapping: JSON body", async () => {
  const cases = [
    { status: 400, body: { code: "bad_request", error: "symbol is required" } },
    { status: 401, body: { code: "unauthorized", error: "unknown API key" } },
    { status: 404, body: { code: "not_found", error: "no book for FOO/BAR" } },
    { status: 503, body: { code: "unavailable", error: "no overview yet" } },
  ];
  for (const c of cases) {
    const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: mockFetch(c).fetch });
    const err = await client.orderbook("FOO/BAR").catch((e) => e);
    assert.ok(err instanceof KlondikeApiError, String(err));
    assert.ok(err instanceof Error);
    assert.equal(err.name, "KlondikeApiError");
    assert.equal(err.status, c.status);
    assert.equal(err.code, c.body.code);
    assert.equal(err.message, c.body.error);
    assert.equal(err.retryAfter, undefined);
  }
});

test("error mapping: 429 with Retry-After", async () => {
  const m = mockFetch({ status: 429, body: { code: "rate_limited", error: "rate limit exceeded" }, headers: { "Retry-After": "3" } });
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: m.fetch });
  await assert.rejects(client.trades("BTC/USDT"), {
    name: "KlondikeApiError",
    status: 429,
    code: "rate_limited",
    message: "rate limit exceeded",
    retryAfter: 3,
  });
});

test("error mapping: body that is not JSON", async () => {
  const m = mockFetch({ status: 502, body: "<html>bad gateway</html>\n", headers: { "Content-Type": "text/html" } });
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: m.fetch });
  await assert.rejects(client.symbols(), { status: 502, code: "Bad Gateway", message: "<html>bad gateway</html>" });
  const empty = mockFetch({ status: 500, body: "" });
  await assert.rejects(new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: empty.fetch }).symbols(), {
    status: 500,
    code: "Internal Server Error",
  });
});

test("network errors pass through", async () => {
  const boom = new TypeError("fetch failed");
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: mockFetch(boom).fetch });
  await assert.rejects(client.symbols(), (e) => e === boom);
});

test("abort signal and timeout", async () => {
  const hanging = (_url, init) =>
    new Promise((_resolve, reject) => init.signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError"))));
  const ctrl = new AbortController();
  const client = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: hanging });
  const p = client.symbols(undefined, { signal: ctrl.signal });
  ctrl.abort(new Error("stop"));
  await assert.rejects(p, { message: "stop" });

  const slow = new KlondikeClient({ apiKey: KEY, baseUrl: "http://h", fetch: hanging, timeoutMs: 20 });
  await assert.rejects(slow.overview(), { name: "TimeoutError" });
});

test("apiKey is required", () => {
  assert.throws(() => new KlondikeClient({}), TypeError);
});
