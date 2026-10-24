using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Winnow.Core;

// ===================================================================== Paths

public static class Paths
{
    /// <summary>The folder Winnow.exe is in. Settings, the undo journal and logs live here.</summary>
    public static string AppDir { get; } = ResolveAppDir();

    static string ResolveAppDir()
    {
        try
        {
            string p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p);
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    public static string SettingsFile => Path.Combine(AppDir, "winnow-settings.json");
    public static string JournalFile => Path.Combine(AppDir, "winnow-journal.json");
    public static string LogFile => Path.Combine(AppDir, "winnow-log.txt");
    public static string CrashLog => Path.Combine(AppDir, "winnow-crash.log");
    public static string SelfTestFile => Path.Combine(AppDir, "winnow-selftest.txt");
    public static string CatalogueOverride => Path.Combine(AppDir, "winnow-catalogue.json");

    public static string Downloads => Native.DownloadsFolder();

    /// <summary>Where user output goes: the chosen folder if it still exists, otherwise Downloads\Winnow.</summary>
    public static string DefaultSaveDir()
    {
        string chosen = Settings.Current.SaveDir;
        if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen)) return chosen;
        string d = Path.Combine(Downloads, "Winnow");
        try { Directory.CreateDirectory(d); } catch { return Downloads; }
        return d;
    }

    public static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Full path to a Windows program in System32 (e.g. "sc.exe"), so an elevated Winnow never
    /// picks up a same-named exe from its own folder (usually Downloads).</summary>
    public static string SystemExe(string exe) => Path.Combine(WindowsDir, "System32", exe);

    /// <summary>Full path to a program in the Windows folder itself (e.g. "explorer.exe").</summary>
    public static string WindowsExe(string exe) => Path.Combine(WindowsDir, exe);

    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
}

// ================================================================== Settings

public sealed class Settings
{
    public static Settings Current { get; private set; } = new();

    [JsonPropertyName("theme")] public string Theme { get; set; } = "phosphor";      // phosphor | dark | light
    [JsonPropertyName("refresh_ms")] public int RefreshMs { get; set; } = 1000;       // 500 | 1000 | 2000
    [JsonPropertyName("save_dir")] public string SaveDir { get; set; } = "";
    [JsonPropertyName("sensors")] public bool SensorsEnabled { get; set; } = true;
    [JsonPropertyName("pawnio_auto_install")] public bool PawnIoAutoInstall { get; set; } = true;
    [JsonPropertyName("recorder_minutes")] public int RecorderMinutes { get; set; } = 60;
    [JsonPropertyName("restore_point")] public bool RestorePointBeforeApply { get; set; } = true;
    [JsonPropertyName("smooth_graphs")] public bool SmoothGraphs { get; set; } = true;
    [JsonPropertyName("glow")] public bool Glow { get; set; } = true;
    [JsonPropertyName("start_view")] public string StartView { get; set; } = "summary";
    [JsonPropertyName("window")] public double[] Window { get; set; } = null;          // left, top, width, height
    [JsonPropertyName("process_tree")] public bool ProcessTree { get; set; } = true;
    [JsonPropertyName("show_self")] public bool ShowSelf { get; set; } = false;        // include Winnow in its own lists
    [JsonPropertyName("extra")] public Dictionary<string, string> Extra { get; set; } = new();

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Opts) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("settings load: " + ex.Message);
            Current = new Settings();
        }
        Current.Extra ??= new();
        if (Current.RefreshMs is not (500 or 1000 or 2000)) Current.RefreshMs = 1000;
        Current.RecorderMinutes = Math.Clamp(Current.RecorderMinutes, 5, 240);
    }

    static readonly object _saveLock = new();

    public static void Save()
    {
        // Write a temp file, then swap it in, so a crash or power cut mid-write cannot leave a truncated settings file.
        try
        {
            lock (_saveLock)
            {
                string tmp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Opts));
                File.Move(tmp, Paths.SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
    }

    public string Get(string key, string fallback = "") => Extra.TryGetValue(key, out var v) ? v : fallback;
    public void Set(string key, string value) { Extra[key] = value; Save(); }
}

// ======================================================================= Log

public static class Log
{
    static readonly object _lock = new();

    public static void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 4_000_000) File.Delete(fi.FullName);
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void Crash(string where, Exception ex)
    {
        try
        {
            File.AppendAllText(Paths.CrashLog,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}

// ================================================================= Elevation

public static class Elevation
{
    public static bool IsAdmin { get; } = CheckAdmin();

    static bool CheckAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Relaunch this exe elevated. Returns true if the elevated copy started (caller should exit).</summary>
    public static bool RelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args.Select(Shell.Quote).Append("--elevated")),
                WorkingDirectory = Paths.AppDir,
            };
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // the user said no at the UAC prompt
        }
        catch (Exception ex)
        {
            Log.Write("elevate: " + ex.Message);
            return false;
        }
    }
}

