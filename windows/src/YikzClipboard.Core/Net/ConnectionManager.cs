using System.Diagnostics;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Protocol;

namespace YikzClipboard.Core.Net;

public enum ConnectionState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
}

public enum StopReason
{
    None,
    User,
    AuthLost,
    UpdateRequired,
    TooManyConnections,
    NoCredentials,
}

public sealed record ConnectTarget(Uri Uri, string Token, Func<string> HelloJson);

public sealed class ConnectionOptions
{
    public TimeSpan HeartbeatInterval { get; init; } = ProtocolConstants.HeartbeatInterval;
    public TimeSpan DeadTimeout { get; init; } = ProtocolConstants.DeadConnectionTimeout;
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan? WatchdogTick { get; init; }
}

public sealed class ReconnectBackoff
{
    private readonly Random _random;
    private readonly object _gate = new();
    private int _attempt;

    public ReconnectBackoff(Random? random = null, double baseSeconds = 0.5, double maxSeconds = 30)
    {
        _random = random ?? Random.Shared;
        BaseSeconds = baseSeconds;
        MaxSeconds = maxSeconds;
    }

    public double BaseSeconds { get; }

    public double MaxSeconds { get; }

    public int Attempt
    {
        get
        {
            lock (_gate)
            {
                return _attempt;
            }
        }
    }

    public TimeSpan Ceiling(int attempt)
    {
        var exp = Math.Min(attempt, 30);
        return TimeSpan.FromSeconds(Math.Min(MaxSeconds, BaseSeconds * Math.Pow(2, exp)));
    }

    public TimeSpan NextDelay()
    {
        lock (_gate)
        {
            var ceiling = Ceiling(_attempt).TotalSeconds;
            _attempt++;
            return TimeSpan.FromSeconds(_random.NextDouble() * ceiling);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _attempt = 0;
        }
    }
}

