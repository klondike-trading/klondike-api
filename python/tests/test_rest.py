"""REST clients against httpx.MockTransport."""

from __future__ import annotations

import json
from typing import Any, Callable, Dict, List

import httpx
import pytest

import klondike

KEY = "kl_test0test0test0test0test0test0te"
BASE = "https://api.example.test"

BOOK = {
    "symbol": "BTC/USDT",
    "ts": 1793706479000,
    "venues": [
        {"venue": "binance", "ts": 1793706478950, "bid": 104250, "bidQty": 1.2, "ask": 104250.1, "askQty": 0.8},
        {"venue": "kraken", "ts": None, "bid": None, "bidQty": None, "ask": 104251, "askQty": 2},
    ],
    "bids": [{"price": 104250, "qty": 1.2, "venue": "binance"}],
    "asks": [{"price": 104250.1, "qty": 0.8, "venue": "binance"}],
}

OVERVIEW: Dict[str, Any] = {
    "ts": 1793706479654,
    "windowsFromTs": 1793702880000,
    "feeds": {"venues": 38, "venuesKnown": 41, "booksLive": 512, "lagMsP50": 42.5},
    "pairs": [
        {
            "symbol": "BTC/USDT:USDT", "base": "BTC", "quote": "USDT:USDT", "market": "perp", "status": "warn",
            "reasons": ["stale:bitget"], "exchanges": 12, "live": 11, "mid": 104250.05, "volume1hUsd": 1.5e9,
            "volumeTopExchange": "binanceusdm", "volumeTopShare": 0.41, "depth10Usd": 2.5e7,
            "depth10PartialShare": 0.02, "imbalance": -0.1,
            "impact": [{
                "notionalUsd": 1e6, "buyAllInBps": 4.2, "sellAllInBps": None, "buyFilled": 1, "sellFilled": 0.8,
                "buyVenues": 5, "sellVenues": 4, "bestSingleBuyAllInBps": 6.1, "bestSingleSellAllInBps": None,
                "bestSingleBuyExchange": "binanceusdm", "bestSingleSellExchange": "okx",
            }],
            "crosses1h": 31, "crossesPositive1h": 2, "maxNetBps1h": 1.7, "basisBps": 3.4,
        }
    ],
    "leaders": [{"exchange": "binance", "share": 0.6}, {"exchange": "okx", "share": 0.4}],
    "implied": {
        "pegs": [{"symbol": "USDC/USDT", "bps": -1.2, "venueSpreadBps": 0.8, "venues": 9}],
        "fx": [{"ccy": "EUR", "implied": 1.081, "direct": 1.0805, "devBps": 4.6}, {"ccy": "TRY", "implied": 0.029}],
        "triangles": [{"cross": "ETH/BTC", "cycle": "USDT>BTC>ETH>USDT", "bestNetBps": -21.5}],
    },
    "paths": [{"sell": "ETH", "buy": "SOL", "quantity": 10, "tokens": ["ETH", "USDT", "SOL"], "costBps": 18.2,
               "directCostBps": None, "ts": 1793706479000}],
}


class Recorder:
    def __init__(self, respond: Callable[[httpx.Request], httpx.Response]) -> None:
        self.requests: List[httpx.Request] = []
        self.respond = respond

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        return self.respond(request)


def routes(request: httpx.Request) -> httpx.Response:
    path = request.url.path
    if path == "/v1/symbols":
        return httpx.Response(200, json={"symbols": [
            {"symbol": "BTC/USDT", "market": "spot", "venues": ["binance", "okx"], "lastTradeTs": 1793706479654}
        ]})
    if path == "/v1/trades":
        return httpx.Response(200, json={"symbol": request.url.params["symbol"], "trades": [
            {"ts": 1793706479654, "venue": "binance", "price": 104250.1, "qty": 0.0125, "side": "buy"},
            {"ts": 1793706479655, "venue": "okx", "price": 104250, "qty": 1, "side": "weird"},
        ]})
    if path == "/v1/orderbook":
        return httpx.Response(200, json=BOOK)
    if path == "/v1/overview":
        return httpx.Response(200, json=OVERVIEW)
    return httpx.Response(404, json={"code": "not_found", "error": "unknown endpoint"})


def make_client(rec: Recorder) -> klondike.Client:
    return klondike.Client(KEY, base_url=BASE + "/", transport=httpx.MockTransport(rec))


def test_symbols_and_headers() -> None:
    rec = Recorder(routes)
    with make_client(rec) as c:
        symbols = c.symbols()
        assert symbols == [klondike.SymbolInfo("BTC/USDT", "spot", ["binance", "okx"], 1793706479654)]
        c.symbols(q="/usdc")
    req = rec.requests[0]
    assert str(req.url) == BASE + "/v1/symbols"
    assert req.headers["X-API-Key"] == KEY
    assert req.headers["Accept"] == "application/json"
    assert req.headers["User-Agent"] == f"klondike-python/{klondike.__version__}"
    assert rec.requests[1].url.params["q"] == "/usdc"


def test_trades_parameters() -> None:
    rec = Recorder(routes)
    with make_client(rec) as c:
        t = c.trades("BTC/USDT")
        c.trades("ETH/USDT:USDT", limit=10, since=1793706479000, venues=["binanceusdm", "bybit"])
        c.trades("ETH/USDT", venues="okx, kraken")
    assert t.symbol == "BTC/USDT"
    assert t.trades[0] == klondike.Trade(1793706479654, "binance", 104250.1, 0.0125, klondike.Side.BUY)
    assert t.trades[1].side is klondike.Side.UNKNOWN
    assert dict(rec.requests[0].url.params) == {"symbol": "BTC/USDT"}
    assert dict(rec.requests[1].url.params) == {
        "symbol": "ETH/USDT:USDT", "limit": "10", "since": "1793706479000", "venues": "binanceusdm,bybit",
    }
    assert rec.requests[2].url.params["venues"] == "okx,kraken"


