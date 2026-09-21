using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using YikzClipboard.App.Platform;
using YikzClipboard.App.UI;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;
using YikzClipboard.Core.Updates;

namespace YikzClipboard.App;

public sealed class AppHost
{
    public static readonly string AppVersion = SemVer.FromAssembly(typeof(AppHost).Assembly).ToString();

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
    private DispatcherQueueTimer? _updateTimer;
    private DispatcherQueueTimer? _installTimer;
    private bool _autoInstallBlocked;
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
        var lastChecked = Settings.Current.LastUpdateCheck is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
        Updater = new Updater(
            new UpdateClient(UpdateClient.CreateHttpClient(AppVersion), ResolveUpdateEndpoint),
            SemVer.Parse(AppVersion),
            UpdatePolicy.CurrentPlatformKey(),
            Path.Combine(Paths.Root, "updates"),
            Logger,
            lastChecked);
    }

    private UpdateEndpoint? ResolveUpdateEndpoint()
    {
        var token = Service.Api.Token;
        return Service.IsSignedIn && !string.IsNullOrEmpty(token) ? new UpdateEndpoint(Service.Api.BaseUri, token) : null;
    }

    internal AppPaths Paths { get; }
    internal Logger Logger { get; }
    internal SettingsStore Settings { get; }
    internal Notifier Notifier { get; }
    internal WinImageTools ImageTools { get; }
    internal WindowsClipboardSink Sink { get; }
    internal ClipboardSyncService Service { get; }
    internal ClipboardMonitor? Monitor { get; private set; }
    internal Updater Updater { get; }
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
        ApplyTrayVisibility();
        SyncStartupRegistration();
        Service.Start();
        _maintenance = _queue.CreateTimer();
        _maintenance.Interval = TimeSpan.FromMinutes(30);
        _maintenance.IsRepeating = true;
        _maintenance.Tick += (_, _) => Task.Run(() => Service.PruneReceivedCache());
        _maintenance.Start();
        StartUpdater();
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => UiThread.Post(_queue, OnSecondInstance), null, Timeout.Infinite, false);
        var background = HasArg("--background");
        if (HasArg("--updated"))
        {
            Logger.Info("update", "updated to version " + AppVersion);
            Notifier.Info("Yikz Clipboard updated", "Version " + AppVersion + " is installed.");
        }
        else if (HasArg("--update-failed"))
        {
            Logger.Warn("update", "the previous update failed and was rolled back");
            Notifier.Problem("Update failed", "The update could not be installed and the previous version was restored. See the logs for details.");
        }
        if (!Service.IsSignedIn || !Service.HasKey)
        {
            if (!background || Settings.Current.IsReachableOnlyByRelaunch && !HasArg("--updated"))
            {
                ShowSettings();
            }
        }
        else if (!background)
        {
            ShowHistory();
        }
        EnsureTaskbarEntry();
    }

    private bool HasArg(string name) => _args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private void ApplyTrayVisibility()
    {
        if (_quitting)
        {
            return;
        }
        if (Settings.Current.ShowTrayIcon)
        {
            if (_tray == null)
            {
                try
                {
                    _tray = new TrayIconController(this);
                }
                catch (Exception ex)
                {
                    Logger.Error("tray", "tray icon could not be created", ex);
                }
            }
        }
        else if (_tray != null)
        {
            var tray = _tray;
            _tray = null;
            tray.Dispose();
        }
    }

    private void EnsureTaskbarEntry()
    {
        if (_quitting)
        {
            return;
        }
        var wanted = Settings.Current.ShowInTaskbar;
        try
        {
            if (_history != null && _history.TaskbarMode != wanted)
            {
                _history.SetTaskbarMode(wanted);
            }
            if (wanted && Service.IsSignedIn && Service.HasKey)
            {
                _history ??= new HistoryWindow(this);
                _history.ShowInTaskbarMinimized();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ui", "taskbar entry failed", ex);
        }
    }

    internal void SetShowTrayIcon(bool visible)
    {
        Settings.Update(s => s.ShowTrayIcon = visible);
        Logger.Info("app", visible ? "tray icon shown" : "tray icon hidden");
        ApplyTrayVisibility();
    }

    internal void SetShowInTaskbar(bool visible)
    {
        Settings.Update(s => s.ShowInTaskbar = visible);
        Logger.Info("app", visible ? "taskbar entry shown" : "taskbar entry hidden");
        EnsureTaskbarEntry();
    }

    private void StartUpdater()
    {
        _ = Task.Run(() => UpdatePackage.CleanupOldVersions(Updater.UpdatesRoot, Updater.Current));
        Updater.Checked += t => Settings.Update(s => s.LastUpdateCheck = t.ToUnixTimeMilliseconds());
        Updater.Changed += _ => UiThread.Post(_queue, OnUpdateChanged);
        Service.ReleaseAvailable += v =>
        {
            Logger.Info("update", "server announced release " + v);
            UiThread.Post(_queue, () => CheckForUpdates(false));
        };
        _updateTimer = _queue.CreateTimer();
        _updateTimer.Interval = UpdatePolicy.LaunchDelay;
        _updateTimer.IsRepeating = true;
        _updateTimer.Tick += (t, _) =>
        {
            if (t.Interval != UpdatePolicy.CheckInterval)
            {
                t.Interval = UpdatePolicy.CheckInterval;
            }
            CheckForUpdates(false);
        };
        _updateTimer.Start();
        _installTimer = _queue.CreateTimer();
        _installTimer.Interval = TimeSpan.FromMinutes(1);
        _installTimer.IsRepeating = true;
        _installTimer.Tick += (_, _) => TryAutoInstall();
        _installTimer.Start();
    }

    internal void CheckForUpdates(bool manual)
    {
        if (_quitting)
        {
            return;
        }
        if (manual)
        {
            _autoInstallBlocked = false;
        }
        if (!Service.IsSignedIn)
        {
            if (manual)
            {
                Logger.Info("update", "update check skipped, not signed in");
            }
            return;
        }
        _ = Updater.CheckAsync(manual);
    }

    private void OnUpdateChanged()
    {
        if (_quitting)
        {
            return;
        }
        try
        {
            _tray?.Update();
            _settingsWindow?.OnUpdateChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("ui", "update refresh failed", ex);
        }
        if (Updater.Status.Stage == UpdateStage.Ready)
        {
            TryAutoInstall();
        }
    }

    private bool IsIdle()
    {
        if (_history != null && _history.IsShown)
        {
            return false;
        }
        if (_settingsWindow != null || _logsWindow != null)
        {
            return false;
        }
        return !Service.IsSyncing;
    }

    private void TryAutoInstall()
    {
        if (_quitting || _autoInstallBlocked || !Settings.Current.AutoInstallUpdates)
        {
            return;
        }
        if (Updater.Status.Stage != UpdateStage.Ready || !IsIdle())
        {
            return;
        }
        Logger.Info("update", "installing the update automatically while idle");
        if (!InstallUpdate(false))
        {
            _autoInstallBlocked = true;
        }
    }

    internal bool InstallUpdate(bool userInitiated)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Notifier.Problem("Update could not be installed", "The app location is unknown.");
            return false;
        }
        if (!Updater.StartInstall(Environment.ProcessId, exe, out var error))
        {
            Notifier.Problem("Update could not be installed", error ?? "Unknown error.");
            return false;
        }
        Logger.Info("update", userInitiated ? "restarting to update" : "restarting to update automatically");
        Quit();
        return true;
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
        if (!Service.IsSignedIn || !Service.HasKey || Settings.Current.IsReachableOnlyByRelaunch)
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
            if (Settings.Current.ShowInTaskbar && _history == null)
            {
                EnsureTaskbarEntry();
            }
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

    internal void ShowSettings(string? page = null)
    {
        try
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.ShowWindow(page);
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
            _updateTimer?.Stop();
            _installTimer?.Stop();
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
