using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using YikzClipboard.App.Platform;
using YikzClipboard.App.UI;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;

namespace YikzClipboard.App;

public sealed class AppHost
{
    public const string AppVersion = "1.0.0";

    private readonly string[] _args;
    private readonly EventWaitHandle _activate;
    private readonly DispatcherQueue _queue;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private DateTime _lastWakeReconnect = DateTime.MinValue;
    private RegisteredWaitHandle? _activateWait;
    private DispatcherQueueTimer? _maintenance;
    private DispatcherQueueTimer? _uiRefresh;
    private bool _uiRefreshPending;
    private HiddenMessageWindow? _messageWindow;
    private HotkeyRegistration? _hotkey;
    private NetworkWatcher? _network;
    private TrayIconController? _tray;
    private HistoryWindow? _history;
    private SettingsWindow? _settingsWindow;
    private LogsWindow? _logsWindow;
    private bool _quitting;

    internal AppHost(string[] args, EventWaitHandle activate)
    {
        _args = args;
        _activate = activate;
        _queue = DispatcherQueue.GetForCurrentThread();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YikzClipboard");
        Paths = new AppPaths(root);
        Logger = new Logger(Paths.LogsDir);
        App.Log = Logger;
        Settings = new SettingsStore(Paths.SettingsFile);
        Logger.MinimumLevel = Settings.Current.LogLevel == "debug" ? LogLevel.Debug : LogLevel.Info;
        Notifier = new Notifier(Logger, _queue) { Settings = () => Settings.Current };
        ImageTools = new WinImageTools();
        Sink = new WindowsClipboardSink(_queue, () => _messageWindow?.Handle ?? IntPtr.Zero, () => Monitor, Logger);
        Service = new ClipboardSyncService(
            Paths,
            Settings,
            new DpapiSecureStore(Paths.SecretsDir),
            Logger,
            Sink,
            Notifier,
            ImageTools,
            AppVersion);
    }

    internal AppPaths Paths { get; }
    internal Logger Logger { get; }
    internal SettingsStore Settings { get; }
    internal Notifier Notifier { get; }
    internal WinImageTools ImageTools { get; }
    internal WindowsClipboardSink Sink { get; }
    internal ClipboardSyncService Service { get; }
    internal ClipboardMonitor? Monitor { get; private set; }
    internal DispatcherQueue Queue => _queue;
    internal string ActiveHotkey => _hotkey?.Active ?? "";
    internal IntPtr MessageWindowHandle => _messageWindow?.Handle ?? IntPtr.Zero;

