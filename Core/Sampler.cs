using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Winnow.Core;

/// <summary>
/// The measuring core. One background thread takes a complete Snapshot every
/// Settings.RefreshMs and hands it to the UI thread through the Tick event.
/// Sampling never runs on the UI thread, so a slow counter can never freeze
/// the window.
///
/// Sources: NtQuerySystemInformation for processes and per-core CPU (one
/// system call each per tick, not one per process), PDH for disks, GPU
/// engines and effective clock, GlobalMemoryStatusEx / GetPerformanceInfo for
/// memory, NetworkInterface for adapters, NetEtw for per-process network, and
/// Sensors for temperatures, fans and power.
/// </summary>
public static class Sampler
{
    /// <summary>Raised on the UI thread after every tick.</summary>
    public static event Action<Snapshot> Tick;

    public static Snapshot Latest { get; private set; }

    /// <summary>Rolling history of trimmed snapshots - the Flight Recorder's live buffer.</summary>
    public static RingBuffer<Snapshot> History { get; private set; } = new(3600);

    public static int LogicalProcessors { get; } = Environment.ProcessorCount;

    /// <summary>Efficiency class per logical processor (higher = faster core). All equal on non-hybrid CPUs.</summary>
    public static byte[] EfficiencyClass { get; private set; } = System.Array.Empty<byte>();
    public static bool IsHybrid { get; private set; }

    static Thread _thread;
    static volatile bool _stop;
    static Dispatcher _ui;

    // PDH paths
    const string PDiskIdle = @"\PhysicalDisk(*)\% Idle Time";
    const string PDiskRead = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    const string PDiskWrite = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    const string PDiskResp = @"\PhysicalDisk(*)\Avg. Disk sec/Transfer";
    const string PGpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    const string PGpuMem = @"\GPU Adapter Memory(*)\Dedicated Usage";
    const string PCpuPerf = @"\Processor Information(_Total)\% Processor Performance";

    public static void Start()
    {
        if (_thread != null) return;
        _ui = Application.Current?.Dispatcher;
        ResizeHistory();
        ReadTopology();
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Winnow sampler", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static void Stop() => _stop = true;

    /// <summary>Recompute the history length after the refresh rate or recorder minutes change.</summary>
    public static void ResizeHistory()
    {
        int cap = Math.Max(60, Settings.Current.RecorderMinutes * 60_000 / Math.Max(250, Settings.Current.RefreshMs));
        if (History.Capacity != cap)
        {
            var old = History.ToList();
            var fresh = new RingBuffer<Snapshot>(cap);
            foreach (var s in old) fresh.Push(s);
            History = fresh;
        }
    }

    // ---------------------------------------------------------------- loop

    static void Loop()
    {
        var engine = new Engine();
        engine.Sample(prime: true);
        Thread.Sleep(500);
        while (!_stop)
        {
            long t0 = Environment.TickCount64;
            Snapshot snap = null;
            try { snap = engine.Sample(prime: false); }
            catch (Exception ex) { Log.Write("sampler: " + ex); }

            if (snap != null)
            {
                Latest = snap;
                History.Push(snap.Trimmed());
                var ui = _ui;
                if (ui != null && !ui.HasShutdownStarted)
                    ui.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        try { Tick?.Invoke(snap); }
                        catch (Exception ex) { Log.Write("tick handler: " + ex); }
                    }));
            }

