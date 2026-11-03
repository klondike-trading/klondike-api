// End to end against a running API, skipped unless KLONDIKE_TEST_URL is set:
//   KLONDIKE_TEST_URL=http://localhost:8093 KLONDIKE_TEST_KEY=kl_... npm test
// (api-go/dev.sh starts a local stack whose fakefeed publishes BTC/USDT,
// ETH/USDT, BTC/USDT:USDT and SOL/USDC).
import assert from "node:assert/strict";
import { describe, test } from "node:test";

import WebSocket from "ws";

import { KlondikeApiError, KlondikeClient } from "../dist/index.js";

const url = process.env.KLONDIKE_TEST_URL;
const apiKey = process.env.KLONDIKE_TEST_KEY ?? "kl_dev0dev0dev0dev0dev0dev0dev0dev0";
const skip = !url && "KLONDIKE_TEST_URL is not set";
const SYMBOL = process.env.KLONDIKE_TEST_SYMBOL ?? "BTC/USDT";

describe("e2e REST", { skip }, () => {
  const client = new KlondikeClient({ apiKey, baseUrl: url });

  test("symbols", async () => {
    const symbols = await client.symbols();
    assert.ok(symbols.some((s) => s.symbol === SYMBOL), JSON.stringify(symbols));
    for (const s of symbols) {
      assert.ok(["spot", "perp"].includes(s.market));
      assert.ok(Array.isArray(s.venues) && s.venues.length > 0);
      assert.equal(typeof s.lastTradeTs, "number");
    }
    const usdc = await client.symbols("/usdc");
    assert.ok(usdc.every((s) => s.symbol.toLowerCase().includes("/usdc")));
  });

  test("trades", async () => {
    const t = await client.trades(SYMBOL, { limit: 5 });
    assert.equal(t.symbol, SYMBOL);
    assert.ok(t.trades.length > 0 && t.trades.length <= 5);
    for (const tr of t.trades) {
      assert.ok(["buy", "sell", "unknown"].includes(tr.side));
      assert.ok(tr.price > 0 && tr.qty > 0 && tr.ts > 0);
    }
    const venue = t.trades[0].venue;
    const only = await client.trades(SYMBOL, { venues: [venue], since: t.trades[0].ts - 1 });
    assert.ok(only.trades.every((tr) => tr.venue === venue && tr.ts >= t.trades[0].ts));
  });

  test("orderbook", async () => {
    const b = await client.orderbook(SYMBOL, { depth: 5 });
    assert.equal(b.symbol, SYMBOL);
    assert.ok(b.bids.length > 0 && b.bids.length <= 5);
    assert.ok(b.asks.length > 0 && b.asks.length <= 5);
    assert.ok(b.bids.every((l, i) => i === 0 || l.price <= b.bids[i - 1].price), "bids best (highest) first");
    assert.ok(b.asks.every((l, i) => i === 0 || l.price >= b.asks[i - 1].price), "asks best (lowest) first");
    const names = new Set(b.venues.map((v) => v.venue));
    assert.ok([...b.bids, ...b.asks].every((l) => names.has(l.venue)));
  });

  test("overview", async () => {
    const o = await client.overview();
    assert.equal(typeof o.ts, "number");
    assert.ok(Array.isArray(o.pairs) && o.pairs.length > 0);
    assert.ok(o.feeds && typeof o.feeds.booksLive === "number");
  });

  test("errors", async () => {
    await assert.rejects(client.trades(""), (e) => e instanceof KlondikeApiError && e.status === 400 && e.code === "bad_request");
    await assert.rejects(client.orderbook(SYMBOL, { depth: 5000 }), { status: 400 });
    const bad = new KlondikeClient({ apiKey: "kl_00000000000000000000000000000000", baseUrl: url });
    await assert.rejects(bad.symbols(), { status: 401, code: "unauthorized" });
  });
});

const impls = [["ws package", WebSocket]];
if (typeof globalThis.WebSocket === "function") impls.push(["global WebSocket", globalThis.WebSocket]);

for (const [name, Impl] of impls) {
  describe(`e2e stream, ${name}`, { skip }, () => {
    const client = new KlondikeClient({ apiKey, baseUrl: url });

    for (const format of ["json", "capnp"]) {
      test(`${format}: trades, orderbook and overview`, { timeout: 30_000 }, async () => {
        const stream = client.stream({ format, WebSocket: Impl });
        stream.subscribeTrades(SYMBOL);
        stream.subscribeOrderbook(SYMBOL, { depth: 5 });
        stream.subscribeOverview();
        const acks = [];
        stream.on("subscribed", (m) => acks.push(`${m.channel} ${m.symbol ?? ""}`.trim()));
        const got = {};
        let pinged = false;
        for await (const m of stream) {
          got[m.type] ??= m;
          if (got.trades && got.orderbook && got.overview && !pinged) {
            pinged = true;
            stream.ping();
          }
          if (m.type === "pong") break;
        }
        assert.equal(stream.error, undefined);
        assert.deepStrictEqual(acks.sort(), ["orderbook BTC/USDT", "overview", "trades BTC/USDT"].map((s) => s.replace("BTC/USDT", SYMBOL)).sort());

        assert.equal(got.trades.symbol, SYMBOL);
        assert.ok(got.trades.trades.length > 0);
        const tr = got.trades.trades[0];
        assert.ok(tr.price > 0 && tr.ts > 1e12 && ["buy", "sell", "unknown"].includes(tr.side));

        const b = got.orderbook;
        assert.equal(b.symbol, SYMBOL);
        assert.ok(b.bids.length > 0 && b.bids.length <= 5 && b.asks.length <= 5);
        const names = new Set(b.venues.map((v) => v.venue));
        assert.ok([...b.bids, ...b.asks].every((l) => names.has(l.venue)), "level venues are names");
        for (const v of b.venues) {
          assert.ok(v.ts === null || v.ts > 1e12);
          for (const k of ["bid", "bidQty", "ask", "askQty"]) assert.ok(v[k] === null || Number.isFinite(v[k]));
        }

        assert.ok(got.overview.overview.pairs.length > 0);
        assert.equal(stream.closed, true);
      });
    }

    test("refused key", async () => {
      const bad = new KlondikeClient({ apiKey: "kl_00000000000000000000000000000000", baseUrl: url });
      const stream = bad.stream({ WebSocket: Impl });
      await assert.rejects(stream.ready(), (e) => e instanceof KlondikeApiError && e.status === 401);
      assert.equal(stream.closed, true);
    });

    test("server error messages do not close the connection", { timeout: 15_000 }, async () => {
      const stream = client.stream({ WebSocket: Impl });
      await stream.ready();
      stream.subscribeOrderbook(SYMBOL, { depth: 1000 }); // out of 1..200
      const err = await new Promise((r) => stream.once("error", r));
      assert.equal(err.code, "bad_request");
      assert.equal(err.channel, "orderbook");
      assert.deepStrictEqual(stream.subscriptions, [], "a refused subscription is not sent again");
      stream.ping();
      await new Promise((r) => stream.once("pong", r));
      await stream.close();
    });
  });
}
