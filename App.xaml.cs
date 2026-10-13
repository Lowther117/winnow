using System.Windows;
using System.Windows.Threading;
using Winnow.Core;
using Winnow.Theme;
using Winnow.Views;   // Status, StatusKind

namespace Winnow;

public partial class App : Application
{
    public static string[] Args { get; private set; } = System.Array.Empty<string>();

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
            int code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        Settings.Load();

        // "Winnow.exe uitest" - opens every page and screenshots it (used by the GitHub build). Looks, never changes.
        if (e.Args.Any(a => a.Equals("uitest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
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

        // One UAC prompt, then the elevated copy carries on. If the user says no, run limited.
        if (!Elevation.IsAdmin && !e.Args.Contains("--elevated") && !e.Args.Contains("--no-elevate"))
        {
            if (Elevation.RelaunchElevated(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(0);
                return;
            }
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
        try { Settings.Save(); } catch { }
        base.OnExit(e);
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Crash("ui", e.Exception);
        e.Handled = true; // keep running; one broken view should not take the whole app down
        try { Status.Set("Something went wrong: " + e.Exception.Message + " (logged to winnow-crash.log)", StatusKind.Bad); } catch { }
    }
}
