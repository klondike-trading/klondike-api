// The stream against a local mock server (http + the ws package): both
// WebSocket implementations, both formats, reconnection, refused keys.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createServer } from "node:http";
import { after, before, describe, test } from "node:test";
import { fileURLToPath } from "node:url";
import { once } from "node:events";

import WebSocket, { WebSocketServer } from "ws";

import { KlondikeApiError, KlondikeClient, decodeJsonMessage } from "../dist/index.js";

const KEY = "kl_0123456789abcdefghijklmnopqrstuv";
const dir = fileURLToPath(new URL("../../testdata/messages/", import.meta.url));
const fixture = (name) => ({ json: readFileSync(`${dir}${name}.json`, "utf8"), capnp: readFileSync(`${dir}${name}.capnp`) });
const TRADES = fixture("0-trades");
const BOOK = fixture("1-orderbook");
const PONG = fixture("6-pong");

/** A mock of the API: key check before the upgrade, acks, a fixture per subscription. */
class MockServer {
  validKeys = new Set([KEY]);
  connections = []; // { ws, format, key, received: [] }
  connectAttempts = 0;

  async start() {
    this.wss = new WebSocketServer({ noServer: true });
    this.http = createServer((req, res) => {
      const ok = this.validKeys.has(req.headers["x-api-key"]);
      res.writeHead(ok ? 200 : 401, { "Content-Type": "application/json" });
      res.end(ok ? '{"symbols":[]}' : '{"code":"unauthorized","error":"unknown API key"}');
    });
    this.http.on("upgrade", (req, socket, head) => {
      this.connectAttempts++;
      const url = new URL(req.url, "http://x");
      const key = req.headers["x-api-key"] || url.searchParams.get("api_key");
      if (url.pathname !== "/v1/ws" || !this.validKeys.has(key)) {
        const body = '{"code":"unauthorized","error":"unknown API key"}';
        socket.end(`HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: ${body.length}\r\nConnection: close\r\n\r\n${body}`);
        return;
      }
      this.wss.handleUpgrade(req, socket, head, (ws) => {
        const conn = { ws, format: url.searchParams.get("format"), key, header: !!req.headers["x-api-key"], received: [] };
        this.connections.push(conn);
        ws.on("message", (data) => {
          const m = JSON.parse(data.toString());
          conn.received.push(m);
          const send = (f) => (conn.format === "capnp" ? ws.send(f.capnp, { binary: true }) : ws.send(f.json));
          if (m.op === "ping") return send(PONG);
          if (m.symbol === "BAD/BAD") {
            const err = { type: "error", code: "bad_request", message: "symbol not supported", channel: m.channel, symbol: m.symbol };
            return ws.send(JSON.stringify(err));
          }
          const ack = { type: m.op === "subscribe" ? "subscribed" : "unsubscribed", channel: m.channel };
          if (m.symbol) ack.symbol = m.symbol;
          // A message type of a newer server, then the ack (text frames even
          // in capnp: the client must decode both).
          ws.send('{"type":"funding","rate":0.0001}');
          ws.send(JSON.stringify(ack));
          if (m.op === "subscribe" && m.channel === "trades") send(TRADES);
          if (m.op === "subscribe" && m.channel === "orderbook") send(BOOK);
        });
      });
    });
    this.http.listen(0, "127.0.0.1");
    await once(this.http, "listening");
    this.url = `http://127.0.0.1:${this.http.address().port}`;
  }

  async waitConnections(n) {
    while (this.connections.length < n) await new Promise((r) => setTimeout(r, 10));
  }

  async stop() {
    for (const c of this.wss.clients) c.terminate();
    this.http.closeAllConnections?.();
    await new Promise((r) => this.http.close(r));
  }
}

const server = new MockServer();
before(() => server.start());
after(() => server.stop());

const impls = [["ws package", WebSocket]];
if (typeof globalThis.WebSocket === "function") impls.push(["global WebSocket", globalThis.WebSocket]);

function nextOf(stream, type) {
  return new Promise((resolve) => stream.once(type, resolve));
}

