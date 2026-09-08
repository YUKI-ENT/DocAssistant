using System.Configuration;
using System.Data;
using System.Windows;

namespace PDFWriter;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LifecycleLog.Write("Startup");
        Dispatcher.ShutdownStarted += (_, _) => LifecycleLog.Write("Dispatcher.ShutdownStarted");
        Dispatcher.ShutdownFinished += (_, _) => LifecycleLog.Write("Dispatcher.ShutdownFinished");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LifecycleLog.Write("ProcessExit");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LifecycleLog.Write($"Application.Exit code={e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
