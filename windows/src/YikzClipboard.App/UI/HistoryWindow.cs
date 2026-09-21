using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using YikzClipboard.App.Interop;
using YikzClipboard.App.Platform;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Protocol;
using YikzClipboard.Core.Storage;
using YikzClipboard.Core.Sync;

namespace YikzClipboard.App.UI;

internal sealed record HeaderRow(string Title);

internal sealed class HistoryWindow : Window
{
    private static readonly string[] KindFilters = ["All Types", "Text", "Links", "Images", "Files"];

    private readonly AppHost _host;
    private readonly ClipboardSyncService _service;
    private readonly Grid _root = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _kind = new();
    private readonly ListView _list = new();
    private readonly Grid _previewHost = new();
    private readonly StackPanel _infoPanel = new() { Spacing = 0 };
    private readonly TextBlock _statusText = Ui.Secondary("", 12);
    private readonly Border _statusDotHost = new() { Width = 8, Height = 8 };
    private readonly TextBlock _toast = Ui.Secondary("", 12);
    private readonly TextBlock _empty = Ui.Secondary("", 13, true);
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Dictionary<string, BitmapImage> _thumbs = new();
    private readonly LinkedList<string> _thumbOrder = new();
    private readonly HashSet<string> _thumbLoading = new();
    private readonly DispatcherQueueTimerWrapper _toastTimer;
    private List<HistoryEntry> _all = new();
    private List<object> _rows = new();
    private HistoryEntry? _selected;
    private IntPtr _previous;
    private CancellationTokenSource? _previewCts;
    private bool _dirty = true;
    private bool _modal;
    private bool _exiting;
    private bool _positioned;
    private bool _focusPending;
    private bool _taskbarMode;

