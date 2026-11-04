# Klondike API — Excel add-in

Live market data in a worksheet, as plain formulas: trades, merged order books and the market overview
update on their own and can be used by your own formulas.

```
=KLONDIKE.QUOTE("BTC/USDT","bid")
=KLONDIKE.ORDERBOOK("BTC/USDT",10)
=KLONDIKE.IMPACT("ETH/USDT",1000000,"buyAllInBps")
```

## Install

Excel for Windows only: an `.xll` is a native Windows add-in, which neither Excel for Mac nor Excel on the web
can load.

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) of the same
   bitness as Excel (*File > Account > About Excel* tells; x64 almost always).
2. Get `Klondike-AddIn64-packed.xll`, or `Klondike-AddIn-packed.xll` for a 32-bit Excel, from the
   `excel-addin` artifact of the [CI](https://github.com/klondike-trading/klondike-api/actions/workflows/ci.yml),
   or [build it](#development).
3. Windows blocks add-ins downloaded from the internet: right-click the file, *Properties*, tick *Unblock*.
4. In Excel: *File > Options > Add-ins > Manage: Excel Add-ins > Go > Browse*, and select the file.
5. In the new **Klondike** tab of the ribbon, click **API key** and paste a key created on the
   [portal](https://api.klondike-crypto.com/portal/).

The key is stored encrypted for your Windows account (DPAPI), never in the workbook. The environment variable
`KLONDIKE_API_KEY`, when set, takes precedence.

## Functions

Everything is live, except `KLONDIKE.SYMBOLS`, read again every minute. Only the first argument is required
unless stated, and the `headers` of a table default to `TRUE`.

| Function | |
|---|---|
| `KLONDIKE.QUOTE(symbol, [field], [venue])` | a value of the merged book, or of one venue |
| `KLONDIKE.LAST(symbol, [field], [venue])` | a value of the last trade, of all venues or of one |
| `KLONDIKE.ORDERBOOK(symbol, [depth], [venues], [headers])` | table of the merged book, bids and asks side by side, each level with its venue; 10 levels by default, 200 at most |
| `KLONDIKE.VENUES(symbol, [headers])` | table of the best bid and ask of every venue |
| `KLONDIKE.TRADES(symbol, [limit], [venues], [headers])` | table of the last trades, newest first; 20 by default, 1000 at most |
| `KLONDIKE.OVERVIEW([section], [headers])` | table of a section of the overview |
| `KLONDIKE.PAIR(symbol, [field])` | a value of a pair of the overview |
| `KLONDIKE.IMPACT(symbol, notionalUsd, [field])` | what a market order costs, taker fees included: `notionalUsd` is required, 100000, 1000000 or 10000000 |
| `KLONDIKE.SYMBOLS([query], [headers])` | table of the symbols with recent trades and their venues |
| `KLONDIKE.STATUS()` | `connected`, `connecting`, `reconnecting`, `idle`, `no API key`, `API key refused` |

Symbols are unified CCXT symbols: `BTC/USDT`, `BTC/USDT:USDT` (linear perpetual), `BTC/USD:BTC` (inverse
perpetual). `venues` is `"binance,okx"` or a range of cells. Fields and sections are not case sensitive.

| | Fields (the first one is the default) |
|---|---|
| `QUOTE` | `bid`, `ask`, `mid`, `spread`, `spreadBps`, `bidQty`, `askQty`, `time`; on the merged book, `bidVenue` and `askVenue` too |
| `LAST` | `price`, `qty`, `side`, `venue`, `time` |
| `PAIR` | `mid`, `status`, `reasons`, `base`, `quote`, `market`, `exchanges`, `live`, `volume1hUsd`, `volumeTopExchange`, `volumeTopShare`, `depth10Usd`, `depth10PartialShare`, `imbalance`, `crosses1h`, `crossesPositive1h`, `maxNetBps1h`, `basisBps` |
| `IMPACT` | `buyAllInBps`, `sellAllInBps`, `buyFilled`, `sellFilled`, `buyVenues`, `sellVenues`, `bestSingleBuyAllInBps`, `bestSingleSellAllInBps`, `bestSingleBuyExchange`, `bestSingleSellExchange` |
| `OVERVIEW` sections | `pairs`, `impact`, `feeds`, `leaders`, `pegs`, `fx`, `triangles`, `paths` |

## What a cell shows

| | |
|---|---|
| `#GETTING_DATA` | waiting for the first value. It stays for a symbol no venue streams; Klondike starts streaming a pair when someone asks and a venue lists it |
| `#N/A` | the value does not exist: no ask on this venue, a pair that is not in the overview |
| `klondike: ...` | the error in plain text: no key, a key refused, an unknown field |
| empty cell of a table | a value missing in that row |

**Dates** are Excel dates in **UTC**: apply a date format to the column to read them, for instance
`yyyy-mm-dd hh:mm:ss.000`. A function cannot format the cells it fills.

**Tables** spill on Microsoft 365 and Excel 2021. On older versions, select the range and enter the formula
with Ctrl+Shift+Enter: the table is cut to the range. Leave room below: a table that grows over a non-empty
cell shows `#SPILL!`.

**Refresh**: Excel recalculates live formulas at most every 2 seconds by default. To change it for the whole of
Excel, in milliseconds, use the *Immediate* window of the VBA editor (Alt+F11, then Ctrl+G):
`Application.RTD.ThrottleInterval = 500`.

## Limits

However many cells ask for it, a stream is subscribed once: a workbook can repeat `KLONDIKE.QUOTE` at will. A
stream is a channel (trades, order book, overview), a symbol and a set of `venues`; `QUOTE` and `VENUES` share
the order book of all venues.

The API allows 5 WebSocket connections and 50 subscriptions per connection for a key. The add-in uses at most 4
connections, which leaves one for your other tools, so an Excel instance can follow **200 streams**, and at most 4
different `venues` for the same channel and symbol, since a connection holds only one of them. Beyond that the
cell says so, and takes the place of the next stream that goes away. Other tools or Excel instances using the
same key share the 5 connections: when none is left, `KLONDIKE.STATUS()` stays on `reconnecting`.

## Development

```sh
dotnet build src/Klondike.Excel -c Release
```

makes `src/Klondike.Excel/bin/Release/net10.0-windows/publish/Klondike-AddIn64-packed.xll` and its 32-bit
twin, on any OS. Loading it requires Excel on Windows. Most of the add-in is `src/Klondike.Excel.Core`, without
Excel, tested on any OS: subscriptions (`MarketHub`), what each function shows (`CellFeeds`), tables and
arguments. `src/Klondike.Excel` is what only Excel can run: the [Excel-DNA](https://excel-dna.net/) functions, the ribbon and
the API key.
