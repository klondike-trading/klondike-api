# Authentication

Every `/v1` endpoint takes an API key.

## Getting a key

1. Open the [portal](https://api.klondike-crypto.com/portal/) and sign in with
   your Klondike account.
2. Name the key (the machine or project it is for) and create it.
3. Copy it: it is shown **once**. Klondike only keeps a hash of it.

Keys look like `kl_` followed by 32 letters and digits. You can hold up to 10
active keys; revoke the ones you no longer use on the portal. A revoked key is
refused within a minute.

No account yet? Ask your Klondike contact.

## Sending the key

=== "Header"

    ```sh
    curl -H "X-API-Key: kl_..." "https://api.klondike-crypto.com/v1/overview"
    ```

=== "Bearer"

    ```sh
    curl -H "Authorization: Bearer kl_..." "https://api.klondike-crypto.com/v1/overview"
    ```

=== "WebSocket in a browser"

    Browsers cannot set headers on a WebSocket: pass the key in the query.

    ```js
    new WebSocket("wss://api.klondike-crypto.com/v1/ws?format=json&api_key=kl_...");
    ```

    A key in a URL can end up in logs and in the browser history: use a key
    dedicated to the page, and revoke it if it leaks.

Keep keys out of source code: read them from an environment variable
(`KLONDIKE_API_KEY` in the examples) or a secret manager.

## Limits

| | per key |
|---|---|
| REST requests | 10 per second, bursts of 20 |
| WebSocket connections | 5 at once |
| Subscriptions | 50 per WebSocket connection |

Beyond them the API answers `429 Too Many Requests` with a `Retry-After`
header (seconds). The libraries expose it (`retry_after`, `retryAfter`,
`RetryAfter`).

## Errors

Errors are JSON with a stable `code` and a message:

```json
{"code": "unauthorized", "error": "unknown or revoked API key"}
```

| Status | `code` | |
|---|---|---|
| 400 | `bad_request` | invalid parameter |
| 401 | `unauthorized` | missing, unknown or revoked key |
| 404 | `not_found` | no data for this symbol |
| 429 | `rate_limited` | too many requests or connections |
| 503 | `unavailable` | the data is not available yet, retry later |
