using System.Runtime.InteropServices;

namespace Winnow.Core;

/// <summary>
/// A thin wrapper over the Performance Data Helper API, used for the counters
/// Windows only exposes there: disk activity, GPU engines, and effective CPU
/// clock. English counter paths, so it works on any display language.
/// A counter that has not produced a valid reading yet returns null, never 0.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_FMT_NOCAP100 = 0x00008000;
    const uint PDH_MORE_DATA = 0x800007D2;
    const uint ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Explicit)]
    struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
        [FieldOffset(8)] public long largeValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    IntPtr _query;
    readonly Dictionary<string, IntPtr> _counters = new();
    public string LastError { get; private set; }

    public PdhQuery()
    {
        uint rc = PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (rc != ERROR_SUCCESS) { LastError = $"PdhOpenQuery 0x{rc:X8}"; _query = IntPtr.Zero; }
    }

    public bool IsOpen => _query != IntPtr.Zero;

    /// <summary>Add a counter by English path, e.g. \PhysicalDisk(*)\% Idle Time.</summary>
    public bool Add(string path)
    {
        if (_query == IntPtr.Zero) return false;
        if (_counters.ContainsKey(path)) return true;
        uint rc = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out IntPtr c);
        if (rc != ERROR_SUCCESS) { LastError = $"{path}: 0x{rc:X8}"; return false; }
        _counters[path] = c;
        return true;
    }

    public bool Has(string path) => _counters.ContainsKey(path);

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    /// <summary>Single-instance counter value, or null if not available yet.</summary>
    public double? Value(string path)
    {
        if (!_counters.TryGetValue(path, out IntPtr c)) return null;
        uint rc = PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out var v);
        if (rc != ERROR_SUCCESS || v.CStatus > 1) return null; // 0 = valid, 1 = valid new data
        return v.doubleValue;
    }

    /// <summary>All instances of a wildcard counter as name -> value. Empty if not available.</summary>
    public Dictionary<string, double> Instances(string path)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_counters.TryGetValue(path, out IntPtr c)) return result;
        uint size = 0;
        uint rc = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (rc != PDH_MORE_DATA || size == 0) return result;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf);
            if (rc != ERROR_SUCCESS) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W on 64-bit: szName (8) + PDH_FMT_COUNTERVALUE (16) = 24 bytes
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * 24;
                IntPtr namePtr = Marshal.ReadIntPtr(item, 0);
                uint status = (uint)Marshal.ReadInt32(item, 8);
                if (status > 1) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                string name = Marshal.PtrToStringUni(namePtr) ?? "";
                // Duplicate instance names (e.g. two "svchost") get summed.
                result[name] = result.TryGetValue(name, out double prev) ? prev + value : value;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
