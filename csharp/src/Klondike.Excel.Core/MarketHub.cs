using System.Threading.Channels;

namespace Klondike.Excel;

/// <summary>Options of a <see cref="MarketHub"/>.</summary>
public sealed record MarketHubOptions
{
    /// <summary>The API key; without one, every feed says so until <see cref="MarketHub.SetApiKey"/>.</summary>
    public string? ApiKey { get; init; }
    /// <summary>The server, the production API by default.</summary>
    public string BaseUrl { get; init; } = KlondikeClient.DefaultBaseUrl;
    /// <summary>Encoding of the streams.</summary>
    public StreamFormat Format { get; init; } = StreamFormat.Json;
    /// <summary>WebSocket connections opened at most. The API accepts 5 per key: one is left to other tools.</summary>
    public int MaxConnections { get; init; } = 4;
    /// <summary>Subscriptions per connection, the limit of the API.</summary>
    public int MaxSubscriptionsPerConnection { get; init; } = 50;
    /// <summary>Time between two REST calls: the API accepts 10 per second per key.</summary>
    public TimeSpan RestInterval { get; init; } = TimeSpan.FromMilliseconds(150);
    /// <summary>How long a connection without subscriptions stays open: arguments often change one after the other.</summary>
    public TimeSpan IdleClose { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Longest wait between two connection attempts.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>A value of a feed, or the error that stops it.</summary>
public readonly record struct FeedUpdate<T>(T? Value, string? Error) where T : class;

/// <summary>
/// The market data of a workbook. However many cells ask for a stream, it is subscribed once; subscriptions
/// are spread over a few connections within the limits of the API, and REST calls are spaced out.
/// <para>
/// A connection holds one subscription per channel and symbol, and subscribing again replaces it: the
/// same symbol with other venues goes on another connection, and cells asking for the same stream share
/// the largest depth. Everything happens on one task, listeners included: they must not block and must not
/// keep the lists they are given.
/// </para>
/// </summary>
public sealed class MarketHub : IAsyncDisposable
{
    /// <summary>Levels per side a stream gives at most.</summary>
    public const int MaxStreamDepth = 200;
    /// <summary>Trades the API keeps per symbol.</summary>
    public const int MaxTradesWindow = 1000;
    internal const string NoKeyMessage = "no API key: use the Klondike tab of the ribbon, or set KLONDIKE_API_KEY";
    internal const string RefusedMessage = "API key refused";
    private const int MaxRestAttempts = 4;

    private readonly MarketHubOptions _options;
    private readonly Channel<Action> _inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _restLock = new(1, 1);
    private readonly Dictionary<SubscriptionKey, Entry> _entries = [];
    private readonly List<Connection> _connections = [];
    private readonly List<Action<string>> _statusListeners = [];
    private KlondikeClient? _client;
    private bool _refused;
    private string _status = "";
    private int _disposed;

    /// <summary>A hub; it connects once something is watched.</summary>
    public MarketHub(MarketHubOptions? options = null)
    {
        _options = options ?? new MarketHubOptions();
        _client = NewClient(_options.ApiKey);
        _status = ComputeStatus();
        _loop = Task.Run(RunLoopAsync);
    }

    /// <summary>Replaces the API key: every stream is opened again with it.</summary>
    public void SetApiKey(string? apiKey) => Post(() =>
    {
        foreach (var connection in _connections.ToArray())
        {
            Close(connection);
        }
        _client?.Dispose();
        _client = NewClient(apiKey);
        _refused = false;
        foreach (var entry in _entries.Values)
        {
            entry.Connection = null;
            entry.Error = null;
            Attach(entry);
        }
        UpdateStatus();
    });

    /// <summary>The merged book of a symbol, at least <paramref name="depth"/> levels per side.</summary>
    public IDisposable WatchOrderbook(string symbol, int depth, string? venues, Action<FeedUpdate<Orderbook>> listener) =>
        Watch(new SubscriptionKey(Channels.Orderbook, symbol, CanonicalVenues(venues)), Math.Clamp(depth, 1, MaxStreamDepth), listener);

    /// <summary>
    /// The last trades of a symbol, oldest first: the recent ones of the REST API, then those of the stream.
    /// At least <paramref name="limit"/> when the API has them, more when another cell asks for more.
    /// </summary>
    public IDisposable WatchTrades(string symbol, int limit, string? venues, Action<FeedUpdate<IReadOnlyList<Trade>>> listener) =>
        Watch(new SubscriptionKey(Channels.Trades, symbol, CanonicalVenues(venues)), Math.Clamp(limit, 1, MaxTradesWindow), listener);

    /// <summary>The market overview.</summary>
    public IDisposable WatchOverview(Action<FeedUpdate<Overview>> listener) =>
        Watch(new SubscriptionKey(Channels.Overview, "", ""), 0, listener);

    /// <summary>The state of the connections: connected, connecting, reconnecting, idle, no key or refused.</summary>
    public IDisposable WatchStatus(Action<string> listener)
    {
        Post(() =>
        {
            _statusListeners.Add(listener);
            listener(_status);
        });
        return new Unsubscriber(() => Post(() => _statusListeners.Remove(listener)));
    }

    /// <summary>The symbols with recent trades, on the same schedule as the other REST calls.</summary>
    public async Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(string? query, CancellationToken cancellationToken = default)
    {
        var client = Volatile.Read(ref _client) ?? throw new InvalidOperationException(NoKeyMessage);
        return await RestAsync(c => c.GetSymbolsAsync(query, cancellationToken), client, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            foreach (var connection in _connections.ToArray())
            {
                Close(connection);
            }
            _connections.Clear();
            _entries.Clear();
            _statusListeners.Clear();
            stopped.SetResult();
        });
        await stopped.Task.ConfigureAwait(false);
        await _closing.CancelAsync().ConfigureAwait(false);
        _inbox.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        _client?.Dispose();
        _closing.Dispose();
    }

    private KlondikeClient? NewClient(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : new KlondikeClient(apiKey.Trim(), new KlondikeClientOptions { BaseUrl = _options.BaseUrl });

    internal static string CanonicalVenues(string? venues) =>
        venues is null
            ? ""
            : string.Join(',', venues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => v.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));

    private void Post(Action action) => _inbox.Writer.TryWrite(action);

    private async Task RunLoopAsync()
    {
        await foreach (var action in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                System.Diagnostics.Trace.TraceError("klondike: {0}", e);
            }
        }
    }

