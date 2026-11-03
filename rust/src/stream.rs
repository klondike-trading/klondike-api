use std::collections::BTreeMap;
use std::future::Future;
use std::pin::Pin;
use std::task::{Context, Poll};
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use reqwest::header::{HeaderValue, RETRY_AFTER, USER_AGENT};
use tokio::net::TcpStream;
use tokio::sync::{mpsc, oneshot};
use tokio::time::{sleep, timeout};
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::protocol::WebSocketConfig;
use tokio_tungstenite::tungstenite::{self, Message as WsMessage};
use tokio_tungstenite::{MaybeTlsStream, WebSocketStream};

use crate::message::ClientMessage;
use crate::{Channel, Client, Error, Format, Message, Result, StreamError, Subscription};

type Ws = WebSocketStream<MaybeTlsStream<TcpStream>>;

const INITIAL_BACKOFF: Duration = Duration::from_millis(500);
const CONNECT_TIMEOUT: Duration = Duration::from_secs(30);
const WRITE_TIMEOUT: Duration = Duration::from_secs(10);
// The server pings every 20 s: a connection silent for longer is dead.
const IDLE_TIMEOUT: Duration = Duration::from_secs(60);
// The overview is a few hundred kB.
const MAX_MESSAGE_SIZE: usize = 64 << 20;
// Close code of a connection the server refuses to serve any longer.
const POLICY_VIOLATION: u16 = 1008;

/// Options of [`Client::stream`].
///
/// ```
/// let opts = klondike::StreamOptions { format: klondike::Format::Capnp, ..Default::default() };
/// ```
#[derive(Debug, Clone)]
pub struct StreamOptions {
    /// Encoding of the server messages, JSON by default. The messages are the
    /// same whatever the format.
    pub format: Format,
    /// Reopen a lost connection and send the subscriptions again (default
    /// `true`). Without it, the stream ends with the error that lost it.
    pub reconnect: bool,
    /// Longest wait between two reconnection attempts (default 30 s); the
    /// first wait is 0.5 s, doubled after each failed attempt.
    pub max_backoff: Duration,
    /// Messages buffered for the reader (default 1024). A reader slower than
    /// the stream for long is disconnected by the server (close code `4001`).
    pub buffer: usize,
}

impl Default for StreamOptions {
    fn default() -> Self {
        StreamOptions {
            format: Format::Json,
            reconnect: true,
            max_backoff: Duration::from_secs(30),
            buffer: 1024,
        }
    }
}

/// A WebSocket stream of the API, opened with [`Client::stream`].
///
/// [`next`](Stream::next) returns the server messages: data,
/// acknowledgements, errors of the server and pongs. A lost connection is
/// reopened in the background with an exponential backoff, and the
/// subscriptions are sent again; the messages sent while disconnected are
/// lost. The stream ends (`None`) after [`close`](Stream::close), or after a
/// terminal error: the key refused (401) when reconnecting, or a lost
/// connection with [`StreamOptions::reconnect`] off; the `Err` item before
/// `None` says why. A frame that cannot be decoded loses the connection;
/// messages of a type this version does not know are skipped.
///
/// `Stream` also implements [`futures_core::Stream`]. To subscribe from
/// another task while reading, use a [`StreamHandle`].
///
/// ```no_run
/// # async fn run() -> klondike::Result<()> {
/// use klondike::{Client, Format, Message, StreamOptions};
///
/// let client = Client::new(std::env::var("KLONDIKE_API_KEY").unwrap());
/// let mut stream = client
///     .stream(StreamOptions { format: Format::Capnp, ..Default::default() })
///     .await?;
/// stream.subscribe_trades("BTC/USDT", &[]).await?;
/// while let Some(msg) = stream.next().await {
///     if let Message::Trades(t) = msg? {
///         for trade in t.trades {
///             println!("{} {} {:?} {} @ {}", t.symbol, trade.venue, trade.side, trade.qty, trade.price);
///         }
///     }
/// }
/// # Ok(()) }
/// ```
#[derive(Debug)]
pub struct Stream {
    handle: StreamHandle,
    rx: mpsc::Receiver<Result<Message>>,
}

/// Sends the subscriptions of a [`Stream`] from any task. Cheap to clone.
#[derive(Debug, Clone)]
pub struct StreamHandle {
    cmds: mpsc::UnboundedSender<Command>,
}

#[derive(Debug)]
enum Command {
    Send {
        msg: ClientMessage,
        reply: oneshot::Sender<Result<()>>,
    },
    Close {
        reply: oneshot::Sender<()>,
    },
}

