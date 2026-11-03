// Package klondike is the Go client of the Klondike market data API: trades
// and merged order books of the crypto pairs Klondike streams from about forty
// venues, and the market overview, over REST and WebSocket.
//
//	c := klondike.New(os.Getenv("KLONDIKE_API_KEY"))
//	book, err := c.Orderbook(ctx, "BTC/USDT", &klondike.OrderbookOptions{Depth: 10})
//
// See https://klondike-trading.github.io/klondike-api/ for the documentation.
package klondike

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// DefaultBaseURL is the production API.
const DefaultBaseURL = "https://api.klondike-crypto.com"

// Version is the version of this library, sent in the User-Agent.
const Version = "0.1.0"

// Client calls the REST endpoints and opens WebSocket streams. It is safe for
// concurrent use.
type Client struct {
	baseURL    string
	apiKey     string
	httpClient *http.Client
	userAgent  string
}

// Option configures a Client.
type Option func(*Client)

// WithBaseURL points the client to another server (a test server, for instance).
func WithBaseURL(u string) Option {
	return func(c *Client) { c.baseURL = strings.TrimRight(u, "/") }
}

// WithHTTPClient replaces the default HTTP client (30 s timeout).
func WithHTTPClient(h *http.Client) Option {
	return func(c *Client) { c.httpClient = h }
}

// New returns a client authenticated with the API key.
func New(apiKey string, opts ...Option) *Client {
	c := &Client{
		baseURL:    DefaultBaseURL,
		apiKey:     apiKey,
		httpClient: &http.Client{Timeout: 30 * time.Second},
		userAgent:  "klondike-go/" + Version,
	}
	for _, o := range opts {
		o(c)
	}
	return c
}

// APIError is an error answer of the API.
type APIError struct {
	Status     int    // HTTP status
	Code       string // bad_request, unauthorized, not_found, rate_limited...
	Message    string
	RetryAfter time.Duration // set on 429
}

func (e *APIError) Error() string {
	return fmt.Sprintf("klondike: %d %s: %s", e.Status, e.Code, e.Message)
}

// IsNotFound reports whether err is a 404 of the API.
func IsNotFound(err error) bool {
	var e *APIError
	return errors.As(err, &e) && e.Status == http.StatusNotFound
}

// Symbols lists the symbols with recent trades; query filters them on a case
// insensitive substring ("" for all).
func (c *Client) Symbols(ctx context.Context, query string) ([]SymbolInfo, error) {
	q := url.Values{}
	if query != "" {
		q.Set("q", query)
	}
	var out struct {
		Symbols []SymbolInfo `json:"symbols"`
	}
	if err := c.get(ctx, "/v1/symbols", q, &out); err != nil {
		return nil, err
	}
	return out.Symbols, nil
}

// TradesOptions are the optional parameters of Trades.
type TradesOptions struct {
	Limit  int      // most recent trades, 1 to 1000 (default 100)
	Since  int64    // only trades strictly after this timestamp, ms
	Venues []string // CCXT exchange ids; all when empty
}

// Trades returns the recent trades of the symbol on every venue, oldest first.
func (c *Client) Trades(ctx context.Context, symbol string, opts *TradesOptions) (*Trades, error) {
	q := url.Values{"symbol": {symbol}}
	if opts != nil {
		if opts.Limit > 0 {
			q.Set("limit", strconv.Itoa(opts.Limit))
		}
		if opts.Since > 0 {
			q.Set("since", strconv.FormatInt(opts.Since, 10))
		}
		if len(opts.Venues) > 0 {
			q.Set("venues", strings.Join(opts.Venues, ","))
		}
	}
	var out Trades
	if err := c.get(ctx, "/v1/trades", q, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// OrderbookOptions are the optional parameters of Orderbook.
type OrderbookOptions struct {
	Depth  int      // levels per side, 1 to 1000 (default 50)
	Venues []string // CCXT exchange ids; all when empty
}

// Orderbook returns the merged order book of the symbol.
func (c *Client) Orderbook(ctx context.Context, symbol string, opts *OrderbookOptions) (*Orderbook, error) {
	q := url.Values{"symbol": {symbol}}
	if opts != nil {
		if opts.Depth > 0 {
			q.Set("depth", strconv.Itoa(opts.Depth))
		}
		if len(opts.Venues) > 0 {
			q.Set("venues", strings.Join(opts.Venues, ","))
		}
	}
	var out Orderbook
	if err := c.get(ctx, "/v1/orderbook", q, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// Overview returns the market overview.
func (c *Client) Overview(ctx context.Context) (*Overview, error) {
	var out Overview
	if err := c.get(ctx, "/v1/overview", nil, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (c *Client) get(ctx context.Context, path string, q url.Values, out any) error {
	u := c.baseURL + path
	if len(q) > 0 {
		u += "?" + q.Encode()
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return err
	}
	req.Header.Set("X-API-Key", c.apiKey)
	req.Header.Set("Accept", "application/json")
	req.Header.Set("User-Agent", c.userAgent)
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return apiError(resp)
	}
	return json.NewDecoder(resp.Body).Decode(out)
}

func apiError(resp *http.Response) error {
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 64<<10))
	e := &APIError{Status: resp.StatusCode, Code: http.StatusText(resp.StatusCode), Message: strings.TrimSpace(string(body))}
	var payload struct {
		Code  string `json:"code"`
		Error string `json:"error"`
	}
	if json.Unmarshal(body, &payload) == nil && payload.Error != "" {
		e.Code, e.Message = payload.Code, payload.Error
	}
	if s, err := strconv.Atoi(resp.Header.Get("Retry-After")); err == nil {
		e.RetryAfter = time.Duration(s) * time.Second
	}
	return e
}
