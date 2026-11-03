"""The fixtures of ../testdata/messages are the same server messages encoded by
the Go client in both formats: <n>-<type>.json and <n>-<type>.capnp."""

from __future__ import annotations

import json
import sys

import pytest

import klondike
from klondike import (
    Channel,
    Level,
    Message,
    MessageType,
    Side,
    StreamError,
    Subscription,
    Trade,
    VenueQuote,
    decode,
    decode_capnp,
    decode_json,
)
from klondike import _capnp

from .conftest import FIXTURES, SCHEMA, fixture_names, require_contract


def load(name: str) -> tuple[Message, Message]:
    js = decode_json((FIXTURES / f"{name}.json").read_text())
    cp = decode_capnp((FIXTURES / f"{name}.capnp").read_bytes())
    return js, cp


def test_fixtures_present() -> None:
    require_contract()
    names = fixture_names()
    assert len(names) == 7
    for n in names:
        assert (FIXTURES / f"{n}.capnp").is_file(), n


@pytest.mark.parametrize("name", fixture_names() or ["missing"])
def test_both_encodings_decode_to_equal_messages(name: str) -> None:
    require_contract()
    js, cp = load(name)
    assert js == cp
    assert js.type == name.split("-", 1)[1]
    # decode() picks the decoder from the frame type.
    assert decode((FIXTURES / f"{name}.json").read_text()) == js
    assert decode((FIXTURES / f"{name}.capnp").read_bytes()) == js


def test_trades() -> None:
    require_contract()
    js, _ = load("0-trades")
    assert js.type is MessageType.TRADES
    assert js.trades is not None
    assert js.trades.symbol == "BTC/USDT"
    assert js.trades.trades == [
        Trade(ts=1793706479654, venue="binance", price=104250.1, qty=0.0125, side=Side.BUY),
        Trade(ts=1793706479702, venue="okx", price=104249.9, qty=0.4, side=Side.SELL),
        Trade(ts=1793706479800, venue="kraken", price=104250.0, qty=1.0, side=Side.UNKNOWN),
    ]
    assert js.trades.trades[0].side == "buy"
    assert isinstance(js.trades.trades[2].price, float)


def test_orderbook_missing_values_and_level_venues() -> None:
    require_contract()
    js, cp = load("1-orderbook")
    for m in (js, cp):
        ob = m.orderbook
        assert ob is not None
        assert ob.ts == 1793706479000
        assert ob.venues == [
            VenueQuote("binance", 1793706478950, 104250.0, 1.2, 104250.1, 0.8),
            VenueQuote("kraken", 1793706478890, 104249.0, 0.5, None, None),
            VenueQuote("gemini", None, None, None, None, None),
        ]
        assert ob.bids == [Level(104250.0, 1.2, "binance"), Level(104249.0, 0.5, "kraken")]
        assert ob.asks == [Level(104250.1, 0.8, "binance")]


def test_overview() -> None:
    require_contract()
    js, _ = load("2-overview")
    ov = js.overview
    assert ov is not None
    assert ov.ts == 1
    assert ov.pairs[0].symbol == "BTC/USDT"
    assert ov.pairs[0].basis_bps == 1.5
    assert ov.pairs[0].depth10_partial_share is None
    assert ov.pairs[0].reasons == []
    assert ov.pairs[0].impact[0].notional_usd == 100000.0
    assert ov.pairs[0].impact[0].buy_all_in_bps == 4.2
    assert ov.pairs[0].impact[0].sell_all_in_bps is None
    assert ov.pairs[0].market == "spot"
    assert ov.leaders[0].exchange == "binance" and ov.leaders[0].share == 1.0
    assert ov.implied.pegs == [] and ov.paths == []


def test_control_messages() -> None:
    require_contract()
    assert load("3-subscribed")[0].subscription == Subscription("trades", "BTC/USDT")
    # The overview has no symbol: the field is left out of the JSON.
    assert "symbol" not in json.loads((FIXTURES / "4-unsubscribed.json").read_text())
    unsub = load("4-unsubscribed")[0]
    assert unsub.type is MessageType.UNSUBSCRIBED
    assert unsub.subscription == Subscription(Channel.OVERVIEW, "")
    err = load("5-error")[0].error
    assert err == StreamError(code="bad_request", message="symbol is required", channel="trades", symbol="")
    assert isinstance(err, klondike.KlondikeError)
    assert str(err) == "klondike: symbol is required (bad_request trades)"
    assert load("6-pong")[0] == Message(MessageType.PONG)


def test_unknown_and_invalid_messages() -> None:
    with pytest.raises(klondike.UnknownMessageError):
        decode_json('{"type":"novelty"}')
    with pytest.raises(klondike.DecodeError):
        decode_json("not json")
    with pytest.raises(klondike.DecodeError):
        decode_capnp(b"\x00\x01garbage")


def test_schema_copy_is_identical_to_the_contract() -> None:
    if not SCHEMA.is_file():
        pytest.skip(f"{SCHEMA} not found")
    with open(_capnp.SCHEMA_PATH, "rb") as f:
        shipped = f.read()
    assert shipped == SCHEMA.read_bytes(), "copy schema/klondike_api.capnp into src/klondike/schema/"


def test_capnp_without_pycapnp(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(_capnp, "_schema", None)
    monkeypatch.setitem(sys.modules, "capnp", None)  # import capnp raises ImportError
    assert not klondike.capnp_available()
    with pytest.raises(klondike.MissingDependencyError, match=r"klondike\[capnp\]"):
        decode_capnp(b"")
    client = klondike.AsyncClient("kl_test")
    with pytest.raises(ImportError, match="pycapnp"):
        client.stream(format="capnp")
    client.stream(format="json")  # JSON needs nothing more
