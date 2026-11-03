package klondike

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
)

// StreamOptions configure a WebSocket stream.
type StreamOptions struct {
	Format           Format        // json (default) or capnp: the messages are the same
	DisableReconnect bool          // by default a lost connection is reopened and the subscriptions sent again
	MaxBackoff       time.Duration // longest wait between two reconnections (default 30 s)
	Buffer           int           // messages buffered for the reader (default 1024)
}

// Stream is a WebSocket connection to the API that reconnects on its own and
// subscribes again after a reconnection. Read the messages from Messages().
type Stream struct {
	client *Client
	opts   StreamOptions
	url    string

	ctx    context.Context
	cancel context.CancelFunc
	out    chan Message
	done   chan struct{}

	mu   sync.Mutex
	conn *websocket.Conn
	subs map[Subscription]ClientMessage
	err  error
}

// ErrUnauthorized is returned when the API key is refused.
var ErrUnauthorized = errors.New("klondike: API key refused")

// Stream opens a WebSocket stream. The first connection is opened before it
// returns: a refused key or an unreachable server is an error here.
func (c *Client) Stream(ctx context.Context, opts *StreamOptions) (*Stream, error) {
	o := StreamOptions{}
	if opts != nil {
		o = *opts
	}
	if o.Format == "" {
		o.Format = FormatJSON
	}
	if o.MaxBackoff <= 0 {
		o.MaxBackoff = 30 * time.Second
	}
	if o.Buffer <= 0 {
		o.Buffer = 1024
	}
	u, err := url.Parse(c.baseURL + "/v1/ws")
	if err != nil {
		return nil, err
	}
	switch u.Scheme {
	case "https":
		u.Scheme = "wss"
	case "http":
		u.Scheme = "ws"
	}
	u.RawQuery = url.Values{"format": {string(o.Format)}}.Encode()

	sctx, cancel := context.WithCancel(context.Background())
	s := &Stream{
		client: c, opts: o, url: u.String(),
		ctx: sctx, cancel: cancel,
		out: make(chan Message, o.Buffer), done: make(chan struct{}),
		subs: map[Subscription]ClientMessage{},
	}
	conn, err := s.dial(ctx)
	if err != nil {
		cancel()
		return nil, err
	}
	s.conn = conn
	go s.run(conn)
	return s, nil
}

// Messages returns the server messages: data, acknowledgements and errors. It
// is closed when the stream ends; Err then says why.
func (s *Stream) Messages() <-chan Message { return s.out }

// Err is the reason the stream ended, nil after Close.
func (s *Stream) Err() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.err
}

// Close closes the connection and ends the stream.
func (s *Stream) Close() error {
	s.cancel()
	s.mu.Lock()
	conn := s.conn
	s.mu.Unlock()
	if conn != nil {
		conn.Close(websocket.StatusNormalClosure, "")
	}
	<-s.done
	return nil
}

// SubscribeTrades streams the trades of the symbol, of the venues given (all
// when none).
func (s *Stream) SubscribeTrades(ctx context.Context, symbol string, venues ...string) error {
	return s.subscribe(ctx, ClientMessage{Op: "subscribe", Channel: ChannelTrades, Symbol: symbol, Venues: venues})
}

// SubscribeOrderbook streams the merged book of the symbol, depth levels per
// side (20 when 0), of the venues given (all when none).
func (s *Stream) SubscribeOrderbook(ctx context.Context, symbol string, depth int, venues ...string) error {
	return s.subscribe(ctx, ClientMessage{Op: "subscribe", Channel: ChannelOrderbook, Symbol: symbol, Depth: depth, Venues: venues})
}

// SubscribeOverview streams the market overview.
func (s *Stream) SubscribeOverview(ctx context.Context) error {
	return s.subscribe(ctx, ClientMessage{Op: "subscribe", Channel: ChannelOverview})
}

// Unsubscribe stops a stream (symbol is empty for the overview).
func (s *Stream) Unsubscribe(ctx context.Context, channel Channel, symbol string) error {
	s.mu.Lock()
	delete(s.subs, Subscription{Channel: channel, Symbol: symbol})
	s.mu.Unlock()
	return s.send(ctx, ClientMessage{Op: "unsubscribe", Channel: channel, Symbol: symbol})
}

// Ping asks the server for a pong message.
func (s *Stream) Ping(ctx context.Context) error {
	return s.send(ctx, ClientMessage{Op: "ping"})
}

func (s *Stream) subscribe(ctx context.Context, m ClientMessage) error {
	s.mu.Lock()
	s.subs[Subscription{Channel: m.Channel, Symbol: m.Symbol}] = m
	s.mu.Unlock()
	return s.send(ctx, m)
}

// send writes on the current connection. While reconnecting it is a no-op:
// the subscriptions are sent again once connected.
func (s *Stream) send(ctx context.Context, m ClientMessage) error {
	b, err := json.Marshal(m)
	if err != nil {
		return err
	}
	s.mu.Lock()
	conn, end := s.conn, s.err
	s.mu.Unlock()
	if s.ctx.Err() != nil {
		if end != nil {
			return end
		}
		return errors.New("klondike: stream closed")
	}
	if conn == nil {
		return nil
	}
	if err := conn.Write(ctx, websocket.MessageText, b); err != nil && s.opts.DisableReconnect {
		return err
	}
	return nil
}

