using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace YikzClipboard.App;

public static class Program
{
    public const string MutexName = @"Local\YikzClipboard.SingleInstance";
    public const string ActivateEventName = @"Local\YikzClipboard.Activate";

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
            }
            catch (Exception)
            {
            }
            return 0;
        }
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args, activate);
        });
        GC.KeepAlive(mutex);
        return 0;
    }
}