    public void Start()
    {
        Logger.Info("app", $"Yikz Clipboard {AppVersion} starting on {Environment.OSVersion.VersionString} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        try
        {
            _messageWindow = new HiddenMessageWindow(Logger);
            _messageWindow.StartListening();
            _messageWindow.HotkeyPressed += id =>
            {
                if (id == HotkeyRegistration.HistoryHotkeyId)
                {
                    ToggleHistory();
                }
            };
            _messageWindow.Resumed += OnWake;
            _messageWindow.SessionUnlocked += () => OnWake("session unlocked");
            _messageWindow.Suspending += () => Logger.Info("power", "system is going to sleep");
            _messageWindow.SettingChanged += () => ScheduleUiRefresh();
            Monitor = new ClipboardMonitor(_messageWindow, Service, _queue, Logger);
            _hotkey = new HotkeyRegistration(_messageWindow.Handle, Logger);
            var active = _hotkey.RegisterWithFallback(Settings.Current.Hotkey);
            if (!string.IsNullOrEmpty(active) && !string.Equals(active, Settings.Current.Hotkey, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn("hotkey", Settings.Current.Hotkey + " is unavailable, using " + active);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("app", "platform setup failed", ex);
        }
        Notifier.Fallback = (title, body) => _tray?.ShowBalloon(title, body);
        Notifier.DownloadRequested += id => CopyItem(id);
        Notifier.OpenRequested += _ => ShowHistory();
        Notifier.Initialize();
        _network = new NetworkWatcher(reason => UiThread.Post(_queue, () => Service.ReconnectNow(reason)), Logger);
        Service.StatusChanged += _ => ScheduleUiRefresh();
        Service.HistoryChanged += ScheduleUiRefresh;
        Service.StorageWarningChanged += _ => ScheduleUiRefresh();
        Service.DevicesChanged += ScheduleUiRefresh;
        Service.ItemApplied += (entry, _) => Logger.Debug("app", "applied " + entry.Id);
        _uiRefresh = _queue.CreateTimer();
        _uiRefresh.Interval = TimeSpan.FromMilliseconds(120);
        _uiRefresh.IsRepeating = false;
        _uiRefresh.Tick += (_, _) => RefreshUi();
        try
        {
            _tray = new TrayIconController(this);
        }
        catch (Exception ex)
        {
            Logger.Error("tray", "tray icon could not be created", ex);
        }
        SyncStartupRegistration();
        Service.Start();
        _maintenance = _queue.CreateTimer();
        _maintenance.Interval = TimeSpan.FromMinutes(30);
        _maintenance.IsRepeating = true;
        _maintenance.Tick += (_, _) => Task.Run(() => Service.PruneReceivedCache());
        _maintenance.Start();
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => UiThread.Post(_queue, OnSecondInstance), null, Timeout.Infinite, false);
        var background = _args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        if (!Service.IsSignedIn || !Service.HasKey)
        {
            if (!background)
            {
                ShowSettings();
            }
        }
        else if (!background)
        {
            ShowHistory();
        }
    }

    private void SyncStartupRegistration()
    {
        try
        {
            var wanted = Settings.Current.StartWithWindows;
            if (wanted != StartupRegistration.IsEnabled())
            {
                StartupRegistration.Set(wanted, Logger);
            }
            else if (wanted)
            {
                StartupRegistration.Set(true, Logger);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("startup", "could not sync start with Windows", ex);
        }
    }

    private void OnSecondInstance()
    {
        Logger.Info("app", "activated by another launch");
        if (!Service.IsSignedIn || !Service.HasKey)
        {
            ShowSettings();
        }
        else
        {
            ShowHistory();
        }
    }

    private void OnWake(string reason)
    {
        var now = DateTime.UtcNow;
        if (reason == "display turned on" && (now - _startedAt < TimeSpan.FromSeconds(10) || now - _lastWakeReconnect < TimeSpan.FromSeconds(5)))
        {
            return;
        }
        if (now - _lastWakeReconnect < TimeSpan.FromSeconds(2))
        {
            return;
        }
        _lastWakeReconnect = now;
        Logger.Info("power", reason + ", reconnecting now");
        Service.ReconnectNow(reason);
    }

    internal void ScheduleUiRefresh()
    {
        UiThread.Post(_queue, () =>
        {
            if (_uiRefresh == null)
            {
                return;
            }
            _uiRefreshPending = true;
            if (!_uiRefresh.IsRunning)
            {
                _uiRefresh.Start();
            }
        });
    }

    private void RefreshUi()
    {
        if (!_uiRefreshPending || _quitting)
        {
            return;
        }
        _uiRefreshPending = false;
        try
        {
            _tray?.Update();
            _history?.OnServiceChanged();
            _settingsWindow?.OnServiceChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("ui", "refresh failed", ex);
        }
    }

    internal void ToggleHistory()
    {
        if (_history != null && _history.IsShown && _history.IsForeground)
        {
            _history.HideWindow();
            return;
        }
        ShowHistory();
    }

    internal void ShowHistory()
    {
        if (!Service.IsSignedIn || !Service.HasKey)
        {
            ShowSettings();
            return;
        }
        var previous = ForegroundTracker.Capture(_history?.Handle ?? IntPtr.Zero);
        Service.Probe();
        try
        {
            _history ??= new HistoryWindow(this);
            _history.ShowAt(previous);
        }
        catch (Exception ex)
        {
            Logger.Error("ui", "history window failed", ex);
        }
    }

    internal void ShowSettings()
    {
        try
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.ShowWindow();
        }
        catch (Exception ex)
        {
            Logger.Error("ui", "settings window failed", ex);
        }
    }

    internal void ShowLogs()
    {
        try
        {
            if (_logsWindow == null)
            {
                _logsWindow = new LogsWindow(this);
                _logsWindow.Closed += (_, _) => _logsWindow = null;
            }
            _logsWindow.ShowWindow();
        }
        catch (Exception ex)
        {
            Logger.Error("ui", "logs window failed", ex);
        }
    }

    internal async void CopyItem(string id)
    {
        try
        {
            var ok = await Service.CopyToClipboardAsync(id);
            if (!ok)
            {
                Logger.Warn("app", "item " + id + " could not be copied");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("app", "copy failed", ex);
            Notifier.Problem("Could not copy item", ex.Message);
        }
    }

    internal async void SendClipboardNow()
    {
        if (Monitor == null)
        {
            return;
        }
        var ok = await Monitor.CaptureAsync(true);
        Logger.Info("app", ok ? "sending the current clipboard" : "nothing to send from the clipboard");
    }

    internal void TogglePause()
    {
        Service.SetPaused(!Settings.Current.Paused);
        ScheduleUiRefresh();
    }

    internal void TogglePauseTo(bool paused)
    {
        if (Settings.Current.Paused != paused)
        {
            Service.SetPaused(paused);
            ScheduleUiRefresh();
        }
    }

    internal bool ChangeHotkey(string text, out string? error)
    {
        error = "Hotkeys are unavailable";
        if (_hotkey == null)
        {
            return false;
        }
        var previous = _hotkey.Active;
        if (_hotkey.TryRegister(text, out error))
        {
            var active = _hotkey.Active!;
            Settings.Update(s => s.Hotkey = active);
            ScheduleUiRefresh();
            return true;
        }
        if (!string.IsNullOrEmpty(previous))
        {
            _hotkey.TryRegister(previous, out _);
        }
        return false;
    }

    internal void SetStartWithWindows(bool enabled)
    {
        Settings.Update(s => s.StartWithWindows = enabled);
        StartupRegistration.Set(enabled, Logger);
    }

    internal async void Quit()
    {
        if (_quitting)
        {
            return;
        }
        _quitting = true;
        Logger.Info("app", "quitting");
        try
        {
            _activateWait?.Unregister(null);
            _maintenance?.Stop();
            _history?.CloseForExit();
            _settingsWindow?.Close();
            _logsWindow?.Close();
            _tray?.Dispose();
            _hotkey?.Dispose();
            _network?.Dispose();
            _messageWindow?.Dispose();
            Notifier.Dispose();
            var dispose = Service.DisposeAsync().AsTask();
            await Task.WhenAny(dispose, Task.Delay(3000));
            await Logger.FlushAsync();
        }
        catch (Exception)
        {
        }
        Application.Current.Exit();
    }
}
