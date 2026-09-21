using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Net;

public sealed class WsUnauthorizedException : Exception
{
    public WsUnauthorizedException() : base("WebSocket upgrade was rejected with 401")
    {
    }
}

public readonly record struct WsReceiveResult(string? Text, int? CloseCode, string? CloseReason)
{
    public bool IsClosed => Text == null;

    public static WsReceiveResult FromText(string text) => new(text, null, null);

    public static WsReceiveResult Closed(int? code, string? reason) => new(null, code, reason);
}

public interface IWebSocketConnection : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken ct);

    Task<WsReceiveResult> ReceiveAsync(CancellationToken ct);

    Task CloseAsync(int code, string reason, CancellationToken ct);

    void Abort();
}

public interface IWebSocketTransport
{
    Task<IWebSocketConnection> ConnectAsync(Uri uri, string token, CancellationToken ct);
}

public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly string _userAgent;

    public ClientWebSocketTransport(string userAgent)
    {
        _userAgent = userAgent;
    }

    public async Task<IWebSocketConnection> ConnectAsync(Uri uri, string token, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.SetRequestHeader("User-Agent", _userAgent);
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception) when (ws.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            ws.Dispose();
            throw new WsUnauthorizedException();
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        return new ClientWebSocketConnection(ws);
    }
}

internal sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _buffer = new byte[16384];

    public ClientWebSocketConnection(ClientWebSocket ws)
    {
        _ws = ws;
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<WsReceiveResult> ReceiveAsync(CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>(16384);
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return WsReceiveResult.Closed((int?)_ws.CloseStatus, _ws.CloseStatusDescription);
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return WsReceiveResult.Closed((int?)_ws.CloseStatus, _ws.CloseStatusDescription);
            }
            writer.Write(_buffer.AsSpan(0, result.Count));
            if (writer.WrittenCount > ProtocolConstants.MaxWsServerFrameBytes * 2)
            {
                Abort();
                return WsReceiveResult.Closed(WsCloseCodes.TooLarge, "message too large");
            }
            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    writer.Clear();
                    continue;
                }
                return WsReceiveResult.FromText(Encoding.UTF8.GetString(writer.WrittenSpan));
            }
        }
    }

    public async Task CloseAsync(int code, string reason, CancellationToken ct)
    {
        try
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                await _ws.CloseOutputAsync((WebSocketCloseStatus)code, reason, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    public void Abort()
    {
        try
        {
            _ws.Abort();
        }
        catch
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
