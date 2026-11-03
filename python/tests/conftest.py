from __future__ import annotations

from pathlib import Path

import pytest

# The contract lives next to the libraries in the klondike-api repository.
REPO = Path(__file__).resolve().parents[2]
FIXTURES = REPO / "testdata" / "messages"
SCHEMA = REPO / "schema" / "klondike_api.capnp"


def fixture_names() -> list[str]:
    if not FIXTURES.is_dir():
        return []
    return sorted(p.stem for p in FIXTURES.glob("*.json"))


def require_contract() -> None:
    if not FIXTURES.is_dir():
        pytest.skip(f"{FIXTURES} not found: run the tests from the klondike-api repository")
