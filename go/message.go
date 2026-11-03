package klondike

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"

	"capnproto.org/go/capnp/v3"

	"github.com/klondike-trading/klondike-api/go/klondikecapnp"
)

// Channel is a stream of the WebSocket API.
type Channel string

const (
	ChannelTrades    Channel = "trades"
	ChannelOrderbook Channel = "orderbook"
	ChannelOverview  Channel = "overview"
)

// MessageType is the type of a server message.
type MessageType string

const (
	TypeTrades       MessageType = "trades"
	TypeOrderbook    MessageType = "orderbook"
	TypeOverview     MessageType = "overview"
	TypeSubscribed   MessageType = "subscribed"
	TypeUnsubscribed MessageType = "unsubscribed"
	TypeError        MessageType = "error"
	TypePong         MessageType = "pong"
)

// Format is the encoding of the server messages of a WebSocket.
type Format string

const (
	FormatJSON  Format = "json"
	FormatCapnp Format = "capnp"
)

// Subscription names a stream: Symbol is empty for the overview.
type Subscription struct {
	Channel Channel `json:"channel"`
	Symbol  string  `json:"symbol,omitempty"`
}

// StreamError is an error message of the server. It does not close the
// connection.
type StreamError struct {
	Code    string  `json:"code"`
	Message string  `json:"message"`
	Channel Channel `json:"channel,omitempty"`
	Symbol  string  `json:"symbol,omitempty"`
}

func (e *StreamError) Error() string {
	if e.Channel != "" {
		return fmt.Sprintf("klondike: %s (%s %s %s)", e.Message, e.Code, e.Channel, e.Symbol)
	}
	return fmt.Sprintf("klondike: %s (%s)", e.Message, e.Code)
}

// Message is a server message: Type says which field is set (none for pong).
type Message struct {
	Type         MessageType
	Trades       *Trades
	Orderbook    *Orderbook
	Overview     *Overview
	Subscription *Subscription // subscribed and unsubscribed
	Error        *StreamError
}

// ClientMessage is a message of the client: subscribe, unsubscribe or ping.
type ClientMessage struct {
	Op      string   `json:"op"`
	Channel Channel  `json:"channel,omitempty"`
	Symbol  string   `json:"symbol,omitempty"`
	Venues  []string `json:"venues,omitempty"`
	Depth   int      `json:"depth,omitempty"`
}

type jsonEnvelope struct {
	Type     MessageType     `json:"type"`
	Overview json.RawMessage `json:"overview,omitempty"`
}

// ErrUnknownMessage is returned when decoding a message of a type this
// version does not know: a newer server may add types, the stream skips them.
var ErrUnknownMessage = errors.New("klondike: unknown message type")

// MarshalJSON encodes the message as the server sends it with format=json.
func (m Message) MarshalJSON() ([]byte, error) {
	switch m.Type {
	case TypeTrades:
		if m.Trades == nil {
			return nil, fmt.Errorf("klondike: trades message without trades")
		}
		return json.Marshal(struct {
			Type MessageType `json:"type"`
			*Trades
		}{m.Type, m.Trades})
	case TypeOrderbook:
		if m.Orderbook == nil {
			return nil, fmt.Errorf("klondike: orderbook message without book")
		}
		return json.Marshal(struct {
			Type MessageType `json:"type"`
			*Orderbook
		}{m.Type, m.Orderbook})
	case TypeOverview:
		return json.Marshal(struct {
			Type     MessageType `json:"type"`
			Overview *Overview   `json:"overview"`
		}{m.Type, m.Overview})
	case TypeSubscribed, TypeUnsubscribed:
		if m.Subscription == nil {
			return nil, fmt.Errorf("klondike: %s message without subscription", m.Type)
		}
		return json.Marshal(struct {
			Type MessageType `json:"type"`
			*Subscription
		}{m.Type, m.Subscription})
	case TypeError:
		if m.Error == nil {
			return nil, fmt.Errorf("klondike: error message without error")
		}
		return json.Marshal(struct {
			Type MessageType `json:"type"`
			*StreamError
		}{m.Type, m.Error})
	case TypePong:
		return []byte(`{"type":"pong"}`), nil
	}
	return nil, fmt.Errorf("klondike: unknown message type %q", m.Type)
}

// UnmarshalJSON decodes a server message of format=json.
func (m *Message) UnmarshalJSON(data []byte) error {
	var env jsonEnvelope
	if err := json.Unmarshal(data, &env); err != nil {
		return err
	}
	*m = Message{Type: env.Type}
	switch env.Type {
	case TypeTrades:
		m.Trades = &Trades{}
		return json.Unmarshal(data, m.Trades)
	case TypeOrderbook:
		m.Orderbook = &Orderbook{}
		return json.Unmarshal(data, m.Orderbook)
	case TypeOverview:
		m.Overview = &Overview{}
		return json.Unmarshal(env.Overview, m.Overview)
	case TypeSubscribed, TypeUnsubscribed:
		m.Subscription = &Subscription{}
		return json.Unmarshal(data, m.Subscription)
	case TypeError:
		m.Error = &StreamError{}
		return json.Unmarshal(data, m.Error)
	case TypePong:
		return nil
	}
	return fmt.Errorf("%w %q", ErrUnknownMessage, env.Type)
}