impl Client {
    /// Opens a WebSocket stream. The first connection is opened before it
    /// returns: a refused key ([`Error::is_unauthorized`]), too many
    /// connections for the key (429) or an unreachable server are errors
    /// here. Must be called within a Tokio runtime.
    pub async fn stream(&self, opts: StreamOptions) -> Result<Stream> {
        let mut url = self.url("/v1/ws");
        let scheme = if url.scheme() == "https" { "wss" } else { "ws" };
        url.set_scheme(scheme)
            .map_err(|_| Error::Config(format!("cannot derive a WebSocket URL from {url}")))?;
        url.query_pairs_mut()
            .append_pair("format", opts.format.as_str());
        let dialer = Dialer {
            url: url.to_string(),
            api_key: self.api_key_header()?,
            user_agent: HeaderValue::from_str(&self.inner.user_agent)
                .map_err(|_| Error::Config("invalid user agent".into()))?,
        };
        let ws = dialer.dial().await?;
        let (cmd_tx, cmd_rx) = mpsc::unbounded_channel();
        let (out_tx, out_rx) = mpsc::channel(opts.buffer.max(1));
        let worker = Worker {
            dialer,
            opts,
            cmds: cmd_rx,
            out: out_tx,
            subs: BTreeMap::new(),
        };
        tokio::spawn(worker.run(ws));
        Ok(Stream {
            handle: StreamHandle { cmds: cmd_tx },
            rx: out_rx,
        })
    }
}

impl Stream {
    /// The next server message; `None` once the stream has ended.
    pub async fn next(&mut self) -> Option<Result<Message>> {
        self.rx.recv().await
    }

    /// A handle to subscribe from other tasks while this one reads.
    pub fn handle(&self) -> StreamHandle {
        self.handle.clone()
    }

    /// See [`StreamHandle::subscribe_trades`].
    pub async fn subscribe_trades(&self, symbol: &str, venues: &[&str]) -> Result<()> {
        self.handle.subscribe_trades(symbol, venues).await
    }

    /// See [`StreamHandle::subscribe_orderbook`].
    pub async fn subscribe_orderbook(
        &self,
        symbol: &str,
        depth: u32,
        venues: &[&str],
    ) -> Result<()> {
        self.handle.subscribe_orderbook(symbol, depth, venues).await
    }

    /// See [`StreamHandle::subscribe_overview`].
    pub async fn subscribe_overview(&self) -> Result<()> {
        self.handle.subscribe_overview().await
    }

    /// See [`StreamHandle::unsubscribe`].
    pub async fn unsubscribe(&self, channel: Channel, symbol: &str) -> Result<()> {
        self.handle.unsubscribe(channel, symbol).await
    }

    /// See [`StreamHandle::ping`].
    pub async fn ping(&self) -> Result<()> {
        self.handle.ping().await
    }

    /// See [`StreamHandle::close`].
    pub async fn close(&self) {
        self.handle.close().await
    }
}

impl futures_core::Stream for Stream {
    type Item = Result<Message>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        self.rx.poll_recv(cx)
    }
}

impl StreamHandle {
    /// Streams the trades of the symbol, of the venues given (all when
    /// empty). Subscribing again to a symbol replaces its venues.
    pub async fn subscribe_trades(&self, symbol: &str, venues: &[&str]) -> Result<()> {
        self.send(ClientMessage::subscribe(
            Channel::Trades,
            symbol,
            None,
            venues,
        ))
        .await
    }

    /// Streams the merged order book of the symbol, `depth` levels per side
    /// (1 to 200; 0 for the server default, 20), with the levels of the
    /// venues given (all when empty). Subscribing again to a symbol replaces
    /// its depth and venues.
    pub async fn subscribe_orderbook(
        &self,
        symbol: &str,
        depth: u32,
        venues: &[&str],
    ) -> Result<()> {
        let depth = (depth > 0).then_some(depth);
        self.send(ClientMessage::subscribe(
            Channel::Orderbook,
            symbol,
            depth,
            venues,
        ))
        .await
    }

    /// Streams the market overview.
    pub async fn subscribe_overview(&self) -> Result<()> {
        self.send(ClientMessage::subscribe(Channel::Overview, "", None, &[]))
            .await
    }

    /// Stops a subscription (`symbol` is empty for the overview).
    pub async fn unsubscribe(&self, channel: Channel, symbol: &str) -> Result<()> {
        self.send(ClientMessage::unsubscribe(channel, symbol)).await
    }

    /// Asks the server for a [`Message::Pong`].
    pub async fn ping(&self) -> Result<()> {
        self.send(ClientMessage::ping()).await
    }

    /// Closes the connection and ends the stream: [`Stream::next`] returns
    /// the messages already received, then `None`.
    pub async fn close(&self) {
        let (reply, done) = oneshot::channel();
        if self.cmds.send(Command::Close { reply }).is_ok() {
            let _ = done.await;
        }
    }

