"""REST clients of the Klondike API: :class:`Client` (sync) and
:class:`AsyncClient` (async, which also opens WebSocket streams)."""

from __future__ import annotations

import os
from typing import TYPE_CHECKING, Any, Dict, Iterable, List, Optional, Type, Union

import httpx

from ._version import __version__
from .errors import DecodeError, KlondikeError, api_error
from .models import Orderbook, Overview, SymbolInfo, Trades

if TYPE_CHECKING:
    from types import TracebackType

    from .stream import Format, Stream

__all__ = ["Client", "AsyncClient", "DEFAULT_BASE_URL", "USER_AGENT"]

DEFAULT_BASE_URL = "https://api.klondike-crypto.com"
"""The production API."""

USER_AGENT = f"klondike-python/{__version__}"

Venues = Union[str, Iterable[str], None]
"""CCXT exchange ids, as a list or a comma separated string; all when None."""


def venue_list(venues: Venues) -> Optional[List[str]]:
    if venues is None:
        return None
    if isinstance(venues, str):
        items = venues.split(",")
    else:
        items = list(venues)
    out = [v.strip() for v in items if v and v.strip()]
    return out or None


def _trades_params(symbol: str, limit: Optional[int], since: Optional[int], venues: Venues) -> Dict[str, Any]:
    params: Dict[str, Any] = {"symbol": symbol}
    if limit is not None:
        params["limit"] = int(limit)
    if since is not None:
        params["since"] = int(since)
    v = venue_list(venues)
    if v:
        params["venues"] = ",".join(v)
    return params


def _orderbook_params(symbol: str, depth: Optional[int], venues: Venues) -> Dict[str, Any]:
    params: Dict[str, Any] = {"symbol": symbol}
    if depth is not None:
        params["depth"] = int(depth)
    v = venue_list(venues)
    if v:
        params["venues"] = ",".join(v)
    return params


class _Base:
    def __init__(self, api_key: Optional[str], base_url: str) -> None:
        key = api_key if api_key is not None else os.environ.get("KLONDIKE_API_KEY")
        if not key:
            raise KlondikeError("klondike: an API key is required (argument or KLONDIKE_API_KEY)")
        self.api_key: str = key
        self.base_url: str = base_url.rstrip("/")
        self._headers = {"X-API-Key": key, "Accept": "application/json", "User-Agent": USER_AGENT}

    def __repr__(self) -> str:
        return f"{type(self).__name__}(base_url={self.base_url!r})"

    @staticmethod
    def _decode(resp: httpx.Response) -> Any:
        if resp.status_code != 200:
            raise api_error(resp.status_code, resp.headers, resp.content)
        try:
            return resp.json()
        except ValueError as e:
            raise DecodeError(f"klondike: invalid JSON answer: {e}") from e


class Client(_Base):
    """Synchronous client of the REST endpoints.

    ::

        with klondike.Client("kl_...") as client:
            book = client.orderbook("BTC/USDT", depth=10)

    ``api_key`` defaults to the ``KLONDIKE_API_KEY`` environment variable.
    ``transport`` or ``http_client`` replace the default httpx transport or
    client (tests, proxies). Non-200 answers raise :class:`~klondike.APIError`
    (or a subclass); network errors are those of httpx.
    """

    def __init__(
        self,
        api_key: Optional[str] = None,
        base_url: str = DEFAULT_BASE_URL,
        *,
        timeout: float = 30.0,
        transport: Optional[httpx.BaseTransport] = None,
        http_client: Optional[httpx.Client] = None,
    ) -> None:
        super().__init__(api_key, base_url)
        self._owns_http = http_client is None
        self._http = http_client or httpx.Client(timeout=timeout, transport=transport)

    def _get(self, path: str, params: Optional[Dict[str, Any]] = None) -> Any:
        resp = self._http.get(self.base_url + path, params=params, headers=self._headers)
        return self._decode(resp)

    def symbols(self, q: Optional[str] = None) -> List[SymbolInfo]:
        """The symbols with recent trades, sorted; ``q`` filters them on a case
        insensitive substring (``"btc"``, ``"/usdc"``)."""
        doc = self._get("/v1/symbols", {"q": q} if q else None)
        return [SymbolInfo.from_dict(s) for s in doc.get("symbols") or []]

    def trades(
        self, symbol: str, limit: Optional[int] = None, since: Optional[int] = None, venues: Venues = None
    ) -> Trades:
        """The recent trades of the symbol on every venue, oldest first: the
        ``limit`` most recent (1 to 1000, default 100), only those strictly
        after ``since`` (ms) when given, of the ``venues`` given (all when
        None)."""
        return Trades.from_dict(self._get("/v1/trades", _trades_params(symbol, limit, since, venues)))

    def orderbook(self, symbol: str, depth: Optional[int] = None, venues: Venues = None) -> Orderbook:
        """The merged order book of the symbol, ``depth`` levels per side (1 to
        1000, default 50), of the ``venues`` given (all when None). Raises
        :class:`~klondike.NotFoundError` for a symbol no venue streams."""
        return Orderbook.from_dict(self._get("/v1/orderbook", _orderbook_params(symbol, depth, venues)))

    def overview(self) -> Overview:
        """The market overview."""
        return Overview.from_dict(self._get("/v1/overview"))

    def close(self) -> None:
        """Closes the HTTP connections (not those of an ``http_client`` given)."""
        if self._owns_http:
            self._http.close()

    def __enter__(self) -> Client:
        return self

    def __exit__(
        self,
        exc_type: Optional[Type[BaseException]],
        exc: Optional[BaseException],
        tb: Optional[TracebackType],
    ) -> None:
        self.close()