// MarshalCapnp encodes the message as the server sends it with
// format=capnp: a packed ServerMessage.
func (m Message) MarshalCapnp() ([]byte, error) {
	msg, seg, err := capnp.NewMessage(capnp.SingleSegment(nil))
	if err != nil {
		return nil, err
	}
	root, err := klondikecapnp.NewRootServerMessage(seg)
	if err != nil {
		return nil, err
	}
	switch m.Type {
	case TypeTrades:
		if m.Trades == nil {
			return nil, fmt.Errorf("klondike: trades message without trades")
		}
		err = encodeTrades(root, m.Trades)
	case TypeOrderbook:
		if m.Orderbook == nil {
			return nil, fmt.Errorf("klondike: orderbook message without book")
		}
		err = encodeOrderbook(root, m.Orderbook)
	case TypeOverview:
		var b []byte
		if b, err = json.Marshal(m.Overview); err == nil {
			err = root.SetOverviewJson(string(b))
		}
	case TypeSubscribed, TypeUnsubscribed:
		if m.Subscription == nil {
			return nil, fmt.Errorf("klondike: %s message without subscription", m.Type)
		}
		var s klondikecapnp.Subscription
		if m.Type == TypeSubscribed {
			s, err = root.NewSubscribed()
		} else {
			s, err = root.NewUnsubscribed()
		}
		if err == nil {
			err = firstErr(s.SetChannel(string(m.Subscription.Channel)), s.SetSymbol(m.Subscription.Symbol))
		}
	case TypeError:
		if m.Error == nil {
			return nil, fmt.Errorf("klondike: error message without error")
		}
		var e klondikecapnp.Error
		if e, err = root.NewError(); err == nil {
			err = firstErr(e.SetCode(m.Error.Code), e.SetMessage_(m.Error.Message),
				e.SetChannel(string(m.Error.Channel)), e.SetSymbol(m.Error.Symbol))
		}
	case TypePong:
		root.SetPong()
	default:
		return nil, fmt.Errorf("klondike: unknown message type %q", m.Type)
	}
	if err != nil {
		return nil, err
	}
	return msg.MarshalPacked()
}

func encodeTrades(root klondikecapnp.ServerMessage, t *Trades) error {
	ct, err := root.NewTrades()
	if err != nil {
		return err
	}
	if err := ct.SetSymbol(t.Symbol); err != nil {
		return err
	}
	list, err := ct.NewTrades(int32(len(t.Trades)))
	if err != nil {
		return err
	}
	for i, tr := range t.Trades {
		c := list.At(i)
		c.SetTs(tr.Ts)
		c.SetPrice(tr.Price)
		c.SetQty(tr.Qty)
		switch tr.Side {
		case Buy:
			c.SetSide(klondikecapnp.Side_buy)
		case Sell:
			c.SetSide(klondikecapnp.Side_sell)
		default:
			c.SetSide(klondikecapnp.Side_unknown)
		}
		if err := c.SetVenue(tr.Venue); err != nil {
			return err
		}
	}
	return nil
}

func encodeOrderbook(root klondikecapnp.ServerMessage, ob *Orderbook) error {
	cb, err := root.NewOrderbook()
	if err != nil {
		return err
	}
	if err := cb.SetSymbol(ob.Symbol); err != nil {
		return err
	}
	cb.SetTs(ob.Ts)
	venues, err := cb.NewVenues(int32(len(ob.Venues)))
	if err != nil {
		return err
	}
	index := make(map[string]uint16, len(ob.Venues))
	for i, v := range ob.Venues {
		c := venues.At(i)
		if err := c.SetVenue(v.Venue); err != nil {
			return err
		}
		index[v.Venue] = uint16(i)
		if v.Ts != nil {
			c.SetTs(*v.Ts)
		}
		c.SetBid(orNaN(v.Bid))
		c.SetBidQty(orNaN(v.BidQty))
		c.SetAsk(orNaN(v.Ask))
		c.SetAskQty(orNaN(v.AskQty))
	}
	side := func(levels []Level, alloc func(int32) (klondikecapnp.Level_List, error)) error {
		list, err := alloc(int32(len(levels)))
		if err != nil {
			return err
		}
		for i, l := range levels {
			vi, ok := index[l.Venue]
			if !ok {
				return fmt.Errorf("klondike: level of venue %q not in the venues of the book", l.Venue)
			}
			c := list.At(i)
			c.SetPrice(l.Price)
			c.SetQty(l.Qty)
			c.SetVenue(vi)
		}
		return nil
	}
	if err := side(ob.Bids, cb.NewBids); err != nil {
		return err
	}
	return side(ob.Asks, cb.NewAsks)
}

