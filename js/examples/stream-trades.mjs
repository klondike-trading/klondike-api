// Streams the trades of a symbol on every venue.
//
//   npm install @klondike-trading/klondike          (and `ws` on Node < 22)
//   KLONDIKE_API_KEY=kl_... node examples/stream-trades.mjs BTC/USDT capnp
//
// KLONDIKE_URL points to another server (http://localhost:8093 for the local
// stack of api-go/dev.sh). Ctrl-C stops.
import { KlondikeClient } from "@klondike-trading/klondike";

const symbol = process.argv[2] ?? "BTC/USDT";
const format = process.argv[3] ?? "json"; // or capnp: the messages are the same

const client = new KlondikeClient({
  apiKey: process.env.KLONDIKE_API_KEY,
  baseUrl: process.env.KLONDIKE_URL, // production when unset
});
const stream = client.stream({ format });
stream.subscribeTrades(symbol);

stream.on("subscribed", (m) => console.error(`subscribed to ${m.channel} ${m.symbol ?? ""}`));
stream.on("error", (m) => console.error(`server error ${m.code}: ${m.message}`));
stream.on("reconnecting", ({ delayMs, error }) => console.error(`${error.message}, reconnecting in ${delayMs} ms`));
process.on("SIGINT", () => stream.close());

try {
  for await (const m of stream) {
    if (m.type !== "trades") continue;
    for (const t of m.trades) {
      const time = new Date(t.ts).toISOString().slice(11, 23);
      console.log(`${time} ${t.venue.padEnd(14)} ${t.side.padEnd(7)} ${String(t.price).padStart(12)} x ${t.qty}`);
    }
  }
} catch (err) {
  // A refused key (KlondikeApiError, status 401) or a server that cannot be reached.
  console.error(`stream ended: ${err.message}`);
  process.exitCode = 1;
}
