# Cap'n Proto schema

With `GET /v1/ws?format=capnp`, every binary frame of the server is one
`ServerMessage`, serialized with the Cap'n Proto **packed** encoding. The
libraries decode it for you; this page is for writing a client of your own.

```capnp
--8<-- "schema/klondike_api.capnp"
```

Download: [`klondike_api.capnp`](https://github.com/klondike-trading/klondike-api/blob/main/schema/klondike_api.capnp).

Decoding a frame, in pseudo code:

```text
msg  = capnp.unpack(frame)            # packed encoding
root = msg.root(ServerMessage)
switch root.which():
  trades       -> Trades: symbol, trades[ts, venue, price, qty, side]
  orderbook    -> Orderbook: venues[i] = {venue, ts (0: none), bid/ask (NaN: none)}
                  bids/asks[j] = {price, qty, venue = venues[venue].venue}
  overviewJson -> JSON.parse(text), the document of GET /v1/overview
  subscribed / unsubscribed -> {channel, symbol}
  error        -> {code, message, channel, symbol}
  pong
```

The repository has the same messages encoded in both formats in
[`testdata/messages`](https://github.com/klondike-trading/klondike-api/tree/main/testdata/messages):
`<n>-<type>.json` and `<n>-<type>.capnp` decode to the same message, which
makes a good test for a decoder of your own.
