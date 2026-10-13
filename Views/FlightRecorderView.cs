using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Winnow.Controls;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

/// <summary>
/// Flight Recorder, after TMOG's: the same seven telemetry panels, recorded,
/// saved, scrubbed and replayed. "Replay the slow hour."
///
/// Live follows the newest sample in Sampler.History. Pause (or any scrub)
/// freezes the view on a copy of the buffer while recording carries on behind
/// it. A saved .winnowtrace opens in the same view.
/// </summary>
public sealed class FlightRecorderView : UserControl, IView
{
    enum RecMode { Live, Paused, File }

    RecMode _mode = RecMode.Live;
    List<Snapshot> _data = new();
    int _cursor;
    double _netPeak;
    bool _syncing;
    TraceData _file;

    readonly Grid _root = new();
    readonly RecorderStrip _strip = new();
    readonly Slider _slider = new() { Minimum = 0, Maximum = 0, SmallChange = 1, LargeChange = 60, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(70, 6, 70, 0) };
    readonly ToggleButton _live = new() { Content = "● Live", IsChecked = true, ToolTip = "Follow the newest sample. Untick to freeze the view; recording carries on." };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    readonly TextBlock _modeText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    readonly TextBlock _hiText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(10, 0, 6, 0) };
    readonly StackPanel _hiBox = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    readonly Button _backBtn, _saveBtn, _openBtn, _csvBtn;
    readonly UniformGrid _panels = new() { Columns = 7 };
    readonly RecorderCard _cpu, _mem, _disk, _net, _gpu, _therm, _top;

    readonly ObservableCollection<RecorderProcRow> _rows = new();
    readonly Dictionary<string, RecorderProcRow> _rowByKey = new();
    readonly ListCollectionView _view;
    readonly DataGrid _grid;

    const double LaneHeight = 20;

    public FlightRecorderView()
    {
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
        _modeText.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        _hiText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // header
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // toolbar
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // strip
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // panels
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 150 }); // processes
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // note

        // ---- header
        var header = Ui.Header("Flight Recorder",
            "Replay the slow hour. Everything Winnow measured, kept in memory and scrubbable sample by sample. " +
            "Drag across the strip, use the slider or the arrow keys, and click a process to trace it.");
        Add(header, 0);

        // ---- toolbar
        var transport = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _live.Checked += (_, _) => { if (!_syncing) SetMode(RecMode.Live); };
        _live.Unchecked += (_, _) => { if (!_syncing) SetMode(RecMode.Paused); };
        transport.Children.Add(_live);
        transport.Children.Add(Ui.Button("◀", (_, _) => Step(-1), null, "Previous sample (Left arrow; Shift for 10)"));
        transport.Children.Add(Ui.Button("▶", (_, _) => Step(+1), null, "Next sample (Right arrow; Shift for 10)"));
        transport.Children.Add(_time);

        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var find = Ui.Text("Find the spike:", "Label");
        find.Margin = new Thickness(0, 0, 8, 0);
        tools.Children.Add(find);
        tools.Children.Add(Ui.Button("Jump to CPU peak", (_, _) => JumpToPeak(0, "CPU"), "Ghost"));
        tools.Children.Add(Ui.Button("Jump to disk peak", (_, _) => JumpToPeak(2, "disk"), "Ghost"));
        tools.Children.Add(Ui.Button("Jump to memory peak", (_, _) => JumpToPeak(1, "memory"), "Ghost"));
        tools.Children.Add(Ui.Button("Jump to hottest", (_, _) => JumpToPeak(-1, "temperature"), "Ghost"));
        var gap = new Border { Width = 14 };
        tools.Children.Add(gap);
        _saveBtn = Ui.Button("Save trace…", (_, _) => Ui.Run(SaveTrace, "Saving the trace"), "Accent", "Save the buffer (or the file being viewed) as a .winnowtrace file");
        _openBtn = Ui.Button("Open trace…", (_, _) => Ui.Run(OpenTrace, "Opening the trace"), null, "Open a saved .winnowtrace file");
        _csvBtn = Ui.Button("Export CSV…", (_, _) => Ui.Run(ExportCsv, "CSV export"), null, "System metrics per sample as a spreadsheet");
        _backBtn = Ui.Button("Back to live", (_, _) => BackToLive(), "Accent");
        _backBtn.Visibility = Visibility.Collapsed;
        tools.Children.Add(_saveBtn);
        tools.Children.Add(_openBtn);
        tools.Children.Add(_csvBtn);
        tools.Children.Add(_backBtn);

        var bars = new StackPanel();
        bars.Children.Add(transport);
        bars.Children.Add(tools);
        Add(bars, 1);

        // ---- the strip
        _strip.Height = RecorderStrip.LaneCount * LaneHeight + RecorderStrip.AxisHeight;
        _strip.CursorMoved += MoveCursor;
        _slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            MoveCursor((int)Math.Round(e.NewValue));
        };

        var stripHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var clearHi = Ui.Button("Clear", (_, _) => ClearHighlight(), "Ghost", "Stop tracing the selected process");
        clearHi.Padding = new Thickness(6, 1, 6, 1);
        var hiDot = new TextBlock { Text = "■", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        hiDot.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _hiBox.Children.Add(hiDot);
        _hiBox.Children.Add(_hiText);
        _hiBox.Children.Add(clearHi);
        DockPanel.SetDock(_hiBox, Dock.Right);
        stripHead.Children.Add(_hiBox);
        stripHead.Children.Add(_modeText);

        var stripBox = new StackPanel();
        stripBox.Children.Add(stripHead);
        stripBox.Children.Add(_strip);
        stripBox.Children.Add(_slider);
        var stripCard = Ui.Card(stripBox, 10);
        stripCard.Margin = new Thickness(0, 0, 0, 10);
        Add(stripCard, 2);

        // ---- the seven panels
        _cpu = new RecorderCard("CPU", "Graph1");
        _mem = new RecorderCard("Memory", "Graph2");
        _disk = new RecorderCard("Disk", "Graph3");
        _net = new RecorderCard("Network", "Graph4");
        _gpu = new RecorderCard("GPU", "Good");
        _therm = new RecorderCard("Thermals / power", "Warn");
        _therm.Bar.LevelColors = true;
        _therm.Bar.WarnAt = 0.80;
        _therm.Bar.BadAt = 0.90;
        _top = new RecorderCard("Top processes", "Accent", meter: false);
        foreach (var c in new[] { _cpu, _mem, _disk, _net, _gpu, _therm, _top }) _panels.Children.Add(c.Root);
        _panels.Margin = new Thickness(0, 0, -8, 2);
        Add(_panels, 3);

        // ---- processes at the cursor
        _view = new ListCollectionView(_rows);
        _view.SortDescriptions.Add(new SortDescription(nameof(RecorderProcRow.Cpu), ListSortDirection.Descending));
        _view.IsLiveSorting = true;
        foreach (var p in new[] { "Name", "Pid", "Cpu", "Mem", "Disk", "Net", "Gpu", "Watts" }) _view.LiveSortingProperties.Add(p);

        _grid = new DataGrid { ItemsSource = _view, SelectionMode = DataGridSelectionMode.Single };
        _grid.Columns.Add(Col("Name", "Name", null, 0, false));
        _grid.Columns.Add(Col("PID", "Pid", null, 64, true));
        var cpuCol = Col("CPU", "CpuText", "Cpu", 70, true);
        cpuCol.SortDirection = ListSortDirection.Descending;
        _grid.Columns.Add(cpuCol);
        _grid.Columns.Add(Col("Memory", "MemText", "Mem", 86, true));
        _grid.Columns.Add(Col("Disk", "DiskText", "Disk", 86, true));
        _grid.Columns.Add(Col("Network", "NetText", "Net", 86, true));
        _grid.Columns.Add(Col("GPU", "GpuText", "Gpu", 64, true));
        _grid.Columns.Add(Col("Power (est.)", "WattsText", "Watts", 90, true));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is RecorderProcRow r) Highlight(r);
        };
        var copyMenu = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy row" };
        copyItem.Click += (_, _) => { if (_grid.SelectedItem is RecorderProcRow r) Ui.CopyToClipboard(r.ToText()); };
        var traceItem = new MenuItem { Header = "Trace this process on the strip" };
        traceItem.Click += (_, _) => { if (_grid.SelectedItem is RecorderProcRow r) Highlight(r); };
        copyMenu.Items.Add(traceItem);
        copyMenu.Items.Add(copyItem);
        _grid.ContextMenu = copyMenu;

        var procHead = Ui.Text("Processes at the cursor - the busiest ~40 are kept per sample. Gaps in a traced line mean the process " +
                               "was not running or was not among those kept.", "Sub");
        procHead.TextWrapping = TextWrapping.Wrap;
        procHead.Margin = new Thickness(0, 0, 0, 6);
        var procBox = new DockPanel();
        DockPanel.SetDock(procHead, Dock.Top);
        procBox.Children.Add(procHead);
        procBox.Children.Add(_grid);
        Add(procBox, 4);

        // ---- note
        var noteRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var settingsBtn = Ui.Button("Recorder length…", (_, _) => Nav.Go("settings"), "Ghost", "Set how many minutes the recorder keeps");
        DockPanel.SetDock(settingsBtn, Dock.Right);
        noteRow.Children.Add(settingsBtn);
        noteRow.Children.Add(_note);
        Add(noteRow, 5);

        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        SizeChanged += (_, e) =>
        {
            // Fill the page when it is tall enough; scroll (rather than squash the table) when it is not.
            _root.Height = Math.Max(700, e.NewSize.Height);
            _panels.Columns = e.NewSize.Width >= 1000 ? 7 : 4;
        };

        UpdateModeText();
        UpdateNote();
        ShowAt(-1);
    }

    void Add(UIElement el, int row)
    {
        Grid.SetRow(el, row);
        _root.Children.Add(el);
    }

    static DataGridTextColumn Col(string header, string path, string sortPath, double width, bool numeric)
    {
        var c = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
        };
        if (width <= 0) c.MinWidth = 140;
        if (sortPath != null) c.SortMemberPath = sortPath;
        if (numeric) c.ElementStyle = (Style)Application.Current.FindResource("NumCell");
        return c;
    }

    // ------------------------------------------------------------------ IView

    public void OnShown()
    {
        Sampler.Tick -= OnTick;
        Sampler.Tick += OnTick;
        UpdateNote();
        if (_mode == RecMode.Live) PullLive();
        else UpdateTimeLabel();
    }

    public void OnHidden() => Sampler.Tick -= OnTick;

    void OnTick(Snapshot s)
    {
        if (_mode == RecMode.Live) PullLive();
        else if (_mode == RecMode.Paused) { UpdateTimeLabel(); UpdateModeText(); }
    }

    // ------------------------------------------------------------------ modes

    void SetMode(RecMode m)
    {
        _mode = m;
        _syncing = true;
        _live.IsChecked = m == RecMode.Live;
        _syncing = false;
        _live.IsEnabled = m != RecMode.File;
        _backBtn.Visibility = m == RecMode.File ? Visibility.Visible : Visibility.Collapsed;
        if (m == RecMode.Live) PullLive();
        UpdateModeText();
        UpdateTimeLabel();
    }

    void BackToLive()
    {
        _file = null;
        ClearHighlight();
        SetMode(RecMode.Live);
        Status.Set("Back to the live recorder");
    }

    void PullLive()
    {
        _data = Sampler.History.ToList();
        _cursor = Math.Max(0, _data.Count - 1);
        Apply();
        UpdateModeText();
    }

    /// <summary>Push _data and _cursor to the strip, slider, panels and table.</summary>
    void Apply()
    {
        _netPeak = 0;
        foreach (var s in _data)
        {
            if (s == null) continue;
            double n = s.NetRecvBps + s.NetSendBps;
            if (!double.IsNaN(n) && n > _netPeak) _netPeak = n;
        }
        if (_data.Count == 0) _cursor = -1;
        else _cursor = Math.Clamp(_cursor, 0, _data.Count - 1);

        _strip.Data = _data;
        _strip.Position = Math.Max(0, _cursor);
        _syncing = true;
        _slider.Maximum = Math.Max(0, _data.Count - 1);
        _slider.Value = Math.Max(0, _cursor);
        _syncing = false;
        ShowAt(_cursor);
    }

    /// <summary>Any user scrub. Scrubbing while live freezes the view first.</summary>
    void MoveCursor(int i)
    {
        if (_data.Count == 0) return;
        i = Math.Clamp(i, 0, _data.Count - 1);
        if (_mode == RecMode.Live)
        {
            if (i == _data.Count - 1) return;   // still at the newest sample: stay live
            SetMode(RecMode.Paused);
        }
        _cursor = i;
        _strip.Position = i;
        _syncing = true;
        _slider.Value = i;
        _syncing = false;
        ShowAt(i);
    }

    void Step(int delta)
    {
        if (_data.Count == 0) return;
        if (_mode == RecMode.Live) SetMode(RecMode.Paused);
        MoveCursor(_cursor + delta);
    }

    // ------------------------------------------------------------------ highlight

    void Highlight(RecorderProcRow r)
    {
        _strip.SetHighlight(r.Pid, r.CreateTime, r.Name);
        _strip.Height = (RecorderStrip.LaneCount + 1) * LaneHeight + RecorderStrip.AxisHeight;
        _hiText.Text = $"Tracing {r.Name} (pid {r.Pid}) CPU";
        _hiBox.Visibility = Visibility.Visible;
    }

    void ClearHighlight()
    {
        _strip.ClearHighlight();
        _strip.Height = RecorderStrip.LaneCount * LaneHeight + RecorderStrip.AxisHeight;
        _hiBox.Visibility = Visibility.Collapsed;
        _grid.SelectedItem = null;
    }

    // ------------------------------------------------------------------ find the spike

    void JumpToPeak(int lane, string what)
    {
        if (_mode == RecMode.Live) SetMode(RecMode.Paused);
        int best = -1;
        double bv = double.NegativeInfinity;
        for (int i = 0; i < _data.Count; i++)
        {
            var s = _data[i];
            double v = lane < 0 ? Hottest(s) : RecorderStrip.Metric(s, lane);
            if (!double.IsNaN(v) && v > bv) { bv = v; best = i; }
        }
        if (best < 0)
        {
            Status.Set($"No {what} readings in this buffer.", StatusKind.Warn);
            return;
        }
        MoveCursor(best);
        string value = lane < 0 ? Fmt.Temp(bv) : RecorderStrip.MetricText(_data[best], lane);
        Status.Set($"Highest {what}: {value} at {_data[best].Time:HH:mm:ss}");
    }

    static double Hottest(Snapshot s)
    {
        if (s == null) return double.NaN;
        if (s.CpuTempC.HasValue && s.GpuTempC.HasValue) return Math.Max(s.CpuTempC.Value, s.GpuTempC.Value);
        if (s.CpuTempC.HasValue) return s.CpuTempC.Value;
        if (s.GpuTempC.HasValue) return s.GpuTempC.Value;
        return double.NaN;
    }

    // ------------------------------------------------------------------ the instant at the cursor

    void ShowAt(int i)
    {
        UpdateTimeLabel();
        Snapshot s = i >= 0 && i < _data.Count ? _data[i] : null;
        if (s == null)
        {
            foreach (var c in new[] { _cpu, _mem, _disk, _net, _gpu, _therm, _top }) c.Set(Fmt.Na, "\n", double.NaN);
            _rows.Clear();
            _rowByKey.Clear();
            return;
        }

        _cpu.Set(Fmt.Pct(s.CpuTotal), $"kernel {Fmt.Pct(s.CpuKernel)}\nclock {Fmt.Mhz(s.CpuMhz)}", s.CpuTotal / 100.0);
        _mem.Set(Fmt.Pct(s.MemLoadPct),
                 $"{Fmt.Bytes(s.MemUsed)} of {Fmt.Bytes(s.MemTotal)}\ncommit {Fmt.Bytes(s.CommitTotal)}",
                 s.MemTotal > 0 ? s.MemLoadPct / 100.0 : double.NaN);
        _disk.Set(Fmt.Pct(s.DiskActivePct) + (s.DiskActivePct.HasValue ? " active" : ""),
                  $"read {Fmt.Rate(s.DiskReadBps)}\nwrite {Fmt.Rate(s.DiskWriteBps)}",
                  s.DiskActivePct.HasValue ? s.DiskActivePct.Value / 100.0 : double.NaN);
        double net = s.NetRecvBps + s.NetSendBps;
        _net.Set(Fmt.Rate(net), $"↓ {Fmt.Rate(s.NetRecvBps)}\n↑ {Fmt.Rate(s.NetSendBps)}",
                 _netPeak > 0 ? net / _netPeak : double.NaN);
        _gpu.Set(Fmt.Pct(s.GpuPct), $"VRAM {Fmt.Bytes(s.GpuMemUsed)}\ntemp {Fmt.Temp(s.GpuTempC)}",
                 s.GpuPct.HasValue ? s.GpuPct.Value / 100.0 : double.NaN);
        _therm.Set("CPU " + Fmt.Temp(s.CpuTempC),
                   $"GPU {Fmt.Temp(s.GpuTempC)}  hotspot {Fmt.Temp(s.GpuHotspotC)}\nCPU {Fmt.Watts(s.CpuWatts)} · GPU {Fmt.Watts(s.GpuWatts)}",
                   s.CpuTempC.HasValue ? s.CpuTempC.Value / 100.0 : double.NaN);

        var procs = s.Processes ?? new List<ProcSample>();
        var top = procs.OrderByDescending(p => p.Cpu).Take(3).ToList();
        _top.Set(top.Count > 0 ? top[0].Name : Fmt.Na,
                 top.Count > 0 ? string.Join("\n", top.Select(p => Fmt.Pct(p.Cpu, 1) + "  " + p.Name)) : "\n",
                 double.NaN);

        UpdateRows(procs);
    }

    void UpdateRows(List<ProcSample> procs)
    {
        var seen = new HashSet<string>();
        foreach (var p in procs)
        {
            if (p == null) continue;
            string key = p.Key;
            if (!seen.Add(key)) continue;
            if (_rowByKey.TryGetValue(key, out var row)) row.Update(p);
            else
            {
                row = new RecorderProcRow(p);
                _rowByKey[key] = row;
                _rows.Add(row);
            }
        }
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(_rows[i].Key)) continue;
            _rowByKey.Remove(_rows[i].Key);
            _rows.RemoveAt(i);
        }
    }

    void UpdateTimeLabel()
    {
        if (_cursor < 0 || _cursor >= _data.Count || _data[_cursor] == null)
        {
            _time.Text = _mode == RecMode.File ? "This trace has no samples." : "No samples yet - the recorder fills as Winnow runs.";
            return;
        }
        var t = _data[_cursor].Time;
        string when = t.Date == DateTime.Today ? t.ToString("HH:mm:ss") : t.ToString("ddd d MMM yyyy HH:mm:ss");
        string rel;
        if (_mode == RecMode.File)
        {
            var first = _data[0].Time;
            rel = "+" + Fmt.Duration(t >= first ? t - first : TimeSpan.Zero) + " into the trace";
        }
        else rel = Ago(DateTime.Now - t);
        _time.Text = $"{when}  ·  {rel}  ·  sample {_cursor + 1:N0} of {_data.Count:N0}";
    }

    static string Ago(TimeSpan d)
    {
        if (d.TotalSeconds < 1.5) return "now";
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0} s ago";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0.0} min ago";
        return $"{(int)d.TotalHours} h {d.Minutes} min ago";
    }

    void UpdateModeText()
    {
        switch (_mode)
        {
            case RecMode.Live:
                _modeText.Text = $"LIVE - following the newest sample · {_data.Count:N0} samples in the buffer ({SpanText(_data)})";
                _modeText.SetResourceReference(TextBlock.ForegroundProperty, "Good");
                break;
            case RecMode.Paused:
                _modeText.Text = $"PAUSED - the view is frozen on {_data.Count:N0} samples ({SpanText(_data)}); recording carries on. Tick Live to catch up.";
                _modeText.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
                break;
            default:
                var h = _file?.Header;
                string name = _file != null ? Path.GetFileName(_file.Path) : "";
                string extra = h == null ? "" :
                    $" · {(string.IsNullOrEmpty(h.Machine) ? Fmt.Na : h.Machine)} · {(string.IsNullOrEmpty(h.Os) ? Fmt.Na : h.Os)} · saved {h.Created:yyyy-MM-dd HH:mm}";
                _modeText.Text = $"Viewing file: {name}{extra} · {_data.Count:N0} samples ({SpanText(_data)})";
                _modeText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                break;
        }
    }

    static string SpanText(List<Snapshot> d)
    {
        if (d.Count < 2 || d[0] == null || d[^1] == null) return "0 min";
        var span = d[^1].Time - d[0].Time;
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:0} s";
        if (span.TotalHours < 1) return $"{span.TotalMinutes:0} min";
        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    void UpdateNote()
    {
        _note.Text = $"The recorder buffer lives in memory only: it keeps the last {Settings.Current.RecorderMinutes} min " +
                     $"({Sampler.History.Capacity:N0} samples at the current update rate) and is gone when Winnow closes. " +
                     "Save a trace to keep it. The length is set in Settings.";
    }

    // ------------------------------------------------------------------ files

    async Task SaveTrace()
    {
        var data = _mode == RecMode.Live ? Sampler.History.ToList() : _data;
        if (data.Count == 0) { Status.Set("Nothing recorded yet.", StatusKind.Warn); return; }
        string path = Ui.SaveFile("Winnow " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + TraceFile.Extension, TraceFile.Filter);
        if (path == null) return;
        await Ui.Busy(_saveBtn, async () => await Task.Run(() => TraceFile.Save(path, data)), "Saving…");
        Status.Set($"Saved {data.Count:N0} samples to {path}", StatusKind.Good);
    }

    async Task OpenTrace()
    {
        string path = Ui.OpenFile(TraceFile.Filter);
        if (path == null) return;
        TraceData t = null;
        try
        {
            await Ui.Busy(_openBtn, async () => { t = await Task.Run(() => TraceFile.Load(path)); }, "Opening…");
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
        {
            Ui.Error("Could not open that trace.\n\n" + ex.Message);
            return;
        }
        if (t == null || t.Samples.Count == 0)
        {
            Ui.Error("That trace has no samples in it.");
            return;
        }
        _file = t;
        ClearHighlight();
        _data = t.Samples;
        _cursor = _data.Count - 1;
        SetMode(RecMode.File);
        Apply();
        UpdateModeText();
        string warn = t.Truncated ? " The file ended early; everything before the cut is shown." :
                      t.SkippedLines > 0 ? $" {t.SkippedLines} unreadable line(s) skipped." : "";
        Status.Set($"Opened {Path.GetFileName(path)}: {t.Samples.Count:N0} samples.{warn}", warn.Length > 0 ? StatusKind.Warn : StatusKind.Good);
    }

    async Task ExportCsv()
    {
        var data = _mode == RecMode.Live ? Sampler.History.ToList() : _data;
        if (data.Count == 0) { Status.Set("Nothing recorded yet.", StatusKind.Warn); return; }
        string path = Ui.SaveFile("Winnow recorder " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".csv", "CSV file|*.csv");
        if (path == null) return;
        await Ui.Busy(_csvBtn, async () => await Task.Run(() => TraceFile.ExportCsv(path, data)), "Exporting…");
        Status.Set($"Exported {data.Count:N0} samples to {path}", StatusKind.Good);
    }
}

