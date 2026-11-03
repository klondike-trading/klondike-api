//! REST calls against a mock server.

use std::time::Duration;

use klondike::{Client, Error, OrderbookOptions, Side, TradesOptions};
use wiremock::matchers::{header, method, path, query_param, query_param_is_missing};
use wiremock::{Mock, MockServer, ResponseTemplate};

const KEY: &str = "kl_0123456789abcdefghijABCDEFGHIJ01";

fn client(server: &MockServer) -> Client {
    Client::builder()
        .api_key(KEY)
        .base_url(server.uri())
        .build()
        .unwrap()
}

fn json(body: &str) -> ResponseTemplate {
    ResponseTemplate::new(200).set_body_raw(body.as_bytes().to_vec(), "application/json")
}

#[tokio::test]
async fn symbols() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/v1/symbols"))
        .and(header("X-API-Key", KEY))
        .and(query_param("q", "btc"))
        .respond_with(json(
            r#"{"symbols":[{"symbol":"BTC/USDT","market":"spot","venues":["binance","okx"],"lastTradeTs":1793706479654}]}"#,
        ))
        .expect(1)
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/v1/symbols"))
        .and(query_param_is_missing("q"))
        .respond_with(json(r#"{"symbols":[]}"#))
        .expect(1)
        .mount(&server)
        .await;

    let c = client(&server);
    let symbols = c.symbols("btc").await.unwrap();
    assert_eq!(symbols.len(), 1);
    assert_eq!(symbols[0].symbol, "BTC/USDT");
    assert_eq!(symbols[0].market, "spot");
    assert_eq!(symbols[0].venues, ["binance", "okx"]);
    assert_eq!(symbols[0].last_trade_ts, 1793706479654);
    assert!(c.symbols("").await.unwrap().is_empty());
}

#[tokio::test]
async fn trades_with_options() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/v1/trades"))
        .and(header("X-API-Key", KEY))
        .and(header("Accept", "application/json"))
        .and(query_param("symbol", "BTC/USDT:USDT"))
        .and(query_param("limit", "2"))
        .and(query_param("since", "1793706479000"))
        .and(query_param("venues", "binanceusdm,bybit"))
        .respond_with(json(
            r#"{"symbol":"BTC/USDT:USDT","trades":[
                {"ts":1793706479654,"venue":"binanceusdm","price":104250.1,"qty":0.0125,"side":"buy"},
                {"ts":1793706479702,"venue":"bybit","price":104249.9,"qty":0.4,"side":"sell"}]}"#,
        ))
        .expect(1)
        .mount(&server)
        .await;

    let trades = client(&server)
        .trades(
            "BTC/USDT:USDT",
            TradesOptions {
                limit: Some(2),
                since: Some(1793706479000),
                venues: vec!["binanceusdm".into(), "bybit".into()],
            },
        )
        .await
        .unwrap();
    assert_eq!(trades.symbol, "BTC/USDT:USDT");
    assert_eq!(trades.trades.len(), 2);
    assert_eq!(trades.trades[1].side, Side::Sell);
    assert_eq!(trades.trades[1].qty, 0.4);
}

#[tokio::test]
async fn trades_without_options() {
    let server = MockServer::start().await;
    Mock::given(path("/v1/trades"))
        .and(query_param("symbol", "BTC/USDT"))
        .and(query_param_is_missing("limit"))
        .and(query_param_is_missing("since"))
        .and(query_param_is_missing("venues"))
        .respond_with(json(r#"{"symbol":"BTC/USDT","trades":[]}"#))
        .expect(1)
        .mount(&server)
        .await;
    let trades = client(&server)
        .trades("BTC/USDT", Default::default())
        .await
        .unwrap();
    assert!(trades.trades.is_empty());
}

#[tokio::test]
async fn orderbook() {
    let server = MockServer::start().await;
    Mock::given(path("/v1/orderbook"))
        .and(query_param("symbol", "BTC/USDT"))
        .and(query_param("depth", "2"))
        .and(query_param("venues", "binance,kraken"))
        .respond_with(json(
            r#"{"symbol":"BTC/USDT","ts":1793706479000,
                "venues":[{"venue":"binance","ts":1793706478950,"bid":104250.0,"bidQty":1.2,"ask":104250.1,"askQty":0.8},
                          {"venue":"kraken","ts":null,"bid":104249.0,"bidQty":0.5,"ask":null,"askQty":null}],
                "bids":[{"price":104250.0,"qty":1.2,"venue":"binance"},{"price":104249.0,"qty":0.5,"venue":"kraken"}],
                "asks":[{"price":104250.1,"qty":0.8,"venue":"binance"}]}"#,
        ))
        .mount(&server)
        .await;
    let book = client(&server)
        .orderbook(
            "BTC/USDT",
            OrderbookOptions {
                depth: Some(2),
                venues: vec!["binance".into(), "kraken".into()],
            },
        )
        .await
        .unwrap();
    assert_eq!(book.ts, 1793706479000);
    assert_eq!(book.venues[1].ts, None);
    assert_eq!(book.venues[1].ask, None);
    assert_eq!(book.venues[1].bid, Some(104249.0));
    assert_eq!(book.bids[1].venue, "kraken");
    assert_eq!(book.asks[0].price, 104250.1);
}

#[tokio::test]
async fn overview() {
    let server = MockServer::start().await;
    let body = std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../testdata/messages/2-overview.json"),
    )
    .unwrap();
    let doc: serde_json::Value = serde_json::from_slice(&body).unwrap();
    Mock::given(path("/v1/overview"))
        .respond_with(ResponseTemplate::new(200).set_body_json(&doc["overview"]))
        .mount(&server)
        .await;
    let ov = client(&server).overview().await.unwrap();
    assert_eq!(ov.ts, 1);
    assert_eq!(ov.pairs[0].basis_bps, Some(1.5));
    assert_eq!(ov.leaders[0].share, 1.0);
}

