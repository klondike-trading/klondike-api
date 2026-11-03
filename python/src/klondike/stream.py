"""WebSocket streams of the Klondike API (``GET /v1/ws``)."""

from __future__ import annotations

import asyncio
import json
import logging
from typing import TYPE_CHECKING, Any, Dict, List, Literal, Optional, Tuple, Type, Union

from websockets.asyncio.client import ClientConnection, connect
from websockets.exceptions import ConnectionClosed, InvalidStatus

from ._capnp import load_schema
from .client import USER_AGENT, Venues, venue_list
from .errors import APIError, DecodeError, StreamClosedError, StreamError, UnauthorizedError, api_error
from .messages import Channel, Message, Subscription, UnknownMessageError, client_message, decode

if TYPE_CHECKING:
    from types import TracebackType

Format = Literal["json", "capnp"]
"""Encoding of the server messages."""

__all__ = ["Stream", "Format"]

log = logging.getLogger("klondike")

# The overview is a few hundred kB; a book of 200 levels much less.
MAX_MESSAGE_SIZE = 64 << 20

# Error codes refusing a subscription for good.
_REFUSED = frozenset({"bad_request", "unknown_channel", "too_many_subscriptions"})

# Close code of the server for a policy violation: reconnecting would not help.
_POLICY_VIOLATION = 1008


def _channel(c: Union[Channel, str]) -> str:
    return c.value if isinstance(c, Channel) else str(c)