/// <summary>One of the seven telemetry panels.</summary>
internal sealed class RecorderCard
{
    public readonly Border Root;
    public readonly Meter Bar;
    readonly TextBlock _value, _sub;

    public RecorderCard(string title, string colorKey, bool meter = true)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(title, "Label"));
        _value = Ui.Text(Fmt.Na, "ValueSmall", 18);
        _value.TextTrimming = TextTrimming.CharacterEllipsis;
        _value.Margin = new Thickness(0, 2, 0, 2);
        _sub = Ui.Text("\n", "Sub", 11.5);
        _sub.TextWrapping = TextWrapping.NoWrap;
        _sub.TextTrimming = TextTrimming.CharacterEllipsis;
        Bar = new Meter { ColorKey = colorKey, Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        if (!meter) Bar.Visibility = Visibility.Collapsed;
        sp.Children.Add(_value);
        sp.Children.Add(_sub);
        sp.Children.Add(Bar);
        Root = Ui.Card(sp);
        Root.Padding = new Thickness(10, 8, 10, 8);
        Root.Margin = new Thickness(0, 0, 8, 8);
    }

    public void Set(string value, string sub, double meter01)
    {
        _value.Text = value;
        _sub.Text = sub;
        Bar.Value = double.IsNaN(meter01) ? double.NaN : Math.Clamp(meter01, 0, 1);
    }
}

