using System.Diagnostics;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace Winnow.Core;

/// <summary>
/// "Winnow.exe selftest": exercises every measuring source without opening a
/// window or asking for elevation, writes winnow-selftest.txt beside the exe,
/// and exits 0 if nothing essential failed. build-exe.bat runs this before it
/// claims the build worked.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        int problems = 0;
        void Ok(string what, string detail) => sb.AppendLine($"  ok    {what}: {detail}");
        void Note(string what, string detail) => sb.AppendLine($"  note  {what}: {detail}");
        void Bad(string what, string detail) { problems++; sb.AppendLine($"  FAIL  {what}: {detail}"); }

        sb.AppendLine("Winnow self-test");
        sb.AppendLine($"  version {typeof(SelfTest).Assembly.GetName().Version}  ·  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  {WinVer.Summary}");
        sb.AppendLine($"  exe folder {Paths.AppDir}");
        sb.AppendLine($"  administrator: {(Elevation.IsAdmin ? "yes" : "no (expected for the self-test)")}");
        sb.AppendLine();

        var sw = Stopwatch.StartNew();
        try
        {
            Settings.Load();
            if (WinVer.IsWindows11) Ok("Windows version", WinVer.Summary);
            else Note("Windows version", $"build {WinVer.Build} is older than Windows 11 - Winnow targets 11");

            var s = Sampler.SampleOnce();
            if (s == null) Bad("sampler", "no snapshot");
            else
            {
                Ok("CPU", $"{s.CpuTotal:0}% over {s.CpuPerCore.Length} logical processors" + (s.CpuMhz.HasValue ? $", {s.CpuMhz:0} MHz" : ", clock unavailable"));
                if (s.MemTotal > 0) Ok("memory", $"{Fmt.Bytes(s.MemUsed)} of {Fmt.Bytes(s.MemTotal)} in use");
                else Bad("memory", "GlobalMemoryStatusEx returned nothing");
                if (s.Processes.Count > 10) Ok("processes", $"{s.Processes.Count} via NtQuerySystemInformation");
                else Bad("processes", $"only {s.Processes.Count} found");
                if (s.Disks.Count > 0) Ok("disks", string.Join(", ", s.Disks.Select(d => $"{d.Name} {Fmt.Pct(d.ActivePct)}")));
                else Note("disks", "no PhysicalDisk counters (counters may be disabled - lodctr /r rebuilds them)");
                Ok("network", s.Nets.Count + " active adapter(s)");
                if (s.PerProcessGpuAvailable) Ok("GPU", $"{Fmt.Pct(s.GpuPct)} busiest engine");
                else Note("GPU", "GPU Engine counters not available on this PC");
            }
        }
        catch (Exception ex) { Bad("sampler", ex.ToString()); }

        try
        {
            var computer = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true, IsMotherboardEnabled = true };
            computer.Open();
            int sensors = 0, temps = 0, devices = computer.Hardware.Count;
            foreach (var hw in computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                foreach (var sn in hw.Sensors.Concat(hw.SubHardware.SelectMany(x => x.Sensors)))
                {
                    sensors++;
                    if (sn.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && sn.Value.HasValue) temps++;
                }
            }
            computer.Close();
            Ok("sensors library", $"{devices} device(s), {sensors} sensors, {temps} temperature readings without admin");
            if (Sensors.IsPawnIoInstalled()) Ok("PawnIO driver", "installed");
            else Note("PawnIO driver", "not installed yet - Winnow installs it on first run (as administrator) for CPU temperatures and power");
        }
        catch (Exception ex) { Bad("sensors library", ex.Message); }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Service");
            int n = searcher.Get().Count;
            Ok("WMI", n + " services");
        }
        catch (Exception ex) { Bad("WMI", ex.Message); }

        try
        {
            int n = 0;
            foreach (var (hive, path) in new[] {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") })
            {
                using var k = hive.OpenSubKey(path);
                n += k?.SubKeyCount ?? 0;
            }
            Ok("installed programs", n + " uninstall entries");
        }
        catch (Exception ex) { Bad("uninstall registry", ex.Message); }

        string winget = Shell.WingetPath();
        if (winget != null) Ok("winget", winget);
        else Note("winget", "not found - the Install view needs App Installer from the Microsoft Store");

        try
        {
            // Task.Run: Run() is called from App.OnStartup on the UI thread, where blocking on an
            // await that resumes on the Dispatcher's SynchronizationContext would deadlock.
            var r = Task.Run(() => Shell.PowerShellAsync("'ps-ok'", 60_000)).GetAwaiter().GetResult();
            if (r.Output.Contains("ps-ok")) Ok("PowerShell", "runs");
            else Bad("PowerShell", r.Output.Trim());
        }
        catch (Exception ex) { Bad("PowerShell", ex.Message); }

        try
        {
            using var s = typeof(SelfTest).Assembly.GetManifestResourceStream("Winnow.catalogue.json");
            if (s == null) Bad("install catalogue", "embedded catalogue.json missing");
            else Ok("install catalogue", s.Length + " bytes embedded");
        }
        catch (Exception ex) { Bad("install catalogue", ex.Message); }

        Ok("downloads folder", Paths.Downloads);

        sb.AppendLine();
        sb.AppendLine($"  {sw.ElapsedMilliseconds} ms");
        sb.AppendLine(problems == 0 ? "RESULT: all checks passed" : $"RESULT: PROBLEMS FOUND ({problems})");
        string text = sb.ToString();
        try { File.WriteAllText(Paths.SelfTestFile, text); } catch { }
        try { Console.Out.Write(text); } catch { }
        return problems == 0 ? 0 : 1;
    }
}
