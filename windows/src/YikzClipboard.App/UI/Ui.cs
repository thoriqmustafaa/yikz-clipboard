using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using YikzClipboard.App.Interop;
using YikzClipboard.Core.Storage;

namespace YikzClipboard.App.UI;

internal static class Glyphs
{
    public const string Text = "\ue8a5";
    public const string Link = "\ue71b";
    public const string Image = "\ueb9f";
    public const string File = "\ue7c3";
    public const string Files = "\ue8b7";
    public const string Unknown = "\ue9ce";
    public const string Search = "\ue721";
    public const string Pin = "\ue718";
    public const string Unpin = "\ue77a";
    public const string Delete = "\ue74d";
    public const string Save = "\ue74e";
    public const string Copy = "\ue8c8";
    public const string Paste = "\ue77f";
    public const string Settings = "\ue713";
    public const string Account = "\ue77b";
    public const string Sync = "\ue895";
    public const string Devices = "\ue772";
    public const string Laptop = "\ue7f8";
    public const string Phone = "\ue8ea";
    public const string Desktop = "\ue7f4";
    public const string Globe = "\ue774";
    public const string Storage = "\ueda2";
    public const string Info = "\ue946";
    public const string Keyboard = "\ue765";
    public const string Refresh = "\ue72c";
    public const string Folder = "\ue8b7";
    public const string OpenFolder = "\ue838";
    public const string Download = "\ue896";
    public const string Warning = "\ue7ba";
    public const string Lock = "\ue72e";
    public const string Pause = "\ue769";
    public const string Play = "\ue768";
    public const string Clear = "\ue894";
    public const string Export = "\uede1";
    public const string OpenLink = "\ue8a7";
    public const string Filter = "\ue71c";
    public const string History = "\ue81c";
    public const string Logs = "\ue9f9";
    public const string Rename = "\ue8ac";
    public const string Checkmark = "\ue73e";
    public const string Clipboard = "\uf0e3";
    public const string Music = "\ue8d6";
    public const string Video = "\ue714";
    public const string Archive = "\uf012";
    public const string Code = "\ue943";
    public const string Pdf = "\uea90";

    public static string ForKind(EntryKind kind) => kind switch
    {
        EntryKind.Text => Text,
        EntryKind.Link => Link,
        EntryKind.Image => Image,
        EntryKind.Files => File,
        _ => Unknown,
    };

    public static string ForPlatform(string? platform) => platform switch
    {
        "macos" => Laptop,
        "android" => Phone,
        "windows" => Desktop,
        "web" => Globe,
        _ => Devices,
    };

    public static string ForFileName(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic" or ".svg" or ".tif" or ".tiff" => Image,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac" => Music,
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" => Video,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".xz" => Archive,
            ".cs" or ".js" or ".ts" or ".go" or ".py" or ".kt" or ".swift" or ".json" or ".xml" or ".html" or ".css" or ".sh" or ".yml" or ".yaml" => Code,
            ".pdf" => Pdf,
            ".txt" or ".md" or ".log" or ".csv" or ".rtf" or ".doc" or ".docx" => Text,
            _ => File,
        };
    }
}

internal static class Ui
{
    public static Style? Style(string key)
    {
        if (Application.Current?.Resources != null && Application.Current.Resources.TryGetValue(key, out var value))
        {
            return value as Style;
        }
        return null;
    }

    public static T Styled<T>(T element, string key) where T : FrameworkElement
    {
        var style = Style(key);
        if (style != null)
        {
            element.Style = style;
        }
        return element;
    }

    public static Brush? Brush(string key)
    {
        if (Application.Current?.Resources != null && Application.Current.Resources.TryGetValue(key, out var value))
        {
            return value as Brush;
        }
        return null;
    }

    public static TextBlock Text(string text, double size = 14, bool semibold = false, string? style = null, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (semibold)
        {
            tb.FontWeight = FontWeights.SemiBold;
        }
        if (style != null)
        {
            Styled(tb, style);
        }
        return tb;
    }

    public static TextBlock Secondary(string text, double size = 12, bool wrap = false) => Text(text, size, false, "YkSecondaryText", wrap);

    public static TextBlock Tertiary(string text, double size = 12) => Text(text, size, false, "YkTertiaryText");

    public static TextBlock Title(string text, double size = 20) => Text(text, size, true);