/// <summary>A process row in the Flight Recorder table, updated in place as the cursor moves.</summary>
public sealed class RecorderProcRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    public string Key { get; }
    public int Pid { get; }
    public long CreateTime { get; }
    public string Name { get; }

    double _cpu, _mem, _disk, _net, _gpu, _watts;
    string _cpuText = "", _memText = "", _diskText = "", _netText = "", _gpuText = "", _wattsText = "";

    public double Cpu { get => _cpu; private set => Set(ref _cpu, value, nameof(Cpu)); }
    public double Mem { get => _mem; private set => Set(ref _mem, value, nameof(Mem)); }
    public double Disk { get => _disk; private set => Set(ref _disk, value, nameof(Disk)); }
    public double Net { get => _net; private set => Set(ref _net, value, nameof(Net)); }
    public double Gpu { get => _gpu; private set => Set(ref _gpu, value, nameof(Gpu)); }
    public double Watts { get => _watts; private set => Set(ref _watts, value, nameof(Watts)); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value, nameof(CpuText)); }
    public string MemText { get => _memText; private set => Set(ref _memText, value, nameof(MemText)); }
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value, nameof(DiskText)); }
    public string NetText { get => _netText; private set => Set(ref _netText, value, nameof(NetText)); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value, nameof(GpuText)); }
    public string WattsText { get => _wattsText; private set => Set(ref _wattsText, value, nameof(WattsText)); }

    public RecorderProcRow(ProcSample p)
    {
        Key = p.Key;
        Pid = p.Pid;
        CreateTime = p.CreateTime;
        Name = p.Name ?? "";
        Update(p);
    }

    public void Update(ProcSample p)
    {
        Cpu = p.Cpu;
        CpuText = Fmt.Pct(p.Cpu, 1);
        Mem = p.PrivateWorkingSet;
        MemText = Fmt.Bytes(p.PrivateWorkingSet);
        double disk = p.ReadBps + p.WriteBps;
        Disk = disk;
        DiskText = Fmt.Rate(disk);
        Net = p.NetBps ?? -1;
        NetText = Fmt.Rate(p.NetBps);
        Gpu = p.GpuPct ?? -1;
        GpuText = Fmt.Pct(p.GpuPct, 1);
        Watts = p.EstWatts ?? -1;
        WattsText = Fmt.Watts(p.EstWatts);
    }

    public string ToText() => $"{Name}\t{Pid}\t{CpuText}\t{MemText}\t{DiskText}\t{NetText}\t{GpuText}\t{WattsText}";

    void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// The overview strip: one lane per metric across the whole buffer, drawn in
