"""Stream against a local fake server replaying the Go fixtures."""

from __future__ import annotations

import asyncio
import json
from http import HTTPStatus
from typing import Any, AsyncIterator, Dict, List, Optional, Union
from urllib.parse import parse_qs, urlsplit

import pytest
from websockets.asyncio.server import ServerConnection, serve
from websockets.http11 import Request, Response

import klondike
from klondike import Message, MessageType, decode_json

from .conftest import FIXTURES, require_contract

KEY = "kl_test0test0test0test0test0test0te"
TIMEOUT = 5.0


class FakeServer:
    """Answers the client messages with the fixtures, in the format asked."""

    def __init__(self) -> None:
        self.received: List[List[Dict[str, Any]]] = []  # client messages, by connection
        self.headers: List[Any] = []
        self.accept = True
        self.close_after_subscribe: Optional[int] = None  # close code, first connection only
        self.url = ""
        self.conns: List[ServerConnection] = []

    def frame(self, fmt: str, name: str) -> Union[str, bytes]:
        if fmt == "capnp":
            return (FIXTURES / f"{name}.capnp").read_bytes()
        return (FIXTURES / f"{name}.json").read_text()

    def error(self, fmt: str, code: str, channel: str, symbol: str) -> Union[str, bytes]:
        if fmt == "capnp":
            from klondike._capnp import load_schema

            err = {"code": code, "message": code, "channel": channel, "symbol": symbol}
            data: bytes = load_schema().ServerMessage.new_message(error=err).to_bytes_packed()
            return data
        return json.dumps({"type": "error", "code": code, "message": code, "channel": channel, "symbol": symbol})

    def process_request(self, conn: ServerConnection, request: Request) -> Optional[Response]:
        self.headers.append(request.headers)
        if request.headers.get("X-API-Key") != KEY or not self.accept:
            return conn.respond(HTTPStatus.UNAUTHORIZED, '{"code":"unauthorized","error":"unknown API key"}\n')
        return None

    async def handler(self, ws: ServerConnection) -> None:
        assert ws.request is not None
        url = urlsplit(ws.request.path)
        assert url.path == "/v1/ws"
        fmt = parse_qs(url.query)["format"][0]
        index = len(self.received)
        got: List[Dict[str, Any]] = []
        self.received.append(got)
        self.conns.append(ws)
        async for raw in ws:
            assert isinstance(raw, str), "the client always sends JSON text frames"
            m = json.loads(raw)
            got.append(m)
            refused = self.refusal(m)
            if refused:
                await ws.send(self.error(fmt, refused, m.get("channel", ""), m.get("symbol", "")))
            elif m["op"] == "ping":
                await ws.send(self.frame(fmt, "6-pong"))
            elif m["op"] == "unsubscribe":
                await ws.send(self.frame(fmt, "4-unsubscribed"))
            elif m["channel"] == "trades":
                await ws.send(self.frame(fmt, "3-subscribed"))
                await ws.send(self.frame(fmt, "0-trades"))
            elif m["channel"] == "orderbook":
                await ws.send(self.frame(fmt, "1-orderbook"))
            elif m["channel"] == "overview":
                await ws.send(self.frame(fmt, "2-overview"))
            if m["op"] == "subscribe" and index == 0 and self.close_after_subscribe:
                await ws.close(self.close_after_subscribe, "bye")
                return


    @staticmethod
    def refusal(m: Dict[str, Any]) -> str:
        if m["op"] != "subscribe":
            return ""
        if m["channel"] not in ("trades", "orderbook", "overview"):
            return "unknown_channel"
        if m["channel"] != "overview" and not m.get("symbol"):
            return "bad_request"
        if m.get("depth", 20) > 200:
            return "bad_request"
        if m.get("symbol") == "MANY/SUBS":
            return "too_many_subscriptions"
        if m.get("symbol") == "NEW/COIN":
            return "unavailable"  # may work later: kept
        return ""


@pytest.fixture
async def server() -> AsyncIterator[FakeServer]:
    require_contract()
    fake = FakeServer()
    async with serve(fake.handler, "127.0.0.1", 0, process_request=fake.process_request) as srv:
        port = list(srv.sockets)[0].getsockname()[1]
        fake.url = f"http://127.0.0.1:{port}"
        yield fake


