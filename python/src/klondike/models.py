"""Data models of the Klondike API.

The attributes are the camelCase fields of the JSON documents in snake_case
(``bidQty`` is ``bid_qty``, ``volume1hUsd`` is ``volume_1h_usd``). Timestamps
are milliseconds since the Unix epoch (UTC), prices are in the quote currency
and quantities in the base currency (in USD for inverse perpetuals).

``None`` means a missing value: a venue without a bid, a cost the book cannot
fill. Every model has a ``from_dict`` class method decoding the JSON document
of the API; missing fields take their zero value, unknown fields are ignored.
"""

from __future__ import annotations

import enum
from dataclasses import dataclass, field
from typing import Any, List, Mapping, Optional

__all__ = [
    "Side",
    "Trade",
    "Trades",
    "SymbolInfo",
    "VenueQuote",
    "Level",
    "Orderbook",
    "Overview",
    "Feeds",
    "Pair",
    "Impact",
    "Leader",
    "Implied",
    "Peg",
    "ImpliedFX",
    "Triangle",
    "Path",
]

_Doc = Mapping[str, Any]


class Side(str, enum.Enum):
    """Aggressor (taker) side of a trade. Compares equal to its string."""

    BUY = "buy"
    SELL = "sell"
    UNKNOWN = "unknown"

    @classmethod
    def parse(cls, value: Any) -> Side:
        try:
            return cls(value)
        except ValueError:
            return cls.UNKNOWN

    def __str__(self) -> str:
        return self.value


def _opt_float(v: Any) -> Optional[float]:
    return None if v is None else float(v)


def _opt_int(v: Any) -> Optional[int]:
    return None if v is None else int(v)


def _list(d: _Doc, key: str) -> List[Any]:
    v = d.get(key)
    return list(v) if v else []


@dataclass
class Trade:
    """A trade of one venue."""

    ts: int
    """Exchange time in ms (receive time when the venue gives none)."""
    venue: str
    """CCXT exchange id: ``binance``, ``binanceusdm``, ``krakenfutures``..."""
    price: float
    qty: float
    side: Side

    @classmethod
    def from_dict(cls, d: _Doc) -> Trade:
        return cls(
            ts=int(d.get("ts") or 0),
            venue=d.get("venue") or "",
            price=float(d.get("price") or 0),
            qty=float(d.get("qty") or 0),
            side=Side.parse(d.get("side")),
        )


@dataclass
class Trades:
    """Trades of one symbol, oldest first."""

    symbol: str
    trades: List[Trade] = field(default_factory=list)

    @classmethod
    def from_dict(cls, d: _Doc) -> Trades:
        return cls(
            symbol=d.get("symbol") or "",
            trades=[Trade.from_dict(t) for t in _list(d, "trades")],
        )


@dataclass
class SymbolInfo:
    """A symbol the API received trades for recently."""

    symbol: str
    market: str
    """``spot`` or ``perp``."""
    venues: List[str] = field(default_factory=list)
    last_trade_ts: int = 0

    @classmethod
    def from_dict(cls, d: _Doc) -> SymbolInfo:
        return cls(
            symbol=d.get("symbol") or "",
            market=d.get("market") or "",
            venues=_list(d, "venues"),
            last_trade_ts=int(d.get("lastTradeTs") or 0),
        )


@dataclass
class VenueQuote:
    """Best bid and ask of one venue on its whole book, ``None`` when missing."""

    venue: str
    ts: Optional[int] = None
    """Last update of the venue book, ms."""
    bid: Optional[float] = None
    bid_qty: Optional[float] = None
    ask: Optional[float] = None
    ask_qty: Optional[float] = None

    @classmethod
    def from_dict(cls, d: _Doc) -> VenueQuote:
        return cls(
            venue=d.get("venue") or "",
            ts=_opt_int(d.get("ts")),
            bid=_opt_float(d.get("bid")),
            bid_qty=_opt_float(d.get("bidQty")),
            ask=_opt_float(d.get("ask")),
            ask_qty=_opt_float(d.get("askQty")),
        )


@dataclass
class Level:
    """A level of the merged book with the venue it comes from."""

    price: float
    qty: float
    venue: str

    @classmethod
    def from_dict(cls, d: _Doc) -> Level:
        return cls(
            price=float(d.get("price") or 0),
            qty=float(d.get("qty") or 0),
            venue=d.get("venue") or "",
        )


@dataclass
class Orderbook:
    """Merged order book of a symbol: the best bid and ask of every venue and
    the first levels of each side, best first."""

    symbol: str
    ts: int
    venues: List[VenueQuote] = field(default_factory=list)
    bids: List[Level] = field(default_factory=list)
    """Best (highest) first."""
    asks: List[Level] = field(default_factory=list)
    """Best (lowest) first."""

    @classmethod
    def from_dict(cls, d: _Doc) -> Orderbook:
        return cls(
            symbol=d.get("symbol") or "",
            ts=int(d.get("ts") or 0),
            venues=[VenueQuote.from_dict(v) for v in _list(d, "venues")],
            bids=[Level.from_dict(x) for x in _list(d, "bids")],
            asks=[Level.from_dict(x) for x in _list(d, "asks")],
        )


