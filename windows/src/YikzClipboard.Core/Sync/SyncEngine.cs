using System.Text.Json;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;

namespace YikzClipboard.Core.Sync;

public sealed class SyncContext
{
    public required KeyMaterial Keys { get; init; }
    public required string DeviceId { get; init; }
    public required string SaltB64 { get; init; }
    public required Func<AppSettings> Settings { get; init; }
}

public sealed class SyncEngine
{
    private readonly ApiClient _api;
    private readonly HistoryStore _store;
    private readonly ContentFetcher _fetcher;
    private readonly IClipboardSink _sink;
    private readonly IUserNotifier _notifier;
    private readonly EchoGuard _guard;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly Func<SyncContext?> _context;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    private SyncState _state;
    private bool _catchingUp;
    private readonly List<ItemHeader> _liveDuringCatchUp = new();
    private CancellationTokenSource? _welcomeCts;
    private Task _welcomeTask = Task.CompletedTask;
    private long _lastWrittenSeq;
    private TimeSpan _clockOffset;

    public SyncEngine(
        ApiClient api,
        HistoryStore store,
        ContentFetcher fetcher,
        IClipboardSink sink,
        IUserNotifier notifier,
        EchoGuard guard,
        IClock clock,
        ILog log,
        Func<SyncContext?> context)
    {
        _api = api;
        _store = store;
        _fetcher = fetcher;
        _sink = sink;
        _notifier = notifier;
        _guard = guard;
        _clock = clock;
        _log = log;
        _context = context;
        _state = store.LoadSyncState();
    }

    public int CatchUpPageLimit { get; set; } = ProtocolConstants.HistoryMaxLimit;

    public int MaxMissingFetch { get; set; } = 100;

    public Func<string, string>? DeviceNameResolver { get; set; }

    public TimeSpan ClockOffset
    {
        get
        {
            lock (_gate)
            {
                return _clockOffset;
            }
        }
    }

    public DateTimeOffset ServerNow => _clock.UtcNow + ClockOffset;

    public SyncState StateSnapshot
    {
        get
        {
            lock (_gate)
            {
                return new SyncState
                {
                    ServerId = _state.ServerId,
                    LastSeq = _state.LastSeq,
                    StateRev = _state.StateRev,
                    AppliedSeq = _state.AppliedSeq,
                };
            }
        }
    }

    public bool IsCatchingUp
    {
        get
        {
            lock (_gate)
            {
                return _catchingUp;
            }
        }
    }

    public Task WelcomeTask
    {
        get
        {
            lock (_gate)
            {
                return _welcomeTask;
            }
        }
    }

    public event Action<WelcomeMessage>? Welcomed;
    public event Action<PresenceMessage>? PresenceChanged;
    public event Action? DevicesChanged;
    public event Action<StorageWarningMessage>? StorageWarning;
    public event Action<string>? KeyInvalid;
    public event Action<HistoryEntry, ReceivedContent>? Applied;
    public event Action<bool>? SyncingChanged;
    public event Action<string>? ReleaseAvailable;

    public void ResetState()
    {
        lock (_gate)
        {
            _state = new SyncState();
            _store.SaveSyncState(_state);
            _lastWrittenSeq = 0;
        }
    }

