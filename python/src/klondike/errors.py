"""Exceptions of the Klondike client."""

from __future__ import annotations

import json
from dataclasses import dataclass
from http import HTTPStatus
from typing import Any, Dict, Mapping, Optional, Type

__all__ = [
    "KlondikeError",
    "APIError",
    "BadRequestError",
    "UnauthorizedError",
    "NotFoundError",
    "RateLimitError",
    "ServiceUnavailableError",
    "StreamError",
    "StreamClosedError",
    "DecodeError",
    "MissingDependencyError",
]


class KlondikeError(Exception):
    """Base class of the errors of this library."""


class APIError(KlondikeError):
    """An error answer of the API (any status but 200).

    ``code`` is the machine readable code of the body (``bad_request``,
    ``unauthorized``, ``not_found``, ``rate_limited``, ``unavailable``...),
    ``message`` the human readable one, ``retry_after`` the seconds to wait
    given by the ``Retry-After`` header (on 429), ``None`` otherwise.
    """

    def __init__(self, status: int, code: str, message: str, retry_after: Optional[float] = None) -> None:
        super().__init__(status, code, message, retry_after)
        self.status = status
        self.code = code
        self.message = message
        self.retry_after = retry_after

    def __str__(self) -> str:
        return f"klondike: {self.status} {self.code}: {self.message}"

    def __repr__(self) -> str:
        return (
            f"{type(self).__name__}(status={self.status!r}, code={self.code!r}, "
            f"message={self.message!r}, retry_after={self.retry_after!r})"
        )


class BadRequestError(APIError):
    """400: an invalid parameter."""


class UnauthorizedError(APIError):
    """401: missing, unknown or revoked API key."""


class NotFoundError(APIError):
    """404: no data for this symbol."""


class RateLimitError(APIError):
    """429: rate limit of the key exceeded (``retry_after`` says how long to
    wait), or too many WebSocket connections."""


class ServiceUnavailableError(APIError):
    """503: the data is not available yet, retry later."""


_BY_STATUS: Dict[int, Type[APIError]] = {
    400: BadRequestError,
    401: UnauthorizedError,
    404: NotFoundError,
    429: RateLimitError,
    503: ServiceUnavailableError,
}


def api_error(status: int, headers: Mapping[str, str], body: bytes) -> APIError:
    """Builds the error of a non-200 answer, as the Go client does: the code
    and message of the JSON body, else the status text and the raw body."""
    try:
        code = HTTPStatus(status).phrase
    except ValueError:
        code = str(status)
    message = body[: 64 << 10].decode("utf-8", "replace").strip()
    try:
        payload: Any = json.loads(body)
    except ValueError:
        payload = None
    if isinstance(payload, dict) and payload.get("error"):
        code = str(payload.get("code") or "")
        message = str(payload["error"])
    retry_after: Optional[float] = None
    value = headers.get("Retry-After") or headers.get("retry-after")
    if value is not None:
        try:
            retry_after = float(int(value.strip()))
        except ValueError:
            pass
    return _BY_STATUS.get(status, APIError)(status, code, message, retry_after)


@dataclass(eq=True)
class StreamError(KlondikeError):
    """An ``error`` message of the WebSocket server. It does not close the
    connection: it is delivered as ``Message.error`` and can be raised by the
    application if it wants to.

    ``code`` is ``bad_request``, ``unknown_channel``, ``too_many_subscriptions``
    or ``unavailable``; ``channel`` and ``symbol`` name the subscription the
    error is about (empty otherwise)."""

    code: str
    message: str
    channel: str = ""
    symbol: str = ""

    def __post_init__(self) -> None:
        super().__init__(self.code, self.message, self.channel, self.symbol)

    def __str__(self) -> str:
        about = " ".join(p for p in (self.code, self.channel, self.symbol) if p)
        return f"klondike: {self.message} ({about})"


class StreamClosedError(KlondikeError):
    """The stream ended: closed by the application, refused by the server, or
    lost with reconnection disabled. ``code`` and ``reason`` are those of the
    WebSocket close frame when there was one."""

    def __init__(self, message: str, code: Optional[int] = None, reason: str = "") -> None:
        super().__init__(message)
        self.code = code
        self.reason = reason


class DecodeError(KlondikeError):
    """A server message could not be decoded."""


class MissingDependencyError(KlondikeError, ImportError):
    """An optional dependency is not installed (pycapnp for ``format="capnp"``)."""