    // ---- subscriptions ----

    private IDisposable Watch<T>(SubscriptionKey key, int size, Action<FeedUpdate<T>> listener) where T : class
    {
        var token = new Listener(update => listener(new FeedUpdate<T>(update.Value as T, update.Error)), size);
        Post(() => Add(key, token));
        return new Unsubscriber(() => Post(() => Remove(key, token)));
    }

    private void Add(SubscriptionKey key, Listener listener)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(key);
            _entries[key] = entry;
        }
        entry.Listeners.Add(listener);
        if (entry.Latest is not null || entry.Error is not null)
        {
            listener.Notify(new FeedUpdate<object>(entry.Latest, entry.Error));
        }
        var grown = listener.Size > entry.Size;
        entry.Size = Math.Max(entry.Size, listener.Size);
        if (entry.Connection is null)
        {
            Attach(entry);
        }
        else if (grown)
        {
            SendSubscribe(entry.Connection, entry);
            if (key.Channel == Channels.Trades)
            {
                LoadTrades(entry);
            }
        }
        UpdateStatus();
    }

    private void Remove(SubscriptionKey key, Listener listener)
    {
        if (!_entries.TryGetValue(key, out var entry) || !entry.Listeners.Remove(listener) || entry.Listeners.Count > 0)
        {
            return;
        }
        _entries.Remove(key);
        if (entry.Connection is { } connection)
        {
            connection.Wire.Remove((key.Channel, key.Symbol));
            Send(connection, s => s.UnsubscribeAsync(key.Channel, key.Symbol.Length > 0 ? key.Symbol : null));
            foreach (var waiting in _entries.Values.Where(e => e.WaitingForCapacity).ToArray())
            {
                Attach(waiting);
            }
            if (connection.Wire.Count == 0)
            {
                _ = CloseWhenIdleAsync(connection);
            }
        }
        UpdateStatus();
    }

    private void Attach(Entry entry)
    {
        if (_client is null || _refused)
        {
            Fail(entry, _client is null ? NoKeyMessage : RefusedMessage);
            return;
        }
        var wire = (entry.Key.Channel, entry.Key.Symbol);
        var connection = _connections.Find(c => c.Wire.Count < _options.MaxSubscriptionsPerConnection && !c.Wire.ContainsKey(wire));
        if (connection is null)
        {
            if (_connections.Count >= _options.MaxConnections)
            {
                entry.WaitingForCapacity = true;
                Fail(entry, $"too many subscriptions: {_options.MaxConnections} connections of {_options.MaxSubscriptionsPerConnection} are in use");
                return;
            }
            connection = new Connection(_client);
            _connections.Add(connection);
            _ = OpenAsync(connection);
        }
        connection.Wire[wire] = entry;
        entry.Connection = connection;
        entry.WaitingForCapacity = false;
        entry.Error = null;
        SendSubscribe(connection, entry);
        if (entry.Key.Channel == Channels.Trades)
        {
            LoadTrades(entry);
        }
    }

    private static void Fail(Entry entry, string message)
    {
        entry.Error = message;
        entry.Latest = null;
        entry.NotifyAll();
    }

    private void SendSubscribe(Connection connection, Entry entry)
    {
        var key = entry.Key;
        IReadOnlyList<string>? venues = key.Venues.Length > 0 ? key.Venues.Split(',') : null;
        var depth = Math.Max(entry.Size, 1);
        Send(connection, key.Channel switch
        {
            Channels.Trades => s => s.SubscribeTradesAsync(key.Symbol, venues),
            Channels.Orderbook => s => s.SubscribeOrderbookAsync(key.Symbol, depth, venues),
            _ => s => s.SubscribeOverviewAsync(),
        });
    }

    // Sends in order, without holding the loop up. Before the connection is open, OpenAsync sends everything.
    private static void Send(Connection connection, Func<KlondikeStream, Task> send)
    {
        if (connection.Stream is not { } stream)
        {
            return;
        }
        connection.Sends = connection.Sends.ContinueWith(async _ =>
        {
            try
            {
                await send(stream).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning("klondike: send: {0}", e.Message);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    // ---- connections ----

    private async Task OpenAsync(Connection connection)
    {
        var backoff = TimeSpan.FromMilliseconds(500);
        var token = connection.Closing.Token;
        KlondikeStream stream;
        while (true)
        {
            try
            {
                stream = await connection.Client.OpenStreamAsync(new StreamOptions { Format = _options.Format, MaxBackoff = _options.MaxBackoff }, token).ConfigureAwait(false);
                break;
            }
            catch (KlondikeApiException e) when (e.IsUnauthorized)
            {
                Post(() => Refuse(connection.Client));
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Unreachable, or the connections of the key are in use: it may change.
                Post(() =>
                {
                    connection.LastError = e.Message;
                    UpdateStatus();
                });
                try
                {
                    await Task.Delay(backoff, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
            }
        }
        stream.ConnectionChanged += connected => Post(() => ConnectionChanged(connection, connected));
        Post(() =>
        {
            if (connection.Closing.IsCancellationRequested)
            {
                _ = stream.DisposeAsync().AsTask();
                return;
            }
            connection.Stream = stream;
            connection.Connected = stream.IsConnected;
            connection.LastError = null;
            foreach (var entry in connection.Wire.Values)
            {
                SendSubscribe(connection, entry);
            }
            UpdateStatus();
        });
        try
        {
            await foreach (var message in stream.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                Post(() => Route(connection, message));
            }
        }
        finally
        {
            Post(() => Ended(connection, stream.Error));
        }
    }

    private void ConnectionChanged(Connection connection, bool connected)
    {
        if (connected && !connection.Connected)
        {
            // The trades of the outage are only on the REST API.
            foreach (var entry in connection.Wire.Values.Where(e => e.Key.Channel == Channels.Trades))
            {
                LoadTrades(entry);
            }
        }
        connection.Connected = connected;
        UpdateStatus();
    }

    private void Ended(Connection connection, Exception? error)
    {
        if (connection.Closing.IsCancellationRequested)
        {
            return;
        }
        if (error is KlondikeApiException { IsUnauthorized: true })
        {
            Refuse(connection.Client);
            return;
        }
        // The server will not take the connection back.
        foreach (var entry in connection.Wire.Values.ToArray())
        {
            entry.Connection = null;
            Fail(entry, error?.Message ?? "connection closed");
        }
        connection.Wire.Clear();
        Close(connection);
        UpdateStatus();
    }

    private void Refuse(KlondikeClient client)
    {
        if (!ReferenceEquals(client, _client))
        {
            return; // the answer to a key that has been replaced
        }
        _refused = true;
        foreach (var other in _connections.ToArray())
        {
            Close(other);
        }
        foreach (var entry in _entries.Values)
        {
            entry.Connection = null;
            Fail(entry, RefusedMessage);
        }
        UpdateStatus();
    }

    private void Close(Connection connection)
    {
        _connections.Remove(connection);
        connection.Closing.Cancel();
        if (connection.Stream is { } stream)
        {
            _ = stream.DisposeAsync().AsTask();
        }
    }

    private async Task CloseWhenIdleAsync(Connection connection)
    {
        try
        {
            await Task.Delay(_options.IdleClose, _closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Post(() =>
        {
            if (connection.Wire.Count == 0 && _connections.Contains(connection))
            {
                Close(connection);
                UpdateStatus();
            }
        });
    }

    // ---- data ----

    private void Route(Connection connection, StreamMessage message)
    {
        switch (message)
        {
            case OrderbookMessage { Orderbook: var book } when connection.Wire.TryGetValue((Channels.Orderbook, book.Symbol), out var entry):
                entry.Error = null;
                entry.Latest = book;
                entry.NotifyAll();
                break;
            case TradesMessage { Trades: var batch } when connection.Wire.TryGetValue((Channels.Trades, batch.Symbol), out var entry):
                entry.Error = null;
                entry.Latest = entry.Trades.Append(batch.Trades, entry.Size);
                entry.NotifyAll();
                break;
            case OverviewMessage { Overview: var overview } when connection.Wire.TryGetValue((Channels.Overview, ""), out var entry):
                entry.Error = null;
                entry.Latest = overview;
                entry.NotifyAll();
                break;
            case ErrorMessage { Error: var error } when connection.Wire.TryGetValue((error.Channel, error.Symbol), out var entry)
                && error.Code is "bad_request" or "unknown_channel" or "too_many_subscriptions":
                connection.Wire.Remove((error.Channel, error.Symbol));
                entry.Connection = null;
                Fail(entry, error.Message);
                break;
        }
    }

    private void LoadTrades(Entry entry)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }
        var key = entry.Key;
        var limit = entry.Size;
        IReadOnlyList<string>? venues = key.Venues.Length > 0 ? key.Venues.Split(',') : null;
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await RestAsync(
                    c => c.GetTradesAsync(key.Symbol, new TradesOptions { Limit = limit, Venues = venues }, _closing.Token),
                    client, _closing.Token).ConfigureAwait(false);
                Post(() => MergeTrades(entry, snapshot.Trades));
            }
            catch (KlondikeApiException e) when (e.IsUnauthorized)
            {
                Post(() => Refuse(client));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The stream still feeds the table: it shows what it has, an empty table rather than nothing.
                System.Diagnostics.Trace.TraceWarning("klondike: trades of {0}: {1}", key.Symbol, e.Message);
                Post(() => MergeTrades(entry, []));
            }
        });
    }

    private void MergeTrades(Entry entry, IReadOnlyList<Trade> snapshot)
    {
        if (!_entries.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry) || entry.Error is not null)
        {
            return;
        }
        entry.Latest = entry.Trades.Merge(snapshot, entry.Size);
        entry.NotifyAll();
    }

    // One REST call at a time, spaced out, and waiting when the API says so: a workbook opening with a
    // hundred tables must not exhaust the rate limit of the key.
    private async Task<T> RestAsync<T>(Func<KlondikeClient, Task<T>> call, KlondikeClient client, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _restLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var wait = _options.RestInterval;
            try
            {
                return await call(client).ConfigureAwait(false);
            }
            catch (KlondikeApiException e) when (e.Status == 429 && attempt < MaxRestAttempts)
            {
                wait = e.RetryAfter ?? TimeSpan.FromSeconds(1);
            }
            finally
            {
                // The lock is released when the interval has passed, whatever the outcome.
                _ = ReleaseAfterAsync(wait);
            }
        }
    }

    private async Task ReleaseAfterAsync(TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }
        finally
        {
            _restLock.Release();
        }
    }

    // ---- status ----

    private string ComputeStatus()
    {
        if (_client is null)
        {
            return "no API key";
        }
        if (_refused)
        {
            return RefusedMessage;
        }
        if (_connections.Count == 0)
        {
            return "idle";
        }
        if (_connections.All(c => c.Connected))
        {
            return "connected";
        }
        return _connections.Any(c => c.Stream is not null) || _connections.Any(c => c.LastError is not null) ? "reconnecting" : "connecting";
    }

    private void UpdateStatus()
    {
        var status = ComputeStatus();
        if (status == _status)
        {
            return;
        }
        _status = status;
        foreach (var listener in _statusListeners.ToArray())
        {
            listener(status);
        }
    }

    // ---- state ----

    private readonly record struct SubscriptionKey(string Channel, string Symbol, string Venues);

    private sealed class Listener(Action<FeedUpdate<object>> notify, int size)
    {
        public int Size { get; } = size;

        public void Notify(FeedUpdate<object> update)
        {
            try
            {
                notify(update);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceError("klondike: listener: {0}", e);
            }
        }
    }

    private sealed class Entry(SubscriptionKey key)
    {
        public SubscriptionKey Key { get; } = key;
        public List<Listener> Listeners { get; } = [];
        public int Size { get; set; }
        public Connection? Connection { get; set; }
        public object? Latest { get; set; }
        public string? Error { get; set; }
        public bool WaitingForCapacity { get; set; }
        public TradeWindow Trades { get; } = new();

        public void NotifyAll()
        {
            var update = new FeedUpdate<object>(Latest, Error);
            foreach (var listener in Listeners.ToArray())
            {
                listener.Notify(update);
            }
        }
    }

    private sealed class Connection(KlondikeClient client)
    {
        public KlondikeClient Client { get; } = client;
        public Dictionary<(string Channel, string Symbol), Entry> Wire { get; } = [];
        public CancellationTokenSource Closing { get; } = new();
        public KlondikeStream? Stream { get; set; }
        public bool Connected { get; set; }
        public string? LastError { get; set; }
        public Task Sends { get; set; } = Task.CompletedTask;
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
