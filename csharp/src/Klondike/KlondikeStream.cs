using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Klondike;

/// <summary>The encoding of the server messages of a stream. The messages are the same whatever the format.</summary>
public enum StreamFormat
{
    /// <summary>Text frames, one JSON object per frame.</summary>
    Json,
    /// <summary>Binary frames, one packed Cap'n Proto message per frame: about three times smaller.</summary>
    Capnp,
}

/// <summary>Options of a <see cref="KlondikeStream"/>.</summary>
public sealed record StreamOptions
{
    /// <summary>Encoding of the server messages.</summary>
    public StreamFormat Format { get; init; } = StreamFormat.Json;
    /// <summary>A lost connection is reopened and the subscriptions sent again.</summary>
    public bool Reconnect { get; init; } = true;
    /// <summary>Longest wait between two reconnections.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Messages buffered for the reader. The server drops a client that does not read fast enough.</summary>
    public int Buffer { get; init; } = 1024;
    /// <summary>A connection silent for this long is pinged.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>A connection that does not answer a ping within this time is closed: it died silently.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A WebSocket connection to the API that reconnects on its own and subscribes again after a
/// reconnection. Read the messages with <see cref="ReadAllAsync"/>.
/// </summary>
public sealed class KlondikeStream : IAsyncDisposable
{
    private const int MaxMessageBytes = 64 << 20; // the overview is a few hundred kB
    private const int WebSocketPolicyViolation = 1008;
    private static readonly TimeSpan MinBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly Uri _uri;
    private readonly string _apiKey;
    private readonly StreamOptions _options;
    private readonly Channel<StreamMessage> _messages;
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<(string Channel, string Symbol), ClientMessage> _subscriptions = [];
    private ClientWebSocket? _socket;
    private Exception? _error;
    private Task _run = Task.CompletedTask;
    private int _disposed;
    private int _keepAlivePings; // unanswered pings of the keep-alive: their pongs are not the reader's
    private long _lastReceived;

    private KlondikeStream(Uri uri, string apiKey, StreamOptions options)
    {
        _uri = uri;
        _apiKey = apiKey;
        _options = options;
        _messages = Channel.CreateBounded<StreamMessage>(new BoundedChannelOptions(Math.Max(options.Buffer, 1))
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    internal static async Task<KlondikeStream> OpenAsync(string baseUrl, string apiKey, StreamOptions options, CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(baseUrl + "/v1/ws");
        builder.Scheme = builder.Scheme switch { "https" => "wss", "http" => "ws", var s => s };
        builder.Query = options.Format == StreamFormat.Capnp ? "format=capnp" : "format=json";
        var stream = new KlondikeStream(builder.Uri, apiKey, options);
        var socket = await stream.DialAsync(cancellationToken).ConfigureAwait(false);
        stream.SetSocket(socket);
        stream._run = Task.Run(() => stream.RunAsync(socket));
        return stream;
    }

    /// <summary>The messages: data, acknowledgements and errors. Ends when the stream ends; <see cref="Error"/> then says why.</summary>
    public IAsyncEnumerable<StreamMessage> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _messages.Reader.ReadAllAsync(cancellationToken);

    /// <summary>The reason the stream ended on its own (a refused key, for instance), null otherwise.</summary>
    public Exception? Error
    {
        get
        {
            lock (_lock)
            {
                return _error;
            }
        }
    }

    /// <summary>Whether a connection is open, false while reconnecting.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _socket is not null;
            }
        }
    }

    /// <summary>Raised when the connection is lost or back; called on the thread of the stream, handlers must not block.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// Streams the trades of the symbol, of the venues given (all when none). A connection has one
    /// subscription per channel and symbol: subscribing again replaces its venues.
    /// </summary>
    public Task SubscribeTradesAsync(string symbol, IReadOnlyList<string>? venues = null, CancellationToken cancellationToken = default) =>
        SubscribeAsync(new ClientMessage { Op = "subscribe", Channel = Channels.Trades, Symbol = symbol, Venues = NullIfEmpty(venues) }, cancellationToken);

    /// <summary>
    /// Streams the merged book of the symbol, <paramref name="depth"/> levels per side (1 to 200, 20 when
    /// null), of the venues given (all when none). Subscribing again replaces its depth and venues.
    /// </summary>
    public Task SubscribeOrderbookAsync(string symbol, int? depth = null, IReadOnlyList<string>? venues = null, CancellationToken cancellationToken = default) =>
        SubscribeAsync(new ClientMessage { Op = "subscribe", Channel = Channels.Orderbook, Symbol = symbol, Depth = depth, Venues = NullIfEmpty(venues) }, cancellationToken);

