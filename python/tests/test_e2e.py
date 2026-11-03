"""End to end against a running API, skipped unless KLONDIKE_TEST_URL is set.

    KLONDIKE_TEST_URL=http://localhost:8093 \\
    KLONDIKE_TEST_KEY=kl_dev0dev0dev0dev0dev0dev0dev0dev0 pytest tests/test_e2e.py

(api-go/dev.sh starts a local stack whose fake feed publishes BTC/USDT,
ETH/USDT, BTC/USDT:USDT and SOL/USDC.)
"""

from __future__ import annotations

import asyncio
import os
import time
from typing import Any, Dict, Set

import pytest

import klondike

URL = os.environ.get("KLONDIKE_TEST_URL", "")
KEY = os.environ.get("KLONDIKE_TEST_KEY", "kl_dev0dev0dev0dev0dev0dev0dev0dev0")
SYMBOL = "BTC/USDT"

pytestmark = pytest.mark.skipif(not URL, reason="KLONDIKE_TEST_URL not set")


def test_rest_sync() -> None:
    with klondike.Client(KEY, base_url=URL) as c:
        symbols = c.symbols()
        assert SYMBOL in {s.symbol for s in symbols}
        btc = next(s for s in symbols if s.symbol == SYMBOL)
        assert btc.market == "spot" and btc.venues and btc.last_trade_ts > 0
        assert all("usdc" in s.symbol.lower() for s in c.symbols(q="usdc"))

        trades = c.trades(SYMBOL, limit=5)
        assert trades.symbol == SYMBOL and 0 < len(trades.trades) <= 5
        assert all(t.price > 0 and t.qty > 0 and t.side in ("buy", "sell", "unknown") for t in trades.trades)
        assert [t.ts for t in trades.trades] == sorted(t.ts for t in trades.trades)
        venue = trades.trades[-1].venue
        assert {t.venue for t in c.trades(SYMBOL, limit=20, venues=[venue]).trades} == {venue}
        since = trades.trades[-1].ts
        assert all(t.ts > since for t in c.trades(SYMBOL, since=since).trades)

        book = c.orderbook(SYMBOL, depth=5)
        assert book.symbol == SYMBOL and book.ts > 0
        assert 0 < len(book.bids) <= 5 and 0 < len(book.asks) <= 5
        names = {v.venue for v in book.venues}
        assert all(lv.venue in names for lv in book.bids + book.asks)
        assert book.bids[0].price <= book.asks[0].price or len(names) > 1  # merged books may cross
        assert [lv.price for lv in book.bids] == sorted((lv.price for lv in book.bids), reverse=True)

        ov = c.overview()
        assert ov.ts > 0 and ov.pairs
        assert SYMBOL in {p.symbol for p in ov.pairs}
        assert ov.pairs[0].impact and ov.pairs[0].impact[0].notional_usd > 0

        with pytest.raises(klondike.BadRequestError) as bad:
            c.trades(SYMBOL, limit=5000)
        assert bad.value.code == "bad_request"
        with pytest.raises(klondike.NotFoundError):
            c.orderbook("NOPE/NOWHERE")

    with klondike.Client("kl_" + "0" * 32, base_url=URL) as c:
        with pytest.raises(klondike.UnauthorizedError) as info:
            c.symbols()
        assert info.value.code == "unauthorized"


async def test_rest_async() -> None:
    async with klondike.AsyncClient(KEY, base_url=URL) as c:
        symbols, trades, book, ov = await asyncio.gather(
            c.symbols(), c.trades(SYMBOL, limit=3), c.orderbook(SYMBOL, depth=3), c.overview()
        )
        assert SYMBOL in {s.symbol for s in symbols}
        assert len(trades.trades) <= 3 and len(book.bids) <= 3 and ov.pairs


@pytest.mark.parametrize("fmt", ["json", "capnp"])
async def test_stream(fmt: Any) -> None:
    async with klondike.AsyncClient(KEY, base_url=URL) as c:
        async with c.stream(format=fmt) as stream:
            await stream.subscribe_trades(SYMBOL)
            await stream.subscribe_orderbook(SYMBOL, depth=5)
            await stream.subscribe_overview()
            await stream.subscribe_trades("")  # an error, the connection stays open
            await stream.ping()

            seen: Dict[str, Any] = {}
            acks: Set[str] = set()
            deadline = time.monotonic() + 20
            want = {"trades", "orderbook", "overview", "pong", "error"}
            while not want <= set(seen) or len(acks) < 3:
                assert time.monotonic() < deadline, f"missing {want - set(seen)}, acks {acks}"
                msg = await asyncio.wait_for(stream.__anext__(), 20)
                seen.setdefault(msg.type.value, msg)
                if msg.type == "subscribed":
                    assert msg.subscription is not None
                    acks.add(msg.subscription.channel)

            assert acks == {"trades", "orderbook", "overview"}
            trades = seen["trades"].trades
            assert trades.symbol == SYMBOL and trades.trades and trades.trades[0].price > 0
            book = seen["orderbook"].orderbook
            assert book.symbol == SYMBOL and 0 < len(book.bids) <= 5 and 0 < len(book.asks) <= 5
            names = {v.venue for v in book.venues}
            assert all(lv.venue in names for lv in book.bids + book.asks)
            assert any(v.ts is not None and v.bid is not None for v in book.venues)
            assert seen["overview"].overview.pairs
            err = seen["error"].error
            assert err.code == "bad_request" and err.channel == "trades"

            await stream.unsubscribe("overview")
            while True:
                msg = await asyncio.wait_for(stream.__anext__(), 10)
                if msg.type == "unsubscribed":
                    assert msg.subscription == klondike.Subscription("overview", "")
                    break
            assert {s.channel for s in stream.subscriptions} == {"trades", "orderbook"}


async def test_stream_unauthorized() -> None:
    async with klondike.AsyncClient("kl_" + "0" * 32, base_url=URL) as c:
        with pytest.raises(klondike.UnauthorizedError):
            async with c.stream():
                pass
