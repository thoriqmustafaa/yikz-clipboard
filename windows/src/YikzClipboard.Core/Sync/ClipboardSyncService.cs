using System.Text;
using System.Threading.Channels;
using YikzClipboard.Core.Content;
using YikzClipboard.Core.Crypto;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Net;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;

namespace YikzClipboard.Core.Sync;

public enum ServiceStatus
{
    SignedOut,
    NeedsEncryptionPassword,
    Connecting,
    Connected,
    Reconnecting,
    Paused,
    UpdateRequired,
    TooManyConnections,
}

public enum KeySetupResult
{
    Accepted,
    WrongPassword,
    Failed,
}

public sealed record LoginOutcome(bool Success, bool NeedsEncryptionPassword, string? Error);

public sealed record KeySetupOutcome(KeySetupResult Result, string? Error);

public sealed class ClipboardSyncService : IAsyncDisposable
{
    public const string TokenSecret = "device-token";
    public const string KeySecret = "encryption-key";
    public const string SaltSecret = "account-salt";

    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly ISecureStore _secrets;
    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly IUserNotifier _notifier;
    private readonly ApiClient _api;
    private readonly HistoryStore _store;
    private readonly ContentFetcher _fetcher;
    private readonly Uploader _uploader;
    private readonly SyncEngine _engine;
    private readonly ConnectionManager _connection;
    private readonly EchoGuard _guard = new();
    private readonly Channel<(LocalClip Clip, bool Force)> _outgoing = Channel.CreateBounded<(LocalClip, bool)>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, OnlineDevice> _online = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceInfo> _devices = new(StringComparer.Ordinal);
    private readonly string _appVersion;

    private KeyMaterial? _keys;
    private string? _token;
    private string? _salt;
    private string? _pendingKeyCheck;
    private ServiceStatus _status = ServiceStatus.SignedOut;
    private string? _statusDetail;
    private bool _syncing;
    private StorageWarningMessage? _storageWarning;
    private Task? _outgoingTask;

    public ClipboardSyncService(
        AppPaths paths,
        SettingsStore settings,
        ISecureStore secrets,
        ILog log,
        IClipboardSink sink,
        IUserNotifier notifier,
        IImageTools imageTools,
        string appVersion,
        HttpClient? http = null,
        IWebSocketTransport? transport = null,
        IClock? clock = null,
        HistoryStore? store = null,
        ConnectionOptions? connectionOptions = null)
    {
        _paths = paths;
        _settings = settings;
        _secrets = secrets;
        _log = log;
        _clock = clock ?? SystemClock.Instance;
        _notifier = notifier;
        _appVersion = appVersion;
        if (!ApiClient.TryParseServerUrl(settings.Current.ServerUrl, out var baseUri))
        {
            baseUri = new Uri(AppSettings.DefaultServerUrl + "/");
        }
        _api = new ApiClient(http ?? HttpClientFactory.Create(appVersion), baseUri);
        _api.Unauthorized += OnUnauthorized;
        _store = store ?? new HistoryStore(paths.DatabaseFile);
        _fetcher = new ContentFetcher(_api, _store, paths, log);
        _uploader = new Uploader(_api, imageTools, log, EnsureKeyCheckAsync);
        _engine = new SyncEngine(_api, _store, _fetcher, sink, notifier, _guard, _clock, log, CurrentContext)
        {
            DeviceNameResolver = DeviceName,
        };
        _engine.Welcomed += OnWelcomed;
        _engine.PresenceChanged += OnPresence;
        _engine.DevicesChanged += () => _ = RefreshDevicesAsync();
        _engine.StorageWarning += w =>
        {
            lock (_gate)
            {
                _storageWarning = w.Active ? w : null;
            }
            StorageWarningChanged?.Invoke(w.Active ? w : null);
        };
        _engine.KeyInvalid += OnKeyInvalid;
        _engine.SyncingChanged += s =>
        {
            lock (_gate)
            {
                _syncing = s;
            }
            RaiseStatus();
        };
        _engine.Applied += (e, c) => ItemApplied?.Invoke(e, c);
        _connection = new ConnectionManager(
            transport ?? new ClientWebSocketTransport("YikzClipboard-Windows/" + appVersion),
            BuildTarget,
            connectionOptions,
            new ReconnectBackoff(),
            log);
        _connection.MessageReceived += _engine.HandleMessage;
        _connection.StateChanged += _ => RaiseStatus();
        _connection.Stopped += OnConnectionStopped;
        _connection.WaitingToReconnect += d => ReconnectScheduled?.Invoke(d);
        _store.Changed += () => HistoryChanged?.Invoke();
    }

