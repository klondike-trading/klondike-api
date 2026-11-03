"""Prints the trades of a symbol, all venues, as they happen.

    export KLONDIKE_API_KEY=kl_...
    python examples/stream_trades.py BTC/USDT
    python examples/stream_trades.py ETH/USDT:USDT --venues binanceusdm,bybit --format capnp

The stream reconnects on its own; Ctrl-C stops it.
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import logging
from datetime import datetime, timezone

import klondike


async def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("symbol", nargs="?", default="BTC/USDT", help="unified CCXT symbol (default BTC/USDT)")
    parser.add_argument("--venues", help="comma separated CCXT exchange ids (default all)")
    parser.add_argument("--format", choices=["json", "capnp"], default="json", help="encoding of the server messages")
    parser.add_argument("--url", default=klondike.DEFAULT_BASE_URL, help="API base URL")
    args = parser.parse_args()

    # The API key comes from KLONDIKE_API_KEY.
    async with klondike.AsyncClient(base_url=args.url) as client:
        async with client.stream(format=args.format) as stream:
            await stream.subscribe_trades(args.symbol, venues=args.venues)
            async for msg in stream:
                if msg.type == klondike.MessageType.TRADES and msg.trades:
                    for t in msg.trades.trades:
                        when = datetime.fromtimestamp(t.ts / 1000, tz=timezone.utc).strftime("%H:%M:%S.%f")[:-3]
                        print(f"{when}  {msg.trades.symbol:<16} {t.venue:<14} {t.side.value:<7} {t.qty:>14.6f} @ {t.price:,.2f}")
                elif msg.type == klondike.MessageType.SUBSCRIBED and msg.subscription:
                    print(f"subscribed to {msg.subscription.channel} {msg.subscription.symbol}")
                elif msg.type == klondike.MessageType.ERROR and msg.error:
                    print(f"error: {msg.error}")


if __name__ == "__main__":
    logging.basicConfig(level=logging.WARNING, format="%(levelname)s %(message)s")  # shows the reconnections
    with contextlib.suppress(KeyboardInterrupt):
        asyncio.run(main())
