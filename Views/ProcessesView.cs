using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Winnow.Controls;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

/// <summary>
/// TMOG's Processes page: a live, sortable, filterable process list with an
/// optional parent/child tree, heat-tinted load columns, a details pane with
/// the selected process's own CPU history, and the usual actions. Rows are
/// keyed by pid + start time, and every action re-verifies the start time, so
/// a recycled PID can never redirect a kill to the wrong program.
/// </summary>
public sealed class ProcessesView : UserControl, IView
{
    readonly Dictionary<string, ProcRow> _rows = new();
    readonly ObservableCollection<ProcRow> _shown = new();
    readonly HashSet<string> _collapsed = new();

    readonly DataGrid _grid;
    readonly TextBox _filter;
    readonly TextBlock _filterHint;
    readonly ToggleButton _treeBtn, _detailsBtn;
    readonly TextBlock _sumCpu, _sumMem, _sumCount, _netHint;
    readonly RowDefinition _detailsRow;
    readonly GridSplitter _splitter;
    readonly Border _detailsCard;
    readonly ContextMenu _menu = new();

    // details pane
    readonly Grid _dBody;
    readonly TextBlock _dEmpty, _dTitle, _dCompany, _dUser, _dStarted, _dPriority, _dParent, _dSession,
                       _dThreads, _dHandles, _dPrivate, _dWorking, _dCommit, _dServices, _dGraphCaption;
    readonly TextBox _dPath, _dCmd;
    readonly Graph _cpuGraph;

    readonly Style _numStyle, _textStyle, _heatCellStyle;

    string _sortKey = "cpu";
    bool _sortDesc = true;
    bool _syncing;
    ProcRow _selected;
    int _pendingPid;
    bool _subscribed;
    double _peak;

    static readonly string[] SortKeys = { "name", "pid", "cpu", "mem", "disk", "net", "gpu", "power", "threads", "handles", "user", "desc", "started" };

