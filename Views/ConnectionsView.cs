using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// TMOG's Connections page: every TCP connection and UDP endpoint, the
/// process that owns it, the host name this PC resolved for the remote
/// address, bytes moved (from the ETW network trace) and a flag for known
/// Microsoft telemetry and advertising endpoints.
/// </summary>
public sealed class ConnectionsView : UserControl, IView
{
    sealed class PollResult
    {
        public List<ConnEntry> Entries = new();
        public Dictionary<string, long[]> Totals;                 // null when ETW is not running
        public Dictionary<int, ProcSample> Procs = new();
        public Dictionary<int, string> Paths = new();
    }

    readonly Dictionary<string, ConnRow> _rows = new();
    readonly ObservableCollection<ConnRow> _items = new();
    readonly ListCollectionView _view;
    readonly DataGrid _grid;
    readonly TextBox _filter;
    readonly TextBlock _filterHint;
    readonly ComboBox _proto;
    readonly ToggleButton _hideLocal, _onlyFlagged, _reverse;
    readonly TextBlock _cEst, _cListen, _cUdp, _cFlagged, _hint;
    readonly ContextMenu _menu = new();

    readonly Dictionary<string, string> _rdns = new();       // ip -> reverse name ("" = none found)
    readonly HashSet<string> _rdnsPending = new();
    bool _polling, _subscribed;
    string _filterText = "";

