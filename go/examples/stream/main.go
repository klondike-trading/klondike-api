// Streams the trades and the merged book of BTC/USDT, as Cap'n Proto frames.
//
//	KLONDIKE_API_KEY=kl_... go run ./examples/stream
package main

import (
	"context"
	"fmt"
	"log"
	"os"
	"os/signal"

	klondike "github.com/klondike-trading/klondike-api/go"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	opts := []klondike.Option{}
	if u := os.Getenv("KLONDIKE_API_URL"); u != "" {
		opts = append(opts, klondike.WithBaseURL(u))
	}
	c := klondike.New(os.Getenv("KLONDIKE_API_KEY"), opts...)
	s, err := c.Stream(ctx, &klondike.StreamOptions{Format: klondike.FormatCapnp})
	if err != nil {
		log.Fatal(err)
	}
	defer s.Close()
	s.SubscribeTrades(ctx, "BTC/USDT")
	s.SubscribeOrderbook(ctx, "BTC/USDT", 5)

	for {
		select {
		case <-ctx.Done():
			return
		case m, ok := <-s.Messages():
			if !ok {
				log.Fatal(s.Err())
			}
			switch m.Type {
			case klondike.TypeTrades:
				for _, t := range m.Trades.Trades {
					fmt.Printf("%-12s %-4s %12.2f %10.5f\n", t.Venue, t.Side, t.Price, t.Qty)
				}
			case klondike.TypeOrderbook:
				b := m.Orderbook
				if len(b.Bids) > 0 && len(b.Asks) > 0 {
					fmt.Printf("book  %.2f (%s) / %.2f (%s), %d venues\n",
						b.Bids[0].Price, b.Bids[0].Venue, b.Asks[0].Price, b.Asks[0].Venue, len(b.Venues))
				}
			case klondike.TypeError:
				log.Print(m.Error)
			}
		}
	}
}
