"""Messages of the WebSocket API and their two encodings.

The server sends JSON text frames (``format=json``) or packed Cap'n Proto
binary frames (``format=capnp``); both decode to the same :class:`Message`.
The client always sends JSON.
"""

from __future__ import annotations

import enum
import json
import math
from dataclasses import dataclass
from typing import Any, Dict, Iterable, List, Optional, Union

from ._capnp import load_schema
from .errors import DecodeError, StreamError
from .models import Level, Orderbook, Overview, Side, Trade, Trades, VenueQuote

__all__ = [
    "Channel",
    "MessageType",
    "Subscription",
    "Message",
    "UnknownMessageError",
    "decode_json",
    "decode_capnp",
    "decode",
]


class Channel(str, enum.Enum):
    """A stream of the WebSocket API. Compares equal to its string."""

    TRADES = "trades"
    ORDERBOOK = "orderbook"
    OVERVIEW = "overview"

    def __str__(self) -> str:
        return self.value


class MessageType(str, enum.Enum):
    """Type of a server message. Compares equal to its string."""

    TRADES = "trades"
    ORDERBOOK = "orderbook"
    OVERVIEW = "overview"
    SUBSCRIBED = "subscribed"
    UNSUBSCRIBED = "unsubscribed"
    ERROR = "error"
    PONG = "pong"

    def __str__(self) -> str:
        return self.value


@dataclass(frozen=True)
class Subscription:
    """Names a stream: ``symbol`` is empty for the overview."""

    channel: str
    symbol: str = ""


@dataclass
class Message:
    """A server message. ``type`` says which attribute is set: ``trades``,
    ``orderbook``, ``overview``, ``subscription`` (subscribed and
    unsubscribed) or ``error``; none for ``pong``."""

    type: MessageType
    trades: Optional[Trades] = None
    orderbook: Optional[Orderbook] = None
    overview: Optional[Overview] = None
    subscription: Optional[Subscription] = None
    error: Optional[StreamError] = None


class UnknownMessageError(DecodeError):
    """A message of a type this version of the library does not know (sent by a
    newer server). The stream skips them."""


def decode_json(data: Union[str, bytes, bytearray]) -> Message:
    """Decodes a server message of ``format=json`` (a text frame)."""
    try:
        doc = json.loads(data)
    except ValueError as e:
        raise DecodeError(f"klondike: invalid JSON message: {e}") from e
    if not isinstance(doc, dict):
        raise DecodeError("klondike: a JSON message must be an object")
    try:
        return _from_doc(doc)
    except DecodeError:
        raise
    except (TypeError, ValueError, AttributeError) as e:
        raise DecodeError(f"klondike: invalid {doc.get('type')} message: {e}") from e


def _from_doc(doc: Dict[str, Any]) -> Message:
    typ = doc.get("type")
    if typ == "trades":
        return Message(MessageType.TRADES, trades=Trades.from_dict(doc))
    if typ == "orderbook":
        return Message(MessageType.ORDERBOOK, orderbook=Orderbook.from_dict(doc))
    if typ == "overview":
        return Message(MessageType.OVERVIEW, overview=Overview.from_dict(doc.get("overview") or {}))
    if typ in ("subscribed", "unsubscribed"):
        sub = Subscription(channel=doc.get("channel") or "", symbol=doc.get("symbol") or "")
        return Message(MessageType(typ), subscription=sub)
    if typ == "error":
        err = StreamError(
            code=doc.get("code") or "",
            message=doc.get("message") or "",
            channel=doc.get("channel") or "",
            symbol=doc.get("symbol") or "",
        )
        return Message(MessageType.ERROR, error=err)
    if typ == "pong":
        return Message(MessageType.PONG)
    raise UnknownMessageError(f"klondike: unknown message type {typ!r}")


# Cap'n Proto: Side enum ordinals of the schema.
_SIDES = {0: Side.UNKNOWN, 1: Side.BUY, 2: Side.SELL}