/// OnRender. Long buffers are bucketed per pixel column keeping the maximum, so
/// a one-second spike in an hour still shows. NaN (no reading) leaves a gap.
/// Click or drag to move the cursor; arrow keys and the wheel step it.
/// </summary>
public sealed class RecorderStrip : FrameworkElement
{
    public const int LaneCount = 7;
    public const double AxisHeight = 16;
    const double LabelW = 70, ValueW = 70;

    public static readonly string[] LaneNames = { "CPU", "Memory", "Disk", "Network", "GPU", "CPU temp", "Power" };
    static readonly string[] LaneColors = { "Graph1", "Graph2", "Graph3", "Graph4", "Good", "Warn", "Bad" };

    List<Snapshot> _data = new();
    int _pos;
    int _hiPid = -1;
    long _hiCreate;
    string _hiName = "";

    readonly Pen[] _lanePens = new Pen[LaneCount];
    Pen _gridPen, _cursorPen, _hiPen;
    Brush _bg, _textBrush, _dimBrush, _cursorBand;
    Typeface _face, _numFace;

    /// <summary>Raised when the user moves the cursor (click, drag, keys, wheel).</summary>
    public event Action<int> CursorMoved;

    public RecorderStrip()
    {
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        Cursor = Cursors.SizeWE;
        ToolTip = "Click or drag to scrub. Arrow keys or the mouse wheel step one sample (Shift: 10).";
        BuildPens();
        Loaded += (_, _) => { Palette.Changed += OnPalette; BuildPens(); InvalidateVisual(); };
        Unloaded += (_, _) => { Palette.Changed -= OnPalette; };
    }

