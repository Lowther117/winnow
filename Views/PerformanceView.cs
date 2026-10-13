using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Winnow.Controls;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Task Manager's Performance tab, TMOG-style. Left: every subsystem with a
/// live sparkline. Right: the selected one in detail. Graphs are seeded from
/// the sampler's history whenever the page or a subsystem is shown, so they
/// are never empty; while shown, the sparklines and the visible detail graphs
/// take one sample per tick. Nothing here invents a value: a reading Winnow
/// could not take shows "—" and leaves a gap in the graph.
/// </summary>
public sealed class PerformanceView : UserControl, IView
{
    const int Samples = 60;

    readonly ListBox _list = new() { Width = 214, Margin = new Thickness(0, 0, 14, 0) };
    readonly Border _host = new();
    readonly Dictionary<string, PerfNav> _navs = new();
    readonly List<string> _order = new();
    readonly List<PerfGraphBlock> _blocks = new();
    string _sel = "cpu";
    bool _navigating;
    DateTime _cutoff = DateTime.MinValue;   // newest snapshot already in the graphs

    // ---- CPU
    Grid _cpuPanel;
    TextBlock _cpuTitle, _cpuSub;
    Graph _cpuGraph;
    PerfGraphBlock _cpuBlock;
    UniformGrid _coreHost;
    Graph[] _coreGraphs = System.Array.Empty<Graph>();
    ToggleButton _tOverall, _tLogical;
    StackPanel _coreLegend;
    readonly PerfStatPanel _cpuStats = new();
    bool _logical;

    // ---- memory
    Grid _memPanel;
    TextBlock _memTitle, _memSub, _memTemps, _memLegendUse, _memLegendCache, _memLegendFree;
    Graph _memGraph;
    ColumnDefinition _colUse, _colCache, _colFree;
    readonly PerfStatPanel _memStats = new();

    // ---- disk (one panel, shows the selected disk)
    Grid _diskPanel;
    TextBlock _diskTitle, _diskSub, _diskVols, _diskTemps;
    Graph _diskActive, _diskRate;
    PerfGraphBlock _diskRateBlock;
    readonly PerfStatPanel _diskStats = new();

    // ---- network (one panel, shows the selected adapter)
    Grid _netPanel;
    TextBlock _netTitle, _netSub, _netDetails;
    Graph _netGraph;
    PerfGraphBlock _netBlock;
    readonly PerfStatPanel _netStats = new();

    // ---- GPU
    Grid _gpuPanel;
    TextBlock _gpuTitle, _gpuSub, _gpuNote, _gpuSensorNote;
    Graph _gpuGraph;
    DataGrid _gpuGrid;
    readonly PerfStatPanel _gpuStats = new();

    // ---- thermals
    Grid _thermPanel;
    TextBlock _thermTitle, _thermSub, _thermStatus, _thermWhy;
    TextBox _filterBox;
    ComboBox _typeBox;
    DataGrid _thermGrid;
    Border _thermEmpty;
    bool _pawnInstalled;
    long _pawnCheckedAt = -1_000_000;

    // sensor rows, shared by the Thermals and GPU tables
    readonly ObservableCollection<PerfSensorRow> _sensorRows = new();
    readonly Dictionary<string, PerfSensorRow> _sensorById = new();
    ListCollectionView _thermView, _gpuView;

    public PerformanceView()
    {
        _thermView = new ListCollectionView(_sensorRows);
        _thermView.SortDescriptions.Add(new SortDescription(nameof(PerfSensorRow.Hardware), ListSortDirection.Ascending));
        _thermView.SortDescriptions.Add(new SortDescription(nameof(PerfSensorRow.Type), ListSortDirection.Ascending));
        _thermView.SortDescriptions.Add(new SortDescription(nameof(PerfSensorRow.Name), ListSortDirection.Ascending));
        _thermView.Filter = ThermFilter;
        _gpuView = new ListCollectionView(_sensorRows);
        _gpuView.SortDescriptions.Add(new SortDescription(nameof(PerfSensorRow.Hardware), ListSortDirection.Ascending));
        _gpuView.SortDescriptions.Add(new SortDescription(nameof(PerfSensorRow.Type), ListSortDirection.Ascending));
        _gpuView.SortDescriptions.Add(new SortDescription(nameof(PerfSensorRow.Name), ListSortDirection.Ascending));
        _gpuView.Filter = o => o is PerfSensorRow r && r.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);

        BuildCpuPanel();
        BuildMemPanel();
        BuildDiskPanel();
        BuildNetPanel();
        BuildGpuPanel();
        BuildThermPanel();

        _list.SetValue(VirtualizingPanel.IsVirtualizingProperty, false);
        _list.SelectionChanged += (_, _) =>
        {
            if (_navigating) return;
            if (_list.SelectedItem is ListBoxItem { Tag: string key } && key != _sel) Select(key);
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_host, 1);
        body.Children.Add(_list);
        body.Children.Add(_host);

        var root = new Grid();
        PerfUi.AddRow(root, Ui.Header("Performance", "Every subsystem, live. Pick one on the left to see it in detail."), GridLength.Auto);
        PerfUi.AddRow(root, body, new GridLength(1, GridUnitType.Star));
        Content = root;