    public static FontIcon Icon(string glyph, double size = 16, string? style = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size };
        if (style != null)
        {
            Styled(icon, style);
        }
        return icon;
    }

    public static Border Divider(bool vertical = false)
    {
        var border = Styled(new Border(), "YkDivider");
        if (vertical)
        {
            border.Width = 1;
        }
        else
        {
            border.Height = 1;
        }
        return border;
    }

    public static Border Keycap(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Styled(tb, "YkSecondaryText");
        var border = Styled(new Border { Child = tb, VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 }, "YkKeycap");
        tb.HorizontalAlignment = HorizontalAlignment.Center;
        return border;
    }

    public static StackPanel Keys(params string[] keys)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var k in keys)
        {
            panel.Children.Add(Keycap(k));
        }
        return panel;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var card = Styled(new Border { Child = child }, "YkCard");
        if (padding.HasValue)
        {
            card.Padding = padding.Value;
        }
        return card;
    }

    public static Ellipse Dot(string style, double size = 8)
    {
        return Styled(new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }, style);
    }

    public static Button SubtleButton(UIElement content, string? tooltip = null)
    {
        var button = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tooltip != null)
        {
            ToolTipService.SetToolTip(button, tooltip);
        }
        return button;
    }

    public static StackPanel IconLabel(string glyph, string label, double iconSize = 14)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Icon(glyph, iconSize));
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    public static Grid SettingRow(string glyph, string title, string? description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Icon(glyph, 18);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(Text(title, 14));
        if (!string.IsNullOrEmpty(description))
        {
            texts.Children.Add(Secondary(description, 12, true));
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);
        return grid;
    }

    public static Border SettingCard(string glyph, string title, string? description, FrameworkElement control)
    {
        return Card(SettingRow(glyph, title, description, control), new Thickness(16, 10, 16, 10));
    }

    public static TextBlock SectionHeader(string text)
    {
        var tb = Text(text, 14, true);
        tb.Margin = new Thickness(2, 18, 0, 6);
        return tb;
    }

    public static string RelativeDay(DateTimeOffset time)
    {
        var local = time.ToLocalTime().Date;
        var today = DateTime.Now.Date;
        if (local == today)
        {
            return "Today";
        }
        if (local == today.AddDays(-1))
        {
            return "Yesterday";
        }
        if (local > today.AddDays(-7))
        {
            return local.ToString("dddd", System.Globalization.CultureInfo.CurrentCulture);
        }
        return local.Year == today.Year
            ? local.ToString("MMMM d", System.Globalization.CultureInfo.CurrentCulture)
            : local.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string ShortTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Now.Date || local > DateTimeOffset.Now.AddDays(-7)
            ? local.ToString("t", System.Globalization.CultureInfo.CurrentCulture)
            : local.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string LongTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return RelativeDay(time) + " at " + local.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
    }
}

internal static class WindowHelpers
{
    public static IntPtr Hwnd(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    public static string IconPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    public static void ApplyBackdrop(Window window, Panel root)
    {
        try
        {
            if (MicaController.IsSupported())
            {
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                return;
            }
            if (DesktopAcrylicController.IsSupported())
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                return;
            }
        }
        catch (Exception)
        {
        }
        Ui.Styled(root, "YkSolidBackground");
    }

    public static void SetIcon(Window window)
    {
        try
        {
            if (File.Exists(IconPath))
            {
                window.AppWindow.SetIcon(IconPath);
            }
        }
        catch (Exception)
        {
        }
    }

    public static double ScaleAtCursor()
    {
        Win32.GetCursorPos(out var p);
        return ScaleAt(p);
    }

    public static double ScaleAt(Win32.POINT p)
    {
        try
        {
            var monitor = Win32.MonitorFromPoint(p, 2);
            if (monitor != IntPtr.Zero && Win32.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (Exception)
        {
        }
        return Win32.GetDpiForSystem() / 96.0;
    }

    public static void CenterOnCursorMonitor(Window window, double widthDip, double heightDip, double verticalBias = 0.42)
    {
        Win32.GetCursorPos(out var p);
        var scale = ScaleAt(p);
        var area = DisplayArea.GetFromPoint(new PointInt32(p.X, p.Y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var w = (int)Math.Min(work.Width - 24, widthDip * scale);
        var h = (int)Math.Min(work.Height - 24, heightDip * scale);
        var x = work.X + (work.Width - w) / 2;
        var y = work.Y + (int)((work.Height - h) * verticalBias);
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    public static void ConfigureTitleBar(Window window, UIElement dragRegion)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(dragRegion);
        try
        {
            var bar = window.AppWindow.TitleBar;
            bar.PreferredHeightOption = TitleBarHeightOption.Tall;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            UpdateCaptionColors(window);
            if (window.Content is FrameworkElement fe)
            {
                fe.ActualThemeChanged += (_, _) => UpdateCaptionColors(window);
            }
        }
        catch (Exception)
        {
        }
    }

    public static void UpdateCaptionColors(Window window)
    {
        try
        {
            var dark = window.Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;
            var bar = window.AppWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x0A, 0, 0, 0);
            bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
        }
        catch (Exception)
        {
        }
    }

    public static Grid TitleBar(string title, out Grid dragRegion)
    {
        var bar = new Grid { Height = 48, Padding = new Thickness(16, 0, 140, 0) };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        try
        {
            if (File.Exists(IconPath))
            {
                panel.Children.Add(new Image
                {
                    Width = 16,
                    Height = 16,
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(IconPath)),
                });
            }
        }
        catch (Exception)
        {
        }
        panel.Children.Add(Ui.Text(title, 12));
        bar.Children.Add(panel);
        dragRegion = bar;
        return bar;
    }

    public static void SizeWindow(Window window, double widthDip, double heightDip)
    {
        var scale = ScaleAtCursor();
        window.AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }

    public static void BringToFront(Window window)
    {
        window.AppWindow.Show();
        window.Activate();
        var hwnd = Hwnd(window);
        if (Win32.IsIconic(hwnd))
        {
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        }
        Win32.SetForegroundWindow(hwnd);
    }
}