def client(server: FakeServer, key: str = KEY) -> klondike.AsyncClient:
    return klondike.AsyncClient(key, base_url=server.url)


def fixture(name: str) -> Message:
    return decode_json((FIXTURES / f"{name}.json").read_text())


async def next_msg(stream: klondike.Stream) -> Message:
    return await asyncio.wait_for(stream.__anext__(), TIMEOUT)


@pytest.mark.parametrize("fmt", ["json", "capnp"])
async def test_stream_messages(server: FakeServer, fmt: Any) -> None:
    async with client(server).stream(format=fmt) as stream:
        assert stream.connected and stream.format == fmt
        await stream.subscribe_trades("BTC/USDT", venues=["binance"])
        assert await next_msg(stream) == fixture("3-subscribed")
        assert await next_msg(stream) == fixture("0-trades")
        await stream.subscribe_orderbook("BTC/USDT", depth=5)
        ob = await next_msg(stream)
        assert ob == fixture("1-orderbook") and ob.type is MessageType.ORDERBOOK
        await stream.subscribe_overview()
        assert await next_msg(stream) == fixture("2-overview")
        await stream.ping()
        assert (await next_msg(stream)).type == "pong"
        await stream.unsubscribe(klondike.Channel.OVERVIEW)
        assert await next_msg(stream) == fixture("4-unsubscribed")
        assert stream.subscriptions == [
            klondike.Subscription("trades", "BTC/USDT"),
            klondike.Subscription("orderbook", "BTC/USDT"),
        ]
    assert server.received[0] == [
        {"op": "subscribe", "channel": "trades", "symbol": "BTC/USDT", "venues": ["binance"]},
        {"op": "subscribe", "channel": "orderbook", "symbol": "BTC/USDT", "depth": 5},
        {"op": "subscribe", "channel": "overview"},
        {"op": "ping"},
        {"op": "unsubscribe", "channel": "overview"},
    ]
    assert server.headers[0]["User-Agent"].startswith("klondike-python/")
    # The iteration has stopped.
    with pytest.raises(StopAsyncIteration):
        await stream.__anext__()
    with pytest.raises(klondike.StreamClosedError):
        await stream.ping()


async def test_subscriptions_before_connecting(server: FakeServer) -> None:
    stream = client(server).stream()
    await stream.subscribe_trades("BTC/USDT")
    await stream.subscribe_overview()
    assert not stream.connected
    await stream  # connects and subscribes
    try:
        types = {(await next_msg(stream)).type for _ in range(3)}
        assert types == {"subscribed", "trades", "overview"}
    finally:
        await stream.close()


async def test_reconnects_and_subscribes_again(server: FakeServer) -> None:
    server.close_after_subscribe = 1001  # the server restarts
    async with client(server).stream(min_backoff=0.01) as stream:
        await stream.subscribe_trades("BTC/USDT")
        await stream.subscribe_orderbook("ETH/USDT", depth=7, venues="okx")
        got = [(await next_msg(stream)).type for _ in range(5)]
    # First connection: ack and trades, then closed. Second: the two
    # subscriptions again, in order.
    assert got == ["subscribed", "trades", "subscribed", "trades", "orderbook"]
    assert len(server.received) == 2
    assert server.received[1] == [
        {"op": "subscribe", "channel": "trades", "symbol": "BTC/USDT"},
        {"op": "subscribe", "channel": "orderbook", "symbol": "ETH/USDT", "venues": ["okx"], "depth": 7},
    ]


async def test_unauthorized_at_connect(server: FakeServer) -> None:
    with pytest.raises(klondike.UnauthorizedError) as info:
        async with client(server, key="kl_wrong").stream():
            pass
    assert info.value.status == 401 and info.value.message == "unknown API key"


async def test_unauthorized_at_reconnect_stops(server: FakeServer) -> None:
    server.close_after_subscribe = 1001
    async with client(server).stream(min_backoff=0.01) as stream:
        await stream.subscribe_trades("BTC/USDT")
        assert (await next_msg(stream)).type == "subscribed"
        assert (await next_msg(stream)).type == "trades"
        server.accept = False  # the key is revoked
        with pytest.raises(klondike.UnauthorizedError):
            await next_msg(stream)
        assert isinstance(stream.error, klondike.UnauthorizedError)
        with pytest.raises(klondike.UnauthorizedError):
            await next_msg(stream)


