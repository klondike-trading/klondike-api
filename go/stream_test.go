package klondike

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
)

// fakeServer records the client messages of each connection; the first
// connection refuses a subscription, sends an unknown message type, then
// closes with 1001.
type fakeServer struct {
	mu    sync.Mutex
	conns [][]ClientMessage
}

func (f *fakeServer) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Header.Get("X-API-Key") != "kl_test" {
		http.Error(w, `{"code":"unauthorized","error":"no"}`, http.StatusUnauthorized)
		return
	}
	c, err := websocket.Accept(w, r, nil)
	if err != nil {
		return
	}
	defer c.CloseNow()
	f.mu.Lock()
	n := len(f.conns)
	f.conns = append(f.conns, nil)
	f.mu.Unlock()
	ctx := r.Context()
	for {
		_, data, err := c.Read(ctx)
		if err != nil {
			return
		}
		var m ClientMessage
		json.Unmarshal(data, &m)
		f.mu.Lock()
		f.conns[n] = append(f.conns[n], m)
		count := len(f.conns[n])
		f.mu.Unlock()
		if n > 0 {
			continue
		}
		if m.Symbol == "BAD" {
			b, _ := json.Marshal(Message{Type: TypeError, Error: &StreamError{Code: "bad_request", Message: "no", Channel: m.Channel, Symbol: m.Symbol}})
			c.Write(ctx, websocket.MessageText, b)
		}
		if count == 3 {
			c.Write(ctx, websocket.MessageText, []byte(`{"type":"something_new","x":1}`))
			c.Write(ctx, websocket.MessageText, []byte(`{"type":"pong"}`))
			time.Sleep(50 * time.Millisecond)
			c.Close(websocket.StatusGoingAway, "restart")
			return
		}
	}
}

func (f *fakeServer) messages(n int) []ClientMessage {
	f.mu.Lock()
	defer f.mu.Unlock()
	if n >= len(f.conns) {
		return nil
	}
	return append([]ClientMessage(nil), f.conns[n]...)
}

func TestStreamReconnects(t *testing.T) {
	f := &fakeServer{}
	srv := httptest.NewServer(f)
	defer srv.Close()
	ctx := context.Background()
	if _, err := New("kl_wrong", WithBaseURL(srv.URL)).Stream(ctx, nil); err != ErrUnauthorized {
		t.Fatalf("bad key: %v", err)
	}
	s, err := New("kl_test", WithBaseURL(srv.URL)).Stream(ctx, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	s.SubscribeTrades(ctx, "BTC/USDT")
	s.SubscribeTrades(ctx, "BAD")
	s.SubscribeOverview(ctx)

	// The unknown type is skipped: the error then the pong arrive.
	for _, want := range []MessageType{TypeError, TypePong} {
		select {
		case m := <-s.Messages():
			if m.Type != want {
				t.Fatalf("got %s, want %s", m.Type, want)
			}
		case <-time.After(2 * time.Second):
			t.Fatalf("no %s", want)
		}
	}
	// The first connection is closed by the server after the pong.
	deadline := time.Now().Add(5 * time.Second)
	for len(f.messages(1)) < 2 {
		if time.Now().After(deadline) {
			t.Fatalf("not subscribed again: %+v", f.messages(1))
		}
		time.Sleep(20 * time.Millisecond)
	}
	time.Sleep(100 * time.Millisecond)
	again := map[Subscription]bool{}
	for _, m := range f.messages(1) {
		again[Subscription{m.Channel, m.Symbol}] = true
	}
	if len(again) != 2 || !again[Subscription{ChannelTrades, "BTC/USDT"}] || !again[Subscription{ChannelOverview, ""}] {
		t.Errorf("subscriptions sent again: %+v", f.messages(1))
	}
}