@dataclass
class Feeds:
    """Health of the market data."""

    venues: int = 0
    """Venues with at least one live book."""
    venues_known: int = 0
    books_live: int = 0
    lag_ms_p50: float = 0.0
    """Median latency of the live books, ms."""

    @classmethod
    def from_dict(cls, d: _Doc) -> Feeds:
        return cls(
            venues=int(d.get("venues") or 0),
            venues_known=int(d.get("venuesKnown") or 0),
            books_live=int(d.get("booksLive") or 0),
            lag_ms_p50=float(d.get("lagMsP50") or 0),
        )


@dataclass
class Impact:
    """Cost of a market order of ``notional_usd``, taker fees included, in bps
    of the consolidated mid, on the merged book and on the best single venue.
    A cost is ``None`` when the book cannot fill the order; the filled fraction
    then says how much it can."""

    notional_usd: float = 0.0
    buy_all_in_bps: Optional[float] = None
    sell_all_in_bps: Optional[float] = None
    buy_filled: float = 0.0
    sell_filled: float = 0.0
    buy_venues: int = 0
    sell_venues: int = 0
    best_single_buy_all_in_bps: Optional[float] = None
    best_single_sell_all_in_bps: Optional[float] = None
    best_single_buy_exchange: str = ""
    best_single_sell_exchange: str = ""

    @classmethod
    def from_dict(cls, d: _Doc) -> Impact:
        return cls(
            notional_usd=float(d.get("notionalUsd") or 0),
            buy_all_in_bps=_opt_float(d.get("buyAllInBps")),
            sell_all_in_bps=_opt_float(d.get("sellAllInBps")),
            buy_filled=float(d.get("buyFilled") or 0),
            sell_filled=float(d.get("sellFilled") or 0),
            buy_venues=int(d.get("buyVenues") or 0),
            sell_venues=int(d.get("sellVenues") or 0),
            best_single_buy_all_in_bps=_opt_float(d.get("bestSingleBuyAllInBps")),
            best_single_sell_all_in_bps=_opt_float(d.get("bestSingleSellAllInBps")),
            best_single_buy_exchange=d.get("bestSingleBuyExchange") or "",
            best_single_sell_exchange=d.get("bestSingleSellExchange") or "",
        )


@dataclass
class Pair:
    """A pair of the overview. Amounts are in USD, costs in bps of the
    consolidated mid."""

    symbol: str = ""
    base: str = ""
    quote: str = ""
    """``USDT``, ``USDT:USDT``..."""
    market: str = ""
    """``spot`` or ``perp``."""
    status: str = ""
    """``ok``, ``warn``, ``alert`` or ``down``: data quality only."""
    reasons: List[str] = field(default_factory=list)
    """Why the status is not ok: ``stale:<venue>``, ``not_live:<venue>``..."""
    exchanges: int = 0
    """Venues listing the pair."""
    live: int = 0
    """Venues with a live book."""
    mid: float = 0.0
    volume_1h_usd: float = 0.0
    volume_top_exchange: str = ""
    volume_top_share: float = 0.0
    depth10_usd: float = 0.0
    """Bid + ask within ±10 bps of the mid."""
    depth10_partial_share: Optional[float] = None
    """Share of ``depth10_usd`` from books stopping before ±10 bps: a lower bound."""
    imbalance: float = 0.0
    """(bid − ask) / (bid + ask) within ±50 bps."""
    impact: List[Impact] = field(default_factory=list)
    crosses_1h: int = 0
    """Crosses between venues over 1 h, transient ones included."""
    crosses_positive_1h: int = 0
    """Episodes net positive after taker fees."""
    max_net_bps_1h: Optional[float] = None
    basis_bps: Optional[float] = None
    """Perpetuals: (mid perp − mid spot) / mid spot."""

    @classmethod
    def from_dict(cls, d: _Doc) -> Pair:
        return cls(
            symbol=d.get("symbol") or "",
            base=d.get("base") or "",
            quote=d.get("quote") or "",
            market=d.get("market") or "",
            status=d.get("status") or "",
            reasons=_list(d, "reasons"),
            exchanges=int(d.get("exchanges") or 0),
            live=int(d.get("live") or 0),
            mid=float(d.get("mid") or 0),
            volume_1h_usd=float(d.get("volume1hUsd") or 0),
            volume_top_exchange=d.get("volumeTopExchange") or "",
            volume_top_share=float(d.get("volumeTopShare") or 0),
            depth10_usd=float(d.get("depth10Usd") or 0),
            depth10_partial_share=_opt_float(d.get("depth10PartialShare")),
            imbalance=float(d.get("imbalance") or 0),
            impact=[Impact.from_dict(x) for x in _list(d, "impact")],
            crosses_1h=int(d.get("crosses1h") or 0),
            crosses_positive_1h=int(d.get("crossesPositive1h") or 0),
            max_net_bps_1h=_opt_float(d.get("maxNetBps1h")),
            basis_bps=_opt_float(d.get("basisBps")),
        )


