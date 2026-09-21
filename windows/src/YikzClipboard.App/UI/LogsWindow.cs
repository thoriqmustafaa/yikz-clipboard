using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using YikzClipboard.App.Platform;
using YikzClipboard.Core.Logging;

namespace YikzClipboard.App.UI;

internal sealed class LogsWindow : Window
{
    private readonly AppHost _host;
    private readonly Logger _logger;
    private readonly Grid _root = new();
    private readonly ListView _list = new();
    private readonly TextBox _filter = new();
    private readonly ComboBox _level = new();
    private readonly ToggleButton _follow = new();
    private readonly TextBlock _count = Ui.Secondary("", 12);
    private readonly ObservableCollection<LogEntry> _visible = new();
    private readonly List<LogEntry> _pending = new();
    private readonly DispatcherQueueTimerWrapper _flushTimer;
    private FontFamily? _mono;

    public LogsWindow(AppHost host)
    {
        _host = host;
        _logger = host.Logger;
        Title = "Yikz Clipboard Logs";
        _flushTimer = new DispatcherQueueTimerWrapper(host.Queue, TimeSpan.FromMilliseconds(200), FlushPending);
        if (Application.Current.Resources.TryGetValue("YkMonoFont", out var font))
        {
            _mono = font as FontFamily;
        }
        Build();
        Content = _root;
        WindowHelpers.ApplyBackdrop(this, _root);
        WindowHelpers.SetIcon(this);
        WindowHelpers.ConfigureTitleBar(this, _titleDrag!);
        WindowHelpers.CenterOnCursorMonitor(this, 1040, 680, 0.5);
        _logger.EntryAdded += OnEntryAdded;
        Closed += (_, _) => _logger.EntryAdded -= OnEntryAdded;
        Reload();
    }

    private Grid? _titleDrag;

    private void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = WindowHelpers.TitleBar("Yikz Clipboard Logs", out var drag);
        _titleDrag = drag;
        _root.Children.Add(title);

