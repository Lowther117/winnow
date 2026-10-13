using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Winnow.Controls;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// The home page: "why is my computer slow right now?" One plain-English
/// verdict naming the busiest subsystem and the process responsible, a dial
/// per subsystem with its top three processes, the last minute as small
/// graphs, energy, and a privacy glance. Every number is a measurement; a
/// reading Winnow could not take shows "—", never 0.
/// </summary>
public sealed class SummaryView : UserControl, IView
{
    const int GraphSamples = 60;

    // When a subsystem counts as "under pressure", and when that becomes "bad".
    const double CpuHigh = 80, CpuBad = 95;
    const double MemHigh = 85, MemBad = 95;
    const double DiskHigh = 80, DiskBad = 95;
    const double GpuHigh = 85, GpuBad = 97;
    const double CpuHotC = 90, GpuHotspotHotC = 95, GpuCoreHotC = 90;

    readonly Border _verdictCard;
    readonly TextBlock _verdict = new() { FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _verdictSub = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock _verdictLine = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    readonly Dial _dCpu = MakeDial(CpuHigh / 100, CpuBad / 100, 120);
    readonly Dial _dMem = MakeDial(MemHigh / 100, MemBad / 100, 120);
    readonly Dial _dDisk = MakeDial(DiskHigh / 100, DiskBad / 100, 120);
    readonly Dial _dGpu = MakeDial(GpuHigh / 100, GpuBad / 100, 120);
    readonly Dial _dCpuT = MakeDial(0.8, 0.92, 112);   // scale 30-100 °C
    readonly Dial _dGpuT = MakeDial(0.8, 0.92, 112);

    readonly SummaryTopList _topCpu = new(), _topMem = new(), _topDisk = new(), _topNet = new(), _topGpu = new(), _topWatts = new();
    readonly TextBlock _netRecv, _netSend, _thermNote, _historyLabel;

    readonly Graph _gCpu = MakeGraph("Graph1", false, 1);
    readonly Graph _gMem = MakeGraph("Graph2", false, 1);
    readonly Graph _gDisk = MakeGraph("Graph3", true, 256 * 1024);
    readonly Graph _gNet = MakeGraph("Graph4", true, 16 * 1024);
    readonly Graph _gPow = MakeGraph("Graph2", true, 10);
    readonly TextBlock _vCpu = ValueText(), _vMem = ValueText(), _vDisk = ValueText(), _vNet = ValueText(), _vPow = ValueText();

    readonly TextBlock _eCpu = ValueText(), _eGpu = ValueText(), _eTotal = ValueText();
    readonly StackPanel _eBody = new(), _eMissing = new();

    readonly TextBlock _pCounts = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _pDiag = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _pTel = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    readonly TextBlock _sUptime = ValueText(), _sProcs = ValueText(), _sThreads = ValueText(), _sHandles = ValueText();

    DateTime _cutoff = DateTime.MinValue;   // newest snapshot already in the graphs
    int _privacyVersion;

    public SummaryView()
    {
        // ---- verdict
        var vs = new StackPanel();
        vs.Children.Add(Ui.Text("Right now", "Label"));
        vs.Children.Add(_verdict);
        vs.Children.Add(_verdictSub);
        vs.Children.Add(_verdictLine);
        _verdictCard = Ui.Card(vs);
        _verdictCard.BorderThickness = new Thickness(4, 1, 1, 1);
        _verdictSub.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _verdictLine.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        _verdictLine.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");

        // ---- subsystem cards
        _netRecv = Ui.Text(Fmt.Na, "Value", 20);
        _netSend = Ui.Text(Fmt.Na, "Value", 20);
        var netSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        netSp.Children.Add(Ui.Text("Receive", "Label"));
        netSp.Children.Add(_netRecv);
        var sendLabel = Ui.Text("Send", "Label");
        sendLabel.Margin = new Thickness(0, 6, 0, 0);
        netSp.Children.Add(sendLabel);
        netSp.Children.Add(_netSend);
        var netGauge = new Grid { Height = 120 };
        netGauge.Children.Add(netSp);

        var temps = new UniformGrid { Columns = 2, Height = 120 };
        temps.Children.Add(_dCpuT);
        temps.Children.Add(_dGpuT);
        _thermNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        _thermNote.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var subsystems = new WrapPanel();
        subsystems.Children.Add(SubCard("CPU", _dCpu, "Top by CPU", _topCpu));
        subsystems.Children.Add(SubCard("Memory", _dMem, "Top by memory", _topMem));
        subsystems.Children.Add(SubCard("Disk", _dDisk, "Top by disk I/O", _topDisk));
        subsystems.Children.Add(SubCard("GPU", _dGpu, "Top by GPU", _topGpu));
        subsystems.Children.Add(SubCard("Network", netGauge, "Top by network", _topNet));
        subsystems.Children.Add(SubCard("Temperature", temps, null, _thermNote));

        // ---- history strip
        _historyLabel = Ui.Text("History", "Label");
        _historyLabel.Margin = new Thickness(0, 4, 0, 6);
        var graphs = new WrapPanel();
        graphs.Children.Add(GraphCard("CPU", _gCpu, _vCpu));
        graphs.Children.Add(GraphCard("Memory", _gMem, _vMem));
        graphs.Children.Add(GraphCard("Disk throughput", _gDisk, _vDisk));
        graphs.Children.Add(GraphCard("Network", _gNet, _vNet));
        graphs.Children.Add(GraphCard("Power (CPU + GPU)", _gPow, _vPow));

        // ---- energy
        var energy = new StackPanel();
        energy.Children.Add(Ui.Text("Energy", "H2"));
        _eBody.Children.Add(KeyValue("CPU package", _eCpu));
        _eBody.Children.Add(KeyValue("GPU", _eGpu));
        _eBody.Children.Add(KeyValue("Total measured", _eTotal));
        var estLabel = Ui.Text("Top by estimated power (est.)", "Label");
        estLabel.Margin = new Thickness(0, 8, 0, 0);
        _eBody.Children.Add(estLabel);
        _eBody.Children.Add(_topWatts);
        var missing = Ui.Text("Power sensors unavailable — see Settings › Hardware sensors", "Sub");
        missing.TextWrapping = TextWrapping.Wrap;
        _eMissing.Children.Add(missing);
        var openSettings = Ui.Button("Open Settings", (_, _) => Nav.Go("settings"));
        openSettings.HorizontalAlignment = HorizontalAlignment.Left;
        openSettings.Margin = new Thickness(0, 8, 0, 0);
        _eMissing.Children.Add(openSettings);
        _eMissing.Visibility = Visibility.Collapsed;
        energy.Children.Add(_eBody);
        energy.Children.Add(_eMissing);

        // ---- privacy
        var privacy = new StackPanel();
        privacy.Children.Add(Ui.Text("Privacy at a glance", "H2"));
        privacy.Children.Add(_pCounts);
        privacy.Children.Add(_pDiag);
        privacy.Children.Add(_pTel);
        _pTel.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var openPrivacy = Ui.Button("Open Privacy", (_, _) => Nav.Go("privacy"));
        openPrivacy.HorizontalAlignment = HorizontalAlignment.Left;
        openPrivacy.Margin = new Thickness(0, 10, 0, 0);
        privacy.Children.Add(openPrivacy);

        // ---- system
        var sysCard = new StackPanel();
        sysCard.Children.Add(Ui.Text("System", "H2"));
        sysCard.Children.Add(KeyValue("Up time", _sUptime));
        sysCard.Children.Add(KeyValue("Processes", _sProcs));
        sysCard.Children.Add(KeyValue("Threads", _sThreads));
        sysCard.Children.Add(KeyValue("Handles", _sHandles));

        var info = new WrapPanel();
        info.Children.Add(Ui.Card(energy));
        info.Children.Add(Ui.Card(privacy));
        info.Children.Add(Ui.Card(sysCard));

        // ---- layout
        var content = new StackPanel();
        content.Children.Add(_verdictCard);
        content.Children.Add(subsystems);
        content.Children.Add(_historyLabel);
        content.Children.Add(graphs);
        content.Children.Add(info);
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Reflow(scroll, subsystems, 200);
        Reflow(scroll, graphs, 170);
        Reflow(scroll, info, 270);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Ui.Header("Summary", "What your PC is doing right now, and which process is responsible."));
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        SetVerdict("Measuring…", "", "", "Dim");
    }