async def test_policy_violation_stops(server: FakeServer) -> None:
    server.close_after_subscribe = 1008
    async with client(server).stream(min_backoff=0.01) as stream:
        await stream.subscribe_trades("BTC/USDT")
        await next_msg(stream)
        await next_msg(stream)
        with pytest.raises(klondike.StreamClosedError) as info:
            await next_msg(stream)
    assert info.value.code == 1008
    assert len(server.received) == 1


async def test_no_reconnect(server: FakeServer) -> None:
    server.close_after_subscribe = 1001
    async with client(server).stream(reconnect=False) as stream:
        await stream.subscribe_trades("BTC/USDT")
        msgs = []
        with pytest.raises(klondike.StreamClosedError) as info:
            async for m in stream:
                msgs.append(m)
    assert [m.type for m in msgs] == ["subscribed", "trades"]
    assert (info.value.code, info.value.reason) == (1001, "bye")


async def test_close_from_another_task_ends_the_iteration(server: FakeServer) -> None:
    async with client(server).stream() as stream:
        await stream.subscribe_trades("BTC/USDT")

        async def read() -> List[Message]:
            return [m async for m in stream]

        reader = asyncio.ensure_future(read())
        await asyncio.sleep(0.2)
        await stream.close()
        msgs = await asyncio.wait_for(reader, TIMEOUT)
    assert [m.type for m in msgs] == ["subscribed", "trades"]


async def test_close_while_reconnecting(server: FakeServer) -> None:
    server.close_after_subscribe = 1001
    stream = await client(server).stream(min_backoff=10, max_backoff=10)
    await stream.subscribe_trades("BTC/USDT")
    await next_msg(stream)
    await next_msg(stream)
    reader = asyncio.ensure_future(stream.__anext__())
    await asyncio.sleep(0.2)  # waiting 10 s before reconnecting
    assert not stream.connected
    await stream.close()
    with pytest.raises(StopAsyncIteration):
        await asyncio.wait_for(reader, TIMEOUT)


def test_websocket_url() -> None:
    c = klondike.AsyncClient(KEY)
    assert c.stream().url == "wss://api.klondike-crypto.com/v1/ws?format=json"
    assert klondike.AsyncClient(KEY, base_url="http://localhost:8093/").stream(format="capnp").url == (
        "ws://localhost:8093/v1/ws?format=capnp"
    )
    with pytest.raises(ValueError):
        c.stream(format="xml")  # type: ignore[arg-type]


@pytest.mark.parametrize("fmt", ["json", "capnp"])
async def test_refused_subscriptions_are_not_sent_again(server: FakeServer, fmt: Any) -> None:
    async with client(server).stream(format=fmt, min_backoff=0.01) as stream:
        await stream.subscribe_trades("BTC/USDT")
        await stream._subscribe({"op": "subscribe", "channel": "bogus", "symbol": "X/Y"})
        await stream.subscribe_trades("")
        await stream.subscribe_orderbook("ETH/USDT", depth=500)
        await stream.subscribe_trades("MANY/SUBS")
        await stream.subscribe_trades("NEW/COIN")
        errors = []
        while len(errors) < 5:
            msg = await next_msg(stream)
            if msg.type == "error":
                assert msg.error is not None
                errors.append((msg.error.code, msg.error.channel, msg.error.symbol))
        assert errors == [
            ("unknown_channel", "bogus", "X/Y"),
            ("bad_request", "trades", ""),
            ("bad_request", "orderbook", "ETH/USDT"),
            ("too_many_subscriptions", "trades", "MANY/SUBS"),
            ("unavailable", "trades", "NEW/COIN"),
        ]
        assert stream.subscriptions == [
            klondike.Subscription("trades", "BTC/USDT"),
            klondike.Subscription("trades", "NEW/COIN"),
        ]
        await server.conns[0].close(1001, "restart")
        while True:
            msg = await next_msg(stream)
            if msg.type == "error" and len(server.received) == 2:
                break  # NEW/COIN refused again on the second connection
    assert server.received[1] == [
        {"op": "subscribe", "channel": "trades", "symbol": "BTC/USDT"},
        {"op": "subscribe", "channel": "trades", "symbol": "NEW/COIN"},
    ]