    public HistoryStore History => _store;

    public ApiClient Api => _api;

    public SyncEngine Engine => _engine;

    public ConnectionManager Connection => _connection;

    public EchoGuard Guard => _guard;

    public AppSettings Settings => _settings.Current;

    public SettingsStore SettingsStore => _settings;

    public string? DeviceId => _settings.Current.DeviceId;

    public bool IsSignedIn
    {
        get
        {
            lock (_gate)
            {
                return _token != null;
            }
        }
    }

    public bool HasKey
    {
        get
        {
            lock (_gate)
            {
                return _keys != null;
            }
        }
    }

    public bool IsSyncing
    {
        get
        {
            lock (_gate)
            {
                return _syncing;
            }
        }
    }

    public StorageWarningMessage? ActiveStorageWarning
    {
        get
        {
            lock (_gate)
            {
                return _storageWarning;
            }
        }
    }

    public ServiceStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public string? StatusDetail
    {
        get
        {
            lock (_gate)
            {
                return _statusDetail;
            }
        }
    }

    public event Action<ServiceStatus>? StatusChanged;
    public event Action? HistoryChanged;
    public event Action? DevicesChanged;
    public event Action<StorageWarningMessage?>? StorageWarningChanged;
    public event Action<HistoryEntry, ReceivedContent>? ItemApplied;
    public event Action<HistoryEntry>? ItemSent;
    public event Action<TimeSpan>? ReconnectScheduled;

    public void Start()
    {
        _outgoingTask ??= Task.Run(() => OutgoingLoopAsync(_disposeCts.Token));
        _ = Task.Run(() => _fetcher.PruneReceived(TimeSpan.FromDays(7), 2L * 1024 * 1024 * 1024));
        var tokenBytes = _secrets.Get(TokenSecret);
        var keyBytes = _secrets.Get(KeySecret);
        var saltBytes = _secrets.Get(SaltSecret);
        lock (_gate)
        {
            _token = tokenBytes != null ? Encoding.UTF8.GetString(tokenBytes) : null;
            _salt = saltBytes != null ? Encoding.UTF8.GetString(saltBytes) : null;
            if (keyBytes != null && keyBytes.Length == ProtocolConstants.KeyLength)
            {
                _keys = new KeyMaterial(keyBytes);
            }
            if (_token != null && !DeviceToken.IsValid(_token))
            {
                _token = null;
            }
        }
        _api.Token = _token;
        if (_token == null)
        {
            SetStatus(ServiceStatus.SignedOut, null);
            return;
        }
        if (_keys == null || _salt == null)
        {
            SetStatus(ServiceStatus.NeedsEncryptionPassword, null);
            return;
        }
        StartSync();
    }

    private void StartSync()
    {
        if (_settings.Current.Paused)
        {
            SetStatus(ServiceStatus.Paused, null);
            return;
        }
        SetStatus(ServiceStatus.Connecting, null);
        _connection.Start();
        _ = RefreshDevicesAsync();
    }

    private ConnectTarget? BuildTarget()
    {
        string? token;
        lock (_gate)
        {
            token = _token;
        }
        var deviceId = _settings.Current.DeviceId;
        if (token == null || deviceId == null || !HasKey)
        {
            return null;
        }
        return new ConnectTarget(_api.WebSocketUri(), token, () => WsCodec.Hello(new HelloMessage
        {
            DeviceId = deviceId,
            LastSeq = _engine.StateSnapshot.LastSeq,
            AppVersion = _appVersion,
            Platform = ProtocolConstants.Platform,
        }));
    }

