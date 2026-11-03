//! WebSocket streams against a scripted local server.

// The handshake callback type of tungstenite returns a large error.
#![allow(clippy::result_large_err)]

use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use klondike::{
    Channel, Client, Error, Format, Message, Orderbook, StreamOptions, Subscription, Trade, Trades,
};
use serde_json::{json, Value};
use tokio::net::{TcpListener, TcpStream};
use tokio::time::timeout;
use tokio_tungstenite::tungstenite::handshake::server::{ErrorResponse, Request, Response};
use tokio_tungstenite::tungstenite::protocol::frame::coding::CloseCode;
use tokio_tungstenite::tungstenite::protocol::CloseFrame;
use tokio_tungstenite::tungstenite::Message as WsMessage;
use tokio_tungstenite::WebSocketStream;

const KEY: &str = "kl_0123456789abcdefghijABCDEFGHIJ01";
const WAIT: Duration = Duration::from_secs(5);

type Ws = WebSocketStream<TcpStream>;

async fn listen() -> (TcpListener, Client) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let client = Client::builder()
        .api_key(KEY)
        .base_url(url)
        .build()
        .unwrap();
    (listener, client)
}

/// Accepts a connection; returns it with its format.
async fn accept(l: &TcpListener) -> (Ws, Format) {
    let (tcp, _) = timeout(WAIT, l.accept())
        .await
        .expect("no connection")
        .unwrap();
    let seen = Arc::new(Mutex::new((String::new(), String::new())));
    let s = seen.clone();
    let ws = tokio_tungstenite::accept_hdr_async(tcp, move |req: &Request, resp: Response| {
        let key = req
            .headers()
            .get("X-API-Key")
            .map(|v| v.to_str().unwrap().to_string());
        *s.lock().unwrap() = (req.uri().to_string(), key.unwrap_or_default());
        Ok(resp)
    })
    .await
    .unwrap();
    let (uri, key) = seen.lock().unwrap().clone();
    assert!(uri.starts_with("/v1/ws?format="), "{uri}");
    assert_eq!(key, KEY);
    let format = if uri.ends_with("format=capnp") {
        Format::Capnp
    } else {
        Format::Json
    };
    (ws, format)
}

/// Refuses the upgrade of the next connection with an HTTP error.
async fn refuse(l: &TcpListener, status: u16, body: &'static str) {
    let (tcp, _) = timeout(WAIT, l.accept())
        .await
        .expect("no connection")
        .unwrap();
    let r = tokio_tungstenite::accept_hdr_async(tcp, move |_: &Request, _: Response| {
        let mut resp = ErrorResponse::new(Some(body.to_string()));
        *resp.status_mut() = status.try_into().unwrap();
        Err(resp)
    })
    .await;
    assert!(r.is_err());
}

/// The next client message.
async fn recv(ws: &mut Ws) -> Value {
    loop {
        match timeout(WAIT, ws.next()).await.expect("no client message") {
            Some(Ok(WsMessage::Text(t))) => return serde_json::from_str(&t).unwrap(),
            Some(Ok(WsMessage::Ping(_) | WsMessage::Pong(_))) => continue,
            other => panic!("unexpected {other:?}"),
        }
    }
}

async fn send(ws: &mut Ws, format: Format, m: &Message) {
    let frame = match format {
        Format::Json => WsMessage::text(String::from_utf8(m.to_json().unwrap()).unwrap()),
        Format::Capnp => WsMessage::binary(m.to_capnp().unwrap()),
    };
    ws.send(frame).await.unwrap();
}

async fn next(stream: &mut klondike::Stream) -> Option<klondike::Result<Message>> {
    timeout(WAIT, stream.next()).await.expect("no message")
}

fn trades() -> Message {
    Message::Trades(Trades {
        symbol: "BTC/USDT".into(),
        trades: vec![Trade {
            ts: 1,
            venue: "binance".into(),
            price: 2.5,
            qty: 0.1,
            side: klondike::Side::Buy,
        }],
    })
}

fn book() -> Message {
    let fixture = std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../testdata/messages/1-orderbook.json"),
    )
    .unwrap();
    let Message::Orderbook(b) = Message::from_json(&fixture).unwrap() else {
        panic!()
    };
    Message::Orderbook(Orderbook {
        symbol: "ETH/USDT".into(),
        ..b
    })
}

fn subscribed(channel: Channel, symbol: &str) -> Message {
    Message::Subscribed(Subscription {
        channel,
        symbol: symbol.into(),
    })
}

