//! The fixtures of `../testdata/messages` are the same server messages encoded
//! by the Go library in both formats: `<n>-<type>.json` (`format=json`) and
//! `<n>-<type>.capnp` (`format=capnp`). Both must decode to the same message.

use std::fs;
use std::path::{Path, PathBuf};

use klondike::{Channel, Message, Side, StreamError, Subscription};

fn fixtures_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../testdata/messages")
}

fn kind(m: &Message) -> &'static str {
    match m {
        Message::Trades(_) => "trades",
        Message::Orderbook(_) => "orderbook",
        Message::Overview(_) => "overview",
        Message::Subscribed(_) => "subscribed",
        Message::Unsubscribed(_) => "unsubscribed",
        Message::Error(_) => "error",
        Message::Pong => "pong",
    }
}

/// The fixtures, decoded: (file stem, from JSON, from Cap'n Proto).
fn decoded() -> Vec<(String, Message, Message)> {
    let mut stems: Vec<String> = fs::read_dir(fixtures_dir())
        .expect("../testdata/messages")
        .filter_map(|e| {
            let p = e.unwrap().path();
            (p.extension()? == "json")
                .then(|| p.file_stem().unwrap().to_string_lossy().into_owned())
        })
        .collect();
    stems.sort();
    assert!(stems.len() >= 7, "fixtures missing: {stems:?}");
    stems
        .into_iter()
        .map(|stem| {
            let dir = fixtures_dir();
            let js = fs::read(dir.join(format!("{stem}.json"))).unwrap();
            let cp = fs::read(dir.join(format!("{stem}.capnp")))
                .unwrap_or_else(|e| panic!("{stem}.capnp: {e}"));
            let from_json = Message::from_json(&js).unwrap_or_else(|e| panic!("{stem}.json: {e}"));
            let from_capnp =
                Message::from_capnp(&cp).unwrap_or_else(|e| panic!("{stem}.capnp: {e}"));
            (stem, from_json, from_capnp)
        })
        .collect()
}

#[test]
fn both_encodings_decode_to_the_same_message() {
    for (stem, from_json, from_capnp) in decoded() {
        assert_eq!(from_json, from_capnp, "{stem}: json and capnp differ");
        let want = stem.split_once('-').unwrap().1;
        assert_eq!(kind(&from_json), want, "{stem}");
    }
}

#[test]
fn round_trips() {
    for (stem, m, _) in decoded() {
        let js = m.to_json().unwrap();
        assert_eq!(
            Message::from_json(&js).unwrap(),
            m,
            "{stem}: json round trip"
        );
        let cp = m.to_capnp().unwrap();
        assert_eq!(
            Message::from_capnp(&cp).unwrap(),
            m,
            "{stem}: capnp round trip"
        );
    }
}