    public ConnectionsView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- header + counts
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Header("Connections",
            "Live TCP connections and UDP endpoints with the process that owns each. Host names come from DNS answers this PC " +
            "received while Winnow was running, so connections opened before Winnow started may show no name. " +
            "Known Microsoft telemetry and ad endpoints are flagged."));

        var counts = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 14) };
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _cEst = Count(line, "Established");
        _cListen = Count(line, "Listening");
        _cUdp = Count(line, "UDP");
        _cFlagged = Count(line, "Flagged");
        counts.Children.Add(line);
        _hint = Ui.Text("", "Sub");
        _hint.HorizontalAlignment = HorizontalAlignment.Right;
        _hint.Margin = new Thickness(0, 3, 0, 0);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        _hint.Visibility = Visibility.Collapsed;
        counts.Children.Add(_hint);
        Grid.SetColumn(counts, 1);
        head.Children.Add(counts);
        root.Children.Add(head);

        // ---- toolbar
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var filterHost = new Grid { Width = 300, Margin = new Thickness(0, 0, 8, 4) };
        _filter = new TextBox { ToolTip = "Filter by process, PID, address, host, state or flag (Ctrl+F). \"pid:1234\" shows one process. Esc clears." };
        _filterHint = new TextBlock
        {
            Text = "Filter by process, address, host or state",
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
            _filterText = _filter.Text.Trim();
            RefreshView();
        };
        _filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _filter.Text.Length > 0) { _filter.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Down) { _grid.Focus(); e.Handled = true; }
        };
        bar.Children.Add(filterHost);

        _proto = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 8, 4), ToolTip = "Protocol" };
        foreach (var p in new[] { "All", "TCP", "UDP" }) _proto.Items.Add(new ComboBoxItem { Content = p, Tag = p });
        _proto.SelectedIndex = 0;
        _proto.SelectionChanged += (_, _) => RefreshView();
        bar.Children.Add(_proto);

        _hideLocal = Toggle("Hide listening and local", Settings.Current.Get("conn.hidelocal", "0") == "1",
            "Hide listening TCP ports, UDP endpoints (which have no remote end) and loopback (127.0.0.1 / ::1) traffic", "conn.hidelocal");
        bar.Children.Add(_hideLocal);
        _onlyFlagged = Toggle("Only flagged", false, "Show only connections to known telemetry or ad endpoints", null);
        bar.Children.Add(_onlyFlagged);
        _reverse = Toggle("Reverse lookups", Settings.Current.Get("conn.rdns", "1") == "1",
            "When this PC did not resolve a name for an address, ask DNS for its reverse name (shown with \"(reverse)\"). " +
            "This sends a DNS query per address.", "conn.rdns");
        bar.Children.Add(_reverse);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        // ---- grid
        _view = new ListCollectionView(_items);
        _view.Filter = o => o is ConnRow r && Pass(r);
        _view.IsLiveSorting = true;
        foreach (var prop in new[] { nameof(ConnRow.ProcessName), nameof(ConnRow.Host), nameof(ConnRow.State), nameof(ConnRow.Recv),
                                     nameof(ConnRow.Sent), nameof(ConnRow.Flag), nameof(ConnRow.StateCode) })
            _view.LiveSortingProperties.Add(prop);
        _view.IsLiveFiltering = true;
        foreach (var prop in new[] { nameof(ConnRow.ProcessName), nameof(ConnRow.Host), nameof(ConnRow.State), nameof(ConnRow.Flag),
                                     nameof(ConnRow.StateCode) })
            _view.LiveFilteringProperties.Add(prop);

        _grid = new DataGrid
        {
            SelectionMode = DataGridSelectionMode.Single,
            FrozenColumnCount = 1,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ContextMenu = _menu,
        };
        var procCol = new DataGridTemplateColumn
        {
            Header = "Process", SortMemberPath = nameof(ConnRow.ProcessName), CanUserSort = true,
            Width = new DataGridLength(200), CellTemplate = ProcessTemplate(),
        };
        _grid.Columns.Add(procCol);
        var num = (Style)Application.Current.FindResource("NumCell");
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        var mono = new Style(typeof(TextBlock), text);
        mono.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new DynamicResourceExtension("MonoFont")));
        mono.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
        var flag = new Style(typeof(TextBlock), text);
        flag.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Warn")));
        flag.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));

        _grid.Columns.Add(Col("PID", nameof(ConnRow.PidText), nameof(ConnRow.Pid), 66, num));
        _grid.Columns.Add(Col("Protocol", nameof(ConnRow.Protocol), nameof(ConnRow.Protocol), 72, text));
        _grid.Columns.Add(Col("Local address", nameof(ConnRow.LocalText), nameof(ConnRow.LocalSort), 180, mono));
        _grid.Columns.Add(Col("Remote address", nameof(ConnRow.RemoteText), nameof(ConnRow.RemoteSort), 200, mono));
        _grid.Columns.Add(Col("Remote host", nameof(ConnRow.Host), nameof(ConnRow.Host), 240, text));
        _grid.Columns.Add(Col("State", nameof(ConnRow.State), nameof(ConnRow.State), 100, text));
        var recv = Col("Received", nameof(ConnRow.RecvText), nameof(ConnRow.Recv), 86, num);
        recv.Header = new TextBlock { Text = "Received", ToolTip = "Bytes Winnow has seen on this connection since it started. — means none recorded, or per-process network is unavailable." };
        _grid.Columns.Add(recv);
        var sent = Col("Sent", nameof(ConnRow.SentText), nameof(ConnRow.Sent), 86, num);
        sent.Header = new TextBlock { Text = "Sent", ToolTip = "Bytes Winnow has seen on this connection since it started. — means none recorded, or per-process network is unavailable." };
        _grid.Columns.Add(sent);
        _grid.Columns.Add(Col("Flag", nameof(ConnRow.Flag), nameof(ConnRow.Flag), 90, flag));

        _grid.ItemsSource = _view;
        _view.SortDescriptions.Add(new SortDescription(nameof(ConnRow.ProcessName), ListSortDirection.Ascending));
        procCol.SortDirection = ListSortDirection.Ascending;

        _grid.ContextMenuOpening += OnMenuOpening;
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (Ui.Ctrl(e, Key.C) && _grid.SelectedItem is ConnRow r) { Ui.CopyToClipboard(r.CopyText); e.Handled = true; }
        };
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (Ui.Ctrl(e, Key.F)) { _filter.Focus(); _filter.SelectAll(); e.Handled = true; }
        };
    }

    // ================================================================= builders

    static TextBlock Count(Panel host, string label)
    {
        var l = Ui.Text(label, "Label");
        l.Margin = new Thickness(14, 0, 6, 0);
        host.Children.Add(l);
        var v = Ui.Text(Fmt.Na, "ValueSmall");
        host.Children.Add(v);
        return v;
    }

    ToggleButton Toggle(string text, bool on, string tip, string settingKey)
    {
        var t = new ToggleButton { Content = text, IsChecked = on, ToolTip = tip, Margin = new Thickness(0, 0, 6, 4) };
        t.Checked += (_, _) => { if (settingKey != null) Settings.Current.Set(settingKey, "1"); RefreshView(); };
        t.Unchecked += (_, _) => { if (settingKey != null) Settings.Current.Set(settingKey, "0"); RefreshView(); };
        return t;
    }

    static DataGridTextColumn Col(string header, string path, string sortPath, double width, Style element) => new()
    {
        Header = header,
        Binding = new Binding(path) { Mode = BindingMode.OneWay },
        SortMemberPath = sortPath,
        CanUserSort = true,
        Width = new DataGridLength(width),
        ElementStyle = element,
    };

    static DataTemplate ProcessTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var img = new FrameworkElementFactory(typeof(Image));
        img.SetValue(FrameworkElement.WidthProperty, 16.0);
        img.SetValue(FrameworkElement.HeightProperty, 16.0);
        img.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        img.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        img.SetBinding(Image.SourceProperty, new Binding(nameof(ConnRow.Icon)) { Mode = BindingMode.OneWay });
        panel.AppendChild(img);

        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        tb.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConnRow.ProcessName)) { Mode = BindingMode.OneWay });
        panel.AppendChild(tb);

        return new DataTemplate { VisualTree = panel };
    }

    // ================================================================ lifecycle

    public void OnShown()
    {
        if (!_subscribed) { Sampler.Tick += OnTick; _subscribed = true; }
        Poll();
    }

    public void OnHidden()
    {
        if (_subscribed) { Sampler.Tick -= OnTick; _subscribed = false; }
    }

    /// <summary>Nav.Go("connections", pid) shows just that process's connections.</summary>
    public void Receive(object arg)
    {
        if (arg is int pid) _filter.Text = "pid:" + pid;
    }

    void OnTick(Snapshot s) => Poll();

    // ==================================================================== data

    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var res = await Task.Run(() => Collect());
            Apply(res);
        }
        catch (Exception ex) { Log.Write("connections: " + ex); }
        finally { _polling = false; }
    }

    /// <summary>Background thread: the tables, ETW byte totals, and the owning processes' names and paths.</summary>
    static PollResult Collect()
    {
        var r = new PollResult { Entries = ConnTable.Read() };
        r.Totals = NetEtw.Running ? NetEtw.ConnectionTotals() : null;
        var byPid = new Dictionary<int, ProcSample>();
        var procs = Sampler.Latest?.Processes;
        if (procs != null) foreach (var p in procs) byPid[p.Pid] = p;
        foreach (var e in r.Entries)
        {
            if (r.Procs.ContainsKey(e.Pid) || !byPid.TryGetValue(e.Pid, out var p)) continue;
            r.Procs[e.Pid] = p;
            string path = "";
            try { path = ProcessInfo.Get(p).Path ?? ""; } catch { }
            r.Paths[e.Pid] = path;
        }
        return r;
    }

    static string NameFor(int pid, ProcSample p)
    {
        if (pid == 0) return "System Idle Process";
        if (p != null) return p.Name;
        if (pid == 4) return "System";
        return Fmt.Na;
    }

    void Apply(PollResult res)
    {
        var seen = new HashSet<string>();
        bool reverseOn = _reverse.IsChecked == true;
        var added = new List<ConnRow>();

        foreach (var e in res.Entries)
        {
            string key = e.Key;
            if (!seen.Add(key)) continue;
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new ConnRow(e);
                _rows[key] = row;
                added.Add(row);
            }

            res.Procs.TryGetValue(e.Pid, out var ps);
            res.Paths.TryGetValue(e.Pid, out var path);
            if (row.SetProcess(NameFor(e.Pid, ps), path, ps?.CreateTime ?? 0))
                row.Icon = string.IsNullOrEmpty(row.ExePath) ? null : ProcessInfo.Icon(row.ExePath);
            row.SetState(e.Protocol == "UDP" ? 0 : e.State, e.StateName);

            // host: the name this PC resolved (ETW DNS), else an optional reverse lookup
            string host = null;
            bool reverse = false;
            if (row.RemoteLookup != null)
            {
                host = NetEtw.HostFor(row.RemoteLookup);
                if (host == null && row.RemoteRaw != row.RemoteLookup) host = NetEtw.HostFor(row.RemoteRaw);
                if (host == null && reverseOn && !row.IsLocal)
                {
                    if (_rdns.TryGetValue(row.RemoteLookup, out var rn)) { if (rn.Length > 0) { host = rn; reverse = true; } }
                    else StartReverse(row.RemoteLookup);
                }
            }
            row.SetHost(host, reverse);

            // bytes: ETW keys are "pid|remote|port"; the port's byte order is not guaranteed, so try both
            long recv = -1, sent = -1;
            if (res.Totals != null && row.RemoteLookup != null)
            {
                long[] t = Totals(res.Totals, e.Pid, row.RemoteLookup, row.RemotePort);
                if (t == null && row.RemoteRaw != row.RemoteLookup) t = Totals(res.Totals, e.Pid, row.RemoteRaw, row.RemotePort);
                if (t != null && t.Length >= 2) { recv = t[0]; sent = t[1]; }
            }
            row.SetBytes(recv, sent);
        }

        // gone
        if (_rows.Count != seen.Count)
        {
            var gone = _rows.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            foreach (var g in gone)
            {
                _rows.Remove(g.Key);
                _items.Remove(g);
            }
        }

        // new
        foreach (var a in added) _items.Add(a);

        UpdateCounts();
    }

    static long[] Totals(Dictionary<string, long[]> totals, int pid, string ip, int port)
    {
        int swapped = ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);
        if (totals.TryGetValue(pid + "|" + ip + "|" + port, out var t)) return t;
        if (totals.TryGetValue(pid + "|" + ip + "|" + swapped, out t)) return t;
        return null;
    }

    async void StartReverse(string ip)
    {
        if (_rdnsPending.Count >= 8 || !_rdnsPending.Add(ip)) return;
        string name = "";
        try
        {
            var lookup = System.Net.Dns.GetHostEntryAsync(ip);
            _ = lookup.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            var done = await Task.WhenAny(lookup, Task.Delay(2000));
            if (done == lookup && lookup.Status == TaskStatus.RanToCompletion)
            {
                string hn = lookup.Result?.HostName;
                if (!string.IsNullOrEmpty(hn) && hn != ip) name = hn.TrimEnd('.');
            }
        }
        catch { }
        _rdns[ip] = name;
        _rdnsPending.Remove(ip);
        if (_rdns.Count > 20000) _rdns.Clear();
    }

    // ================================================================= filters

    bool Pass(ConnRow r)
    {
        if (_proto?.SelectedItem is ComboBoxItem { Tag: string p } && p != "All")
        {
            if (p == "TCP" && r.IsUdp) return false;
            if (p == "UDP" && !r.IsUdp) return false;
        }
        if (_hideLocal?.IsChecked == true && (r.IsListening || r.IsUdp || r.IsLocal)) return false;
        if (_onlyFlagged?.IsChecked == true && !r.IsFlagged) return false;
        return r.Matches(_filterText);
    }

    void RefreshView()
    {
        if (_view == null) return;
        _view.Refresh();
        UpdateCounts();
    }

    void UpdateCounts()
    {
        int est = 0, listen = 0, udp = 0, flagged = 0;
        foreach (var r in _rows.Values)
        {
            if (r.IsEstablished) est++;
            else if (r.IsListening) listen++;
            if (r.IsUdp) udp++;
            if (r.IsFlagged) flagged++;
        }
        _cEst.Text = est.ToString("N0");
        _cListen.Text = listen.ToString("N0");
        _cUdp.Text = udp.ToString("N0");
        _cFlagged.Text = flagged.ToString("N0");
        _cFlagged.SetResourceReference(TextBlock.ForegroundProperty, flagged > 0 ? "Warn" : "Text");

        if (!NetEtw.Running)
        {
            _hint.Text = Elevation.IsAdmin
                ? "Byte counts and host names unavailable: " + NetEtw.Status
                : "Byte counts and host names need admin";
            _hint.Visibility = Visibility.Visible;
        }
        else _hint.Visibility = Visibility.Collapsed;
    }

    // ================================================================= actions

    void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rowEl = Ancestor<DataGridRow>(e.OriginalSource);
        if (rowEl?.Item is not ConnRow r) { e.Handled = true; return; }
        if (!ReferenceEquals(_grid.SelectedItem, r)) _grid.SelectedItem = r;

        _menu.Items.Clear();
        _menu.Items.Add(Item("Copy row", () => Ui.CopyToClipboard(r.CopyText)));
        var copyRemote = Item("Copy remote address", () => Ui.CopyToClipboard(r.RemoteText));
        copyRemote.IsEnabled = r.HasRemote;
        _menu.Items.Add(copyRemote);
        _menu.Items.Add(new Separator());

        int pid = r.Pid;
        var go = Item("Go to process", () => Nav.Go("processes", pid));
        go.IsEnabled = pid > 0;
        _menu.Items.Add(go);

        string target = r.HostName ?? r.RemoteLookup;
        var search = Item("Search the host online", () =>
            Shell.Open("https://duckduckgo.com/?q=" + Uri.EscapeDataString(target ?? "")));
        search.IsEnabled = !string.IsNullOrEmpty(target);
        _menu.Items.Add(search);
        _menu.Items.Add(new Separator());

        string exeName = r.ExePath.Length > 0 ? System.IO.Path.GetFileName(r.ExePath) : r.ProcessName;
        var block = Item($"Block {exeName}'s outbound traffic…", () => Block(r));
        block.IsEnabled = r.ExePath.Length > 0;
        if (!block.IsEnabled) block.ToolTip = "Winnow could not read this program's path, so it cannot write a firewall rule for it.";
        _menu.Items.Add(block);
    }

    static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) =>
        {
            try { click(); }
            catch (Exception ex) { Log.Write("connections menu: " + ex); Status.Set(header + " failed: " + ex.Message, StatusKind.Bad); }
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

    void Block(ConnRow r)
    {
        string path = r.ExePath;
        if (string.IsNullOrEmpty(path)) return;
        string exe = System.IO.Path.GetFileName(path);
        // A short hash of the full path keeps two different programs with the same file name
        // (C:\A\app.exe, D:\B\app.exe) on separate rules. Undo removes by the name stored in the journal,
        // so rules made by older versions ("Winnow block app.exe") still undo as before.
        string tag = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                         System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..8].ToLowerInvariant();
        string ruleName = "Winnow block " + exe + " (" + tag + ")";
        if (!Elevation.IsAdmin)
        {
            Ui.Info("Adding a firewall rule needs administrator rights. Restart Winnow and accept the prompt, then try again.");
            return;
        }
        string msg = $"Block all outbound network traffic from {exe}?\n\n{path}\n\n" +
                     $"Winnow adds a Windows Firewall rule named \"{ruleName}\". The program will not be able to connect out " +
                     "until the rule is removed. You can undo it from the change history (Settings › Open change history).";
        string winDir = Paths.WindowsDir;
        if (!string.IsNullOrEmpty(winDir) && path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
            msg += "\n\nThis program is part of Windows. Blocking it can break Windows Update, sign-in, DNS or other networking " +
                   "for everything that shares it (svchost.exe hosts many services).";
        if (!Ui.Confirm(msg, "Block program", "Block", "Cancel", danger: true)) return;

        Ui.Run(async () =>
        {
            var (ok, message) = await Change.AddFirewallBlockAsync(ruleName, path, null, "firewall." + exe + "." + tag, "Block outbound for " + path);
            Status.Set(ok ? $"Outbound traffic from {exe} is now blocked" : "Could not add the firewall rule: " + message,
                       ok ? StatusKind.Good : StatusKind.Bad);
        }, "Firewall block");
    }
}
