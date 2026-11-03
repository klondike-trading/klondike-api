"""Lazy loading of the Cap'n Proto schema shipped in the package (pycapnp is
an optional dependency: ``pip install 'klondike[capnp]'``)."""

from __future__ import annotations

import os
import threading
from typing import Any, Optional

from .errors import MissingDependencyError

SCHEMA_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "schema", "klondike_api.capnp")

_lock = threading.Lock()
_schema: Optional[Any] = None


def capnp_available() -> bool:
    """Whether pycapnp is installed."""
    try:
        import capnp  # noqa: F401
    except ImportError:
        return False
    return True


def load_schema() -> Any:
    """The compiled ``klondike_api.capnp`` module of pycapnp."""
    global _schema
    if _schema is not None:
        return _schema
    with _lock:
        if _schema is None:
            try:
                import capnp
            except ImportError as e:
                raise MissingDependencyError(
                    "klondike: format='capnp' needs pycapnp: pip install 'klondike[capnp]'"
                ) from e
            _schema = capnp.load(SCHEMA_PATH)
    return _schema