            int wait = Math.Max(100, Settings.Current.RefreshMs - (int)(Environment.TickCount64 - t0));
            for (int waited = 0; waited < wait && !_stop; waited += 25) Thread.Sleep(25);
        }
        engine.Dispose();
    }

    /// <summary>Take two samples on the calling thread and return the second. Used by the self-test.</summary>
    public static Snapshot SampleOnce()
    {
        using var engine = new Engine();
        engine.Sample(prime: true);
        Thread.Sleep(600);
        return engine.Sample(prime: false);
    }

    static void ReadTopology()
    {
        try
        {
            Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint len, IntPtr.Zero, 0);
            if (len == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!Native.GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0)) return;
                var classes = new byte[LogicalProcessors];
                int off = 0;
                while (off < len)
                {
                    int size = Marshal.ReadInt32(buf, off);
                    int type = Marshal.ReadInt32(buf, off + 4);
                    if (size <= 0) break;
                    if (type == 0) // CpuSetInformation
                    {
                        byte logical = Marshal.ReadByte(buf, off + 14);
                        ushort group = (ushort)Marshal.ReadInt16(buf, off + 12);
                        byte eff = Marshal.ReadByte(buf, off + 18);
                        int idx = group * 64 + logical;
                        if (idx < classes.Length) classes[idx] = eff;
                    }
                    off += size;
                }
                EfficiencyClass = classes;
                IsHybrid = classes.Distinct().Count() > 1;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.Write("cpu sets: " + ex.Message); }
    }

    // --------------------------------------------------------------- engine

    sealed class Engine : IDisposable
    {
        readonly PdhQuery _pdh = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _lastSec;
        readonly Dictionary<string, long> _prevCpuTime = new();   // key -> kernel+user (100ns)
        readonly Dictionary<string, long> _prevRead = new();
        readonly Dictionary<string, long> _prevWrite = new();
        long[] _prevIdle = System.Array.Empty<long>(), _prevKernel = System.Array.Empty<long>(), _prevUser = System.Array.Empty<long>();
        readonly Dictionary<string, (long rx, long tx)> _prevNet = new();
        IntPtr _buf = IntPtr.Zero;
        int _bufSize = 1 << 20;
        readonly double? _baseMhz;

        public Engine()
        {
            foreach (var p in new[] { PDiskIdle, PDiskRead, PDiskWrite, PDiskResp, PGpuEngine, PGpuMem, PCpuPerf })
                _pdh.Add(p);
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (k?.GetValue("~MHz") is int mhz && mhz > 0) _baseMhz = mhz;
            }
            catch { }
        }

        public void Dispose()
        {
            _pdh.Dispose();
            if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
        }

        public Snapshot Sample(bool prime)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Max(0.05, now - _lastSec);
            _lastSec = now;

            _pdh.Collect();
            var s = new Snapshot { Time = DateTime.Now, IntervalSec = dt, CpuBaseMhz = _baseMhz };

            SampleCores(s);
            SampleMemory(s);
            SampleProcesses(s, dt, prime);
            SampleDisks(s);
            SampleGpu(s);
            SampleNetwork(s, dt);
            Sensors.Pick(s, Sensors.Latest);
            EstimatePower(s);

            s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (_baseMhz.HasValue)
            {
                var perf = _pdh.Value(PCpuPerf);
                if (perf.HasValue) s.CpuMhz = _baseMhz.Value * perf.Value / 100.0;
            }
            return prime ? null : s;
        }

        // ---- CPU per logical processor
        void SampleCores(Snapshot s)
        {
            int n = LogicalProcessors;
            int size = 48 * n;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.NtQuerySystemInformation(Native.SystemProcessorPerformanceInformation, buf, size, out int ret) != 0) return;
                int count = Math.Min(n, ret / 48);
                if (_prevIdle.Length != count)
                {
                    _prevIdle = new long[count]; _prevKernel = new long[count]; _prevUser = new long[count];
                }
                var per = new double[count];
                double busySum = 0, totSum = 0, kernSum = 0;
                for (int i = 0; i < count; i++)
                {
                    long idle = Marshal.ReadInt64(buf, i * 48);
                    long kernel = Marshal.ReadInt64(buf, i * 48 + 8);  // includes idle
                    long user = Marshal.ReadInt64(buf, i * 48 + 16);
                    long dIdle = idle - _prevIdle[i], dKern = kernel - _prevKernel[i], dUser = user - _prevUser[i];
                    _prevIdle[i] = idle; _prevKernel[i] = kernel; _prevUser[i] = user;
                    long total = dKern + dUser;
                    double busy = total > 0 ? Math.Clamp((double)(total - dIdle) / total, 0, 1) : 0;
                    per[i] = busy * 100;
                    busySum += Math.Max(0, total - dIdle);
                    totSum += Math.Max(0, total);
                    kernSum += Math.Max(0, dKern - dIdle);
                }
                s.CpuPerCore = per;
                s.CpuTotal = totSum > 0 ? Math.Clamp(busySum / totSum * 100, 0, 100) : 0;
                s.CpuKernel = totSum > 0 ? Math.Clamp(kernSum / totSum * 100, 0, 100) : 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---- memory
        static void SampleMemory(Snapshot s)
        {
            var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            if (Native.GlobalMemoryStatusEx(ref m))
            {
                s.MemTotal = m.ullTotalPhys;
                s.MemAvailable = m.ullAvailPhys;
                s.MemUsed = m.ullTotalPhys - m.ullAvailPhys;
                s.MemLoadPct = m.ullTotalPhys > 0 ? 100.0 * s.MemUsed / m.ullTotalPhys : 0;
            }
            var p = new Native.PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>() };
            if (Native.GetPerformanceInfo(ref p, p.cb))
            {
                ulong page = (ulong)p.PageSize;
                s.CommitTotal = (ulong)p.CommitTotal * page;
                s.CommitLimit = (ulong)p.CommitLimit * page;
                s.Cached = (ulong)p.SystemCache * page;
                s.PagedPool = (ulong)p.KernelPaged * page;
                s.NonPagedPool = (ulong)p.KernelNonpaged * page;
                s.HandleCount = (int)p.HandleCount;
                s.ProcessCount = (int)p.ProcessCount;
                s.ThreadCount = (int)p.ThreadCount;
            }
        }

        // ---- processes: one NtQuerySystemInformation call for all of them
        void SampleProcesses(Snapshot s, double dt, bool prime)
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_bufSize);
            uint st;
            int ret;
            while ((st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buf, _bufSize, out ret)) == Native.STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buf);
                _bufSize = Math.Max(_bufSize * 2, ret + (1 << 16));
                _buf = Marshal.AllocHGlobal(_bufSize);
            }
            if (st != 0) return;

            double cpuScale = 100.0 / (dt * 10_000_000.0 * LogicalProcessors);
            var seen = new HashSet<string>();
            var list = new List<ProcSample>(400);
            int off = 0;
            while (true)
            {
                IntPtr e = _buf + off;
                int next = Marshal.ReadInt32(e, 0x00);
                int pid = (int)Marshal.ReadInt64(e, 0x50);
                if (pid != 0)
                {
                    var p = new ProcSample
                    {
                        Pid = pid,
                        Threads = Marshal.ReadInt32(e, 0x04),
                        PrivateWorkingSet = (ulong)Marshal.ReadInt64(e, 0x08),
                        CreateTime = Marshal.ReadInt64(e, 0x20),
                        Name = Native.ReadUnicodeString(e + 0x38),
                        BasePriority = Marshal.ReadInt32(e, 0x48),
                        ParentPid = (int)Marshal.ReadInt64(e, 0x58),
                        Handles = Marshal.ReadInt32(e, 0x60),
                        SessionId = Marshal.ReadInt32(e, 0x64),
                        WorkingSet = (ulong)Marshal.ReadInt64(e, 0x90),
                        CommitBytes = (ulong)Marshal.ReadInt64(e, 0xB8),
                    };
                    if (pid == 4 && string.IsNullOrEmpty(p.Name)) p.Name = "System";
                    if (string.IsNullOrEmpty(p.Name)) p.Name = pid == 4 ? "System" : "pid " + pid;

                    long cpu = Marshal.ReadInt64(e, 0x28) + Marshal.ReadInt64(e, 0x30); // user + kernel
                    long rd = Marshal.ReadInt64(e, 0xE8);
                    long wr = Marshal.ReadInt64(e, 0xF0);
                    string key = p.Key;
                    seen.Add(key);
                    if (_prevCpuTime.TryGetValue(key, out long pc))
                    {
                        p.Cpu = Math.Clamp((cpu - pc) * cpuScale, 0, 100);
                        p.ReadBps = Math.Max(0, (rd - _prevRead[key]) / dt);
                        p.WriteBps = Math.Max(0, (wr - _prevWrite[key]) / dt);
                    }
                    _prevCpuTime[key] = cpu; _prevRead[key] = rd; _prevWrite[key] = wr;
                    list.Add(p);
                }
                if (next == 0) break;
                off += next;
            }

            // forget processes that have gone
            if (_prevCpuTime.Count > seen.Count + 64)
            {
                foreach (var k in _prevCpuTime.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    _prevCpuTime.Remove(k); _prevRead.Remove(k); _prevWrite.Remove(k);
                }
            }

            // per-process network from ETW
            s.PerProcessNetAvailable = NetEtw.Running;
            if (NetEtw.Running)
            {
                var net = NetEtw.Drain();
                foreach (var p in list)
                {
                    if (net.TryGetValue(p.Pid, out var a))
                    {
                        p.NetRecvBps = a[0] / dt; p.NetSendBps = a[1] / dt; p.NetBps = (a[0] + a[1]) / dt;
                    }
                    else { p.NetRecvBps = 0; p.NetSendBps = 0; p.NetBps = 0; }
                }
            }
            s.Processes = list;
        }

        // ---- physical disks
        void SampleDisks(Snapshot s)
        {
            var idle = _pdh.Instances(PDiskIdle);
            var rd = _pdh.Instances(PDiskRead);
            var wr = _pdh.Instances(PDiskWrite);
            var resp = _pdh.Instances(PDiskResp);
            foreach (var name in idle.Keys.Union(rd.Keys))
            {
                if (name == "_Total") continue;
                var d = new DiskSample { Name = name };
                var parts = name.Split(' ', 2);
                if (int.TryParse(parts[0], out int idx)) d.Index = idx;
                d.Letters = parts.Length > 1 ? parts[1] : "";
                if (idle.TryGetValue(name, out double id)) d.ActivePct = Math.Clamp(100 - id, 0, 100);
                if (rd.TryGetValue(name, out double r)) d.ReadBps = r;
                if (wr.TryGetValue(name, out double w)) d.WriteBps = w;
                if (resp.TryGetValue(name, out double t)) d.AvgResponseMs = t * 1000;
                s.Disks.Add(d);
            }
            s.Disks.Sort((a, b) => a.Index.CompareTo(b.Index));
            s.DiskReadBps = s.Disks.Sum(d => d.ReadBps ?? 0);
            s.DiskWriteBps = s.Disks.Sum(d => d.WriteBps ?? 0);
            s.DiskActivePct = s.Disks.Count > 0 && s.Disks.Any(d => d.ActivePct.HasValue)
                ? s.Disks.Max(d => d.ActivePct ?? 0) : null;
        }

        // ---- GPU engines, Task Manager's method: per engine type, sum over processes; GPU% = busiest type.
        void SampleGpu(Snapshot s)
        {
            var eng = _pdh.Instances(PGpuEngine);
            if (eng.Count == 0) { s.PerProcessGpuAvailable = false; return; }
            s.PerProcessGpuAvailable = true;
            var perPidType = new Dictionary<(int pid, string type), double>();
            var perType = new Dictionary<string, double>();
            foreach (var kv in eng)
            {
                // pid_1234_luid_0x00000000_0x0000D1E5_phys_0_eng_0_engtype_3D
                string n = kv.Key;
                if (!n.StartsWith("pid_")) continue;
                int us = n.IndexOf('_', 4);
                if (us < 0 || !int.TryParse(n.AsSpan(4, us - 4), out int pid)) continue;
                int et = n.IndexOf("engtype_", StringComparison.Ordinal);
                string type = et >= 0 ? n[(et + 8)..] : "?";
                var k = (pid, type);
                perPidType[k] = perPidType.TryGetValue(k, out double v) ? v + kv.Value : kv.Value;
                perType[type] = perType.TryGetValue(type, out double t) ? t + kv.Value : kv.Value;
            }
            s.GpuPct = perType.Count > 0 ? Math.Clamp(perType.Values.Max(), 0, 100) : 0;
            var perPid = new Dictionary<int, double>();
            foreach (var kv in perPidType)
                perPid[kv.Key.pid] = Math.Max(perPid.TryGetValue(kv.Key.pid, out double v) ? v : 0, kv.Value);
            foreach (var p in s.Processes)
                p.GpuPct = perPid.TryGetValue(p.Pid, out double g) ? Math.Clamp(g, 0, 100) : 0;

            var mem = _pdh.Instances(PGpuMem);
            if (mem.Count > 0) s.GpuMemUsed = mem.Values.Sum();
        }

        // ---- network adapters
        void SampleNetwork(Snapshot s, double dt)
        {
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return; }
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                long rx, tx;
                try { var st = nic.GetIPStatistics(); rx = st.BytesReceived; tx = st.BytesSent; }
                catch { continue; }
                var n = new NetSample
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Type = nic.NetworkInterfaceType.ToString(),
                    LinkSpeedBps = nic.Speed,
                    TotalRecv = rx,
                    TotalSent = tx,
                };
                if (_prevNet.TryGetValue(nic.Id, out var prev))
                {
                    n.RecvBps = Math.Max(0, (rx - prev.rx) / dt);
                    n.SendBps = Math.Max(0, (tx - prev.tx) / dt);
                }
                _prevNet[nic.Id] = (rx, tx);
                s.Nets.Add(n);
            }
            s.NetRecvBps = s.Nets.Sum(n => n.RecvBps);
            s.NetSendBps = s.Nets.Sum(n => n.SendBps);
        }

        // ---- per-process power: an estimate, and labelled as one.
        static void EstimatePower(Snapshot s)
        {
            if (!s.CpuWatts.HasValue && !s.GpuWatts.HasValue) return;
            double cpuSum = s.Processes.Sum(p => p.Cpu);
            double gpuSum = s.Processes.Sum(p => p.GpuPct ?? 0);
            foreach (var p in s.Processes)
            {
                double w = 0;
                if (s.CpuWatts.HasValue && cpuSum > 0) w += s.CpuWatts.Value * p.Cpu / cpuSum;
                if (s.GpuWatts.HasValue && gpuSum > 0) w += s.GpuWatts.Value * (p.GpuPct ?? 0) / gpuSum;
                p.EstWatts = w;
            }
        }
    }
}