        var toolbar = new Grid { Padding = new Thickness(16, 4, 16, 10), ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 420 });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _filter.PlaceholderText = "Filter logs";
        _filter.TextChanged += (_, _) => Reload();
        toolbar.Children.Add(_filter);
        foreach (var level in new[] { "All levels", "Info and above", "Warnings and errors", "Errors only" })
        {
            _level.Items.Add(level);
        }
        _level.SelectedIndex = 0;
        _level.MinWidth = 170;
        _level.SelectionChanged += (_, _) => Reload();
        Grid.SetColumn(_level, 1);
        toolbar.Children.Add(_level);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _follow.Content = Ui.IconLabel(Glyphs.Download, "Follow");
        _follow.IsChecked = true;
        ToolTipService.SetToolTip(_follow, "Scroll to new entries");
        buttons.Children.Add(_follow);
        buttons.Children.Add(ToolButton(Glyphs.Copy, "Copy", CopyVisible));
        buttons.Children.Add(ToolButton(Glyphs.Export, "Export", () => _ = ExportAsync()));
        buttons.Children.Add(ToolButton(Glyphs.OpenFolder, "Open folder", () => Shell.Open(_host.Paths.LogsDir)));
        buttons.Children.Add(ToolButton(Glyphs.Clear, "Clear", () =>
        {
            _logger.ClearBuffer();
            Reload();
        }));
        Grid.SetColumn(buttons, 3);
        toolbar.Children.Add(buttons);
        Grid.SetRow(toolbar, 1);
        _root.Children.Add(toolbar);

        _list.SelectionMode = ListViewSelectionMode.Extended;
        _list.ItemsSource = _visible;
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid /></DataTemplate>");
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 1, 12, 1)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle = style;
        _list.ContainerContentChanging += OnContainerContentChanging;
        var listBorder = Ui.Styled(new Border { Child = _list, Margin = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(8) }, "YkCard");
        listBorder.Padding = new Thickness(0, 4, 0, 4);
        Grid.SetRow(listBorder, 2);
        _root.Children.Add(listBorder);

        var footer = new Grid { Padding = new Thickness(18, 8, 18, 10) };
        footer.Children.Add(_count);
        var path = Ui.Tertiary(_host.Paths.LogsDir, 12);
        path.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(path);
        Grid.SetRow(footer, 3);
        _root.Children.Add(footer);
    }

    private static Button ToolButton(string glyph, string label, Action action)
    {
        var button = new Button { Content = Ui.IconLabel(glyph, label) };
        button.Click += (_, _) => action();
        return button;
    }

    public void ShowWindow()
    {
        WindowHelpers.BringToFront(this);
        _filter.Focus(FocusState.Programmatic);
    }

    private LogLevel MinLevel => _level.SelectedIndex switch
    {
        1 => LogLevel.Info,
        2 => LogLevel.Warning,
        3 => LogLevel.Error,
        _ => LogLevel.Debug,
    };

    private bool Matches(LogEntry e)
    {
        if (e.Level < MinLevel)
        {
            return false;
        }
        var q = _filter.Text.Trim();
        return q.Length == 0 || e.Message.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Reload()
    {
        _visible.Clear();
        foreach (var e in _logger.Snapshot().Where(Matches))
        {
            _visible.Add(e);
        }
        UpdateCount();
        ScrollToEnd();
    }

    private void OnEntryAdded(LogEntry entry)
    {
        lock (_pending)
        {
            _pending.Add(entry);
        }
        UiThread.Post(_host.Queue, () => _flushTimer.EnsureStarted());
    }

    private void FlushPending()
    {
        List<LogEntry> batch;
        lock (_pending)
        {
            batch = new List<LogEntry>(_pending);
            _pending.Clear();
        }
        foreach (var e in batch.Where(Matches))
        {
            _visible.Add(e);
        }
        while (_visible.Count > 5000)
        {
            _visible.RemoveAt(0);
        }
        UpdateCount();
        if (batch.Count > 0)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (_follow.IsChecked == true && _visible.Count > 0)
        {
            _list.ScrollIntoView(_visible[^1]);
        }
    }

    private void UpdateCount()
    {
        _count.Text = _visible.Count.ToString("N0") + " entries";
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid root || args.Item is not LogEntry e)
        {
            return;
        }
        args.Handled = true;
        root.Children.Clear();
        root.ColumnDefinitions.Clear();
        root.ColumnSpacing = 12;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var time = Cell(e.Time.ToLocalTime().ToString("HH:mm:ss.fff"), "YkTertiaryText");
        root.Children.Add(time);
        var level = Cell(LogEntry.LevelTag(e.Level), e.Level switch
        {
            LogLevel.Error => null,
            LogLevel.Warning => null,
            LogLevel.Debug => "YkTertiaryText",
            _ => "YkSecondaryText",
        });
        if (e.Level >= LogLevel.Warning)
        {
            level.Foreground = Ui.Brush(e.Level == LogLevel.Error ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush") ?? level.Foreground;
            level.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
        Grid.SetColumn(level, 1);
        root.Children.Add(level);
        var category = Cell(e.Category, "YkSecondaryText");
        Grid.SetColumn(category, 2);
        root.Children.Add(category);
        var message = Cell(e.Message, null);
        message.TextWrapping = TextWrapping.Wrap;
        message.TextTrimming = TextTrimming.None;
        message.IsTextSelectionEnabled = true;
        Grid.SetColumn(message, 3);
        root.Children.Add(message);
    }

    private TextBlock Cell(string text, string? style)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 3) };
        if (_mono != null)
        {
            tb.FontFamily = _mono;
        }
        if (style != null)
        {
            Ui.Styled(tb, style);
        }
        return tb;
    }

    private string VisibleText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine(e.Format());
        }
        return sb.ToString();
    }

    private void CopyVisible()
    {
        var selected = _list.SelectedItems.OfType<LogEntry>().ToList();
        var text = VisibleText(selected.Count > 0 ? selected : _visible);
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async Task ExportAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = "yikz-clipboard-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("Log file", new List<string> { ".log" });
            picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
            var result = await picker.PickSaveFileAsync();
            if (result == null || string.IsNullOrEmpty(result.Path))
            {
                return;
            }
            await _logger.FlushAsync();
            var sb = new StringBuilder();
            foreach (var file in _logger.LogFiles())
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    sb.Append(await reader.ReadToEndAsync());
                }
                catch (Exception)
                {
                }
            }
            if (sb.Length == 0)
            {
                sb.Append(VisibleText(_logger.Snapshot()));
            }
            await File.WriteAllTextAsync(result.Path, sb.ToString());
            _logger.Info("logs", "exported logs to " + result.Path);
        }
        catch (Exception ex)
        {
            _logger.Error("logs", "export failed", ex);
        }
    }
}
