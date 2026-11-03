# WebSocket streams

```
wss://api.klondike-crypto.com/v1/ws?format=json
wss://api.klondike-crypto.com/v1/ws?format=capnp
```

One connection carries any number of subscriptions (50 at most): the trades of
a symbol, its merged order book, the overview. The API key goes in the
`X-API-Key` header, or in the `api_key` query parameter when the client cannot
set headers (browsers). A missing or revoked key is refused before the upgrade
with a 401.

`format` selects the encoding of the **server** messages:

| `format` | Server frames | Client frames |
|---|---|---|
| `json` (default) | text, one JSON object per frame | text, JSON |
| `capnp` | binary, one packed Cap'n Proto `ServerMessage` per frame ([schema](capnp.md)) | text, JSON |

The client always sends JSON: subscriptions are rare, the data is what is worth
encoding in binary.

## Client messages

```json
{"op": "subscribe", "channel": "trades", "symbol": "BTC/USDT"}
{"op": "subscribe", "channel": "trades", "symbol": "ETH/USDT:USDT", "venues": ["binanceusdm", "bybit"]}
{"op": "subscribe", "channel": "orderbook", "symbol": "BTC/USDT", "depth": 20}
{"op": "subscribe", "channel": "orderbook", "symbol": "BTC/USDT", "depth": 50, "venues": ["coinbase", "kraken"]}
{"op": "subscribe", "channel": "overview"}
{"op": "unsubscribe", "channel": "trades", "symbol": "BTC/USDT"}
{"op": "ping"}
```

| Field | | |
|---|---|---|
| `op` | `subscribe`, `unsubscribe`, `ping` | |
| `channel` | `trades`, `orderbook`, `overview` | |
| `symbol` | unified CCXT symbol | required except for `overview` |
| `venues` | CCXT exchange ids | optional, all venues when absent |
| `depth` | levels per side, 1 to 200 | `orderbook` only, default 20 |

Subscribing again to the same channel and symbol replaces its `venues` and
`depth`. A symbol no venue streams yet is subscribed anyway: Klondike starts
streaming the pairs its users ask for, and the data follows within a few
seconds when a venue lists the pair.

## Server messages

Every subscription and unsubscription is acknowledged:

```json
{"type": "subscribed", "channel": "trades", "symbol": "BTC/USDT"}
{"type": "unsubscribed", "channel": "trades", "symbol": "BTC/USDT"}
{"type": "subscribed", "channel": "overview"}
```

New message types may be added: a client should ignore a `type` it does not
know (the libraries do).

### trades

The trades received since the previous message, batched every 100 ms, oldest
first. Nothing is sent while the symbol does not trade.

```json
{"type": "trades", "symbol": "BTC/USDT", "trades": [
  {"ts": 1793706479654, "venue": "binance", "price": 104250.1, "qty": 0.0125, "side": "buy"},
  {"ts": 1793706479702, "venue": "okx", "price": 104249.9, "qty": 0.4, "side": "sell"}
]}
```

`side` is the aggressor side: `buy`, `sell` or `unknown`. The fields are those
of [`GET /v1/trades`](rest.md#trades).

### orderbook

The merged order book, whole (`depth` levels per side), when it changes and at
most four times a second; the venues are merged about once a second. The
latest book is sent right after the subscription when the API has one.

```json
{"type": "orderbook", "symbol": "BTC/USDT", "ts": 1793706479000,
 "venues": [
   {"venue": "binance", "ts": 1793706478950, "bid": 104250.0, "bidQty": 1.2, "ask": 104250.1, "askQty": 0.8},
   {"venue": "kraken", "ts": 1793706478890, "bid": 104249.0, "bidQty": 0.5, "ask": null, "askQty": null}
 ],
 "bids": [{"price": 104250.0, "qty": 1.2, "venue": "binance"}, {"price": 104249.0, "qty": 0.5, "venue": "kraken"}],
 "asks": [{"price": 104250.1, "qty": 0.8, "venue": "binance"}]}
```

Same fields as [`GET /v1/orderbook`](rest.md#orderbook). `venues` is not
filtered by the `venues` of the subscription; `bids` and `asks` are.

### overview

The overview, whole, right after the subscription and then each time it
changes (every few seconds).

```json
{"type": "overview", "overview": {"ts": 1793706479654, "feeds": {...}, "pairs": [...], ...}}
```

`overview` is the document of [`GET /v1/overview`](rest.md#overview).

### error

```json
{"type": "error", "code": "bad_request", "message": "symbol is required", "channel": "trades", "symbol": ""}
```

| `code` | |
|---|---|
| `bad_request` | invalid JSON, unknown `op`, missing symbol, `depth` out of range |
| `unknown_channel` | |
| `too_many_subscriptions` | more than 50 subscriptions on the connection |

An error does not close the connection. A subscription refused with
`bad_request`, `unknown_channel` or `too_many_subscriptions` does not exist on
the server: the libraries forget it and do not send it again after a
reconnection.

### pong

`{"type": "pong"}`, the answer to `{"op": "ping"}`. The server also sends
WebSocket ping frames every 20 s; WebSocket libraries answer them on their own.

## Limits and closing

* 5 connections at once per API key; a sixth is refused with a 429.
* A client that does not read fast enough is disconnected (close code `4001`,
  reason `slow consumer`) rather than served stale data.
* The server closes with `1001` when it restarts. The client libraries
  reconnect with a backoff and subscribe again; a client of your own should do
  the same.

## Cap'n Proto

With `format=capnp`, each binary frame is one `ServerMessage` of
[`klondike_api.capnp`](capnp.md), packed encoding. The union says the type:
`trades`, `orderbook`, `overviewJson` (the overview document as JSON text),
`subscribed`, `unsubscribed`, `error`, `pong`. Differences with JSON:

* a missing number is NaN, a missing timestamp 0;
* the `venue` of a level is an index into `Orderbook.venues`.

The libraries give the same objects whatever the format. Cap'n Proto frames are
about three times smaller than JSON on an order book of 50 levels and are
decoded without parsing text.