// UnmarshalCapnp decodes a server message of format=capnp.
func (m *Message) UnmarshalCapnp(data []byte) error {
	msg, err := capnp.UnmarshalPacked(data)
	if err != nil {
		return err
	}
	root, err := klondikecapnp.ReadRootServerMessage(msg)
	if err != nil {
		return err
	}
	*m = Message{}
	switch root.Which() {
	case klondikecapnp.ServerMessage_Which_trades:
		m.Type = TypeTrades
		m.Trades, err = decodeTrades(root)
	case klondikecapnp.ServerMessage_Which_orderbook:
		m.Type = TypeOrderbook
		m.Orderbook, err = decodeOrderbook(root)
	case klondikecapnp.ServerMessage_Which_overviewJson:
		m.Type = TypeOverview
		var b []byte
		if b, err = root.OverviewJsonBytes(); err == nil {
			m.Overview = &Overview{}
			err = json.Unmarshal(b, m.Overview)
		}
	case klondikecapnp.ServerMessage_Which_subscribed, klondikecapnp.ServerMessage_Which_unsubscribed:
		var s klondikecapnp.Subscription
		if root.Which() == klondikecapnp.ServerMessage_Which_subscribed {
			m.Type = TypeSubscribed
			s, err = root.Subscribed()
		} else {
			m.Type = TypeUnsubscribed
			s, err = root.Unsubscribed()
		}
		if err == nil {
			channel, _ := s.Channel()
			symbol, _ := s.Symbol()
			m.Subscription = &Subscription{Channel: Channel(channel), Symbol: symbol}
		}
	case klondikecapnp.ServerMessage_Which_error:
		m.Type = TypeError
		var e klondikecapnp.Error
		if e, err = root.Error(); err == nil {
			code, _ := e.Code()
			text, _ := e.Message_()
			channel, _ := e.Channel()
			symbol, _ := e.Symbol()
			m.Error = &StreamError{Code: code, Message: text, Channel: Channel(channel), Symbol: symbol}
		}
	case klondikecapnp.ServerMessage_Which_pong:
		m.Type = TypePong
	default:
		return fmt.Errorf("%w %d", ErrUnknownMessage, root.Which())
	}
	return err
}

func decodeTrades(root klondikecapnp.ServerMessage) (*Trades, error) {
	ct, err := root.Trades()
	if err != nil {
		return nil, err
	}
	symbol, err := ct.Symbol()
	if err != nil {
		return nil, err
	}
	list, err := ct.Trades()
	if err != nil {
		return nil, err
	}
	out := &Trades{Symbol: symbol, Trades: make([]Trade, list.Len())}
	for i := range out.Trades {
		c := list.At(i)
		venue, err := c.Venue()
		if err != nil {
			return nil, err
		}
		side := UnknownSide
		switch c.Side() {
		case klondikecapnp.Side_buy:
			side = Buy
		case klondikecapnp.Side_sell:
			side = Sell
		}
		out.Trades[i] = Trade{Ts: c.Ts(), Venue: venue, Price: c.Price(), Qty: c.Qty(), Side: side}
	}
	return out, nil
}

func decodeOrderbook(root klondikecapnp.ServerMessage) (*Orderbook, error) {
	cb, err := root.Orderbook()
	if err != nil {
		return nil, err
	}
	symbol, err := cb.Symbol()
	if err != nil {
		return nil, err
	}
	venues, err := cb.Venues()
	if err != nil {
		return nil, err
	}
	out := &Orderbook{Symbol: symbol, Ts: cb.Ts(), Venues: make([]VenueQuote, venues.Len())}
	for i := range out.Venues {
		c := venues.At(i)
		name, err := c.Venue()
		if err != nil {
			return nil, err
		}
		q := VenueQuote{Venue: name, Bid: ptrUnlessNaN(c.Bid()), BidQty: ptrUnlessNaN(c.BidQty()),
			Ask: ptrUnlessNaN(c.Ask()), AskQty: ptrUnlessNaN(c.AskQty())}
		if ts := c.Ts(); ts != 0 {
			q.Ts = &ts
		}
		out.Venues[i] = q
	}
	side := func(get func() (klondikecapnp.Level_List, error)) ([]Level, error) {
		list, err := get()
		if err != nil {
			return nil, err
		}
		levels := make([]Level, list.Len())
		for i := range levels {
			c := list.At(i)
			vi := int(c.Venue())
			if vi >= len(out.Venues) {
				return nil, fmt.Errorf("klondike: level venue index %d out of %d venues", vi, len(out.Venues))
			}
			levels[i] = Level{Price: c.Price(), Qty: c.Qty(), Venue: out.Venues[vi].Venue}
		}
		return levels, nil
	}
	if out.Bids, err = side(cb.Bids); err != nil {
		return nil, err
	}
	if out.Asks, err = side(cb.Asks); err != nil {
		return nil, err
	}
	return out, nil
}

func orNaN(v *float64) float64 {
	if v == nil {
		return math.NaN()
	}
	return *v
}

func ptrUnlessNaN(v float64) *float64 {
	if math.IsNaN(v) {
		return nil
	}
	return &v
}

func firstErr(errs ...error) error {
	for _, err := range errs {
		if err != nil {
			return err
		}
	}
	return nil
}