    /// <summary>Streams the market overview.</summary>
    public Task SubscribeOverviewAsync(CancellationToken cancellationToken = default) =>
        SubscribeAsync(new ClientMessage { Op = "subscribe", Channel = Channels.Overview }, cancellationToken);

    /// <summary>Stops a stream; <paramref name="symbol"/> is null for the overview.</summary>
    public Task UnsubscribeAsync(string channel, string? symbol = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _subscriptions.Remove((channel, symbol ?? ""));
        }
        return SendAsync(new ClientMessage { Op = "unsubscribe", Channel = channel, Symbol = symbol }, cancellationToken);
    }

    /// <summary>Asks the server for a <see cref="PongMessage"/>.</summary>
    public Task PingAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new ClientMessage { Op = "ping" }, cancellationToken);

    /// <summary>Closes the connection and ends the stream.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        ClientWebSocket? socket;
        lock (_lock)
        {
            socket = _socket;
        }
        if (socket is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // Closing anyway.
            }
        }
        await _closing.CancelAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false);
        _closing.Dispose();
        _sendLock.Dispose();
    }

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string>? venues) => venues is { Count: > 0 } ? venues : null;

    private Task SubscribeAsync(ClientMessage message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _subscriptions[(message.Channel!, message.Symbol ?? "")] = message;
        }
        return SendAsync(message, cancellationToken);
    }

    // Writes on the current connection. While reconnecting it is a no-op: the subscriptions are sent
    // again once connected.
    private async Task SendAsync(ClientMessage message, CancellationToken cancellationToken)
    {
        ClientWebSocket? socket;
        Exception? ended;
        lock (_lock)
        {
            socket = _socket;
            ended = _error;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(KlondikeStream));
        }
        if (ended is not null)
        {
            throw new InvalidOperationException("klondike: the stream has ended", ended);
        }
        if (socket is not null)
        {
            try
            {
                await WriteAsync(socket, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (_options.Reconnect && e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection is going away: the subscriptions follow on the next one.
            }
        }
    }

    private async Task WriteAsync(ClientWebSocket socket, ClientMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, KlondikeJson.Default.ClientMessage);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<ClientWebSocket> DialAsync(CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-API-Key", _apiKey);
        socket.Options.SetRequestHeader("User-Agent", KlondikeClient.UserAgent);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // see KeepAliveAsync
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DialTimeout);
        try
        {
            await socket.ConnectAsync(_uri, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (Exception e)
        {
            var status = socket.HttpStatusCode;
            var retryAfter = socket.HttpResponseHeaders?.TryGetValue("Retry-After", out var values) == true
                && int.TryParse(values.FirstOrDefault(), out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : (TimeSpan?)null;
            socket.Dispose();
            if (status is 0 or HttpStatusCode.SwitchingProtocols)
            {
                if (e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"klondike: no answer from {_uri.Host} in {DialTimeout.TotalSeconds} s", e);
                }
                throw;
            }
            var code = status switch
            {
                HttpStatusCode.Unauthorized => "unauthorized",
                HttpStatusCode.TooManyRequests => "rate_limited",
                HttpStatusCode.BadRequest => "bad_request",
                _ => status.ToString(),
            };
            throw new KlondikeApiException((int)status, code, status == HttpStatusCode.Unauthorized ? "API key refused" : e.Message, retryAfter);
        }
    }

    private void SetSocket(ClientWebSocket? socket)
    {
        bool changed;
        lock (_lock)
        {
            changed = (_socket is null) != (socket is null);
            _socket = socket;
        }
        if (changed)
        {
            ConnectionChanged?.Invoke(socket is not null);
        }
    }

    private void Finish(Exception error)
    {
        lock (_lock)
        {
            _error = error;
        }
    }

    private async Task RunAsync(ClientWebSocket socket)
    {
        var token = _closing.Token;
        var backoff = MinBackoff;
        try
        {
            while (true)
            {
                var connected = Stopwatch.GetTimestamp();
                Exception? error = null;
                try
                {
                    await ReadAsync(socket, token).ConfigureAwait(false);
                }
                catch (Exception e) when (!token.IsCancellationRequested)
                {
                    error = e;
                }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                    // Closing.
                }
                var closeStatus = socket.CloseStatus;
                var description = socket.CloseStatusDescription;
                SetSocket(null);
                socket.Dispose();
                if (token.IsCancellationRequested)
                {
                    return;
                }
                if (!_options.Reconnect || (int?)closeStatus == WebSocketPolicyViolation)
                {
                    Finish(error ?? new WebSocketException($"klondike: connection closed ({(int?)closeStatus} {description})"));
                    return;
                }
                // A connection that lived resets the backoff; one closed right after the subscriptions
                // (a slow consumer, a server refusing them) does not, so that it is not reopened every
                // half second.
                if (Stopwatch.GetElapsedTime(connected) > StableAfter)
                {
                    backoff = MinBackoff;
                }
                while (true)
                {
                    await Task.Delay(backoff, token).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, Math.Max(_options.MaxBackoff.Ticks, 1)));
                    try
                    {
                        socket = await DialAsync(token).ConfigureAwait(false);
                        break;
                    }
                    catch (KlondikeApiException e) when (e.IsUnauthorized)
                    {
                        Finish(e);
                        return;
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && e is not OperationCanceledException)
                    {
                        // Not reachable yet: try again.
                    }
                }
                ClientMessage[] subscriptions;
                lock (_lock)
                {
                    subscriptions = [.. _subscriptions.Values];
                }
                SetSocket(socket);
                try
                {
                    foreach (var message in subscriptions)
                    {
                        await WriteAsync(socket, message, token).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is WebSocketException or InvalidOperationException)
                {
                    socket.Abort(); // the read below sees it and reconnects
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closed.
        }
        finally
        {
            SetSocket(null);
            _messages.Writer.TryComplete();
        }
    }

    private async Task ReadAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Interlocked.Exchange(ref _keepAlivePings, 0);
        Volatile.Write(ref _lastReceived, Stopwatch.GetTimestamp());
        var keepAlive = KeepAliveAsync(socket, stop.Token);
        var frame = new ArrayBufferWriter<byte>(64 << 10);
        try
        {
            while (true)
            {
                frame.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(frame.GetMemory(16 << 10), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    frame.Advance(result.Count);
                    if (frame.WrittenCount > MaxMessageBytes)
                    {
                        throw new InvalidDataException("klondike: server message too large");
                    }
                }
                while (!result.EndOfMessage);
                Volatile.Write(ref _lastReceived, Stopwatch.GetTimestamp());

                var message = result.MessageType == WebSocketMessageType.Binary
                    ? MessageDecoder.DecodeCapnp(frame.WrittenSpan)
                    : MessageDecoder.DecodeJson(frame.WrittenSpan);
                switch (message)
                {
                    case null:
                        continue;
                    case PongMessage when Interlocked.Decrement(ref _keepAlivePings) >= 0:
                        continue;
                    case PongMessage:
                        Interlocked.Exchange(ref _keepAlivePings, 0);
                        break;
                    case ErrorMessage e:
                        DropRefused(e.Error);
                        break;
                }
                await _messages.Writer.WriteAsync(message, token).ConfigureAwait(false);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await keepAlive.ConfigureAwait(false);
        }
    }

    // Forgets a subscription the server refused, so that it is not sent again after a reconnection.
    private void DropRefused(StreamError error)
    {
        if (error.Channel.Length > 0 && error.Code is "bad_request" or "unknown_channel" or "too_many_subscriptions")
        {
            lock (_lock)
            {
                _subscriptions.Remove((error.Channel, error.Symbol));
            }
        }
    }

    // Pings a silent connection: one that died (no FIN, no RST) is closed after PingTimeout instead of
    // blocking the stream forever. ClientWebSocket cannot send ping frames before .NET 9 and does not
    // show those of the server, so this is the ping of the protocol, whose answer is a message.
    private async Task KeepAliveAsync(ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.PingInterval, token).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastReceived)) < _options.PingInterval)
                {
                    continue;
                }
                var sent = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref _keepAlivePings);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(SendTimeout);
                    await WriteAsync(socket, new ClientMessage { Op = "ping" }, timeout.Token).ConfigureAwait(false);
                }
                await Task.Delay(_options.PingTimeout, token).ConfigureAwait(false);
                if (Volatile.Read(ref _lastReceived) < sent)
                {
                    socket.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            socket.Abort(); // the send timed out
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection is going away: the read sees it.
        }
    }
}