    public void CancelPending()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _welcomeCts;
            _welcomeCts = null;
        }
        cts?.Cancel();
    }

    public void HandleMessage(string type, string json)
    {
        try
        {
            switch (type)
            {
                case WsTypes.Welcome:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.WelcomeMessage);
                        if (msg != null)
                        {
                            OnWelcome(msg);
                        }
                        break;
                    }
                case WsTypes.Clip:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ClipMessage);
                        if (msg?.Item != null)
                        {
                            OnLiveClip(msg.Item);
                        }
                        break;
                    }
                case WsTypes.ClipDeleted:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ClipDeletedMessage);
                        if (msg != null)
                        {
                            _store.Delete(msg.Ids);
                            OnStateRevEvent(msg.StateRev);
                        }
                        break;
                    }
                case WsTypes.ClipPinned:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ClipPinnedMessage);
                        if (msg != null)
                        {
                            _store.SetPinned(msg.Id, msg.Pinned);
                            OnStateRevEvent(msg.StateRev);
                        }
                        break;
                    }
                case WsTypes.Presence:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PresenceMessage);
                        if (msg != null)
                        {
                            PresenceChanged?.Invoke(msg);
                        }
                        break;
                    }
                case WsTypes.DevicesChanged:
                    DevicesChanged?.Invoke();
                    break;
                case WsTypes.StorageWarning:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.StorageWarningMessage);
                        if (msg != null)
                        {
                            StorageWarning?.Invoke(msg);
                        }
                        break;
                    }
                case WsTypes.ReleaseAvailable:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ReleaseAvailableMessage);
                        if (msg != null)
                        {
                            ReleaseAvailable?.Invoke(msg.Version);
                        }
                        break;
                    }
                case WsTypes.Error:
                    {
                        var msg = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.WsErrorMessage);
                        _log.Warn("sync", "server reported " + msg?.Code + ": " + msg?.Message);
                        break;
                    }
            }
        }
        catch (JsonException ex)
        {
            _log.Warn("sync", "could not parse " + type + " message", ex);
        }
        catch (Exception ex)
        {
            _log.Error("sync", "failed to handle " + type, ex);
        }
    }

    private void OnWelcome(WelcomeMessage welcome)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _welcomeCts?.Cancel();
            _welcomeCts = cts = new CancellationTokenSource();
            _catchingUp = true;
            _liveDuringCatchUp.Clear();
            _clockOffset = welcome.ServerTime - _clock.UtcNow;
        }
        _log.Info("sync", $"welcome: server seq {welcome.CurrentSeq}, state_rev {welcome.StateRev}, {welcome.OnlineDevices.Count} online");
        try
        {
            Welcomed?.Invoke(welcome);
        }
        catch
        {
        }
        var previous = WelcomeTask;
        var task = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
            }
            await RunWelcomeAsync(welcome, cts.Token).ConfigureAwait(false);
        });
        lock (_gate)
        {
            _welcomeTask = task;
        }
    }

    private async Task RunWelcomeAsync(WelcomeMessage welcome, CancellationToken ct)
    {
        SyncingChanged?.Invoke(true);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await CatchUpAsync(welcome, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (ApiException ex) when (ex.Status == 401)
                {
                    return;
                }
                catch (Exception ex) when (attempt < 6)
                {
                    var delay = RetryPolicy.HttpDelay(attempt);
                    _log.Warn("sync", $"catch-up failed, retrying in {delay.TotalSeconds:0.0}s: {ex.Message}");
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("sync", "catch-up failed, waiting for the next connection", ex);
                    return;
                }
            }
        }
        finally
        {
            SyncingChanged?.Invoke(false);
        }
    }

    private async Task CatchUpAsync(WelcomeMessage welcome, CancellationToken ct)
    {
        var ctx = _context() ?? throw new InvalidOperationException("not signed in");
        SyncState state;
        lock (_gate)
        {
            state = _state;
        }
        if (state.ServerId != null && !string.Equals(state.ServerId, welcome.ServerId, StringComparison.Ordinal))
        {
            _log.Warn("sync", "server id changed, clearing local history");
            _store.Clear();
            lock (_gate)
            {
                _state = new SyncState { ServerId = null };
                _lastWrittenSeq = 0;
                _store.SaveSyncState(_state);
            }
            var me = await _api.GetMeAsync(ct).ConfigureAwait(false);
            if (!string.Equals(me.Salt, ctx.SaltB64, StringComparison.Ordinal) ||
                (me.KeyCheck != null && !string.Equals(me.KeyCheck, ctx.Keys.KeyCheck, StringComparison.Ordinal)))
            {
                _log.Warn("sync", "encryption salt or key check changed on the server");
                KeyInvalid?.Invoke("The server was reset. Enter the encryption password again.");
                return;
            }
            if (me.KeyCheck == null)
            {
                try
                {
                    await _api.PutKeyCheckAsync(ctx.Keys.KeyCheck, ct).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.Code == ErrorCodes.KeyCheckExists)
                {
                    KeyInvalid?.Invoke("Another device set a different encryption password.");
                    return;
                }
            }
        }
        lock (_gate)
        {
            _state.ServerId = welcome.ServerId;
            _store.SaveSyncState(_state);
            state = _state;
        }
        var inserted = new List<ItemHeader>();
        var initial = state.LastSeq == 0;
        if (initial)
        {
            await InitialSyncAsync(welcome.CurrentSeq, ctx, ctx.Settings().LocalHistoryLimit, ct).ConfigureAwait(false);
            lock (_gate)
            {
                var liveMax = AutoApplyPolicy.HighestSeq(_liveDuringCatchUp, 0);
                _state.LastSeq = Math.Max(welcome.CurrentSeq, liveMax);
                _state.AppliedSeq = Math.Max(_state.AppliedSeq, welcome.CurrentSeq);
                _store.SaveSyncState(_state);
            }
        }
        else if (welcome.CurrentSeq > state.LastSeq)
        {
            var cursor = state.LastSeq;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _api.GetHistoryAsync(after: cursor, limit: CatchUpPageLimit, ct: ct).ConfigureAwait(false);
                if (page.Items.Count == 0)
                {
                    break;
                }
                var entries = page.Items.Select(h => ItemCodec.Decrypt(h, ctx.Keys)).ToList();
                _store.UpsertMany(entries);
                foreach (var h in page.Items)
                {
                    _guard.Remember(h.ContentHash);
                }
                inserted.AddRange(page.Items);
                cursor = page.Items[^1].Seq;
                if (!page.HasMore)
                {
                    break;
                }
            }
            lock (_gate)
            {
                var liveMax = AutoApplyPolicy.HighestSeq(_liveDuringCatchUp, 0);
                _state.LastSeq = Math.Max(Math.Max(cursor, liveMax), _state.LastSeq);
                _store.SaveSyncState(_state);
            }
            _log.Info("sync", $"caught up {inserted.Count} items, last_seq {StateSnapshot.LastSeq}");
        }
        bool needReconcile;
        lock (_gate)
        {
            needReconcile = _state.StateRev == null || _state.StateRev.Value != welcome.StateRev;
        }
        if (needReconcile)
        {
            await ReconcileAsync(ct).ConfigureAwait(false);
        }
        ItemHeader? toApply;
        List<ItemHeader> candidates;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            candidates = new List<ItemHeader>(inserted);
            candidates.AddRange(_liveDuringCatchUp);
            _liveDuringCatchUp.Clear();
            _catchingUp = false;
            toApply = AutoApplyPolicy.SelectAfterCatchUp(candidates, ctx.DeviceId, _state.AppliedSeq, _clock.UtcNow + _clockOffset);
            _state.AppliedSeq = AutoApplyPolicy.HighestSeq(candidates, _state.AppliedSeq);
            _state.LastSeq = AutoApplyPolicy.HighestSeq(candidates, _state.LastSeq);
            _store.SaveSyncState(_state);
        }
        if (toApply != null)
        {
            var entry = _store.Get(toApply.Id);
            if (entry != null)
            {
                _ = ApplyAsync(entry, toApply.Payload != null ? StrictBase64.Decode(toApply.Payload) : null, false, CancellationToken.None);
            }
        }
    }

    private async Task InitialSyncAsync(long currentSeq, SyncContext ctx, int maxItems, CancellationToken ct)
    {
        if (currentSeq <= 0)
        {
            return;
        }
        var before = currentSeq + 1;
        var total = 0;
        while (total < maxItems)
        {
            ct.ThrowIfCancellationRequested();
            var limit = Math.Min(CatchUpPageLimit, maxItems - total);
            var page = await _api.GetHistoryAsync(before: before, limit: limit, ct: ct).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }
            var entries = page.Items.Select(h => ItemCodec.Decrypt(h, ctx.Keys)).ToList();
            _store.UpsertMany(entries);
            total += page.Items.Count;
            before = page.Items.Min(h => h.Seq);
            if (!page.HasMore)
            {
                break;
            }
        }
        _log.Info("sync", $"initial sync loaded {total} items");
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        var ctx = _context();
        if (ctx == null)
        {
            return;
        }
        await _reconcileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await _api.GetHistoryIndexAsync(ct).ConfigureAwait(false);
            var local = _store.Index();
            var serverIds = new HashSet<string>(index.Items.Select(i => i.Id), StringComparer.Ordinal);
            var toDelete = local.Where(kv => !serverIds.Contains(kv.Key) && kv.Value.Seq <= index.CurrentSeq).Select(kv => kv.Key).ToList();
            if (toDelete.Count > 0)
            {
                _store.Delete(toDelete);
            }
            var pins = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var item in index.Items)
            {
                if (local.TryGetValue(item.Id, out var l) && l.Pinned != item.Pinned)
                {
                    pins[item.Id] = item.Pinned;
                }
            }
            if (pins.Count > 0)
            {
                _store.SetPinnedMany(pins);
            }
            var minSeq = local.Count > 0 ? local.Values.Min(v => v.Seq) : long.MaxValue;
            var missing = index.Items
                .Where(i => !local.ContainsKey(i.Id) && (i.Seq >= minSeq || i.Pinned))
                .OrderByDescending(i => i.Seq)
                .Take(MaxMissingFetch)
                .ToList();
            foreach (var m in missing)
            {
                try
                {
                    var header = await _api.GetItemAsync(m.Id, ct).ConfigureAwait(false);
                    var entry = ItemCodec.Decrypt(header, ctx.Keys);
                    _store.Upsert(entry, header.Payload != null && StrictBase64.TryDecode(header.Payload, out var p) ? p : null);
                }
                catch (ApiException ex) when (ex.Status == 404)
                {
                }
            }
            lock (_gate)
            {
                _state.StateRev = index.StateRev;
                _store.SaveSyncState(_state);
            }
            _log.Info("sync", $"reconciled: removed {toDelete.Count}, pin changes {pins.Count}, fetched {missing.Count}, state_rev {index.StateRev}");
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void OnStateRevEvent(long eventRev)
    {
        StateRevAction action;
        lock (_gate)
        {
            action = StateRevPolicy.OnEvent(_state.StateRev, eventRev);
            if (action == StateRevAction.Store)
            {
                _state.StateRev = eventRev;
                _store.SaveSyncState(_state);
            }
            if (_catchingUp && action == StateRevAction.Reconcile)
            {
                action = StateRevAction.Ignore;
            }
        }
        if (action == StateRevAction.Reconcile)
        {
            _log.Info("sync", "missed state changes, reconciling");
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("sync", "reconcile failed", ex);
                }
            });
        }
    }

    private void OnLiveClip(ItemHeader header)
    {
        var ctx = _context();
        if (ctx == null)
        {
            return;
        }
        var entry = ItemCodec.Decrypt(header, ctx.Keys);
        byte[]? sealedPayload = null;
        if (header.Payload != null && !StrictBase64.TryDecode(header.Payload, out sealedPayload))
        {
            _log.Warn("sync", "invalid payload encoding for " + header.Id);
            sealedPayload = null;
        }
        _store.Upsert(entry, sealedPayload);
        _guard.Remember(header.ContentHash);
        bool eligible;
        lock (_gate)
        {
            if (_catchingUp)
            {
                _liveDuringCatchUp.Add(header);
                return;
            }
            _state.LastSeq = Math.Max(_state.LastSeq, header.Seq);
            eligible = AutoApplyPolicy.IsEligible(header, ctx.DeviceId, _state.AppliedSeq, _clock.UtcNow + _clockOffset);
            _state.AppliedSeq = Math.Max(_state.AppliedSeq, header.Seq);
            _store.SaveSyncState(_state);
        }
        if (eligible)
        {
            _ = ApplyAsync(entry, sealedPayload, false, CancellationToken.None);
        }
    }

    public async Task<bool> ApplyAsync(HistoryEntry entry, byte[]? sealedPayload, bool manual, CancellationToken ct)
    {
        var ctx = _context();
        if (ctx == null || entry.Meta == null)
        {
            return false;
        }
        var settings = ctx.Settings();
        if (!manual)
        {
            if (!settings.AutoApply)
            {
                return false;
            }
            var kindAllowed = entry.Header.Kind switch
            {
                ProtocolConstants.KindText => settings.SyncText,
                ProtocolConstants.KindImage => settings.SyncImages,
                ProtocolConstants.KindFiles => settings.SyncFiles,
                _ => false,
            };
            if (!kindAllowed)
            {
                _log.Info("apply", "skipping " + entry.Header.Kind + " item, receiving this kind is off");
                return false;
            }
            if (entry.Header.ChunkCount > 0 && entry.Header.Size > settings.AutoDownloadLimitBytes)
            {
                _log.Info("apply", $"item {entry.Id} is {HistoryEntry.FormatSize(entry.Header.Size)}, above the auto-download limit");
                _notifier.LargeItemAvailable(entry, DeviceNameResolver?.Invoke(entry.Header.DeviceId) ?? "another device");
                return false;
            }
        }
        try
        {
            using var fetched = await _fetcher.FetchAsync(entry, ctx.Keys, sealedPayload, null, ct).ConfigureAwait(false);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!manual && entry.Seq < _lastWrittenSeq)
                {
                    _log.Info("apply", $"skipping {entry.Id}, a newer item was applied meanwhile");
                    return false;
                }
                var content = _fetcher.Materialize(entry, fetched);
                _guard.Remember(entry.Header.ContentHash);
                await _sink.WriteAsync(content, ct).ConfigureAwait(false);
                _lastWrittenSeq = Math.Max(_lastWrittenSeq, entry.Seq);
                _log.Info("apply", $"placed {entry.KindLabel.ToLowerInvariant()} item seq {entry.Seq} on the clipboard");
                try
                {
                    Applied?.Invoke(entry, content);
                }
                catch
                {
                }
                return true;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (CryptoException ex)
        {
            _log.Error("apply", "item " + entry.Id + " failed verification, discarded", ex);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("apply", "could not apply item " + entry.Id, ex);
            if (manual)
            {
                throw;
            }
            return false;
        }
    }
}
