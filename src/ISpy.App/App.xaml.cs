using System.IO;
using System.Windows;
using System.Windows.Threading;
using ISpy.Core;

namespace ISpy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTimeline.Mark("managed entry");
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal(args.ExceptionObject as Exception);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Nothing on the startup path may touch the network or block on I/O: the window is
        // constructed and shown first, and inventory loads only once it is on screen.
        new MainWindow().Show();

        StartupTimeline.Mark("window shown");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        LogFatal(e.Exception);

    protected override void OnExit(ExitEventArgs e)
    {
        StartupTimeline.Flush();
        base.OnExit(e);
    }

    /// <summary>
    /// Leaves a crash trail rather than vanishing. The likeliest cause of an unexpected exception
    /// here is a device speaking an ISAPI dialect we have not seen, and the log is what pins it down.
    /// </summary>
    private static void LogFatal(Exception? exception)
    {
        if (exception is null) return;

        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogDirectory, "crash.log"),
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Already failing; nothing useful left to do.
        }
    }
}