    private SyncContext? CurrentContext()
    {
        KeyMaterial? keys;
        string? salt;
        lock (_gate)
        {
            keys = _keys;
            salt = _salt;
        }
        var deviceId = _settings.Current.DeviceId;
        if (keys == null || deviceId == null || salt == null)
        {
            return null;
        }
        return new SyncContext
        {
            Keys = keys,
            DeviceId = deviceId,
            SaltB64 = salt,
            Settings = () => _settings.Current,
        };
    }

    private void SetStatus(ServiceStatus status, string? detail)
    {
        lock (_gate)
        {
            _status = status;
            _statusDetail = detail;
        }
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            _log.Error("service", "status handler failed", ex);
        }
    }

    private void RaiseStatus()
    {
        ServiceStatus status;
        string? detail;
        lock (_gate)
        {
            if (_status is ServiceStatus.SignedOut or ServiceStatus.NeedsEncryptionPassword or ServiceStatus.Paused or ServiceStatus.UpdateRequired)
            {
                status = _status;
                detail = _statusDetail;
            }
            else
            {
                status = _connection.State switch
                {
                    ConnectionState.Connected => ServiceStatus.Connected,
                    ConnectionState.Connecting => ServiceStatus.Connecting,
                    ConnectionState.Reconnecting => ServiceStatus.Reconnecting,
                    ConnectionState.Stopped when _connection.LastStopReason == StopReason.TooManyConnections => ServiceStatus.TooManyConnections,
                    ConnectionState.Stopped when _connection.LastStopReason == StopReason.UpdateRequired => ServiceStatus.UpdateRequired,
                    _ => _status,
                };
                detail = _syncing && status == ServiceStatus.Connected ? "Syncing history" : null;
            }
        }
        SetStatus(status, detail);
    }

    public string StatusText
    {
        get
        {
            return Status switch
            {
                ServiceStatus.SignedOut => "Signed out",
                ServiceStatus.NeedsEncryptionPassword => "Encryption password needed",
                ServiceStatus.Connecting => "Connecting",
                ServiceStatus.Connected => IsSyncing ? "Syncing" : "Connected",
                ServiceStatus.Reconnecting => "Offline, retrying",
                ServiceStatus.Paused => "Sync paused",
                ServiceStatus.UpdateRequired => "Update required",
                ServiceStatus.TooManyConnections => "Disconnected (too many connections)",
                _ => "Unknown",
            };
        }
    }

    public async Task<LoginOutcome> LoginAsync(string serverUrl, string username, string password, string deviceName, CancellationToken ct = default)
    {
        if (!ApiClient.TryParseServerUrl(serverUrl, out var baseUri))
        {
            return new LoginOutcome(false, false, "Enter a valid server URL.");
        }
        var trimmedName = deviceName.Trim();
        if (trimmedName.Length == 0)
        {
            trimmedName = Environment.MachineName;
        }
        if (TextPreview.CountCodePoints(trimmedName) > 64)
        {
            trimmedName = TextPreview.Make(trimmedName, 64);
        }
        await _connection.StopAsync().ConfigureAwait(false);
        var serverChanged = !string.Equals(_api.BaseUri.ToString(), baseUri.ToString(), StringComparison.OrdinalIgnoreCase);
        _api.BaseUri = baseUri;
        LoginResponse response;
        try
        {
            response = await _api.LoginAsync(new LoginRequest
            {
                Username = username.Trim(),
                Password = password,
                DeviceName = trimmedName,
                Platform = ProtocolConstants.Platform,
                DeviceId = serverChanged ? null : _settings.Current.DeviceId,
            }, ct).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            var message = ex.Code switch
            {
                ErrorCodes.InvalidCredentials => "Wrong username or password.",
                ErrorCodes.RateLimited => "Too many attempts. Try again in " + Math.Ceiling((ex.RetryAfter ?? TimeSpan.FromMinutes(10)).TotalMinutes) + " min.",
                ErrorCodes.InvalidRequest => "The server rejected the request: " + ex.Message,
                _ when ex.IsNetworkError => "Cannot reach the server. Check the URL and your connection.",
                _ => "Sign in failed: " + ex.Message,
            };
            _log.Warn("auth", "login failed: " + ex.Code);
            return new LoginOutcome(false, false, message);
        }
        if (response.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            return new LoginOutcome(false, false, "This server uses a newer protocol. Update the app.");
        }
        if (response.Kdf.Algorithm != ProtocolConstants.KdfAlgorithm || response.Kdf.KeyLength != ProtocolConstants.KeyLength)
        {
            return new LoginOutcome(false, false, "Unsupported key derivation settings on the server.");
        }
        if (!StrictBase64.TryDecode(response.Salt, out var salt) || salt.Length != ProtocolConstants.SaltLength)
        {
            return new LoginOutcome(false, false, "The server sent an invalid salt.");
        }
        var previousDevice = _settings.Current.DeviceId;
        var previousSalt = _salt;
        _settings.Update(s =>
        {
            s.ServerUrl = baseUri.ToString().TrimEnd('/');
            s.Username = response.Username;
            s.DeviceName = trimmedName;
            s.DeviceId = response.DeviceId;
        });
        _secrets.Set(TokenSecret, Encoding.UTF8.GetBytes(response.Token));
        _secrets.Set(SaltSecret, Encoding.UTF8.GetBytes(response.Salt));
        bool keepKey;
        lock (_gate)
        {
            _token = response.Token;
            keepKey = _keys != null && string.Equals(previousSalt, response.Salt, StringComparison.Ordinal) &&
                response.KeyCheck != null && string.Equals(response.KeyCheck, _keys.KeyCheck, StringComparison.Ordinal);
            _salt = response.Salt;
            _pendingKeyCheck = response.KeyCheck;
            if (!keepKey)
            {
                _keys?.Dispose();
                _keys = null;
            }
        }
        _api.Token = response.Token;
        if (previousDevice != null && previousDevice != response.DeviceId)
        {
            _store.Clear();
            _engine.ResetState();
        }
        _log.Info("auth", "signed in as " + response.Username + " on " + baseUri.Host);
        if (keepKey)
        {
            _secrets.Delete(KeySecret);
            _secrets.Set(KeySecret, _keys!.Key);
            StartSync();
            return new LoginOutcome(true, false, null);
        }
        _secrets.Delete(KeySecret);
        SetStatus(ServiceStatus.NeedsEncryptionPassword, null);
        return new LoginOutcome(true, true, null);
    }

    public async Task<KeySetupOutcome> SetEncryptionPasswordAsync(string encryptionPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(encryptionPassword))
        {
            return new KeySetupOutcome(KeySetupResult.WrongPassword, "Enter the encryption password.");
        }
        string? salt;
        lock (_gate)
        {
            salt = _salt;
        }
        MeResponse me;
        try
        {
            me = await _api.GetMeAsync(ct).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            return new KeySetupOutcome(KeySetupResult.Failed, ex.IsNetworkError ? "Cannot reach the server." : "Server error: " + ex.Message);
        }
        if (me.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            return new KeySetupOutcome(KeySetupResult.Failed, "This server uses a newer protocol. Update the app.");
        }
        if (!StrictBase64.TryDecode(me.Salt, out var saltBytes) || saltBytes.Length != ProtocolConstants.SaltLength)
        {
            return new KeySetupOutcome(KeySetupResult.Failed, "The server sent an invalid salt.");
        }
        if (salt != me.Salt)
        {
            _secrets.Set(SaltSecret, Encoding.UTF8.GetBytes(me.Salt));
            lock (_gate)
            {
                _salt = me.Salt;
            }
        }
        var key = await Task.Run(() => CryptoBox.DeriveKey(encryptionPassword, saltBytes, me.Kdf.Iterations > 0 ? me.Kdf.Iterations : ProtocolConstants.KdfIterations), ct).ConfigureAwait(false);
        var material = new KeyMaterial(key);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        var verdict = await VerifyOrSetKeyCheckAsync(material, me.KeyCheck, ct).ConfigureAwait(false);
        if (verdict.Result != KeySetupResult.Accepted)
        {
            material.Dispose();
            return verdict;
        }
        _secrets.Set(KeySecret, material.Key);
        lock (_gate)
        {
            _keys?.Dispose();
            _keys = material;
            _pendingKeyCheck = material.KeyCheck;
        }
        _log.Info("auth", "encryption key accepted");
        StartSync();
        return verdict;
    }

    private async Task<KeySetupOutcome> VerifyOrSetKeyCheckAsync(KeyMaterial material, string? serverKeyCheck, CancellationToken ct)
    {
        if (serverKeyCheck != null)
        {
            return string.Equals(serverKeyCheck, material.KeyCheck, StringComparison.Ordinal)
                ? new KeySetupOutcome(KeySetupResult.Accepted, null)
                : new KeySetupOutcome(KeySetupResult.WrongPassword, "Wrong encryption password. It must match the one used on your other devices.");
        }
        try
        {
            await _api.PutKeyCheckAsync(material.KeyCheck, ct).ConfigureAwait(false);
            _log.Info("auth", "first device: stored the key check on the server");
            return new KeySetupOutcome(KeySetupResult.Accepted, null);
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.KeyCheckExists)
        {
            try
            {
                var me = await _api.GetMeAsync(ct).ConfigureAwait(false);
                return string.Equals(me.KeyCheck, material.KeyCheck, StringComparison.Ordinal)
                    ? new KeySetupOutcome(KeySetupResult.Accepted, null)
                    : new KeySetupOutcome(KeySetupResult.WrongPassword, "Wrong encryption password. It must match the one used on your other devices.");
            }
            catch (ApiException inner)
            {
                return new KeySetupOutcome(KeySetupResult.Failed, inner.Message);
            }
        }
        catch (ApiException ex)
        {
            return new KeySetupOutcome(KeySetupResult.Failed, ex.IsNetworkError ? "Cannot reach the server." : ex.Message);
        }
    }

    private async Task<bool> EnsureKeyCheckAsync(CancellationToken ct)
    {
        KeyMaterial? keys;
        lock (_gate)
        {
            keys = _keys;
        }
        if (keys == null)
        {
            return false;
        }
        var me = await _api.GetMeAsync(ct).ConfigureAwait(false);
        var verdict = await VerifyOrSetKeyCheckAsync(keys, me.KeyCheck, ct).ConfigureAwait(false);
        if (verdict.Result == KeySetupResult.WrongPassword)
        {
            OnKeyInvalid("The encryption password no longer matches the server.");
        }
        return verdict.Result == KeySetupResult.Accepted;
    }

    public async Task SignOutAsync()
    {
        try
        {
            if (IsSignedIn)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _api.LogoutAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("auth", "logout request failed: " + ex.Message);
        }
        await _connection.StopAsync().ConfigureAwait(false);
        _engine.CancelPending();
        ForgetCredentials(deleteKey: true);
        _store.Clear();
        _engine.ResetState();
        _guard.Clear();
        _log.Info("auth", "signed out");
        SetStatus(ServiceStatus.SignedOut, null);
    }

    private void ForgetCredentials(bool deleteKey)
    {
        _secrets.Delete(TokenSecret);
        if (deleteKey)
        {
            _secrets.Delete(KeySecret);
            _secrets.Delete(SaltSecret);
        }
        lock (_gate)
        {
            _token = null;
            if (deleteKey)
            {
                _keys?.Dispose();
                _keys = null;
                _salt = null;
            }
        }
        _api.Token = null;
    }

    private void OnUnauthorized()
    {
        if (!IsSignedIn)
        {
            return;
        }
        _log.Warn("auth", "device token was rejected, sign in again");
        ForgetCredentials(deleteKey: false);
        _ = Task.Run(async () =>
        {
            await _connection.StopAsync().ConfigureAwait(false);
            SetStatus(ServiceStatus.SignedOut, "This device was signed out. Sign in again.");
        });
    }

    private void OnConnectionStopped(StopReason reason)
    {
        switch (reason)
        {
            case StopReason.AuthLost:
                OnUnauthorized();
                break;
            case StopReason.UpdateRequired:
                SetStatus(ServiceStatus.UpdateRequired, "The server needs a newer app version.");
                _notifier.Problem("Update required", "The server uses a newer protocol. Update Yikz Clipboard.");
                break;
            case StopReason.TooManyConnections:
                SetStatus(ServiceStatus.TooManyConnections, "Too many connections for this device.");
                break;
            default:
                RaiseStatus();
                break;
        }
    }

    private void OnKeyInvalid(string message)
    {
        _log.Warn("auth", message);
        lock (_gate)
        {
            _keys?.Dispose();
            _keys = null;
        }
        _secrets.Delete(KeySecret);
        _ = Task.Run(async () =>
        {
            await _connection.StopAsync().ConfigureAwait(false);
            SetStatus(ServiceStatus.NeedsEncryptionPassword, message);
        });
    }

    private void OnWelcomed(WelcomeMessage welcome)
    {
        lock (_gate)
        {
            _online.Clear();
            foreach (var d in welcome.OnlineDevices)
            {
                _online[d.DeviceId] = d;
            }
        }
        if (welcome.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            _log.Warn("ws", "server welcome has protocol_version " + welcome.ProtocolVersion);
        }
        DevicesChanged?.Invoke();
    }

    private void OnPresence(PresenceMessage presence)
    {
        lock (_gate)
        {
            if (presence.Online)
            {
                _online[presence.DeviceId] = new OnlineDevice { DeviceId = presence.DeviceId, Name = presence.Name, Platform = presence.Platform };
            }
            else
            {
                _online.Remove(presence.DeviceId);
            }
            if (_devices.TryGetValue(presence.DeviceId, out var d))
            {
                d.Online = presence.Online;
            }
        }
        DevicesChanged?.Invoke();
    }

    public async Task<IReadOnlyList<DeviceInfo>> RefreshDevicesAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn)
        {
            return Array.Empty<DeviceInfo>();
        }
        try
        {
            var list = await _api.GetDevicesAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                _devices.Clear();
                foreach (var d in list)
                {
                    _devices[d.Id] = d;
                }
            }
            DevicesChanged?.Invoke();
            return list;
        }
        catch (Exception ex)
        {
            _log.Debug("devices", "refresh failed: " + ex.Message);
            return Devices;
        }
    }

    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values.OrderBy(d => d.CreatedAt).ToList();
            }
        }
    }

    public IReadOnlyList<OnlineDevice> OnlineDevices
    {
        get
        {
            lock (_gate)
            {
                return _online.Values.ToList();
            }
        }
    }

    public string DeviceName(string deviceId)
    {
        if (deviceId == _settings.Current.DeviceId)
        {
            return "This PC";
        }
        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out var d))
            {
                return d.Name;
            }
            if (_online.TryGetValue(deviceId, out var o))
            {
                return o.Name;
            }
        }
        return "Unknown device";
    }

    public string? DevicePlatform(string deviceId)
    {
        if (deviceId == _settings.Current.DeviceId)
        {
            return ProtocolConstants.Platform;
        }
        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out var d))
            {
                return d.Platform;
            }
            if (_online.TryGetValue(deviceId, out var o))
            {
                return o.Platform;
            }
        }
        return null;
    }

    public void SetPaused(bool paused)
    {
        _settings.Update(s => s.Paused = paused);
        if (paused)
        {
            _ = Task.Run(async () =>
            {
                await _connection.StopAsync().ConfigureAwait(false);
                SetStatus(ServiceStatus.Paused, null);
            });
            _log.Info("service", "sync paused");
        }
        else
        {
            _log.Info("service", "sync resumed");
            if (IsSignedIn && HasKey)
            {
                SetStatus(ServiceStatus.Connecting, null);
                _connection.Start();
            }
            else
            {
                SetStatus(IsSignedIn ? ServiceStatus.NeedsEncryptionPassword : ServiceStatus.SignedOut, null);
            }
        }
    }

    public void ReconnectNow(string reason)
    {
        if (Status is ServiceStatus.TooManyConnections)
        {
            SetStatus(ServiceStatus.Connecting, null);
        }
        _connection.ReconnectNow(reason);
    }

    public void Probe() => _connection.Probe();

    public bool SubmitLocalClip(LocalClip clip, bool force = false)
    {
        if (!IsSignedIn || !HasKey)
        {
            return false;
        }
        if (_settings.Current.Paused && !force)
        {
            return false;
        }
        var s = _settings.Current;
        var allowed = clip switch
        {
            LocalText => s.SyncText,
            LocalImage => s.SyncImages,
            LocalFiles => s.SyncFiles,
            _ => false,
        };
        if (!allowed && !force)
        {
            return false;
        }
        return _outgoing.Writer.TryWrite((clip, force));
    }

    private async Task OutgoingLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (clip, force) in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(clip, force, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (UploadException ex)
                {
                    _log.Error("send", ex.Message, ex.InnerException);
                    _notifier.Problem("Could not sync item", ex.Message);
                }
                catch (ApiException ex) when (ex.Code == ErrorCodes.DiskLow)
                {
                    _log.Warn("send", "server disk is low, large upload rejected");
                    _notifier.Problem("Server storage is low", "Large items cannot be uploaded right now.");
                }
                catch (Exception ex)
                {
                    _log.Error("send", "upload failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(LocalClip clip, bool force, CancellationToken ct)
    {
        KeyMaterial? keys;
        lock (_gate)
        {
            keys = _keys;
        }
        if (keys == null)
        {
            return;
        }
        using var prepared = await Task.Run(() => ContentPreparer.Prepare(clip, keys, _paths, _log, _settings.Current.MaxUploadBytes), ct).ConfigureAwait(false);
        if (prepared == null)
        {
            return;
        }
        if (!force)
        {
            var decision = OutgoingPolicy.Decide(prepared.ContentHash, prepared.Text, keys, _guard, _store.Newest()?.Header.ContentHash);
            if (decision != OutgoingDecision.Upload)
            {
                _log.Debug("send", "not sending " + prepared.Kind + ": " + decision);
                return;
            }
        }
        _guard.Remember(prepared.ContentHash);
        _log.Info("send", $"uploading {prepared.Kind} ({HistoryEntry.FormatSize(prepared.Size)})");
        var (header, sealedPayload) = await _uploader.UploadAsync(prepared, keys, ct).ConfigureAwait(false);
        var entry = new HistoryEntry(Strip(header), prepared.Meta, MetaState.Ok);
        _store.Upsert(entry, sealedPayload);
        _log.Info("send", $"sent seq {header.Seq}");
        ItemSent?.Invoke(entry);
    }

    private static ItemHeader Strip(ItemHeader header)
    {
        var h = header.Clone();
        h.Payload = null;
        return h;
    }

    public async Task<bool> CopyToClipboardAsync(string id, CancellationToken ct = default)
    {
        var entry = _store.Get(id);
        if (entry == null)
        {
            return false;
        }
        return await _engine.ApplyAsync(entry, null, true, ct).ConfigureAwait(false);
    }

    public async Task<FetchedContent?> FetchContentAsync(string id, IProgress<double>? progress, CancellationToken ct = default)
    {
        var entry = _store.Get(id);
        KeyMaterial? keys;
        lock (_gate)
        {
            keys = _keys;
        }
        if (entry == null || keys == null || entry.Meta == null)
        {
            return null;
        }
        return await _fetcher.FetchAsync(entry, keys, null, progress, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> SaveAsync(string id, string targetPathOrFolder, CancellationToken ct = default)
    {
        var entry = _store.Get(id) ?? throw new InvalidOperationException("item not found");
        using var content = await FetchContentAsync(id, null, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("item cannot be read");
        if (entry.Header.Kind == ProtocolConstants.KindFiles)
        {
            using var stream = content.OpenRead();
            return Ycf1.ExtractTo(stream, content.Length, targetPathOrFolder);
        }
        await using (var output = new FileStream(targetPathOrFolder, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var input = content.OpenRead())
        {
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }
        return new[] { targetPathOrFolder };
    }

    public async Task<string?> GetTextAsync(string id, CancellationToken ct = default)
    {
        var entry = _store.Get(id);
        if (entry == null || entry.Header.Kind != ProtocolConstants.KindText)
        {
            return null;
        }
        using var content = await FetchContentAsync(id, null, ct).ConfigureAwait(false);
        return content == null ? null : Encoding.UTF8.GetString(content.ReadAll());
    }

    public async Task<byte[]?> GetThumbnailAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        KeyMaterial? keys;
        lock (_gate)
        {
            keys = _keys;
        }
        if (keys == null)
        {
            return null;
        }
        try
        {
            return await _fetcher.GetThumbnailAsync(entry, keys, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug("thumb", "thumbnail unavailable: " + ex.Message);
            return null;
        }
    }

    public async Task SetPinnedAsync(string id, bool pinned, CancellationToken ct = default)
    {
        try
        {
            var header = await _api.SetPinnedAsync(id, pinned, ct).ConfigureAwait(false);
            _store.SetPinned(id, header.Pinned);
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.PinnedLimit)
        {
            _notifier.Problem("Pinned storage is full", "Unpin other items before pinning this one.");
            throw;
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _api.DeleteItemAsync(id, ct).ConfigureAwait(false);
        _store.Delete(new[] { id });
    }

    public Task<StorageInfo> GetStorageAsync(CancellationToken ct = default) => _api.GetStorageAsync(ct);

    public async Task RenameDeviceAsync(string id, string name, CancellationToken ct = default)
    {
        await _api.RenameDeviceAsync(id, name, ct).ConfigureAwait(false);
        if (id == _settings.Current.DeviceId)
        {
            _settings.Update(s => s.DeviceName = name);
        }
        await RefreshDevicesAsync(ct).ConfigureAwait(false);
    }

    public async Task RevokeDeviceAsync(string id, CancellationToken ct = default)
    {
        await _api.DeleteDeviceAsync(id, ct).ConfigureAwait(false);
        await RefreshDevicesAsync(ct).ConfigureAwait(false);
    }

    public void UpdateServerUrl(string url)
    {
        if (ApiClient.TryParseServerUrl(url, out var uri))
        {
            _api.BaseUri = uri;
            _settings.Update(s => s.ServerUrl = uri.ToString().TrimEnd('/'));
        }
    }

    public bool IsFirstDevice
    {
        get
        {
            lock (_gate)
            {
                return _token != null && _pendingKeyCheck == null;
            }
        }
    }

    public string? AccountServerHost => _api.BaseUri.Host;

    public void ResetLocalHistory()
    {
        _engine.CancelPending();
        _store.Clear();
        _engine.ResetState();
        _guard.Clear();
        _log.Info("service", "local history cache cleared, resyncing");
        _connection.ReconnectNow("local history reset");
    }

    public long ReceivedCacheBytes()
    {
        try
        {
            var dir = new DirectoryInfo(_paths.ReceivedDir);
            return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void ClearReceivedCache()
    {
        _fetcher.PruneReceived(TimeSpan.Zero, 0);
    }

    public void PruneReceivedCache() => _fetcher.PruneReceived(TimeSpan.FromDays(7), 2L * 1024 * 1024 * 1024);

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _outgoing.Writer.TryComplete();
        await _connection.DisposeAsync().ConfigureAwait(false);
        _engine.CancelPending();
        _store.Dispose();
        lock (_gate)
        {
            _keys?.Dispose();
            _keys = null;
        }
    }
}
