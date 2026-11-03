# REST endpoints

Base URL: `https://api.klondike-crypto.com`. Every response is JSON. The full
schema is in the [OpenAPI reference](reference.md).

## Symbols

`GET /v1/symbols?q=btc`

The symbols the API received trades for in the last ten minutes, with the
venues they traded on. `q` filters on a case insensitive substring.

```json
{"symbols": [
  {"symbol": "BTC/USDT", "market": "spot", "venues": ["binance", "bybit", "okx"], "lastTradeTs": 1793706479654},
  {"symbol": "BTC/USDT:USDT", "market": "perp", "venues": ["binanceusdm", "bybit"], "lastTradeTs": 1793706479650}
]}
```

## Trades

`GET /v1/trades?symbol=BTC/USDT&limit=100&since=1793706400000&venues=binance,okx`

| Parameter | | |
|---|---|---|
| `symbol` | required | |
| `limit` | 1 to 1000, default 100 | the most recent trades |
| `since` | ms | only trades strictly after this time |
| `venues` | comma separated | all venues when absent |

```json
{"symbol": "BTC/USDT", "trades": [
  {"ts": 1793706479654, "venue": "binance", "price": 104250.1, "qty": 0.0125, "side": "buy"},
  {"ts": 1793706479702, "venue": "okx", "price": 104249.9, "qty": 0.4, "side": "sell"}
]}
```

Trades are sorted by time, oldest first. `side` is the aggressor side (`buy`,
`sell`, `unknown`).

