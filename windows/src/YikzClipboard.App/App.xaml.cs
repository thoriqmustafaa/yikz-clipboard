using Microsoft.UI.Xaml;
using YikzClipboard.Core.Logging;

namespace YikzClipboard.App;

public partial class App : Application
{
    private readonly string[] _args;
    private readonly EventWaitHandle _activate;
    private AppHost? _host;

    public App(string[] args, EventWaitHandle activate)
    {
        _args = args;
        _activate = activate;
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log?.Error("app", "fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log?.Warn("app", "unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    public static ILog? Log { get; internal set; }

    public static AppHost? Host { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = new AppHost(_args, _activate);
        Host = _host;
        _host.Start();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log?.Error("app", "unhandled UI exception: " + e.Message, e.Exception);
        e.Handled = true;
    }
}