    public ProcessesView()
    {
        _numStyle = (Style)Application.Current.FindResource("NumCell");
        _textStyle = new Style(typeof(TextBlock));
        _textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        _heatCellStyle = new Style(typeof(DataGridCell), Application.Current.TryFindResource(typeof(DataGridCell)) as Style);
        _heatCellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));

        string savedSort = Settings.Current.Get("proc.sort", "cpu");
        if (SortKeys.Contains(savedSort)) _sortKey = savedSort;
        _sortDesc = Settings.Current.Get("proc.sortdesc", _sortKey == "cpu" ? "1" : DefaultDesc(_sortKey) ? "1" : "0") == "1";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // toolbar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 }); // grid
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // splitter
        _detailsRow = new RowDefinition { Height = new GridLength(210) };
        root.RowDefinitions.Add(_detailsRow);                                                         // details

        // ---- header + live summary
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Header("Processes",
            "Every running process, live. Click a column to sort, switch on Tree to see which process started which, " +
            "and right-click to act. Rows follow pid and start time, so a recycled PID is never mistaken for the old process."));

        var sum = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 14) };
        var sumLine = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _sumCpu = SumPair(sumLine, "CPU");
        _sumMem = SumPair(sumLine, "Memory");
        _sumCount = SumPair(sumLine, "Processes");
        sum.Children.Add(sumLine);
        _netHint = Ui.Text("", "Sub");
        _netHint.HorizontalAlignment = HorizontalAlignment.Right;
        _netHint.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        _netHint.Visibility = Visibility.Collapsed;
        _netHint.Margin = new Thickness(0, 3, 0, 0);
        sum.Children.Add(_netHint);
        Grid.SetColumn(sum, 1);
        head.Children.Add(sum);
        root.Children.Add(head);

        // ---- toolbar
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var filterHost = new Grid { Width = 340 };
        _filter = new TextBox { ToolTip = "Filter by name, PID, user, description or command line (Ctrl+F). Esc clears." };
        _filterHint = new TextBlock
        {
            Text = "Filter by name, PID, user, description or command line",
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _filterHint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        filterHost.Children.Add(_filter);
        filterHost.Children.Add(_filterHint);
        _filter.TextChanged += (_, _) =>
        {
            _filterHint.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Rebuild();
        };
        _filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _filter.Text.Length > 0) { _filter.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Down) { _grid.Focus(); e.Handled = true; }
        };
        bar.Children.Add(filterHost);

        _treeBtn = new ToggleButton
        {
            Content = "Tree", IsChecked = Settings.Current.ProcessTree, Margin = new Thickness(8, 0, 4, 0),
            ToolTip = "Show which process started which. Click ▾ / ▸ beside a name to fold its children.",
        };
        _treeBtn.Checked += (_, _) => { Settings.Current.ProcessTree = true; Settings.Save(); Rebuild(); };
        _treeBtn.Unchecked += (_, _) => { Settings.Current.ProcessTree = false; Settings.Save(); Rebuild(); };
        bar.Children.Add(_treeBtn);

        _detailsBtn = new ToggleButton
        {
            Content = "Details", IsChecked = Settings.Current.Get("proc.details", "1") == "1",
            ToolTip = "Show or hide the details pane for the selected process",
        };
        _detailsBtn.Checked += (_, _) => { Settings.Current.Set("proc.details", "1"); ApplyDetailsVisibility(); };
        _detailsBtn.Unchecked += (_, _) => { Settings.Current.Set("proc.details", "0"); ApplyDetailsVisibility(); };
        bar.Children.Add(_detailsBtn);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        // ---- the grid
        _grid = new DataGrid
        {
            ItemsSource = _shown,
            SelectionMode = DataGridSelectionMode.Single,
            FrozenColumnCount = 1,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ContextMenu = _menu,
        };
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Name", SortMemberPath = "name", CanUserSort = true, Width = new DataGridLength(250),
            CellTemplate = NameTemplate(),
        });
        _grid.Columns.Add(Col("PID", nameof(ProcRow.PidText), "pid", 66, true));
        _grid.Columns.Add(Col("CPU", nameof(ProcRow.CpuText), "cpu", 72, true, nameof(ProcRow.CpuHeat)));
        _grid.Columns.Add(Col("Memory", nameof(ProcRow.MemText), "mem", 90, true, nameof(ProcRow.MemHeat)));
        _grid.Columns.Add(Col("Disk", nameof(ProcRow.DiskText), "disk", 90, true, nameof(ProcRow.DiskHeat)));
        _grid.Columns.Add(Col("Network", nameof(ProcRow.NetText), "net", 90, true, nameof(ProcRow.NetHeat)));
        _grid.Columns.Add(Col("GPU", nameof(ProcRow.GpuText), "gpu", 68, true, nameof(ProcRow.GpuHeat)));
        _grid.Columns.Add(Col("Power (est.)", nameof(ProcRow.PowerText), "power", 88, true));
        _grid.Columns.Add(Col("Threads", nameof(ProcRow.ThreadsText), "threads", 70, true));
        _grid.Columns.Add(Col("Handles", nameof(ProcRow.HandlesText), "handles", 76, true));
        _grid.Columns.Add(Col("User", nameof(ProcRow.User), "user", 120, false));
        _grid.Columns.Add(Col("Description", nameof(ProcRow.DescriptionText), "desc", 300, false));
        _grid.Columns.Add(Col("Started", nameof(ProcRow.StartedText), "started", 100, true));
        foreach (var c in _grid.Columns)
            c.SortDirection = c.SortMemberPath == _sortKey
                ? (_sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : (ListSortDirection?)null;

        _grid.Sorting += OnSorting;
        _grid.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var r = _grid.SelectedItem as ProcRow;
            if (r == null && _selected != null && _shown.Contains(_selected)) { _selected = null; ShowDetails(null, true); return; }
            if (r != null && !ReferenceEquals(r, _selected)) { _selected = r; ShowDetails(r, true); }
        };
        _grid.PreviewMouseLeftButtonDown += OnGridMouseDown;
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _grid.SelectedItem is ProcRow r) { e.Handled = true; EndProcess(r); }
        };
        _grid.ContextMenuOpening += OnMenuOpening;
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        // ---- splitter + details pane
        _splitter = new GridSplitter
        {
            Height = 4,
            Margin = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = Cursors.SizeNS,
        };
        Grid.SetRow(_splitter, 3);
        root.Children.Add(_splitter);

        _dEmpty = Ui.Text("Select a process to see its details.", "Sub");
        _dEmpty.VerticalAlignment = VerticalAlignment.Top;

        _dBody = new Grid();
        _dBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _dBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        var info = new StackPanel();
        _dTitle = Ui.Text("", "H2");
        _dTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(_dTitle);

        var pairs = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) pairs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _dCompany = Pair(pairs, 0, 0, "Company");
        _dUser = Pair(pairs, 1, 0, "User");
        _dStarted = Pair(pairs, 2, 0, "Started");
        _dParent = Pair(pairs, 3, 0, "Parent");
        _dPriority = Pair(pairs, 0, 1, "Priority");
        _dSession = Pair(pairs, 1, 1, "Session");
        _dThreads = Pair(pairs, 2, 1, "Threads");
        _dHandles = Pair(pairs, 3, 1, "Handles");
        _dPrivate = Pair(pairs, 0, 2, "Memory");
        _dWorking = Pair(pairs, 1, 2, "Working set");
        _dCommit = Pair(pairs, 2, 2, "Commit");
        info.Children.Add(pairs);

        info.Children.Add(Caption("Path"));
        _dPath = new TextBox { IsReadOnly = true, Margin = new Thickness(0, 0, 0, 6) };
        _dPath.SetResourceReference(TextBox.FontFamilyProperty, "MonoFont");
        info.Children.Add(_dPath);
        info.Children.Add(Caption("Command line"));
        _dCmd = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 64, Margin = new Thickness(0, 0, 0, 6) };
        _dCmd.SetResourceReference(TextBox.FontFamilyProperty, "MonoFont");
        info.Children.Add(_dCmd);
        info.Children.Add(Caption("Hosted services"));
        _dServices = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        info.Children.Add(_dServices);
        var infoScroll = new ScrollViewer { Content = info, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 14, 0) };
        _dBody.Children.Add(infoScroll);

        var graphPanel = new DockPanel();
        _dGraphCaption = Ui.Text("CPU (this process)", "Label");
        _dGraphCaption.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(_dGraphCaption, Dock.Top);
        graphPanel.Children.Add(_dGraphCaption);
        _cpuGraph = new Graph { Capacity = 60, Max = 100, AutoMax = true, AutoFloor = 5, GridLines = 4, MinHeight = 60 };
        _cpuGraph.AddSeries("Graph1", true, 1.6, "CPU");
        graphPanel.Children.Add(_cpuGraph);
        Grid.SetColumn(graphPanel, 1);
        _dBody.Children.Add(graphPanel);

        var detailsHost = new Grid();
        detailsHost.Children.Add(_dEmpty);
        detailsHost.Children.Add(_dBody);
        _detailsCard = Ui.Card(detailsHost, 10);
        _detailsCard.Margin = new Thickness(0);
        Grid.SetRow(_detailsCard, 4);
        root.Children.Add(_detailsCard);

        Content = root;
        ShowDetails(null, true);
        ApplyDetailsVisibility();

        PreviewKeyDown += (_, e) =>
        {
            if (Ui.Ctrl(e, Key.F)) { _filter.Focus(); _filter.SelectAll(); e.Handled = true; }
        };
        Palette.Changed += () =>
        {
            ProcHeat.Build();
            foreach (var r in _rows.Values) r.RaiseHeat();
        };
    }

    // ================================================================= builders

    static TextBlock SumPair(Panel host, string label)
    {
        var l = Ui.Text(label, "Label");
        l.Margin = new Thickness(14, 0, 6, 0);
        host.Children.Add(l);
        var v = Ui.Text(Fmt.Na, "ValueSmall");
        host.Children.Add(v);
        return v;
    }

    static TextBlock Caption(string text)
    {
        var t = Ui.Text(text, "Label");
        t.Margin = new Thickness(0, 2, 0, 2);
        return t;
    }

    static TextBlock Pair(Grid g, int row, int col, string label)
    {
        var l = Ui.Text(label, "Label");
        l.Margin = new Thickness(0, 2, 8, 2);
        Grid.SetRow(l, row);
        Grid.SetColumn(l, col * 2);
        g.Children.Add(l);
        var v = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 16, 2),
            Text = Fmt.Na,
        };
        Grid.SetRow(v, row);
        Grid.SetColumn(v, col * 2 + 1);
        g.Children.Add(v);
        return v;
    }

    DataGridTextColumn Col(string header, string path, string sortKey, double width, bool numeric, string heatPath = null)
    {
        var c = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            SortMemberPath = sortKey,
            CanUserSort = true,
            Width = new DataGridLength(width),
        };
        if (heatPath != null)
        {
            var st = new Style(typeof(TextBlock), _numStyle);
            st.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            st.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            st.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 4, 8, 4)));
            st.Setters.Add(new Setter(TextBlock.BackgroundProperty, new Binding(heatPath) { Mode = BindingMode.OneWay }));
            c.ElementStyle = st;
            c.CellStyle = _heatCellStyle;
        }
        else c.ElementStyle = numeric ? _numStyle : _textStyle;
        return c;
    }

    static DataTemplate NameTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(ProcRow.Indent)) { Mode = BindingMode.OneWay });

        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(FrameworkElement.WidthProperty, 14.0);
        glyph.SetValue(FrameworkElement.TagProperty, "toggle");
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        glyph.SetValue(TextBlock.BackgroundProperty, Brushes.Transparent);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProcRow.Glyph)) { Mode = BindingMode.OneWay });
        glyph.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ProcRow.GlyphVisibility)) { Mode = BindingMode.OneWay });
        panel.AppendChild(glyph);

        var img = new FrameworkElementFactory(typeof(Image));
        img.SetValue(FrameworkElement.WidthProperty, 16.0);
        img.SetValue(FrameworkElement.HeightProperty, 16.0);
        img.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        img.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        img.SetBinding(Image.SourceProperty, new Binding(nameof(ProcRow.Icon)) { Mode = BindingMode.OneWay });
        panel.AppendChild(img);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ProcRow.Name)) { Mode = BindingMode.OneWay });
        panel.AppendChild(text);

        return new DataTemplate { VisualTree = panel };
    }

    // ================================================================ lifecycle

    public void OnShown()
    {
        if (!_subscribed) { Sampler.Tick += OnTick; _subscribed = true; }
        ServiceMap.Refresh();
        if (Sampler.Latest != null) Apply(Sampler.Latest, false);
        if (_selected != null) ShowDetails(_selected, true);
    }

    public void OnHidden()
    {
        if (_subscribed) { Sampler.Tick -= OnTick; _subscribed = false; }
    }

    public void Receive(object arg)
    {
        if (arg is int pid) SelectPid(pid);
    }

    void OnTick(Snapshot s) => Apply(s, true);

    // ==================================================================== data

    void Apply(Snapshot s, bool push)
    {
        if (s == null) return;
        int self = Environment.ProcessId;
        bool showSelf = Settings.Current.ShowSelf;
        var seen = new HashSet<string>();
        List<ProcRow> fresh = null;

        foreach (var p in s.Processes)
        {
            if (!showSelf && p.Pid == self) continue;
            string key = p.Key;
            if (!seen.Add(key)) continue;
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new ProcRow(p);
                _rows[key] = row;
                (fresh ??= new List<ProcRow>()).Add(row);
            }
            row.Update(p, s.MemTotal);
            if (row.IsSvchost && row.DetailsLoaded) row.RefreshDescription();
        }

        if (_rows.Count != seen.Count)
        {
            foreach (var k in _rows.Keys.Where(x => !seen.Contains(x)).ToList())
            {
                _rows.Remove(k);
                _collapsed.Remove(k);
            }
        }

        if (fresh != null) LoadDetails(fresh);

        _sumCpu.Text = Fmt.Pct(s.CpuTotal, 1);
        _sumMem.Text = Fmt.Pct(s.MemLoadPct);
        _sumCount.Text = s.Processes.Count.ToString("N0");
        if (s.PerProcessNetAvailable) _netHint.Visibility = Visibility.Collapsed;
        else
        {
            _netHint.Text = Elevation.IsAdmin
                ? "Per-process network unavailable: " + NetEtw.Status
                : "Per-process network needs admin";
            _netHint.Visibility = Visibility.Visible;
        }

        Rebuild();

        if (_selected != null)
        {
            if (_rows.ContainsKey(_selected.Key))
            {
                UpdateLive(_selected);
                if (push)
                {
                    _cpuGraph.Push(_selected.Cpu);
                    _peak = Math.Max(_peak, _selected.Cpu);
                    _dGraphCaption.Text = $"CPU (this process) · now {Fmt.Pct(_selected.Cpu, 1)} · peak {Fmt.Pct(_peak, 1)}";
                }
            }
            else
            {
                _dTitle.Text = $"{_selected.Name}  ·  PID {_selected.Pid}  ·  exited";
                _selected = null;
            }
        }

        if (_pendingPid != 0)
        {
            int pid = _pendingPid;
            _pendingPid = 0;
            SelectPid(pid);
        }
    }

    /// <summary>Path, user, description and icon: looked up once per new row, off the UI thread (icon on it).</summary>
    void LoadDetails(List<ProcRow> rows)
    {
        var samples = rows.Select(r => r.Sample).ToList();
        Task.Run(() => samples.Select(p => ProcessInfo.Get(p)).ToList())
            .ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    if (t.Exception != null) Log.Write("process details: " + t.Exception.GetBaseException().Message);
                    return;
                }
                try
                {
                    var list = t.Result;
                    for (int i = 0; i < rows.Count && i < list.Count; i++)
                    {
                        rows[i].ApplyDetails(list[i]);
                        if (!string.IsNullOrEmpty(list[i].Path)) rows[i].Icon = ProcessInfo.Icon(list[i].Path);
                    }
                    if (_filter.Text.Trim().Length > 0 || _sortKey == "user" || _sortKey == "desc") Rebuild();
                    if (_selected != null && rows.Contains(_selected)) ShowDetails(_selected, false);
                }
                catch (Exception ex) { Log.Write("process details apply: " + ex.Message); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ============================================================ sort + layout

    static bool DefaultDesc(string key) => key is not ("name" or "pid" or "user" or "desc" or "started");

    void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;
        if (key == _sortKey) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = DefaultDesc(key); }
        foreach (var c in _grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = _sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending;
        Settings.Current.Extra["proc.sort"] = _sortKey;
        Settings.Current.Set("proc.sortdesc", _sortDesc ? "1" : "0");
        Rebuild();
        if (_selected != null && _shown.Contains(_selected)) _grid.ScrollIntoView(_selected);
    }

    static int Primary(string key, ProcRow a, ProcRow b)
    {
        switch (key)
        {
            case "name": return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            case "pid": return a.Pid.CompareTo(b.Pid);
            case "cpu": return a.Cpu.CompareTo(b.Cpu);
            case "mem": return a.Mem.CompareTo(b.Mem);
            case "disk": return a.Disk.CompareTo(b.Disk);
            case "net": return a.Net.CompareTo(b.Net);
            case "gpu": return a.Gpu.CompareTo(b.Gpu);
            case "power": return a.Power.CompareTo(b.Power);
            case "threads": return a.Threads.CompareTo(b.Threads);
            case "handles": return a.Handles.CompareTo(b.Handles);
            case "user": return string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase);
            case "desc": return string.Compare(a.DescriptionText, b.DescriptionText, StringComparison.OrdinalIgnoreCase);
            case "started": return a.CreateTime.CompareTo(b.CreateTime);
            default: return a.Cpu.CompareTo(b.Cpu);
        }
    }

    Comparison<ProcRow> MakeComparison()
    {
        string key = _sortKey;
        int sign = _sortDesc ? -1 : 1;
        return (a, b) =>
        {
            int c = Primary(key, a, b) * sign;
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        };
    }

    /// <summary>Work out the order to show (filter, sort, tree) and move the grid's rows into it without rebuilding them.</summary>
    void Rebuild()
    {
        var cmp = MakeComparison();
        string f = _filter.Text.Trim();
        bool tree = _treeBtn.IsChecked == true;
        var target = new List<ProcRow>(_rows.Count);

        if (!tree)
        {
            foreach (var r in _rows.Values)
            {
                r.Parent = null;
                if (r.Matches(f)) target.Add(r);
            }
            target.Sort(cmp);
            foreach (var r in target) r.SetTree(0, false, false, false);
        }
        else BuildTree(target, cmp, f);

        Sync(target);
    }

    void BuildTree(List<ProcRow> target, Comparison<ProcRow> cmp, string f)
    {
        var byPid = new Dictionary<int, ProcRow>(_rows.Count);
        foreach (var r in _rows.Values) byPid[r.Pid] = r;

        // A child is only a child if the parent pid matches AND it started after the parent
        // (otherwise the "parent" is an unrelated process that was given a recycled PID).
        foreach (var r in _rows.Values)
        {
            r.Parent = null;
            if (r.ParentPid != r.Pid && byPid.TryGetValue(r.ParentPid, out var p) && !ReferenceEquals(p, r) && p.CreateTime <= r.CreateTime)
                r.Parent = p;
        }
        // break any cycle (only possible with identical start times)
        foreach (var r in _rows.Values)
        {
            var a = r;
            int n = 0;
            while (a != null && n < 512) { a = a.Parent; n++; }
            if (a != null) r.Parent = null;
        }

        var children = new Dictionary<ProcRow, List<ProcRow>>();
        var roots = new List<ProcRow>();
        foreach (var r in _rows.Values)
        {
            if (r.Parent == null) roots.Add(r);
            else
            {
                if (!children.TryGetValue(r.Parent, out var l)) children[r.Parent] = l = new List<ProcRow>();
                l.Add(r);
            }
        }

        // With a filter, show the matches plus their ancestors (for context), and ignore folding.
        HashSet<ProcRow> visible = null;
        if (f.Length > 0)
        {
            visible = new HashSet<ProcRow>();
            foreach (var r in _rows.Values)
            {
                if (!r.Matches(f)) continue;
                var a = r;
                int guard = 0;
                while (a != null && visible.Add(a) && guard++ < 512) a = a.Parent;
            }
        }

        var visited = new HashSet<ProcRow>();
        roots.Sort(cmp);
        foreach (var r in roots) Walk(r, 0);

        void Walk(ProcRow r, int depth)
        {
            if (!visited.Add(r)) return;
            if (visible != null && !visible.Contains(r)) return;
            children.TryGetValue(r, out var kids);
            bool has = kids != null && (visible == null ? kids.Count > 0 : kids.Any(x => visible.Contains(x)));
            bool collapsed = visible == null && has && _collapsed.Contains(r.Key);
            r.SetTree(depth, true, has, collapsed);
            target.Add(r);
            if (kids == null || collapsed) return;
            kids.Sort(cmp);
            foreach (var k in kids) Walk(k, depth + 1);
        }
    }

    void Sync(List<ProcRow> target)
    {
        _syncing = true;
        try
        {
            var want = new HashSet<ProcRow>(target);
            for (int i = _shown.Count - 1; i >= 0; i--)
                if (!want.Contains(_shown[i])) _shown.RemoveAt(i);
            for (int i = 0; i < target.Count; i++)
            {
                var r = target[i];
                if (i < _shown.Count && ReferenceEquals(_shown[i], r)) continue;
                int j = -1;
                for (int k = i + 1; k < _shown.Count; k++)
                    if (ReferenceEquals(_shown[k], r)) { j = k; break; }
                if (j >= 0) _shown.Move(j, i);
                else _shown.Insert(i, r);
            }
            // keep the selection where the user put it
            if (_selected != null && !ReferenceEquals(_grid.SelectedItem, _selected) && want.Contains(_selected))
                _grid.SelectedItem = _selected;
        }
        finally { _syncing = false; }
    }

    void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock { Tag: "toggle" } tb && tb.DataContext is ProcRow r && r.HasChildren)
        {
            if (!_collapsed.Remove(r.Key)) _collapsed.Add(r.Key);
            Rebuild();
            e.Handled = true;
        }
    }

    void SelectPid(int pid)
    {
        var r = _rows.Values.FirstOrDefault(x => x.Pid == pid);
        if (r == null)
        {
            if (_rows.Count == 0) _pendingPid = pid;
            else Status.Set($"PID {pid} is not running any more", StatusKind.Warn);
            return;
        }
        if (!r.Matches(_filter.Text.Trim())) _filter.Text = "";
        int guard = 0;
        for (var a = r.Parent; a != null && guard++ < 512; a = a.Parent) _collapsed.Remove(a.Key);
        Rebuild();
        _selected = r;
        _syncing = true;
        try { _grid.SelectedItem = r; }
        finally { _syncing = false; }
        ShowDetails(r, true);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_shown.Contains(r)) { _grid.ScrollIntoView(r); _grid.Focus(); }
        }));
    }

    // ================================================================= details

    void ApplyDetailsVisibility()
    {
        bool on = _detailsBtn.IsChecked == true;
        _detailsCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _detailsRow.MinHeight = on ? 120 : 0;
        _detailsRow.Height = on ? new GridLength(210) : new GridLength(0);
    }

    static string Or(string s) => string.IsNullOrEmpty(s) ? Fmt.Na : s;

    void ShowDetails(ProcRow r, bool reseed)
    {
        if (r == null)
        {
            _dBody.Visibility = Visibility.Collapsed;
            _dEmpty.Visibility = Visibility.Visible;
            _cpuGraph.Clear();
            _peak = 0;
            return;
        }
        _dBody.Visibility = Visibility.Visible;
        _dEmpty.Visibility = Visibility.Collapsed;

        _dTitle.Text = $"{r.Name}  ·  PID {r.Pid}";
        _dPath.Text = r.ExePath.Length > 0 ? r.ExePath : r.DetailsLoaded ? "unavailable (access denied or no image file)" : "reading…";
        _dCmd.Text = r.CommandLine.Length > 0 ? r.CommandLine : r.DetailsLoaded ? Fmt.Na : "reading…";
        _dCompany.Text = Or(r.Company);
        _dCompany.ToolTip = r.Company.Length > 0 ? r.Company : null;
        _dUser.Text = Or(r.User);
        var started = r.Sample.Started;
        _dStarted.Text = started == DateTime.MinValue ? Fmt.Na : started.ToString("dd MMM yyyy HH:mm:ss");
        var parent = _rows.Values.FirstOrDefault(x => x.Pid == r.ParentPid && !ReferenceEquals(x, r) && x.CreateTime <= r.CreateTime);
        _dParent.Text = parent != null ? $"{parent.Name} ({parent.Pid})" : r.ParentPid > 0 ? $"PID {r.ParentPid} (not running)" : Fmt.Na;
        _dParent.ToolTip = _dParent.Text;
        _dSession.Text = r.Sample.SessionId.ToString();

        if (reseed)
        {
            _cpuGraph.Clear();
            var hist = Sampler.History.ToList();
            int start = Math.Max(0, hist.Count - _cpuGraph.Capacity);
            var rows = new List<double[]>();
            _peak = 0;
            for (int i = start; i < hist.Count; i++)
            {
                double v = double.NaN;
                var snap = hist[i];
                if (snap != null)
                    foreach (var p in snap.Processes)
                        if (p.Pid == r.Pid && p.CreateTime == r.CreateTime) { v = p.Cpu; break; }
                if (!double.IsNaN(v)) _peak = Math.Max(_peak, v);
                rows.Add(new[] { v });
            }
            _cpuGraph.Seed(rows);
            _dGraphCaption.Text = $"CPU (this process) · now {Fmt.Pct(r.Cpu, 1)}";
        }
        UpdateLive(r);
    }

    void UpdateLive(ProcRow r)
    {
        var s = r.Sample;
        _dThreads.Text = s.Threads.ToString("N0");
        _dHandles.Text = s.Handles.ToString("N0");
        _dPrivate.Text = Fmt.Bytes(s.PrivateWorkingSet);
        _dWorking.Text = Fmt.Bytes(s.WorkingSet);
        _dCommit.Text = Fmt.Bytes(s.CommitBytes);
        _dPriority.Text = ProcessActions.PriorityName(r.Pid);
        var services = ServiceMap.For(r.Pid);
        _dServices.Text = services.Count == 0 ? "None" : string.Join(", ", services.Select(x => x.display + " (" + x.name + ")"));
    }

    // ================================================================= actions

    static string Plural(int n, string one, string many) => n == 1 ? one : many;

    static string Who(ProcRow r) => r.User.Length > 0 ? r.User : "session 0";

    void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rowEl = Ancestor<DataGridRow>(e.OriginalSource);
        if (rowEl?.Item is not ProcRow r) { e.Handled = true; return; }
        if (!ReferenceEquals(_grid.SelectedItem, r)) _grid.SelectedItem = r;

        _menu.Items.Clear();
        _menu.Items.Add(Item("End process", () => EndProcess(r)));
        _menu.Items.Add(Item("End process tree", () => EndTree(r)));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Suspend", () => Suspend(r)));
        _menu.Items.Add(Item("Resume", () => Resume(r)));

        var prio = new MenuItem { Header = "Set priority" };
        string current = ProcessActions.PriorityName(r.Pid);
        foreach (var (name, value) in ProcessActions.Priorities)
        {
            string n = name;
            uint v = value;
            var mi = new MenuItem { Header = n, IsChecked = n == current };
            mi.Click += (_, _) => SetPriority(r, n, v);
            prio.Items.Add(mi);
        }
        _menu.Items.Add(prio);
        _menu.Items.Add(new Separator());

        var open = Item("Open file location", () => ProcessActions.OpenFileLocation(r.Sample));
        open.IsEnabled = r.ExePath.Length > 0;
        _menu.Items.Add(open);
        _menu.Items.Add(Item("Copy details", () => CopyDetails(r)));
        _menu.Items.Add(Item("Search online", () => Shell.Open("https://duckduckgo.com/?q=" + Uri.EscapeDataString(r.Name + " process"))));
        _menu.Items.Add(Item("Show network connections", () => Nav.Go("connections", r.Pid)));

        var services = ServiceMap.For(r.Pid);
        if (services.Count == 1)
        {
            string svc = services[0].name;
            _menu.Items.Add(Item("Go to service (" + services[0].display + ")", () => Nav.Go("services", svc)));
        }
        else if (services.Count > 1)
        {
            var go = new MenuItem { Header = "Go to service" };
            foreach (var sv in services)
            {
                string svc = sv.name;
                go.Items.Add(Item(sv.display.Length > 0 ? sv.display : sv.name, () => Nav.Go("services", svc)));
            }
            _menu.Items.Add(go);
        }
    }

    static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) =>
        {
            try { click(); }
            catch (Exception ex) { Log.Write("process menu: " + ex); Status.Set(header + " failed: " + ex.Message, StatusKind.Bad); }
        };
        return mi;
    }

    static T Ancestor<T>(object start) where T : DependencyObject
    {
        var d = start as DependencyObject;
        int guard = 0;
        while (d != null && d is not T && guard++ < 200)
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    void EndProcess(ProcRow r)
    {
        string msg = $"End {r.Name} (PID {r.Pid})?\n\nAnything unsaved in it will be lost.";
        if (r.IsSystem)
            msg += $"\n\nThis is a system process (it runs as {Who(r)}). Ending it can make Windows unstable, sign you out or restart the PC.";
        if (!Ui.Confirm(msg, "End process", "End process", "Cancel", danger: true)) return;
        var (ok, message) = ProcessActions.Kill(r.Sample);
        Status.Set($"{r.Name} (PID {r.Pid}): {message}", ok ? StatusKind.Good : StatusKind.Bad);
    }

    void EndTree(ProcRow r)
    {
        var all = Sampler.Latest?.Processes ?? new List<ProcSample>();
        var desc = ProcessActions.Descendants(r.Sample, all);
        bool system = r.IsSystem;
        foreach (var d in desc)
            if (_rows.TryGetValue(d.Key, out var dr) && dr.IsSystem) { system = true; break; }

        string msg = desc.Count == 0
            ? $"End {r.Name} (PID {r.Pid})? It has no child processes.\n\nAnything unsaved in it will be lost."
            : $"End {r.Name} (PID {r.Pid}) and the {desc.Count} {Plural(desc.Count, "process", "processes")} it started?\n\nAnything unsaved in them will be lost.";
        if (system)
            msg += "\n\nThis includes a system process (session 0 or a Windows service account). Ending it can make Windows unstable, sign you out or restart the PC.";
        if (!Ui.Confirm(msg, "End process tree", "End process tree", "Cancel", danger: true)) return;

        var root = r.Sample;
        string name = r.Name;
        Ui.Run(async () =>
        {
            var (ok, failed) = await Task.Run(() => ProcessActions.KillTree(root, all));
            if (failed == 0) Status.Set($"{name}: ended {ok} {Plural(ok, "process", "processes")}", StatusKind.Good);
            else Status.Set($"{name}: ended {ok}, could not end {failed} (access denied or already gone)", StatusKind.Warn);
        }, "End process tree");
    }

    void Suspend(ProcRow r)
    {
        if (r.IsSystem && !Ui.Confirm($"Suspend {r.Name} (PID {r.Pid})?\n\nIt is a system process (it runs as {Who(r)}). " +
                                      "Freezing it can hang parts of Windows until you resume it.", "Suspend process", "Suspend", "Cancel", danger: true))
            return;
        var (ok, message) = ProcessActions.Suspend(r.Sample);
        Status.Set($"{r.Name} (PID {r.Pid}): {message}", ok ? StatusKind.Good : StatusKind.Bad);
    }

    void Resume(ProcRow r)
    {
        var (ok, message) = ProcessActions.Resume(r.Sample);
        Status.Set($"{r.Name} (PID {r.Pid}): {message}", ok ? StatusKind.Good : StatusKind.Bad);
    }

    void SetPriority(ProcRow r, string name, uint value)
    {
        if (name == "Realtime" && !Ui.Confirm($"Set {r.Name} to Realtime priority?\n\nA busy realtime process can starve Windows itself, " +
                                               "including the mouse and keyboard. High is usually enough.", "Set priority", "Set Realtime", "Cancel", danger: true))
            return;
        var (ok, message) = ProcessActions.SetPriority(r.Sample, value);
        Status.Set(ok ? $"{r.Name} (PID {r.Pid}): priority set to {name}" : $"{r.Name} (PID {r.Pid}): {message}", ok ? StatusKind.Good : StatusKind.Bad);
        if (ReferenceEquals(r, _selected)) _dPriority.Text = ProcessActions.PriorityName(r.Pid);
    }

    static void CopyDetails(ProcRow r)
    {
        var lines = new List<string>
        {
            "Name: " + r.Name,
            "PID: " + r.Pid,
            "Path: " + Or(r.ExePath),
            "Command line: " + Or(r.CommandLine),
            "Description: " + Or(r.DescriptionText),
            "Company: " + Or(r.Company),
            "User: " + Or(r.User),
            "Started: " + r.StartedText,
        };
        Ui.CopyToClipboard(string.Join(Environment.NewLine, lines));
    }
}
