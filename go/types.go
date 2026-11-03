package klondike

// Side is the aggressor (taker) side of a trade.
type Side string

const (
	Buy         Side = "buy"
	Sell        Side = "sell"
	UnknownSide Side = "unknown"
)

// Trade is one trade of a venue. Ts is the exchange time in milliseconds
// (receive time when the venue gives none); Qty is in the base currency, in
// USD for inverse perpetuals.
type Trade struct {
	Ts    int64   `json:"ts"`
	Venue string  `json:"venue"`
	Price float64 `json:"price"`
	Qty   float64 `json:"qty"`
	Side  Side    `json:"side"`
}

// Trades are trades of one symbol, oldest first.
type Trades struct {
	Symbol string  `json:"symbol"`
	Trades []Trade `json:"trades"`
}

// SymbolInfo is a symbol the API received trades for recently.
type SymbolInfo struct {
	Symbol      string   `json:"symbol"`
	Market      string   `json:"market"` // spot or perp
	Venues      []string `json:"venues"`
	LastTradeTs int64    `json:"lastTradeTs"`
}

// VenueQuote is the best bid and ask of one venue, nil when the venue has none.
type VenueQuote struct {
	Venue  string   `json:"venue"`
	Ts     *int64   `json:"ts"`
	Bid    *float64 `json:"bid"`
	BidQty *float64 `json:"bidQty"`
	Ask    *float64 `json:"ask"`
	AskQty *float64 `json:"askQty"`
}

// Level is a level of the merged book with the venue it comes from.
type Level struct {
	Price float64 `json:"price"`
	Qty   float64 `json:"qty"`
	Venue string  `json:"venue"`
}

// Orderbook is the merged order book of a symbol: the best bid and ask of
// every venue and the first levels of each side, best first.
type Orderbook struct {
	Symbol string       `json:"symbol"`
	Ts     int64        `json:"ts"`
	Venues []VenueQuote `json:"venues"`
	Bids   []Level      `json:"bids"`
	Asks   []Level      `json:"asks"`
}

// Overview is the market overview: the pairs followed permanently, what an
// order costs on the merged book and on the best single venue, crosses
// between venues, venues setting the price, implied prices.
type Overview struct {
	Ts            int64    `json:"ts"`
	WindowsFromTs int64    `json:"windowsFromTs"` // oldest minute of the 1 h counters
	Feeds         Feeds    `json:"feeds"`
	Pairs         []Pair   `json:"pairs"`
	Leaders       []Leader `json:"leaders"`
	Implied       Implied  `json:"implied"`
	Paths         []Path   `json:"paths"`
}

// Feeds is the health of the market data.
type Feeds struct {
	Venues      int     `json:"venues"` // with at least one live book
	VenuesKnown int     `json:"venuesKnown"`
	BooksLive   int     `json:"booksLive"`
	LagMsP50    float64 `json:"lagMsP50"`
}

// Pair is a pair of the overview. Amounts are in USD, costs in bps of the
// consolidated mid.
type Pair struct {
	Symbol              string   `json:"symbol"`
	Base                string   `json:"base"`
	Quote               string   `json:"quote"`  // USDT, USDT:USDT...
	Market              string   `json:"market"` // spot or perp
	Status              string   `json:"status"` // ok, warn, alert or down: data quality only
	Reasons             []string `json:"reasons,omitempty"`
	Exchanges           int      `json:"exchanges"`
	Live                int      `json:"live"`
	Mid                 float64  `json:"mid"`
	Volume1hUsd         float64  `json:"volume1hUsd"`
	VolumeTopExchange   string   `json:"volumeTopExchange"`
	VolumeTopShare      float64  `json:"volumeTopShare"`
	Depth10Usd          float64  `json:"depth10Usd"`          // bid + ask within ±10 bps of the mid
	Depth10PartialShare *float64 `json:"depth10PartialShare"` // share from books stopping before ±10 bps: a lower bound
	Imbalance           float64  `json:"imbalance"`           // (bid − ask) / (bid + ask) within ±50 bps
	Impact              []Impact `json:"impact"`
	Crosses1h           int      `json:"crosses1h"`         // crosses between venues, transient ones included
	CrossesPositive1h   int      `json:"crossesPositive1h"` // episodes net positive after taker fees
	MaxNetBps1h         *float64 `json:"maxNetBps1h"`
	BasisBps            *float64 `json:"basisBps"` // perps: (mid perp − mid spot) / mid spot
}

// Impact is the cost of a market order of NotionalUsd, taker fees included,
// on the merged book and on the best single venue. A cost is nil when the
// book cannot fill the order; the filled fraction then says how much it can.
type Impact struct {
	NotionalUsd            float64  `json:"notionalUsd"`
	BuyAllInBps            *float64 `json:"buyAllInBps"`
	SellAllInBps           *float64 `json:"sellAllInBps"`
	BuyFilled              float64  `json:"buyFilled"`
	SellFilled             float64  `json:"sellFilled"`
	BuyVenues              int      `json:"buyVenues"`
	SellVenues             int      `json:"sellVenues"`
	BestSingleBuyAllInBps  *float64 `json:"bestSingleBuyAllInBps"`
	BestSingleSellAllInBps *float64 `json:"bestSingleSellAllInBps"`
	BestSingleBuyExchange  string   `json:"bestSingleBuyExchange"`
	BestSingleSellExchange string   `json:"bestSingleSellExchange"`
}

// Leader is the share of the time a venue holds the best consolidated bid or
// ask; ties split the time, so that the shares sum to 1.
type Leader struct {
	Exchange string  `json:"exchange"`
	Share    float64 `json:"share"`
}

type Implied struct {
	Pegs      []Peg       `json:"pegs"`
	FX        []ImpliedFX `json:"fx"`
	Triangles []Triangle  `json:"triangles"`
}

// Peg is a stablecoin pair: its consolidated mid in bps from 1.
type Peg struct {
	Symbol         string  `json:"symbol"`
	Bps            float64 `json:"bps"`
	VenueSpreadBps float64 `json:"venueSpreadBps"` // highest minus lowest venue mid
	Venues         int     `json:"venues"`
}

// ImpliedFX is the rate of a fiat currency in USD implied by crypto pairs,
// against the direct pair when there is one.
type ImpliedFX struct {
	Ccy     string   `json:"ccy"`
	Implied float64  `json:"implied"`
	Direct  *float64 `json:"direct"`
	DevBps  *float64 `json:"devBps"`
}

// Triangle is the best of the two cycles through a cross pair and USDT, net
// of the taker fees of its three legs.
type Triangle struct {
	Cross      string  `json:"cross"`
	Cycle      string  `json:"cycle"`
	BestNetBps float64 `json:"bestNetBps"`
}

// Path is the best conversion path, fees included; costs are against the
// consolidated mid of the direct pair.
type Path struct {
	Sell          string   `json:"sell"`
	Buy           string   `json:"buy"`
	Quantity      float64  `json:"quantity"`
	Tokens        []string `json:"tokens"`
	CostBps       float64  `json:"costBps"`
	DirectCostBps *float64 `json:"directCostBps"`
	Ts            int64    `json:"ts"`
}
