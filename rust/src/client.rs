use std::sync::Arc;
use std::time::Duration;

use reqwest::header::{HeaderValue, ACCEPT, RETRY_AFTER, USER_AGENT};
use reqwest::Url;
use serde::de::DeserializeOwned;
use serde::Deserialize;

use crate::{Error, Orderbook, Overview, Result, SymbolInfo, Trades};

/// The production API.
pub const DEFAULT_BASE_URL: &str = "https://api.klondike-crypto.com";

/// Version of this library, sent in the `User-Agent`.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

const DEFAULT_TIMEOUT: Duration = Duration::from_secs(30);

/// Client of the API: calls the REST endpoints and opens WebSocket streams.
///
/// It is cheap to clone (the clones share the HTTP connection pool) and can
/// be used from several tasks at once.
///
/// ```no_run
/// # async fn run() -> klondike::Result<()> {
/// let client = klondike::Client::new(std::env::var("KLONDIKE_API_KEY").unwrap());
/// let book = client
///     .orderbook("BTC/USDT", klondike::OrderbookOptions { depth: Some(10), ..Default::default() })
///     .await?;
/// println!("best bid {:?}", book.bids.first());
/// # Ok(()) }
/// ```
#[derive(Clone, Debug)]
pub struct Client {
    pub(crate) inner: Arc<Inner>,
}

pub(crate) struct Inner {
    pub(crate) base_url: Url,
    pub(crate) api_key: String,
    pub(crate) user_agent: String,
    http: reqwest::Client,
}

impl std::fmt::Debug for Inner {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Client")
            .field("base_url", &self.base_url.as_str())
            .field("api_key", &"<redacted>")
            .finish_non_exhaustive()
    }
}

/// Builder of a [`Client`], to change the base URL or the HTTP client.
///
/// ```no_run
/// # fn run() -> klondike::Result<()> {
/// let client = klondike::Client::builder()
///     .api_key("kl_...")
///     .base_url("http://localhost:8093")
///     .build()?;
/// # Ok(()) }
/// ```
#[derive(Default)]
#[must_use]
pub struct ClientBuilder {
    api_key: Option<String>,
    base_url: Option<String>,
    http: Option<reqwest::Client>,
}

impl ClientBuilder {
    /// The API key, created on <https://api.klondike-crypto.com/portal/>
    /// (`kl_` followed by 32 characters). Required.
    pub fn api_key(mut self, api_key: impl Into<String>) -> Self {
        self.api_key = Some(api_key.into());
        self
    }

    /// The server, [`DEFAULT_BASE_URL`] by default (a test server, for
    /// instance). The WebSocket URL is derived from it (`https` → `wss`).
    pub fn base_url(mut self, base_url: impl Into<String>) -> Self {
        self.base_url = Some(base_url.into());
        self
    }

    /// The HTTP client of the REST calls, instead of the default one (30 s
    /// timeout). Its timeout applies to the REST calls only.
    pub fn http_client(mut self, http: reqwest::Client) -> Self {
        self.http = Some(http);
        self
    }

    /// Builds the client.
    pub fn build(self) -> Result<Client> {
        let api_key = self
            .api_key
            .ok_or_else(|| Error::Config("API key required".into()))?;
        let raw = self.base_url.as_deref().unwrap_or(DEFAULT_BASE_URL);
        let mut base_url = Url::parse(raw.trim_end_matches('/'))
            .map_err(|e| Error::Config(format!("invalid base URL {raw:?}: {e}")))?;
        if !matches!(base_url.scheme(), "http" | "https") || base_url.cannot_be_a_base() {
            return Err(Error::Config(format!(
                "invalid base URL {raw:?}: http or https expected"
            )));
        }
        base_url.set_query(None);
        base_url.set_fragment(None);
        let http = match self.http {
            Some(h) => h,
            None => reqwest::Client::builder()
                .timeout(DEFAULT_TIMEOUT)
                .build()?,
        };
        Ok(Client {
            inner: Arc::new(Inner {
                base_url,
                api_key,
                user_agent: format!("klondike-rust/{VERSION}"),
                http,
            }),
        })
    }
}

impl std::fmt::Debug for ClientBuilder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientBuilder")
            .field("base_url", &self.base_url)
            .field("api_key", &self.api_key.as_ref().map(|_| "<redacted>"))
            .finish_non_exhaustive()
    }
}

/// Optional parameters of [`Client::trades`].
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TradesOptions {
    /// Number of trades, the most recent ones: 1 to 1000 (100 when `None`).
    pub limit: Option<u32>,
    /// Only trades strictly after this timestamp, ms.
    pub since: Option<i64>,
    /// CCXT exchange ids; all venues when empty.
    pub venues: Vec<String>,
}