class AsyncClient(_Base):
    """Asynchronous client of the REST endpoints and of the WebSocket streams.

    ::

        async with klondike.AsyncClient("kl_...") as client:
            trades = await client.trades("BTC/USDT", limit=10)
            async with client.stream() as stream:
                await stream.subscribe_trades("BTC/USDT")
                async for msg in stream:
                    ...
    """

    def __init__(
        self,
        api_key: Optional[str] = None,
        base_url: str = DEFAULT_BASE_URL,
        *,
        timeout: float = 30.0,
        transport: Optional[httpx.AsyncBaseTransport] = None,
        http_client: Optional[httpx.AsyncClient] = None,
    ) -> None:
        super().__init__(api_key, base_url)
        self._owns_http = http_client is None
        self._http = http_client or httpx.AsyncClient(timeout=timeout, transport=transport)

    async def _get(self, path: str, params: Optional[Dict[str, Any]] = None) -> Any:
        resp = await self._http.get(self.base_url + path, params=params, headers=self._headers)
        return self._decode(resp)

    async def symbols(self, q: Optional[str] = None) -> List[SymbolInfo]:
        """See :meth:`Client.symbols`."""
        doc = await self._get("/v1/symbols", {"q": q} if q else None)
        return [SymbolInfo.from_dict(s) for s in doc.get("symbols") or []]

    async def trades(
        self, symbol: str, limit: Optional[int] = None, since: Optional[int] = None, venues: Venues = None
    ) -> Trades:
        """See :meth:`Client.trades`."""
        return Trades.from_dict(await self._get("/v1/trades", _trades_params(symbol, limit, since, venues)))

    async def orderbook(self, symbol: str, depth: Optional[int] = None, venues: Venues = None) -> Orderbook:
        """See :meth:`Client.orderbook`."""
        return Orderbook.from_dict(await self._get("/v1/orderbook", _orderbook_params(symbol, depth, venues)))

    async def overview(self) -> Overview:
        """See :meth:`Client.overview`."""
        return Overview.from_dict(await self._get("/v1/overview"))

    def stream(
        self,
        format: Format = "json",
        *,
        reconnect: bool = True,
        min_backoff: float = 0.5,
        max_backoff: float = 30.0,
        open_timeout: Optional[float] = 10.0,
    ) -> Stream:
        """A WebSocket stream, opened by ``async with`` (or ``await``).

        ``format`` is the encoding of the server messages, ``"json"`` or
        ``"capnp"`` (needs ``klondike[capnp]``): the messages are the same. A
        lost connection is reopened after a backoff growing from
        ``min_backoff`` to ``max_backoff`` seconds, and the subscriptions are
        sent again, unless ``reconnect`` is False."""
        from .stream import Stream

        return Stream(
            self.base_url,
            self.api_key,
            format=format,
            reconnect=reconnect,
            min_backoff=min_backoff,
            max_backoff=max_backoff,
            open_timeout=open_timeout,
        )

    async def aclose(self) -> None:
        """Closes the HTTP connections (not those of an ``http_client`` given)."""
        if self._owns_http:
            await self._http.aclose()

    async def __aenter__(self) -> AsyncClient:
        return self

    async def __aexit__(
        self,
        exc_type: Optional[Type[BaseException]],
        exc: Optional[BaseException],
        tb: Optional[TracebackType],
    ) -> None:
        await self.aclose()
