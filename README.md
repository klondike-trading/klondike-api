# Klondike Market Data API

Real-time crypto market data consolidated across about forty centralized venues: trades, merged
order books (every level with its venue) and a market overview (what an order really costs on the
merged book and on the best single venue, crosses between venues, implied prices), over REST and
WebSocket, in JSON or Cap'n Proto.

**Documentation: https://klondike-trading.github.io/klondike-api/**

| | Package | |
|---|---|---|
| Python | `pip install klondike` | [python/](python/) |
| JavaScript / TypeScript | `npm install @klondike-trading/klondike` | [js/](js/) |
| Go | `go get github.com/klondike-trading/klondike-api/go` | [go/](go/) |
| Rust | `cargo add klondike` | [rust/](rust/) |
| C# / .NET | `dotnet add package Klondike` | [csharp/](csharp/) |
| Excel (Windows) | add-in with live worksheet functions | [csharp/src/Klondike.Excel/](csharp/src/Klondike.Excel/) |

API keys are created on the [portal](https://api.klondike-crypto.com/portal/).

```sh
curl -H "X-API-Key: $KLONDIKE_API_KEY" "https://api.klondike-crypto.com/v1/orderbook?symbol=BTC/USDT&depth=5"
```

## Repository

| | |
|---|---|
| [`openapi.yaml`](openapi.yaml) | REST contract (OpenAPI 3.1) |
| [`schema/klondike_api.capnp`](schema/klondike_api.capnp) | binary frames of the WebSocket (`format=capnp`) |
| [`testdata/messages`](testdata/messages) | every server message in both formats, decoded by the tests of each library |
| [`docs/`](docs/) | the documentation site (MkDocs Material), published by `.github/workflows/docs.yml` |

This repository is published from the Klondike monorepo, where the server lives: changes are made
there and copied here. Issues are welcome.

## License

MIT