    void OnPalette()
    {
        BuildPens();
        InvalidateVisual();
    }

    public List<Snapshot> Data
    {
        get => _data;
        set { _data = value ?? new List<Snapshot>(); InvalidateVisual(); }
    }

    /// <summary>Cursor index into Data.</summary>
    public int Position
    {
        get => _pos;
        set { _pos = value; InvalidateVisual(); }
    }

    public void SetHighlight(int pid, long createTime, string name)
    {
        _hiPid = pid;
        _hiCreate = createTime;
        _hiName = name ?? "";
        InvalidateVisual();
    }

    public void ClearHighlight()
    {
        _hiPid = -1;
        _hiCreate = 0;
        _hiName = "";
        InvalidateVisual();
    }

    // ------------------------------------------------------------ metrics

    /// <summary>The value a lane plots for a snapshot, or NaN for no reading.</summary>
    public static double Metric(Snapshot s, int lane)
    {
        if (s == null) return double.NaN;
        switch (lane)
        {
            case 0: return s.CpuTotal;
            case 1: return s.MemTotal > 0 ? s.MemLoadPct : double.NaN;
            case 2: return s.DiskActivePct ?? double.NaN;
            case 3: return s.NetRecvBps + s.NetSendBps;
            case 4: return s.GpuPct ?? double.NaN;
            case 5: return s.CpuTempC ?? double.NaN;
            case 6: return s.CpuWatts.HasValue || s.GpuWatts.HasValue ? (s.CpuWatts ?? 0) + (s.GpuWatts ?? 0) : double.NaN;
            default: return double.NaN;
        }
    }

