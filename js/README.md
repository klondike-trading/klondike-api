# Klondike API — JavaScript / TypeScript client

Trades, merged order books (every level with its venue) and the market overview of the crypto pairs
Klondike consolidates across about forty venues, over REST and WebSocket, in JSON or Cap'n Proto.

```sh
npm install @klondike-trading/klondike
# Node 18 / 20 only, for the WebSocket streams (Node >= 22 and browsers have a global WebSocket):
npm install ws
```

ESM with TypeScript declarations, for Node 18 or later and browsers.
Documentation: https://klondike-trading.github.io/klondike-api/

## API key

You create keys on the [portal](https://api.klondike-crypto.com/portal/) after signing in. A key is
`kl_` followed by 32 characters. Keep it on the server side: a key used in a web page can be
read by anyone who opens that page.

```js
import { KlondikeClient } from "@klondike-trading/klondike";

const client = new KlondikeClient({ apiKey: process.env.KLONDIKE_API_KEY });
// Options: baseUrl (https://api.klondike-crypto.com by default), fetch (the global one by
// default), timeoutMs (30 000 by default, for REST calls).
```

## REST

```js
// Merged order book: best bid/ask of every venue, 10 levels per side, each with its venue.
const book = await client.orderbook("BTC/USDT", { depth: 10 });
for (const l of book.bids) console.log(l.venue, l.price, l.qty);
for (const v of book.venues) console.log(v.venue, v.bid, v.ask); // null when the venue has none

// The last trades of every venue (or of some of them), oldest first.
const { trades } = await client.trades("ETH/USDT:USDT", { limit: 100, venues: ["binanceusdm", "bybit"] });
const recent = await client.trades("BTC/USDT", { since: Date.now() - 5_000 });

// Market overview: cost of 100 k$ / 1 M$ / 10 M$ orders, depth, crosses, implied prices.
const overview = await client.overview();
for (const p of overview.pairs) console.log(p.symbol, p.mid, p.depth10Usd);

// Symbols with recent trades, filtered on a case insensitive substring.
const symbols = await client.symbols("usdc");
```

Every call takes a `signal` (`{ signal: AbortSignal }`) as well. Symbols are unified CCXT symbols
(`BTC/USDT` spot, `BTC/USDT:USDT` linear perpetual, `BTC/USD:BTC` inverse perpetual), venues CCXT
exchange ids, timestamps milliseconds since the epoch.

An error answer of the API rejects with a `KlondikeApiError`:

```js
import { KlondikeApiError } from "@klondike-trading/klondike";

try {
  await client.orderbook("FOO/BAR");
} catch (err) {
  if (err instanceof KlondikeApiError) {
    // status: 400, 401, 404, 429, 503...; code: bad_request, unauthorized, not_found, rate_limited...
    console.log(err.status, err.code, err.message, err.retryAfter); // retryAfter: seconds, on a 429
  }
}
```

## WebSocket streams

```js
const stream = client.stream({ format: "capnp" }); // or "json" (the default): the messages are the same

stream.subscribeTrades("BTC/USDT");
stream.subscribeOrderbook("BTC/USDT", { depth: 20, venues: ["binance", "coinbase", "kraken"] });
stream.subscribeOverview();

for await (const m of stream) {
  switch (m.type) {
    case "trades":
      console.log(m.symbol, m.trades.length, "trades");
      break;
    case "orderbook":
      console.log(m.symbol, m.bids[0], m.asks[0]);
      break;
    case "overview":
      console.log(m.overview.pairs.length, "pairs");
      break;
    case "error":
      console.warn(m.code, m.message); // the connection stays open
      break;
  }
}
```

Or with listeners:

```js
stream
  .on("trades", (m) => console.log(m.trades))
  .on("orderbook", (m) => console.log(m.bids.length, m.asks.length))
  .on("overview", (m) => console.log(m.overview.ts))
  .on("subscribed", (m) => console.log("subscribed", m.channel, m.symbol)) // no symbol for the overview
  .on("error", (m) => console.warn(m.code, m.message, m.channel, m.symbol))
  .on("pong", () => console.log("pong"))
  .on("message", (m) => {}) // every server message
  .on("open", () => {}) // each connection, the first one and after a reconnection
  .on("reconnecting", ({ delayMs, error }) => console.warn(error.message, "retry in", delayMs))
  .on("close", (err) => console.log("ended", err)); // undefined after close()

await stream.ready(); // the first connection is open (rejects on a refused key)
stream.ping();
stream.unsubscribe("orderbook", "BTC/USDT");
stream.unsubscribe("overview");
await stream.close();
```

- The connection opens as soon as `client.stream()` returns. A subscription made before it is open
  is sent when it opens.
- A lost connection is reopened with an exponential backoff, 0.5 s to 30 s (`maxBackoffMs`), and
  the subscriptions are sent again. The stream ends only on `close()`, on a refused key
  (`KlondikeApiError` with status 401), or when the first connection fails. `reconnect: false`
  ends it at the first loss instead. The `for await` loop then ends after `close()`, or throws the
  error; leaving the loop closes the stream.
- A subscription the server refuses (an `error` message that names the channel) is not sent again
  after a reconnection. A message type this version does not know is skipped.
- The messages are the same objects in `json` and in `capnp`. Cap'n Proto frames are about
  three times smaller on an order book and are decoded without parsing text.
- WebSocket implementation: the global `WebSocket` when there is one (browsers, Node >= 22), with
  the key in the `api_key` query parameter, since browsers cannot set headers. Otherwise the
  [`ws`](https://www.npmjs.com/package/ws) package, with the `X-API-Key` header. To pick one,
  pass `WebSocket`: `client.stream({ WebSocket: (await import("ws")).default })`.

### Node

`examples/stream-trades.mjs` streams the trades of a symbol:

```sh
KLONDIKE_API_KEY=kl_... node examples/stream-trades.mjs BTC/USDT capnp
```

### Browser

With a bundler (Vite, webpack, esbuild...), `import { KlondikeClient } from "@klondike-trading/klondike"`
as in Node. Without a bundler, use an ESM CDN:

```html
<pre id="out"></pre>
<script type="module">
  import { KlondikeClient } from "https://esm.sh/@klondike-trading/klondike@0.1.0";

  const client = new KlondikeClient({ apiKey: "kl_..." }); // visible to anyone who opens the page
  const book = await client.orderbook("BTC/USDT", { depth: 5 });
  console.log(book.bids, book.asks);

  const stream = client.stream({ format: "capnp" }); // or "json"
  stream.subscribeOrderbook("BTC/USDT", { depth: 5 });
  stream.on("orderbook", (m) => {
    document.getElementById("out").textContent = m.asks
      .slice()
      .reverse()
      .concat(m.bids)
      .map((l) => `${l.price.toFixed(2).padStart(12)} ${l.qty.toFixed(4).padStart(10)} ${l.venue}`)
      .join("\n");
  });
  stream.on("close", (err) => err && console.error(err));
</script>
```

The REST endpoints allow any origin (CORS). Leaving the page closes the WebSocket.

## Types

Every model is exported as a TypeScript type, with the field names of the JSON documents:
`Trade`, `Trades`, `SymbolInfo`, `VenueQuote`, `Level`, `Orderbook`, `Overview`, `Feeds`, `Pair`,
`Impact`, `Leader`, `Implied`, `Peg`, `ImpliedFX`, `Triangle`, `Path`, and the stream messages
`StreamMessage` (`TradesMessage`, `OrderbookMessage`, `OverviewMessage`, `SubscribedMessage`,
`UnsubscribedMessage`, `ErrorMessage`, `PongMessage`). `decodeMessage(data)` decodes a server frame
(a string for JSON, bytes for Cap'n Proto), for a WebSocket client of your own.

## Development

```sh
npm install
npm test                 # builds with tsc, then node --test
npm run gen:capnp        # regenerates src/capnp/klondike_api.ts from ../schema/klondike_api.capnp (needs capnp)
```

The tests decode each message of [`testdata/messages`](https://github.com/klondike-trading/klondike-api/tree/main/testdata/messages) in both formats and
check that they give the same object. They also run the REST calls against a mocked `fetch` and
the stream against a mock server. The end-to-end tests run against a real server when
`KLONDIKE_TEST_URL` is set:

```sh
(cd ../../api-go && HTTP_PORT=8093 ./dev.sh) &   # in the Klondike monorepo: local stack with simulated data
KLONDIKE_TEST_URL=http://localhost:8093 KLONDIKE_TEST_KEY=kl_dev0dev0dev0dev0dev0dev0dev0dev0 npm test
```

## License

MIT
