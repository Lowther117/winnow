using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Winnow.Core;

/// <summary>One TCP connection or UDP endpoint, with the process that owns it.</summary>
public sealed class ConnEntry
{
    public string Protocol = "TCP";        // TCP | UDP
    public bool IsV6;
    public IPAddress LocalAddress = IPAddress.Any;
    public int LocalPort;
    public IPAddress RemoteAddress;        // null for UDP endpoints (they have no remote end)
    public int RemotePort;
    public int State;                      // MIB_TCP_STATE (1..12); 0 for UDP
    public int Pid;

    public const int StateListen = 2;
    public const int StateEstablished = 5;

    static readonly string[] StateNames =
    {
        "—", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1",
        "FIN wait 2", "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB",
    };

    public string StateName => Protocol == "UDP" ? Fmt.Na : State > 0 && State < StateNames.Length ? StateNames[State] : Fmt.Na;

    /// <summary>True when there is a real remote end to talk about (not a listener, not UDP, not 0.0.0.0).</summary>
    public bool HasRemote =>
        RemoteAddress != null && State != StateListen &&
        !RemoteAddress.Equals(IPAddress.Any) && !RemoteAddress.Equals(IPAddress.IPv6Any);

    public string LocalText => Endpoint(LocalAddress, LocalPort);
    public string RemoteText => HasRemote ? Endpoint(RemoteAddress, RemotePort) : Fmt.Na;

    /// <summary>Identity for in-place row updates: protocol + local + remote + pid.</summary>
    public string Key => Protocol + (IsV6 ? "6" : "4") + "|" + LocalText + "|" + RemoteText + "|" + Pid;

    public static string Endpoint(IPAddress a, int port)
    {
        if (a == null) return Fmt.Na;
        return a.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + a + "]:" + port : a + ":" + port;
    }

    /// <summary>The address as a plain string for lookups: IPv4-mapped IPv6 becomes IPv4, scope ids are dropped.</summary>
    public static string LookupKey(IPAddress a)
    {
        if (a == null) return null;
        if (a.IsIPv4MappedToIPv6) return a.MapToIPv4().ToString();
        if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0) return new IPAddress(a.GetAddressBytes()).ToString();
        return a.ToString();
    }

    public static bool IsLoopback(IPAddress a)
    {
        if (a == null) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        return IPAddress.IsLoopback(a);
    }
}

/// <summary>
/// The system's TCP and UDP tables with owning process ids, from
/// GetExtendedTcpTable / GetExtendedUdpTable (iphlpapi.dll) - what netstat -ano
/// and Resource Monitor read. Cheap enough to call every tick.
/// </summary>
public static class ConnTable
{
    const int AF_INET = 2;
    const int AF_INET6 = 23;
    const int TCP_TABLE_OWNER_PID_ALL = 5;
    const int UDP_TABLE_OWNER_PID = 1;
    const uint ERROR_INSUFFICIENT_BUFFER = 122;

    // Row sizes (all members are DWORDs or byte arrays, so no padding; the table starts after a 4-byte count).
    const int Tcp4Row = 24;   // state, localAddr, localPort, remoteAddr, remotePort, owningPid
    const int Tcp6Row = 56;   // localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, owningPid
    const int Udp4Row = 12;   // localAddr, localPort, owningPid
    const int Udp6Row = 28;   // localAddr[16], localScope, localPort, owningPid

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>All TCP connections (IPv4 + IPv6) and UDP endpoints. Never throws; a table that cannot be read is skipped.</summary>
    public static List<ConnEntry> Read()
    {
        var list = new List<ConnEntry>(512);
        try { ReadTcp4(list); } catch (Exception ex) { Log.Write("tcp4 table: " + ex.Message); }
        try { ReadTcp6(list); } catch (Exception ex) { Log.Write("tcp6 table: " + ex.Message); }
        try { ReadUdp4(list); } catch (Exception ex) { Log.Write("udp4 table: " + ex.Message); }
        try { ReadUdp6(list); } catch (Exception ex) { Log.Write("udp6 table: " + ex.Message); }
        return list;
    }