class Stream:
    """A WebSocket connection to the API that reconnects on its own and sends
    the subscriptions again after a reconnection.

    Get one from :meth:`AsyncClient.stream` and open it with ``async with``
    (or ``await``)::

        async with client.stream(format="capnp") as stream:
            await stream.subscribe_trades("BTC/USDT")
            await stream.subscribe_orderbook("BTC/USDT", depth=10)
            async for msg in stream:
                if msg.type == "trades":
                    print(msg.trades)

    Iterating yields :class:`~klondike.Message` objects: data,
    acknowledgements of the subscriptions, errors and pongs, the same whatever
    the format. The connection is (re)opened while iterating: read the stream
    from one task at a time; subscribing, pinging and closing work from any
    task. The iteration stops after :meth:`close`; it raises
    :class:`~klondike.UnauthorizedError` when the key is refused at a
    reconnection and :class:`~klondike.StreamClosedError` when the connection
    is lost with reconnection disabled.
    """

    def __init__(
        self,
        base_url: str,
        api_key: str,
        *,
        format: Format = "json",
        reconnect: bool = True,
        min_backoff: float = 0.5,
        max_backoff: float = 30.0,
        open_timeout: Optional[float] = 10.0,
    ) -> None:
        if format not in ("json", "capnp"):
            raise ValueError("klondike: format must be 'json' or 'capnp'")
        if format == "capnp":
            load_schema()  # MissingDependencyError now rather than on the first frame
        url = base_url.rstrip("/")
        if url.startswith("https://"):
            url = "wss://" + url[len("https://") :]
        elif url.startswith("http://"):
            url = "ws://" + url[len("http://") :]
        self._url = f"{url}/v1/ws?format={format}"
        self._api_key = api_key
        self._format = format
        self._reconnect = reconnect
        self._min_backoff = max(min_backoff, 0.0)
        self._max_backoff = max(max_backoff, self._min_backoff)
        self._open_timeout = open_timeout

        self._ws: Optional[ClientConnection] = None
        self._subs: Dict[Subscription, Dict[str, Any]] = {}
        self._started = False
        self._closed = False
        self._closing: Optional[asyncio.Event] = None
        self._backoff = self._min_backoff
        self._retry_after = 0.0
        self._error: Optional[BaseException] = None

    def __repr__(self) -> str:
        state = "closed" if self._closed else "connected" if self._ws is not None else "not connected"
        return f"Stream({self._url!r}, {state}, {len(self._subs)} subscriptions)"

    @property
    def url(self) -> str:
        """The WebSocket URL."""
        return self._url

    @property
    def format(self) -> str:
        """The encoding of the server messages, ``json`` or ``capnp``."""
        return self._format

    @property
    def connected(self) -> bool:
        """Whether a connection is open (False while reconnecting)."""
        return self._ws is not None

    @property
    def subscriptions(self) -> List[Subscription]:
        """The current subscriptions, sent again after a reconnection."""
        return list(self._subs)

    @property
    def error(self) -> Optional[BaseException]:
        """Why the stream ended, None while it runs or after :meth:`close`."""
        return self._error

    # Opening and closing.

    async def connect(self) -> Stream:
        """Opens the connection and sends the subscriptions made so far. A
        refused key raises :class:`~klondike.UnauthorizedError`, another
        refusal :class:`~klondike.APIError`, an unreachable server the error of
        the connection. Does nothing when already connected."""
        if self._closed:
            raise self._ended()
        if self._ws is not None:
            return self
        self._event()
        ws = await self._dial()
        self._started = True
        if self._closed:
            await ws.close()
            raise self._ended()
        try:
            await self._open(ws)
        except ConnectionClosed as e:
            if not self._reconnect:
                self._finish(StreamClosedError("klondike: connection closed", *_close_of(e)))
                raise self._ended() from e
            # recv() reconnects.
        return self

    def __await__(self) -> Any:
        return self.connect().__await__()

    async def __aenter__(self) -> Stream:
        return await self.connect()

    async def __aexit__(
        self,
        exc_type: Optional[Type[BaseException]],
        exc: Optional[BaseException],
        tb: Optional[TracebackType],
    ) -> None:
        await self.close()

    async def close(self) -> None:
        """Closes the connection and ends the stream: the iteration stops."""
        self._closed = True
        if self._closing is not None:
            self._closing.set()
        ws, self._ws = self._ws, None
        if ws is not None:
            await ws.close()

    # Subscriptions.

    async def subscribe_trades(self, symbol: str, venues: Venues = None) -> None:
        """Streams the trades of the symbol, of the ``venues`` given (all when
        None), batched every 100 ms. Subscribing again replaces the venues."""
        await self._subscribe(client_message("subscribe", Channel.TRADES.value, symbol, venue_list(venues)))

    async def subscribe_orderbook(self, symbol: str, depth: int = 20, venues: Venues = None) -> None:
        """Streams the merged book of the symbol, ``depth`` levels per side (1
        to 200), of the ``venues`` given (all when None), at most four times a
        second. Subscribing again replaces the depth and the venues."""
        await self._subscribe(client_message("subscribe", Channel.ORDERBOOK.value, symbol, venue_list(venues), depth))

    async def subscribe_overview(self) -> None:
        """Streams the market overview, whole, each time it changes."""
        await self._subscribe(client_message("subscribe", Channel.OVERVIEW.value))

    async def unsubscribe(self, channel: Union[Channel, str], symbol: Optional[str] = None) -> None:
        """Stops a stream (no symbol for the overview)."""
        ch = _channel(channel)
        sym = "" if ch == Channel.OVERVIEW.value else (symbol or "")
        self._subs.pop(Subscription(ch, sym), None)
        await self._send(client_message("unsubscribe", ch, sym))

    async def ping(self) -> None:
        """Asks the server for a ``pong`` message (dropped while reconnecting)."""
        await self._send(client_message("ping"))

    async def _subscribe(self, m: Dict[str, Any]) -> None:
        if self._closed:
            raise self._ended()
        self._subs[Subscription(m["channel"], m.get("symbol", ""))] = m
        await self._send(m)

    async def _send(self, m: Dict[str, Any]) -> None:
        if self._closed:
            raise self._ended()
        ws = self._ws
        if ws is None:
            return  # not connected yet or reconnecting: the subscriptions follow
        try:
            await ws.send(json.dumps(m, separators=(",", ":")))
        except ConnectionClosed as e:
            if not self._reconnect:
                raise StreamClosedError("klondike: connection closed", *_close_of(e)) from e
            # The reader reconnects and subscribes again.

    # Reading.

    def __aiter__(self) -> Stream:
        return self

    async def __anext__(self) -> Message:
        try:
            return await self.recv()
        except StreamClosedError:
            if self._error is None:
                raise StopAsyncIteration from None
            raise

    async def recv(self) -> Message:
        """The next server message. Opens the connection if needed and
        reconnects when it is lost. Raises :class:`~klondike.StreamClosedError`
        once the stream is closed."""
        while True:
            if self._closed:
                raise self._ended()
            ws = self._ws
            if ws is None:
                if self._started:
                    await self._reconnect_loop()
                else:
                    await self.connect()
                continue
            try:
                data = await ws.recv()
            except ConnectionClosed as e:
                self._lost(ws, e)
                continue
            try:
                msg = decode(data)
            except UnknownMessageError as e:
                log.debug("%s: skipped", e)
            except DecodeError as e:
                # As the Go client: a frame that cannot be decoded ends the
                # connection, the next one starts afresh.
                log.warning("%s: closing the connection", e)
                await ws.close()
                self._lost(ws, e)
                continue
            if msg.error is not None:
                self._drop_refused(msg.error)
            return msg

    def _drop_refused(self, e: StreamError) -> None:
        # A subscription the server refuses for good is not sent again after a
        # reconnection (as the Go client). "unavailable" may succeed later.
        if e.code in _REFUSED and e.channel:
            self._subs.pop(Subscription(e.channel, e.symbol), None)

    def _lost(self, ws: ClientConnection, err: BaseException) -> None:
        if self._ws is ws:
            self._ws = None
        if self._closed:
            return
        code, reason = _close_of(err) if isinstance(err, ConnectionClosed) else (None, "")
        about = " ".join(str(p) for p in (code, reason) if p) or "no close frame"
        if isinstance(err, DecodeError) and not self._reconnect:
            self._finish(err)
        elif not self._reconnect or code == _POLICY_VIOLATION:
            closed = StreamClosedError(f"klondike: connection closed ({about})", code, reason)
            closed.__cause__ = err
            self._finish(closed)
        else:
            log.warning("klondike: connection lost (%s), reconnecting", about)

    async def _reconnect_loop(self) -> None:
        # The backoff is kept on the stream: a recv() cancelled by a timeout
        # of the application does not start the waits over.
        while True:
            if await self._wait_closing(max(self._backoff, self._retry_after)):
                raise self._ended()
            self._retry_after = 0.0
            self._backoff = min(self._backoff * 2, self._max_backoff)
            try:
                ws = await self._dial()
            except UnauthorizedError as e:
                self._finish(e)
                raise
            except Exception as e:  # network, 429, 5xx, invalid handshake: retry
                if isinstance(e, APIError) and e.retry_after:
                    self._retry_after = e.retry_after
                log.warning("klondike: reconnection failed: %r", e)
                continue
            if self._closed:
                await ws.close()
                raise self._ended()
            try:
                await self._open(ws)
            except ConnectionClosed:
                self._ws = None
                continue
            self._backoff = self._min_backoff
            log.info("klondike: reconnected to %s", self._url)
            return

    async def _dial(self) -> ClientConnection:
        try:
            return await connect(
                self._url,
                additional_headers={"X-API-Key": self._api_key},
                user_agent_header=USER_AGENT,
                max_size=MAX_MESSAGE_SIZE,
                open_timeout=self._open_timeout,
            )
        except InvalidStatus as e:
            r = e.response
            raise api_error(r.status_code, r.headers, bytes(r.body or b"")) from e

    async def _open(self, ws: ClientConnection) -> None:
        self._ws = ws
        try:
            for m in list(self._subs.values()):
                await ws.send(json.dumps(m, separators=(",", ":")))
        except BaseException:
            # Lost, or cancelled half way: a connection missing subscriptions
            # must not be used.
            if self._ws is ws:
                self._ws = None
            asyncio.ensure_future(ws.close())
            raise

    def _event(self) -> asyncio.Event:
        # Created in the running loop (Python 3.9 binds events at creation).
        if self._closing is None:
            self._closing = asyncio.Event()
            if self._closed:
                self._closing.set()
        return self._closing

    async def _wait_closing(self, delay: float) -> bool:
        try:
            await asyncio.wait_for(self._event().wait(), delay)
        except asyncio.TimeoutError:
            return False
        return True

    def _finish(self, err: BaseException) -> None:
        self._error = err
        self._closed = True
        if self._closing is not None:
            self._closing.set()

    def _ended(self) -> BaseException:
        if self._error is not None:
            return self._error
        return StreamClosedError("klondike: stream closed")


def _close_of(e: ConnectionClosed) -> Tuple[Optional[int], str]:
    frame = e.rcvd or e.sent
    if frame is None:
        return None, ""
    return frame.code, frame.reason
