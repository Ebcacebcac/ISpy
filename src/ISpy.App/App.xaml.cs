using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ISpy.Core;

namespace ISpy.App;

public partial class App : Application
{
    /// <summary>Held for the process lifetime; releasing it is what lets the next launch start.</summary>
    private Mutex? _singleInstance;

    private int _handledDispatcherFailures;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTimeline.Mark("managed entry");

        // Must run before any window exists. On an install, update or uninstall the launcher passes
        // hook arguments and this call services them and exits; on a normal launch it costs
        // microseconds and returns.
        Velopack.VelopackApp.Build().Run();

        if (!ClaimSingleInstance())
        {
            // A second launch should raise the window that is already open rather than opening a
            // second grid and doubling the load on the recorder.
            ActivateRunningInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Every window the app ever opens gets the dark native title bar, without each window
        // having to remember to ask.
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent,
            new RoutedEventHandler((sender, _) => WindowStyling.ApplyDarkChrome((Window)sender)));

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal(args.ExceptionObject as Exception);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // A faulted task nobody awaited must not bring the process down at the next GC.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal(args.Exception);
            args.SetObserved();
        };

        // Nothing on the startup path may touch the network or block on I/O: the window is
        // constructed and shown first, and inventory loads only once it is on screen.
        new MainWindow().Show();

        StartupTimeline.Mark("window shown");
    }

    /// <summary>
    /// A surveillance app must stay up: an unexpected error in one interaction is logged and
    /// reported, and the cameras keep running. The cap exists because an exception thrown by
    /// every dispatcher frame would otherwise become an endless dialog loop - past it, the crash
    /// is allowed through and the log tells the story.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);

        if (_handledDispatcherFailures >= 5) return;
        _handledDispatcherFailures++;

        e.Handled = true;

        MessageBox.Show(
            "Something went wrong, but ISpy is still running." +
            Environment.NewLine + Environment.NewLine + e.Exception.Message +
            Environment.NewLine + Environment.NewLine + "Details were saved to:" +
            Environment.NewLine + Path.Combine(AppPaths.LogDirectory, "crash.log"),
            "ISpy", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupTimeline.Flush();

        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }

    private bool ClaimSingleInstance()
    {
        // Local, not Global: two Windows users on one machine may each run their own copy.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\ISpy.SingleInstance", out var isOwner);

        if (isOwner) return true;

        _singleInstance.Dispose();
        _singleInstance = null;
        return false;
    }

    private static void ActivateRunningInstance()
    {
        var current = System.Diagnostics.Process.GetCurrentProcess();

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero) continue;

            ShowWindow(process.MainWindowHandle, ShowWindowRestore);
            SetForegroundWindow(process.MainWindowHandle);
            return;
        }
    }

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

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