    /// Sends the message on the current connection. While reconnecting, the
    /// subscriptions are recorded and sent once connected.
    async fn send(&self, msg: ClientMessage) -> Result<()> {
        let (reply, done) = oneshot::channel();
        self.cmds
            .send(Command::Send { msg, reply })
            .map_err(|_| Error::StreamClosed)?;
        done.await.map_err(|_| Error::StreamClosed)?
    }
}

#[derive(Clone)]
struct Dialer {
    url: String,
    api_key: HeaderValue,
    user_agent: HeaderValue,
}

impl Dialer {
    async fn dial(&self) -> Result<Ws> {
        let mut req = self.url.as_str().into_client_request()?;
        req.headers_mut().insert("X-API-Key", self.api_key.clone());
        req.headers_mut()
            .insert(USER_AGENT, self.user_agent.clone());
        let config = WebSocketConfig::default()
            .max_message_size(Some(MAX_MESSAGE_SIZE))
            .max_frame_size(Some(MAX_MESSAGE_SIZE));
        let connect = tokio_tungstenite::connect_async_with_config(req, Some(config), true);
        match timeout(CONNECT_TIMEOUT, connect).await {
            Ok(Ok((ws, _))) => Ok(ws),
            Ok(Err(tungstenite::Error::Http(resp))) => {
                let retry_after = resp
                    .headers()
                    .get(RETRY_AFTER)
                    .and_then(|v| v.to_str().ok());
                Err(Error::from_response(
                    resp.status().as_u16(),
                    retry_after,
                    resp.body().as_deref().unwrap_or_default(),
                ))
            }
            Ok(Err(e)) => Err(e.into()),
            Err(_) => Err(tungstenite::Error::Io(std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                "WebSocket connection timed out",
            ))
            .into()),
        }
    }
}

/// Why a connection ended.
enum End {
    /// Closed by the user, or the stream was dropped.
    Shutdown,
    /// Lost: reconnect, or end the stream with the error.
    Lost(Error),
}

/// The background task owning the connection.
struct Worker {
    dialer: Dialer,
    opts: StreamOptions,
    cmds: mpsc::UnboundedReceiver<Command>,
    out: mpsc::Sender<Result<Message>>,
    subs: BTreeMap<Subscription, ClientMessage>,
}

impl Worker {
    async fn run(mut self, mut ws: Ws) {
        let mut backoff = INITIAL_BACKOFF;
        loop {
            let err = match self.serve(&mut ws).await {
                End::Shutdown => return,
                End::Lost(err) => err,
            };
            let refused = matches!(
                &err,
                Error::Closed {
                    code: Some(POLICY_VIOLATION),
                    ..
                }
            );
            if !self.opts.reconnect || refused {
                let _ = self.out.send(Err(err)).await;
                return;
            }
            ws = loop {
                if self.offline(sleep(backoff)).await.is_none() {
                    return;
                }
                backoff = (backoff * 2).min(self.opts.max_backoff.max(INITIAL_BACKOFF));
                let dialer = self.dialer.clone();
                let result = match self.offline(async move { dialer.dial().await }).await {
                    None => return,
                    Some(r) => r,
                };
                match result {
                    Ok(ws) => break ws,
                    Err(e) if e.is_unauthorized() => {
                        let _ = self.out.send(Err(e)).await;
                        return;
                    }
                    Err(e) => {
                        if let Some(wait) = e.retry_after() {
                            backoff = backoff.max(wait);
                        }
                    }
                }
            };
            if self.resubscribe(&mut ws).await.is_ok() {
                backoff = INITIAL_BACKOFF;
            }
        }
    }

    /// Reads the connection and executes the commands until it ends.
    async fn serve(&mut self, ws: &mut Ws) -> End {
        loop {
            let frame = tokio::select! {
                frame = timeout(IDLE_TIMEOUT, ws.next()) => frame,
                Some(cmd) = self.cmds.recv() => {
                    if let Some(end) = self.command(ws, cmd).await {
                        return end;
                    }
                    continue;
                }
                _ = self.out.closed() => {
                    let _ = timeout(WRITE_TIMEOUT, ws.close(None)).await;
                    return End::Shutdown;
                }
            };
            let item = match frame {
                Ok(Some(Ok(WsMessage::Text(text)))) => Message::from_json(text.as_bytes()),
                Ok(Some(Ok(WsMessage::Binary(data)))) => Message::from_capnp(&data),
                Ok(Some(Ok(WsMessage::Close(frame)))) => {
                    return End::Lost(Error::Closed {
                        code: frame.as_ref().map(|f| u16::from(f.code)),
                        reason: frame.map(|f| f.reason.to_string()).unwrap_or_default(),
                    })
                }
                Ok(Some(Ok(_))) => continue, // ping, pong: answered by tungstenite
                Ok(Some(Err(e))) => return End::Lost(e.into()),
                Ok(None) => {
                    return End::Lost(Error::Closed {
                        code: None,
                        reason: String::new(),
                    })
                }
                Err(_) => {
                    return End::Lost(Error::Closed {
                        code: None,
                        reason: "no data from the server".into(),
                    })
                }
            };
            let msg = match item {
                Ok(msg) => msg,
                // A newer server may add types.
                Err(Error::UnknownMessage(_)) => continue,
                // A corrupt frame: the connection is not trusted any longer.
                Err(e) => return End::Lost(e),
            };
            if let Message::Error(e) = &msg {
                self.refused(e);
            }
            if let Some(end) = self.deliver(ws, Ok(msg)).await {
                return end;
            }
        }
    }

