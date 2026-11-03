"""Python client of the Klondike market data API.

Trades and merged order books of the crypto pairs Klondike streams from about
forty venues, and the market overview, over REST and WebSocket::

    import klondike

    client = klondike.Client("kl_...")
    book = client.orderbook("BTC/USDT", depth=10)

See https://klondike-trading.github.io/klondike-api/ for the documentation.
"""

from ._capnp import capnp_available
from ._version import __version__
from .client import DEFAULT_BASE_URL, AsyncClient, Client
from .errors import (
    APIError,
    BadRequestError,
    DecodeError,
    KlondikeError,
    MissingDependencyError,
    NotFoundError,
    RateLimitError,
    ServiceUnavailableError,
    StreamClosedError,
    StreamError,
    UnauthorizedError,
)
from .messages import (
    Channel,
    Message,
    MessageType,
    Subscription,
    UnknownMessageError,
    decode,
    decode_capnp,
    decode_json,
)
from .models import (
    Feeds,
    Impact,
    Implied,
    ImpliedFX,
    Leader,
    Level,
    Orderbook,
    Overview,
    Pair,
    Path,
    Peg,
    Side,
    SymbolInfo,
    Trade,
    Trades,
    Triangle,
    VenueQuote,
)
from .stream import Format, Stream

__all__ = [
    "__version__",
    "DEFAULT_BASE_URL",
    "Client",
    "AsyncClient",
    "Stream",
    "Format",
    "capnp_available",
    # Messages
    "Channel",
    "Message",
    "MessageType",
    "Subscription",
    "decode",
    "decode_json",
    "decode_capnp",
    # Models
    "Side",
    "Trade",
    "Trades",
    "SymbolInfo",
    "VenueQuote",
    "Level",
    "Orderbook",
    "Overview",
    "Feeds",
    "Pair",
    "Impact",
    "Leader",
    "Implied",
    "Peg",
    "ImpliedFX",
    "Triangle",
    "Path",
    # Errors
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
    "UnknownMessageError",
    "MissingDependencyError",
]