    /// <summary>Call the API with a growing buffer until the table fits. Caller frees the result.</summary>
    static IntPtr Fetch(bool tcp, int af, int tableClass, out int size)
    {
        size = 0;
        IntPtr buf = IntPtr.Zero;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            uint rc = tcp
                ? GetExtendedTcpTable(buf, ref size, false, af, tableClass, 0)
                : GetExtendedUdpTable(buf, ref size, false, af, tableClass, 0);
            if (rc == 0 && buf != IntPtr.Zero) return buf;
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
            if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0) return IntPtr.Zero;
            size = Math.Max(size, 64) + 4096;   // the table can grow between the two calls
            buf = Marshal.AllocHGlobal(size);
        }
        if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        return IntPtr.Zero;
    }

    static int Port(int raw) => (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    static IPAddress V4(IntPtr buf, int offset)
    {
        var b = new byte[4];
        Marshal.Copy(buf + offset, b, 0, 4);
        return new IPAddress(b);
    }

    static IPAddress V6(IntPtr buf, int offset, uint scope)
    {
        var b = new byte[16];
        Marshal.Copy(buf + offset, b, 0, 16);
        return new IPAddress(b, scope);
    }

    static int Count(IntPtr buf, int size, int rowSize) =>
        Math.Max(0, Math.Min(Marshal.ReadInt32(buf), (size - 4) / rowSize));

    static void ReadTcp4(List<ConnEntry> list)
    {
        IntPtr buf = Fetch(true, AF_INET, TCP_TABLE_OWNER_PID_ALL, out int size);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Count(buf, size, Tcp4Row);
            for (int i = 0; i < n; i++)
            {
                int o = 4 + i * Tcp4Row;
                list.Add(new ConnEntry
                {
                    Protocol = "TCP",
                    IsV6 = false,
                    State = Marshal.ReadInt32(buf, o),
                    LocalAddress = V4(buf, o + 4),
                    LocalPort = Port(Marshal.ReadInt32(buf, o + 8)),
                    RemoteAddress = V4(buf, o + 12),
                    RemotePort = Port(Marshal.ReadInt32(buf, o + 16)),
                    Pid = Marshal.ReadInt32(buf, o + 20),
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static void ReadTcp6(List<ConnEntry> list)
    {
        IntPtr buf = Fetch(true, AF_INET6, TCP_TABLE_OWNER_PID_ALL, out int size);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Count(buf, size, Tcp6Row);
            for (int i = 0; i < n; i++)
            {
                int o = 4 + i * Tcp6Row;
                uint localScope = (uint)Marshal.ReadInt32(buf, o + 16);
                uint remoteScope = (uint)Marshal.ReadInt32(buf, o + 40);
                list.Add(new ConnEntry
                {
                    Protocol = "TCP",
                    IsV6 = true,
                    LocalAddress = V6(buf, o, localScope),
                    LocalPort = Port(Marshal.ReadInt32(buf, o + 20)),
                    RemoteAddress = V6(buf, o + 24, remoteScope),
                    RemotePort = Port(Marshal.ReadInt32(buf, o + 44)),
                    State = Marshal.ReadInt32(buf, o + 48),
                    Pid = Marshal.ReadInt32(buf, o + 52),
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static void ReadUdp4(List<ConnEntry> list)
    {
        IntPtr buf = Fetch(false, AF_INET, UDP_TABLE_OWNER_PID, out int size);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Count(buf, size, Udp4Row);
            for (int i = 0; i < n; i++)
            {
                int o = 4 + i * Udp4Row;
                list.Add(new ConnEntry
                {
                    Protocol = "UDP",
                    IsV6 = false,
                    LocalAddress = V4(buf, o),
                    LocalPort = Port(Marshal.ReadInt32(buf, o + 4)),
                    Pid = Marshal.ReadInt32(buf, o + 8),
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static void ReadUdp6(List<ConnEntry> list)
    {
        IntPtr buf = Fetch(false, AF_INET6, UDP_TABLE_OWNER_PID, out int size);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Count(buf, size, Udp6Row);
            for (int i = 0; i < n; i++)
            {
                int o = 4 + i * Udp6Row;
                uint scope = (uint)Marshal.ReadInt32(buf, o + 16);
                list.Add(new ConnEntry
                {
                    Protocol = "UDP",
                    IsV6 = true,
                    LocalAddress = V6(buf, o, scope),
                    LocalPort = Port(Marshal.ReadInt32(buf, o + 20)),
                    Pid = Marshal.ReadInt32(buf, o + 24),
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

/// <summary>
/// Host-name patterns for Microsoft telemetry / diagnostics and advertising
/// endpoints. "*" matches any run of characters. First match wins, so put the
/// specific patterns before the broad ones. Add more with <see cref="Add"/>.
/// </summary>
public static class ConnFlags
{
    public const string Telemetry = "Telemetry";
    public const string Ads = "Ads";

    public static readonly List<(string pattern, string kind)> Patterns = new()
    {
        // advertising
        ("*.ads.msn.com", Ads),
        ("ads.msn.com", Ads),
        ("*.adnxs.com", Ads),
        ("adnxs.com", Ads),
        // telemetry and diagnostics
        ("functional.events.data.microsoft.com", Telemetry),
        ("self.events.data.microsoft.com", Telemetry),
        ("*.events.data.microsoft.com", Telemetry),
        ("events.data.microsoft.com", Telemetry),
        ("vortex*.data.microsoft.com", Telemetry),
        ("settings-win.data.microsoft.com", Telemetry),
        ("*.telemetry.microsoft.com", Telemetry),
        ("telemetry.microsoft.com", Telemetry),
        ("watson.*microsoft.com", Telemetry),
        ("browser.events.data.msn.com", Telemetry),
        ("*.events.data.msn.com", Telemetry),
        ("*.data.microsoft.com", Telemetry),
    };

    static Regex[] _rx;
    static string[] _kinds;
    static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();

    public static void Add(string pattern, string kind)
    {
        lock (_lock)
        {
            Patterns.Add((pattern, kind));
            _rx = null;
            _cache.Clear();
        }
    }

    /// <summary>"Telemetry", "Ads" or null.</summary>
    public static string Classify(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        lock (_lock)
        {
            if (_cache.TryGetValue(host, out var hit)) return hit;
            if (_rx == null)
            {
                _rx = new Regex[Patterns.Count];
                _kinds = new string[Patterns.Count];
                for (int i = 0; i < Patterns.Count; i++)
                {
                    string p = Patterns[i].pattern.Trim().ToLowerInvariant();
                    _rx[i] = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$",
                                       RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    _kinds[i] = Patterns[i].kind;
                }
            }
            string kind = null;
            for (int i = 0; i < _rx.Length; i++)
                if (_rx[i].IsMatch(host)) { kind = _kinds[i]; break; }
            if (_cache.Count > 20000) _cache.Clear();
            _cache[host] = kind;
            return kind;
        }
    }
}
