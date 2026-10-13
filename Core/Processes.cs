using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Winnow.Core;

/// <summary>Slow-changing details about a process, looked up once per (pid, start time) and cached.</summary>
public sealed class ProcDetails
{
    public string Path = "";
    public string Description = "";
    public string Company = "";
    public string User = "";
    public string CommandLine = "";
    public bool Accessible;
}

public static class ProcessInfo
{
    static readonly Dictionary<string, ProcDetails> _cache = new();
    static readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();

    /// <summary>Details for a process sample. Cheap after the first call.</summary>
    public static ProcDetails Get(ProcSample p) => Get(p.Pid, p.CreateTime);

    public static ProcDetails Get(int pid, long createTime)
    {
        string key = pid + ":" + createTime;
        lock (_lock) if (_cache.TryGetValue(key, out var d)) return d;
        var det = Lookup(pid);
        lock (_lock)
        {
            if (_cache.Count > 5000) _cache.Clear();
            _cache[key] = det;
        }
        return det;
    }

    static ProcDetails Lookup(int pid)
    {
        var d = new ProcDetails();
        if (pid <= 4) { d.Description = pid == 4 ? "NT Kernel & System" : ""; d.User = "SYSTEM"; return d; }
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return d;
        try
        {
            d.Accessible = true;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) d.Path = sb.ToString();
            d.User = UserOf(h);
            d.CommandLine = CommandLineOf(h);
        }
        finally { Native.CloseHandle(h); }

        if (d.Path.Length > 0)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(d.Path);
                d.Description = vi.FileDescription ?? "";
                d.Company = vi.CompanyName ?? "";
            }
            catch { }
        }
        return d;
    }

    static string UserOf(IntPtr process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out IntPtr token)) return "";
        try
        {
            Native.GetTokenInformation(token, Native.TokenUser, IntPtr.Zero, 0, out int len);
            if (len <= 0) return "";
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!Native.GetTokenInformation(token, Native.TokenUser, buf, len, out len)) return "";
                IntPtr sidPtr = Marshal.ReadIntPtr(buf); // TOKEN_USER.User.Sid
                var sid = new SecurityIdentifier(sidPtr);
                try
                {
                    string acct = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
                    int slash = acct.IndexOf('\\');
                    return slash >= 0 ? acct[(slash + 1)..] : acct;
                }
                catch { return sid.Value; }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.CloseHandle(token); }
    }

    static string CommandLineOf(IntPtr process)
    {
        const int ProcessCommandLineInformation = 60;
        Native.NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out int len);
        if (len <= 0 || len > 1 << 20) return "";
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (Native.NtQueryInformationProcess(process, ProcessCommandLineInformation, buf, len, out _) != 0) return "";
            return Native.ReadUnicodeString(buf);
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Small (16 px) shell icon for an exe path. Call on the UI thread. Null if none.</summary>
    public static ImageSource Icon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (_lock) if (_icons.TryGetValue(path, out var cached)) return cached;
        ImageSource img = null;
        try
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_ICON | Native.SHGFI_SMALLICON);
            if (info.hIcon != IntPtr.Zero)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                img = bs;
                Native.DestroyIcon(info.hIcon);
            }
        }
        catch { }
        lock (_lock) _icons[path] = img;
        return img;
    }
}

