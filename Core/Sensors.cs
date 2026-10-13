using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Winnow.Core;

/// <summary>
/// Hardware sensors through LibreHardwareMonitorLib: every temperature, clock,
/// fan, voltage and power reading the board exposes.
///
/// CPU temperatures and power need ring-0 access. LibreHardwareMonitorLib 0.9.6
/// gets that through PawnIO - a Microsoft-signed, HVCI-compatible driver - not
/// the old WinRing0 driver that Microsoft Defender quarantines. PawnIO is not
/// shipped inside the library, so Winnow installs it once (winget, falling back
/// to the official installer) when sensors are on and it is missing.
///
/// Runs on its own thread: a full hardware update can take 50-300 ms and must
/// never hold up the sampler or the UI.
/// </summary>
public static class Sensors
{
    static Computer _computer;
    static Thread _thread;
    static volatile List<SensorReading> _latest = new();
    static volatile bool _stop;
    static readonly object _gate = new();

    /// <summary>Human-readable state for the status bar and System Info.</summary>
    public static string Status { get; private set; } = "Not started";
    public static bool Running => _thread != null && _thread.IsAlive;
    public static bool PawnIoInstalled => IsPawnIoInstalled();

    /// <summary>The latest readings (a fresh list each update - safe to enumerate from any thread).</summary>
    public static List<SensorReading> Latest => _latest;

    public static event Action StatusChanged;