#[tokio::test]
async fn base_url_with_a_path() {
    let server = MockServer::start().await;
    Mock::given(path("/prefix/v1/symbols"))
        .respond_with(json(r#"{"symbols":[]}"#))
        .expect(1)
        .mount(&server)
        .await;
    let c = Client::builder()
        .api_key(KEY)
        .base_url(format!("{}/prefix/", server.uri()))
        .build()
        .unwrap();
    c.symbols("").await.unwrap();
}

#[tokio::test]
async fn api_errors() {
    let server = MockServer::start().await;
    let error = |status: u16, code: &str, msg: &str| {
        ResponseTemplate::new(status).set_body_raw(
            format!(r#"{{"code":"{code}","error":"{msg}"}}"#).into_bytes(),
            "application/json",
        )
    };
    Mock::given(path("/v1/symbols"))
        .respond_with(error(401, "unauthorized", "unknown API key"))
        .mount(&server)
        .await;
    Mock::given(path("/v1/trades"))
        .respond_with(
            error(429, "rate_limited", "rate limit exceeded").insert_header("Retry-After", "7"),
        )
        .mount(&server)
        .await;
    Mock::given(path("/v1/orderbook"))
        .respond_with(error(404, "not_found", "no order book for FOO/BAR"))
        .mount(&server)
        .await;
    Mock::given(path("/v1/overview"))
        .respond_with(ResponseTemplate::new(502).set_body_string("<html>bad gateway</html>\n"))
        .mount(&server)
        .await;
    let c = client(&server);

    let e = c.symbols("").await.unwrap_err();
    assert!(e.is_unauthorized());
    match &e {
        Error::Api {
            status,
            code,
            message,
            retry_after,
        } => {
            assert_eq!(
                (*status, code.as_str(), message.as_str()),
                (401, "unauthorized", "unknown API key")
            );
            assert_eq!(*retry_after, None);
        }
        e => panic!("{e:?}"),
    }
    assert_eq!(e.to_string(), "klondike: 401 unauthorized: unknown API key");

    let e = c.trades("BTC/USDT", Default::default()).await.unwrap_err();
    assert!(e.is_rate_limited());
    assert_eq!(e.retry_after(), Some(Duration::from_secs(7)));

    let e = c
        .orderbook("FOO/BAR", Default::default())
        .await
        .unwrap_err();
    assert!(e.is_not_found());
    assert!(matches!(e, Error::Api { ref code, .. } if code == "not_found"));

    // Not an error of the API (a proxy): the status text and the body.
    match c.overview().await.unwrap_err() {
        Error::Api {
            status,
            code,
            message,
            ..
        } => {
            assert_eq!(
                (status, code.as_str(), message.as_str()),
                (502, "Bad Gateway", "<html>bad gateway</html>")
            );
        }
        e => panic!("{e:?}"),
    }
}

#[tokio::test]
async fn invalid_json_is_a_decode_error() {
    let server = MockServer::start().await;
    Mock::given(path("/v1/trades"))
        .respond_with(json("{not json"))
        .mount(&server)
        .await;
    let e = client(&server)
        .trades("BTC/USDT", Default::default())
        .await
        .unwrap_err();
    assert!(matches!(e, Error::Json(_)), "{e:?}");
}

#[tokio::test]
async fn connection_refused_is_an_http_error() {
    let c = Client::builder()
        .api_key(KEY)
        .base_url("http://127.0.0.1:1")
        .build()
        .unwrap();
    let e = c.symbols("").await.unwrap_err();
    assert!(matches!(e, Error::Http(_)), "{e:?}");
}

#[test]
fn builder_validation() {
    assert!(matches!(Client::builder().build(), Err(Error::Config(_))));
    assert!(matches!(
        Client::builder().api_key(KEY).base_url("ftp://x").build(),
        Err(Error::Config(_))
    ));
    assert!(matches!(
        Client::builder().api_key(KEY).base_url("not a url").build(),
        Err(Error::Config(_))
    ));
    let c = Client::new(KEY);
    assert_eq!(c.base_url(), klondike::DEFAULT_BASE_URL);
    assert!(!format!("{c:?}").contains(KEY), "the key leaks in Debug");
}

#[tokio::test]
async fn custom_http_client() {
    let server = MockServer::start().await;
    Mock::given(path("/v1/symbols"))
        .and(header("X-Custom", "1"))
        .respond_with(json(r#"{"symbols":[]}"#))
        .expect(1)
        .mount(&server)
        .await;
    let mut headers = klondike::reqwest::header::HeaderMap::new();
    headers.insert("X-Custom", "1".parse().unwrap());
    let http = klondike::reqwest::Client::builder()
        .default_headers(headers)
        .build()
        .unwrap();
    let c = Client::builder()
        .api_key(KEY)
        .base_url(server.uri())
        .http_client(http)
        .build()
        .unwrap();
    c.symbols("").await.unwrap();
}