// ==================================================================== WinVer

public static class WinVer
{
    public static int Build { get; }
    public static int Ubr { get; }
    public static string DisplayVersion { get; } = "";   // e.g. 24H2
    public static string EditionId { get; } = "";        // Core, Professional, Enterprise, Education, ...
    public static string ProductName { get; } = "";

    static WinVer()
    {
        try
        {
            var v = new Native.RTL_OSVERSIONINFOW { dwOSVersionInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.RTL_OSVERSIONINFOW>() };
            if (Native.RtlGetVersion(ref v) == 0) Build = (int)v.dwBuildNumber;
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k != null)
            {
                DisplayVersion = k.GetValue("DisplayVersion") as string ?? "";
                EditionId = k.GetValue("EditionID") as string ?? "";
                ProductName = k.GetValue("ProductName") as string ?? "";
                if (k.GetValue("UBR") is int ubr) Ubr = ubr;
            }
            // The registry still says "Windows 10" on Windows 11.
            if (Build >= 22000 && ProductName.StartsWith("Windows 10")) ProductName = "Windows 11" + ProductName["Windows 10".Length..];
        }
        catch { }
    }

    public static bool IsWindows11 => Build >= 22000;

    /// <summary>Home / Pro / Pro for Workstations / Enterprise / Education ...</summary>
    public static string EditionName => EditionId switch
    {
        "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" or "CoreN" => "Home",
        "Professional" or "ProfessionalN" => "Pro",
        "ProfessionalWorkstation" => "Pro for Workstations",
        "ProfessionalEducation" => "Pro Education",
        "Enterprise" or "EnterpriseN" or "EnterpriseS" or "IoTEnterprise" or "IoTEnterpriseS" => "Enterprise",
        "Education" or "EducationN" => "Education",
        "" => "Unknown",
        _ => EditionId
    };

    /// <summary>Enterprise and Education honour AllowTelemetry=0 ("Security"); Home and Pro stop at Required.</summary>
    public static bool HonoursTelemetryZero => EditionName is "Enterprise" or "Education";

    /// <summary>Group Policy-only settings (e.g. some Start/Spotlight policies) are ignored on Home.</summary>
    public static bool IsHome => EditionName == "Home";

    public static string Summary => $"{ProductName} {DisplayVersion} (build {Build}.{Ubr}) - {EditionName}";
}

// ===================================================================== Shell

public sealed class ShellResult
{
    public int ExitCode = -1;
    public string Output = "";   // stdout + stderr, in order received
    public bool TimedOut;
    public bool Ok => ExitCode == 0 && !TimedOut;
}

public static class Shell
{
    /// <summary>Run a program hidden and capture its output. Never throws; failures come back in the result.</summary>
    public static async Task<ShellResult> RunAsync(string file, string args, int timeoutMs = 120_000,
                                                   Action<string> onLine = null, CancellationToken ct = default)
    {
        var result = new ShellResult();
        var sb = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Paths.AppDir,
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void Line(string l)
            {
                if (l == null) return;
                lock (sb) sb.AppendLine(l);
                try { onLine?.Invoke(l); } catch { }
            }
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived += (_, e) => Line(e.Data);
            p.Start();
            try { p.StandardInput.Close(); } catch { }   // nothing can ever sit waiting for input
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                result.TimedOut = !ct.IsCancellationRequested;
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            if (p.HasExited)
            {
                // Flush async output, but never hang on a pipe a stray grandchild still holds open.
                p.WaitForExit(5000);
                result.ExitCode = p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine(ex.Message);
        }
        lock (sb) result.Output = sb.ToString();
        return result;
    }

