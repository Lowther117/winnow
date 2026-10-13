using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Winnow.Core;

/// <summary>
/// Per-process (and per-connection) network bytes from the
/// Microsoft-Windows-Kernel-Network ETW provider - the same live event feed
/// Resource Monitor uses. Runs in a private session, so it never fights other
/// tools over the single "NT Kernel Logger". Needs administrator rights; when
/// it cannot start, per-process network shows as unavailable, not zero.
/// </summary>
public static class NetEtw
{
    static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    static readonly Guid DnsClient = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _dns = new();
    const string SessionName = "Winnow-Network";

    static TraceEventSession _session;
    static readonly object _lock = new();
    static Dictionary<int, long[]> _perPid = new();          // pid -> [recv, sent] bytes since last drain
    static Dictionary<string, long[]> _perConn = new();      // "pid|remote|port" -> [recv, sent] cumulative

    public static bool Running { get; private set; }
    /// <summary>Diagnostics: events seen since start (all / network / DNS / failed to parse).</summary>
    public static long EventsSeen, NetEventsSeen, DnsEventsSeen, ParseFailures;
    public static string LastUnparsed = "";
    public static string Status { get; private set; } = "Not started";

    public static void Start()
    {
        if (Running) return;
        if (!Elevation.IsAdmin) { Status = "Needs administrator rights"; return; }
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x30); // IPv4 | IPv6
            // DNS answers, so the Connections page can name the host behind an IP (what was asked for, not a reverse lookup)
            try { _session.EnableProvider(DnsClient, TraceEventLevel.Informational, ulong.MaxValue); }
            catch (Exception ex) { Log.Write("ETW dns: " + ex.Message); }
            var parser = new RegisteredTraceEventParser(_session.Source);
            parser.All += OnEvent;
            var t = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Log.Write("ETW process: " + ex.Message); }
                Running = false;
                Status = "Stopped";
            }) { IsBackground = true, Name = "Winnow ETW network" };
            t.Start();
            Running = true;
            Status = "OK";
        }
        catch (Exception ex)
        {
            Running = false;
            Status = "Could not start: " + ex.Message;
            Log.Write("ETW start: " + ex);
            try { _session?.Dispose(); } catch { }
            _session = null;
        }
    }

    public static void Stop()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        Running = false;
    }

    // Event IDs: TCP 10/26 send, 11/27 receive; UDP 42/58 send, 43/59 receive (v4/v6).
    static void OnEvent(TraceEvent e)
    {
        Interlocked.Increment(ref EventsSeen);
        int id = (int)e.ID;
        if (e.ProviderGuid == DnsClient)
        {
            if (id == 3008) { Interlocked.Increment(ref DnsEventsSeen); OnDns(e); }
            return;
        }
        bool send = id is 10 or 26 or 42 or 58;
        bool recv = id is 11 or 27 or 43 or 59;
        if (!send && !recv) return;
        Interlocked.Increment(ref NetEventsSeen);
        try
        {
            int pid = Convert.ToInt32(e.PayloadByName("PID"));
            long size = Convert.ToInt64(e.PayloadByName("size"));
            string daddr = e.PayloadStringByName("daddr") ?? "";
            string dport = e.PayloadStringByName("dport") ?? "";
            lock (_lock)
            {
                if (!_perPid.TryGetValue(pid, out var a)) _perPid[pid] = a = new long[2];
                a[recv ? 0 : 1] += size;
                string key = pid + "|" + daddr + "|" + dport;
                if (!_perConn.TryGetValue(key, out var c)) _perConn[key] = c = new long[2];
                c[recv ? 0 : 1] += size;
                if (_perConn.Count > 20000) _perConn.Clear(); // never grow without bound
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref ParseFailures) <= 3)
                LastUnparsed = $"id {id}: {ex.Message}; fields: {string.Join(",", e.PayloadNames ?? System.Array.Empty<string>())}";
        }
    }

    // Event 3008 (query completed): QueryName + QueryResults like "type:  5 alias.example;::ffff:20.42.65.85;"
    static void OnDns(TraceEvent e)
    {
        try
        {
            string name = e.PayloadStringByName("QueryName");
            string results = e.PayloadStringByName("QueryResults");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(results)) return;
            foreach (var raw in results.Split(';'))
            {
                string t = raw.Trim();
                if (t.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) t = t[7..];
                if (System.Net.IPAddress.TryParse(t, out var ip)) _dns[ip.ToString()] = name.TrimEnd('.');
            }
            if (_dns.Count > 50000) _dns.Clear();
        }
        catch { }
    }

    /// <summary>The host name this PC last resolved to the given IP, or null. Seeded from the DNS client's answers.</summary>
    public static string HostFor(string ip) => ip != null && _dns.TryGetValue(ip, out var h) ? h : null;

    /// <summary>Bytes received/sent per pid since the previous call.</summary>
    public static Dictionary<int, long[]> Drain()
    {
        lock (_lock)
        {
            var d = _perPid;
            _perPid = new Dictionary<int, long[]>();
            return d;
        }
    }

    /// <summary>
    /// Cumulative bytes per connection seen since start, keyed "pid|remoteAddress|remotePort".
    /// The port is as the provider reports it; callers should try both byte orders.
    /// </summary>
    public static Dictionary<string, long[]> ConnectionTotals()
    {
        lock (_lock) return _perConn.ToDictionary(k => k.Key, v => (long[])v.Value.Clone());
    }
}
