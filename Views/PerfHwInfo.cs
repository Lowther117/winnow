using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>Static facts about one physical disk (WMI), keyed by disk number.</summary>
internal sealed class PerfDiskInfo
{
    public string Model = "";
    public ulong Size;
    public string Media = "";   // SSD | HDD | SCM | ""
    public string Bus = "";     // NVMe | SATA | USB | ...
}

/// <summary>
/// Hardware facts for the Performance page that do not change while Winnow
/// runs (CPU name and cores, memory modules, disk models, GPU names). Read
/// once, in the background - WMI can take a second or two on a cold start.
/// Every field is replaced whole at the end of the load, so the UI thread can
/// read them at any time without locking.
/// </summary>
internal static class PerfHwInfo
{
    public static string CpuName = "";
    public static int Sockets, Cores;
    public static int? MaxMhz, L2KB, L3KB;
    public static string MemModules = "", MemSpeed = "";
    public static Dictionary<int, PerfDiskInfo> Disks = new();
    public static List<string> Gpus = new();

    static Task _task;
    static readonly object _gate = new();

    public static Task LoadAsync()
    {
        lock (_gate) return _task ??= Task.Run(() => Load());
    }

    static int ToInt(object o)
    {
        try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; }
    }

    static ulong ToULong(object o)
    {
        try { return o == null ? 0UL : Convert.ToUInt64(o); } catch { return 0UL; }
    }

    static void Load()
    {
        // ---- CPU
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("ProcessorNameString") is string n && n.Trim().Length > 0) CpuName = n.Trim();
        }
        catch (Exception ex) { Log.Write("perf cpu name: " + ex.Message); }

        try
        {
            int sockets = 0, cores = 0;
            int? max = null, l2 = null, l3 = null;
            string wmiName = null;
            using var q = new ManagementObjectSearcher("SELECT Name, NumberOfCores, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor");
            using var coll = q.Get();
            foreach (ManagementObject mo in coll)
            {
                using (mo)
                {
                    sockets++;
                    cores += ToInt(mo["NumberOfCores"]);
                    int m = ToInt(mo["MaxClockSpeed"]);
                    if (m > 0 && max == null) max = m;
                    int c2 = ToInt(mo["L2CacheSize"]);
                    if (c2 > 0 && l2 == null) l2 = c2;
                    int c3 = ToInt(mo["L3CacheSize"]);
                    if (c3 > 0 && l3 == null) l3 = c3;
                    if (wmiName == null) wmiName = (mo["Name"] as string)?.Trim();
                }
            }
            Sockets = sockets;
            Cores = cores;
            MaxMhz = max;
            L2KB = l2;
            L3KB = l3;
            if (string.IsNullOrEmpty(CpuName) && !string.IsNullOrEmpty(wmiName)) CpuName = wmiName;
        }
        catch (Exception ex) { Log.Write("perf cpu wmi: " + ex.Message); }

        // ---- memory modules
        try
        {
            var caps = new List<ulong>();
            int speed = 0;
            using var q = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            using var coll = q.Get();
            foreach (ManagementObject mo in coll)
            {
                using (mo)
                {
                    ulong cap = ToULong(mo["Capacity"]);
                    if (cap > 0) caps.Add(cap);
                    int s = ToInt(mo["ConfiguredClockSpeed"]);
                    if (s <= 0) s = ToInt(mo["Speed"]);
                    if (s > speed) speed = s;
                }
            }
            if (caps.Count > 0)
                MemModules = caps.All(c => c == caps[0])
                    ? caps.Count + " × " + Fmt.Bytes(caps[0])
                    : string.Join(" + ", caps.Select(c => Fmt.Bytes(c)));
            if (speed > 0) MemSpeed = speed + " MT/s";
        }
        catch (Exception ex) { Log.Write("perf memory wmi: " + ex.Message); }

        // ---- disks
        var disks = new Dictionary<int, PerfDiskInfo>();
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Index, Model, Size FROM Win32_DiskDrive");
            using var coll = q.Get();
            foreach (ManagementObject mo in coll)
            {
                using (mo)
                {
                    int idx = ToInt(mo["Index"]);
                    disks[idx] = new PerfDiskInfo
                    {
                        Model = (mo["Model"] as string)?.Trim() ?? "",
                        Size = ToULong(mo["Size"]),
                    };
                }
            }
        }
        catch (Exception ex) { Log.Write("perf disk wmi: " + ex.Message); }
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"));
            using var coll = q.Get();
            foreach (ManagementObject mo in coll)
            {
                using (mo)
                {
                    if (!int.TryParse(Convert.ToString(mo["DeviceId"]), out int idx)) continue;
                    if (!disks.TryGetValue(idx, out var d)) disks[idx] = d = new PerfDiskInfo();
                    d.Media = MediaName(ToInt(mo["MediaType"]));
                    d.Bus = BusName(ToInt(mo["BusType"]));
                }
            }
        }
        catch (Exception ex) { Log.Write("perf storage wmi: " + ex.Message); }
        Disks = disks;

        // ---- GPUs
        try
        {
            var gpus = new List<string>();
            using var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var coll = q.Get();
            foreach (ManagementObject mo in coll)
            {
                using (mo)
                {
                    string n = (mo["Name"] as string)?.Trim();
                    if (!string.IsNullOrEmpty(n) && !gpus.Contains(n)) gpus.Add(n);
                }
            }
            Gpus = gpus;
        }
        catch (Exception ex) { Log.Write("perf gpu wmi: " + ex.Message); }
    }

    static string MediaName(int m) => m switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };

    static string BusName(int b) => b switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "Virtual (file)", 16 => "Storage Spaces",
        17 => "NVMe", _ => ""
    };

    /// <summary>"SSD · NVMe" style description, or "" when unknown.</summary>
    public static string DiskKind(int index)
    {
        if (!Disks.TryGetValue(index, out var d)) return "";
        var parts = new List<string>();
        if (d.Media.Length > 0) parts.Add(d.Media);
        if (d.Bus.Length > 0) parts.Add(d.Bus);
        return string.Join(" · ", parts);
    }

    /// <summary>Capacity and free space for each drive letter on a disk. Call off the UI thread.</summary>
    public static string Volumes(string letters)
    {
        var parts = (letters ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "No drive letters on this disk.";
        var lines = new List<string>();
        foreach (var p in parts)
        {
            try
            {
                var di = new DriveInfo(p);
                if (!di.IsReady) { lines.Add(p + "  not ready"); continue; }
                string label = string.IsNullOrWhiteSpace(di.VolumeLabel) ? "" : di.VolumeLabel + "  ";
                lines.Add($"{p}  {label}{di.DriveFormat}  ·  {Fmt.Bytes(di.AvailableFreeSpace)} free of {Fmt.Bytes(di.TotalSize)}");
            }
            catch (Exception ex) { lines.Add(p + "  unavailable: " + ex.Message); }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Addresses and identity of a network adapter. Call off the UI thread.</summary>
    public static string NetDetails(string name)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
            if (nic == null) return "This adapter is no longer present.";
            var lines = new List<string>();
            var ip = nic.GetIPProperties();
            var v4 = new List<string>();
            var v6 = new List<string>();
            foreach (var a in ip.UnicastAddresses)
            {
                string text = a.Address + "/" + a.PrefixLength;
                if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) v4.Add(text);
                else if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) v6.Add(text);
            }
            lines.Add("IPv4 address: " + (v4.Count > 0 ? string.Join(", ", v4) : "none"));
            lines.Add("IPv6 address: " + (v6.Count > 0 ? string.Join(", ", v6) : "none"));
            var gw = ip.GatewayAddresses.Select(g => g.Address.ToString()).ToList();
            if (gw.Count > 0) lines.Add("Gateway: " + string.Join(", ", gw));
            var dns = ip.DnsAddresses.Select(d => d.ToString()).ToList();
            if (dns.Count > 0) lines.Add("DNS servers: " + string.Join(", ", dns));
            if (!string.IsNullOrEmpty(ip.DnsSuffix)) lines.Add("DNS suffix: " + ip.DnsSuffix);
            try
            {
                var v4p = ip.GetIPv4Properties();
                if (v4p != null) lines.Add("DHCP: " + (v4p.IsDhcpEnabled ? "on" : "off"));
            }
            catch { }
            try
            {
                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length > 0) lines.Add("Hardware address: " + string.Join("-", mac.Select(b => b.ToString("X2"))));
            }
            catch { }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return "Adapter details unavailable: " + ex.Message;
        }
    }
}