    public static string MetricText(Snapshot s, int lane)
    {
        double v = Metric(s, lane);
        if (double.IsNaN(v)) return Fmt.Na;
        switch (lane)
        {
            case 3: return Fmt.Rate(v);
            case 5: return Fmt.Temp(v);
            case 6: return Fmt.Watts(v);
            default: return Fmt.Pct(v);
        }
    }

    double HighlightValue(Snapshot s)
    {
        if (s?.Processes == null) return double.NaN;
        foreach (var p in s.Processes)
            if (p != null && p.Pid == _hiPid && p.CreateTime == _hiCreate) return p.Cpu;
        return double.NaN;
    }

    double LaneValue(Snapshot s, int lane) => lane == LaneCount ? HighlightValue(s) : Metric(s, lane);

    double LaneMax(int lane)
    {
        if (lane == 0 || lane == 1 || lane == 2 || lane == 4) return 100;
        double m = 0;
        foreach (var s in _data)
        {
            double v = LaneValue(s, lane);
            if (!double.IsNaN(v) && v > m) m = v;
        }
        if (lane == 5) return Math.Max(100, m);
        if (lane == LaneCount) return Math.Max(5, Graph.NiceCeiling(m * 1.1));
        if (lane == 3) return Graph.NiceCeiling(Math.Max(m * 1.05, 1024));
        return Graph.NiceCeiling(Math.Max(m * 1.05, 1));
    }

