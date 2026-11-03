// The fixtures of ../testdata/messages are the same server messages encoded
// by the Go library in both formats: <n>-<type>.json (format=json) and
// <n>-<type>.capnp (packed Cap'n Proto). Both must decode to the same object.
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { Message } from "capnp-es";

import { decodeCapnpMessage, decodeJsonMessage, decodeMessage } from "../dist/index.js";
import { ServerMessage } from "../dist/capnp/klondike_api.js";

const dir = fileURLToPath(new URL("../../testdata/messages/", import.meta.url));
const names = [...new Set(readdirSync(dir).map((f) => f.replace(/\.(json|capnp)$/, "")))].sort();

test("fixtures exist", () => {
  assert.ok(names.length >= 7, `fixtures in ${dir}`);
  for (const t of ["trades", "orderbook", "overview", "subscribed", "unsubscribed", "error", "pong"]) {
    assert.ok(names.some((n) => n.endsWith(`-${t}`)), `fixture of ${t}`);
  }
});

for (const name of names) {
  test(`fixture ${name}: json and capnp decode to equal objects`, () => {
    const json = readFileSync(`${dir}${name}.json`, "utf8");
    const capnp = readFileSync(`${dir}${name}.capnp`); // a Buffer
    const fromJson = decodeJsonMessage(json);
    const fromCapnp = decodeCapnpMessage(capnp);
    assert.deepStrictEqual(fromCapnp, fromJson);
    assert.equal(fromJson.type, name.split("-")[1]);

    // The same through decodeMessage, from an ArrayBuffer as a browser WebSocket gives it.
    const ab = capnp.buffer.slice(capnp.byteOffset, capnp.byteOffset + capnp.byteLength);
    assert.deepStrictEqual(decodeMessage(ab), fromJson);
    assert.deepStrictEqual(decodeMessage(json), fromJson);

    // Nothing of the JSON document is lost.
    const raw = JSON.parse(json);
    if (raw.type === "overview") assert.deepStrictEqual(fromJson.overview, raw.overview);
    else assert.deepStrictEqual(fromJson, raw);
  });
}

test("capnp: missing numbers and timestamps are null, level venues are names", () => {
  const m = decodeCapnpMessage(readFileSync(`${dir}1-orderbook.capnp`));
  assert.equal(m.type, "orderbook");
  const kraken = m.venues.find((v) => v.venue === "kraken");
  assert.equal(kraken.ask, null);
  assert.equal(kraken.askQty, null);
  assert.equal(typeof kraken.ts, "number");
  const gemini = m.venues.find((v) => v.venue === "gemini");
  assert.deepStrictEqual(gemini, { venue: "gemini", ts: null, bid: null, bidQty: null, ask: null, askQty: null });
  assert.deepStrictEqual(m.bids.map((l) => l.venue), ["binance", "kraken"]);
});

test("error message: empty channel and symbol are left out", () => {
  assert.deepStrictEqual(decodeJsonMessage('{"type":"error","code":"bad_request","message":"invalid JSON","channel":"","symbol":""}'), {
    type: "error",
    code: "bad_request",
    message: "invalid JSON",
  });
});

test("subscription acknowledgements: no symbol for the overview", () => {
  const expected = { type: "unsubscribed", channel: "overview" };
  assert.deepStrictEqual(decodeJsonMessage('{"type":"unsubscribed","channel":"overview"}'), expected);
  assert.deepStrictEqual(decodeJsonMessage('{"type":"unsubscribed","channel":"overview","symbol":""}'), expected);
  assert.deepStrictEqual(decodeCapnpMessage(readFileSync(`${dir}4-unsubscribed.capnp`)), expected);
});

test("unknown message types are skipped (undefined), not errors", () => {
  assert.equal(decodeJsonMessage('{"type":"funding","symbol":"BTC/USDT:USDT"}'), undefined);
  // A union case after pong (@6), as a newer schema would add.
  const msg = new Message();
  const root = msg.initRoot(ServerMessage);
  root.pong = undefined;
  new DataView(msg.getSegment(0).buffer).setUint16(8, 7, true); // discriminant of the root struct
  assert.equal(decodeCapnpMessage(new Uint8Array(msg.toPackedArrayBuffer())), undefined);
  root.pong = undefined;
  assert.deepStrictEqual(decodeCapnpMessage(new Uint8Array(msg.toPackedArrayBuffer())), { type: "pong" });
});

test("corrupt frames are errors", () => {
  assert.throws(() => decodeJsonMessage("{"), { name: "KlondikeDecodeError", message: /invalid JSON/ });
  assert.throws(() => decodeJsonMessage("[1]"), { name: "KlondikeDecodeError" });
  assert.throws(() => decodeJsonMessage('{"symbol":"BTC/USDT"}'), { name: "KlondikeDecodeError" });
  assert.throws(() => decodeCapnpMessage(new Uint8Array([1, 2, 3])), { name: "KlondikeDecodeError" });
});
