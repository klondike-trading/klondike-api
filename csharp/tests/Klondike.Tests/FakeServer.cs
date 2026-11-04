using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klondike.Tests;

/// <summary>A real HTTP and WebSocket server on a local port, scripted by the tests.</summary>
internal sealed class FakeServer : IAsyncDisposable
{
    public const string Key = "kl_test";

    private readonly WebApplication _app;
    private readonly Channel<FakeConnection> _connections = Channel.CreateUnbounded<FakeConnection>();
    private readonly ConcurrentDictionary<string, Func<HttpContext, Task>> _routes = new();

    public ConcurrentQueue<HttpRequestInfo> Requests { get; } = new();
    public string Url { get; }
    public int ConnectionCount;

    /// <summary>When set, answers the upgrade with this status instead of accepting it.</summary>
    public int? RefuseWebSockets { get; set; }

    /// <summary>Connections accepted but not yet read by the test.</summary>
    public ChannelReader<FakeConnection> Connections => _connections.Reader;

    private FakeServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public static async Task<FakeServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeServer? server = null;
        app.UseWebSockets();
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeServer(app, url);
        return server;
    }

    public void Route(string path, Func<HttpContext, Task> handler) => _routes[path] = handler;

    public void RouteJson(string path, string json) => Route(path, c => WriteJsonAsync(c, 200, json));

    public static Task WriteJsonAsync(HttpContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(json);
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        Requests.Enqueue(new HttpRequestInfo(request.Path, request.QueryString.Value ?? "", request.Headers["X-API-Key"].ToString(), request.Headers.UserAgent.ToString()));
        if (request.Path != "/v1/ws")
        {
            if (_routes.TryGetValue(request.Path, out var route))
            {
                await route(context);
            }
            else
            {
                await WriteJsonAsync(context, 404, """{"code":"not_found","error":"no route"}""");
            }
            return;
        }
        if (RefuseWebSockets is { } status)
        {
            if (status == 429)
            {
                context.Response.Headers.RetryAfter = "7";
            }
            await WriteJsonAsync(context, status, """{"code":"refused","error":"refused"}""");
            return;
        }
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref ConnectionCount);
        var connection = new FakeConnection(socket, request.Query["format"].ToString());
        _connections.Writer.TryWrite(connection);
        await connection.RunAsync(context.RequestAborted);
    }

    public async Task<FakeConnection> NextConnectionAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _connections.Reader.ReadAsync(timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Connections still open: they are dropped below.
        }
        await _app.DisposeAsync();
    }
}

internal sealed record HttpRequestInfo(string Path, string Query, string ApiKey, string UserAgent);

internal sealed class FakeConnection(WebSocket socket, string format)
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<JsonElement> _received = Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Format { get; } = format;

    /// <summary>Answers pings of the protocol when set, as the real server does.</summary>
    public bool AnswerPings { get; set; }

    public async Task RunAsync(CancellationToken aborted)
    {
        var buffer = new byte[64 << 10];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, aborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                var message = document.RootElement.Clone();
                _received.Writer.TryWrite(message);
                if (AnswerPings && message.GetProperty("op").GetString() == "ping")
                {
                    await SendTextAsync("""{"type":"pong"}""");
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Gone.
        }
        await _done.Task;
    }

    /// <summary>The next message of the client.</summary>
    public async Task<JsonElement> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _received.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>The next messages of the client, as compact JSON.</summary>
    public async Task<string> ReceiveTextAsync() => (await ReceiveAsync()).GetRawText();

    public async Task SendTextAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(int status, string reason = "")
    {
        await socket.CloseOutputAsync((WebSocketCloseStatus)status, reason, CancellationToken.None);
        _done.TrySetResult();
    }

    /// <summary>Lets the server side end, when the test does not close it.</summary>
    public void Release() => _done.TrySetResult();
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what = "condition", int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>The next message of a stream, with a timeout.</summary>
    public static async Task<StreamMessage> NextAsync(IAsyncEnumerator<StreamMessage> messages)
    {
        var next = messages.MoveNextAsync().AsTask();
        if (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10))) != next)
        {
            throw new TimeoutException("no message");
        }
        Assert.True(await next, "the stream has ended");
        return messages.Current;
    }
}

/// <summary>The fixtures of testdata/messages, written by the Go library (the encoder of the server).</summary>
internal static class Fixtures
{
    public static string Directory { get; } = Find();

    public static byte[] Json(string name) => File.ReadAllBytes(Path.Combine(Directory, name + ".json"));

    public static byte[] Capnp(string name) => File.ReadAllBytes(Path.Combine(Directory, name + ".capnp"));

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "testdata", "messages");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("testdata/messages");
    }
}
