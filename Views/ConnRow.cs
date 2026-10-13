using System.ComponentModel;
using System.Windows.Media;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// One row on the Connections page, keyed by protocol + local + remote + pid
/// and updated in place every tick.
/// </summary>
public sealed class ConnRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    public ConnRow(ConnEntry e)
    {
        Key = e.Key;
        Pid = e.Pid;
        PidText = e.Pid.ToString();
        Protocol = e.Protocol + (e.IsV6 ? "v6" : "");
        IsUdp = e.Protocol == "UDP";
        LocalText = e.LocalText;
        RemoteText = e.RemoteText;
        HasRemote = e.HasRemote;
        RemoteLookup = e.HasRemote ? ConnEntry.LookupKey(e.RemoteAddress) : null;
        RemoteRaw = e.HasRemote ? e.RemoteAddress.ToString() : null;
        RemotePort = e.RemotePort;
        LocalPort = e.LocalPort;
        IsLocal = ConnEntry.IsLoopback(e.LocalAddress) || (e.HasRemote && ConnEntry.IsLoopback(e.RemoteAddress));
        LocalSort = Pad(e.LocalAddress?.ToString(), e.LocalPort);
        RemoteSort = e.HasRemote ? Pad(RemoteRaw, e.RemotePort) : "";
    }

    // Addresses sort by their text with the port zero-padded, so :80 sorts before :443.
    static string Pad(string addr, int port) => (addr ?? "") + ":" + port.ToString("00000");

    // ---------------------------------------------------------------- identity
    public string Key { get; }
    public int Pid { get; }
    public string PidText { get; }
    public string Protocol { get; }
    public bool IsUdp { get; }
    public string LocalText { get; }
    public string RemoteText { get; }
    public string LocalSort { get; }
    public string RemoteSort { get; }
    public bool HasRemote { get; }
    /// <summary>Remote address as used for DNS / ETW lookups (IPv4-mapped addresses unwrapped), or null.</summary>
    public string RemoteLookup { get; }
    public string RemoteRaw { get; }
    public int RemotePort { get; }
    public int LocalPort { get; }
    public bool IsLocal { get; }

    // ----------------------------------------------------------------- process
    string _processName = Fmt.Na;
    public string ProcessName { get => _processName; private set => Set(ref _processName, value, nameof(ProcessName)); }

    ImageSource _icon;
    public ImageSource Icon { get => _icon; set => Set(ref _icon, value, nameof(Icon)); }

    public string ExePath { get; private set; } = "";
    public long CreateTime { get; private set; }

    /// <summary>Set the owning process. Returns true when the exe path changed (so the caller fetches a new icon).</summary>
    public bool SetProcess(string name, string path, long createTime)
    {
        ProcessName = string.IsNullOrEmpty(name) ? Fmt.Na : name;
        CreateTime = createTime;
        path ??= "";
        if (path == ExePath) return false;
        ExePath = path;
        return true;
    }

    // ------------------------------------------------------------------- state
    int _stateCode = -1;
    public int StateCode => _stateCode;

    string _state = Fmt.Na;
    public string State { get => _state; private set => Set(ref _state, value, nameof(State)); }

    public void SetState(int code, string name)
    {
        Set(ref _stateCode, code, nameof(StateCode));
        State = name;
    }

    public bool IsListening => !IsUdp && _stateCode == ConnEntry.StateListen;
    public bool IsEstablished => !IsUdp && _stateCode == ConnEntry.StateEstablished;

    // -------------------------------------------------------------------- host
    string _host = Fmt.Na;
    public string Host { get => _host; private set => Set(ref _host, value, nameof(Host)); }

    /// <summary>The bare host name (no "(reverse)" marker), or null.</summary>
    public string HostName { get; private set; }

    string _flag = "";
    public string Flag { get => _flag; private set => Set(ref _flag, value, nameof(Flag)); }
    public bool IsFlagged => _flag.Length > 0;

    public void SetHost(string name, bool reverse)
    {
        HostName = string.IsNullOrEmpty(name) ? null : name;
        Host = HostName == null ? Fmt.Na : reverse ? HostName + " (reverse)" : HostName;
        Flag = ConnFlags.Classify(HostName) ?? "";
    }

    // ------------------------------------------------------------------- bytes
    long _recv = -1, _sent = -1;
    /// <summary>Cumulative bytes seen since Winnow started, or -1 when not measured.</summary>
    public long Recv => _recv;
    public long Sent => _sent;

    string _recvText = Fmt.Na, _sentText = Fmt.Na;
    public string RecvText => _recvText;
    public string SentText => _sentText;

    public void SetBytes(long recv, long sent)
    {
        if (Set(ref _recv, recv, nameof(Recv))) Set(ref _recvText, recv < 0 ? Fmt.Na : Fmt.Bytes(recv), nameof(RecvText));
        if (Set(ref _sent, sent, nameof(Sent))) Set(ref _sentText, sent < 0 ? Fmt.Na : Fmt.Bytes(sent), nameof(SentText));
    }

    // ------------------------------------------------------------------ search
    public bool Matches(string f)
    {
        if (string.IsNullOrEmpty(f)) return true;
        if (f.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(f.AsSpan(4).Trim(), out int pid) && pid == Pid;
        return _processName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || PidText == f
            || LocalText.Contains(f, StringComparison.OrdinalIgnoreCase)
            || RemoteText.Contains(f, StringComparison.OrdinalIgnoreCase)
            || _host.Contains(f, StringComparison.OrdinalIgnoreCase)
            || _state.Contains(f, StringComparison.OrdinalIgnoreCase)
            || _flag.Contains(f, StringComparison.OrdinalIgnoreCase)
            || Protocol.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    public string CopyText =>
        string.Join("\t", ProcessName, PidText, Protocol, LocalText, RemoteText, Host, State, RecvText, SentText, Flag);
}
