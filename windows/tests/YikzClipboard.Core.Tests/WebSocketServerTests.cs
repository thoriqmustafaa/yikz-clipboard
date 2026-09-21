using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public sealed class FakeWsServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public FakeWsServer(Func<WebSocket, int, Task> behavior, string token = "yc_goodtoken")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/ws", async context =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + token)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("""{"code":"unauthorized","message":"missing, invalid or revoked token"}""");
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var n = Interlocked.Increment(ref Connections);
            try
            {
                await behavior(ws, n);
            }
            catch
            {
            }
        });
    }

    public int Connections;

    public Uri WsUri { get; private set; } = null!;

    public async Task StartAsync()
    {
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        WsUri = new Uri(address.Replace("http://", "ws://") + "/ws");
    }

    public static async Task<string?> ReadAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    public static Task SendAsync(WebSocket ws, string text) => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(cts.Token);
        await _app.DisposeAsync();
    }
}

[Collection(TimingCollection.Name)]
public class WebSocketServerTests
{
    private static readonly string Welcome = Vectors.WsMessage("welcome").GetRawText();

    private static ConnectionManager Client(FakeWsServer server, string token, ConnectionOptions options)
    {
        return new ConnectionManager(
            new ClientWebSocketTransport("tests"),
            () => new ConnectTarget(server.WsUri, token, () => WsCodec.Hello(new HelloMessage { DeviceId = "01a05c00-03e0-7c59-b16c-c1bb2928ae88", LastSeq = 0, AppVersion = "test" })),
            options,
            new ReconnectBackoff(new Random(5), 0.05, 0.2),
            new TestLog());
    }

    [Fact]
    public async Task SilentServerIsDetectedAsDeadAndClientReconnects()
    {
        var received = new ConcurrentQueue<string>();
        await using var server = new FakeWsServer(async (ws, n) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var hello = await FakeWsServer.ReadAsync(ws, cts.Token);
            received.Enqueue(hello!);
            await FakeWsServer.SendAsync(ws, Welcome);
            while (await FakeWsServer.ReadAsync(ws, cts.Token) is { } msg)
            {
                received.Enqueue(msg);
            }
        }, "yc_goodtoken");
        await server.StartAsync();
        await using var client = Client(server, "yc_goodtoken", new ConnectionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(150),
            DeadTimeout = TimeSpan.FromMilliseconds(700),
        });
        var connectedCount = 0;
        client.StateChanged += s =>
        {
            if (s == ConnectionState.Connected)
            {
                Interlocked.Increment(ref connectedCount);
            }
        };
        client.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref connectedCount) >= 2, TimeSpan.FromSeconds(10), "client did not reconnect after heartbeat timeout");
        Assert.True(server.Connections >= 2);
        var hello = JsonNode.Parse(received.First())!;
        Assert.Equal("hello", (string?)hello["type"]);
        Assert.Equal("windows", (string?)hello["platform"]);
        Assert.Contains(received, m => m.Contains("\"ping\""));
    }

    [Fact]
    public async Task ServerPingsKeepConnectionAliveAndArePonged()
    {
        var pongs = 0;
        await using var server = new FakeWsServer(async (ws, n) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await FakeWsServer.ReadAsync(ws, cts.Token);
            await FakeWsServer.SendAsync(ws, Welcome);
            var reader = Task.Run(async () =>
            {
                while (await FakeWsServer.ReadAsync(ws, cts.Token) is { } msg)
                {
                    if (msg.Contains("\"pong\""))
                    {
                        Interlocked.Increment(ref pongs);
                    }
                }
            });
            for (var i = 0; i < 30 && ws.State == WebSocketState.Open; i++)
            {
                await Task.Delay(100);
                await FakeWsServer.SendAsync(ws, "{\"type\":\"ping\",\"ts\":" + i + "}");
            }
            await reader;
        });
        await server.StartAsync();
        await using var client = Client(server, "yc_goodtoken", new ConnectionOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(20),
            DeadTimeout = TimeSpan.FromMilliseconds(5000),
        });
        client.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref pongs) >= 8, TimeSpan.FromSeconds(10));
        Assert.Equal(1, server.Connections);
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task RejectedTokenStopsWithAuthLost()
    {
        await using var server = new FakeWsServer((ws, n) => Task.CompletedTask, "yc_goodtoken");
        await server.StartAsync();
        await using var client = Client(server, "yc_badtoken", new ConnectionOptions());
        var stopped = new TaskCompletionSource<StopReason>();
        client.Stopped += r => stopped.TrySetResult(r);
        client.Start();
        Assert.Equal(StopReason.AuthLost, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task RevokedCloseCodeStopsClient()
    {
        await using var server = new FakeWsServer(async (ws, n) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await FakeWsServer.ReadAsync(ws, cts.Token);
            await FakeWsServer.SendAsync(ws, Welcome);
            await Task.Delay(100);
            await ws.CloseAsync((WebSocketCloseStatus)4001, "revoked", CancellationToken.None);
        });
        await server.StartAsync();
        await using var client = Client(server, "yc_goodtoken", new ConnectionOptions());
        var stopped = new TaskCompletionSource<StopReason>();
        client.Stopped += r => stopped.TrySetResult(r);
        client.Start();
        Assert.Equal(StopReason.AuthLost, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task LargeServerFrameIsReceived()
    {
        var bigClip = Vectors.WsMessage("clip_inline_text").GetRawText();
        var node = JsonNode.Parse(bigClip)!;
        node["padding"] = new string('x', 600_000);
        var frame = node.ToJsonString();
        await using var server = new FakeWsServer(async (ws, n) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await FakeWsServer.ReadAsync(ws, cts.Token);
            await FakeWsServer.SendAsync(ws, Welcome);
            await FakeWsServer.SendAsync(ws, frame);
            while (await FakeWsServer.ReadAsync(ws, cts.Token) is not null)
            {
            }
        });
        await server.StartAsync();
        await using var client = Client(server, "yc_goodtoken", new ConnectionOptions());
        var got = new TaskCompletionSource<string>();
        client.MessageReceived += (type, json) =>
        {
            if (type == WsTypes.Clip)
            {
                got.TrySetResult(json);
            }
        };
        client.Start();
        var json = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(frame.Length, json.Length);
    }
}
