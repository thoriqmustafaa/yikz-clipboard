using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using YikzClipboard.App.Interop;
using YikzClipboard.App.Platform;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;
using YikzClipboard.Core.Updates;

namespace YikzClipboard.App.UI;

internal sealed class RelayCommand : ICommand
{
    private readonly Action _action;

    public RelayCommand(Action action)
    {
        _action = action;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        try
        {
            _action();
        }
        catch (Exception ex)
        {
            App.Log?.Error("tray", "menu action failed", ex);
        }
    }
}

internal enum TrayState
{
    Connected,
    Syncing,
    Connecting,
    Offline,
    Paused,
    Attention,
}

internal static class TrayIconRenderer
{
    public static Icon Render(TrayState state, bool lightTaskbar, int size)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var dim = state is TrayState.Paused or TrayState.Offline;
            var baseColor = lightTaskbar ? Color.FromArgb(255, 28, 28, 28) : Color.FromArgb(255, 255, 255, 255);
            var glyph = dim ? Color.FromArgb(150, baseColor) : baseColor;
            float s = size;
            var stroke = Math.Max(1.25f, s * 0.085f);
            using (var pen = new Pen(glyph, stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(glyph))
            {
                var body = new RectangleF(s * 0.17f, s * 0.13f, s * 0.66f, s * 0.80f);
                using (var path = RoundedRect(body, s * 0.13f))
                {
                    g.DrawPath(pen, path);
                }
                var clip = new RectangleF(s * 0.33f, s * 0.05f, s * 0.34f, s * 0.17f);
                using (var clipPath = RoundedRect(clip, s * 0.06f))
                {
                    g.FillPath(brush, clipPath);
                }
                g.DrawLine(pen, s * 0.34f, s * 0.47f, s * 0.66f, s * 0.47f);
                g.DrawLine(pen, s * 0.34f, s * 0.64f, s * 0.56f, s * 0.64f);
            }
            Color? badge = state switch
            {
                TrayState.Connecting => Color.FromArgb(255, 242, 166, 12),
                TrayState.Offline => Color.FromArgb(255, 242, 166, 12),
                TrayState.Attention => Color.FromArgb(255, 229, 72, 77),
                TrayState.Syncing => Color.FromArgb(255, 59, 130, 246),
                _ => null,
            };
            if (state == TrayState.Paused)
            {
                ClearCircle(g, s * 0.76f, s * 0.76f, s * 0.27f);
                using var pauseBrush = new SolidBrush(baseColor);
                g.FillRectangle(pauseBrush, s * 0.62f, s * 0.58f, s * 0.09f, s * 0.36f);
                g.FillRectangle(pauseBrush, s * 0.80f, s * 0.58f, s * 0.09f, s * 0.36f);
            }
            else if (badge.HasValue)
            {
                ClearCircle(g, s * 0.76f, s * 0.76f, s * 0.27f);
                using var badgeBrush = new SolidBrush(badge.Value);
                var r = s * 0.19f;
                g.FillEllipse(badgeBrush, s * 0.76f - r, s * 0.76f - r, r * 2, r * 2);
            }
        }
        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        Win32.DestroyIcon(handle);
        return icon;
    }

    private static void ClearCircle(Graphics g, float cx, float cy, float r)
    {
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using var clear = new SolidBrush(Color.Transparent);
        g.FillEllipse(clear, cx - r, cy - r, r * 2, r * 2);
        g.CompositingMode = mode;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class TrayIconController : IDisposable
{
    private readonly AppHost _host;
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyout _menu = new();
    private Icon? _currentIcon;
    private TrayState? _renderedState;
    private bool? _renderedLight;

    public TrayIconController(AppHost host)
    {
        _host = host;
        _icon = new TaskbarIcon
        {
            ToolTipText = "Yikz Clipboard",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => _host.ToggleHistory()),
            ContextFlyout = _menu,
        };
        Update();
        _icon.ForceCreate(false);
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            App.Log?.Warn("tray", "balloon failed", ex);
        }
    }

    private TrayState CurrentState()
    {
        var service = _host.Service;
        return service.Status switch
        {
            ServiceStatus.Connected => service.IsSyncing ? TrayState.Syncing : TrayState.Connected,
            ServiceStatus.Connecting => TrayState.Connecting,
            ServiceStatus.Reconnecting => TrayState.Offline,
            ServiceStatus.Paused => TrayState.Paused,
            _ => TrayState.Attention,
        };
    }

    public void Update()
    {
        var state = CurrentState();
        var light = Shell.IsTaskbarLight();
        if (state != _renderedState || light != _renderedLight)
        {
            try
            {
                var dpi = Win32.GetDpiForSystem();
                var size = Math.Max(16, Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi));
                var icon = TrayIconRenderer.Render(state, light, size);
                _icon.Icon = icon;
                _currentIcon?.Dispose();
                _currentIcon = icon;
                _renderedState = state;
                _renderedLight = light;
            }
            catch (Exception ex)
            {
                App.Log?.Warn("tray", "icon render failed", ex);
            }
        }
        var status = _host.Service.StatusText;
        _icon.ToolTipText = "Yikz Clipboard: " + status;
        RebuildMenu(status);
    }

    private void RebuildMenu(string status)
    {
        var service = _host.Service;
        _menu.Items.Clear();
        _menu.Items.Add(new MenuFlyoutItem { Text = status, IsEnabled = false });
        var warning = service.ActiveStorageWarning;
        if (warning != null)
        {
            _menu.Items.Add(new MenuFlyoutItem { Text = "Server storage is low", IsEnabled = false });
        }
        var update = _host.Updater.Status;
        if (update.Stage == UpdateStage.Ready && update.Prepared != null)
        {
            _menu.Items.Add(new MenuFlyoutItem
            {
                Text = "Restart to update (" + update.Prepared.Version + ")",
                Command = new RelayCommand(() => _host.InstallUpdate(true)),
            });
        }
        _menu.Items.Add(new MenuFlyoutSeparator());
        var recent = service.IsSignedIn ? service.History.List(8) : new List<HistoryEntry>();
        if (recent.Count == 0)
        {
            _menu.Items.Add(new MenuFlyoutItem { Text = service.IsSignedIn ? "No items yet" : "Not signed in", IsEnabled = false });
        }
        else
        {
            foreach (var entry in recent)
            {
                var id = entry.Id;
                var title = entry.Title.Replace('\t', ' ').Replace("&", "&&");
                if (title.Length > 46)
                {
                    title = title[..45] + "...";
                }
                _menu.Items.Add(new MenuFlyoutItem
                {
                    Text = title + "\t" + Ui.ShortTime(entry.Header.CreatedAt),
                    Command = new RelayCommand(() => _host.CopyItem(id)),
                });
            }
        }
        _menu.Items.Add(new MenuFlyoutSeparator());
        var hotkey = _host.ActiveHotkey;
        _menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Open History" + (string.IsNullOrEmpty(hotkey) ? "" : "\t" + hotkey),
            Command = new RelayCommand(() => _host.ShowHistory()),
        });
        _menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Send Clipboard Now",
            IsEnabled = service.IsSignedIn && service.HasKey,
            Command = new RelayCommand(() => _host.SendClipboardNow()),
        });
        _menu.Items.Add(new MenuFlyoutItem
        {
            Text = service.Settings.Paused ? "Resume Sync" : "Pause Sync",
            IsEnabled = service.IsSignedIn,
            Command = new RelayCommand(() => _host.TogglePause()),
        });
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(new MenuFlyoutItem { Text = "Settings", Command = new RelayCommand(() => _host.ShowSettings()) });
        _menu.Items.Add(new MenuFlyoutItem { Text = "Logs", Command = new RelayCommand(() => _host.ShowLogs()) });
        _menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Check for Updates",
            IsEnabled = service.IsSignedIn && update.Stage is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Installing),
            Command = new RelayCommand(() =>
            {
                _host.CheckForUpdates(true);
                _host.ShowSettings("updates");
            }),
        });
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = new RelayCommand(() => _host.Quit()) });
    }

    public void Dispose()
    {
        try
        {
            _icon.Dispose();
        }
        catch
        {
        }
        _currentIcon?.Dispose();
    }
}
