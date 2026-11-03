# Klondike API — Go client

```sh
go get github.com/klondike-trading/klondike-api/go
```

```go
import klondike "github.com/klondike-trading/klondike-api/go"
```

Go 1.23 or later. Create an API key on the [portal](https://api.klondike-crypto.com/portal/).

## REST

```go
ctx := context.Background()
c := klondike.New(os.Getenv("KLONDIKE_API_KEY"))

// Merged order book: best bid/ask of every venue, 10 levels per side.
book, err := c.Orderbook(ctx, "BTC/USDT", &klondike.OrderbookOptions{Depth: 10})
if err != nil {
	log.Fatal(err)
}
for _, l := range book.Bids {
	fmt.Println(l.Venue, l.Price, l.Qty)
}

// The last 100 trades of every venue, or of some of them.
trades, err := c.Trades(ctx, "ETH/USDT:USDT", &klondike.TradesOptions{Limit: 100, Venues: []string{"binanceusdm", "bybit"}})

// Market overview: cost of 100 k$ / 1 M$ / 10 M$ orders, depth, crosses, implied prices.
ov, err := c.Overview(ctx)
for _, p := range ov.Pairs {
	fmt.Println(p.Symbol, p.Mid, p.Depth10Usd)
}

// Symbols with recent trades.
symbols, err := c.Symbols(ctx, "usdc")
```

Errors of the API are `*klondike.APIError` (`Status`, `Code`, `Message`, `RetryAfter` on a 429);
`klondike.IsNotFound(err)` tells a symbol nobody streams.

## WebSocket

```go
s, err := c.Stream(ctx, &klondike.StreamOptions{Format: klondike.FormatCapnp}) // or FormatJSON
if err != nil {
	log.Fatal(err) // klondike.ErrUnauthorized for a refused key
}
defer s.Close()

s.SubscribeTrades(ctx, "BTC/USDT")
s.SubscribeOrderbook(ctx, "BTC/USDT", 20, "binance", "coinbase", "kraken")
s.SubscribeOverview(ctx)

for m := range s.Messages() {
	switch m.Type {
	case klondike.TypeTrades:
		fmt.Println(len(m.Trades.Trades), "trades")
	case klondike.TypeOrderbook:
		fmt.Println(len(m.Orderbook.Venues), "venues,", len(m.Orderbook.Bids), "bids")
	case klondike.TypeOverview:
		fmt.Println(len(m.Overview.Pairs), "pairs")
	case klondike.TypeError:
		log.Print(m.Error)
	}
}
log.Print(s.Err())
```

The messages are the same whatever the format. A lost connection is reopened with a backoff (0.5 s
to 30 s) and the subscriptions are sent again; `Messages()` is closed only by `Close` or a refused
key. See [`examples/stream`](https://github.com/klondike-trading/klondike-api/blob/main/go/examples/stream/main.go).

## Development

```sh
go test ./...
KLONDIKE_UPDATE_FIXTURES=1 go test -run TestFixtures .   # rewrite ../testdata/messages
./klondikecapnp/gen.sh                                     # after a change of ../schema/klondike_api.capnp
```

`message.go` is also the encoder of the server: the Go types are the reference of the wire format.