async fn exchange(format: Format) {
    let (l, client) = listen().await;
    let server = tokio::spawn(async move {
        let (mut ws, f) = accept(&l).await;
        assert_eq!(f, format);
        assert_eq!(
            recv(&mut ws).await,
            json!({"op":"subscribe","channel":"trades","symbol":"BTC/USDT"})
        );
        send(&mut ws, f, &subscribed(Channel::Trades, "BTC/USDT")).await;
        send(&mut ws, f, &trades()).await;
        assert_eq!(
            recv(&mut ws).await,
            json!({"op":"subscribe","channel":"orderbook","symbol":"ETH/USDT","depth":5,"venues":["binance","kraken"]})
        );
        send(&mut ws, f, &book()).await;
        assert_eq!(
            recv(&mut ws).await,
            json!({"op":"subscribe","channel":"overview"})
        );
        let ov = std::fs::read(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../testdata/messages/2-overview.json"),
        )
        .unwrap();
        send(&mut ws, f, &Message::from_json(&ov).unwrap()).await;
        assert_eq!(
            recv(&mut ws).await,
            json!({"op":"unsubscribe","channel":"trades","symbol":"BTC/USDT"})
        );
        assert_eq!(recv(&mut ws).await, json!({"op":"ping"}));
        send(&mut ws, f, &Message::Pong).await;
        // The client closes.
        loop {
            match timeout(WAIT, ws.next()).await.unwrap() {
                Some(Ok(WsMessage::Close(_))) | None => break,
                Some(Ok(_)) => {}
                Some(Err(_)) => break,
            }
        }
    });

    let mut stream = client
        .stream(StreamOptions {
            format,
            ..Default::default()
        })
        .await
        .unwrap();
    stream.subscribe_trades("BTC/USDT", &[]).await.unwrap();
    assert_eq!(
        next(&mut stream).await.unwrap().unwrap(),
        subscribed(Channel::Trades, "BTC/USDT")
    );
    assert_eq!(next(&mut stream).await.unwrap().unwrap(), trades());
    stream
        .subscribe_orderbook("ETH/USDT", 5, &["binance", "kraken"])
        .await
        .unwrap();
    assert_eq!(next(&mut stream).await.unwrap().unwrap(), book());
    // From another task, with a handle.
    let handle = stream.handle();
    tokio::spawn(async move { handle.subscribe_overview().await.unwrap() })
        .await
        .unwrap();
    match next(&mut stream).await.unwrap().unwrap() {
        Message::Overview(o) => assert_eq!(o.pairs[0].basis_bps, Some(1.5)),
        m => panic!("{m:?}"),
    }
    stream
        .unsubscribe(Channel::Trades, "BTC/USDT")
        .await
        .unwrap();
    stream.ping().await.unwrap();
    assert_eq!(next(&mut stream).await.unwrap().unwrap(), Message::Pong);
    stream.close().await;
    assert!(next(&mut stream).await.is_none());
    assert!(matches!(stream.ping().await, Err(Error::StreamClosed)));
    server.await.unwrap();
}

#[tokio::test]
async fn exchange_json() {
    exchange(Format::Json).await;
}

#[tokio::test]
async fn exchange_capnp() {
    exchange(Format::Capnp).await;
}

#[tokio::test]
async fn futures_stream() {
    let (l, client) = listen().await;
    tokio::spawn(async move {
        let (mut ws, f) = accept(&l).await;
        recv(&mut ws).await;
        send(&mut ws, f, &Message::Pong).await;
        let _ = ws.next().await;
    });
    let stream = client.stream(StreamOptions::default()).await.unwrap();
    stream.ping().await.unwrap();
    let mut s = Box::pin(stream);
    let m = timeout(WAIT, futures_util::StreamExt::next(&mut s))
        .await
        .unwrap();
    assert_eq!(m.unwrap().unwrap(), Message::Pong);
}

