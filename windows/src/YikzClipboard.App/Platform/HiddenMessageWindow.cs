using System.Runtime.InteropServices;
using YikzClipboard.App.Interop;
using YikzClipboard.Core.Logging;

namespace YikzClipboard.App.Platform;

internal sealed class HiddenMessageWindow : IDisposable
{
    private const string ClassName = "YikzClipboard.MessageWindow";
    private readonly Win32.WndProc _proc;
    private readonly ILog _log;
    private IntPtr _suspendResumeHandle;
    private IntPtr _displayHandle;
    private bool _clipboardListening;
    private bool _sessionRegistered;
    private bool _disposed;

    public HiddenMessageWindow(ILog log)
    {
        _log = log;
        _proc = WindowProc;
        var hInstance = Win32.GetModuleHandle(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);
        Handle = Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE, ClassName, "Yikz Clipboard", Win32.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("could not create message window, error " + Marshal.GetLastWin32Error());
        }
    }

    public IntPtr Handle { get; }

    public event Action? ClipboardUpdated;
    public event Action<int>? HotkeyPressed;
    public event Action<string>? Resumed;
    public event Action? Suspending;
    public event Action? SessionUnlocked;
    public event Action? SettingChanged;

    public void StartListening()
    {
        _clipboardListening = Win32.AddClipboardFormatListener(Handle);
        if (!_clipboardListening)
        {
            _log.Error("clipboard", "AddClipboardFormatListener failed, error " + Marshal.GetLastWin32Error());
        }
        try
        {
            _suspendResumeHandle = Win32.RegisterSuspendResumeNotification(Handle, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        catch (Exception ex)
        {
            _log.Warn("power", "suspend/resume notification unavailable", ex);
        }
        try
        {
            var guid = Win32.GUID_CONSOLE_DISPLAY_STATE;
            _displayHandle = Win32.RegisterPowerSettingNotification(Handle, ref guid, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        catch (Exception ex)
        {
            _log.Warn("power", "display state notification unavailable", ex);
        }
        try
        {
            _sessionRegistered = Win32.WTSRegisterSessionNotification(Handle, Win32.NOTIFY_FOR_THIS_SESSION);
        }
        catch (Exception ex)
        {
            _log.Warn("power", "session notification unavailable", ex);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case Win32.WM_CLIPBOARDUPDATE:
                    ClipboardUpdated?.Invoke();
                    return IntPtr.Zero;
                case Win32.WM_HOTKEY:
                    HotkeyPressed?.Invoke(wParam.ToInt32());
                    return IntPtr.Zero;
                case Win32.WM_POWERBROADCAST:
                    HandlePower(wParam.ToInt32(), lParam);
                    return new IntPtr(1);
                case Win32.WM_WTSSESSION_CHANGE:
                    if (wParam.ToInt32() == Win32.WTS_SESSION_UNLOCK)
                    {
                        SessionUnlocked?.Invoke();
                    }
                    break;
                case Win32.WM_SETTINGCHANGE:
                    SettingChanged?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("win32", "message handler failed for 0x" + msg.ToString("x"), ex);
        }
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandlePower(int evt, IntPtr lParam)
    {
        switch (evt)
        {
            case Win32.PBT_APMSUSPEND:
                Suspending?.Invoke();
                break;
            case Win32.PBT_APMRESUMEAUTOMATIC:
                Resumed?.Invoke("resume from sleep");
                break;
            case Win32.PBT_APMRESUMESUSPEND:
                Resumed?.Invoke("resume by user");
                break;
            case Win32.PBT_POWERSETTINGCHANGE:
                if (lParam != IntPtr.Zero)
                {
                    var setting = Marshal.PtrToStructure<Win32.POWERBROADCAST_SETTING>(lParam);
                    if (setting.PowerSetting == Win32.GUID_CONSOLE_DISPLAY_STATE && setting.Data == 1)
                    {
                        Resumed?.Invoke("display turned on");
                    }
                }
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_clipboardListening)
        {
            Win32.RemoveClipboardFormatListener(Handle);
        }
        if (_suspendResumeHandle != IntPtr.Zero)
        {
            Win32.UnregisterSuspendResumeNotification(_suspendResumeHandle);
        }
        if (_displayHandle != IntPtr.Zero)
        {
            Win32.UnregisterPowerSettingNotification(_displayHandle);
        }
        if (_sessionRegistered)
        {
            Win32.WTSUnRegisterSessionNotification(Handle);
        }
        Win32.DestroyWindow(Handle);
    }
}