# The overview is a few hundred kB of text: well under this limit (512 MiB).
_TRAVERSAL_LIMIT = 64 << 20


def decode_capnp(data: Union[bytes, bytearray, memoryview]) -> Message:
    """Decodes a server message of ``format=capnp``: a packed ``ServerMessage``
    (a binary frame). Needs pycapnp (``pip install 'klondike[capnp]'``).

    A missing number (NaN) becomes ``None``, as a missing timestamp (0); the
    venue index of a level becomes the venue name, as in JSON."""
    schema = load_schema()
    try:
        root = schema.ServerMessage.from_bytes_packed(bytes(data), traversal_limit_in_words=_TRAVERSAL_LIMIT)
        which = root.which()
    except Exception as e:  # pycapnp raises KjException, a plain Exception
        raise DecodeError(f"klondike: invalid capnp message: {e}") from e
    try:
        if which == "trades":
            return Message(MessageType.TRADES, trades=_capnp_trades(root.trades))
        if which == "orderbook":
            return Message(MessageType.ORDERBOOK, orderbook=_capnp_orderbook(root.orderbook))
        if which == "overviewJson":
            return Message(MessageType.OVERVIEW, overview=Overview.from_dict(json.loads(root.overviewJson)))
        if which in ("subscribed", "unsubscribed"):
            s = getattr(root, which)
            return Message(MessageType(which), subscription=Subscription(channel=s.channel, symbol=s.symbol))
        if which == "error":
            ce = root.error
            return Message(
                MessageType.ERROR,
                error=StreamError(code=ce.code, message=ce.message, channel=ce.channel, symbol=ce.symbol),
            )
        if which == "pong":
            return Message(MessageType.PONG)
    except DecodeError:
        raise
    except Exception as e:
        raise DecodeError(f"klondike: invalid capnp {which} message: {e}") from e
    raise UnknownMessageError(f"klondike: unknown capnp message {which!r}")


def decode(data: Union[str, bytes, bytearray]) -> Message:
    """Decodes a WebSocket frame: text frames are JSON, binary frames Cap'n
    Proto."""
    if isinstance(data, str):
        return decode_json(data)
    return decode_capnp(data)


def _nan_none(v: float) -> Optional[float]:
    return None if math.isnan(v) else v


def _capnp_trades(ct: Any) -> Trades:
    return Trades(
        symbol=ct.symbol,
        trades=[
            Trade(ts=t.ts, venue=t.venue, price=t.price, qty=t.qty, side=_SIDES.get(t.side.raw, Side.UNKNOWN))
            for t in ct.trades
        ],
    )


def _capnp_orderbook(cb: Any) -> Orderbook:
    venues = [
        VenueQuote(
            venue=v.venue,
            ts=v.ts or None,
            bid=_nan_none(v.bid),
            bid_qty=_nan_none(v.bidQty),
            ask=_nan_none(v.ask),
            ask_qty=_nan_none(v.askQty),
        )
        for v in cb.venues
    ]
    names = [v.venue for v in venues]

    def side(levels: Iterable[Any]) -> List[Level]:
        out = []
        for lv in levels:
            i = lv.venue
            if i >= len(names):
                raise DecodeError(f"klondike: level venue index {i} out of {len(names)} venues")
            out.append(Level(price=lv.price, qty=lv.qty, venue=names[i]))
        return out

    return Orderbook(symbol=cb.symbol, ts=cb.ts, venues=venues, bids=side(cb.bids), asks=side(cb.asks))


def client_message(
    op: str,
    channel: Optional[str] = None,
    symbol: Optional[str] = None,
    venues: Optional[List[str]] = None,
    depth: Optional[int] = None,
) -> Dict[str, Any]:
    """A client message (subscribe, unsubscribe or ping), empty fields left
    out as the Go client does."""
    m: Dict[str, Any] = {"op": op}
    if channel:
        m["channel"] = str(channel)
    if symbol:
        m["symbol"] = symbol
    if venues:
        m["venues"] = list(venues)
    if depth:
        m["depth"] = depth
    return m