#[tokio::test]
async fn reconnects_and_subscribes_again() {
    let (l, client) = listen().await;
    let server = tokio::spawn(async move {
        let (mut ws, f) = accept(&l).await;
        recv(&mut ws).await;
        recv(&mut ws).await;
        recv(&mut ws).await;
        // Server restart.
        ws.close(Some(CloseFrame {
            code: CloseCode::Away,
            reason: "".into(),
        }))
        .await
        .unwrap();
        drop(ws);
        // A refused attempt: retried.
        refuse(
            &l,
            503,
            r#"{"code":"unavailable","error":"keys unavailable, retry later"}"#,
        )
        .await;
        let (mut ws, _) = accept(&l).await;
        let mut subs = vec![recv(&mut ws).await, recv(&mut ws).await];
        subs.sort_by_key(|v| v.to_string());
        // The orderbook subscription was replaced, the trades one removed.
        assert_eq!(
            subs,
            [
                json!({"op":"subscribe","channel":"orderbook","symbol":"ETH/USDT","depth":10}),
                json!({"op":"subscribe","channel":"overview"}),
            ]
        );
        send(&mut ws, f, &trades()).await;
        // Until the client goes away.
        let _ = timeout(WAIT, ws.next()).await;
    });

    let mut stream = client.stream(StreamOptions::default()).await.unwrap();
    stream.subscribe_trades("BTC/USDT", &[]).await.unwrap();
    stream
        .subscribe_orderbook("ETH/USDT", 5, &[])
        .await
        .unwrap();
    stream.subscribe_overview().await.unwrap();
    // While disconnected, the changes are recorded.
    tokio::time::sleep(Duration::from_millis(200)).await;
    stream
        .unsubscribe(Channel::Trades, "BTC/USDT")
        .await
        .unwrap();
    stream
        .subscribe_orderbook("ETH/USDT", 10, &[])
        .await
        .unwrap();
    let m = timeout(Duration::from_secs(10), stream.next())
        .await
        .unwrap();
    assert_eq!(m.unwrap().unwrap(), trades());
    drop(stream);
    server.await.unwrap();
}

#[tokio::test]
async fn unauthorized_at_connect() {
    let (l, client) = listen().await;
    tokio::spawn(async move {
        refuse(
            &l,
            401,
            r#"{"code":"unauthorized","error":"unknown API key"}"#,
        )
        .await;
    });
    let e = client.stream(StreamOptions::default()).await.unwrap_err();
    assert!(e.is_unauthorized(), "{e:?}");
    assert!(matches!(e, Error::Api { ref message, .. } if message == "unknown API key"));
}

#[tokio::test]
async fn too_many_connections_at_connect() {
    let (l, client) = listen().await;
    tokio::spawn(async move {
        refuse(
            &l,
            429,
            r#"{"code":"rate_limited","error":"too many WebSocket connections for this key"}"#,
        )
        .await;
    });
    let e = client.stream(StreamOptions::default()).await.unwrap_err();
    assert!(e.is_rate_limited(), "{e:?}");
}

#[tokio::test]
async fn unauthorized_on_reconnect_ends_the_stream() {
    let (l, client) = listen().await;
    tokio::spawn(async move {
        let (ws, _) = accept(&l).await;
        drop(ws);
        refuse(
            &l,
            401,
            r#"{"code":"unauthorized","error":"API key revoked"}"#,
        )
        .await;
    });
    let mut stream = client.stream(StreamOptions::default()).await.unwrap();
    let e = next(&mut stream).await.unwrap().unwrap_err();
    assert!(e.is_unauthorized(), "{e:?}");
    assert!(next(&mut stream).await.is_none());
    assert!(matches!(
        stream.subscribe_overview().await,
        Err(Error::StreamClosed)
    ));
}

#[tokio::test]
async fn without_reconnect_the_close_ends_the_stream() {
    let (l, client) = listen().await;
    tokio::spawn(async move {
        let (mut ws, _) = accept(&l).await;
        ws.close(Some(CloseFrame {
            code: CloseCode::from(4001),
            reason: "slow consumer".into(),
        }))
        .await
        .unwrap();
        let _ = ws.next().await;
    });
    let mut stream = client
        .stream(StreamOptions {
            reconnect: false,
            ..Default::default()
        })
        .await
        .unwrap();
    match next(&mut stream).await.unwrap() {
        Err(Error::Closed {
            code: Some(4001),
            reason,
        }) => assert_eq!(reason, "slow consumer"),
        other => panic!("{other:?}"),
    }
    assert!(next(&mut stream).await.is_none());
}