    string LaneScale(int lane, double max)
    {
        if (lane == 3) return "max " + Fmt.Rate(max);
        if (lane == 6) return "max " + Fmt.Watts(max);
        if (lane == LaneCount) return "max " + Fmt.Pct(max);
        return "";
    }

    // ------------------------------------------------------------ drawing

    static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }

    void BuildPens()
    {
        var p = Palette.Current;
        for (int i = 0; i < LaneCount; i++)
            _lanePens[i] = Frozen(new Pen(Palette.Brush(FrameClock.ColorFor(LaneColors[i])), 1.3) { LineJoin = PenLineJoin.Round });
        _gridPen = Frozen(new Pen(Palette.Brush(p.Grid), 1));
        _cursorPen = Frozen(new Pen(Palette.Brush(p.Accent), 1.5));
        _hiPen = Frozen(new Pen(Palette.Brush(p.Text), 1.4) { LineJoin = PenLineJoin.Round });
        _bg = Palette.Brush(p.Code);
        _textBrush = Palette.Brush(p.Text);
        _dimBrush = Palette.Brush(p.Dim);
        _cursorBand = Palette.Brush(p.Accent, 0x30);
        var ui = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var num = TryFindResource("NumFont") as FontFamily ?? ui;
        _face = new Typeface(ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _numFace = new Typeface(num, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    static double X(double i, int n, double plotX, double plotW) => n <= 1 ? plotX + plotW : plotX + i * plotW / (n - 1);

    FormattedText Text(string s, Brush b, double size, Typeface face, double dpi) =>
        new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, b, dpi);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < LabelW + ValueW + 20 || h < AxisHeight + 20) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(_bg, null, new Rect(0, 0, w, h));

        int lanes = LaneCount + (_hiPid >= 0 ? 1 : 0);
        double plotX = LabelW, plotW = Math.Max(10, w - LabelW - ValueW);
        double laneH = (h - AxisHeight) / lanes;
        int n = _data.Count;
        Snapshot cur = n > 0 ? _data[Math.Clamp(_pos, 0, n - 1)] : null;

        for (int lane = 0; lane < lanes; lane++)
        {
            double top = lane * laneH;
            if (lane > 0)
            {
                double y = Math.Round(top) + 0.5;
                dc.DrawLine(_gridPen, new Point(0, y), new Point(w, y));
            }
            bool hi = lane == LaneCount;
            Pen pen = hi ? _hiPen : _lanePens[lane];

            string name = hi ? _hiName : LaneNames[lane];
            if (name.Length > 11) name = name.Substring(0, 10) + "…";
            var label = Text(name, pen.Brush, 11, _face, dpi);
            dc.DrawText(label, new Point(6, top + (laneH - label.Height) / 2));

            string val;
            if (cur == null) val = Fmt.Na;
            else if (hi)
            {
                double hv = HighlightValue(cur);
                val = double.IsNaN(hv) ? Fmt.Na : Fmt.Pct(hv, 1);
            }
            else val = MetricText(cur, lane);
            var vt = Text(val, _textBrush, 11, _numFace, dpi);
            dc.DrawText(vt, new Point(w - 6 - vt.Width, top + (laneH - vt.Height) / 2));

            if (n == 0) continue;
            double max = LaneMax(lane);
            if (max <= 0) max = 1;
            string scale = LaneScale(lane, max);
            if (scale.Length > 0 && laneH >= 16)
            {
                var st = Text(scale, _dimBrush, 9, _numFace, dpi);
                dc.DrawText(st, new Point(plotX + plotW - st.Width - 2, top + 1));
            }

            var geo = new StreamGeometry();
            bool any = false;
            using (var ctx = geo.Open())
            {
                int cols = (int)Math.Min(n, Math.Max(1, Math.Floor(plotW)));
                bool open = false;
                for (int c = 0; c < cols; c++)
                {
                    int i0 = (int)((long)c * n / cols);
                    int i1 = (int)((long)(c + 1) * n / cols);
                    if (i1 <= i0) i1 = i0 + 1;
                    double v = double.NaN;
                    for (int i = i0; i < i1 && i < n; i++)
                    {
                        double x = LaneValue(_data[i], lane);
                        if (!double.IsNaN(x) && (double.IsNaN(v) || x > v)) v = x;
                    }
                    if (double.IsNaN(v)) { open = false; continue; }
                    double px = X((i0 + i1 - 1) / 2.0, n, plotX, plotW);
                    double py = top + laneH - 2 - Math.Clamp(v / max, 0, 1) * Math.Max(1, laneH - 5);
                    var pt = new Point(px, py);
                    if (!open)
                    {
                        ctx.BeginFigure(pt, false, false);
                        ctx.LineTo(new Point(px + 0.6, py), true, false);
                        open = true;
                        any = true;
                    }
                    else ctx.LineTo(pt, true, true);
                }
            }
            if (any)
            {
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
        }

        // time axis
        double axisY = h - AxisHeight;
        dc.DrawLine(_gridPen, new Point(0, Math.Round(axisY) + 0.5), new Point(w, Math.Round(axisY) + 0.5));
        if (n > 0 && _data[0] != null && _data[n - 1] != null)
        {
            var t0 = _data[0].Time;
            var t1 = _data[n - 1].Time;
            string fmt = (t1 - t0).TotalMinutes < 10 ? "HH:mm:ss" : "HH:mm";
            int ticks = n >= 5 ? 4 : n - 1;
            for (int k = 0; k <= ticks; k++)
            {
                int i = ticks == 0 ? 0 : (int)Math.Round((n - 1) * k / (double)ticks);
                if (_data[i] == null) continue;
                var ft = Text(_data[i].Time.ToString(fmt), _dimBrush, 10, _numFace, dpi);
                double x = X(i, n, plotX, plotW);
                x -= k == 0 ? 0 : k == ticks ? ft.Width : ft.Width / 2;
                dc.DrawText(ft, new Point(Math.Max(0, x), axisY + 2));
            }

            // cursor
            double cx = Math.Round(X(Math.Clamp(_pos, 0, n - 1), n, plotX, plotW)) + 0.5;
            dc.DrawRectangle(_cursorBand, null, new Rect(cx - 3, 0, 6, axisY));
            dc.DrawLine(_cursorPen, new Point(cx, 0), new Point(cx, axisY));
        }
        else
        {
            var ft = Text("No samples yet - the recorder fills as Winnow runs.", _dimBrush, 12, _face, dpi);
            dc.DrawText(ft, new Point(plotX + (plotW - ft.Width) / 2, (axisY - ft.Height) / 2));
        }
    }

    // ------------------------------------------------------------ input

    void Pick(double x)
    {
        int n = _data.Count;
        if (n == 0) return;
        double plotW = Math.Max(10, ActualWidth - LabelW - ValueW);
        int i = n <= 1 ? 0 : (int)Math.Round((x - LabelW) / plotW * (n - 1));
        CursorMoved?.Invoke(Math.Clamp(i, 0, n - 1));
    }

    void StepBy(int delta)
    {
        int n = _data.Count;
        if (n == 0) return;
        CursorMoved?.Invoke(Math.Clamp(_pos + delta, 0, n - 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        Pick(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        StepBy(e.Delta > 0 ? -step : step);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: StepBy(-step); e.Handled = true; break;
            case Key.Right: StepBy(step); e.Handled = true; break;
            case Key.Home: if (_data.Count > 0) CursorMoved?.Invoke(0); e.Handled = true; break;
            case Key.End: if (_data.Count > 0) CursorMoved?.Invoke(_data.Count - 1); e.Handled = true; break;
        }
    }
}
