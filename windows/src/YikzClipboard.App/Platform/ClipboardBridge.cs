using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using YikzClipboard.Core.Imaging;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Platform;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;

namespace YikzClipboard.App.Platform;

internal static class UiThread
{
    public static Task RunAsync(DispatcherQueue queue, Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("dispatcher is shutting down"));
        }
        return tcs.Task;
    }

    public static void Post(DispatcherQueue queue, Action action)
    {
        queue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.Log?.Error("ui", "dispatched action failed", ex);
            }
        });
    }
}

internal sealed class ClipboardMonitor
{
    private readonly HiddenMessageWindow _window;
    private readonly ClipboardSyncService _service;
    private readonly DispatcherQueue _queue;
    private readonly ILog _log;
    private readonly DispatcherQueueTimer _debounce;
    private uint _ignoredSequence;

    public ClipboardMonitor(HiddenMessageWindow window, ClipboardSyncService service, DispatcherQueue queue, ILog log)
    {
        _window = window;
        _service = service;
        _queue = queue;
        _log = log;
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(150);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => _ = CaptureAsync(false);
        _window.ClipboardUpdated += OnClipboardUpdated;
    }

    public void IgnoreSequence(uint sequence)
    {
        _ignoredSequence = sequence;
    }

    private void OnClipboardUpdated()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public async Task<bool> CaptureAsync(bool force)
    {
        try
        {
            var snapshot = ClipboardAccess.Read(_window.Handle, honorOriginMarker: !force);
            if (!force && snapshot.Sequence != 0 && snapshot.Sequence == _ignoredSequence)
            {
                return false;
            }
            switch (snapshot.Kind)
            {
                case ClipboardSnapshotKind.Sensitive:
                    _log.Info("clipboard", "skipped content marked private (" + snapshot.Reason + ")");
                    return false;
                case ClipboardSnapshotKind.OwnWrite:
                    return false;
                case ClipboardSnapshotKind.None:
                    if (snapshot.Reason != null)
                    {
                        _log.Debug("clipboard", "nothing captured: " + snapshot.Reason);
                    }
                    return false;
            }
            var source = SourceAppResolver.Resolve(snapshot.OwnerWindow);
            LocalClip? clip = null;
            switch (snapshot.Kind)
            {
                case ClipboardSnapshotKind.Text:
                    clip = new LocalText(snapshot.Text!, source);
                    break;
                case ClipboardSnapshotKind.Files:
                    clip = new LocalFiles(snapshot.Files!, source);
                    break;
                case ClipboardSnapshotKind.Image:
                    {
                        var png = snapshot.Png;
                        if (png == null || !PngInfo.IsPng(png))
                        {
                            var dib = snapshot.Dib;
                            if (dib == null)
                            {
                                return false;
                            }
                            png = await Task.Run(() => WinImageTools.DibToPngAsync(dib));
                        }
                        PngInfo.TryGetSize(png, out var w, out var h);
                        clip = new LocalImage(png, w, h, source);
                        break;
                    }
            }
            if (clip == null)
            {
                return false;
            }
            var accepted = _service.SubmitLocalClip(clip, force);
            if (force && !accepted)
            {
                _log.Warn("clipboard", "could not queue the clipboard for sending");
            }
            return accepted;
        }
        catch (Exception ex)
        {
            _log.Error("clipboard", "reading the clipboard failed", ex);
            return false;
        }
    }
}

internal sealed class WindowsClipboardSink : IClipboardSink
{
    private readonly DispatcherQueue _queue;
    private readonly Func<IntPtr> _owner;
    private readonly Func<ClipboardMonitor?> _monitor;
    private readonly ILog _log;

    public WindowsClipboardSink(DispatcherQueue queue, Func<IntPtr> owner, Func<ClipboardMonitor?> monitor, ILog log)
    {
        _queue = queue;
        _owner = owner;
        _monitor = monitor;
        _log = log;
    }

    public async Task WriteAsync(ReceivedContent content, CancellationToken ct)
    {
        byte[]? dib = null;
        if (content is ReceivedImage image)
        {
            try
            {
                dib = await WinImageTools.PngToDibAsync(image.Png);
            }
            catch (Exception ex)
            {
                _log.Warn("clipboard", "could not build CF_DIB, writing PNG only", ex);
            }
        }
        await UiThread.RunAsync(_queue, () =>
        {
            var owner = _owner();
            var sequence = content switch
            {
                ReceivedText t => ClipboardAccess.WriteText(owner, t.Text, t.ItemId),
                ReceivedImage i => ClipboardAccess.WriteImage(owner, i.Png, dib, i.ItemId),
                ReceivedFiles f => ClipboardAccess.WriteFiles(owner, f.Paths, f.ItemId),
                _ => 0u,
            };
            _monitor()?.IgnoreSequence(sequence);
        });
    }
}

internal sealed class Notifier : IUserNotifier, IDisposable
{
    private readonly ILog _log;
    private readonly DispatcherQueue _queue;
    private readonly Dictionary<string, DateTime> _recentProblems = new();
    private AppNotificationManager? _manager;
    private bool _registered;

    public Notifier(ILog log, DispatcherQueue queue)
    {
        _log = log;
        _queue = queue;
    }

    public Action<string, string>? Fallback { get; set; }

    public Func<AppSettings>? Settings { get; set; }

    public event Action<string>? DownloadRequested;

    public event Action<string>? OpenRequested;

    public void Initialize()
    {
        try
        {
            _manager = AppNotificationManager.Default;
            _manager.NotificationInvoked += OnInvoked;
            _manager.Register();
            _registered = true;
            _log.Info("notify", "app notifications registered");
        }
        catch (Exception ex)
        {
            _registered = false;
            _log.Warn("notify", "app notifications unavailable, using tray balloons", ex);
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;
        arguments.TryGetValue("action", out var action);
        arguments.TryGetValue("id", out var id);
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        UiThread.Post(_queue, () =>
        {
            if (action == "download")
            {
                DownloadRequested?.Invoke(id);
            }
            else
            {
                OpenRequested?.Invoke(id);
            }
        });
    }

    private void Show(string title, string body, string? itemId, bool downloadButton)
    {
        if (_registered && _manager != null)
        {
            try
            {
                var builder = new AppNotificationBuilder().AddText(title).AddText(body);
                if (itemId != null)
                {
                    builder.AddArgument("action", "open").AddArgument("id", itemId);
                    if (downloadButton)
                    {
                        builder.AddButton(new AppNotificationButton("Download").AddArgument("action", "download").AddArgument("id", itemId));
                    }
                }
                _manager.Show(builder.BuildNotification());
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("notify", "showing a notification failed", ex);
            }
        }
        UiThread.Post(_queue, () => Fallback?.Invoke(title, body));
    }

    public void LargeItemAvailable(HistoryEntry entry, string deviceName)
    {
        Show("Large item from " + deviceName, entry.Title + " (" + HistoryEntry.FormatSize(entry.Header.Size) + ")", entry.Id, true);
    }

    public void ItemReceived(HistoryEntry entry, string deviceName)
    {
        if (Settings?.Invoke().NotifyReceived == true)
        {
            Show("Copied from " + deviceName, entry.Title, entry.Id, false);
        }
    }

    public void Problem(string title, string message)
    {
        lock (_recentProblems)
        {
            if (_recentProblems.TryGetValue(title, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(2))
            {
                return;
            }
            _recentProblems[title] = DateTime.UtcNow;
        }
        Show(title, message, null, false);
    }

    public void Dispose()
    {
        if (_registered && _manager != null)
        {
            try
            {
                _manager.Unregister();
            }
            catch
            {
            }
        }
    }
}