    /// Hands a message to the reader, executing the commands while its
    /// buffer is full.
    async fn deliver(&mut self, ws: &mut Ws, item: Result<Message>) -> Option<End> {
        match self.out.try_reserve() {
            Ok(permit) => {
                permit.send(item);
                return None;
            }
            Err(mpsc::error::TrySendError::Closed(())) => return Some(End::Shutdown),
            Err(mpsc::error::TrySendError::Full(())) => {}
        }
        let out = self.out.clone();
        loop {
            tokio::select! {
                permit = out.reserve() => {
                    return match permit {
                        Ok(permit) => {
                            permit.send(item);
                            None
                        }
                        Err(_) => Some(End::Shutdown),
                    };
                }
                Some(cmd) = self.cmds.recv() => {
                    if let Some(end) = self.command(ws, cmd).await {
                        return Some(end);
                    }
                }
            }
        }
    }

    /// Executes a command on the connection.
    async fn command(&mut self, ws: &mut Ws, cmd: Command) -> Option<End> {
        match cmd {
            Command::Send { msg, reply } => {
                self.record(&msg);
                match write(ws, &msg).await {
                    Ok(()) => {
                        let _ = reply.send(Ok(()));
                        None
                    }
                    Err(e) if self.opts.reconnect => {
                        // Sent again once reconnected.
                        let _ = reply.send(Ok(()));
                        Some(End::Lost(e))
                    }
                    Err(e) => {
                        let _ = reply.send(Err(Error::Closed {
                            code: None,
                            reason: e.to_string(),
                        }));
                        Some(End::Lost(e))
                    }
                }
            }
            Command::Close { reply } => {
                if timeout(WRITE_TIMEOUT, ws.close(None)).await.is_ok() {
                    // Wait for the close frame of the server.
                    let _ = timeout(Duration::from_secs(1), async {
                        while let Some(Ok(_)) = ws.next().await {}
                    })
                    .await;
                }
                let _ = reply.send(());
                Some(End::Shutdown)
            }
        }
    }

    /// Runs `fut` while disconnected, recording the subscriptions sent
    /// meanwhile; `None` when the stream is closed or dropped.
    async fn offline<F: Future>(&mut self, fut: F) -> Option<F::Output> {
        tokio::pin!(fut);
        loop {
            tokio::select! {
                out = &mut fut => return Some(out),
                Some(cmd) = self.cmds.recv() => match cmd {
                    Command::Send { msg, reply } => {
                        self.record(&msg);
                        let _ = reply.send(Ok(()));
                    }
                    Command::Close { reply } => {
                        let _ = reply.send(());
                        return None;
                    }
                },
                _ = self.out.closed() => return None,
            }
        }
    }

    fn record(&mut self, msg: &ClientMessage) {
        match msg.op {
            "subscribe" => {
                self.subs.insert(msg.subscription(), msg.clone());
            }
            "unsubscribe" => {
                self.subs.remove(&msg.subscription());
            }
            _ => {}
        }
    }

    /// Forgets a subscription the server refused, so that it is not sent
    /// again after a reconnection.
    fn refused(&mut self, e: &StreamError) {
        if let Some(channel) = &e.channel {
            if matches!(
                e.code.as_str(),
                "bad_request" | "unknown_channel" | "too_many_subscriptions"
            ) {
                self.subs.remove(&Subscription {
                    channel: channel.clone(),
                    symbol: e.symbol.clone(),
                });
            }
        }
    }

    async fn resubscribe(&mut self, ws: &mut Ws) -> Result<()> {
        for msg in self.subs.values() {
            write(ws, msg).await?;
        }
        Ok(())
    }
}

async fn write(ws: &mut Ws, msg: &ClientMessage) -> Result<()> {
    let text = serde_json::to_string(msg)?;
    match timeout(WRITE_TIMEOUT, ws.send(WsMessage::text(text))).await {
        Ok(r) => Ok(r?),
        Err(_) => Err(tungstenite::Error::Io(std::io::Error::new(
            std::io::ErrorKind::TimedOut,
            "WebSocket write timed out",
        ))
        .into()),
    }
}