    // ================================================================ builders

    static Dial MakeDial(double warnAt, double badAt, double height)
    {
        // Width cleared so the dial fills its card; it draws at min(width, height), centred.
        return new Dial { Width = double.NaN, Height = height, WarnAt = warnAt, BadAt = badAt, LevelColors = true };
    }

    static Graph MakeGraph(string colorKey, bool autoMax, double floor)
    {
        var g = new Graph { Capacity = GraphSamples, Height = 54, AutoMax = autoMax, AutoFloor = floor, GridLines = 2 };
        g.AddSeries(colorKey, true, 1.4);
        return g;
    }

    static TextBlock ValueText() => Ui.Text(Fmt.Na, "ValueSmall");

    static Border SubCard(string title, UIElement gauge, string listTitle, UIElement list)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(title, "Label"));
        sp.Children.Add(gauge);
        if (listTitle != null)
        {
            var lt = Ui.Text(listTitle, "Label");
            lt.Margin = new Thickness(0, 6, 0, 2);
            sp.Children.Add(lt);
        }
        sp.Children.Add(list);
        return Ui.Card(sp);
    }

    static Border GraphCard(string label, Graph g, TextBlock value)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);
        var l = Ui.Text(label, "Label");
        l.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(l);
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(g);
        return Ui.Card(sp);
    }

    static DockPanel KeyValue(string label, TextBlock value)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(value, Dock.Right);
        dp.Children.Add(value);
        var l = Ui.Text(label);
        l.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        dp.Children.Add(l);
        return dp;
    }

    /// <summary>
    /// Give the cards in a WrapPanel equal widths that fill the row, from minWidth up.
    /// Widths come from the ScrollViewer's own width minus a fixed allowance, so the
    /// scrollbar appearing or disappearing cannot make the layout oscillate.
    /// </summary>
    static void Reflow(ScrollViewer sv, WrapPanel wp, double minWidth)
    {
        void Apply()
        {
            double avail = sv.ActualWidth - 14;
            int n = wp.Children.Count;
            if (avail < 100 || n == 0) return;
            int cols = Math.Clamp((int)(avail / (minWidth + 10)), 1, n);
            int rows = (n + cols - 1) / cols;
            cols = (n + rows - 1) / rows;           // balance: 3+3 rather than 5+1
            double w = Math.Floor(avail / cols) - 10;  // Card margin is 10 on the right
            foreach (UIElement c in wp.Children)
                if (c is FrameworkElement fe && (double.IsNaN(fe.Width) || Math.Abs(fe.Width - w) > 0.5)) fe.Width = w;
        }
        sv.SizeChanged += (_, e) => { if (e.WidthChanged) Apply(); };
    }

    // ================================================================ IView

    public void OnShown()
    {
        _historyLabel.Text = $"History · last {GraphSamples * Settings.Current.RefreshMs / 1000.0:0} seconds";
        SeedGraphs();
        var s = Sampler.Latest;
        if (s != null) UpdateReadouts(s);
        Sampler.Tick -= OnTick;
        Sampler.Tick += OnTick;
        Journal.Changed -= OnJournalChanged;
        Journal.Changed += OnJournalChanged;
        RefreshPrivacy();
    }

    public void OnHidden()
    {
        Sampler.Tick -= OnTick;
        Journal.Changed -= OnJournalChanged;
    }

    void OnTick(Snapshot s)
    {
        if (s.Time > _cutoff)
        {
            _cutoff = s.Time;
            _gCpu.Push(GCpu(s));
            _gMem.Push(GMem(s));
            _gDisk.Push(GDisk(s));
            _gNet.Push(GNet(s));
            _gPow.Push(GPow(s));
        }
        UpdateReadouts(s);
    }

    void SeedGraphs()
    {
        var all = Sampler.History.ToList();
        var hist = all.Count > GraphSamples ? all.GetRange(all.Count - GraphSamples, GraphSamples) : all;
        hist = hist.Where(h => h != null).ToList();
        _cutoff = hist.Count > 0 ? hist[^1].Time : DateTime.MinValue;
        _gCpu.Seed(hist.Select(h => new[] { GCpu(h) }).ToList());
        _gMem.Seed(hist.Select(h => new[] { GMem(h) }).ToList());
        _gDisk.Seed(hist.Select(h => new[] { GDisk(h) }).ToList());
        _gNet.Seed(hist.Select(h => new[] { GNet(h) }).ToList());
        _gPow.Seed(hist.Select(h => new[] { GPow(h) }).ToList());
    }

    static double GCpu(Snapshot h) => h.CpuTotal;
    static double GMem(Snapshot h) => h.MemTotal > 0 ? h.MemLoadPct : double.NaN;
    static double GDisk(Snapshot h) => h.Disks.Count > 0 ? h.DiskReadBps + h.DiskWriteBps : double.NaN;
    static double GNet(Snapshot h) => h.Nets.Count > 0 ? h.NetRecvBps + h.NetSendBps : double.NaN;
    static double GPow(Snapshot h) => h.CpuWatts.HasValue || h.GpuWatts.HasValue ? (h.CpuWatts ?? 0) + (h.GpuWatts ?? 0) : double.NaN;

    // ================================================================ readouts

    void UpdateReadouts(Snapshot s)
    {
        UpdateVerdict(s);

        bool memOk = s.MemTotal > 0;
        var busy = BusiestDisk(s);

        // dials
        _dCpu.Set(s.CpuTotal / 100.0, Fmt.Pct(s.CpuTotal), s.CpuMhz.HasValue ? Fmt.Mhz(s.CpuMhz) : "utilisation");
        _dMem.Set(memOk ? s.MemLoadPct / 100.0 : double.NaN, memOk ? Fmt.Pct(s.MemLoadPct) : Fmt.Na,
                  memOk ? Fmt.Bytes(s.MemUsed) + " in use" : "unavailable");
        _dDisk.Set(busy?.ActivePct is double a ? a / 100.0 : double.NaN, Fmt.Pct(busy?.ActivePct),
                   busy != null ? DiskLabel(busy) + " active" : "unavailable");
        _dGpu.Set(s.GpuPct.HasValue ? s.GpuPct.Value / 100.0 : double.NaN, Fmt.Pct(s.GpuPct),
                  s.GpuMemUsed.HasValue ? Fmt.Bytes(s.GpuMemUsed) + " VRAM" : s.GpuPct.HasValue ? "busiest engine" : "unavailable");
        _dCpuT.Set(TempScale(s.CpuTempC), Fmt.Temp(s.CpuTempC), "CPU");
        _dGpuT.Set(TempScale(s.GpuTempC), Fmt.Temp(s.GpuTempC), "GPU");

        // top processes per subsystem
        _topCpu.Show(Top(s, p => p.Cpu, p => Fmt.Pct(p.Cpu, 1), 0.05), "No process is using measurable CPU time.");
        _topMem.Show(Top(s, p => p.PrivateWorkingSet, p => Fmt.Bytes(p.PrivateWorkingSet), 0), "No process memory readings.");
        _topDisk.Show(Top(s, p => p.ReadBps + p.WriteBps, p => Fmt.Rate(p.ReadBps + p.WriteBps), 0), "No process is doing disk I/O.");
        if (s.PerProcessNetAvailable)
            _topNet.Show(Top(s, p => p.NetBps ?? 0, p => Fmt.Bits(p.NetBps), 0), "No process is using the network.");
        else
            _topNet.Note(Elevation.IsAdmin ? "Per-process network unavailable: " + NetEtw.Status : "Per-process network needs admin.");
        if (s.PerProcessGpuAvailable)
            _topGpu.Show(Top(s, p => p.GpuPct ?? 0, p => Fmt.Pct(p.GpuPct), 0.05), "No process is using the GPU.");
        else
            _topGpu.Note("Per-process GPU counters are unavailable.");

        // network gauge
        bool netOk = s.Nets.Count > 0;
        _netRecv.Text = netOk ? Fmt.Bits(s.NetRecvBps) : Fmt.Na;
        _netSend.Text = netOk ? Fmt.Bits(s.NetSendBps) : Fmt.Na;

        // temperature note
        var notes = new List<string>();
        if (s.GpuHotspotC.HasValue) notes.Add("GPU hot spot " + Fmt.Temp(s.GpuHotspotC));
        SensorReading hottest = null;
        foreach (var r in s.Sensors)
            if (r.Type == "Temperature" && r.Value.HasValue && (hottest == null || r.Value.Value > hottest.Value.Value)) hottest = r;
        if (hottest != null) notes.Add($"Hottest sensor: {hottest.Name} ({hottest.Hardware}) {Fmt.Sensor(hottest)}");
        if (s.Sensors.Count == 0) notes.Add("No sensor readings. Sensors: " + Sensors.Status);
        _thermNote.Text = string.Join("\n", notes);

        // history values
        _vCpu.Text = Fmt.Pct(s.CpuTotal);
        _vMem.Text = memOk ? Fmt.Pct(s.MemLoadPct) : Fmt.Na;
        _vDisk.Text = s.Disks.Count > 0 ? Fmt.Rate(s.DiskReadBps + s.DiskWriteBps) : Fmt.Na;
        _vNet.Text = netOk ? Fmt.Bits(s.NetRecvBps + s.NetSendBps) : Fmt.Na;
        double pow = GPow(s);
        _vPow.Text = double.IsNaN(pow) ? Fmt.Na : Fmt.Watts(pow);

        // energy
        bool power = s.CpuWatts.HasValue || s.GpuWatts.HasValue;
        _eBody.Visibility = power ? Visibility.Visible : Visibility.Collapsed;
        _eMissing.Visibility = power ? Visibility.Collapsed : Visibility.Visible;
        if (power)
        {
            _eCpu.Text = Fmt.Watts(s.CpuWatts);
            _eGpu.Text = Fmt.Watts(s.GpuWatts);
            _eTotal.Text = Fmt.Watts(pow) + (s.CpuWatts.HasValue && s.GpuWatts.HasValue ? "" : s.CpuWatts.HasValue ? " (CPU only)" : " (GPU only)");
            _topWatts.Show(Top(s, p => p.EstWatts ?? 0, p => Fmt.Watts(p.EstWatts) + " est.", 0.05), "No process has a measurable share.");
        }

        // system
        _sUptime.Text = Fmt.Duration(s.Uptime);
        _sProcs.Text = s.ProcessCount.ToString("N0");
        _sThreads.Text = s.ThreadCount.ToString("N0");
        _sHandles.Text = s.HandleCount.ToString("N0");
    }

    static double TempScale(double? c) => c.HasValue ? Math.Clamp((c.Value - 30) / 70.0, 0, 1) : double.NaN;

    /// <summary>Top three processes by a measure, above a floor. Winnow itself is left out unless Settings says otherwise.</summary>
    static List<(ProcSample p, string value)> Top(Snapshot s, Func<ProcSample, double> key, Func<ProcSample, string> fmt, double floor)
    {
        bool self = Settings.Current.ShowSelf;
        int me = Environment.ProcessId;
        return s.Processes.Where(p => (self || p.Pid != me) && key(p) > floor)
                          .OrderByDescending(key)
                          .Take(3)
                          .Select(p => (p, fmt(p)))
                          .ToList();
    }

    // ================================================================ verdict

    sealed class SummaryIssue
    {
        public double Score;   // how far past its threshold; thermals rank first
        public int Level;      // 1 = warn, 2 = bad
        public string Short = "", Text = "";
    }

    void UpdateVerdict(Snapshot s)
    {
        var topCpu = TopBy(s, p => p.Cpu);
        var topMem = TopBy(s, p => p.PrivateWorkingSet);
        var topIo = TopBy(s, p => p.ReadBps + p.WriteBps);
        var topGpu = s.PerProcessGpuAvailable ? TopBy(s, p => p.GpuPct ?? 0) : null;
        var busy = BusiestDisk(s);
        bool memOk = s.MemTotal > 0;

        var issues = new List<SummaryIssue>();

        if (s.CpuTotal > CpuHigh)
            issues.Add(new SummaryIssue
            {
                Score = s.CpuTotal / CpuHigh, Level = s.CpuTotal >= CpuBad ? 2 : 1, Short = "CPU " + Fmt.Pct(s.CpuTotal),
                Text = topCpu != null
                    ? $"CPU is the bottleneck: {Who(topCpu)} is using {Fmt.Pct(topCpu.Cpu)} of the CPU"
                    : $"CPU is the bottleneck at {Fmt.Pct(s.CpuTotal)}",
            });

        if (memOk && s.MemLoadPct > MemHigh)
            issues.Add(new SummaryIssue
            {
                Score = s.MemLoadPct / MemHigh, Level = s.MemLoadPct >= MemBad ? 2 : 1, Short = "memory " + Fmt.Pct(s.MemLoadPct),
                Text = topMem != null
                    ? $"Memory is the bottleneck: {Who(topMem)} is using {Fmt.Bytes(topMem.PrivateWorkingSet)} of {Fmt.Bytes(s.MemTotal)}"
                    : $"Memory is the bottleneck at {Fmt.Pct(s.MemLoadPct)}",
            });

        if (busy != null && busy.ActivePct.HasValue && busy.ActivePct.Value > DiskHigh)
        {
            double act = busy.ActivePct.Value;
            double io = topIo != null ? topIo.ReadBps + topIo.WriteBps : 0;
            issues.Add(new SummaryIssue
            {
                Score = act / DiskHigh, Level = act >= DiskBad ? 2 : 1, Short = DiskLabel(busy) + " " + Fmt.Pct(act) + " active",
                Text = io > 0
                    ? $"{DiskLabel(busy)} is the bottleneck ({Fmt.Pct(act)} active): {Who(topIo)} is doing the most I/O ({Fmt.Rate(io)})"
                    : $"{DiskLabel(busy)} is the bottleneck ({Fmt.Pct(act)} active)",
            });
        }

        if (s.GpuPct.HasValue && s.GpuPct.Value > GpuHigh)
            issues.Add(new SummaryIssue
            {
                Score = s.GpuPct.Value / GpuHigh, Level = s.GpuPct.Value >= GpuBad ? 2 : 1, Short = "GPU " + Fmt.Pct(s.GpuPct),
                Text = topGpu != null && (topGpu.GpuPct ?? 0) > 0
                    ? $"GPU is the bottleneck: {Who(topGpu)} is using {Fmt.Pct(topGpu.GpuPct)} of the GPU"
                    : $"GPU is the bottleneck at {Fmt.Pct(s.GpuPct)}",
            });

        // Heat throttles everything else, so it outranks load.
        if (s.CpuTempC.HasValue && s.CpuTempC.Value > CpuHotC)
            issues.Add(new SummaryIssue
            {
                Score = 2 + s.CpuTempC.Value / CpuHotC, Level = 2, Short = "CPU " + Fmt.Temp(s.CpuTempC),
                Text = $"The CPU is running hot at {Fmt.Temp(s.CpuTempC)}" +
                       (topCpu != null ? $": {Who(topCpu)} is the biggest CPU load ({Fmt.Pct(topCpu.Cpu)})" : ""),
            });

        // GPU hot spot when the card reports one; otherwise fall back to the core temperature.
        double? gpuHeat = s.GpuHotspotC ?? s.GpuTempC;
        double gpuLimit = s.GpuHotspotC.HasValue ? GpuHotspotHotC : GpuCoreHotC;
        if (gpuHeat.HasValue && gpuHeat.Value > gpuLimit)
        {
            string what = s.GpuHotspotC.HasValue ? "GPU hot spot" : "GPU";
            issues.Add(new SummaryIssue
            {
                Score = 2 + gpuHeat.Value / gpuLimit, Level = 2, Short = what + " " + Fmt.Temp(gpuHeat),
                Text = $"The {what} is running hot at {Fmt.Temp(gpuHeat)}" +
                       (topGpu != null && (topGpu.GpuPct ?? 0) > 0 ? $": {Who(topGpu)} is the biggest GPU load ({Fmt.Pct(topGpu.GpuPct)})" : ""),
            });
        }

        string line = $"CPU {Fmt.Pct(s.CpuTotal)} · Memory {(memOk ? Fmt.Pct(s.MemLoadPct) : Fmt.Na)} · " +
                      $"Disk {Fmt.Pct(s.DiskActivePct)} · GPU {Fmt.Pct(s.GpuPct)} · " +
                      $"CPU {Fmt.Temp(s.CpuTempC)} · GPU {Fmt.Temp(s.GpuTempC)}";

        if (issues.Count > 0)
        {
            var ordered = issues.OrderByDescending(i => i.Score).ToList();
            var top = ordered[0];
            var extra = new List<string>();
            if (ordered.Count > 1) extra.Add("Also under pressure: " + string.Join(", ", ordered.Skip(1).Select(i => i.Short)) + ".");
            if (top.Short.StartsWith("CPU ") && s.CpuTotal > 0 && s.CpuKernel > s.CpuTotal / 2)
                extra.Add($"Most of the CPU time ({Fmt.Pct(s.CpuKernel)}) is in the kernel: drivers, interrupts or system services.");
            int level = ordered.Max(i => i.Level);
            SetVerdict(top.Text, string.Join(" ", extra), line, level >= 2 ? "Bad" : "Warn");
            return;
        }

        // Nothing past a threshold: still name the busiest subsystem and who is behind it.
        var cands = new List<(double ratio, string text)>
        {
            (s.CpuTotal / CpuHigh, $"CPU at {Fmt.Pct(s.CpuTotal)}" + (topCpu != null && topCpu.Cpu >= 0.05 ? $" — {Who(topCpu)} is the top process ({Fmt.Pct(topCpu.Cpu, 1)})" : "")),
        };
        if (memOk)
            cands.Add((s.MemLoadPct / MemHigh, $"memory at {Fmt.Pct(s.MemLoadPct)}" + (topMem != null ? $" — {Who(topMem)} holds the most ({Fmt.Bytes(topMem.PrivateWorkingSet)})" : "")));
        if (busy != null && busy.ActivePct.HasValue)
            cands.Add((busy.ActivePct.Value / DiskHigh, $"{DiskLabel(busy)} at {Fmt.Pct(busy.ActivePct)} active" +
                       (topIo != null && topIo.ReadBps + topIo.WriteBps > 0 ? $" — {Who(topIo)} is doing the most I/O ({Fmt.Rate(topIo.ReadBps + topIo.WriteBps)})" : "")));
        if (s.GpuPct.HasValue)
            cands.Add((s.GpuPct.Value / GpuHigh, $"GPU at {Fmt.Pct(s.GpuPct)}" +
                       (topGpu != null && (topGpu.GpuPct ?? 0) > 0 ? $" — {Who(topGpu)} is the top user ({Fmt.Pct(topGpu.GpuPct)})" : "")));
        var busiest = cands.OrderByDescending(c => c.ratio).First();
        SetVerdict("Nothing is under pressure", "Busiest: " + busiest.text + ".", line, "Good");
    }

    void SetVerdict(string text, string sub, string line, string colorKey)
    {
        _verdict.Text = text;
        _verdictSub.Text = sub;
        _verdictSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        _verdictLine.Text = line;
        _verdictLine.Visibility = string.IsNullOrEmpty(line) ? Visibility.Collapsed : Visibility.Visible;
        _verdict.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        _verdictCard.SetResourceReference(Border.BorderBrushProperty, colorKey);
    }

    static ProcSample TopBy(Snapshot s, Func<ProcSample, double> f)
    {
        ProcSample best = null;
        double bv = double.MinValue;
        foreach (var p in s.Processes)
        {
            double v = f(p);
            if (v > bv) { bv = v; best = p; }
        }
        return best;
    }

    static DiskSample BusiestDisk(Snapshot s)
    {
        DiskSample best = null;
        foreach (var d in s.Disks)
            if (d.ActivePct.HasValue && (best == null || d.ActivePct.Value > best.ActivePct.Value)) best = d;
        return best;
    }

    static string DiskLabel(DiskSample d) =>
        "Disk " + d.Index + (string.IsNullOrWhiteSpace(d.Letters) ? "" : " (" + d.Letters.Trim() + ")");

    /// <summary>The process name, plus the services inside it when it is a svchost.</summary>
    internal static string Who(ProcSample p)
    {
        if (p == null) return "an unknown process";
        if (p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            string d = ServiceMap.Describe(p.Pid);
            if (!string.IsNullOrEmpty(d)) return p.Name + " (" + d + ")";
        }
        return p.Name;
    }

    // ================================================================ privacy

    sealed class SummaryPrivacyInfo
    {
        public int Privacy, Startup, Services, Other;
        public string DiagTrack;
        public object Telemetry;
    }

    void OnJournalChanged() => Dispatcher.BeginInvoke(new Action(RefreshPrivacy));

    /// <summary>Cheap: one journal copy and two registry reads, off the UI thread. Runs on show and on journal changes only.</summary>
    async void RefreshPrivacy()
    {
        int version = ++_privacyVersion;
        try
        {
            var r = await Task.Run(() =>
            {
                var info = new SummaryPrivacyInfo();
                foreach (var e in Journal.Entries)
                {
                    if (e.Reverted || e.Kind == "Note") continue;
                    string g = e.Group ?? "";
                    if (g.StartsWith("privacy.", StringComparison.OrdinalIgnoreCase)) info.Privacy++;
                    else if (g.StartsWith("startup.", StringComparison.OrdinalIgnoreCase)) info.Startup++;
                    else if (g.StartsWith("services.", StringComparison.OrdinalIgnoreCase)) info.Services++;
                    else info.Other++;
                }
                info.DiagTrack = Change.GetServiceStart("DiagTrack");
                info.Telemetry = Change.GetRegistry("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry");
                return info;
            });
            if (version != _privacyVersion) return;

            int total = r.Privacy + r.Startup + r.Services + r.Other;
            _pCounts.Text = total == 0
                ? "No Winnow changes are in effect."
                : $"Winnow changes in effect: {total}\nPrivacy {r.Privacy} · Startup {r.Startup} · Services {r.Services}" +
                  (r.Other > 0 ? $" · Other {r.Other}" : "");

            _pDiag.Text = "Telemetry service (DiagTrack): " + (r.DiagTrack == null ? "not present" : StartName(r.DiagTrack));
            _pDiag.SetResourceReference(TextBlock.ForegroundProperty, r.DiagTrack == null || r.DiagTrack == "Disabled" ? "Good" : "Warn");

            _pTel.Text = "Telemetry policy (AllowTelemetry): " + TelemetryText(r.Telemetry);
        }
        catch (Exception ex)
        {
            Log.Write("summary privacy: " + ex.Message);
            _pCounts.Text = "Could not read the change journal: " + ex.Message;
        }
    }

    static string StartName(string mode) => mode switch
    {
        "AutomaticDelayed" => "Automatic (delayed start)",
        "Automatic" => "Automatic",
        "Manual" => "Manual (starts when needed)",
        "Disabled" => "Disabled",
        _ => mode
    };

    static string TelemetryText(object v)
    {
        if (v is not int level) return v == null ? "not set (Windows default)" : Convert.ToString(v);
        return level switch
        {
            0 => WinVer.HonoursTelemetryZero ? "0 - Security (diagnostic data off)" : "0 - treated as Required on " + WinVer.EditionName,
            1 => "1 - Required",
            2 => "2 - Enhanced",
            3 => "3 - Optional",
            _ => level.ToString()
        };
    }
}

