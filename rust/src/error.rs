use std::fmt;
use std::time::Duration;

use tokio_tungstenite::tungstenite;

/// Result of the operations of this crate.
pub type Result<T, E = Error> = std::result::Result<T, E>;

/// Errors of the client.
#[derive(Debug)]
#[non_exhaustive]
pub enum Error {
    /// An error answer of the API: an HTTP status other than 200, or a
    /// WebSocket upgrade refused (401 for a missing or revoked key, 429 when
    /// the key has too many connections).
    Api {
        /// HTTP status.
        status: u16,
        /// `bad_request`, `unauthorized`, `not_found`, `rate_limited`,
        /// `unavailable`... (the status text when the body is not an API error).
        code: String,
        /// Human readable message.
        message: String,
        /// The `Retry-After` header, set on 429.
        retry_after: Option<Duration>,
    },
    /// The HTTP request failed (connection, TLS, timeout...).
    Http(reqwest::Error),
    /// The WebSocket failed (connection, TLS, protocol...).
    WebSocket(Box<tungstenite::Error>),
    /// The server closed the WebSocket, and the stream does not reconnect
    /// (code `4001` is a slow consumer, `1001` a server restart).
    Closed {
        /// Close code, `None` when the connection ended without a close frame.
        code: Option<u16>,
        /// Close reason.
        reason: String,
    },
    /// A JSON document could not be decoded.
    Json(serde_json::Error),
    /// A Cap'n Proto frame could not be decoded, or a message encoded.
    Capnp(capnp::Error),
    /// A server message of a type this version does not know (JSON `type`
    /// or Cap'n Proto union case): a newer server may add types. Streams
    /// skip these messages; only [`Message::from_json`](crate::Message::from_json)
    /// and [`Message::from_capnp`](crate::Message::from_capnp) return it.
    UnknownMessage(String),
    /// Invalid configuration: base URL, missing API key...
    Config(String),
    /// The stream has been closed with [`Stream::close`](crate::Stream::close)
    /// or has ended.
    StreamClosed,
}

impl Error {
    /// HTTP status of an [`Error::Api`].
    pub fn status(&self) -> Option<u16> {
        match self {
            Error::Api { status, .. } => Some(*status),
            Error::Http(e) => e.status().map(|s| s.as_u16()),
            _ => None,
        }
    }

    /// Whether the API refused the key (401).
    pub fn is_unauthorized(&self) -> bool {
        self.status() == Some(401)
    }

    /// Whether the API has no data for the request (404).
    pub fn is_not_found(&self) -> bool {
        self.status() == Some(404)
    }

    /// Whether the rate limit of the key is exceeded (429).
    pub fn is_rate_limited(&self) -> bool {
        self.status() == Some(429)
    }

    /// How long to wait before retrying, on a 429.
    pub fn retry_after(&self) -> Option<Duration> {
        match self {
            Error::Api { retry_after, .. } => *retry_after,
            _ => None,
        }
    }

    /// Builds the error of an answer of the API: the body is `{"code", "error"}`
    /// when the API wrote it, anything else otherwise (a proxy, for instance).
    pub(crate) fn from_response(status: u16, retry_after: Option<&str>, body: &[u8]) -> Error {
        #[derive(serde::Deserialize)]
        struct Payload {
            #[serde(default)]
            code: String,
            #[serde(default)]
            error: String,
        }
        let (code, message) = match serde_json::from_slice::<Payload>(body) {
            Ok(p) if !p.error.is_empty() => (p.code, p.error),
            _ => (
                reqwest::StatusCode::from_u16(status)
                    .ok()
                    .and_then(|s| s.canonical_reason())
                    .unwrap_or("")
                    .to_string(),
                String::from_utf8_lossy(&body[..body.len().min(64 << 10)])
                    .trim()
                    .to_string(),
            ),
        };
        Error::Api {
            status,
            code,
            message,
            retry_after: retry_after
                .and_then(|s| s.trim().parse::<u64>().ok())
                .map(Duration::from_secs),
        }
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Api {
                status,
                code,
                message,
                ..
            } => write!(f, "klondike: {status} {code}: {message}"),
            Error::Http(e) => write!(f, "klondike: http: {e}"),
            Error::WebSocket(e) => write!(f, "klondike: websocket: {e}"),
            Error::Closed {
                code: Some(code),
                reason,
            } => write!(
                f,
                "klondike: connection closed by the server ({code} {reason})"
            ),
            Error::Closed { code: None, .. } => write!(f, "klondike: connection lost"),
            Error::Json(e) => write!(f, "klondike: json: {e}"),
            Error::Capnp(e) => write!(f, "klondike: capnp: {e}"),
            Error::UnknownMessage(t) => write!(f, "klondike: unknown message type {t:?}"),
            Error::Config(e) => write!(f, "klondike: {e}"),
            Error::StreamClosed => write!(f, "klondike: stream closed"),
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Http(e) => Some(e),
            Error::WebSocket(e) => Some(e.as_ref()),
            Error::Json(e) => Some(e),
            Error::Capnp(e) => Some(e),
            _ => None,
        }
    }
}

impl From<reqwest::Error> for Error {
    fn from(e: reqwest::Error) -> Self {
        Error::Http(e)
    }
}

impl From<tungstenite::Error> for Error {
    fn from(e: tungstenite::Error) -> Self {
        Error::WebSocket(Box::new(e))
    }
}

impl From<serde_json::Error> for Error {
    fn from(e: serde_json::Error) -> Self {
        Error::Json(e)
    }
}

impl From<capnp::Error> for Error {
    fn from(e: capnp::Error) -> Self {
        Error::Capnp(e)
    }
}

impl From<capnp::NotInSchema> for Error {
    fn from(e: capnp::NotInSchema) -> Self {
        Error::Capnp(e.into())
    }
}
