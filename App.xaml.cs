using System.Windows;
using System.Windows.Threading;
using Winnow.Core;
using Winnow.Theme;
using Winnow.Views;   // Status, StatusKind

namespace Winnow;

public partial class App : Application
{
    public static string[] Args { get; private set; } = System.Array.Empty<string>();

    // Held for the life of the window so a second launch brings this one forward instead of opening another.
    const string SingleInstanceName = @"Local\Winnow.SingleInstance";
    static Mutex _singleInstance;
    static bool _noSaveOnExit;   // selftest / uitest / second copy: never overwrite the user's settings file

    protected override void OnStartup(StartupEventArgs e)
    {
        Args = e.Args;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Crash("unhandled", ev.ExceptionObject as Exception ?? new Exception("unknown"));
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Crash("task", ev.Exception); ev.SetObserved(); };
        DispatcherUnhandledException += OnDispatcherException;

        base.OnStartup(e);

        // "Winnow.exe selftest" - run by build-exe.bat to prove the exe works. No window, no UAC.
        if (e.Args.Any(a => a.Equals("selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _noSaveOnExit = true;
            int code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        Settings.Load();

        // "Winnow.exe uitest" - opens every page and screenshots it (used by the GitHub build). Looks, never changes.
        if (e.Args.Any(a => a.Equals("uitest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _noSaveOnExit = true;
            Settings.Current.PawnIoAutoInstall = false;
            Settings.Current.Theme = "phosphor";
            Palette.Apply("phosphor");
            Journal.Load();
            Sampler.Start();
            Sensors.Start();
            NetEtw.Start();
            ServiceMap.Refresh(force: true);
            Views.UiTest.Run(this);
            return;
        }

        // One Winnow at a time: a second launch brings the open window forward and exits.
        if (!TakeSingleInstance())
        {
            _noSaveOnExit = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ShowExistingInstance();
            Shutdown(0);
            return;
        }

        // One UAC prompt, then the elevated copy carries on. If the user says no, run limited.
        if (!Elevation.IsAdmin && !e.Args.Contains("--elevated") && !e.Args.Contains("--no-elevate"))
        {
            // Let go of the single-instance lock first so the elevated copy can take it.
            DropSingleInstance();
            if (Elevation.RelaunchElevated(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(0);
                return;
            }
            TakeSingleInstance();   // UAC refused: carry on limited, still as the only copy
        }

        try
        {
            Palette.Apply(Settings.Current.Theme);
            Journal.Load();

            if (!WinVer.IsWindows11)
                MessageBox.Show($"Winnow is built for Windows 11 and later. This PC reports build {WinVer.Build}.\n\n" +
                                "It will open, but some privacy settings may not exist here and some views may be incomplete.",
                                "Winnow", MessageBoxButton.OK, MessageBoxImage.Warning);

            Sampler.Start();
            if (Settings.Current.SensorsEnabled) Sensors.Start();
            NetEtw.Start();
            ServiceMap.Refresh(force: true);

            var w = new MainWindow();
            MainWindow = w;
            w.Show();
        }
        catch (Exception ex)
        {
            Log.Crash("startup", ex);
            MessageBox.Show("Winnow could not start:\n\n" + ex.Message + "\n\nDetails are in winnow-crash.log beside Winnow.exe.",
                            "Winnow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Sampler.Stop(); } catch { }
        try { NetEtw.Stop(); } catch { }
        try { Sensors.Stop(); } catch { }
        if (!_noSaveOnExit) { try { Settings.Save(); } catch { } }
        DropSingleInstance();
        base.OnExit(e);
    }

    /// <summary>True when this process now holds the single-instance lock (or the check itself failed).</summary>
    static bool TakeSingleInstance()
    {
        try
        {
            var m = new Mutex(true, SingleInstanceName, out bool createdNew);
            if (createdNew) { _singleInstance = m; return true; }
            m.Dispose();
            return false;
        }
        catch (UnauthorizedAccessException) { return false; }   // held by an elevated Winnow this copy cannot open
        catch (Exception ex) { Log.Write("single instance: " + ex.Message); return true; }   // never block startup on this
    }

    static void DropSingleInstance()
    {
        var m = _singleInstance;
        _singleInstance = null;
        if (m == null) return;
        try { m.ReleaseMutex(); } catch { }
        try { m.Dispose(); } catch { }
    }

    /// <summary>Restore and focus the other Winnow's window; say so if it cannot be found.</summary>
    static void ShowExistingInstance()
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                using (p)
                {
                    if (p.Id == me.Id) continue;
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                    if (Native.SetForegroundWindow(h)) return;
                }
            }
        }
        catch { }
        MessageBox.Show("Winnow is already running.", "Winnow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Crash("ui", e.Exception);
        e.Handled = true; // keep running; one broken view should not take the whole app down
        try { Status.Set("Something went wrong: " + e.Exception.Message + " (logged to winnow-crash.log)", StatusKind.Bad); } catch { }
    }
}
