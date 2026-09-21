using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using YikzClipboard.App.Platform;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;

namespace YikzClipboard.App.UI;

internal sealed class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly ClipboardSyncService _service;
    private readonly Grid _root = new();
    private readonly NavigationView _nav = new();
    private readonly Dictionary<string, NavigationViewItem> _items = new();
    private string _page = "account";
    private ServiceStatus? _lastStatus;
    private bool _busy;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        _service = host.Service;
        Title = "Yikz Clipboard Settings";
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = WindowHelpers.TitleBar("Yikz Clipboard Settings", out var drag);
        _root.Children.Add(titleBar);
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = false;
        _nav.IsPaneToggleButtonVisible = false;
        _nav.OpenPaneLength = 232;
        _nav.IsTitleBarAutoPaddingEnabled = false;
        AddNavItem("account", "Account", Glyphs.Account);
        AddNavItem("sync", "Sync", Glyphs.Sync);
        AddNavItem("general", "General", Glyphs.Settings);
        AddNavItem("devices", "Devices and Storage", Glyphs.Devices);
        AddNavItem("about", "About", Glyphs.Info);
        _nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                _page = tag;
                Render();
            }
        };
        Grid.SetRow(_nav, 1);
        _root.Children.Add(_nav);
        Content = _root;
        WindowHelpers.ApplyBackdrop(this, _root);
        WindowHelpers.SetIcon(this);
        WindowHelpers.ConfigureTitleBar(this, drag);
        WindowHelpers.CenterOnCursorMonitor(this, 980, 700, 0.5);
        _nav.SelectedItem = _items["account"];
        _root.ActualThemeChanged += (_, _) => Render();
    }

    private void AddNavItem(string tag, string title, string glyph)
    {
        var item = new NavigationViewItem { Content = title, Tag = tag, Icon = new FontIcon { Glyph = glyph } };
        _items[tag] = item;
        _nav.MenuItems.Add(item);
    }

    public void ShowWindow()
    {
        _lastStatus = null;
        OnServiceChanged();
        WindowHelpers.BringToFront(this);
    }

    public void OnServiceChanged()
    {
        var status = _service.Status;
        if (_busy)
        {
            return;
        }
        if (_lastStatus != status)
        {
            _lastStatus = status;
            if (_page == "account")
            {
                Render();
            }
        }
    }

    private void Render()
    {
        FrameworkElement page = _page switch
        {
            "sync" => SyncPage(),
            "general" => GeneralPage(),
            "devices" => DevicesPage(),
            "about" => AboutPage(),
            _ => AccountPage(),
        };
        _nav.Content = new ScrollViewer
        {
            Content = new Border { Child = page, Padding = new Thickness(36, 24, 36, 36), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static StackPanel PageRoot(string title)
    {
        var panel = new StackPanel { Spacing = 4 };
        var header = Ui.Title(title, 28);
        header.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(header);
        return panel;
    }

    private static InfoBar Info(InfoBarSeverity severity, string? title, string message, bool open = true) => new()
    {
        Severity = severity,
        Title = title ?? "",
        Message = message,
        IsOpen = open,
        IsClosable = false,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBox Field(string header, string value, string placeholder = "") => new()
    {
        Header = header,
        Text = value,
        PlaceholderText = placeholder,
        IsSpellCheckEnabled = false,
    };

    private StackPanel AccountPage()
    {
        return _service.Status switch
        {
            ServiceStatus.SignedOut => SignInPage(),
            ServiceStatus.NeedsEncryptionPassword => EncryptionPage(),
            _ => AccountSummaryPage(),
        };
    }

    private StackPanel SignInPage()
    {
        var page = PageRoot("Sign in");
        var detail = _service.StatusDetail;
        if (!string.IsNullOrEmpty(detail))
        {
            page.Children.Add(Info(InfoBarSeverity.Warning, null, detail));
        }
        page.Children.Add(Ui.Secondary("Connect this PC to your yikz-clipboard server. Your clipboard is end-to-end encrypted, the server never sees its content.", 13, true));
        var form = new StackPanel { Spacing = 14, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        var settings = _service.Settings;
        var server = Field("Server", settings.ServerUrl, AppSettings.DefaultServerUrl);
        var user = Field("Username", settings.Username ?? "");
        var password = new PasswordBox { Header = "Password" };
        var device = Field("Device name", settings.DeviceName ?? Environment.MachineName, Environment.MachineName);
        var error = Info(InfoBarSeverity.Error, null, "", false);
        var button = new Button { Content = "Sign in", MinWidth = 120 };
        Ui.Styled(button, "AccentButtonStyle");
        var ring = new ProgressRing { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(button);
        actions.Children.Add(ring);
        form.Children.Add(server);
        form.Children.Add(user);
        form.Children.Add(password);
        form.Children.Add(device);
        form.Children.Add(error);
        form.Children.Add(actions);
        async void Submit()
        {
            if (_busy)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(user.Text) || string.IsNullOrEmpty(password.Password))
            {
                error.Message = "Enter your username and password.";
                error.IsOpen = true;
                return;
            }
            _busy = true;
            button.IsEnabled = false;
            ring.IsActive = true;
            ring.Visibility = Visibility.Visible;
            error.IsOpen = false;
            try
            {
                var outcome = await _service.LoginAsync(server.Text, user.Text, password.Password, device.Text);
                if (!outcome.Success)
                {
                    error.Message = outcome.Error ?? "Sign in failed.";
                    error.IsOpen = true;
                    return;
                }
                _busy = false;
                Render();
            }
            catch (Exception ex)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
            }
            finally
            {
                _busy = false;
                button.IsEnabled = true;
                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;
            }
        }
        button.Click += (_, _) => Submit();
        password.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Submit();
            }
        };
        page.Children.Add(Ui.Card(form, new Thickness(24)));
        return page;
    }

    private StackPanel EncryptionPage()
    {
        var first = _service.IsFirstDevice;
        var page = PageRoot(first ? "Create encryption password" : "Encryption password");
        var detail = _service.StatusDetail;
        if (!string.IsNullOrEmpty(detail))
        {
            page.Children.Add(Info(InfoBarSeverity.Warning, null, detail));
        }
        page.Children.Add(Ui.Secondary(first
            ? "This is the first device on this account. Choose an encryption password. Every other device must use the same one. It never leaves your devices and cannot be recovered."
            : "Enter the encryption password you use on your other devices. It is different from your login password and never leaves this PC.", 13, true));
        var form = new StackPanel { Spacing = 14, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        var pw = new PasswordBox { Header = "Encryption password" };
        var confirm = new PasswordBox { Header = "Confirm encryption password" };
        var error = Info(InfoBarSeverity.Error, null, "", false);
        var button = new Button { Content = first ? "Create and continue" : "Unlock", MinWidth = 120 };
        Ui.Styled(button, "AccentButtonStyle");
        var ring = new ProgressRing { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        var hint = Ui.Secondary("", 12);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(button);
        actions.Children.Add(ring);
        actions.Children.Add(hint);
        form.Children.Add(pw);
        if (first)
        {
            form.Children.Add(confirm);
        }
        form.Children.Add(error);
        form.Children.Add(actions);
        async void Submit()
        {
            if (_busy)
            {
                return;
            }
            if (string.IsNullOrEmpty(pw.Password))
            {
                error.Message = "Enter the encryption password.";
                error.IsOpen = true;
                return;
            }
            if (first && pw.Password != confirm.Password)
            {
                error.Message = "The passwords do not match.";
                error.IsOpen = true;
                return;
            }
            if (first && pw.Password.Length < 8)
            {
                error.Message = "Use at least 8 characters.";
                error.IsOpen = true;
                return;
            }
            _busy = true;
            button.IsEnabled = false;
            ring.IsActive = true;
            ring.Visibility = Visibility.Visible;
            hint.Text = "Deriving key...";
            error.IsOpen = false;
            try
            {
                var outcome = await _service.SetEncryptionPasswordAsync(pw.Password);
                if (outcome.Result != KeySetupResult.Accepted)
                {
                    error.Message = outcome.Error ?? "The password was not accepted.";
                    error.IsOpen = true;
                    return;
                }
                _busy = false;
                Render();
            }
            catch (Exception ex)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
            }
            finally
            {
                _busy = false;
                button.IsEnabled = true;
                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;
                hint.Text = "";
            }
        }
        button.Click += (_, _) => Submit();
        pw.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && !first)
            {
                Submit();
            }
        };
        confirm.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Submit();
            }
        };
        page.Children.Add(Ui.Card(form, new Thickness(24)));
        var signOut = new HyperlinkButton { Content = "Sign out", Margin = new Thickness(0, 8, 0, 0) };
        signOut.Click += async (_, _) =>
        {
            await _service.SignOutAsync();
            Render();
        };
        page.Children.Add(signOut);
        return page;
    }

    private StackPanel AccountSummaryPage()
    {
        var page = PageRoot("Account");
        var settings = _service.Settings;
        switch (_service.Status)
        {
            case ServiceStatus.UpdateRequired:
                page.Children.Add(Info(InfoBarSeverity.Error, "Update required", "The server uses a newer protocol. Install the latest version of Yikz Clipboard."));
                break;
            case ServiceStatus.TooManyConnections:
                page.Children.Add(Info(InfoBarSeverity.Warning, "Disconnected", "This device has too many open connections. Reconnect when the others are closed."));
                break;
        }
        var warning = _service.ActiveStorageWarning;
        if (warning != null)
        {
            page.Children.Add(Info(InfoBarSeverity.Warning, "Server storage is low", "Free space: " + HistoryEntry.FormatSize(warning.FreeDiskBytes) + ". Large uploads are paused."));
        }
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = new PersonPicture { DisplayName = settings.Username ?? "?", Width = 56, Height = 56 };
        header.Children.Add(avatar);
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        who.Children.Add(Ui.Text(settings.Username ?? "Signed in", 18, true));
        who.Children.Add(Ui.Secondary(settings.ServerUrl, 13));
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        statusRow.Children.Add(Ui.Dot(_service.Status switch
        {
            ServiceStatus.Connected => "YkDotSuccess",
            ServiceStatus.Connecting or ServiceStatus.Reconnecting => "YkDotCaution",
            ServiceStatus.Paused => "YkDotNeutral",
            _ => "YkDotCritical",
        }));
        statusRow.Children.Add(Ui.Secondary(_service.StatusText, 12));
        who.Children.Add(statusRow);
        Grid.SetColumn(who, 1);
        header.Children.Add(who);
        var reconnect = new Button { Content = Ui.IconLabel(Glyphs.Refresh, "Reconnect") };
        reconnect.Click += (_, _) => _service.ReconnectNow("user request");
        Grid.SetColumn(reconnect, 2);
        reconnect.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(reconnect);
        page.Children.Add(Ui.Card(header, new Thickness(20)));

        page.Children.Add(Ui.SectionHeader("This device"));
        var nameBox = new TextBox { Text = settings.DeviceName ?? Environment.MachineName, MinWidth = 220 };
        var save = new Button { Content = "Rename" };
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        namePanel.Children.Add(nameBox);
        namePanel.Children.Add(save);
        save.Click += async (_, _) =>
        {
            var id = _service.DeviceId;
            var name = nameBox.Text.Trim();
            if (id == null || name.Length == 0)
            {
                return;
            }
            save.IsEnabled = false;
            try
            {
                await _service.RenameDeviceAsync(id, name);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Rename failed", ex.Message);
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        page.Children.Add(Ui.SettingCard(Glyphs.Desktop, "Device name", "Shown on your other devices", namePanel));
        page.Children.Add(Ui.SettingCard(Glyphs.Lock, "End-to-end encryption", "Your encryption key is stored with Windows data protection. The password itself is never saved.", new TextBlock()));
        var signOut = new Button { Content = "Sign out" };
        signOut.Click += async (_, _) =>
        {
            var confirmed = await ConfirmAsync("Sign out of Yikz Clipboard?", "This removes the device token and encryption key from this PC and clears the local history cache.", "Sign out");
            if (confirmed)
            {
                await _service.SignOutAsync();
                Render();
            }
        };
        page.Children.Add(Ui.SettingCard(Glyphs.Account, "Sign out", "Revokes this device on the server", signOut));
        return page;
    }

    private StackPanel SyncPage()
    {
        var page = PageRoot("Sync");
        var s = _service.Settings;
        var paused = new ToggleSwitch { IsOn = !s.Paused, OnContent = "On", OffContent = "Paused" };
        paused.Toggled += (_, _) => _host.TogglePauseTo(!paused.IsOn);
        page.Children.Add(Ui.SettingCard(Glyphs.Sync, "Clipboard sync", "Send and receive clipboard items on this PC", paused));
        page.Children.Add(Ui.SectionHeader("What to sync"));
        page.Children.Add(Toggle(Glyphs.Text, "Text and links", null, s.SyncText, v => _service.SettingsStore.Update(x => x.SyncText = v)));
        page.Children.Add(Toggle(Glyphs.Image, "Images", "Screenshots and copied pictures, sent as PNG", s.SyncImages, v => _service.SettingsStore.Update(x => x.SyncImages = v)));
        page.Children.Add(Toggle(Glyphs.Files, "Files", "Copied files from File Explorer. Folders are skipped.", s.SyncFiles, v => _service.SettingsStore.Update(x => x.SyncFiles = v)));
        page.Children.Add(Ui.SectionHeader("Receiving"));
        page.Children.Add(Toggle(Glyphs.Paste, "Place received items on the clipboard", "The newest item from another device is copied here automatically, so it also appears in Win+V", s.AutoApply, v => _service.SettingsStore.Update(x => x.AutoApply = v)));
        var limit = new NumberBox
        {
            Value = Math.Round(s.AutoDownloadLimitBytes / 1048576.0),
            Minimum = 1,
            Maximum = 4096,
            SmallChange = 10,
            LargeChange = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 140,
        };
        limit.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue) && e.NewValue >= 1)
            {
                _service.SettingsStore.Update(x => x.AutoDownloadLimitBytes = (long)(e.NewValue * 1048576));
            }
        };
        page.Children.Add(Ui.SettingCard(Glyphs.Download, "Auto-download limit (MB)", "Larger items show a notification with a Download button instead", limit));
        page.Children.Add(Toggle(Glyphs.Info, "Notify when an item arrives", null, s.NotifyReceived, v => _service.SettingsStore.Update(x => x.NotifyReceived = v)));
        page.Children.Add(Ui.SectionHeader("Privacy"));
        page.Children.Add(Ui.SettingCard(Glyphs.Lock, "Private content is never synced", "Items that password managers mark as private, or exclude from clipboard history, are skipped", new TextBlock()));
        return page;
    }

    private static Border Toggle(string glyph, string title, string? description, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "On", OffContent = "Off" };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return Ui.SettingCard(glyph, title, description, toggle);
    }

    private StackPanel GeneralPage()
    {
        var page = PageRoot("General");
        var s = _service.Settings;
        page.Children.Add(Toggle(Glyphs.Play, "Start with Windows", "Runs quietly in the notification area", s.StartWithWindows, v => _host.SetStartWithWindows(v)));
        var hotkeyBox = new ComboBox { IsEditable = true, MinWidth = 180 };
        foreach (var option in new[] { "Win+Alt+V", "Ctrl+Alt+V", "Ctrl+Shift+Space", "Alt+Shift+V", "Win+Shift+V" })
        {
            hotkeyBox.Items.Add(option);
        }
        hotkeyBox.Text = string.IsNullOrEmpty(_host.ActiveHotkey) ? s.Hotkey : _host.ActiveHotkey;
        var hotkeyError = Info(InfoBarSeverity.Warning, null, "", false);
        void Apply(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, _host.ActiveHotkey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (_host.ChangeHotkey(text.Trim(), out var err))
            {
                hotkeyError.IsOpen = false;
            }
            else
            {
                hotkeyError.Message = err ?? "That shortcut is not available.";
                hotkeyError.IsOpen = true;
            }
        }
        hotkeyBox.SelectionChanged += (_, _) => Apply(hotkeyBox.SelectedItem as string);
        hotkeyBox.TextSubmitted += (_, e) => Apply(e.Text);
        page.Children.Add(Ui.SettingCard(Glyphs.Keyboard, "History shortcut", "Opens the clipboard history from anywhere. Type a combination such as Ctrl+Alt+V.", hotkeyBox));
        page.Children.Add(hotkeyError);
        var level = new ComboBox { MinWidth = 140 };
        level.Items.Add("Info");
        level.Items.Add("Debug");
        level.SelectedIndex = s.LogLevel == "debug" ? 1 : 0;
        level.SelectionChanged += (_, _) =>
        {
            var debug = level.SelectedIndex == 1;
            _service.SettingsStore.Update(x => x.LogLevel = debug ? "debug" : "info");
            _host.Logger.MinimumLevel = debug ? Core.Logging.LogLevel.Debug : Core.Logging.LogLevel.Info;
        };
        page.Children.Add(Ui.SettingCard(Glyphs.Logs, "Log detail", "Debug logs help diagnose sync problems", level));
        var logs = new Button { Content = "Open logs" };
        logs.Click += (_, _) => _host.ShowLogs();
        page.Children.Add(Ui.SettingCard(Glyphs.Logs, "Logs", "Connection, sync and clipboard events", logs));
        return page;
    }

    private StackPanel DevicesPage()
    {
        var page = PageRoot("Devices and Storage");
        var storageHost = new StackPanel { Spacing = 4 };
        page.Children.Add(Ui.SectionHeader("Server storage"));
        page.Children.Add(storageHost);
        var devicesHost = new StackPanel { Spacing = 4 };
        var devicesHeader = new Grid();
        devicesHeader.Children.Add(Ui.SectionHeader("Devices"));
        var refresh = Ui.SubtleButton(Ui.Icon(Glyphs.Refresh, 14), "Refresh");
        refresh.HorizontalAlignment = HorizontalAlignment.Right;
        refresh.VerticalAlignment = VerticalAlignment.Bottom;
        devicesHeader.Children.Add(refresh);
        page.Children.Add(devicesHeader);
        page.Children.Add(devicesHost);
        page.Children.Add(Ui.SectionHeader("This PC"));
        var cacheText = Ui.Secondary("Calculating...", 12, true);
        var clearCache = new Button { Content = "Clear" };
        var cacheRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        cacheRow.Children.Add(cacheText);
        cacheRow.Children.Add(clearCache);
        page.Children.Add(Ui.SettingCard(Glyphs.Folder, "Received files", "Kept for 7 days, up to 2 GB", cacheRow));
        clearCache.Click += async (_, _) =>
        {
            await Task.Run(() => _service.ClearReceivedCache());
            cacheText.Text = HistoryEntry.FormatSize(await Task.Run(() => _service.ReceivedCacheBytes()));
        };
        var resync = new Button { Content = "Rebuild" };
        resync.Click += async (_, _) =>
        {
            if (await ConfirmAsync("Rebuild local history?", "The local cache is cleared and downloaded again from the server. Nothing is deleted on the server.", "Rebuild"))
            {
                _service.ResetLocalHistory();
            }
        };
        page.Children.Add(Ui.SettingCard(Glyphs.History, "Local history cache", _service.History.Count().ToString("N0") + " items cached on this PC", resync));
        var openData = new Button { Content = "Open folder" };
        openData.Click += (_, _) => Shell.Open(_host.Paths.Root);
        page.Children.Add(Ui.SettingCard(Glyphs.OpenFolder, "App data", _host.Paths.Root, openData));
        _ = LoadStorageAsync(storageHost);
        _ = LoadDevicesAsync(devicesHost);
        refresh.Click += (_, _) => _ = LoadDevicesAsync(devicesHost);
        _ = Task.Run(() => _service.ReceivedCacheBytes()).ContinueWith(t =>
        {
            UiThread.Post(_host.Queue, () => cacheText.Text = HistoryEntry.FormatSize(t.Result));
        }, TaskScheduler.Default);
        return page;
    }

    private async Task LoadStorageAsync(StackPanel host)
    {
        host.Children.Clear();
        if (!_service.IsSignedIn)
        {
            host.Children.Add(Ui.Card(Ui.Secondary("Sign in to see server storage.", 13)));
            return;
        }
        host.Children.Add(Ui.Card(new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left }));
        try
        {
            var s = await _service.GetStorageAsync();
            host.Children.Clear();
            if (s.DiskLow)
            {
                host.Children.Add(Info(InfoBarSeverity.Warning, "Server disk is low", "Uploads larger than 1 MB are rejected until space is freed."));
            }
            var panel = new StackPanel { Spacing = 10 };
            var top = new Grid();
            top.Children.Add(Ui.Text(HistoryEntry.FormatSize(s.UsedBytes) + " of " + HistoryEntry.FormatSize(s.LimitBytes) + " used", 14, true));
            var count = Ui.Secondary(s.ItemCount.ToString("N0") + " items, kept " + s.RetentionDays + " days", 12);
            count.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(count);
            panel.Children.Add(top);
            panel.Children.Add(new ProgressBar { Value = s.LimitBytes > 0 ? s.UsedBytes * 100.0 / s.LimitBytes : 0, Maximum = 100, Height = 6 });
            panel.Children.Add(Ui.Secondary("Pinned: " + HistoryEntry.FormatSize(s.PinnedBytes) + " of " + HistoryEntry.FormatSize(s.PinnedLimitBytes) + ". Pinned items are kept until unpinned.", 12, true));
            host.Children.Add(Ui.Card(panel, new Thickness(20)));
        }
        catch (Exception ex)
        {
            host.Children.Clear();
            host.Children.Add(Ui.Card(Ui.Secondary("Storage info unavailable: " + ex.Message, 13, true)));
        }
    }

    private async Task LoadDevicesAsync(StackPanel host)
    {
        host.Children.Clear();
        if (!_service.IsSignedIn)
        {
            return;
        }
        var devices = await _service.RefreshDevicesAsync();
        host.Children.Clear();
        if (devices.Count == 0)
        {
            host.Children.Add(Ui.Card(Ui.Secondary("No devices found.", 13)));
            return;
        }
        foreach (var d in devices)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = Ui.Styled(new Border { Width = 36, Height = 36 }, "YkIconTile");
            tile.Child = Ui.Icon(Glyphs.ForPlatform(d.Platform), 18);
            row.Children.Add(tile);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nameRow.Children.Add(Ui.Text(d.Name, 14, true));
            if (d.Current)
            {
                nameRow.Children.Add(Ui.Keycap("This PC"));
            }
            texts.Children.Add(nameRow);
            var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sub.Children.Add(Ui.Dot(d.Revoked ? "YkDotCritical" : d.Online ? "YkDotSuccess" : "YkDotNeutral", 6));
            sub.Children.Add(Ui.Secondary(d.Revoked ? "Signed out" : d.Online ? "Online" : "Last seen " + Ago(d.LastSeenAt), 12));
            sub.Children.Add(Ui.Tertiary(PlatformName(d.Platform), 12));
            texts.Children.Add(sub);
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            if (!d.Current)
            {
                var remove = new Button { Content = d.Revoked ? "Remove" : "Sign out" };
                var device = d;
                remove.Click += async (_, _) =>
                {
                    var ok = await ConfirmAsync(
                        device.Revoked ? "Remove " + device.Name + "?" : "Sign out " + device.Name + "?",
                        device.Revoked ? "The device record is deleted." : "The device is disconnected and must sign in again.",
                        device.Revoked ? "Remove" : "Sign out");
                    if (!ok)
                    {
                        return;
                    }
                    try
                    {
                        await _service.RevokeDeviceAsync(device.Id);
                    }
                    catch (Exception ex)
                    {
                        await ShowMessageAsync("Could not update the device", ex.Message);
                    }
                    await LoadDevicesAsync(host);
                };
                remove.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
            }
            host.Children.Add(Ui.Card(row, new Thickness(16, 12, 16, 12)));
        }
    }

    private static string PlatformName(string platform) => platform switch
    {
        "macos" => "macOS",
        "android" => "Android",
        "windows" => "Windows",
        "web" => "Web",
        _ => platform,
    };

    private static string Ago(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }
        if (span < TimeSpan.FromHours(1))
        {
            return (int)span.TotalMinutes + " min ago";
        }
        if (span < TimeSpan.FromDays(1))
        {
            return (int)span.TotalHours + " h ago";
        }
        return (int)span.TotalDays + " days ago";
    }

    private StackPanel AboutPage()
    {
        var page = PageRoot("About");
        var head = new Grid { ColumnSpacing = 16 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        try
        {
            if (File.Exists(WindowHelpers.IconPath))
            {
                head.Children.Add(new Image { Width = 48, Height = 48, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(WindowHelpers.IconPath)) });
            }
        }
        catch (Exception)
        {
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(Ui.Text("Yikz Clipboard", 18, true));
        texts.Children.Add(Ui.Secondary("Version " + AppHost.AppVersion + ", protocol " + ProtocolConstants.ProtocolVersion, 12));
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);
        page.Children.Add(Ui.Card(head, new Thickness(20)));
        var serverInfo = Ui.Secondary("Checking...", 12);
        page.Children.Add(Ui.SettingCard(Glyphs.Globe, "Server", _service.Settings.ServerUrl, serverInfo));
        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                var health = await _service.Api.GetHealthAsync();
                text = "Version " + health.ServerVersion + (health.ProtocolVersion == ProtocolConstants.ProtocolVersion ? "" : " (protocol " + health.ProtocolVersion + ")");
            }
            catch (Exception)
            {
                text = "Unreachable";
            }
            UiThread.Post(_host.Queue, () => serverInfo.Text = text);
        });
        var folder = new Button { Content = "Open logs folder" };
        folder.Click += (_, _) => Shell.Open(_host.Paths.LogsDir);
        page.Children.Add(Ui.SettingCard(Glyphs.Logs, "Diagnostics", "Logs rotate automatically", folder));
        return page;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _root.XamlRoot,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = _root.XamlRoot,
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
        }
    }
}