for (const [implName, Impl] of impls) {
  describe(implName, () => {
    for (const format of ["json", "capnp"]) {
      test(`${format}: subscriptions, data, ping, iterator`, async () => {
        const n = server.connections.length;
        const client = new KlondikeClient({ apiKey: KEY, baseUrl: server.url });
        const stream = client.stream({ format, WebSocket: Impl });
        // Before the connection is open: sent on open.
        stream.subscribeTrades("BTC/USDT", { venues: ["binance", "okx"] });
        stream.subscribeOrderbook("BTC/USDT", { depth: 10 });
        const seen = [];
        stream.on("message", (m) => seen.push(m.type));
        const got = {};
        for await (const m of stream) {
          got[m.type] = m;
          if (m.type === "orderbook") stream.ping();
          if (m.type === "pong") break; // closes the stream
        }
        assert.equal(stream.closed, true);
        assert.equal(stream.error, undefined);
        assert.deepStrictEqual(got.trades, decodeJsonMessage(TRADES.json));
        assert.deepStrictEqual(got.orderbook, decodeJsonMessage(BOOK.json));
        assert.deepStrictEqual(got.subscribed, { type: "subscribed", channel: "orderbook", symbol: "BTC/USDT" });
        assert.equal(stream.error, undefined);
        assert.deepStrictEqual(seen, ["subscribed", "trades", "subscribed", "orderbook", "pong"]);

        const conn = server.connections[n];
        assert.equal(conn.format, format);
        assert.equal(conn.header, Impl === WebSocket, "the key in the header with ws, in the query otherwise");
        assert.deepStrictEqual(conn.received, [
          { op: "subscribe", channel: "trades", symbol: "BTC/USDT", venues: ["binance", "okx"] },
          { op: "subscribe", channel: "orderbook", symbol: "BTC/USDT", depth: 10 },
          { op: "ping" },
        ]);
        assert.throws(() => stream.subscribeOverview(), /closed/);
      });
    }

    test("reconnects and subscribes again", async () => {
      const n = server.connections.length;
      const stream = new KlondikeClient({ apiKey: KEY, baseUrl: server.url }).stream({ WebSocket: Impl });
      let opens = 0;
      stream.on("open", () => opens++);
      const errors = [];
      stream.on("error", (e) => errors.push(e));
      stream.subscribeTrades("ETH/USDT");
      stream.subscribeOverview();
      stream.subscribeOrderbook("SOL/USDC");
      stream.subscribeTrades("BAD/BAD"); // refused by the server: not sent again
      await stream.ready();
      await server.waitConnections(n + 1);
      await nextOf(stream, "trades");
      stream.unsubscribe("orderbook", "SOL/USDC");
      await nextOf(stream, "unsubscribed");
      assert.deepStrictEqual(errors, [
        { type: "error", code: "bad_request", message: "symbol not supported", channel: "trades", symbol: "BAD/BAD" },
      ]);
      assert.equal(opens, 1, "unknown message types do not drop the connection");

      const reconnecting = nextOf(stream, "reconnecting");
      server.connections[n].ws.close(1001, "restart");
      const r = await reconnecting;
      assert.equal(r.delayMs, 500);
      assert.match(r.error.message, /1001/);
      await nextOf(stream, "open");
      await nextOf(stream, "trades");
      const received = server.connections[n + 1].received;
      assert.equal(received.length, 2);
      assert.deepStrictEqual(
        [...received].sort((a, b) => a.channel.localeCompare(b.channel)),
        [{ op: "subscribe", channel: "overview" }, { op: "subscribe", channel: "trades", symbol: "ETH/USDT" }],
      );
      assert.deepStrictEqual(stream.subscriptions.map((s) => s.channel).sort(), ["overview", "trades"]);
      await stream.close();
      assert.equal(stream.closed, true);
    });

    test("a refused key ends the stream with a 401", async () => {
      const stream = new KlondikeClient({ apiKey: "kl_badbadbadbadbadbadbadbadbadbadba", baseUrl: server.url }).stream({ WebSocket: Impl });
      stream.subscribeTrades("BTC/USDT");
      const closed = nextOf(stream, "close");
      await assert.rejects(stream.ready(), (e) => e instanceof KlondikeApiError && e.status === 401 && e.code === "unauthorized");
      const err = await closed;
      assert.equal(err.status, 401);
      assert.equal(stream.error, err);
      await assert.rejects(
        (async () => {
          for await (const _ of stream);
        })(),
        { status: 401 },
      );
    });

    test("a key revoked while connected ends the stream on reconnection", async () => {
      const key = "kl_revokedrevokedrevokedrevokedre";
      server.validKeys.add(key);
      const n = server.connections.length;
      const stream = new KlondikeClient({ apiKey: key, baseUrl: server.url }).stream({ WebSocket: Impl, maxBackoffMs: 100 });
      await stream.ready();
      await server.waitConnections(n + 1);
      server.validKeys.delete(key);
      const closed = nextOf(stream, "close");
      server.connections[n].ws.terminate();
      const err = await closed;
      assert.ok(err instanceof KlondikeApiError);
      assert.equal(err.status, 401);
    });
  });
}

test("unreachable server: the first connection fails the stream", async () => {
  const stream = new KlondikeClient({ apiKey: KEY, baseUrl: "http://127.0.0.1:9" }).stream({ WebSocket });
  await assert.rejects(stream.ready(), { name: "KlondikeStreamError" });
});

test("close() before the connection opens", async () => {
  const stream = new KlondikeClient({ apiKey: KEY, baseUrl: server.url }).stream({ WebSocket });
  const events = [];
  stream.on("close", (e) => events.push(["close", e]));
  stream.on("open", () => events.push(["open"]));
  await stream.close();
  await assert.rejects(stream.ready(), /closed/);
  await new Promise((r) => setTimeout(r, 50));
  assert.deepStrictEqual(events, [["close", undefined]]);
});

test("reconnect: false ends the stream on the first loss", async () => {
  const n = server.connections.length;
  const stream = new KlondikeClient({ apiKey: KEY, baseUrl: server.url }).stream({ WebSocket, reconnect: false });
  await stream.ready();
  await server.waitConnections(n + 1);
  const closed = nextOf(stream, "close");
  server.connections[n].ws.close(4001, "slow consumer");
  const err = await closed;
  assert.equal(err.name, "KlondikeStreamError");
  assert.equal(err.closeCode, 4001);
});
