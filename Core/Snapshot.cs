namespace Winnow.Core;

// The data model every monitoring view reads. One Snapshot per sampler tick.
//
// The TMOG rule, applied throughout: a reading Winnow could not take is null
// (or NaN in a graph series), never 0. Zero means "measured zero". Views show
// null as "—" / "unavailable", and graphs leave a gap.

public sealed class Snapshot
{
    public DateTime Time;            // local time the tick was taken
    public double IntervalSec;       // real seconds since the previous tick

    // CPU
    public double CpuTotal;          // 0..100, all logical processors
    public double CpuKernel;         // 0..100, kernel-mode share of the total
    public double[] CpuPerCore = System.Array.Empty<double>(); // 0..100 per logical processor
    public double? CpuMhz;           // effective clock (base x % Processor Performance)
    public double? CpuBaseMhz;

    // Memory (bytes)
    public ulong MemTotal, MemAvailable, MemUsed;
    public ulong CommitTotal, CommitLimit, Cached, PagedPool, NonPagedPool;
    public double MemLoadPct;        // 0..100

    // Counts
    public int ProcessCount, ThreadCount, HandleCount;
    public TimeSpan Uptime;

    // Disks and network
    public List<DiskSample> Disks = new();
    public List<NetSample> Nets = new();
    public double DiskReadBps, DiskWriteBps;     // totals over physical disks
    public double? DiskActivePct;                // busiest physical disk
    public double NetRecvBps, NetSendBps;        // totals over active adapters

    // GPU
    public double? GpuPct;           // busiest engine type, summed over processes (Task Manager's method)
    public double? GpuMemUsed;       // bytes, dedicated, all adapters

    // Hardware sensors (LibreHardwareMonitor). Empty when sensors are unavailable.
    public List<SensorReading> Sensors = new();
    public double? CpuTempC, GpuTempC, GpuHotspotC;
    public double? CpuWatts, GpuWatts;

    // Processes (System Idle Process excluded)
    public List<ProcSample> Processes = new();
    public bool PerProcessNetAvailable;   // ETW network trace running
    public bool PerProcessGpuAvailable;   // GPU Engine counters readable

    /// <summary>A lighter copy for the Flight Recorder history: the processes that matter, not all of them.</summary>
    public Snapshot Trimmed(int keep = 40)
    {
        var s = (Snapshot)MemberwiseClone();
        var set = new HashSet<ProcSample>();
        foreach (var p in Processes.OrderByDescending(p => p.Cpu).Take(keep / 2)) set.Add(p);
        foreach (var p in Processes.OrderByDescending(p => p.PrivateWorkingSet).Take(keep / 4)) set.Add(p);
        foreach (var p in Processes.OrderByDescending(p => p.ReadBps + p.WriteBps).Take(keep / 8)) set.Add(p);
        foreach (var p in Processes.OrderByDescending(p => p.NetBps ?? 0).Take(keep / 8)) set.Add(p);
        s.Processes = set.ToList();
        return s;
    }
}

public sealed class ProcSample
{
    public int Pid;
    public int ParentPid;
    public long CreateTime;          // FILETIME (UTC 100ns ticks) - pairs with Pid to identify the process
    public string Name = "";         // image name, e.g. "chrome.exe"
    public double Cpu;               // 0..100 of the whole machine
    public ulong PrivateWorkingSet;  // bytes - Task Manager's "Memory" column
    public ulong WorkingSet;         // bytes
    public ulong CommitBytes;        // bytes - private commit
    public double ReadBps, WriteBps; // all I/O the process issued (file, device, some network)
    public double? NetBps;           // null when the ETW trace is not running
    public double? NetRecvBps, NetSendBps;
    public double? GpuPct;           // null when GPU counters are unavailable
    public double? EstWatts;         // estimate: share of CPU package + GPU power. Null without power sensors.
    public int Threads, Handles, SessionId, BasePriority;

    /// <summary>Stable identity: a recycled PID gets a different key.</summary>
    public string Key => Pid + ":" + CreateTime;
    public DateTime Started => CreateTime > 0 ? DateTime.FromFileTimeUtc(CreateTime).ToLocalTime() : DateTime.MinValue;
}

public sealed class DiskSample
{
    public string Name = "";         // PDH instance, e.g. "0 C:"
    public int Index;                // physical disk number
    public string Letters = "";      // "C: D:"
    public double? ActivePct;        // 100 - % Idle Time
    public double? ReadBps, WriteBps;
    public double? AvgResponseMs;
}

public sealed class NetSample
{
    public string Name = "";
    public string Description = "";
    public string Type = "";         // Ethernet, Wireless80211, ...
    public double RecvBps, SendBps;
    public long LinkSpeedBps;
    public long TotalRecv, TotalSent;
}

public sealed class SensorReading
{
    public string Hardware = "";     // e.g. "AMD Ryzen 7 7800X3D"
    public string HardwareType = ""; // Cpu, GpuNvidia, GpuAmd, GpuIntel, Motherboard, SuperIO, Memory, Storage, Network, Battery, ...
    public string Type = "";         // Temperature, Load, Clock, Power, Fan, Voltage, Data, SmallData, Throughput, Control, Level, ...
    public string Name = "";         // e.g. "Core (Tctl/Tdie)"
    public string Id = "";           // stable identifier, e.g. "/amdcpu/0/temperature/2"
    public float? Value, Min, Max;
    public string Unit => Type switch
    {
        "Temperature" => "°C",
        "Load" or "Control" or "Level" => "%",
        "Clock" or "Frequency" => "MHz",
        "Power" => "W",
        "Fan" => "RPM",
        "Voltage" => "V",
        "Current" => "A",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "B/s",
        "Energy" => "mWh",
        "Flow" => "L/h",
        "Noise" => "dBA",
        "Humidity" => "%",
        _ => ""
    };
}