@dataclass
class Leader:
    """Share of the time a venue holds the best consolidated bid or ask; ties
    split the time, so that the shares sum to 1."""

    exchange: str = ""
    share: float = 0.0

    @classmethod
    def from_dict(cls, d: _Doc) -> Leader:
        return cls(exchange=d.get("exchange") or "", share=float(d.get("share") or 0))


@dataclass
class Peg:
    """A stablecoin pair: its consolidated mid in bps from 1."""

    symbol: str = ""
    bps: float = 0.0
    venue_spread_bps: float = 0.0
    """Highest minus lowest venue mid."""
    venues: int = 0

    @classmethod
    def from_dict(cls, d: _Doc) -> Peg:
        return cls(
            symbol=d.get("symbol") or "",
            bps=float(d.get("bps") or 0),
            venue_spread_bps=float(d.get("venueSpreadBps") or 0),
            venues=int(d.get("venues") or 0),
        )


@dataclass
class ImpliedFX:
    """Rate of a fiat currency in USD implied by crypto pairs, against the
    direct pair when there is one."""

    ccy: str = ""
    implied: float = 0.0
    direct: Optional[float] = None
    dev_bps: Optional[float] = None

    @classmethod
    def from_dict(cls, d: _Doc) -> ImpliedFX:
        return cls(
            ccy=d.get("ccy") or "",
            implied=float(d.get("implied") or 0),
            direct=_opt_float(d.get("direct")),
            dev_bps=_opt_float(d.get("devBps")),
        )


@dataclass
class Triangle:
    """Best of the two cycles through a cross pair and USDT, net of the taker
    fees of its three legs."""

    cross: str = ""
    cycle: str = ""
    best_net_bps: float = 0.0

    @classmethod
    def from_dict(cls, d: _Doc) -> Triangle:
        return cls(
            cross=d.get("cross") or "",
            cycle=d.get("cycle") or "",
            best_net_bps=float(d.get("bestNetBps") or 0),
        )


@dataclass
class Implied:
    """Implied prices: stablecoin pegs, fiat rates and triangles."""

    pegs: List[Peg] = field(default_factory=list)
    fx: List[ImpliedFX] = field(default_factory=list)
    triangles: List[Triangle] = field(default_factory=list)

    @classmethod
    def from_dict(cls, d: _Doc) -> Implied:
        return cls(
            pegs=[Peg.from_dict(x) for x in _list(d, "pegs")],
            fx=[ImpliedFX.from_dict(x) for x in _list(d, "fx")],
            triangles=[Triangle.from_dict(x) for x in _list(d, "triangles")],
        )


@dataclass
class Path:
    """Best conversion path, fees included; costs are against the consolidated
    mid of the direct pair."""

    sell: str = ""
    buy: str = ""
    quantity: float = 0.0
    tokens: List[str] = field(default_factory=list)
    cost_bps: float = 0.0
    direct_cost_bps: Optional[float] = None
    ts: int = 0

    @classmethod
    def from_dict(cls, d: _Doc) -> Path:
        return cls(
            sell=d.get("sell") or "",
            buy=d.get("buy") or "",
            quantity=float(d.get("quantity") or 0),
            tokens=_list(d, "tokens"),
            cost_bps=float(d.get("costBps") or 0),
            direct_cost_bps=_opt_float(d.get("directCostBps")),
            ts=int(d.get("ts") or 0),
        )


@dataclass
class Overview:
    """The market overview: the pairs followed permanently, what an order costs
    on the merged book and on the best single venue, crosses between venues,
    venues setting the price, implied prices."""

    ts: int = 0
    windows_from_ts: int = 0
    """Oldest minute of the 1 h counters, ms."""
    feeds: Feeds = field(default_factory=Feeds)
    pairs: List[Pair] = field(default_factory=list)
    leaders: List[Leader] = field(default_factory=list)
    implied: Implied = field(default_factory=Implied)
    paths: List[Path] = field(default_factory=list)

    @classmethod
    def from_dict(cls, d: _Doc) -> Overview:
        return cls(
            ts=int(d.get("ts") or 0),
            windows_from_ts=int(d.get("windowsFromTs") or 0),
            feeds=Feeds.from_dict(d.get("feeds") or {}),
            pairs=[Pair.from_dict(x) for x in _list(d, "pairs")],
            leaders=[Leader.from_dict(x) for x in _list(d, "leaders")],
            implied=Implied.from_dict(d.get("implied") or {}),
            paths=[Path.from_dict(x) for x in _list(d, "paths")],
        )
