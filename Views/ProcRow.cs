using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

/// <summary>
/// Background tints for the "heat" cells on the Processes page: the busier a
/// value, the stronger the accent behind it. Brushes are frozen and rebuilt
/// only when the theme changes.
/// </summary>
public static class ProcHeat
{
    public const int Levels = 8;
    static SolidColorBrush[] _brushes;

    /// <summary>0 = no tint, 1..Levels = increasing tint. Input is a 0..1 load (square-rooted so small loads still show).</summary>
    public static int Level(double load01)
    {
        if (double.IsNaN(load01) || load01 <= 0.003) return 0;
        double s = Math.Sqrt(Math.Min(1.0, load01));
        int lvl = (int)Math.Ceiling(s * Levels);
        return Math.Clamp(lvl, 1, Levels);
    }

    public static Brush For(int level)
    {
        if (level <= 0) return null;
        if (_brushes == null) Build();
        return _brushes[Math.Min(level, Levels)];
    }

    public static void Build()
    {
        var c = Palette.Current.Accent;
        var arr = new SolidColorBrush[Levels + 1];
        for (int i = 1; i <= Levels; i++)
            arr[i] = Palette.Brush(c, (byte)(0x10 + i * 0x0E));
        _brushes = arr;
    }
}

/// <summary>
/// One process on the Processes page. Keyed by pid + start time, so a PID
/// Windows recycles becomes a new row rather than inheriting this one.
/// Updated in place every tick; only values that actually change raise
/// PropertyChanged.
/// </summary>
public sealed class ProcRow : INotifyPropertyChanged
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

    public ProcRow(ProcSample s)
    {
        Sample = s;
        Key = s.Key;
        Pid = s.Pid;
        CreateTime = s.CreateTime;
        ParentPid = s.ParentPid;
        Name = s.Name ?? "";
        PidText = s.Pid.ToString();
        IsSvchost = string.Equals(Name, "svchost.exe", StringComparison.OrdinalIgnoreCase);
        var started = s.Started;
        if (started == DateTime.MinValue) StartedText = Fmt.Na;
        else if (started.Date == DateTime.Today) StartedText = started.ToString("HH:mm:ss");
        else StartedText = started.ToString("dd MMM HH:mm");
    }

    // ---------------------------------------------------------------- identity

    public string Key { get; }
    public int Pid { get; }
    public long CreateTime { get; }
    public int ParentPid { get; }
    public string Name { get; }
    public string PidText { get; }
    public string StartedText { get; }
    public bool IsSvchost { get; }

    /// <summary>The newest sample for this process (used for actions, which re-verify pid + start time).</summary>
    public ProcSample Sample { get; private set; }

    /// <summary>Parent row in the current tree (set while building the tree; null for roots or in flat mode).</summary>
    public ProcRow Parent { get; set; }

    // ----------------------------------------------------------------- details

    public bool DetailsLoaded { get; private set; }
    public string ExePath { get; private set; } = "";
    public string Company { get; private set; } = "";
    public string CommandLine { get; private set; } = "";
    string _baseDescription = "";

    string _user = "";
    public string User { get => _user; private set => Set(ref _user, value, nameof(User)); }

    string _description = "";
    public string DescriptionText { get => _description; private set => Set(ref _description, value, nameof(DescriptionText)); }

    ImageSource _icon;
    public ImageSource Icon { get => _icon; set => Set(ref _icon, value, nameof(Icon)); }

    public void ApplyDetails(ProcDetails d)
    {
        if (d == null) return;
        ExePath = d.Path ?? "";
        Company = d.Company ?? "";
        CommandLine = d.CommandLine ?? "";
        _baseDescription = d.Description ?? "";
        User = d.User ?? "";
        DetailsLoaded = true;
        RefreshDescription();
    }

    /// <summary>Description, with the hosted services appended for svchost.exe.</summary>
    public void RefreshDescription()
    {
        string text = _baseDescription;
        if (IsSvchost)
        {
            string services = ServiceMap.Describe(Pid);
            if (!string.IsNullOrEmpty(services))
                text = string.IsNullOrEmpty(text) ? services : text + ": " + services;
        }
        DescriptionText = text;
    }

    /// <summary>Session 0 or a built-in service account.</summary>
    public bool IsSystem =>
        Sample.SessionId == 0 || Pid <= 4 ||
        string.Equals(_user, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_user, "LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_user, "NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);

    public bool Matches(string f)
    {
        if (string.IsNullOrEmpty(f)) return true;
        return Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || PidText.StartsWith(f, StringComparison.Ordinal)
            || _user.Contains(f, StringComparison.OrdinalIgnoreCase)
            || _description.Contains(f, StringComparison.OrdinalIgnoreCase)
            || CommandLine.Contains(f, StringComparison.OrdinalIgnoreCase)
            || ExePath.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- live values
    // Plain numeric properties are what the view sorts on; the *Text and *Heat
    // properties are what the grid binds to.

    public double Cpu { get; private set; }
    public ulong Mem { get; private set; }
    public double Disk { get; private set; }
    public double Net { get; private set; } = -1;     // -1 = unavailable (sorts last)
    public double Gpu { get; private set; } = -1;
    public double Power { get; private set; } = -1;
    public int Threads { get; private set; }
    public int Handles { get; private set; }

    string _cpuText = "", _memText = "", _diskText = "", _netText = "", _gpuText = "", _powerText = "", _threadsText = "", _handlesText = "";
    public string CpuText => _cpuText;
    public string MemText => _memText;
    public string DiskText => _diskText;
    public string NetText => _netText;
    public string GpuText => _gpuText;
    public string PowerText => _powerText;
    public string ThreadsText => _threadsText;
    public string HandlesText => _handlesText;

    int _cpuLvl, _memLvl, _diskLvl, _netLvl, _gpuLvl;
    public Brush CpuHeat => ProcHeat.For(_cpuLvl);
    public Brush MemHeat => ProcHeat.For(_memLvl);
    public Brush DiskHeat => ProcHeat.For(_diskLvl);
    public Brush NetHeat => ProcHeat.For(_netLvl);
    public Brush GpuHeat => ProcHeat.For(_gpuLvl);

    public void Update(ProcSample s, ulong memTotal)
    {
        Sample = s;

        Cpu = Math.Round(s.Cpu, 1);
        Set(ref _cpuText, Fmt.Pct(Cpu, 1), nameof(CpuText));
        Set(ref _cpuLvl, ProcHeat.Level(Cpu / 100.0), nameof(CpuHeat));

        Mem = s.PrivateWorkingSet;
        Set(ref _memText, Fmt.Bytes(Mem), nameof(MemText));
        Set(ref _memLvl, memTotal > 0 ? ProcHeat.Level(Mem / (double)memTotal * 4.0) : 0, nameof(MemHeat));

        Disk = s.ReadBps + s.WriteBps;
        Set(ref _diskText, Fmt.Rate(Disk), nameof(DiskText));
        Set(ref _diskLvl, ProcHeat.Level(Disk / (50.0 * 1024 * 1024)), nameof(DiskHeat));

        Net = s.NetBps ?? -1;
        Set(ref _netText, Fmt.Rate(s.NetBps), nameof(NetText));
        Set(ref _netLvl, s.NetBps.HasValue ? ProcHeat.Level(s.NetBps.Value / (10.0 * 1024 * 1024)) : 0, nameof(NetHeat));

        Gpu = s.GpuPct ?? -1;
        Set(ref _gpuText, s.GpuPct.HasValue ? Fmt.Pct(s.GpuPct.Value, 1) : Fmt.Na, nameof(GpuText));
        Set(ref _gpuLvl, s.GpuPct.HasValue ? ProcHeat.Level(s.GpuPct.Value / 100.0) : 0, nameof(GpuHeat));

        Power = s.EstWatts ?? -1;
        Set(ref _powerText, Fmt.Watts(s.EstWatts), nameof(PowerText));

        Threads = s.Threads;
        Set(ref _threadsText, s.Threads.ToString("N0"), nameof(ThreadsText));
        Handles = s.Handles;
        Set(ref _handlesText, s.Handles.ToString("N0"), nameof(HandlesText));
    }

    /// <summary>Re-raise the tint properties after a theme change.</summary>
    public void RaiseHeat()
    {
        Raise(nameof(CpuHeat)); Raise(nameof(MemHeat)); Raise(nameof(DiskHeat)); Raise(nameof(NetHeat)); Raise(nameof(GpuHeat));
    }

    // -------------------------------------------------------------------- tree

    public int Depth { get; private set; }
    public bool HasChildren { get; private set; }

    Thickness _indent;
    public Thickness Indent => _indent;

    string _glyph = "";
    public string Glyph => _glyph;

    Visibility _glyphVisibility = Visibility.Collapsed;
    public Visibility GlyphVisibility => _glyphVisibility;

    public void SetTree(int depth, bool treeMode, bool hasChildren, bool collapsed)
    {
        Depth = depth;
        HasChildren = hasChildren;
        Set(ref _indent, new Thickness(depth * 16, 0, 0, 0), nameof(Indent));
        Set(ref _glyph, !treeMode || !hasChildren ? "" : collapsed ? "▸" : "▾", nameof(Glyph));
        Set(ref _glyphVisibility, treeMode ? Visibility.Visible : Visibility.Collapsed, nameof(GlyphVisibility));
    }
}