    /// <summary>Run a PowerShell script (Windows PowerShell 5.1, always present). Script is passed encoded, so quoting is not a problem.</summary>
    public static Task<ShellResult> PowerShellAsync(string script, int timeoutMs = 120_000, Action<string> onLine = null,
                                                    CancellationToken ct = default)
    {
        string prelude = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;";
        string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude + script));
        string ps = Path.Combine(Paths.WindowsDir, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        return RunAsync(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, timeoutMs, onLine, ct);
    }

    /// <summary>Full path to winget.exe, or null if it is not installed.</summary>
    public static string WingetPath()
    {
        string alias = Path.Combine(Paths.LocalAppData, @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        try
        {
            string wa = Path.Combine(Paths.ProgramFiles, "WindowsApps");
            if (Directory.Exists(wa))
            {
                var hit = Directory.EnumerateDirectories(wa, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                                   .OrderByDescending(d => d).Select(d => Path.Combine(d, "winget.exe"))
                                   .FirstOrDefault(File.Exists);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    public static string Quote(string a)
    {
        if (string.IsNullOrEmpty(a)) return "\"\"";
        if (a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        // CommandLineToArgvW rules: backslashes are literal unless they come before a quote, so a run of
        // them is doubled before an escaped quote and before the closing quote (e.g. "C:\My Dir\\").
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            sb.Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Open a folder or URL with the shell.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open " + target + ": " + ex.Message); }
    }

    /// <summary>Open Explorer with the file selected.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(Paths.WindowsExe("explorer.exe"), "/select,\"" + path + "\"");
            else if (Directory.Exists(path)) Process.Start(Paths.WindowsExe("explorer.exe"), "\"" + path + "\"");
        }
        catch (Exception ex) { Log.Write("reveal: " + ex.Message); }
    }
}

// ====================================================================== Fmt

public static class Fmt
{
    public const string Na = "—";

    public static string Bytes(double b)
    {
        if (double.IsNaN(b)) return Na;
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        double v = Math.Abs(b);
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        string num = i == 0 ? v.ToString("0") : v < 10 ? v.ToString("0.00") : v < 100 ? v.ToString("0.0") : v.ToString("0");
        return (b < 0 ? "-" : "") + num + " " + u[i];
    }
    public static string Bytes(ulong b) => Bytes((double)b);
    public static string Bytes(long b) => Bytes((double)b);
    public static string Bytes(double? b) => b.HasValue ? Bytes(b.Value) : Na;

    public static string Rate(double? bps) => bps.HasValue ? Bytes(bps.Value) + "/s" : Na;

    /// <summary>Bits per second, for network links.</summary>
    public static string Bits(double? bps)
    {
        if (!bps.HasValue) return Na;
        double b = bps.Value * 8;
        if (b >= 1e9) return (b / 1e9).ToString("0.0#") + " Gbps";
        if (b >= 1e6) return (b / 1e6).ToString("0.0#") + " Mbps";
        if (b >= 1e3) return (b / 1e3).ToString("0.0") + " Kbps";
        return b.ToString("0") + " bps";
    }

    public static string Pct(double? p, int decimals = 0) =>
        p.HasValue ? p.Value.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals)) + "%" : Na;

    public static string Temp(double? c) => c.HasValue ? c.Value.ToString("0") + "°C" : Na;
    public static string Watts(double? w) => w.HasValue ? (w.Value < 10 ? w.Value.ToString("0.0") : w.Value.ToString("0")) + " W" : Na;
    public static string Mhz(double? m) => m.HasValue ? (m.Value >= 1000 ? (m.Value / 1000).ToString("0.00") + " GHz" : m.Value.ToString("0") + " MHz") : Na;

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static string Sensor(SensorReading r) => r?.Value == null ? Na : r.Type switch
    {
        "Temperature" => r.Value.Value.ToString("0.0") + " °C",
        "Load" or "Control" or "Level" or "Humidity" => r.Value.Value.ToString("0.0") + " %",
        "Clock" or "Frequency" => r.Value.Value.ToString("0") + " MHz",
        "Power" => r.Value.Value.ToString("0.0") + " W",
        "Fan" => r.Value.Value.ToString("0") + " RPM",
        "Voltage" => r.Value.Value.ToString("0.000") + " V",
        "Current" => r.Value.Value.ToString("0.00") + " A",
        "Data" => r.Value.Value.ToString("0.0") + " GB",
        "SmallData" => r.Value.Value.ToString("0") + " MB",
        "Throughput" => Rate(r.Value.Value),
        _ => r.Value.Value.ToString("0.##") + " " + r.Unit
    };
}

// ================================================================ RingBuffer

/// <summary>Fixed-capacity, oldest-overwritten buffer. Thread-safe for one writer and many readers.</summary>
public sealed class RingBuffer<T>
{
    readonly T[] _items;
    int _start, _count;
    readonly object _lock = new();

    public RingBuffer(int capacity) { _items = new T[Math.Max(1, capacity)]; }

    public int Capacity => _items.Length;
    public int Count { get { lock (_lock) return _count; } }

    public void Push(T item)
    {
        lock (_lock)
        {
            if (_count < _items.Length) { _items[(_start + _count) % _items.Length] = item; _count++; }
            else { _items[_start] = item; _start = (_start + 1) % _items.Length; }
        }
    }

    /// <summary>0 = oldest.</summary>
    public T this[int i]
    {
        get { lock (_lock) return (uint)i < (uint)_count ? _items[(_start + i) % _items.Length] : default; }
    }

    public T Newest { get { lock (_lock) return _count == 0 ? default : _items[(_start + _count - 1) % _items.Length]; } }

    public List<T> ToList()
    {
        lock (_lock)
        {
            var l = new List<T>(_count);
            for (int i = 0; i < _count; i++) l.Add(_items[(_start + i) % _items.Length]);
            return l;
        }
    }

    public void Clear() { lock (_lock) { _start = 0; _count = 0; System.Array.Clear(_items); } }
}