func (s *Stream) dial(ctx context.Context) (*websocket.Conn, error) {
	h := http.Header{}
	h.Set("X-API-Key", s.client.apiKey)
	h.Set("User-Agent", s.client.userAgent)
	conn, resp, err := websocket.Dial(ctx, s.url, &websocket.DialOptions{HTTPHeader: h, HTTPClient: wsHTTPClient(s.client.httpClient)})
	if err != nil {
		if resp != nil && resp.StatusCode == http.StatusUnauthorized {
			return nil, ErrUnauthorized
		}
		if resp != nil && resp.StatusCode != http.StatusSwitchingProtocols {
			return nil, &APIError{Status: resp.StatusCode, Code: http.StatusText(resp.StatusCode), Message: err.Error()}
		}
		return nil, err
	}
	// The overview is a few hundred kB.
	conn.SetReadLimit(64 << 20)
	return conn, nil
}

// wsHTTPClient is the client's HTTP client without its timeout, which would
// end the stream.
func wsHTTPClient(h *http.Client) *http.Client {
	c := *h
	c.Timeout = 0
	return &c
}

func (s *Stream) run(conn *websocket.Conn) {
	defer close(s.done)
	defer close(s.out)
	backoff := minBackoff
	for {
		connected := time.Now()
		err := s.read(conn)
		if s.ctx.Err() != nil {
			return
		}
		s.mu.Lock()
		s.conn = nil
		s.mu.Unlock()
		if s.opts.DisableReconnect || websocket.CloseStatus(err) == websocket.StatusPolicyViolation {
			s.finish(err)
			return
		}
		// A connection that lived resets the backoff; one closed right after
		// the subscriptions (a slow consumer, a server refusing them) does
		// not, so that it is not reopened every half second.
		if time.Since(connected) > stableAfter {
			backoff = minBackoff
		}
		for {
			select {
			case <-s.ctx.Done():
				return
			case <-time.After(backoff):
			}
			backoff = min(backoff*2, s.opts.MaxBackoff)
			c, derr := s.dial(s.ctx)
			if errors.Is(derr, ErrUnauthorized) {
				s.finish(derr)
				return
			}
			if derr == nil {
				conn = c
				break
			}
		}
		if err := s.resubscribe(conn); err != nil {
			conn.CloseNow()
		}
	}
}

func (s *Stream) resubscribe(conn *websocket.Conn) error {
	s.mu.Lock()
	s.conn = conn
	subs := make([]ClientMessage, 0, len(s.subs))
	for _, m := range s.subs {
		subs = append(subs, m)
	}
	s.mu.Unlock()
	for _, m := range subs {
		b, _ := json.Marshal(m)
		if err := conn.Write(s.ctx, websocket.MessageText, b); err != nil {
			return err
		}
	}
	return nil
}

// dropRefused forgets a subscription the server refused, so that it is not
// sent again after a reconnection.
func (s *Stream) dropRefused(e *StreamError) {
	switch e.Code {
	case "bad_request", "unknown_channel", "too_many_subscriptions":
	default:
		return
	}
	if e.Channel == "" {
		return
	}
	s.mu.Lock()
	delete(s.subs, Subscription{Channel: e.Channel, Symbol: e.Symbol})
	s.mu.Unlock()
}

func (s *Stream) finish(err error) {
	s.mu.Lock()
	s.err = err
	s.mu.Unlock()
	s.cancel()
}

const (
	minBackoff   = 500 * time.Millisecond
	stableAfter  = 10 * time.Second
	pingInterval = 20 * time.Second
	pingTimeout  = 10 * time.Second
)

// keepAlive pings the server while the connection is read: a connection that
// died silently (no FIN, no RST) is closed after pingTimeout instead of
// blocking the stream forever.
func (s *Stream) keepAlive(ctx context.Context, conn *websocket.Conn) {
	t := time.NewTicker(pingInterval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			pctx, cancel := context.WithTimeout(ctx, pingTimeout)
			err := conn.Ping(pctx)
			cancel()
			if err != nil {
				conn.CloseNow()
				return
			}
		}
	}
}

func (s *Stream) read(conn *websocket.Conn) error {
	ctx, cancel := context.WithCancel(s.ctx)
	defer cancel()
	go s.keepAlive(ctx, conn)
	for {
		typ, data, err := conn.Read(s.ctx)
		if err != nil {
			return err
		}
		var m Message
		switch {
		case typ == websocket.MessageBinary:
			err = m.UnmarshalCapnp(data)
		default:
			err = json.Unmarshal(data, &m)
		}
		if errors.Is(err, ErrUnknownMessage) {
			continue
		}
		if err != nil {
			return fmt.Errorf("klondike: decode %s message: %w", strings.ToLower(typ.String()), err)
		}
		if m.Type == TypeError {
			s.dropRefused(m.Error)
		}
		select {
		case s.out <- m:
		case <-s.ctx.Done():
			return s.ctx.Err()
		}
	}
}