#[tokio::test]
async fn unknown_messages_are_skipped_corrupt_frames_reconnect() {
    let (l, client) = listen().await;
    let server = tokio::spawn(async move {
        let (mut ws, _) = accept(&l).await;
        recv(&mut ws).await;
        ws.send(WsMessage::text(r#"{"type":"from_the_future","x":1}"#))
            .await
            .unwrap();
        send(&mut ws, Format::Json, &Message::Pong).await;
        ws.send(WsMessage::text("{corrupt")).await.unwrap();
        // The client reconnects and subscribes again.
        let (mut ws, f) = accept(&l).await;
        assert_eq!(
            recv(&mut ws).await,
            json!({"op":"subscribe","channel":"overview"})
        );
        send(&mut ws, f, &trades()).await;
        // Until the client goes away.
        let _ = timeout(WAIT, ws.next()).await;
    });
    let mut stream = client.stream(StreamOptions::default()).await.unwrap();
    stream.subscribe_overview().await.unwrap();
    assert_eq!(next(&mut stream).await.unwrap().unwrap(), Message::Pong);
    assert_eq!(next(&mut stream).await.unwrap().unwrap(), trades());
    drop(stream);
    server.await.unwrap();
}

#[tokio::test]
async fn unknown_capnp_union_case_is_skipped() {
    // A ServerMessage whose union discriminant (7) is past `pong`: the root
    // pointer to a struct of one data word (the discriminant) and one
    // pointer (null).
    let mut words = Vec::new();
    for w in [(1u64 << 32) | (1 << 48), 7, 0] {
        words.extend_from_slice(&w.to_le_bytes());
    }
    let mut frame = Vec::new();
    frame.extend_from_slice(&0u32.to_le_bytes()); // one segment
    frame.extend_from_slice(&3u32.to_le_bytes()); // of 3 words
    frame.extend_from_slice(&words);
    let packed = capnp_unpacked_to_packed(&frame);
    assert!(matches!(
        Message::from_capnp(&packed),
        Err(Error::UnknownMessage(_))
    ));

    let (l, client) = listen().await;
    tokio::spawn(async move {
        let (mut ws, f) = accept(&l).await;
        ws.send(WsMessage::binary(packed)).await.unwrap();
        send(&mut ws, f, &Message::Pong).await;
        let _ = ws.next().await;
    });
    let mut stream = client
        .stream(StreamOptions {
            format: Format::Capnp,
            ..Default::default()
        })
        .await
        .unwrap();
    assert_eq!(next(&mut stream).await.unwrap().unwrap(), Message::Pong);
}

/// Packs an unpacked Cap'n Proto message, without compressing (each word is
/// tagged 0xff and followed by a run length of 0).
fn capnp_unpacked_to_packed(unpacked: &[u8]) -> Vec<u8> {
    let mut out = Vec::new();
    for word in unpacked.chunks(8) {
        out.push(0xff);
        out.extend_from_slice(word);
        out.push(0);
    }
    out
}

#[tokio::test]
async fn refused_subscriptions_are_not_sent_again() {
    let (l, client) = listen().await;
    let server = tokio::spawn(async move {
        let (mut ws, f) = accept(&l).await;
        recv(&mut ws).await;
        recv(&mut ws).await;
        let refused = Message::Error(klondike::StreamError {
            code: "too_many_subscriptions".into(),
            message: "more than 50 subscriptions".into(),
            channel: Some(Channel::Trades),
            symbol: "ETH/USDT".into(),
        });
        send(&mut ws, f, &refused).await;
        // Not about a subscription: nothing is dropped.
        let other = Message::Error(klondike::StreamError {
            code: "bad_request".into(),
            message: "invalid JSON".into(),
            channel: None,
            symbol: String::new(),
        });
        send(&mut ws, f, &other).await;
        drop(ws);
        let (mut ws, f) = accept(&l).await;
        assert_eq!(
            recv(&mut ws).await,
            json!({"op":"subscribe","channel":"trades","symbol":"BTC/USDT"})
        );
        send(&mut ws, f, &Message::Pong).await;
        let _ = timeout(WAIT, ws.next()).await;
    });
    let mut stream = client.stream(StreamOptions::default()).await.unwrap();
    stream.subscribe_trades("BTC/USDT", &[]).await.unwrap();
    stream.subscribe_trades("ETH/USDT", &[]).await.unwrap();
    assert!(
        matches!(next(&mut stream).await.unwrap().unwrap(), Message::Error(e) if e.code == "too_many_subscriptions")
    );
    assert!(
        matches!(next(&mut stream).await.unwrap().unwrap(), Message::Error(e) if e.code == "bad_request")
    );
    assert_eq!(next(&mut stream).await.unwrap().unwrap(), Message::Pong);
    drop(stream);
    server.await.unwrap();
}