/// <summary>One clickable process line on the Summary page: name on the left, value on the right.</summary>
internal sealed class SummaryProcRow : Button
{
    readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _value = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    int _pid;

    public SummaryProcRow()
    {
        Style = (Style)Application.Current.FindResource("Ghost");
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(6, 3, 6, 3);
        Margin = new Thickness(-6, 0, -6, 0);
        _value.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
        _value.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_value, 1);
        g.Children.Add(_name);
        g.Children.Add(_value);
        Content = g;
        Click += (_, _) => { if (_pid > 0) Nav.Go("processes", _pid); };
    }

    public void Set(ProcSample p, string value)
    {
        Visibility = Visibility.Visible;
        if (_name.Text != p.Name) _name.Text = p.Name;
        if (_value.Text != value) _value.Text = value;
        if (_pid != p.Pid)
        {
            _pid = p.Pid;
            ToolTip = SummaryView.Who(p) + " · PID " + p.Pid + "\nClick to show it in Processes.";
        }
    }
}

/// <summary>Up to three SummaryProcRows, or a note explaining why there are none.</summary>
internal sealed class SummaryTopList : StackPanel
{
    readonly SummaryProcRow[] _rows = new SummaryProcRow[3];
    readonly TextBlock _note;

    public SummaryTopList()
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new SummaryProcRow { Visibility = Visibility.Collapsed };
            Children.Add(_rows[i]);
        }
        _note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        Children.Add(_note);
    }

    public void Show(IList<(ProcSample p, string value)> items, string emptyNote)
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            if (i < items.Count) _rows[i].Set(items[i].p, items[i].value);
            else _rows[i].Visibility = Visibility.Collapsed;
        }
        bool showNote = items.Count == 0 && !string.IsNullOrEmpty(emptyNote);
        if (showNote && _note.Text != emptyNote) _note.Text = emptyNote;
        _note.Visibility = showNote ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Note(string text)
    {
        foreach (var r in _rows) r.Visibility = Visibility.Collapsed;
        if (_note.Text != text) _note.Text = text;
        _note.Visibility = Visibility.Visible;
    }
}
