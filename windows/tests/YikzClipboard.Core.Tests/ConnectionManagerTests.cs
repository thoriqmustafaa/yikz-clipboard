using System.Text.Json.Nodes;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Tests.Support;

namespace YikzClipboard.Core.Tests;

public class ConnectionManagerTests
{
    private static readonly string Welcome = Vectors.WsMessage("welcome").GetRawText();

    private static ConnectionManager Create(FakeTransport transport, ConnectionOptions? options = null, ReconnectBackoff? backoff = null)
    {
        return new ConnectionManager(
            transport,
            () => new ConnectTarget(new Uri("wss://clip.test/ws"), "yc_token", () => WsCodec.Hello(new HelloMessage { DeviceId = "dev", LastSeq = 7, AppVersion = "1.0.0" })),
            options ?? new ConnectionOptions { HeartbeatInterval = TimeSpan.FromSeconds(20), DeadTimeout = TimeSpan.FromSeconds(45) },
            backoff ?? new ReconnectBackoff(new Random(1), 0.02, 0.2),
            new TestLog());
    }

    [Fact]
    public async Task SendsHelloFirstAndBecomesConnectedOnWelcome()
    {
        var transport = new FakeTransport();
        await using var cm = Create(transport);
        var types = new List<string>();
        cm.MessageReceived += (t, _) => types.Add(t);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        var hello = JsonNode.Parse(await conn.FirstSent.Task)!;
        Assert.Equal("hello", (string?)hello["type"]);
        Assert.Equal(1, (int)hello["protocol_version"]!);
        Assert.Equal(7, (long)hello["last_seq"]!);
        Assert.Equal("dev", (string?)hello["device_id"]);
        Assert.Equal(ConnectionState.Connecting, cm.State);
        conn.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
        Assert.Contains("welcome", types);
    }

    [Fact]
    public async Task AnswersServerPingWithPong()
    {
        var transport = new FakeTransport();
        await using var cm = Create(transport);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.ServerSend(Welcome);
        conn.ServerSend("""{"type":"ping","ts":1789985420000}""");
        await Wait.UntilAsync(() => conn.Sent.Any(s => s.Contains("\"pong\"")));
        var pong = JsonNode.Parse(conn.Sent.First(s => s.Contains("\"pong\"")))!;
        Assert.Equal(1789985420000, (long)pong["ts"]!);
    }

    [Fact]
    public async Task ReconnectsWithGrowingBackoffAndResetsAfterWelcome()
    {
        var transport = new FakeTransport { FailWith = n => n <= 4 ? new IOException("down") : null };
        var backoff = new ReconnectBackoff(new Random(3), 0.01, 0.1);
        await using var cm = Create(transport, backoff: backoff);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        Assert.Equal(5, transport.Attempts);
        Assert.Equal(4, backoff.Attempt);
        conn.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
        Assert.Equal(0, backoff.Attempt);
        conn.ServerClose(1001, "restart");
        var next = await transport.NextConnectionAsync();
        Assert.Equal(6, transport.Attempts);
        Assert.Equal(1, backoff.Attempt);
        next.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
    }

    [Theory]
    [InlineData(4001, StopReason.AuthLost)]
    [InlineData(4003, StopReason.UpdateRequired)]
    [InlineData(4002, StopReason.TooManyConnections)]
    public async Task TerminalCloseCodesStopReconnecting(int code, StopReason reason)
    {
        var transport = new FakeTransport();
        await using var cm = Create(transport);
        var stopped = new TaskCompletionSource<StopReason>();
        cm.Stopped += r => stopped.TrySetResult(r);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.ServerSend(Welcome);
        conn.ServerClose(code, "bye");
        Assert.Equal(reason, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(300);
        Assert.Equal(1, transport.Attempts);
        Assert.Equal(ConnectionState.Stopped, cm.State);
    }

    [Fact]
    public async Task TooManyConnectionsRestartsOnUserAction()
    {
        var transport = new FakeTransport();
        await using var cm = Create(transport);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.ServerClose(4002);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Stopped);
        cm.ReconnectNow("user");
        var next = await transport.NextConnectionAsync();
        next.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
        Assert.Equal(2, transport.Attempts);
    }