    public HistoryWindow(AppHost host)
    {
        _host = host;
        _service = host.Service;
        Title = "Clipboard History";
        _toastTimer = new DispatcherQueueTimerWrapper(host.Queue, TimeSpan.FromSeconds(2.2), () => _toast.Text = "");
        Build();
        Content = _root;
        WindowHelpers.ApplyBackdrop(this, _root);
        WindowHelpers.SetIcon(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        AppWindow.IsShownInSwitchers = false;
        SetTaskbarMode(host.Settings.Current.ShowInTaskbar);
        AppWindow.Closing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true;
                HideWindow();
            }
        };
        Activated += OnActivated;
        _root.ActualThemeChanged += (_, _) =>
        {
            _list.ItemsSource = null;
            _list.ItemsSource = _rows;
            RestoreSelection();
        };
    }

    public IntPtr Handle => WindowHelpers.Hwnd(this);

    public bool IsShown => AppWindow.IsVisible && !IsMinimized;

    public bool IsMinimized => AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    public bool TaskbarMode => _taskbarMode;

    public void SetTaskbarMode(bool enabled)
    {
        _taskbarMode = enabled;
        try
        {
            AppWindow.IsShownInSwitchers = enabled;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = enabled;
            }
            if (!enabled && AppWindow.IsVisible && IsMinimized)
            {
                AppWindow.Hide();
            }
        }
        catch (Exception ex)
        {
            App.Log?.Warn("history", "could not change the taskbar mode", ex);
        }
    }

    public void ShowInTaskbarMinimized()
    {
        if (!_taskbarMode || AppWindow.IsVisible)
        {
            return;
        }
        if (!_positioned)
        {
            WindowHelpers.CenterOnCursorMonitor(this, 880, 560, 0.35);
            _positioned = true;
        }
        _dirty = true;
        Win32.ShowWindow(Handle, Win32.SW_SHOWMINNOACTIVE);
    }

    public bool IsForeground => Win32.GetForegroundWindow() == Handle;

    private void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.PreviewKeyDown += OnPreviewKeyDown;

        var top = new Grid { Padding = new Thickness(18, 10, 12, 10), ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchIcon = Ui.Icon(Glyphs.Search, 16, "YkSecondaryIcon");
        top.Children.Add(searchIcon);
        _search.PlaceholderText = "Search clipboard history";
        _search.FontSize = 16;
        _search.BorderThickness = new Thickness(0);
        _search.Background = new SolidColorBrush(Colors.Transparent);
        _search.Padding = new Thickness(0, 6, 0, 6);
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.IsSpellCheckEnabled = false;
        _search.TextChanged += (_, _) => ApplyFilter(true);
        _search.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        _search.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        _search.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        Grid.SetColumn(_search, 1);
        top.Children.Add(_search);
        foreach (var k in KindFilters)
        {
            _kind.Items.Add(k);
        }
        _kind.SelectedIndex = 0;
        _kind.MinWidth = 128;
        _kind.VerticalAlignment = VerticalAlignment.Center;
        _kind.SelectionChanged += (_, _) =>
        {
            ApplyFilter(true);
            _search.Focus(FocusState.Programmatic);
        };
        ToolTipService.SetToolTip(_kind, "Filter by type (Ctrl+T)");
        Grid.SetColumn(_kind, 2);
        top.Children.Add(_kind);
        _root.Children.Add(top);

        var divider = Ui.Divider();
        Grid.SetRow(divider, 1);
        _root.Children.Add(divider);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.40, GridUnitType.Star), MinWidth = 260 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.60, GridUnitType.Star) });
        Grid.SetRow(body, 2);

        var listHost = new Grid();
        _list.SelectionMode = ListViewSelectionMode.Single;
        _list.IsItemClickEnabled = true;
        _list.Padding = new Thickness(6, 6, 6, 6);
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid /></DataTemplate>");
        _list.ItemContainerStyle = BuildItemStyle();
        _list.ContainerContentChanging += OnContainerContentChanging;
        _list.SelectionChanged += OnSelectionChanged;
        _list.DoubleTapped += (_, _) => _ = CopySelectedAsync(false);
        _list.IsTabStop = false;
        listHost.Children.Add(_list);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.Margin = new Thickness(24);
        _empty.Visibility = Visibility.Collapsed;
        listHost.Children.Add(_empty);
        body.Children.Add(listHost);

        var vdivider = Ui.Divider(true);
        Grid.SetColumn(vdivider, 1);
        body.Children.Add(vdivider);

        var detail = new Grid();
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewHost.Padding = new Thickness(0);
        detail.Children.Add(_previewHost);
        var infoScroll = new Border { Padding = new Thickness(20, 4, 20, 12), Child = _infoPanel };
        Grid.SetRow(infoScroll, 1);
        detail.Children.Add(infoScroll);
        Grid.SetColumn(detail, 2);
        body.Children.Add(detail);
        _root.Children.Add(body);

        var bottomDivider = Ui.Divider();
        Grid.SetRow(bottomDivider, 3);
        _root.Children.Add(bottomDivider);

        var bottom = new Grid { Padding = new Thickness(16, 6, 8, 6), ColumnSpacing = 10, MinHeight = 44 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusDotHost.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_statusDotHost);
        Grid.SetColumn(_statusText, 1);
        bottom.Children.Add(_statusText);
        _toast.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_toast, 2);
        bottom.Children.Add(_toast);
        Grid.SetColumn(_actions, 3);
        bottom.Children.Add(_actions);
        Grid.SetRow(bottom, 4);
        _root.Children.Add(bottom);
        UpdateStatus();
    }

    private static Style BuildItemStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        return style;
    }

    public void OnServiceChanged()
    {
        _dirty = true;
        UpdateStatus();
        if (IsShown)
        {
            Reload();
        }
    }

    public void ShowAt(IntPtr previous)
    {
        _previous = previous;
        if (!_positioned || !IsShown)
        {
            WindowHelpers.CenterOnCursorMonitor(this, 880, 560, 0.35);
            _positioned = true;
        }
        if (_dirty)
        {
            Reload();
        }
        _focusPending = true;
        WindowHelpers.BringToFront(this);
        _search.Focus(FocusState.Programmatic);
        _search.SelectAll();
        UpdateStatus();
    }

    public void HideWindow()
    {
        _previewCts?.Cancel();
        if (_taskbarMode && !_exiting && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (!IsMinimized)
            {
                presenter.Minimize();
            }
            return;
        }
        AppWindow.Hide();
    }

    public void CloseForExit()
    {
        _exiting = true;
        try
        {
            Close();
        }
        catch (Exception)
        {
        }
    }

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_modal && IsShown)
            {
                HideWindow();
            }
            return;
        }
        if (_focusPending)
        {
            _focusPending = false;
            _search.Focus(FocusState.Programmatic);
            _search.SelectAll();
        }
        else if (_taskbarMode && !_modal)
        {
            _previous = IntPtr.Zero;
            if (_dirty)
            {
                Reload();
                UpdateStatus();
            }
        }
    }

    private void Reload()
    {
        _dirty = false;
        var limit = Math.Max(100, _service.Settings.LocalHistoryLimit);
        _all = _service.History.List(limit);
        ApplyFilter(false);
    }

    private void ApplyFilter(bool resetSelection)
    {
        var terms = _search.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = _kind.SelectedIndex;
        var previousId = _selected?.Id;
        var filtered = _all.Where(e =>
        {
            var ok = kind switch
            {
                1 => e.Kind == EntryKind.Text,
                2 => e.Kind == EntryKind.Link,
                3 => e.Kind == EntryKind.Image,
                4 => e.Kind == EntryKind.Files,
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
            if (terms.Length == 0)
            {
                return true;
            }
            var hay = e.SearchText;
            foreach (var t in terms)
            {
                if (!hay.Contains(t, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }).ToList();
        var rows = new List<object>(filtered.Count + 8);
        var pinned = filtered.Where(e => e.Pinned).ToList();
        if (pinned.Count > 0)
        {
            rows.Add(new HeaderRow("Pinned"));
            rows.AddRange(pinned);
        }
        string? group = null;
        foreach (var e in filtered.Where(e => !e.Pinned))
        {
            var day = Ui.RelativeDay(e.Header.CreatedAt);
            if (day != group)
            {
                rows.Add(new HeaderRow(day));
                group = day;
            }
            rows.Add(e);
        }
        _rows = rows;
        _list.ItemsSource = _rows;
        if (filtered.Count == 0)
        {
            _empty.Text = _all.Count == 0
                ? "Nothing here yet.\nCopy something on any device and it shows up here."
                : "No items match your search.";
            _empty.Visibility = Visibility.Visible;
            _selected = null;
            ShowPreview(null);
            return;
        }
        _empty.Visibility = Visibility.Collapsed;
        var target = !resetSelection && previousId != null ? filtered.FirstOrDefault(e => e.Id == previousId) : null;
        Select(target ?? filtered[0]);
    }

    private void RestoreSelection()
    {
        if (_selected != null)
        {
            var match = _rows.OfType<HistoryEntry>().FirstOrDefault(e => e.Id == _selected.Id);
            if (match != null)
            {
                Select(match);
            }
        }
    }

    private void Select(HistoryEntry entry)
    {
        _list.SelectedItem = entry;
        _list.ScrollIntoView(entry);
    }

    private void MoveSelection(int delta)
    {
        var items = _rows.OfType<HistoryEntry>().ToList();
        if (items.Count == 0)
        {
            return;
        }
        var index = _selected == null ? -1 : items.FindIndex(e => e.Id == _selected.Id);
        var next = Math.Clamp(index + delta, 0, items.Count - 1);
        if (index < 0)
        {
            next = 0;
        }
        Select(items[next]);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_list.SelectedItem is HeaderRow)
        {
            var index = _list.SelectedIndex;
            var next = _rows.Skip(index + 1).OfType<HistoryEntry>().FirstOrDefault() ?? _rows.Take(index).OfType<HistoryEntry>().LastOrDefault();
            if (next != null)
            {
                Select(next);
            }
            return;
        }
        var entry = _list.SelectedItem as HistoryEntry;
        if (entry != null && _selected != null && entry.Id == _selected.Id && entry.Pinned == _selected.Pinned && entry.MetaState == _selected.MetaState)
        {
            _selected = entry;
            return;
        }
        _selected = entry;
        ShowPreview(entry);
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }
        if (args.ItemContainer.ContentTemplateRoot is not Grid root)
        {
            return;
        }
        args.Handled = true;
        root.Children.Clear();
        root.ColumnDefinitions.Clear();
        var container = args.ItemContainer;
        if (args.Item is HeaderRow header)
        {
            container.IsHitTestVisible = false;
            container.IsTabStop = false;
            container.Tag = null;
            root.Height = double.NaN;
            var tb = Ui.Text(header.Title, 12, true, "YkSecondaryText");
            tb.Margin = new Thickness(0, 10, 0, 4);
            root.Children.Add(tb);
            return;
        }
        if (args.Item is not HistoryEntry entry)
        {
            return;
        }
        container.IsHitTestVisible = true;
        container.IsTabStop = true;
        container.Tag = entry.Id;
        root.Height = 40;
        root.ColumnSpacing = 10;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Ui.Styled(new Border { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center }, "YkIconTile");
        if (entry.Kind == EntryKind.Image && _thumbs.TryGetValue(entry.Id, out var cached))
        {
            tile.Child = ThumbImage(cached);
        }
        else
        {
            tile.Child = KindIcon(entry);
            if (entry.Kind == EntryKind.Image && entry.Header.HasThumb)
            {
                _ = LoadThumbAsync(entry, container, tile);
            }
        }
        root.Children.Add(tile);
        var title = Ui.Text(entry.Title, 13);
        title.MaxLines = 1;
        Grid.SetColumn(title, 1);
        root.Children.Add(title);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Header.DeviceId != _service.DeviceId)
        {
            right.Children.Add(Ui.Icon(Glyphs.ForPlatform(_service.DevicePlatform(entry.Header.DeviceId)), 11, "YkSecondaryIcon"));
        }
        right.Children.Add(Ui.Tertiary(Ui.ShortTime(entry.Header.CreatedAt), 11));
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
    }

    private static FontIcon KindIcon(HistoryEntry entry)
    {
        var icon = Ui.Icon(Glyphs.ForKind(entry.Kind), 14);
        if (entry.Kind == EntryKind.Files && entry.Meta?.Files is { Count: 1 } files)
        {
            icon.Glyph = Glyphs.ForFileName(files[0].Name);
        }
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    private static Image ThumbImage(BitmapImage source) => new()
    {
        Source = source,
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private async Task LoadThumbAsync(HistoryEntry entry, Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container, Border tile)
    {
        if (!_thumbLoading.Add(entry.Id))
        {
            return;
        }
        try
        {
            var bytes = await _service.GetThumbnailAsync(entry);
            if (bytes == null)
            {
                return;
            }
            var image = await WinImageTools.ToImageSourceAsync(bytes, 96);
            if (image == null)
            {
                return;
            }
            RememberThumb(entry.Id, image);
            if (container.Tag as string == entry.Id)
            {
                tile.Child = ThumbImage(image);
            }
            if (_selected?.Id == entry.Id)
            {
                ShowPreview(entry);
            }
        }
        catch (Exception ex)
        {
            App.Log?.Debug("history", "thumbnail failed: " + ex.Message);
        }
        finally
        {
            _thumbLoading.Remove(entry.Id);
        }
    }

    private void RememberThumb(string id, BitmapImage image)
    {
        if (_thumbs.ContainsKey(id))
        {
            _thumbOrder.Remove(id);
        }
        _thumbs[id] = image;
        _thumbOrder.AddLast(id);
        while (_thumbOrder.Count > 300)
        {
            var oldest = _thumbOrder.First!.Value;
            _thumbOrder.RemoveFirst();
            _thumbs.Remove(oldest);
        }
    }

    private void ShowPreview(HistoryEntry? entry)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;
        _previewHost.Children.Clear();
        _infoPanel.Children.Clear();
        BuildActions(entry);
        if (entry == null)
        {
            return;
        }
        UIElement content;
        switch (entry.Kind)
        {
            case EntryKind.Text:
                content = TextPreview(entry, ct);
                break;
            case EntryKind.Link:
                content = LinkPreview(entry);
                break;
            case EntryKind.Image:
                content = ImagePreview(entry, ct);
                break;
            case EntryKind.Files:
                content = FilesPreview(entry);
                break;
            default:
                content = MessagePreview(Glyphs.Warning, entry.MetaState == MetaState.Corrupt
                    ? "This item could not be decrypted. It may have been created with a different encryption password."
                    : "This item was created by a newer app version.");
                break;
        }
        _previewHost.Children.Add(content);
        BuildInfo(entry);
    }

    private UIElement TextPreview(HistoryEntry entry, CancellationToken ct)
    {
        var text = new TextBlock
        {
            Text = entry.Meta?.Preview ?? "",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 14,
            LineHeight = 22,
        };
        var mono = LooksLikeCode(entry.Meta?.Preview ?? "");
        if (mono && Application.Current.Resources.TryGetValue("YkMonoFont", out var font) && font is FontFamily ff)
        {
            text.FontFamily = ff;
            text.FontSize = 13;
        }
        var scroll = new ScrollViewer
        {
            Content = text,
            Padding = new Thickness(22, 18, 22, 18),
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var fullAvailable = entry.IsInline || entry.Header.Size <= _service.Settings.AutoDownloadLimitBytes;
        if (fullAvailable && TextPreviewIsTruncated(entry))
        {
            _ = LoadFullTextAsync(entry, text, ct);
        }
        return scroll;
    }

    private static bool TextPreviewIsTruncated(HistoryEntry entry)
    {
        var preview = entry.Meta?.Preview ?? "";
        return System.Text.Encoding.UTF8.GetByteCount(preview) < entry.Header.Size;
    }

    private static bool LooksLikeCode(string text)
    {
        if (text.Length < 20)
        {
            return false;
        }
        var symbols = text.Count(c => c is '{' or '}' or ';' or '(' or ')' or '<' or '>' or '=' or '[' or ']');
        return text.Contains('\n') && symbols > text.Length / 25;
    }

    private async Task LoadFullTextAsync(HistoryEntry entry, TextBlock target, CancellationToken ct)
    {
        try
        {
            var full = await _service.GetTextAsync(entry.Id, ct);
            if (full == null || ct.IsCancellationRequested)
            {
                return;
            }
            target.Text = full.Length > 200_000 ? full[..200_000] + "\n\n..." : full;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log?.Debug("history", "full text unavailable: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private UIElement LinkPreview(HistoryEntry entry)
    {
        var url = (entry.Meta?.Preview ?? "").Trim();
        var panel = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28) };
        var tile = Ui.Styled(new Border { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Left }, "YkIconTile");
        tile.Child = Ui.Icon(Glyphs.Link, 24, "YkAccentIcon");
        panel.Children.Add(tile);
        string host = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }
        if (!string.IsNullOrEmpty(host))
        {
            panel.Children.Add(Ui.Text(host, 20, true));
        }
        var link = Ui.Secondary(url, 13, true);
        link.IsTextSelectionEnabled = true;
        panel.Children.Add(link);
        var open = new Button { Content = Ui.IconLabel(Glyphs.OpenLink, "Open in browser"), Margin = new Thickness(0, 6, 0, 0) };
        open.Click += (_, _) =>
        {
            Shell.Open(url);
            HideWindow();
        };
        panel.Children.Add(open);
        return panel;
    }

    private UIElement ImagePreview(HistoryEntry entry, CancellationToken ct)
    {
        var grid = new Grid { Padding = new Thickness(20) };
        var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (_thumbs.TryGetValue(entry.Id, out var thumb))
        {
            image.Source = thumb;
        }
        var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var frame = new Border { CornerRadius = new CornerRadius(8), Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(frame);
        var canLoad = entry.IsInline || entry.Header.Size <= _service.Settings.AutoDownloadLimitBytes;
        if (canLoad)
        {
            grid.Children.Add(ring);
            _ = LoadFullImageAsync(entry, image, ring, ct);
        }
        else
        {
            var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
            var button = new Button { Content = Ui.IconLabel(Glyphs.Download, "Load full image (" + HistoryEntry.FormatSize(entry.Header.Size) + ")") };
            button.Click += (_, _) =>
            {
                panel.Children.Clear();
                panel.Children.Add(ring);
                _ = LoadFullImageAsync(entry, image, ring, ct);
            };
            panel.Children.Add(button);
            grid.Children.Add(panel);
        }
        return grid;
    }

    private async Task LoadFullImageAsync(HistoryEntry entry, Image target, ProgressRing ring, CancellationToken ct)
    {
        try
        {
            using var content = await _service.FetchContentAsync(entry.Id, null, ct);
            if (content == null || ct.IsCancellationRequested)
            {
                return;
            }
            var bytes = content.ReadAll();
            var width = entry.Meta?.Image?.Width ?? 0;
            var source = await WinImageTools.ToImageSourceAsync(bytes, width > 1600 ? 1600 : null);
            if (source != null && !ct.IsCancellationRequested)
            {
                target.Source = source;
                if (width > 0)
                {
                    var scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
                    target.MaxWidth = width / scale;
                    target.MaxHeight = (entry.Meta?.Image?.Height ?? 0) / scale;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Log?.Warn("history", "image preview failed: " + ex.Message);
            if (!ct.IsCancellationRequested)
            {
                Toast("Could not load the image");
            }
        }
        finally
        {
            ring.IsActive = false;
            ring.Visibility = Visibility.Collapsed;
        }
    }

    private UIElement FilesPreview(HistoryEntry entry)
    {
        var files = entry.Meta?.Files ?? new List<FileInfoEntry>();
        var stack = new StackPanel { Spacing = 2 };
        foreach (var f in files.Take(200))
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = Ui.Styled(new Border { Width = 32, Height = 32 }, "YkIconTile");
            tile.Child = Ui.Icon(Glyphs.ForFileName(f.Name), 16);
            row.Children.Add(tile);
            var name = Ui.Text(f.Name, 13);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var size = Ui.Tertiary(HistoryEntry.FormatSize(f.Size), 12);
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            stack.Children.Add(row);
        }
        if (files.Count > 200)
        {
            stack.Children.Add(Ui.Secondary("and " + (files.Count - 200) + " more", 12));
        }
        var outer = new StackPanel { Spacing = 12, Margin = new Thickness(18, 16, 18, 16) };
        outer.Children.Add(stack);
        var received = Path.Combine(_host.Paths.ReceivedDir, entry.Id);
        if (Directory.Exists(received))
        {
            var reveal = new Button { Content = Ui.IconLabel(Glyphs.OpenFolder, "Show in File Explorer") };
            reveal.Click += (_, _) =>
            {
                var first = Directory.EnumerateFiles(received).FirstOrDefault();
                if (first != null)
                {
                    Shell.RevealInExplorer(first);
                }
                else
                {
                    Shell.Open(received);
                }
            };
            outer.Children.Add(reveal);
        }
        else if (!entry.IsInline && entry.Header.Size > _service.Settings.AutoDownloadLimitBytes)
        {
            outer.Children.Add(Ui.Secondary("Large item. Copy or Save downloads it (" + HistoryEntry.FormatSize(entry.Header.Size) + ").", 12, true));
        }
        return new ScrollViewer { Content = outer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static UIElement MessagePreview(string glyph, string message)
    {
        var panel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360 };
        var icon = Ui.Icon(glyph, 28, "YkSecondaryIcon");
        panel.Children.Add(icon);
        var text = Ui.Secondary(message, 13, true);
        text.TextAlignment = TextAlignment.Center;
        panel.Children.Add(text);
        return panel;
    }

    private void BuildInfo(HistoryEntry entry)
    {
        _infoPanel.Children.Add(Ui.Divider());
        var header = Ui.Text("Information", 12, true, "YkSectionHeader");
        header.Margin = new Thickness(0, 12, 0, 6);
        _infoPanel.Children.Add(header);
        var device = _service.DeviceName(entry.Header.DeviceId);
        AddInfo("Source", device, Glyphs.ForPlatform(_service.DevicePlatform(entry.Header.DeviceId)));
        if (!string.IsNullOrEmpty(entry.Meta?.SourceApp))
        {
            AddInfo("Application", entry.Meta!.SourceApp!, null);
        }
        AddInfo("Type", entry.KindLabel, null);
        if (entry.Meta?.Image is { } img)
        {
            AddInfo("Dimensions", img.Width + " x " + img.Height, null);
        }
        if (entry.Meta?.Files is { Count: > 1 } files)
        {
            AddInfo("Files", files.Count.ToString(), null);
        }
        if (entry.Kind is EntryKind.Text or EntryKind.Link && entry.Meta != null && !TextPreviewIsTruncated(entry))
        {
            AddInfo("Characters", Core.Content.TextPreview.CountCodePoints(entry.Meta.Preview).ToString("N0"), null);
        }
        AddInfo("Size", HistoryEntry.FormatSize(entry.Header.Size), null);
        AddInfo("Copied", Ui.LongTime(entry.Header.CreatedAt), null);
        AddInfo("Pinned", entry.Pinned ? "Yes" : "No", entry.Pinned ? Glyphs.Pin : null);
    }

    private void AddInfo(string label, string value, string? glyph)
    {
        var row = new Grid { MinHeight = 28, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Ui.Secondary(label, 12));
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null)
        {
            right.Children.Add(Ui.Icon(glyph, 12, "YkSecondaryIcon"));
        }
        var text = Ui.Text(value, 12);
        text.IsTextSelectionEnabled = true;
        right.Children.Add(text);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        _infoPanel.Children.Add(row);
    }

    private void BuildActions(HistoryEntry? entry)
    {
        _actions.Children.Clear();
        if (entry == null)
        {
            return;
        }
        AddAction("Copy", new[] { "Enter" }, () => _ = CopySelectedAsync(false));
        AddAction("Paste", new[] { "Ctrl", "Enter" }, () => _ = CopySelectedAsync(true));
        AddAction(entry.Pinned ? "Unpin" : "Pin", new[] { "Ctrl", "P" }, () => _ = TogglePinAsync());
        AddAction("Delete", new[] { "Del" }, () => _ = DeleteSelectedAsync());
        AddAction("Save As", new[] { "Ctrl", "S" }, () => _ = SaveSelectedAsync());
    }

    private void AddAction(string label, string[] keys, Action action)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(Ui.Text(label, 12));
        panel.Children.Add(Ui.Keys(keys));
        var button = Ui.SubtleButton(panel);
        button.Padding = new Thickness(8, 4, 6, 4);
        button.Click += (_, _) => action();
        _actions.Children.Add(button);
    }

    private void UpdateStatus()
    {
        var status = _service.Status;
        _statusDotHost.Child = Ui.Dot(status switch
        {
            ServiceStatus.Connected => "YkDotSuccess",
            ServiceStatus.Connecting or ServiceStatus.Reconnecting => "YkDotCaution",
            ServiceStatus.Paused => "YkDotNeutral",
            _ => "YkDotCritical",
        });
        var count = _all.Count;
        _statusText.Text = _service.StatusText + (count > 0 ? "  \u00b7  " + count.ToString("N0") + (count == 1 ? " item" : " items") : "");
    }

    private void Toast(string text)
    {
        _toast.Text = text;
        _toastTimer.Restart();
    }

    private static bool IsDown(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (_kind.IsDropDownOpen)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(_search.Text))
                {
                    _search.Text = "";
                }
                else
                {
                    HideWindow();
                }
                e.Handled = true;
                break;
            case VirtualKey.Down:
                if (_kind.IsDropDownOpen)
                {
                    return;
                }
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                if (_kind.IsDropDownOpen)
                {
                    return;
                }
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                MoveSelection(8);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                MoveSelection(-8);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (_kind.IsDropDownOpen)
                {
                    return;
                }
                _ = CopySelectedAsync(ctrl);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                if (ReferenceEquals(FocusManager.GetFocusedElement(_root.XamlRoot), _search) && _search.Text.Length > 0 && _search.SelectionStart < _search.Text.Length)
                {
                    return;
                }
                _ = DeleteSelectedAsync();
                e.Handled = true;
                break;
            case VirtualKey.P when ctrl:
                _ = TogglePinAsync();
                e.Handled = true;
                break;
            case VirtualKey.S when ctrl:
                _ = SaveSelectedAsync();
                e.Handled = true;
                break;
            case VirtualKey.F when ctrl:
            case VirtualKey.L when ctrl:
                _search.Focus(FocusState.Keyboard);
                _search.SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.T when ctrl:
                _kind.SelectedIndex = (_kind.SelectedIndex + 1) % KindFilters.Length;
                e.Handled = true;
                break;
            case VirtualKey.Home when ctrl:
                MoveSelection(-100000);
                e.Handled = true;
                break;
            case VirtualKey.End when ctrl:
                MoveSelection(100000);
                e.Handled = true;
                break;
        }
    }

    private async Task CopySelectedAsync(bool paste)
    {
        var entry = _selected;
        if (entry == null)
        {
            return;
        }
        var previous = _previous;
        if (!paste || previous == IntPtr.Zero)
        {
            HideWindow();
        }
        else
        {
            Toast("Copying...");
        }
        try
        {
            var ok = await _service.CopyToClipboardAsync(entry.Id);
            if (!ok)
            {
                HideWindow();
                _host.Notifier.Problem("Could not copy", "The item could not be downloaded or verified.");
                return;
            }
            if (paste)
            {
                if (previous != IntPtr.Zero && Win32.IsWindow(previous))
                {
                    Win32.SetForegroundWindow(previous);
                }
                HideWindow();
                var pasted = await ForegroundTracker.PasteIntoAsync(previous);
                if (!pasted)
                {
                    App.Log?.Info("history", "no previous window to paste into, item copied");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log?.Error("history", "copy failed", ex);
            _host.Notifier.Problem("Could not copy", ex.Message);
        }
    }

    private async Task TogglePinAsync()
    {
        var entry = _selected;
        if (entry == null)
        {
            return;
        }
        try
        {
            await _service.SetPinnedAsync(entry.Id, !entry.Pinned);
            Toast(entry.Pinned ? "Unpinned" : "Pinned");
            _dirty = true;
            Reload();
        }
        catch (Exception ex)
        {
            Toast("Pin failed: " + ex.Message);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var entry = _selected;
        if (entry == null)
        {
            return;
        }
        var items = _rows.OfType<HistoryEntry>().ToList();
        var index = items.FindIndex(e => e.Id == entry.Id);
        try
        {
            await _service.DeleteAsync(entry.Id);
            _all.RemoveAll(e => e.Id == entry.Id);
            ApplyFilter(true);
            var remaining = _rows.OfType<HistoryEntry>().ToList();
            if (remaining.Count > 0)
            {
                Select(remaining[Math.Clamp(index, 0, remaining.Count - 1)]);
            }
            Toast("Deleted");
            UpdateStatus();
        }
        catch (Exception ex)
        {
            Toast("Delete failed: " + ex.Message);
        }
    }

    private async Task SaveSelectedAsync()
    {
        var entry = _selected;
        if (entry == null || entry.Meta == null)
        {
            return;
        }
        _modal = true;
        try
        {
            string? target;
            if (entry.Header.Kind == ProtocolConstants.KindFiles)
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
                var folder = await picker.PickSingleFolderAsync();
                target = folder?.Path;
            }
            else
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id);
                if (entry.Header.Kind == ProtocolConstants.KindImage)
                {
                    picker.SuggestedFileName = "Image " + entry.Header.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HHmmss");
                    picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
                }
                else
                {
                    picker.SuggestedFileName = "Clipboard " + entry.Header.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HHmmss");
                    picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
                }
                var file = await picker.PickSaveFileAsync();
                target = file?.Path;
            }
            if (string.IsNullOrEmpty(target))
            {
                return;
            }
            Toast("Saving...");
            var paths = await _service.SaveAsync(entry.Id, target);
            Toast("Saved");
            if (paths.Count > 0)
            {
                Shell.RevealInExplorer(paths[0]);
            }
        }
        catch (Exception ex)
        {
            App.Log?.Error("history", "save failed", ex);
            Toast("Save failed: " + ex.Message);
        }
        finally
        {
            _modal = false;
        }
    }
}

internal sealed class DispatcherQueueTimerWrapper
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherQueueTimerWrapper(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => tick();
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void EnsureStarted()
    {
        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }
}
