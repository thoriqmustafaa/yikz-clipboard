using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Storage;

namespace YikzClipboard.Core.Tests.Support;

public sealed record FakeResponse(int Status, byte[] Body, string ContentType = "application/json; charset=utf-8", IReadOnlyDictionary<string, string>? Headers = null)
{
    public static FakeResponse Json(int status, string json) => new(status, Encoding.UTF8.GetBytes(json));

    public static FakeResponse Binary(byte[] body) => new(200, body, "application/octet-stream");

    public static FakeResponse Empty(int status = 204) => new(status, Array.Empty<byte>(), "");
}

public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, byte[]?, FakeResponse>> _routes = new();

    public ConcurrentQueue<(string Method, string PathAndQuery, byte[]? Body, string? Authorization)> Requests { get; } = new();

    public void On(string method, string pathAndQuery, Func<HttpRequestMessage, byte[]?, FakeResponse> handler)
    {
        _routes[method + " " + pathAndQuery] = handler;
    }

    public void On(string method, string pathAndQuery, FakeResponse response) => On(method, pathAndQuery, (_, _) => response);

    public int Count(string method, string pathAndQuery) => Requests.Count(r => r.Method == method && r.PathAndQuery == pathAndQuery);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = null;
        if (request.Content != null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        var key = request.Method.Method + " " + request.RequestUri!.PathAndQuery;
        Requests.Enqueue((request.Method.Method, request.RequestUri.PathAndQuery, body, request.Headers.Authorization?.ToString()));
        FakeResponse response;
        if (_routes.TryGetValue(key, out var handler))
        {
            response = handler(request, body);
        }
        else
        {
            response = FakeResponse.Json(404, """{"code":"not_found","message":"route not found"}""");
        }
        var message = new HttpResponseMessage((HttpStatusCode)response.Status)
        {
            Content = new ByteArrayContent(response.Body),
            RequestMessage = request,
        };
        if (!string.IsNullOrEmpty(response.ContentType))
        {
            message.Content.Headers.TryAddWithoutValidation("Content-Type", response.ContentType);
        }
        if (response.Headers != null)
        {
            foreach (var (k, v) in response.Headers)
            {
                message.Headers.TryAddWithoutValidation(k, v);
            }
        }
        return message;
    }
}

public sealed class FakeSink : IClipboardSink
{
    public ConcurrentQueue<ReceivedContent> Written { get; } = new();

    public TaskCompletionSource<ReceivedContent> FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WriteAsync(ReceivedContent content, CancellationToken ct)
    {
        Written.Enqueue(content);
        FirstWrite.TrySetResult(content);
        return Task.CompletedTask;
    }
}

public sealed class FakeNotifier : IUserNotifier
{
    public ConcurrentQueue<string> Large { get; } = new();
    public ConcurrentQueue<string> Problems { get; } = new();

    public void LargeItemAvailable(HistoryEntry entry, string deviceName) => Large.Enqueue(entry.Id);

    public void ItemReceived(HistoryEntry entry, string deviceName)
    {
    }

    public void Problem(string title, string message) => Problems.Enqueue(title);
}

public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now)
    {
        UtcNow = now;
    }

    public DateTimeOffset UtcNow { get; set; }
}

public sealed class TestLog : ILog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        Lines.Enqueue($"{level} [{category}] {message}{(exception != null ? ": " + exception.Message : "")}");
    }
}

public sealed class FakeConnection : IWebSocketConnection
{
    private readonly Channel<WsReceiveResult> _incoming = Channel.CreateUnbounded<WsReceiveResult>();

    public ConcurrentQueue<string> Sent { get; } = new();

    public bool Aborted { get; private set; }

    public int? ClosedWith { get; private set; }

    public TaskCompletionSource<string> FirstSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string>? OnSent;

    public void ServerSend(string json) => _incoming.Writer.TryWrite(WsReceiveResult.FromText(json));

    public void ServerClose(int code, string reason = "") => _incoming.Writer.TryWrite(WsReceiveResult.Closed(code, reason));

    public Task SendAsync(string text, CancellationToken ct)
    {
        if (Aborted)
        {
            throw new IOException("aborted");
        }
        Sent.Enqueue(text);
        FirstSent.TrySetResult(text);
        OnSent?.Invoke(text);
        return Task.CompletedTask;
    }

    public async Task<WsReceiveResult> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return WsReceiveResult.Closed(null, "aborted");
        }
    }

    public Task CloseAsync(int code, string reason, CancellationToken ct)
    {
        ClosedWith = code;
        return Task.CompletedTask;
    }

    public void Abort()
    {
        Aborted = true;
        _incoming.Writer.TryComplete();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeTransport : IWebSocketTransport
{
    private readonly Channel<FakeConnection> _connections = Channel.CreateUnbounded<FakeConnection>();

    public Func<int, Exception?>? FailWith { get; set; }

    public int Attempts;

    public ConcurrentQueue<DateTime> AttemptTimes { get; } = new();

    public Task<IWebSocketConnection> ConnectAsync(Uri uri, string token, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref Attempts);
        AttemptTimes.Enqueue(DateTime.UtcNow);
        var failure = FailWith?.Invoke(n);
        if (failure != null)
        {
            return Task.FromException<IWebSocketConnection>(failure);
        }
        var conn = new FakeConnection();
        _connections.Writer.TryWrite(conn);
        return Task.FromResult<IWebSocketConnection>(conn);
    }

    public async Task<FakeConnection> NextConnectionAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        return await _connections.Reader.ReadAsync(cts.Token);
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(message ?? "condition not met in time");
            }
            await Task.Delay(10);
        }
    }
}
