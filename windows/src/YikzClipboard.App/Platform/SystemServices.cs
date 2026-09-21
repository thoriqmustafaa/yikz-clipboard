using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using YikzClipboard.App.Interop;
using YikzClipboard.Core.Logging;
using YikzClipboard.Core.Storage;

namespace YikzClipboard.App.Platform;

internal sealed class DpapiSecureStore : ISecureStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("yikz-clipboard/windows/secret-store/v1");
    private readonly string _dir;

    public DpapiSecureStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string name) => Path.Combine(_dir, name + ".bin");

    public byte[]? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Set(string name, byte[] value)
    {
        var protectedBytes = ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);
        var path = PathFor(name);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, path, true);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YikzClipboard";

    public static string Command => "\"" + (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "YikzClipboard.exe")) + "\" --background";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, ILog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            log.Warn("startup", "could not update the Run key", ex);
        }
    }
}

internal readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        uint mods = 0;
        uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win":
                case "windows":
                    mods |= Win32.MOD_WIN;
                    break;
                case "ctrl":
                case "control":
                    mods |= Win32.MOD_CONTROL;
                    break;
                case "alt":
                    mods |= Win32.MOD_ALT;
                    break;
                case "shift":
                    mods |= Win32.MOD_SHIFT;
                    break;
                default:
                    if (vk != 0)
                    {
                        return false;
                    }
                    vk = KeyFromName(raw);
                    if (vk == 0)
                    {
                        return false;
                    }
                    break;
            }
        }
        if (vk == 0 || mods == 0)
        {
            return false;
        }
        hotkey = new Hotkey(mods, vk);
        return true;
    }

    private static uint KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return c;
            }
        }
        var upper = name.ToUpperInvariant();
        if (upper.Length >= 2 && upper[0] == 'F' && int.TryParse(upper[1..], out var f) && f >= 1 && f <= 24)
        {
            return (uint)(0x70 + f - 1);
        }
        return upper switch
        {
            "SPACE" => 0x20,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "`" or "OEM3" => 0xC0,
            "." or "PERIOD" => 0xBE,
            "," or "COMMA" => 0xBC,
            ";" => 0xBA,
            "/" => 0xBF,
            _ => 0,
        };
    }

    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & Win32.MOD_WIN) != 0)
        {
            parts.Add("Win");
        }
        if ((mods & Win32.MOD_CONTROL) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((mods & Win32.MOD_ALT) != 0)
        {
            parts.Add("Alt");
        }
        if ((mods & Win32.MOD_SHIFT) != 0)
        {
            parts.Add("Shift");
        }
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    public static string KeyName(uint vk)
    {
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9'))
        {
            return ((char)vk).ToString();
        }
        if (vk >= 0x70 && vk <= 0x87)
        {
            return "F" + (vk - 0x70 + 1);
        }
        return vk switch
        {
            0x20 => "Space",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0xC0 => "`",
            0xBE => ".",
            0xBC => ",",
            0xBA => ";",
            0xBF => "/",
            _ => "0x" + vk.ToString("X2"),
        };
    }

    public override string ToString() => Format(Modifiers, VirtualKey);
}

internal sealed class HotkeyRegistration : IDisposable
{
    public const int HistoryHotkeyId = 0x5943;
    public const string Fallback = "Ctrl+Alt+V";
    private readonly IntPtr _hwnd;
    private readonly ILog _log;
    private bool _registered;

    public HotkeyRegistration(IntPtr hwnd, ILog log)
    {
        _hwnd = hwnd;
        _log = log;
    }

    public string? Active { get; private set; }

    public bool TryRegister(string text, out string? error)
    {
        error = null;
        if (!Hotkey.TryParse(text, out var hk))
        {
            error = "Use modifiers plus a key, for example Win+Alt+V.";
            return false;
        }
        Unregister();
        if (Win32.RegisterHotKey(_hwnd, HistoryHotkeyId, hk.Modifiers | Win32.MOD_NOREPEAT, hk.VirtualKey))
        {
            _registered = true;
            Active = hk.ToString();
            _log.Info("hotkey", "registered " + Active);
            return true;
        }
        error = hk + " is already used by Windows or another app.";
        _log.Warn("hotkey", "RegisterHotKey failed for " + hk + ", error " + Marshal.GetLastWin32Error());
        return false;
    }

    public string RegisterWithFallback(string preferred)
    {
        if (TryRegister(preferred, out _))
        {
            return Active!;
        }
        if (!string.Equals(preferred, Fallback, StringComparison.OrdinalIgnoreCase) && TryRegister(Fallback, out _))
        {
            return Active!;
        }
        return "";
    }