    [Fact]
    public async Task UnauthorizedUpgradeStopsWithAuthLost()
    {
        var transport = new FakeTransport { FailWith = _ => new WsUnauthorizedException() };
        await using var cm = Create(transport);
        var stopped = new TaskCompletionSource<StopReason>();
        cm.Stopped += r => stopped.TrySetResult(r);
        cm.Start();
        Assert.Equal(StopReason.AuthLost, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, transport.Attempts);
    }

    [Fact]
    public async Task ReconnectNowClosesOpenSocketAndSkipsBackoff()
    {
        var transport = new FakeTransport();
        var backoff = new ReconnectBackoff(new Random(1), 5, 30);
        await using var cm = Create(transport, backoff: backoff);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
        var started = DateTime.UtcNow;
        cm.ReconnectNow("wake");
        var next = await transport.NextConnectionAsync();
        Assert.True(conn.Aborted);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        Assert.Equal(0, backoff.Attempt);
        next.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task ReconnectNowCancelsPendingBackoffDelay()
    {
        var transport = new FakeTransport { FailWith = n => n == 1 ? new IOException("offline") : null };
        var backoff = new ReconnectBackoff(new MaxRandomLike(), 20, 30);
        await using var cm = Create(transport, backoff: backoff);
        cm.Start();
        await Wait.UntilAsync(() => cm.State == ConnectionState.Reconnecting);
        cm.ReconnectNow("network available");
        var conn = await transport.NextConnectionAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, transport.Attempts);
        conn.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
    }

    private sealed class MaxRandomLike : Random
    {
        public override double NextDouble() => 0.99;
    }

    [Fact]
    public async Task HeartbeatSendsPingsAndDetectsDeadConnection()
    {
        var transport = new FakeTransport();
        var options = new ConnectionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DeadTimeout = TimeSpan.FromMilliseconds(450),
        };
        await using var cm = Create(transport, options);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.ServerSend(Welcome);
        await Wait.UntilAsync(() => conn.Sent.Count(s => s.Contains("\"ping\"")) >= 2, TimeSpan.FromSeconds(3));
        var next = await transport.NextConnectionAsync(TimeSpan.FromSeconds(3));
        Assert.True(conn.Aborted);
        Assert.Equal(2, transport.Attempts);
    }

    [Fact]
    public async Task TrafficKeepsConnectionAlive()
    {
        var transport = new FakeTransport();
        var options = new ConnectionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DeadTimeout = TimeSpan.FromMilliseconds(1000),
        };
        await using var cm = Create(transport, options);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.OnSent += s =>
        {
            if (s.Contains("\"ping\""))
            {
                conn.ServerSend("""{"type":"pong","ts":1}""");
            }
        };
        conn.ServerSend(Welcome);
        await Task.Delay(2500);
        Assert.False(conn.Aborted);
        Assert.Equal(1, transport.Attempts);
        Assert.Equal(ConnectionState.Connected, cm.State);
    }

    [Fact]
    public async Task ProbeReconnectsWhenNoAnswer()
    {
        var transport = new FakeTransport();
        var options = new ConnectionOptions { ProbeTimeout = TimeSpan.FromMilliseconds(200) };
        await using var cm = Create(transport, options);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
        cm.Probe();
        await transport.NextConnectionAsync(TimeSpan.FromSeconds(3));
        Assert.True(conn.Aborted);
    }

    [Fact]
    public async Task StopClosesNormally()
    {
        var transport = new FakeTransport();
        var cm = Create(transport);
        cm.Start();
        var conn = await transport.NextConnectionAsync();
        conn.ServerSend(Welcome);
        await Wait.UntilAsync(() => cm.State == ConnectionState.Connected);
        await cm.StopAsync();
        Assert.Equal(1000, conn.ClosedWith);
        Assert.Equal(ConnectionState.Stopped, cm.State);
        Assert.Equal(StopReason.User, cm.LastStopReason);
        await Task.Delay(200);
        Assert.Equal(1, transport.Attempts);
    }
}