public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly IWebSocketTransport _transport;
    private readonly Func<ConnectTarget?> _targetProvider;
    private readonly ConnectionOptions _options;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource _wakeCts = new();
    private CancellationTokenSource? _sessionCts;
    private IWebSocketConnection? _connection;
    private Task? _runTask;
    private bool _immediate;
    private long _lastReceivedMs;
    private long _receivedCount;
    private ConnectionState _state = ConnectionState.Idle;
    private StopReason _stopReason = StopReason.None;

    public ConnectionManager(IWebSocketTransport transport, Func<ConnectTarget?> targetProvider, ConnectionOptions? options = null, ReconnectBackoff? backoff = null, ILog? log = null)
    {
        _transport = transport;
        _targetProvider = targetProvider;
        _options = options ?? new ConnectionOptions();
        Backoff = backoff ?? new ReconnectBackoff();
        _log = log ?? NullLog.Instance;
    }

    public ReconnectBackoff Backoff { get; }

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public StopReason LastStopReason
    {
        get
        {
            lock (_gate)
            {
                return _stopReason;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runTask != null && !_runTask.IsCompleted;
            }
        }
    }

    public int SessionCount { get; private set; }

    public event Action<ConnectionState>? StateChanged;

    public event Action<string, string>? MessageReceived;

    public event Action<StopReason>? Stopped;

    public event Action<TimeSpan>? WaitingToReconnect;

    public void Start()
    {
        lock (_gate)
        {
            if (_runTask != null && !_runTask.IsCompleted)
            {
                return;
            }
            _stopCts?.Dispose();
            _stopCts = new CancellationTokenSource();
            _stopReason = StopReason.None;
            _immediate = true;
            Backoff.Reset();
            var token = _stopCts.Token;
            _runTask = Task.Run(() => RunLoopAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? run;
        IWebSocketConnection? conn;
        lock (_gate)
        {
            run = _runTask;
            conn = _connection;
            _stopCts?.Cancel();
        }
        if (conn != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await conn.CloseAsync(WsCloseCodes.Normal, "client stopping", cts.Token).ConfigureAwait(false);
            conn.Abort();
        }
        if (run != null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        Finish(StopReason.User);
    }

    public void ReconnectNow(string reason)
    {
        bool restart;
        lock (_gate)
        {
            var running = _runTask != null && !_runTask.IsCompleted;
            restart = !running && _stopReason == StopReason.TooManyConnections;
            if (!running && !restart)
            {
                return;
            }
        }
        _log.Info("ws", "reconnect now: " + reason);
        if (restart)
        {
            Start();
            return;
        }
        Backoff.Reset();
        CancellationTokenSource? session;
        IWebSocketConnection? conn;
        lock (_gate)
        {
            _immediate = true;
            var wake = _wakeCts;
            _wakeCts = new CancellationTokenSource();
            wake.Cancel();
            session = _sessionCts;
            conn = _connection;
        }
        try
        {
            session?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        conn?.Abort();
    }

    public void Probe()
    {
        IWebSocketConnection? conn;
        lock (_gate)
        {
            conn = _state == ConnectionState.Connected ? _connection : null;
            if (_runTask == null || _runTask.IsCompleted)
            {
                if (_stopReason != StopReason.TooManyConnections)
                {
                    return;
                }
            }
        }
        if (conn == null)
        {
            ReconnectNow("probe while disconnected");
            return;
        }
        var countAtProbe = Interlocked.Read(ref _receivedCount);
        _ = Task.Run(async () =>
        {
            try
            {
                await conn.SendAsync(WsCodec.Ping(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            await Task.Delay(_options.ProbeTimeout).ConfigureAwait(false);
            if (Interlocked.Read(ref _receivedCount) == countAtProbe)
            {
                ReconnectNow("probe timeout");
            }
        });
    }

    public async Task<bool> SendAsync(string json, CancellationToken ct = default)
    {
        IWebSocketConnection? conn;
        lock (_gate)
        {
            conn = _state == ConnectionState.Connected ? _connection : null;
        }
        if (conn == null)
        {
            return false;
        }
        try
        {
            await conn.SendAsync(json, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("ws", "send failed", ex);
            return false;
        }
    }

    private long NowMs() => _clock.ElapsedMilliseconds;

    private void SetState(ConnectionState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _log.Error("ws", "state handler failed", ex);
        }
    }

    private void Finish(StopReason reason)
    {
        bool raise;
        lock (_gate)
        {
            raise = _state != ConnectionState.Stopped || _stopReason != reason;
            if (_stopReason == StopReason.None || reason != StopReason.User)
            {
                _stopReason = reason;
            }
        }
        SetState(ConnectionState.Stopped);
        if (raise)
        {
            try
            {
                Stopped?.Invoke(reason);
            }
            catch (Exception ex)
            {
                _log.Error("ws", "stop handler failed", ex);
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            bool immediate;
            CancellationToken wake;
            lock (_gate)
            {
                immediate = _immediate;
                _immediate = false;
                wake = _wakeCts.Token;
            }
            if (!immediate)
            {
                var delay = Backoff.NextDelay();
                SetState(ConnectionState.Reconnecting);
                _log.Debug("ws", $"reconnecting in {delay.TotalSeconds:0.00}s (attempt {Backoff.Attempt})");
                try
                {
                    WaitingToReconnect?.Invoke(delay);
                }
                catch
                {
                }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, wake);
                try
                {
                    await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                lock (_gate)
                {
                    _immediate = false;
                }
                if (stop.IsCancellationRequested)
                {
                    break;
                }
            }
            var target = _targetProvider();
            if (target == null)
            {
                _log.Warn("ws", "no credentials, not connecting");
                Finish(StopReason.NoCredentials);
                return;
            }
            SetState(ConnectionState.Connecting);
            var outcome = await RunSessionAsync(target, stop).ConfigureAwait(false);
            if (stop.IsCancellationRequested)
            {
                break;
            }
            switch (outcome)
            {
                case StopReason.AuthLost:
                case StopReason.UpdateRequired:
                case StopReason.TooManyConnections:
                    Finish(outcome);
                    return;
            }
        }
        Finish(StopReason.User);
    }

    private async Task<StopReason> RunSessionAsync(ConnectTarget target, CancellationToken stop)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_gate)
        {
            _sessionCts = sessionCts;
        }
        IWebSocketConnection conn;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
            connectCts.CancelAfter(_options.ConnectTimeout);
            conn = await _transport.ConnectAsync(target.Uri, target.Token, connectCts.Token).ConfigureAwait(false);
        }
        catch (WsUnauthorizedException)
        {
            _log.Warn("ws", "server rejected the device token (401)");
            ClearSession(sessionCts);
            return StopReason.AuthLost;
        }
        catch (Exception ex)
        {
            if (!stop.IsCancellationRequested)
            {
                _log.Warn("ws", "connect failed: " + ex.Message);
            }
            ClearSession(sessionCts);
            return StopReason.None;
        }
        SessionCount++;
        var dead = false;
        int? closeCode = null;
        string? closeReason = null;
        await using (conn.ConfigureAwait(false))
        {
            lock (_gate)
            {
                _connection = conn;
            }
            Interlocked.Exchange(ref _lastReceivedMs, NowMs());
            Task heartbeat = Task.CompletedTask;
            try
            {
                await conn.SendAsync(target.HelloJson(), sessionCts.Token).ConfigureAwait(false);
                heartbeat = HeartbeatLoopAsync(conn, sessionCts, () => dead = true);
                while (true)
                {
                    var result = await conn.ReceiveAsync(sessionCts.Token).ConfigureAwait(false);
                    if (result.IsClosed)
                    {
                        closeCode = result.CloseCode;
                        closeReason = result.CloseReason;
                        break;
                    }
                    Interlocked.Exchange(ref _lastReceivedMs, NowMs());
                    Interlocked.Increment(ref _receivedCount);
                    HandleIncoming(conn, result.Text!);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!stop.IsCancellationRequested)
                {
                    _log.Warn("ws", "connection error: " + ex.Message);
                }
            }
            finally
            {
                try
                {
                    sessionCts.Cancel();
                }
                catch
                {
                }
                lock (_gate)
                {
                    if (ReferenceEquals(_connection, conn))
                    {
                        _connection = null;
                    }
                }
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch
                {
                }
                if (stop.IsCancellationRequested)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await conn.CloseAsync(WsCloseCodes.Normal, "bye", closeCts.Token).ConfigureAwait(false);
                }
                conn.Abort();
            }
        }
        ClearSession(sessionCts);
        if (dead)
        {
            _log.Warn("ws", "heartbeat timeout, no message for " + _options.DeadTimeout.TotalSeconds + "s");
            return StopReason.None;
        }
        if (closeCode.HasValue)
        {
            _log.Info("ws", $"closed by server: {closeCode} {closeReason}");
        }
        return closeCode switch
        {
            WsCloseCodes.Unauthorized => StopReason.AuthLost,
            WsCloseCodes.ProtocolUnsupported => StopReason.UpdateRequired,
            WsCloseCodes.TooManyConnections => StopReason.TooManyConnections,
            _ => StopReason.None,
        };
    }

    private void ClearSession(CancellationTokenSource session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_sessionCts, session))
            {
                _sessionCts = null;
            }
        }
    }

    private void HandleIncoming(IWebSocketConnection conn, string text)
    {
        var type = WsCodec.PeekType(text);
        if (type == null)
        {
            _log.Warn("ws", "ignoring invalid message");
            return;
        }
        if (type == WsTypes.Ping)
        {
            var ts = WsCodec.ReadTs(text);
            _ = SendQuietlyAsync(conn, WsCodec.Pong(ts));
        }
        else if (type == WsTypes.Welcome)
        {
            Backoff.Reset();
            SetState(ConnectionState.Connected);
        }
        else if (type == WsTypes.Error)
        {
            _log.Warn("ws", "server error message: " + text);
        }
        try
        {
            MessageReceived?.Invoke(type, text);
        }
        catch (Exception ex)
        {
            _log.Error("ws", "message handler failed for " + type, ex);
        }
    }

    private async Task SendQuietlyAsync(IWebSocketConnection conn, string json)
    {
        try
        {
            await conn.SendAsync(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug("ws", "send failed: " + ex.Message);
        }
    }

    private async Task HeartbeatLoopAsync(IWebSocketConnection conn, CancellationTokenSource session, Action markDead)
    {
        var interval = _options.HeartbeatInterval;
        var deadAfter = _options.DeadTimeout;
        var tick = _options.WatchdogTick ?? TimeSpan.FromMilliseconds(Math.Max(20, Math.Min(1000, Math.Min(interval.TotalMilliseconds / 4, deadAfter.TotalMilliseconds / 10))));
        var nextPing = NowMs() + (long)interval.TotalMilliseconds;
        var token = session.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tick, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var now = NowMs();
            if (now - Interlocked.Read(ref _lastReceivedMs) >= (long)deadAfter.TotalMilliseconds)
            {
                markDead();
                conn.Abort();
                try
                {
                    session.Cancel();
                }
                catch
                {
                }
                return;
            }
            if (now >= nextPing)
            {
                nextPing = now + (long)interval.TotalMilliseconds;
                await SendQuietlyAsync(conn, WsCodec.Ping(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wakeCts.Dispose();
        _stopCts?.Dispose();
    }
}
