using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Klondike;

/// <summary>Options of a <see cref="KlondikeClient"/>.</summary>
public sealed record KlondikeClientOptions
{
    /// <summary>The server, the production API by default.</summary>
    public string BaseUrl { get; init; } = KlondikeClient.DefaultBaseUrl;

    /// <summary>
    /// The HTTP client of the REST calls; it is not disposed with the <see cref="KlondikeClient"/>. By
    /// default one is created, with a timeout of 30 s.
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>The optional parameters of <see cref="KlondikeClient.GetTradesAsync"/>.</summary>
public sealed record TradesOptions
{
    /// <summary>Most recent trades, 1 to 1000 (100 by default).</summary>
    public int? Limit { get; init; }
    /// <summary>Only trades strictly after this timestamp, ms.</summary>
    public long? Since { get; init; }
    /// <summary>CCXT exchange ids; all venues when empty.</summary>
    public IReadOnlyList<string>? Venues { get; init; }
}

/// <summary>The optional parameters of <see cref="KlondikeClient.GetOrderbookAsync"/>.</summary>
public sealed record OrderbookOptions
{
    /// <summary>Levels per side, 1 to 1000 (50 by default).</summary>
    public int? Depth { get; init; }
    /// <summary>CCXT exchange ids; all venues when empty.</summary>
    public IReadOnlyList<string>? Venues { get; init; }
}

/// <summary>
/// Calls the REST endpoints and opens WebSocket streams. Safe for concurrent use.
/// </summary>
/// <example>
/// <code>
/// using var client = new KlondikeClient(Environment.GetEnvironmentVariable("KLONDIKE_API_KEY")!);
/// var book = await client.GetOrderbookAsync("BTC/USDT", new OrderbookOptions { Depth = 10 });
/// </code>
/// </example>
public sealed class KlondikeClient : IDisposable
{
    /// <summary>The production API.</summary>
    public const string DefaultBaseUrl = "https://api.klondike-crypto.com";

    /// <summary>The version of this library, sent in the User-Agent.</summary>
    public const string Version = "0.1.0";

    internal const string UserAgent = "klondike-csharp/" + Version;

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>A client authenticated with the API key.</summary>
    public KlondikeClient(string apiKey, KlondikeClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        options ??= new KlondikeClientOptions();
        _apiKey = apiKey;
        _baseUrl = options.BaseUrl.TrimEnd('/');
        _ownsHttp = options.HttpClient is null;
        _http = options.HttpClient ?? new HttpClient(new SocketsHttpHandler
        {
            // The overview is served gzipped.
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>The symbols with recent trades; <paramref name="query"/> filters them on a case insensitive substring.</summary>
    public async Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        var response = await GetAsync("/v1/symbols", [("q", query)], KlondikeJson.Default.SymbolsResponse, cancellationToken).ConfigureAwait(false);
        return response.Symbols;
    }

    /// <summary>The recent trades of the symbol on every venue, oldest first.</summary>
    public Task<TradeBatch> GetTradesAsync(string symbol, TradesOptions? options = null, CancellationToken cancellationToken = default) =>
        GetAsync("/v1/trades",
            [("symbol", symbol), ("limit", options?.Limit?.ToString()), ("since", options?.Since?.ToString()), ("venues", JoinVenues(options?.Venues))],
            KlondikeJson.Default.TradeBatch, cancellationToken);

    /// <summary>The merged order book of the symbol.</summary>
    public Task<Orderbook> GetOrderbookAsync(string symbol, OrderbookOptions? options = null, CancellationToken cancellationToken = default) =>
        GetAsync("/v1/orderbook",
            [("symbol", symbol), ("depth", options?.Depth?.ToString()), ("venues", JoinVenues(options?.Venues))],
            KlondikeJson.Default.Orderbook, cancellationToken);

    /// <summary>The market overview.</summary>
    public Task<Overview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/v1/overview", [], KlondikeJson.Default.Overview, cancellationToken);

    /// <summary>
    /// Opens a WebSocket stream. The first connection is opened before this returns: a refused key or an
    /// unreachable server is an error here.
    /// </summary>
    public Task<KlondikeStream> OpenStreamAsync(StreamOptions? options = null, CancellationToken cancellationToken = default) =>
        KlondikeStream.OpenAsync(_baseUrl, _apiKey, options ?? new StreamOptions(), cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<T> GetAsync<T>(string path, (string Name, string? Value)[] query, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path + QueryString(query));
        request.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await ApiErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(body, type, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException($"klondike: null answer from {path}");
        }
    }

    private static string QueryString((string Name, string? Value)[] query)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in query)
        {
            if (!string.IsNullOrEmpty(value))
            {
                sb.Append(sb.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }
        }
        return sb.ToString();
    }

    private static string? JoinVenues(IReadOnlyList<string>? venues) =>
        venues is { Count: > 0 } ? string.Join(',', venues) : null;

    private static async Task<KlondikeApiException> ApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var code = response.StatusCode.ToString();
        var message = body.Trim();
        try
        {
            if (JsonSerializer.Deserialize(body, KlondikeJson.Default.ApiErrorBody) is { Error.Length: > 0 } payload)
            {
                code = payload.Code;
                message = payload.Error;
            }
        }
        catch (JsonException)
        {
            // Not an answer of the API (a proxy, for instance): the body is the message.
        }
        return new KlondikeApiException((int)response.StatusCode, code, message, response.Headers.RetryAfter?.Delta);
    }
}
