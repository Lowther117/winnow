using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winnow.Core;

/// <summary>First line of a .winnowtrace file.</summary>
public sealed class TraceHeader
{
    [JsonPropertyName("format")] public string Format { get; set; } = "winnowtrace";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("created")] public DateTime Created { get; set; }
    [JsonPropertyName("intervalMs")] public int IntervalMs { get; set; }
    [JsonPropertyName("samples")] public int Samples { get; set; }
    [JsonPropertyName("app")] public string App { get; set; } = "";
}

/// <summary>A trace read back from disk.</summary>
public sealed class TraceData
{
    public TraceHeader Header = new();
    public List<Snapshot> Samples = new();
    public int SkippedLines;      // lines that were not valid snapshots
    public bool Truncated;        // the file ended early (e.g. copied while being written)
    public string Path = "";
}

/// <summary>
/// The Flight Recorder's file format: gzip-compressed JSON Lines. Line 1 is a
/// TraceHeader, every following line is one Snapshot. Snapshots are field-based,
/// so the serialiser runs with IncludeFields; computed properties (ProcSample.Key,
/// ProcSample.Started, SensorReading.Unit) are skipped on write and ignored on read.
/// </summary>
public static class TraceFile
{
    public const string Extension = ".winnowtrace";
    public const string Filter = "Winnow trace|*.winnowtrace";
    const string FormatName = "winnowtrace";

    static readonly JsonSerializerOptions Opts = new()
    {
        IncludeFields = true,
        IgnoreReadOnlyProperties = true,
        IgnoreReadOnlyFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
    };

    /// <summary>Write samples to a .winnowtrace file. Run off the UI thread.</summary>
    public static void Save(string path, List<Snapshot> samples)
    {
        var header = new TraceHeader
        {
            Machine = Environment.MachineName,
            Os = WinVer.Summary,
            Created = DateTime.Now,
            IntervalMs = Settings.Current.RefreshMs,
            Samples = samples.Count(s => s != null),
            App = "Winnow " + (typeof(TraceFile).Assembly.GetName().Version?.ToString(3) ?? ""),
        };

        string tmp = path + ".part";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                w.WriteLine(JsonSerializer.Serialize(header, Opts));
                foreach (var s in samples)
                {
                    if (s == null) continue;
                    w.WriteLine(JsonSerializer.Serialize(ForFile(s), Opts));
                }
            }
            File.Move(tmp, path, true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// A copy for the file: the same snapshot with only the sensor readings a replay
    /// uses (temperatures, power, fans). The in-memory snapshot is never modified.
    /// </summary>
    static Snapshot ForFile(Snapshot s)
    {
        var c = s.Trimmed(1_000_000);   // a shallow copy that keeps every process the trimmed snapshot has
        var sensors = s.Sensors ?? new List<SensorReading>();
        c.Sensors = sensors.Where(r => r != null && (r.Type == "Temperature" || r.Type == "Power" || r.Type == "Fan")).ToList();
        return c;
    }

    /// <summary>Read a .winnowtrace file (gzip or plain JSON Lines). Throws InvalidDataException if it is not one.</summary>
    public static TraceData Load(string path)
    {
        var data = new TraceData { Path = path };
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int b1 = fs.ReadByte(), b2 = fs.ReadByte();
        fs.Position = 0;
        Stream input = b1 == 0x1F && b2 == 0x8B ? (Stream)new GZipStream(fs, CompressionMode.Decompress) : fs;
        using var r = new StreamReader(input, Encoding.UTF8);

        string first = r.ReadLine();
        if (string.IsNullOrWhiteSpace(first)) throw new InvalidDataException("The file is empty.");
        TraceHeader h = null;
        try { h = JsonSerializer.Deserialize<TraceHeader>(first, Opts); }
        catch (JsonException) { h = null; }
        if (h == null || !string.Equals(h.Format, FormatName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This is not a Winnow trace (.winnowtrace) file.");
        data.Header = h;

        try
        {
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                try
                {
                    var s = JsonSerializer.Deserialize<Snapshot>(line, Opts);
                    if (s == null) { data.SkippedLines++; continue; }
                    s.Disks ??= new List<DiskSample>();
                    s.Nets ??= new List<NetSample>();
                    s.Processes ??= new List<ProcSample>();
                    s.Sensors ??= new List<SensorReading>();
                    s.CpuPerCore ??= System.Array.Empty<double>();
                    s.Processes.RemoveAll(p => p == null);   // the views sort these without null checks
                    s.Sensors.RemoveAll(x => x == null);
                    data.Samples.Add(s);
                }
                catch (JsonException) { data.SkippedLines++; }
            }
        }
        catch (InvalidDataException) { data.Truncated = true; }   // gzip stream cut short
        catch (IOException) { data.Truncated = true; }

        data.Samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        return data;
    }

    /// <summary>System metrics per sample as CSV (invariant culture, empty cell = no reading).</summary>
    public static void ExportCsv(string path, List<Snapshot> samples)
    {
        var ci = CultureInfo.InvariantCulture;
        string N(double? v) => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) ? v.Value.ToString("0.###", ci) : "";
        var sb = new StringBuilder();
        sb.AppendLine("time,cpu_pct,mem_pct,mem_used_bytes,disk_active_pct,disk_read_bps,disk_write_bps,net_recv_bps,net_send_bps,gpu_pct,cpu_temp_c,gpu_temp_c,cpu_w,gpu_w");
        foreach (var s in samples)
        {
            if (s == null) continue;
            sb.Append(s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", ci)).Append(',')
              .Append(N(s.CpuTotal)).Append(',')
              .Append(N(s.MemLoadPct)).Append(',')
              .Append(N(s.MemUsed)).Append(',')
              .Append(N(s.DiskActivePct)).Append(',')
              .Append(N(s.DiskReadBps)).Append(',')
              .Append(N(s.DiskWriteBps)).Append(',')
              .Append(N(s.NetRecvBps)).Append(',')
              .Append(N(s.NetSendBps)).Append(',')
              .Append(N(s.GpuPct)).Append(',')
              .Append(N(s.CpuTempC)).Append(',')
              .Append(N(s.GpuTempC)).Append(',')
              .Append(N(s.CpuWatts)).Append(',')
              .Append(N(s.GpuWatts)).AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