/// <summary>
/// Acting on processes. Every action re-checks the process's start time first,
/// so a PID Windows has recycled since the list was drawn can never redirect a
/// kill to the wrong program.
/// </summary>
public static class ProcessActions
{
    public static bool IsSameProcess(int pid, long createTime)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return Native.GetProcessTimes(h, out long c, out _, out _, out _) && c == createTime; }
        finally { Native.CloseHandle(h); }
    }

    static (bool ok, string message) With(int pid, long createTime, uint access, Func<IntPtr, bool> act, string verb,
                                          bool refuseCritical = false)
    {
        if (!IsSameProcess(pid, createTime)) return (false, "that process has already exited");
        IntPtr h = Native.OpenProcess(access | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (false, $"access denied ({Marshal.GetLastWin32Error()})");
        try
        {
            // check again on the handle we will actually use
            if (!Native.GetProcessTimes(h, out long c, out _, out _, out _) || c != createTime)
                return (false, "that process has already exited");
            if (refuseCritical)
            {
                if (pid == Environment.ProcessId)
                    return (false, "that is Winnow itself - close it from its own window instead");
                if (Native.IsProcessCritical(h, out bool critical) && critical)
                    return (false, "Windows marks that process as critical - ending or suspending it would crash the PC");
            }
            return act(h) ? (true, verb) : (false, $"{verb} failed ({Marshal.GetLastWin32Error()})");
        }
        finally { Native.CloseHandle(h); }
    }

    public static (bool ok, string message) Kill(ProcSample p) =>
        With(p.Pid, p.CreateTime, Native.PROCESS_TERMINATE, h => Native.TerminateProcess(h, 1), "ended", refuseCritical: true);

    /// <summary>Kill a process and every descendant (descendants first).</summary>
    public static (int ok, int failed) KillTree(ProcSample root, IReadOnlyList<ProcSample> all)
    {
        var tree = Descendants(root, all);
        tree.Reverse();
        tree.Add(root);
        int ok = 0, failed = 0;
        foreach (var p in tree) if (Kill(p).ok) ok++; else failed++;
        return (ok, failed);
    }

    /// <summary>All descendants. A child must have started after its parent, which rules out recycled PIDs.</summary>
    public static List<ProcSample> Descendants(ProcSample root, IReadOnlyList<ProcSample> all)
    {
        var result = new List<ProcSample>();
        var queue = new Queue<ProcSample>();
        queue.Enqueue(root);
        var seen = new HashSet<string> { root.Key };
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var c in all)
            {
                if (c.ParentPid == parent.Pid && c.CreateTime >= parent.CreateTime && seen.Add(c.Key))
                {
                    result.Add(c);
                    queue.Enqueue(c);
                }
            }
        }
        return result;
    }

    public static (bool ok, string message) Suspend(ProcSample p) =>
        With(p.Pid, p.CreateTime, Native.PROCESS_SUSPEND_RESUME, h => Native.NtSuspendProcess(h) == 0, "suspended", refuseCritical: true);

    public static (bool ok, string message) Resume(ProcSample p) =>
        With(p.Pid, p.CreateTime, Native.PROCESS_SUSPEND_RESUME, h => Native.NtResumeProcess(h) == 0, "resumed");

    public static readonly (string name, uint value)[] Priorities =
    {
        ("Realtime", 0x100), ("High", 0x80), ("Above normal", 0x8000), ("Normal", 0x20), ("Below normal", 0x4000), ("Low", 0x40)
    };

    public static (bool ok, string message) SetPriority(ProcSample p, uint priorityClass) =>
        With(p.Pid, p.CreateTime, Native.PROCESS_SET_INFORMATION, h => Native.SetPriorityClass(h, priorityClass), "priority set");

    public static string PriorityName(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return Fmt.Na;
        try
        {
            uint c = Native.GetPriorityClass(h);
            foreach (var (name, value) in Priorities) if (value == c) return name;
            return Fmt.Na;
        }
        finally { Native.CloseHandle(h); }
    }

    public static void OpenFileLocation(ProcSample p)
    {
        var d = ProcessInfo.Get(p);
        if (!string.IsNullOrEmpty(d.Path)) Shell.Reveal(d.Path);
    }
}

/// <summary>Which Windows services run inside which process (so svchost.exe can say what it is). Refreshed in the background.</summary>
public static class ServiceMap
{
    static volatile Dictionary<int, List<(string name, string display)>> _map = new();
    static long _last;
    static int _busy;

    public static IReadOnlyList<(string name, string display)> For(int pid)
    {
        Refresh();
        return _map.TryGetValue(pid, out var l) ? l : (IReadOnlyList<(string, string)>)System.Array.Empty<(string, string)>();
    }

    public static string Describe(int pid)
    {
        var l = For(pid);
        if (l.Count == 0) return "";
        return l.Count <= 3 ? string.Join(", ", l.Select(x => x.display)) : string.Join(", ", l.Take(2).Select(x => x.display)) + $" +{l.Count - 2} more";
    }

    /// <summary>Kick a background refresh if the map is older than 10 s.</summary>
    public static void Refresh(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _last < 10_000) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _last = now;
        Task.Run(() =>
        {
            try
            {
                var map = new Dictionary<int, List<(string, string)>>();
                using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE ProcessId <> 0");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        int pid = Convert.ToInt32(mo["ProcessId"]);
                        if (!map.TryGetValue(pid, out var l)) map[pid] = l = new List<(string, string)>();
                        l.Add(((string)mo["Name"] ?? "", (string)mo["DisplayName"] ?? ""));
                    }
                }
                _map = map;
            }
            catch (Exception ex) { Log.Write("service map: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }
}