    public void Unregister()
    {
        if (_registered)
        {
            Win32.UnregisterHotKey(_hwnd, HistoryHotkeyId);
            _registered = false;
            Active = null;
        }
    }

    public void Dispose() => Unregister();
}

internal static class ForegroundTracker
{
    public static IntPtr Capture(IntPtr exclude)
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == exclude)
        {
            return IntPtr.Zero;
        }
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Win32.GetCurrentProcessId())
        {
            return IntPtr.Zero;
        }
        var buffer = new char[256];
        var length = Win32.GetClassName(hwnd, buffer, buffer.Length);
        var className = length > 0 ? new string(buffer, 0, length) : "";
        if (className.StartsWith("Shell_", StringComparison.Ordinal) ||
            className is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Progman" or "WorkerW")
        {
            return IntPtr.Zero;
        }
        return hwnd;
    }

    public static async Task<bool> PasteIntoAsync(IntPtr target)
    {
        if (target == IntPtr.Zero || !Win32.IsWindow(target))
        {
            return false;
        }
        if (Win32.IsIconic(target))
        {
            Win32.ShowWindow(target, Win32.SW_RESTORE);
        }
        Win32.SetForegroundWindow(target);
        await Task.Delay(120);
        for (var i = 0; i < 20 && IsModifierDown(); i++)
        {
            await Task.Delay(25);
        }
        var inputs = new[]
        {
            Key(Win32.VK_CONTROL, false),
            Key(Win32.VK_V, false),
            Key(Win32.VK_V, true),
            Key(Win32.VK_CONTROL, true),
        };
        var sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
        return sent == inputs.Length;
    }

    private static bool IsModifierDown()
    {
        foreach (var vk in new[] { Win32.VK_LWIN, Win32.VK_RWIN, Win32.VK_MENU, Win32.VK_SHIFT })
        {
            if ((Win32.GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static Win32.INPUT Key(ushort vk, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0 },
        },
    };
}

internal static class SourceAppResolver
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(IntPtr ownerWindow)
    {
        var hwnd = ownerWindow != IntPtr.Zero ? ownerWindow : Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Win32.GetCurrentProcessId())
        {
            return null;
        }
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (!Win32.QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return null;
            }
            var path = new string(buffer, 0, (int)size);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var cached))
                {
                    return cached;
                }
            }
            string name;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription!.Trim() : Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                name = Path.GetFileNameWithoutExtension(path);
            }
            if (name.Length > 80)
            {
                name = name[..80];
            }
            lock (Cache)
            {
                Cache[path] = name;
            }
            return name;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }
}

internal sealed class NetworkWatcher : IDisposable
{
    private readonly Action<string> _onChange;
    private readonly ILog _log;
    private readonly Timer _debounce;
    private string _pendingReason = "";
    private bool _lastAvailable;
    private string _lastSignature;

    public NetworkWatcher(Action<string> onChange, ILog log)
    {
        _onChange = onChange;
        _log = log;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _lastAvailable = NetworkInterface.GetIsNetworkAvailable();
        _lastSignature = Signature();
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        try
        {
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnStatusChanged;
        }
        catch (Exception ex)
        {
            _log.Warn("network", "NetworkStatusChanged unavailable", ex);
        }
    }

    private static string Signature()
    {
        try
        {
            var parts = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => n.Id + "/" + a.Address))
                .OrderBy(s => s, StringComparer.Ordinal);
            return string.Join(";", parts);
        }
        catch
        {
            return "";
        }
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Schedule(e.IsAvailable ? "network available" : "network lost");

    private void OnAddressChanged(object? sender, EventArgs e) => Schedule("network address changed");

    private void OnStatusChanged(object? sender) => Schedule("network status changed");

    private void Schedule(string reason)
    {
        _pendingReason = reason;
        _debounce.Change(1500, Timeout.Infinite);
    }

    private void Fire()
    {
        var available = NetworkInterface.GetIsNetworkAvailable();
        var signature = Signature();
        var changed = available != _lastAvailable || !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
        _lastAvailable = available;
        _lastSignature = signature;
        if (!changed)
        {
            return;
        }
        _log.Info("network", _pendingReason + (available ? "" : " (offline)"));
        if (available)
        {
            _onChange(_pendingReason);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        try
        {
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
        }
        catch
        {
        }
        _debounce.Dispose();
    }
}

internal static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    public static bool IsTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