#[test]
fn fixture_values() {
    let all = decoded();
    let get = |t: &str| {
        all.iter()
            .find(|(s, _, _)| s.ends_with(t))
            .unwrap()
            .1
            .clone()
    };

    let Message::Trades(t) = get("-trades") else {
        panic!()
    };
    assert_eq!(t.symbol, "BTC/USDT");
    assert_eq!(t.trades.len(), 3);
    assert_eq!(
        (t.trades[0].ts, t.trades[0].venue.as_str()),
        (1793706479654, "binance")
    );
    assert_eq!((t.trades[0].price, t.trades[0].qty), (104250.1, 0.0125));
    let sides: Vec<Side> = t.trades.iter().map(|t| t.side).collect();
    assert_eq!(sides, [Side::Buy, Side::Sell, Side::Unknown]);

    // Missing numbers: null in JSON, NaN / 0 in Cap'n Proto, None here.
    let Message::Orderbook(b) = get("-orderbook") else {
        panic!()
    };
    assert_eq!(b.venues.len(), 3);
    assert_eq!(b.venues[0].ts, Some(1793706478950));
    assert_eq!(b.venues[0].ask, Some(104250.1));
    assert_eq!(
        (b.venues[1].bid, b.venues[1].ask, b.venues[1].ask_qty),
        (Some(104249.0), None, None)
    );
    assert_eq!(
        (b.venues[2].venue.as_str(), b.venues[2].ts, b.venues[2].bid),
        ("gemini", None, None)
    );
    // Level venues: an index into the venues in Cap'n Proto, a name here.
    let venues: Vec<&str> = b.bids.iter().map(|l| l.venue.as_str()).collect();
    assert_eq!(venues, ["binance", "kraken"]);
    assert_eq!(b.asks[0].venue, "binance");

    let Message::Overview(o) = get("-overview") else {
        panic!()
    };
    assert_eq!(o.ts, 1);
    assert_eq!(o.pairs[0].symbol, "BTC/USDT");
    assert_eq!(o.pairs[0].basis_bps, Some(1.5));
    assert_eq!(o.pairs[0].max_net_bps1h, None);
    assert_eq!(o.pairs[0].impact[0].notional_usd, 100000.0);
    assert_eq!(o.pairs[0].impact[0].buy_all_in_bps, Some(4.2));
    assert_eq!(o.pairs[0].impact[0].sell_all_in_bps, None);
    assert_eq!(o.leaders[0].exchange, "binance");

    assert_eq!(
        get("-subscribed"),
        Message::Subscribed(Subscription {
            channel: Channel::Trades,
            symbol: "BTC/USDT".into()
        })
    );
    assert_eq!(
        get("-unsubscribed"),
        Message::Unsubscribed(Subscription {
            channel: Channel::Overview,
            symbol: String::new()
        })
    );
    assert_eq!(
        get("-error"),
        Message::Error(StreamError {
            code: "bad_request".into(),
            message: "symbol is required".into(),
            channel: Some(Channel::Trades),
            symbol: String::new(),
        })
    );
    assert_eq!(get("-pong"), Message::Pong);
}

#[test]
fn json_wire_format_matches_go() {
    // Same keys and nesting as the Go encoder (numbers may be written
    // differently: 104250.0 against 104250).
    for (stem, m, _) in decoded() {
        let go: serde_json::Value =
            serde_json::from_slice(&fs::read(fixtures_dir().join(format!("{stem}.json"))).unwrap())
                .unwrap();
        let rust: serde_json::Value = serde_json::from_slice(&m.to_json().unwrap()).unwrap();
        assert_eq!(normalize(go), normalize(rust), "{stem}");
    }
}

fn normalize(v: serde_json::Value) -> serde_json::Value {
    use serde_json::Value;
    match v {
        Value::Number(n) => Value::from(n.as_f64().unwrap()),
        Value::Array(a) => Value::Array(a.into_iter().map(normalize).collect()),
        Value::Object(o) => Value::Object(o.into_iter().map(|(k, v)| (k, normalize(v))).collect()),
        v => v,
    }
}

#[test]
fn unknown_message_type_is_an_error() {
    assert!(Message::from_json(br#"{"type":"bogus"}"#).is_err());
    assert!(Message::from_capnp(b"\x00\x01garbage").is_err());
}

#[test]
fn level_venue_index_out_of_range_is_an_error() {
    let cp = fs::read(fixtures_dir().join("1-orderbook.capnp")).unwrap();
    let Message::Orderbook(mut b) = Message::from_capnp(&cp).unwrap() else {
        panic!()
    };
    b.bids[0].venue = "unknown".into();
    assert!(Message::Orderbook(b).to_capnp().is_err());
}

/// The generated code (src/klondike_api_capnp.rs) comes from
/// schema/klondike_api.capnp: it must be the schema of the repository, or
/// gen.sh must be run again.
#[test]
fn generated_code_matches_schema() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let upstream = root.join("../schema/klondike_api.capnp");
    let Ok(upstream) = fs::read(&upstream) else {
        eprintln!("skipped: {} not found (packaged crate)", upstream.display());
        return;
    };
    let copy = fs::read(root.join("schema/klondike_api.capnp")).unwrap();
    assert!(
        copy == upstream,
        "schema/klondike_api.capnp differs from ../schema/klondike_api.capnp: run ./gen.sh"
    );
    let generated = fs::read_to_string(root.join("src/klondike_api_capnp.rs")).unwrap();
    assert!(generated.contains("// source: klondike_api.capnp"));
}
