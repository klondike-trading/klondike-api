package klondike

import (
	"encoding/json"
	"reflect"
	"testing"
)

func f(v float64) *float64 { return &v }
func i(v int64) *int64     { return &v }

func sampleMessages() []Message {
	return []Message{
		{Type: TypeTrades, Trades: &Trades{Symbol: "BTC/USDT", Trades: []Trade{
			{Ts: 1793706479654, Venue: "binance", Price: 104250.1, Qty: 0.0125, Side: Buy},
			{Ts: 1793706479702, Venue: "okx", Price: 104249.9, Qty: 0.4, Side: Sell},
			{Ts: 1793706479800, Venue: "kraken", Price: 104250, Qty: 1, Side: UnknownSide},
		}}},
		{Type: TypeOrderbook, Orderbook: &Orderbook{Symbol: "BTC/USDT", Ts: 1793706479000,
			Venues: []VenueQuote{
				{Venue: "binance", Ts: i(1793706478950), Bid: f(104250), BidQty: f(1.2), Ask: f(104250.1), AskQty: f(0.8)},
				{Venue: "kraken", Ts: i(1793706478890), Bid: f(104249), BidQty: f(0.5)},
				{Venue: "gemini"},
			},
			Bids: []Level{{104250, 1.2, "binance"}, {104249, 0.5, "kraken"}},
			Asks: []Level{{104250.1, 0.8, "binance"}},
		}},
		{Type: TypeOverview, Overview: &Overview{Ts: 1, Pairs: []Pair{{Symbol: "BTC/USDT", Base: "BTC", Quote: "USDT", Market: "spot", Status: "ok", Exchanges: 3, Live: 3, Mid: 104250, BasisBps: f(1.5), Impact: []Impact{{NotionalUsd: 1e5, BuyAllInBps: f(4.2), BuyFilled: 1, SellFilled: 0.62, BestSingleBuyExchange: "binance", BestSingleSellExchange: "okx"}}}},
			Leaders: []Leader{{"binance", 1}}, Implied: Implied{Pegs: []Peg{}, FX: []ImpliedFX{}, Triangles: []Triangle{}}, Paths: []Path{}}},
		{Type: TypeSubscribed, Subscription: &Subscription{Channel: ChannelTrades, Symbol: "BTC/USDT"}},
		{Type: TypeUnsubscribed, Subscription: &Subscription{Channel: ChannelOverview}},
		{Type: TypeError, Error: &StreamError{Code: "bad_request", Message: "symbol is required", Channel: ChannelTrades}},
		{Type: TypePong},
	}
}

func TestJSONRoundTrip(t *testing.T) {
	for _, m := range sampleMessages() {
		b, err := json.Marshal(m)
		if err != nil {
			t.Fatalf("%s: %v", m.Type, err)
		}
		var back Message
		if err := json.Unmarshal(b, &back); err != nil {
			t.Fatalf("%s: %v (%s)", m.Type, err, b)
		}
		if !reflect.DeepEqual(m, back) {
			t.Errorf("%s: json round trip\n got %+v\nwant %+v\n%s", m.Type, back, m, b)
		}
	}
}

func TestCapnpRoundTrip(t *testing.T) {
	for _, m := range sampleMessages() {
		b, err := m.MarshalCapnp()
		if err != nil {
			t.Fatalf("%s: %v", m.Type, err)
		}
		var back Message
		if err := back.UnmarshalCapnp(b); err != nil {
			t.Fatalf("%s: %v", m.Type, err)
		}
		if !reflect.DeepEqual(m, back) {
			t.Errorf("%s: capnp round trip\n got %+v\nwant %+v", m.Type, back, m)
		}
	}
}

func TestJSONWireFormat(t *testing.T) {
	b, _ := json.Marshal(sampleMessages()[0])
	want := `{"type":"trades","symbol":"BTC/USDT","trades":[{"ts":1793706479654,"venue":"binance","price":104250.1,"qty":0.0125,"side":"buy"},{"ts":1793706479702,"venue":"okx","price":104249.9,"qty":0.4,"side":"sell"},{"ts":1793706479800,"venue":"kraken","price":104250,"qty":1,"side":"unknown"}]}`
	if string(b) != want {
		t.Errorf("got %s", b)
	}
}