def test_orderbook() -> None:
    rec = Recorder(routes)
    with make_client(rec) as c:
        ob = c.orderbook("BTC/USDT", depth=5, venues=["binance"])
    assert dict(rec.requests[0].url.params) == {"symbol": "BTC/USDT", "depth": "5", "venues": "binance"}
    assert ob.venues[1] == klondike.VenueQuote("kraken", None, None, None, 104251.0, 2.0)
    assert ob.bids == [klondike.Level(104250.0, 1.2, "binance")]


def test_overview() -> None:
    rec = Recorder(routes)
    with make_client(rec) as c:
        ov = c.overview()
    assert ov.windows_from_ts == 1793702880000
    assert ov.feeds == klondike.Feeds(38, 41, 512, 42.5)
    p = ov.pairs[0]
    assert (p.market, p.status, p.reasons, p.volume_1h_usd, p.depth10_usd) == ("perp", "warn", ["stale:bitget"], 1.5e9, 2.5e7)
    assert (p.crosses_1h, p.crosses_positive_1h, p.max_net_bps_1h, p.basis_bps) == (31, 2, 1.7, 3.4)
    imp = p.impact[0]
    assert imp.sell_all_in_bps is None and imp.best_single_buy_all_in_bps == 6.1
    assert imp.best_single_sell_exchange == "okx"
    assert ov.implied.fx[1] == klondike.ImpliedFX("TRY", 0.029, None, None)
    assert ov.implied.pegs[0].venue_spread_bps == 0.8
    assert ov.implied.triangles[0].best_net_bps == -21.5
    assert ov.paths[0].tokens == ["ETH", "USDT", "SOL"] and ov.paths[0].direct_cost_bps is None


@pytest.mark.parametrize(
    "status,body,headers,cls,code,message,retry_after",
    [
        (400, {"code": "bad_request", "error": "symbol is required"}, {}, klondike.BadRequestError,
         "bad_request", "symbol is required", None),
        (401, {"code": "unauthorized", "error": "unknown API key"}, {}, klondike.UnauthorizedError,
         "unauthorized", "unknown API key", None),
        (404, {"code": "not_found", "error": "no order book for X/Y"}, {}, klondike.NotFoundError,
         "not_found", "no order book for X/Y", None),
        (429, {"code": "rate_limited", "error": "rate limit exceeded"}, {"Retry-After": "3"},
         klondike.RateLimitError, "rate_limited", "rate limit exceeded", 3.0),
        (503, {"code": "unavailable", "error": "no overview yet"}, {}, klondike.ServiceUnavailableError,
         "unavailable", "no overview yet", None),
        (502, "<html>bad gateway</html>", {}, klondike.APIError, "Bad Gateway", "<html>bad gateway</html>", None),
    ],
)
def test_error_mapping(
    status: int, body: Any, headers: Dict[str, str], cls: type, code: str, message: str, retry_after: Any
) -> None:
    def respond(_: httpx.Request) -> httpx.Response:
        if isinstance(body, str):
            return httpx.Response(status, text=body, headers=headers)
        return httpx.Response(status, json=body, headers=headers)

    with make_client(Recorder(respond)) as c:
        with pytest.raises(cls) as info:
            c.orderbook("X/Y")
    e = info.value
    assert type(e) is cls
    assert isinstance(e, klondike.APIError) and isinstance(e, klondike.KlondikeError)
    assert (e.status, e.code, e.message, e.retry_after) == (status, code, message, retry_after)
    assert str(e) == f"klondike: {status} {code}: {message}"


def test_invalid_json_answer() -> None:
    with make_client(Recorder(lambda _: httpx.Response(200, text="{"))) as c:
        with pytest.raises(klondike.DecodeError):
            c.overview()


def test_api_key_from_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("KLONDIKE_API_KEY", KEY)
    assert klondike.Client().api_key == KEY
    monkeypatch.delenv("KLONDIKE_API_KEY")
    with pytest.raises(klondike.KlondikeError):
        klondike.Client()


async def test_async_client() -> None:
    rec = Recorder(routes)
    async with klondike.AsyncClient(KEY, base_url=BASE, transport=httpx.MockTransport(rec)) as c:
        assert (await c.symbols())[0].symbol == "BTC/USDT"
        assert (await c.trades("BTC/USDT", limit=2)).trades[0].venue == "binance"
        assert (await c.orderbook("BTC/USDT")).symbol == "BTC/USDT"
        assert (await c.overview()).pairs[0].symbol == "BTC/USDT:USDT"
        with pytest.raises(klondike.NotFoundError):
            await c._get("/v1/nope")
    assert rec.requests[1].url.params["limit"] == "2"
    assert all(r.headers["X-API-Key"] == KEY for r in rec.requests)


async def test_async_rate_limit() -> None:
    def respond(_: httpx.Request) -> httpx.Response:
        return httpx.Response(429, content=json.dumps({"code": "rate_limited", "error": "slow down"}),
                              headers={"Retry-After": "12"})

    async with klondike.AsyncClient(KEY, base_url=BASE, transport=httpx.MockTransport(respond)) as c:
        with pytest.raises(klondike.RateLimitError) as info:
            await c.trades("BTC/USDT")
    assert info.value.retry_after == 12.0