The API keeps the last 1,000 trades of each symbol: this endpoint is a recent
window, not a history. To get every trade, [stream](websocket.md#trades) them;
to catch up after a disconnection, call it with `since` set to the time of the
last trade you received.

## Orderbook

`GET /v1/orderbook?symbol=BTC/USDT&depth=50&venues=binance,coinbase`

| Parameter | | |
|---|---|---|
| `symbol` | required | |
| `depth` | 1 to 1000, default 50 | levels per side |
| `venues` | comma separated | levels of these venues only; `venues` of the response always has them all |

```json
{"symbol": "BTC/USDT", "ts": 1793706479000,
 "venues": [
   {"venue": "binance", "ts": 1793706478950, "bid": 104250.0, "bidQty": 1.2, "ask": 104250.1, "askQty": 0.8},
   {"venue": "kraken", "ts": 1793706478890, "bid": 104249.0, "bidQty": 0.5, "ask": null, "askQty": null}
 ],
 "bids": [{"price": 104250.0, "qty": 1.2, "venue": "binance"}, {"price": 104249.0, "qty": 0.5, "venue": "kraken"}],
 "asks": [{"price": 104250.1, "qty": 0.8, "venue": "binance"}]}
```

* `venues`: the best bid and ask of each venue on its whole book, `null` when
  it has none; `ts` is the last update of the venue book.
* `bids` (highest first) and `asks` (lowest first): the merged levels. Two
  venues at the same price are two levels.
* The merged book can be **crossed** (a bid above an ask of another venue): a
  real opportunity, short lived, or a stale book. Compare the `ts` of the
  venues.

The venues are merged about once a second. The first request for a symbol
nobody follows can take up to two seconds; a symbol no venue streams gives a
404. Asking for a pair outside the ones Klondike follows makes Klondike start
streaming it when a venue lists it: retry a few seconds later.

## Overview

`GET /v1/overview`

The market overview, refreshed every few seconds. With `If-None-Match` set to
the `ETag` of the previous response, the API answers `304 Not Modified` while
it has not changed. It is served gzipped when the client accepts it.

```json
{
  "ts": 1793706479654,
  "windowsFromTs": 1793702880000,
  "feeds": {"venues": 40, "venuesKnown": 42, "booksLive": 1565, "lagMsP50": 111},
  "pairs": [{
    "symbol": "BTC/USDT", "base": "BTC", "quote": "USDT", "market": "spot",
    "status": "ok", "exchanges": 31, "live": 30, "mid": 104250.05,
    "volume1hUsd": 812345678, "volumeTopExchange": "binance", "volumeTopShare": 0.41,
    "depth10Usd": 48123456, "depth10PartialShare": 0.02, "imbalance": -0.08,
    "impact": [{
      "notionalUsd": 1000000,
      "buyAllInBps": 7.3, "sellAllInBps": 7.1, "buyFilled": 1, "sellFilled": 1,
      "buyVenues": 9, "sellVenues": 8,
      "bestSingleBuyAllInBps": 10.2, "bestSingleSellAllInBps": 10.4,
      "bestSingleBuyExchange": "binance", "bestSingleSellExchange": "okx"
    }],
    "crosses1h": 42, "crossesPositive1h": 1, "maxNetBps1h": 0.4, "basisBps": null
  }],
  "leaders": [{"exchange": "binance", "share": 0.31}],
  "implied": {
    "pegs": [{"symbol": "USDT/USD", "bps": -15.85, "venueSpreadBps": 20.58, "venues": 13}],
    "fx": [{"ccy": "EUR", "implied": 1.1178, "direct": null, "devBps": null}],
    "triangles": [{"cross": "ETH/BTC", "cycle": "BTC → USDT → ETH → BTC", "bestNetBps": -5.0}]
  },
  "paths": [{"sell": "EUR", "buy": "BTC", "quantity": 100000, "tokens": ["EUR", "USDC", "BTC"], "costBps": 12.4, "directCostBps": 15.1, "ts": 1793706460000}]
}
```

### Pairs

| Field | |
|---|---|
| `status`, `reasons` | data quality only: `ok`, `warn`, `alert`, `down`; the reasons name the venues: `stale:<venue>` (book not updated), `not_live:<venue>`, `outlier:<venue>` (quotes far from the others), `cross_stale:<venue>` (crossed by a stale book) |
| `exchanges`, `live` | venues listing the pair, venues with a live book |
| `mid` | consolidated mid; the median of the venue mids when the merged book is crossed |
| `volume1hUsd` | notional traded over the last hour, **measured on the trades received**, not declared by the venues; `volumeTopExchange` and `volumeTopShare` say how concentrated it is |
| `depth10Usd` | bid + ask depth within ±10 bps of the mid, in USD; a lower bound when some books stop before ±10 bps (`depth10PartialShare` of it) |
| `imbalance` | (bid − ask) / (bid + ask) within ±50 bps, from −1 to 1 |
| `impact` | the cost of a market order of `notionalUsd` (100 k$, 1 M$, 10 M$), in bps of the mid, **taker fees included**: on the merged book (`buyAllInBps`, `sellAllInBps`, venues touched), and on the best single venue (`bestSingle*`). A cost is `null` when the book cannot fill the order; `buyFilled` / `sellFilled` then say how much it can. The gain of aggregation is `bestSingle*AllInBps − *AllInBps`; it can be negative, a single venue being cheaper after fees. |
| `crosses1h`, `crossesPositive1h`, `maxNetBps1h` | crosses between venues over the last hour, transient ones included; those net positive after the taker fees of both legs; the best net |
| `basisBps` | perpetuals: (mid perp − mid spot) / mid spot against the spot pair of the same quote, `null` without a live spot |

### Other parts

| Field | |
|---|---|
| `feeds` | venues with at least one live book out of the venues known, live books, median latency of the books |
| `leaders` | the share of the time each venue holds the best consolidated bid or ask over the last hour, on BTC, ETH and SOL spot, ties split: the shares sum to 1 |
| `implied.pegs` | stablecoin pairs: consolidated mid in bps from 1, spread between the venue mids |
| `implied.fx` | the rate of a fiat currency in USD implied by crypto pairs (median of mid(X/USD) / mid(X/ccy)), against the direct pair when there is one |
| `implied.triangles` | the best of the two cycles through a cross pair and USDT, net of the taker fees of the three legs |
| `paths` | the cheapest conversion path for a few fixed conversions, fees included, against the direct pair; empty while the path finder is not connected |