        Reconcile(Sampler.Latest);
        SelectKey("cpu");
    }

    // ================================================================ IView

    public void OnShown()
    {
        _cutoff = Sampler.History.Newest?.Time ?? DateTime.MinValue;
        foreach (var b in _blocks) b.RefreshSpan();
        var s = Sampler.Latest;
        Reconcile(s);
        var hist = Recent();
        foreach (var n in _navs.Values) SeedNav(n, hist);
        SeedPanel(hist);
        if (s != null)
        {
            foreach (var n in _navs.Values) n.ValueText.Text = n.Text(s);
            UpdatePanel(s);
        }
        LoadDetails();
        Sampler.Tick -= OnTick;
        Sampler.Tick += OnTick;
    }

    public void OnHidden() => Sampler.Tick -= OnTick;

    /// <summary>Nav.Go("performance", "cpu" | "memory" | "disk" | "network" | "gpu" | "thermals").</summary>
    public void Receive(object arg)
    {
        if (arg is not string a) return;
        string want = a.ToLowerInvariant() switch
        {
            "cpu" => "cpu",
            "mem" or "memory" => "mem",
            "gpu" => "gpu",
            "thermals" or "sensors" or "temperature" => "thermals",
            "disk" => _order.FirstOrDefault(k => k.StartsWith("disk:")),
            "net" or "network" => _order.FirstOrDefault(k => k.StartsWith("net:")),
            _ => _navs.ContainsKey(a) ? a : null
        };
        if (want != null && _navs.ContainsKey(want)) SelectKey(want);
    }

    // ================================================================ tick

    void OnTick(Snapshot s)
    {
        Reconcile(s);
        bool fresh = s.Time > _cutoff;
        if (fresh) _cutoff = s.Time;
        foreach (var k in _order)
        {
            var n = _navs[k];
            if (fresh) n.Spark.Push(n.Values(s));
            string t = n.Text(s);
            if (n.ValueText.Text != t) n.ValueText.Text = t;
        }
        if (fresh) PushPanel(s);
        UpdatePanel(s);
    }

    /// <summary>History snapshots up to the cutoff, oldest first, at most one graph's worth.</summary>
    List<Snapshot> Recent()
    {
        var all = Sampler.History.ToList();
        var res = new List<Snapshot>(Samples);
        for (int i = all.Count - 1; i >= 0 && res.Count < Samples; i--)
            if (all[i] != null && all[i].Time <= _cutoff) res.Add(all[i]);
        res.Reverse();
        return res;
    }

    // ================================================================ left list

    void Reconcile(Snapshot s)
    {
        var want = new List<string> { "cpu", "mem" };
        if (s != null)
        {
            foreach (var d in s.Disks) want.Add("disk:" + d.Name);
            foreach (var n in s.Nets) want.Add("net:" + n.Name);
        }
        want.Add("gpu");
        want.Add("thermals");
        if (want.SequenceEqual(_order)) return;

        List<Snapshot> hist = null;
        foreach (var k in want)
        {
            if (_navs.ContainsKey(k)) continue;
            var nav = CreateNav(k, s);
            _navs[k] = nav;
            hist ??= Recent();
            SeedNav(nav, hist);
            if (s != null) nav.ValueText.Text = nav.Text(s);
        }
        foreach (var k in _navs.Keys.Except(want).ToList()) _navs.Remove(k);

        bool lost = !_navs.ContainsKey(_sel);
        if (lost) _sel = "cpu";
        _navigating = true;
        _list.Items.Clear();
        foreach (var k in want) _list.Items.Add(_navs[k].Item);
        _list.SelectedItem = _navs[_sel].Item;
        _navigating = false;
        _order.Clear();
        _order.AddRange(want);
        if (lost) Select(_sel);
    }

    PerfNav CreateNav(string key, Snapshot s)
    {
        if (key == "cpu")
            return MakeNav(key, "CPU", "Graph1", null, false, 100,
                h => new[] { h.CpuTotal },
                h => Fmt.Pct(h.CpuTotal) + (h.CpuMhz.HasValue ? "  " + Fmt.Mhz(h.CpuMhz) : ""));
        if (key == "mem")
            return MakeNav(key, "Memory", "Graph2", null, false, 100,
                h => new[] { MemPct(h) },
                h => h.MemTotal > 0 ? $"{Fmt.Bytes(h.MemUsed)} / {Fmt.Bytes(h.MemTotal)} ({Fmt.Pct(h.MemLoadPct)})" : Fmt.Na);
        if (key.StartsWith("disk:"))
        {
            string name = key.Substring(5);
            var d0 = s?.Disks.FirstOrDefault(x => x.Name == name);
            int idx = d0?.Index ?? -1;
            return MakeNav(key, d0 != null ? DiskTitle(d0) : name, "Graph3", null, false, 100,
                h => new[] { FindDisk(h, name)?.ActivePct ?? double.NaN },
                h =>
                {
                    var d = FindDisk(h, name);
                    string kind = idx >= 0 ? PerfHwInfo.DiskKind(idx) : "";
                    return Fmt.Pct(d?.ActivePct) + (kind.Length > 0 ? "  " + kind : "");
                });
        }
        if (key.StartsWith("net:"))
        {
            string name = key.Substring(4);
            var nav = MakeNav(key, name, "Graph4", "Graph2", true, 100,
                h =>
                {
                    var n = FindNet(h, name);
                    return n == null ? new[] { double.NaN, double.NaN } : new[] { n.RecvBps, n.SendBps };
                },
                h =>
                {
                    var n = FindNet(h, name);
                    return n == null ? Fmt.Na : $"R {Fmt.Bits(n.RecvBps)}  S {Fmt.Bits(n.SendBps)}";
                });
            nav.Spark.AutoFloor = 16 * 1024;
            return nav;
        }
        if (key == "gpu")
            return MakeNav(key, "GPU", "Graph1", null, false, 100,
                h => new[] { h.GpuPct ?? double.NaN },
                h => Fmt.Pct(h.GpuPct) + (h.GpuTempC.HasValue ? "  " + Fmt.Temp(h.GpuTempC) : ""));
        return MakeNav("thermals", "Thermals", "Graph4", "Graph3", false, 100,
            h => new[] { h.CpuTempC ?? double.NaN, h.GpuTempC ?? double.NaN },
            h => h.CpuTempC.HasValue || h.GpuTempC.HasValue
                ? $"CPU {Fmt.Temp(h.CpuTempC)}  GPU {Fmt.Temp(h.GpuTempC)}"
                : h.Sensors.Count > 0 ? h.Sensors.Count + " readings" : "unavailable");
    }

    static PerfNav MakeNav(string key, string title, string color1, string color2, bool autoMax, double max,
                           Func<Snapshot, double[]> values, Func<Snapshot, string> text)
    {
        var n = new PerfNav { Key = key, Values = values, Text = text };
        n.Spark = new Graph
        {
            Width = 66, Height = 38, Capacity = Samples, ShowGrid = false, AutoMax = autoMax, Max = max,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        n.Spark.AddSeries(color1, true, 1.2);
        if (color2 != null) n.Spark.AddSeries(color2, false, 1.2);
        n.TitleText = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        n.ValueText = new TextBlock { Text = Fmt.Na, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        n.ValueText.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
        n.ValueText.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(n.TitleText);
        sp.Children.Add(n.ValueText);
        Grid.SetColumn(sp, 1);
        g.Children.Add(n.Spark);
        g.Children.Add(sp);
        n.Item = new ListBoxItem { Content = g, Tag = key, Padding = new Thickness(8, 6, 8, 6), ToolTip = title };
        return n;
    }

    static void SeedNav(PerfNav n, List<Snapshot> hist) => n.Spark.Seed(hist.Select(n.Values).ToList());

    void SelectKey(string key)
    {
        if (!_navs.TryGetValue(key, out var nav)) return;
        _navigating = true;
        _list.SelectedItem = nav.Item;
        _navigating = false;
        Select(key);
    }

    // ================================================================ selection

    void Select(string key)
    {
        _sel = key;
        FrameworkElement panel =
            key == "cpu" ? _cpuPanel :
            key == "mem" ? _memPanel :
            key.StartsWith("disk:") ? _diskPanel :
            key.StartsWith("net:") ? _netPanel :
            key == "gpu" ? _gpuPanel : _thermPanel;
        if (_host.Child != panel) _host.Child = panel;

        if (key.StartsWith("disk:")) { _diskVols.Text = "Reading volumes…"; _diskTemps.Text = ""; }
        if (key.StartsWith("net:")) _netDetails.Text = "Reading adapter details…";
        if (key == "thermals") _pawnCheckedAt = -1_000_000;

        SeedPanel(Recent());
        var s = Sampler.Latest;
        if (s != null) UpdatePanel(s);
        LoadDetails();
    }

    string SelName => _sel.StartsWith("disk:") ? _sel.Substring(5) : _sel.StartsWith("net:") ? _sel.Substring(4) : "";

    void SeedPanel(List<Snapshot> hist)
    {
        string name = SelName;
        if (_sel == "cpu")
        {
            _cpuGraph.Seed(hist.Select(h => new[] { h.CpuTotal, h.CpuKernel }).ToList());
            if (_logical) SeedCores(hist);
        }
        else if (_sel == "mem") _memGraph.Seed(hist.Select(h => new[] { MemPct(h) }).ToList());
        else if (_sel.StartsWith("disk:"))
        {
            _diskActive.Seed(hist.Select(h => new[] { FindDisk(h, name)?.ActivePct ?? double.NaN }).ToList());
            _diskRate.Seed(hist.Select(h =>
            {
                var d = FindDisk(h, name);
                return new[] { d?.ReadBps ?? double.NaN, d?.WriteBps ?? double.NaN };
            }).ToList());
        }
        else if (_sel.StartsWith("net:"))
        {
            _netGraph.Seed(hist.Select(h =>
            {
                var n = FindNet(h, name);
                return n == null ? new[] { double.NaN, double.NaN } : new[] { n.RecvBps, n.SendBps };
            }).ToList());
        }
        else if (_sel == "gpu") _gpuGraph.Seed(hist.Select(h => new[] { h.GpuPct ?? double.NaN }).ToList());
        foreach (var b in _blocks) b.UpdateScale();
    }

    void SeedCores(List<Snapshot> hist)
    {
        for (int i = 0; i < _coreGraphs.Length; i++)
        {
            int c = i;
            _coreGraphs[i].Seed(hist.Select(h => new[] { c < h.CpuPerCore.Length ? h.CpuPerCore[c] : double.NaN }).ToList());
        }
    }

    void PushPanel(Snapshot s)
    {
        string name = SelName;
        if (_sel == "cpu")
        {
            _cpuGraph.Push(s.CpuTotal, s.CpuKernel);
            if (_logical)
                for (int i = 0; i < _coreGraphs.Length; i++)
                    _coreGraphs[i].Push(i < s.CpuPerCore.Length ? s.CpuPerCore[i] : double.NaN);
        }
        else if (_sel == "mem") _memGraph.Push(MemPct(s));
        else if (_sel.StartsWith("disk:"))
        {
            var d = FindDisk(s, name);
            _diskActive.Push(d?.ActivePct ?? double.NaN);
            _diskRate.Push(d?.ReadBps ?? double.NaN, d?.WriteBps ?? double.NaN);
            _diskRateBlock.UpdateScale();
        }
        else if (_sel.StartsWith("net:"))
        {
            var n = FindNet(s, name);
            if (n == null) _netGraph.Push(double.NaN, double.NaN);
            else _netGraph.Push(n.RecvBps, n.SendBps);
            _netBlock.UpdateScale();
        }
        else if (_sel == "gpu") _gpuGraph.Push(s.GpuPct ?? double.NaN);
    }

    void UpdatePanel(Snapshot s)
    {
        if (_sel == "cpu") UpdateCpu(s);
        else if (_sel == "mem") UpdateMem(s);
        else if (_sel.StartsWith("disk:")) UpdateDisk(s);
        else if (_sel.StartsWith("net:")) UpdateNet(s);
        else if (_sel == "gpu") { UpdateSensorRows(s); UpdateGpu(s); }
        else { UpdateSensorRows(s); UpdateTherm(s); }
    }

    /// <summary>Static facts (WMI) and per-selection details (volumes, addresses), all off the UI thread.</summary>
    async void LoadDetails()
    {
        string key = _sel;
        try
        {
            await PerfHwInfo.LoadAsync();
            if (key != _sel) return;
            ApplyStaticInfo();
            if (key.StartsWith("disk:"))
            {
                string letters = FindDisk(Sampler.Latest, SelName)?.Letters ?? "";
                string text = await Task.Run(() => PerfHwInfo.Volumes(letters));
                if (key == _sel) _diskVols.Text = text;
            }
            else if (key.StartsWith("net:"))
            {
                string name = SelName;
                string text = await Task.Run(() => PerfHwInfo.NetDetails(name));
                if (key == _sel) _netDetails.Text = text;
            }
        }
        catch (Exception ex)
        {
            Log.Write("performance details: " + ex.Message);
        }
    }

    void ApplyStaticInfo()
    {
        var s = Sampler.Latest;
        if (_sel == "cpu")
        {
            string name = PerfHwInfo.CpuName;
            if (string.IsNullOrEmpty(name) && s != null)
                name = s.Sensors.FirstOrDefault(r => r.HardwareType == "Cpu")?.Hardware ?? "";
            _cpuSub.Text = name;
            _cpuStats.Set("Sockets", PerfHwInfo.Sockets > 0 ? PerfHwInfo.Sockets.ToString() : Fmt.Na);
            _cpuStats.Set("Cores", PerfHwInfo.Cores > 0 ? PerfHwInfo.Cores.ToString() : Fmt.Na);
            _cpuStats.Set("Logical processors", LogicalText());
            _cpuStats.Set("Max speed", Fmt.Mhz(PerfHwInfo.MaxMhz));
            _cpuStats.Set("L2 cache", PerfHwInfo.L2KB.HasValue ? Fmt.Bytes(PerfHwInfo.L2KB.Value * 1024.0) : Fmt.Na);
            _cpuStats.Set("L3 cache", PerfHwInfo.L3KB.HasValue ? Fmt.Bytes(PerfHwInfo.L3KB.Value * 1024.0) : Fmt.Na);
        }
        else if (_sel == "mem")
        {
            _memStats.Set("Speed", PerfHwInfo.MemSpeed.Length > 0 ? PerfHwInfo.MemSpeed : Fmt.Na);
            _memStats.Set("Modules", PerfHwInfo.MemModules.Length > 0 ? PerfHwInfo.MemModules : Fmt.Na);
            if (s != null && s.MemTotal > 0)
                _memSub.Text = Fmt.Bytes(s.MemTotal) + (PerfHwInfo.MemSpeed.Length > 0 ? " · " + PerfHwInfo.MemSpeed : "");
        }
        else if (_sel.StartsWith("disk:"))
        {
            var d = FindDisk(s, SelName);
            if (d != null && PerfHwInfo.Disks.TryGetValue(d.Index, out var info))
            {
                var parts = new List<string>();
                if (info.Model.Length > 0) parts.Add(info.Model);
                string kind = PerfHwInfo.DiskKind(d.Index);
                if (kind.Length > 0) parts.Add(kind);
                _diskSub.Text = string.Join(" · ", parts);
                _diskStats.Set("Capacity", info.Size > 0 ? Fmt.Bytes(info.Size) : Fmt.Na);
                _diskStats.Set("Type", kind.Length > 0 ? kind : Fmt.Na);
            }
            else
            {
                _diskSub.Text = "";
                _diskStats.Set("Capacity", Fmt.Na);
                _diskStats.Set("Type", Fmt.Na);
            }
        }
        else if (_sel == "gpu")
        {
            _gpuSub.Text = GpuName(s);
        }
    }

    // ================================================================ CPU

    void BuildCpuPanel()
    {
        _cpuGraph = new Graph { Capacity = Samples, Max = 100 };
        _cpuGraph.AddSeries("Graph1", true, 1.6, "Total");
        _cpuGraph.AddSeries("Graph3", false, 1.2, "Kernel");
        _cpuBlock = new PerfGraphBlock("% Utilisation", _cpuGraph, v => v.ToString("0") + "%", ("Graph1", "Total"), ("Graph3", "Kernel time"));
        _blocks.Add(_cpuBlock);

        _tOverall = new ToggleButton { Content = "Overall", IsChecked = true };
        _tLogical = new ToggleButton { Content = "Logical processors" };
        _tOverall.Click += (_, _) => SetLogical(false);
        _tLogical.Click += (_, _) => SetLogical(true);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(_tOverall);
        tools.Children.Add(_tLogical);
        _coreLegend = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(6, 0, 0, 0) };
        if (Sampler.IsHybrid)
        {
            _coreLegend.Children.Add(PerfUi.Swatch("PCore", "P-core", 6));
            _coreLegend.Children.Add(PerfUi.Swatch("ECore", "E-core", 12));
        }
        tools.Children.Add(_coreLegend);

        int n = Math.Max(1, Sampler.LogicalProcessors);
        _coreGraphs = new Graph[n];
        _coreHost = new UniformGrid { Columns = CoreColumns(n), Visibility = Visibility.Collapsed };
        for (int i = 0; i < n; i++)
        {
            var g = new Graph { Capacity = Samples, Max = 100, GridLines = 2, Margin = new Thickness(2) };
            g.AddSeries(CoreColor(i), true, 1.2);
            _coreGraphs[i] = g;
            var cell = new Grid { ToolTip = CoreTip(i) };
            cell.Children.Add(g);
            var lab = PerfUi.Small(i.ToString());
            lab.FontSize = 10;
            lab.Margin = new Thickness(6, 3, 0, 0);
            lab.VerticalAlignment = VerticalAlignment.Top;
            lab.HorizontalAlignment = HorizontalAlignment.Left;
            lab.IsHitTestVisible = false;
            cell.Children.Add(lab);
            _coreHost.Children.Add(cell);
        }

        var area = new Grid();
        area.Children.Add(_cpuBlock);
        area.Children.Add(_coreHost);

        foreach (var label in new[] { "Utilisation", "Speed", "Base speed", "Kernel time", "Processes", "Threads", "Handles", "Up time",
                                      "Temperature", "Package power", "Sockets", "Cores", "Logical processors", "Max speed", "L2 cache", "L3 cache" })
            _cpuStats.Set(label, Fmt.Na);
        _cpuStats.Set("Logical processors", LogicalText());

        _cpuPanel = new Grid();
        PerfUi.AddRow(_cpuPanel, PerfUi.Title("CPU", out _cpuTitle, out _cpuSub), GridLength.Auto);
        PerfUi.AddRow(_cpuPanel, tools, GridLength.Auto);
        PerfUi.AddRow(_cpuPanel, area, new GridLength(1, GridUnitType.Star), 110);
        PerfUi.AddRow(_cpuPanel, PerfUi.Scroll(_cpuStats, 190), GridLength.Auto);
    }

    void SetLogical(bool on)
    {
        _logical = on;
        _tOverall.IsChecked = !on;
        _tLogical.IsChecked = on;
        _cpuBlock.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _coreHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _coreLegend.Visibility = on && Sampler.IsHybrid ? Visibility.Visible : Visibility.Collapsed;
        if (on) SeedCores(Recent());
    }

    /// <summary>Near-square grid, preferring a column count that divides the processor count evenly.</summary>
    static int CoreColumns(int n)
    {
        int c = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        for (int k = c; k <= c + 2 && k <= n; k++)
            if (n % k == 0) return k;
        return c;
    }

    /// <summary>Higher efficiency class = performance core. Non-hybrid CPUs all use Graph1.</summary>
    static string CoreColor(int i)
    {
        var ec = Sampler.EfficiencyClass;
        if (!Sampler.IsHybrid || ec.Length == 0 || i >= ec.Length) return "Graph1";
        return ec[i] == ec.Max() ? "PCore" : "ECore";
    }

    static string CoreTip(int i)
    {
        var ec = Sampler.EfficiencyClass;
        if (!Sampler.IsHybrid || i >= ec.Length) return "Logical processor " + i;
        return "Logical processor " + i + (ec[i] == ec.Max() ? " · P-core (performance)" : " · E-core (efficiency)");
    }

    static string LogicalText()
    {
        int n = Sampler.LogicalProcessors;
        var ec = Sampler.EfficiencyClass;
        if (!Sampler.IsHybrid || ec.Length == 0) return n.ToString();
        byte top = ec.Max();
        int p = ec.Count(x => x == top);
        return $"{n} ({p} P, {ec.Length - p} E)";
    }

    void UpdateCpu(Snapshot s)
    {
        _cpuStats.Set("Utilisation", Fmt.Pct(s.CpuTotal));
        _cpuStats.Set("Speed", Fmt.Mhz(s.CpuMhz));
        _cpuStats.Set("Base speed", Fmt.Mhz(s.CpuBaseMhz));
        _cpuStats.Set("Kernel time", Fmt.Pct(s.CpuKernel));
        _cpuStats.Set("Processes", s.ProcessCount.ToString("N0"));
        _cpuStats.Set("Threads", s.ThreadCount.ToString("N0"));
        _cpuStats.Set("Handles", s.HandleCount.ToString("N0"));
        _cpuStats.Set("Up time", Fmt.Duration(s.Uptime));
        _cpuStats.Set("Temperature", Fmt.Temp(s.CpuTempC));
        _cpuStats.Set("Package power", Fmt.Watts(s.CpuWatts));
        if (string.IsNullOrEmpty(_cpuSub.Text))
            _cpuSub.Text = s.Sensors.FirstOrDefault(r => r.HardwareType == "Cpu")?.Hardware ?? "";
    }

    // ================================================================ memory

    void BuildMemPanel()
    {
        _memGraph = new Graph { Capacity = Samples, Max = 100 };
        _memGraph.AddSeries("Graph2", true, 1.6, "In use");
        var block = new PerfGraphBlock("Memory in use (% of installed)", _memGraph, v => v.ToString("0") + "%");
        _blocks.Add(block);

        // composition bar: in use | cached | free
        var bar = new Grid { Height = 22 };
        _colUse = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _colCache = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _colFree = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        bar.ColumnDefinitions.Add(_colUse);
        bar.ColumnDefinitions.Add(_colCache);
        bar.ColumnDefinitions.Add(_colFree);
        var segUse = new Border { ToolTip = "In use: memory programs and Windows are holding" };
        segUse.SetResourceReference(Border.BackgroundProperty, "Graph2");
        var segCache = new Border { ToolTip = "Cached: the system file cache. Most of it is handed to programs on demand." };
        segCache.SetResourceReference(Border.BackgroundProperty, "Graph3");
        var segFree = new Border { ToolTip = "Free: available and not holding cached data" };
        segFree.SetResourceReference(Border.BackgroundProperty, "Field");
        Grid.SetColumn(segCache, 1);
        Grid.SetColumn(segFree, 2);
        bar.Children.Add(segUse);
        bar.Children.Add(segCache);
        bar.Children.Add(segFree);
        var barFrame = new Border { Child = bar, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ClipToBounds = true, Padding = new Thickness(1) };
        barFrame.SetResourceReference(Border.BorderBrushProperty, "Border");

        var legend = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        _memLegendUse = PerfUi.Small("");
        _memLegendCache = PerfUi.Small("");
        _memLegendFree = PerfUi.Small("");
        legend.Children.Add(LegendItem("Graph2", _memLegendUse, 0));
        legend.Children.Add(LegendItem("Graph3", _memLegendCache, 16));
        legend.Children.Add(LegendItem("Field", _memLegendFree, 16));

        var comp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        comp.Children.Add(Ui.Text("Composition", "Label"));
        comp.Children.Add(barFrame);
        comp.Children.Add(legend);

        foreach (var label in new[] { "In use", "Available", "Committed", "Cached", "Paged pool", "Non-paged pool", "Total", "Speed", "Modules" })
            _memStats.Set(label, Fmt.Na);
        _memTemps = PerfUi.Wrapped();
        _memTemps.Visibility = Visibility.Collapsed;
        var lower = new StackPanel();
        lower.Children.Add(_memStats);
        lower.Children.Add(_memTemps);

        _memPanel = new Grid();
        PerfUi.AddRow(_memPanel, PerfUi.Title("Memory", out _memTitle, out _memSub), GridLength.Auto);
        PerfUi.AddRow(_memPanel, block, new GridLength(1, GridUnitType.Star), 100);
        PerfUi.AddRow(_memPanel, comp, GridLength.Auto);
        PerfUi.AddRow(_memPanel, PerfUi.Scroll(lower, 170), GridLength.Auto);
    }

    static StackPanel LegendItem(string colorKey, TextBlock text, double left)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(left, 0, 0, 0) };
        var sq = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        sq.SetResourceReference(Border.BackgroundProperty, colorKey);
        sq.SetResourceReference(Border.BorderBrushProperty, "Border");
        sp.Children.Add(sq);
        text.Margin = new Thickness(5, 0, 0, 0);
        sp.Children.Add(text);
        return sp;
    }

    void UpdateMem(Snapshot s)
    {
        bool ok = s.MemTotal > 0;
        bool perf = s.CommitLimit > 0;   // GetPerformanceInfo succeeded
        if (string.IsNullOrEmpty(_memSub.Text) && ok) _memSub.Text = Fmt.Bytes(s.MemTotal);
        _memStats.Set("In use", ok ? Fmt.Bytes(s.MemUsed) : Fmt.Na);
        _memStats.Set("Available", ok ? Fmt.Bytes(s.MemAvailable) : Fmt.Na);
        _memStats.Set("Committed", perf ? $"{Fmt.Bytes(s.CommitTotal)} / {Fmt.Bytes(s.CommitLimit)}" : Fmt.Na);
        _memStats.Set("Cached", perf ? Fmt.Bytes(s.Cached) : Fmt.Na);
        _memStats.Set("Paged pool", perf ? Fmt.Bytes(s.PagedPool) : Fmt.Na);
        _memStats.Set("Non-paged pool", perf ? Fmt.Bytes(s.NonPagedPool) : Fmt.Na);
        _memStats.Set("Total", ok ? Fmt.Bytes(s.MemTotal) : Fmt.Na);

        if (ok)
        {
            double total = s.MemTotal;
            double inUse = s.MemUsed;
            double cached = perf ? Math.Min((double)s.Cached, (double)s.MemAvailable) : 0;
            double free = Math.Max(0, (double)s.MemAvailable - cached);
            _colUse.Width = new GridLength(Math.Max(0, inUse / total), GridUnitType.Star);
            _colCache.Width = new GridLength(Math.Max(0, cached / total), GridUnitType.Star);
            _colFree.Width = new GridLength(Math.Max(0, free / total), GridUnitType.Star);
            _memLegendUse.Text = "In use " + Fmt.Bytes(inUse);
            _memLegendCache.Text = perf ? "Cached " + Fmt.Bytes(cached) : "Cached " + Fmt.Na;
            _memLegendFree.Text = "Free " + Fmt.Bytes(free) + "  (available = cached + free)";
        }
        else
        {
            _memLegendUse.Text = "In use " + Fmt.Na;
            _memLegendCache.Text = "Cached " + Fmt.Na;
            _memLegendFree.Text = "Free " + Fmt.Na;
        }

        var temps = s.Sensors.Where(r => r.HardwareType == "Memory" && r.Type == "Temperature").ToList();
        if (temps.Count > 0)
        {
            _memTemps.Text = "Module temperatures:\n" + string.Join("\n", temps.Select(r => $"{r.Hardware} · {r.Name}: {Fmt.Sensor(r)}"));
            _memTemps.Visibility = Visibility.Visible;
        }
        else _memTemps.Visibility = Visibility.Collapsed;
    }

    // ================================================================ disk

    void BuildDiskPanel()
    {
        _diskActive = new Graph { Capacity = Samples, Max = 100 };
        _diskActive.AddSeries("Graph3", true, 1.6, "Active time");
        var activeBlock = new PerfGraphBlock("Active time", _diskActive, v => v.ToString("0") + "%");
        _blocks.Add(activeBlock);

        _diskRate = new Graph { Capacity = Samples, AutoMax = true, AutoFloor = 256 * 1024 };
        _diskRate.AddSeries("Graph1", true, 1.4, "Read");
        _diskRate.AddSeries("Graph4", false, 1.4, "Write");
        _diskRateBlock = new PerfGraphBlock("Transfer rate", _diskRate, v => Fmt.Rate(v), ("Graph1", "Read"), ("Graph4", "Write"));
        _blocks.Add(_diskRateBlock);
        _diskRateBlock.Margin = new Thickness(0, 8, 0, 0);

        var graphs = new Grid();
        PerfUi.AddRow(graphs, activeBlock, new GridLength(1, GridUnitType.Star), 80);
        PerfUi.AddRow(graphs, _diskRateBlock, new GridLength(1, GridUnitType.Star), 80);

        foreach (var label in new[] { "Active time", "Average response", "Read speed", "Write speed", "Temperature", "Capacity", "Type" })
            _diskStats.Set(label, Fmt.Na);
        _diskVols = PerfUi.Wrapped();
        _diskVols.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _diskTemps = PerfUi.Wrapped();
        var lower = new StackPanel();
        lower.Children.Add(_diskStats);
        lower.Children.Add(Ui.Text("Volumes", "Label"));
        lower.Children.Add(_diskVols);
        lower.Children.Add(_diskTemps);

        _diskPanel = new Grid();
        PerfUi.AddRow(_diskPanel, PerfUi.Title("Disk", out _diskTitle, out _diskSub), GridLength.Auto);
        PerfUi.AddRow(_diskPanel, graphs, new GridLength(1, GridUnitType.Star), 180);
        PerfUi.AddRow(_diskPanel, PerfUi.Scroll(lower, 190), GridLength.Auto);
    }

    void UpdateDisk(Snapshot s)
    {
        var d = FindDisk(s, SelName);
        if (d == null) return;
        _diskTitle.Text = DiskTitle(d);
        _diskStats.Set("Active time", Fmt.Pct(d.ActivePct));
        _diskStats.Set("Average response", d.AvgResponseMs.HasValue ? d.AvgResponseMs.Value.ToString("0.0") + " ms" : Fmt.Na);
        _diskStats.Set("Read speed", Fmt.Rate(d.ReadBps));
        _diskStats.Set("Write speed", Fmt.Rate(d.WriteBps));

        // Drive temperature: LibreHardwareMonitor names storage by model, so match on the WMI model name.
        var temps = s.Sensors.Where(r => r.HardwareType == "Storage" && r.Type == "Temperature" && r.Value.HasValue).ToList();
        string model = PerfHwInfo.Disks.TryGetValue(d.Index, out var info) ? info.Model : "";
        var mine = model.Length > 0 ? temps.Where(r => SameDevice(r.Hardware, model)).ToList() : new List<SensorReading>();
        if (mine.Count > 0)
        {
            _diskStats.Set("Temperature", Fmt.Temp(mine[0].Value));
            _diskTemps.Text = mine.Count > 1 ? "Drive sensors: " + string.Join(" · ", mine.Select(r => r.Name + " " + Fmt.Sensor(r))) : "";
        }
        else
        {
            _diskStats.Set("Temperature", Fmt.Na);
            _diskTemps.Text = temps.Count == 0
                ? "No drive temperature sensors are readable."
                : "Could not match this disk to a sensor. All drive temperatures:\n" +
                  string.Join("\n", temps.Select(r => $"{r.Hardware} · {r.Name}: {Fmt.Sensor(r)}"));
        }
    }

    static bool SameDevice(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ network

    void BuildNetPanel()
    {
        _netGraph = new Graph { Capacity = Samples, AutoMax = true, AutoFloor = 16 * 1024 };
        _netGraph.AddSeries("Graph4", true, 1.5, "Receive");
        _netGraph.AddSeries("Graph2", false, 1.5, "Send");
        _netBlock = new PerfGraphBlock("Throughput", _netGraph, v => Fmt.Bits(v), ("Graph4", "Receive"), ("Graph2", "Send"));
        _blocks.Add(_netBlock);

        foreach (var label in new[] { "Receive", "Send", "Link speed", "Link use", "Type", "Received (total)", "Sent (total)" })
            _netStats.Set(label, Fmt.Na);
        _netDetails = PerfUi.Wrapped();
        _netDetails.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        var lower = new StackPanel();
        lower.Children.Add(_netStats);
        lower.Children.Add(_netDetails);

        _netPanel = new Grid();
        PerfUi.AddRow(_netPanel, PerfUi.Title("Network", out _netTitle, out _netSub), GridLength.Auto);
        PerfUi.AddRow(_netPanel, _netBlock, new GridLength(1, GridUnitType.Star), 110);
        PerfUi.AddRow(_netPanel, PerfUi.Scroll(lower, 210), GridLength.Auto);
    }

    void UpdateNet(Snapshot s)
    {
        var n = FindNet(s, SelName);
        if (n == null) return;
        _netTitle.Text = n.Name;
        _netSub.Text = n.Description;
        _netStats.Set("Receive", Fmt.Bits(n.RecvBps));
        _netStats.Set("Send", Fmt.Bits(n.SendBps));
        // NetworkInterface.Speed is already bits per second; Fmt.Bits takes bytes per second.
        bool link = n.LinkSpeedBps > 0;
        _netStats.Set("Link speed", link ? Fmt.Bits(n.LinkSpeedBps / 8.0) : Fmt.Na);
        _netStats.Set("Link use", link ? Fmt.Pct(Math.Max(n.RecvBps, n.SendBps) * 8 / n.LinkSpeedBps * 100, 1) : Fmt.Na);
        _netStats.Set("Type", string.IsNullOrEmpty(n.Type) ? Fmt.Na : n.Type);
        _netStats.Set("Received (total)", Fmt.Bytes(n.TotalRecv));
        _netStats.Set("Sent (total)", Fmt.Bytes(n.TotalSent));
    }

    // ================================================================ GPU

    void BuildGpuPanel()
    {
        _gpuGraph = new Graph { Capacity = Samples, Max = 100 };
        _gpuGraph.AddSeries("Graph1", true, 1.6, "Utilisation");
        var block = new PerfGraphBlock("% Utilisation (busiest engine)", _gpuGraph, v => v.ToString("0") + "%");
        _blocks.Add(block);

        _gpuNote = PerfUi.Wrapped("GPU utilisation is unavailable: the GPU Engine performance counters could not be read on this PC.");
        _gpuNote.Visibility = Visibility.Collapsed;

        foreach (var label in new[] { "Utilisation", "Dedicated memory", "Temperature", "Hot spot", "Power" })
            _gpuStats.Set(label, Fmt.Na);

        _gpuGrid = SensorGrid(_gpuView, true);
        _gpuGrid.MaxHeight = 190;
        _gpuGrid.Margin = new Thickness(0, 6, 0, 0);
        _gpuSensorNote = PerfUi.Wrapped();
        _gpuSensorNote.Visibility = Visibility.Collapsed;

        var lower = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        lower.Children.Add(_gpuNote);
        lower.Children.Add(_gpuStats);
        lower.Children.Add(Ui.Text("GPU sensors", "Label"));
        lower.Children.Add(_gpuGrid);
        lower.Children.Add(_gpuSensorNote);

        _gpuPanel = new Grid();
        PerfUi.AddRow(_gpuPanel, PerfUi.Title("GPU", out _gpuTitle, out _gpuSub), GridLength.Auto);
        PerfUi.AddRow(_gpuPanel, block, new GridLength(1, GridUnitType.Star), 100);
        PerfUi.AddRow(_gpuPanel, lower, GridLength.Auto);
    }

    void UpdateGpu(Snapshot s)
    {
        _gpuNote.Visibility = s.GpuPct.HasValue ? Visibility.Collapsed : Visibility.Visible;
        _gpuStats.Set("Utilisation", s.GpuPct.HasValue ? Fmt.Pct(s.GpuPct) : "unavailable");
        _gpuStats.Set("Dedicated memory", s.GpuMemUsed.HasValue ? Fmt.Bytes(s.GpuMemUsed) : "unavailable",
                      "Dedicated GPU memory in use, all adapters");
        _gpuStats.Set("Temperature", Fmt.Temp(s.GpuTempC));
        _gpuStats.Set("Hot spot", Fmt.Temp(s.GpuHotspotC));
        _gpuStats.Set("Power", Fmt.Watts(s.GpuWatts));
        if (string.IsNullOrEmpty(_gpuSub.Text)) _gpuSub.Text = GpuName(s);

        bool any = _gpuView.Count > 0;
        _gpuGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _gpuSensorNote.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any) _gpuSensorNote.Text = "No GPU sensors are readable. Sensors: " + Sensors.Status;
    }

    static string GpuName(Snapshot s)
    {
        var names = s?.Sensors.Where(r => r.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase))
                              .Select(r => r.Hardware).Distinct().ToList() ?? new List<string>();
        if (names.Count == 0) names = PerfHwInfo.Gpus;
        return string.Join(" · ", names);
    }

    // ================================================================ thermals

    void BuildThermPanel()
    {
        _thermStatus = PerfUi.Wrapped();
        _thermStatus.Margin = new Thickness(0, 0, 0, 8);

        _filterBox = new TextBox { Width = 200, Margin = new Thickness(6, 0, 14, 0), ToolTip = "Match hardware, type or sensor name" };
        _filterBox.TextChanged += (_, _) => _thermView.Refresh();
        _typeBox = new ComboBox { Width = 130, Margin = new Thickness(6, 0, 0, 0) };
        foreach (var t in new[] { "All", "Temperature", "Fan", "Power", "Clock", "Voltage", "Load" }) _typeBox.Items.Add(t);
        _typeBox.SelectedIndex = 0;
        _typeBox.SelectionChanged += (_, _) => _thermView.Refresh();
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        filters.Children.Add(Ui.Text("Filter", "Label"));
        filters.Children.Add(_filterBox);
        filters.Children.Add(Ui.Text("Type", "Label"));
        filters.Children.Add(_typeBox);

        _thermGrid = SensorGrid(_thermView, true);

        _thermWhy = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var why = new StackPanel();
        why.Children.Add(Ui.Text("No sensor readings", "H2"));
        why.Children.Add(_thermWhy);
        var open = Ui.Button("Open Settings", (_, _) => Nav.Go("settings"), "Accent");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        why.Children.Add(open);
        _thermEmpty = new Border
        {
            Child = why, Style = (Style)Application.Current.FindResource("HintBox"),
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
        };

        var tableArea = new Grid();
        tableArea.Children.Add(_thermGrid);
        tableArea.Children.Add(_thermEmpty);

        _thermPanel = new Grid();
        PerfUi.AddRow(_thermPanel, PerfUi.Title("Thermals", out _thermTitle, out _thermSub), GridLength.Auto);
        PerfUi.AddRow(_thermPanel, _thermStatus, GridLength.Auto);
        PerfUi.AddRow(_thermPanel, filters, GridLength.Auto);
        PerfUi.AddRow(_thermPanel, tableArea, new GridLength(1, GridUnitType.Star), 120);
    }

    static DataGrid SensorGrid(ListCollectionView view, bool withHardware)
    {
        var g = new DataGrid { ItemsSource = view, AutoGenerateColumns = false, IsReadOnly = true };
        if (withHardware) g.Columns.Add(Col("Hardware", nameof(PerfSensorRow.Hardware), null, new DataGridLength(1.3, DataGridLengthUnitType.Star), false));
        g.Columns.Add(Col("Type", nameof(PerfSensorRow.Type), null, new DataGridLength(95), false));
        g.Columns.Add(Col("Name", nameof(PerfSensorRow.Name), null, new DataGridLength(1, DataGridLengthUnitType.Star), false));
        g.Columns.Add(Col("Value", nameof(PerfSensorRow.ValueText), nameof(PerfSensorRow.ValueNum), new DataGridLength(95), true));
        g.Columns.Add(Col("Min", nameof(PerfSensorRow.MinText), nameof(PerfSensorRow.MinNum), new DataGridLength(90), true));
        g.Columns.Add(Col("Max", nameof(PerfSensorRow.MaxText), nameof(PerfSensorRow.MaxNum), new DataGridLength(90), true));
        foreach (var c in g.Columns) c.MinWidth = c.Width.IsStar ? (c.Header as string == "Hardware" ? 170 : 150) : c.Width.Value;
        return g;
    }

    static DataGridTextColumn Col(string header, string path, string sortPath, DataGridLength width, bool numeric)
    {
        var c = new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width };
        if (sortPath != null) c.SortMemberPath = sortPath;
        if (numeric) c.ElementStyle = (Style)Application.Current.FindResource("NumCell");
        return c;
    }

    bool ThermFilter(object o)
    {
        if (o is not PerfSensorRow r) return false;
        if (_typeBox?.SelectedItem is string t && t != "All" && r.Type != t) return false;
        string f = _filterBox?.Text?.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return r.Hardware.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Type.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Update the sensor rows in place: new readings added, vanished ones removed, the rest refreshed.</summary>
    void UpdateSensorRows(Snapshot s)
    {
        var list = s.Sensors;
        var seen = new HashSet<string>();
        foreach (var r in list)
        {
            if (string.IsNullOrEmpty(r.Id) || !seen.Add(r.Id)) continue;
            if (_sensorById.TryGetValue(r.Id, out var row)) row.Update(r);
            else
            {
                row = new PerfSensorRow(r);
                _sensorById[r.Id] = row;
                _sensorRows.Add(row);
            }
        }
        if (_sensorRows.Count > seen.Count)
        {
            for (int i = _sensorRows.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(_sensorRows[i].Id)) continue;
                _sensorById.Remove(_sensorRows[i].Id);
                _sensorRows.RemoveAt(i);
            }
        }
    }

    void UpdateTherm(Snapshot s)
    {
        int n = s.Sensors.Count;
        _thermSub.Text = n > 0 ? $"{n} readings · {_thermView.Count} shown" : "";
        _thermStatus.Text = "Sensors: " + Sensors.Status + "   ·   Values from LibreHardwareMonitor. Min and Max are since Winnow started.";
        bool empty = n == 0;
        _thermEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _thermGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            long now = Environment.TickCount64;
            if (now - _pawnCheckedAt > 5000)
            {
                _pawnCheckedAt = now;
                _pawnInstalled = Sensors.IsPawnIoInstalled();
            }
            _thermWhy.Text = WhyNoSensors();
        }
    }

    string WhyNoSensors()
    {
        if (!Settings.Current.SensorsEnabled)
            return "Hardware sensors are turned off in Settings › Hardware sensors.";
        var lines = new List<string> { "Sensor state: " + Sensors.Status + "." };
        if (!Elevation.IsAdmin)
            lines.Add("Winnow is not running as administrator, so most sensors cannot be read. Restart Winnow and accept the administrator prompt.");
        if (!_pawnInstalled)
            lines.Add("The PawnIO driver is not installed, so CPU temperatures and power cannot be read. It can be installed from Settings › Hardware sensors.");
        if (Sensors.Running && Elevation.IsAdmin && _pawnInstalled)
            lines.Add("The sensor reader is running but has not returned any readings yet.");
        return string.Join("\n\n", lines);
    }

    // ================================================================ shared

    static double MemPct(Snapshot h) => h.MemTotal > 0 ? h.MemLoadPct : double.NaN;

    static DiskSample FindDisk(Snapshot h, string name)
    {
        if (h == null) return null;
        foreach (var d in h.Disks) if (d.Name == name) return d;
        return null;
    }

    static NetSample FindNet(Snapshot h, string name)
    {
        if (h == null) return null;
        foreach (var n in h.Nets) if (n.Name == name) return n;
        return null;
    }

    static string DiskTitle(DiskSample d) =>
        "Disk " + d.Index + (string.IsNullOrWhiteSpace(d.Letters) ? "" : " (" + d.Letters.Trim() + ")");
}