/// Optional parameters of [`Client::orderbook`].
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OrderbookOptions {
    /// Levels per side: 1 to 1000 (50 when `None`).
    pub depth: Option<u32>,
    /// CCXT exchange ids; all venues when empty. The `venues` quotes of the
    /// book are not filtered, the levels are.
    pub venues: Vec<String>,
}

impl Client {
    /// A client of the production API authenticated with the key.
    ///
    /// # Panics
    ///
    /// When the TLS backend cannot be initialized, like
    /// [`reqwest::Client::new`]; [`Client::builder`] returns the error instead.
    pub fn new(api_key: impl Into<String>) -> Client {
        Client::builder()
            .api_key(api_key)
            .build()
            .expect("klondike: cannot build the HTTP client")
    }

    /// A builder, to change the base URL or the HTTP client.
    pub fn builder() -> ClientBuilder {
        ClientBuilder::default()
    }

    /// The base URL of the API.
    pub fn base_url(&self) -> &str {
        self.inner.base_url.as_str().trim_end_matches('/')
    }

    /// The symbols the API received trades for in the last minutes, with
    /// their venues, sorted by symbol. `query` filters them on a case
    /// insensitive substring (`"btc"`, `"/usdc"`; `""` for all).
    pub async fn symbols(&self, query: &str) -> Result<Vec<SymbolInfo>> {
        #[derive(Deserialize)]
        struct Out {
            symbols: Vec<SymbolInfo>,
        }
        let mut q = Vec::new();
        if !query.is_empty() {
            q.push(("q", query.to_string()));
        }
        let out: Out = self.get("/v1/symbols", &q).await?;
        Ok(out.symbols)
    }

    /// The last trades of the symbol on every venue, oldest first. The API
    /// keeps the last 1,000 trades of each symbol: stream them to get them
    /// all.
    pub async fn trades(&self, symbol: &str, opts: TradesOptions) -> Result<Trades> {
        let mut q = vec![("symbol", symbol.to_string())];
        if let Some(limit) = opts.limit {
            q.push(("limit", limit.to_string()));
        }
        if let Some(since) = opts.since {
            q.push(("since", since.to_string()));
        }
        if !opts.venues.is_empty() {
            q.push(("venues", opts.venues.join(",")));
        }
        self.get("/v1/trades", &q).await
    }

    /// The merged order book of the symbol. The first request for a symbol
    /// nobody follows can take up to two seconds; a symbol no venue streams
    /// is a 404 ([`Error::is_not_found`]).
    pub async fn orderbook(&self, symbol: &str, opts: OrderbookOptions) -> Result<Orderbook> {
        let mut q = vec![("symbol", symbol.to_string())];
        if let Some(depth) = opts.depth {
            q.push(("depth", depth.to_string()));
        }
        if !opts.venues.is_empty() {
            q.push(("venues", opts.venues.join(",")));
        }
        self.get("/v1/orderbook", &q).await
    }

    /// The market overview (a 503 until the API received one).
    pub async fn overview(&self) -> Result<Overview> {
        self.get("/v1/overview", &[]).await
    }

    pub(crate) fn url(&self, path: &str) -> Url {
        let mut url = self.inner.base_url.clone();
        let joined = format!("{}{}", url.path().trim_end_matches('/'), path);
        url.set_path(&joined);
        url
    }

    async fn get<T: DeserializeOwned>(&self, path: &str, query: &[(&str, String)]) -> Result<T> {
        let mut url = self.url(path);
        if !query.is_empty() {
            url.query_pairs_mut()
                .extend_pairs(query.iter().map(|(k, v)| (*k, v.as_str())));
        }
        let resp = self
            .inner
            .http
            .get(url)
            .header("X-API-Key", self.api_key_header()?)
            .header(ACCEPT, "application/json")
            .header(USER_AGENT, self.inner.user_agent.as_str())
            .send()
            .await?;
        let status = resp.status();
        let retry_after = resp
            .headers()
            .get(RETRY_AFTER)
            .and_then(|v| v.to_str().ok())
            .map(str::to_string);
        let body = resp.bytes().await?;
        if status != reqwest::StatusCode::OK {
            return Err(Error::from_response(
                status.as_u16(),
                retry_after.as_deref(),
                &body,
            ));
        }
        Ok(serde_json::from_slice(&body)?)
    }

    pub(crate) fn api_key_header(&self) -> Result<HeaderValue> {
        let mut v = HeaderValue::from_str(&self.inner.api_key)
            .map_err(|_| Error::Config("invalid characters in the API key".into()))?;
        v.set_sensitive(true);
        Ok(v)
    }
}