    public static void Start()
    {
        // A previous loop told to stop may still be finishing its last update (Restart, or the
        // Settings checkbox toggled off and on). Wait briefly for it, or Start would see it as
        // "running", return, and leave sensors off.
        var old = _thread;
        if (old != null && old.IsAlive && _stop && old != Thread.CurrentThread) old.Join(2000);
        lock (_gate)
        {
            if (Running) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Winnow sensors", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    public static void Stop()
    {
        _stop = true;
        lock (_gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
        _latest = new List<SensorReading>();
        SetStatus("Off");
    }

    /// <summary>Close and reopen the hardware tree, e.g. after PawnIO was installed.</summary>
    public static void Restart()
    {
        Stop();
        Thread.Sleep(200);
        Start();
    }

    static void SetStatus(string s)
    {
        Status = s;
        try { StatusChanged?.Invoke(); } catch { }
    }

    static void Loop()
    {
        try
        {
            if (Settings.Current.PawnIoAutoInstall && Elevation.IsAdmin && !IsPawnIoInstalled())
            {
                SetStatus("Installing the PawnIO sensor driver…");
                var (ok, msg) = InstallPawnIoAsync().GetAwaiter().GetResult();
                SetStatus(ok ? "PawnIO installed" : "PawnIO not installed: " + msg);
            }

            lock (_gate)
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,
                    IsStorageEnabled = true,
                };
                _computer.Open();
            }
            UpdateStatusAfterOpen();
        }
        catch (Exception ex)
        {
            SetStatus("Sensors failed to start: " + ex.Message);
            Log.Write("sensors open: " + ex);
            return;
        }

        var visitor = new UpdateVisitor();
        while (!_stop)
        {
            var started = Environment.TickCount64;
            try
            {
                Computer c;
                lock (_gate) c = _computer;
                if (c == null) break;
                c.Accept(visitor);
                _latest = Read(c);
            }
            catch (Exception ex)
            {
                Log.Write("sensors update: " + ex.Message);
            }
            int wait = Math.Max(250, Settings.Current.RefreshMs - (int)(Environment.TickCount64 - started));
            for (int waited = 0; waited < wait && !_stop; waited += 50) Thread.Sleep(50);
        }
    }

    static void UpdateStatusAfterOpen()
    {
        if (!Elevation.IsAdmin) SetStatus("Limited - Winnow is not running as administrator");
        else if (!IsPawnIoInstalled()) SetStatus("Partial - PawnIO driver missing, so CPU temperatures and power are unavailable");
        else SetStatus("OK");
    }

    static List<SensorReading> Read(Computer c)
    {
        var list = new List<SensorReading>(256);
        foreach (IHardware hw in c.Hardware) AddHardware(list, hw);
        return list;
    }

    static void AddHardware(List<SensorReading> list, IHardware hw)
    {
        foreach (ISensor s in hw.Sensors)
        {
            list.Add(new SensorReading
            {
                Hardware = hw.Name,
                HardwareType = hw.HardwareType.ToString(),
                Type = s.SensorType.ToString(),
                Name = s.Name,
                Id = s.Identifier.ToString(),
                Value = Clean(s.Value),
                Min = Clean(s.Min),
                Max = Clean(s.Max),
            });
        }
        foreach (IHardware sub in hw.SubHardware) AddHardware(list, sub);
    }

    // NaN and infinities are "no reading", not a value.
    static float? Clean(float? v) => v.HasValue && float.IsFinite(v.Value) ? v : null;

    sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    // ------------------------------------------------------------- picking

    /// <summary>Fill the headline sensor fields on a snapshot from a list of readings.</summary>
    public static void Pick(Snapshot s, List<SensorReading> r)
    {
        s.Sensors = r;
        if (r.Count == 0) return;
        s.CpuTempC = First(r, "Cpu", "Temperature", "CPU Package", "Core (Tctl/Tdie)", "Tctl/Tdie", "Tctl", "Tdie", "Core Max", "Core Average", "CPU Core");
        s.GpuTempC = First(r, "Gpu", "Temperature", "GPU Core", "GPU Temperature", "GPU");
        s.GpuHotspotC = First(r, "Gpu", "Temperature", "GPU Hot Spot", "GPU Hotspot", "Hot Spot", "Junction");
        s.CpuWatts = First(r, "Cpu", "Power", "CPU Package", "Package");
        s.GpuWatts = First(r, "Gpu", "Power", "GPU Package", "GPU Power", "GPU Board Power", "GPU Core");
    }

    static double? First(List<SensorReading> r, string hwPrefix, string type, params string[] names)
    {
        var pool = r.Where(x => x.Type == type && x.Value.HasValue &&
                                x.HardwareType.StartsWith(hwPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0) return null;
        foreach (var n in names)
        {
            var hit = pool.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault(x => x.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Value;
        }
        // For temperatures fall back to the hottest; for power, nothing - a wrong rail is worse than none.
        return type == "Temperature" ? pool.Max(x => x.Value) : null;
    }

    // -------------------------------------------------------------- PawnIO

    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return k != null;
        }
        catch { return false; }
    }

    const string PawnIoUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    /// <summary>Install PawnIO: winget first, then the official installer. Needs admin.</summary>
    public static async Task<(bool ok, string message)> InstallPawnIoAsync()
    {
        if (IsPawnIoInstalled()) return (true, "already installed");
        if (!Elevation.IsAdmin) return (false, "needs administrator rights");

        string winget = Shell.WingetPath();
        if (winget != null)
        {
            var r = await Shell.RunAsync(winget,
                "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                timeoutMs: 180_000);
            Log.Write($"PawnIO via winget: exit {r.ExitCode}\n{r.Output}");
            if (IsPawnIoInstalled()) return (true, "installed with winget");
        }

        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "Winnow-PawnIO_setup.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Winnow/1.0");
                var bytes = await http.GetByteArrayAsync(PawnIoUrl);
                await File.WriteAllBytesAsync(tmp, bytes);
            }
            var r = await Shell.RunAsync(tmp, "-install -silent", timeoutMs: 180_000);
            Log.Write($"PawnIO via installer: exit {r.ExitCode}\n{r.Output}");
            try { File.Delete(tmp); } catch { }
            if (IsPawnIoInstalled()) return (true, "installed from pawnio.eu's official release");
            return (false, $"installer exited {r.ExitCode} and the driver is still missing");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
