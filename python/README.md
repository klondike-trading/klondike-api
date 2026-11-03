# klondike

Python client of the [Klondike](https://klondike-trading.github.io/klondike-api/)
market data API: trades and merged order books of the crypto pairs Klondike
streams from about forty centralized venues, and the market overview (what an
order really costs on the merged book, what aggregation saves against the best
single venue, dislocations between venues, implied prices), over REST and
WebSocket.

* sync and async REST clients (`httpx`),
* reconnecting WebSocket streams (`websockets`), JSON or Cap'n Proto frames
  decoded to the same objects,
* typed dataclasses, Python 3.9 and later.

## Install

```sh
pip install klondike            # REST and JSON streams
pip install 'klondike[capnp]'   # + Cap'n Proto streams (pycapnp)
```

## API key

Sign in on the [portal](https://api.klondike-crypto.com/portal/) and create a
key (`kl_` followed by 32 characters). Pass it to the clients, or set the
`KLONDIKE_API_KEY` environment variable:

```sh
export KLONDIKE_API_KEY=kl_...
```

It is sent in the `X-API-Key` header.

## REST

```python
import klondike

with klondike.Client("kl_...") as client:          # or Client() with KLONDIKE_API_KEY
    for s in client.symbols(q="btc"):
        print(s.symbol, s.market, s.venues)

    trades = client.trades("BTC/USDT", limit=10, venues=["binance", "okx"])
    for t in trades.trades:                         # oldest first
        print(t.ts, t.venue, t.side, t.qty, t.price)

    book = client.orderbook("BTC/USDT", depth=5)
    print("best bid", book.bids[0].price, "on", book.bids[0].venue)
    for v in book.venues:                           # best bid and ask of each venue
        print(v.venue, v.bid, v.ask)                # None when the venue has none

    ov = client.overview()
    for p in ov.pairs:
        cost = p.impact[0]                          # market order of 100 k$
        print(p.symbol, p.mid, cost.buy_all_in_bps, cost.best_single_buy_all_in_bps)
```

`AsyncClient` has the same methods, as coroutines:

```python
async with klondike.AsyncClient() as client:
    book = await client.orderbook("ETH/USDT:USDT", depth=20)
```

Symbols are unified CCXT symbols (`BTC/USDT` spot, `BTC/USDT:USDT` linear
perpetual, `BTC/USD:BTC` inverse perpetual), venues CCXT exchange ids.
Timestamps are milliseconds since the Unix epoch, prices in the quote currency,
quantities in the base currency (in USD for inverse perpetuals). The attributes
are the JSON fields in snake_case (`bidQty` is `bid_qty`, `volume1hUsd` is
`volume_1h_usd`).

### Errors

A non-200 answer raises `klondike.APIError` (`status`, `code`, `message`,
`retry_after`), or one of its subclasses:

| Status | Exception |
|---|---|
| 400 | `BadRequestError` |
| 401 | `UnauthorizedError` |
| 404 | `NotFoundError` (a symbol no venue streams) |
| 429 | `RateLimitError` (`retry_after` in seconds) |
| 503 | `ServiceUnavailableError` |

```python
try:
    book = client.orderbook("FOO/BAR")
except klondike.NotFoundError:
    book = None
except klondike.RateLimitError as e:
    time.sleep(e.retry_after or 1)
```

All the errors of the library derive from `klondike.KlondikeError`; network
errors are those of httpx.

## Streaming

`AsyncClient.stream()` opens a WebSocket carrying any number of subscriptions
(50 at most): the trades of a symbol, its merged order book, the overview.

```python
import asyncio
import klondike

async def main():
    async with klondike.AsyncClient() as client:
        async with client.stream() as stream:
            await stream.subscribe_trades("BTC/USDT")
            await stream.subscribe_trades("ETH/USDT:USDT", venues=["binanceusdm", "bybit"])
            await stream.subscribe_orderbook("BTC/USDT", depth=10)
            await stream.subscribe_overview()

            async for msg in stream:
                if msg.type == "trades":
                    for t in msg.trades.trades:
                        print(msg.trades.symbol, t.venue, t.side, t.qty, t.price)
                elif msg.type == "orderbook":
                    ob = msg.orderbook
                    print(ob.symbol, ob.bids[0].price, ob.asks[0].price)
                elif msg.type == "overview":
                    print(len(msg.overview.pairs), "pairs")
                elif msg.type == "error":
                    print("error:", msg.error)   # does not close the connection

asyncio.run(main())
```

Each message has a `type` (`trades`, `orderbook`, `overview`, `subscribed`,
`unsubscribed`, `error`, `pong`, see `klondike.MessageType`) and the matching
attribute: `trades`, `orderbook`, `overview`, `subscription` (subscribed and
unsubscribed) or `error`.

* `unsubscribe("trades", "BTC/USDT")`, `unsubscribe("overview")` stop a stream;
  `ping()` asks for a `pong`; `close()` (or leaving `async with`) ends the
  iteration. Subscribing again to the same channel and symbol replaces its
  venues and depth.
* A lost connection (server restart, network, slow consumer) is reopened with
  an exponential backoff from 0.5 s to 30 s and the subscriptions are sent
  again (except those the server refused with a `bad_request`,
  `unknown_channel` or `too_many_subscriptions` error): the iteration goes
  on. A key refused at a reconnection raises
  `UnauthorizedError` from the iteration; `stream(reconnect=False)` raises
  `StreamClosedError` instead of reconnecting.
* Read the stream fast enough: the server disconnects clients that lag.
  The reconnections are logged on the `klondike` logger.

### Formats

The server sends JSON text frames by default, or packed
[Cap'n Proto](https://capnproto.org/) binary frames, about three times smaller
on order books, with `format="capnp"` (needs `pip install 'klondike[capnp]'`).
The messages are the same objects whatever the format:

```python
async with client.stream(format="capnp") as stream:
    await stream.subscribe_orderbook("BTC/USDT", depth=50)
    async for msg in stream:
        ...
```

The schema is shipped in the package (`klondike/schema/klondike_api.capnp`).
`klondike.decode_json()` and `klondike.decode_capnp()` decode a single frame.

### Example

[`examples/stream_trades.py`](https://github.com/klondike-trading/klondike-api/blob/main/python/examples/stream_trades.py) prints the trades of a
symbol:

```sh
export KLONDIKE_API_KEY=kl_...
python examples/stream_trades.py BTC/USDT --format capnp
```

## Development

```sh
uv venv --python 3.12 .venv
uv pip install --python .venv/bin/python -e '.[capnp,dev]'
.venv/bin/pytest          # unit tests: fixtures of ../testdata, REST, streams
.venv/bin/mypy
```

The end to end tests run against a server when `KLONDIKE_TEST_URL` is set, for
instance the local stack of the Klondike repository (`api-go/dev.sh`):

```sh
KLONDIKE_TEST_URL=http://localhost:8093 \
KLONDIKE_TEST_KEY=kl_dev0dev0dev0dev0dev0dev0dev0dev0 .venv/bin/pytest
```

## License

MIT
