using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

/// <summary>What WMI and the registry say about one service. Built off the UI thread.</summary>
public sealed class SvcInfo
{
    public string Name = "";
    public string DisplayName = "";
    public string State = "";
    public string StartMode = "";      // Change.GetServiceStart form: Automatic | AutomaticDelayed | Manual | Disabled | Boot | System
    public bool Trigger;               // has trigger-start conditions
    public int Pid;
    public string PathName = "";
    public string Description = "";
    public string StartName = "";
    public string Exe = "";
    public bool ExeFound;
    public string Origin = "";         // Microsoft | Microsoft (outside Windows) | Third-party | ""
}

/// <summary>One row of the Services grid.</summary>
public sealed class SvcRow : INotifyPropertyChanged
{
    public SvcRow(SvcInfo i) { I = i; }

    public SvcInfo I { get; }
    public event PropertyChangedEventHandler PropertyChanged;
    void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Name => I.Name;
    public string DisplayName => I.DisplayName.Length > 0 ? I.DisplayName : I.Name;
    public string StateText => I.State.Length > 0 ? I.State : Fmt.Na;
    public string StateTone => I.State == "Running" ? "Good" : I.State == "Stopped" ? "Dim" : I.State.Length == 0 ? "Dim" : "Warn";
    public string StartText => SvcQuery.StartLabel(I.StartMode, I.Trigger);
    public string StartTone => I.StartMode == "Disabled" ? "Dim" : "Text";
    public int Pid => I.Pid;
    public string PidText => I.Pid > 0 ? I.Pid.ToString() : Fmt.Na;
    public string PidTone => I.Pid > 0 ? "Accent" : "Dim";
    public string LogOn => I.StartName.Length > 0 ? I.StartName : Fmt.Na;
    public string Description => I.Description.Length > 0 ? I.Description : Fmt.Na;
    public string Telemetry => SvcQuery.TelemetryNote(I.Name) != null ? "Telemetry" : "";
    public string TelemetryTip => SvcQuery.TelemetryNote(I.Name);
    public string Origin => I.Origin.Length > 0 ? I.Origin : Fmt.Na;
    public string OriginTone => I.Origin == "Microsoft" ? "Dim" : I.Origin.Length == 0 ? "Dim" : "Text";
    public string PathName => I.PathName.Length > 0 ? I.PathName : Fmt.Na;
    public bool IsProtected => SvcQuery.IsProtected(I.Name);

    public void SetState(string state, int pid)
    {
        if (state == I.State && pid == I.Pid) return;
        I.State = state ?? "";
        I.Pid = pid;
        On(nameof(StateText)); On(nameof(StateTone)); On(nameof(Pid)); On(nameof(PidText)); On(nameof(PidTone));
    }

    public void SetStart(string mode, bool trigger)
    {
        I.StartMode = mode ?? "";
        I.Trigger = trigger;
        On(nameof(StartText)); On(nameof(StartTone));
    }

    public void Retone() { On(nameof(StateTone)); On(nameof(StartTone)); On(nameof(PidTone)); On(nameof(OriginTone)); }
}

/// <summary>Palette key name to the current theme brush.</summary>
public sealed class SvcToneBrush : IValueConverter
{
    public static readonly SvcToneBrush Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string key && key.Length > 0 && Application.Current != null && Application.Current.TryFindResource(key) is Brush b)
            return b;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>WMI and registry reads for the Services page. Everything here runs off the UI thread.</summary>
public static class SvcQuery
{
    // Services Windows cannot run without, or that keep the PC secure. Winnow will not stop or disable these.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "Winmgmt", "EventLog", "Dhcp", "Dnscache", "nsi", "BFE",
        "MpsSvc", "WinDefend", "SecurityHealthService", "wuauserv", "CryptSvc", "TrustedInstaller", "ProfSvc", "Power",
        "PlugPlay", "Schedule", "gpsvc", "AudioSrv", "AudioEndpointBuilder", "Themes", "UserManager", "CoreMessagingRegistrar",
        "StateRepository", "BrokerInfrastructure", "SystemEventsBroker", "TimeBrokerSvc", "WdNisSvc", "wscsvc",
        // Elevation (Winnow's own UAC prompt), Windows Update, installers, Store apps, keys/credentials, networking.
        "Appinfo", "BITS", "UsoSvc", "WaaSMedicSvc", "msiserver", "AppXSvc", "ClipSVC", "KeyIso", "VaultSvc",
        "netprofm", "NlaSvc", "WlanSvc",
    };

    // Only services Microsoft itself documents as diagnostic-data components
    // ("Configure Windows diagnostic data in your organization").
    static readonly Dictionary<string, string> TelemetryServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DiagTrack"] = "Connected User Experiences and Telemetry - collects Windows diagnostic data and sends it to Microsoft.",
        ["WerSvc"] = "Windows Error Reporting - sends crash and hang reports (and, with optional diagnostic data, crash dumps) to Microsoft.",
    };

    public static bool IsProtected(string name) => name != null && Protected.Contains(name);

    /// <summary>Why a service is tagged as telemetry, or null if it is not.</summary>
    public static string TelemetryNote(string name) =>
        name != null && TelemetryServices.TryGetValue(name, out var why) ? why : null;

    public static string StartLabel(string mode, bool trigger)
    {
        string s = mode switch
        {
            "AutomaticDelayed" => "Automatic (delayed)",
            "Automatic" => "Automatic",
            "Manual" => "Manual",
            "Disabled" => "Disabled",
            "Boot" => "Boot",
            "System" => "System",
            null => Fmt.Na,
            "" => Fmt.Na,
            _ => mode,
        };
        if (trigger && mode != "Disabled" && !string.IsNullOrEmpty(mode)) s += " (trigger)";
        return s;
    }

    static string FromWmiStartMode(string m) => m switch
    {
        "Auto" => "Automatic",
        "Manual" => "Manual",
        "Disabled" => "Disabled",
        "Boot" => "Boot",
        "System" => "System",
        _ => "",
    };

    static string S(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop] as string ?? ""; } catch { return ""; }
    }

    static int I32(ManagementBaseObject mo, string prop)
    {
        try { object v = mo[prop]; return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    static bool HasTriggers(string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name + @"\TriggerInfo");
            return k != null && k.SubKeyCount > 0;
        }
        catch { return false; }
    }

    /// <summary>Every service with its start type and origin. Throws if WMI is unavailable.</summary>
    public static List<SvcInfo> LoadAll()
    {
        var list = new List<SvcInfo>();
        using (var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, State, StartMode, ProcessId, PathName, Description, StartName FROM Win32_Service"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    list.Add(new SvcInfo
                    {
                        Name = S(mo, "Name"),
                        DisplayName = S(mo, "DisplayName"),
                        State = S(mo, "State"),
                        StartMode = S(mo, "StartMode"),
                        Pid = I32(mo, "ProcessId"),
                        PathName = S(mo, "PathName"),
                        Description = S(mo, "Description"),
                        StartName = S(mo, "StartName"),
                    });
                }
            }
        }

        string winDir = Paths.WindowsDir.TrimEnd('\\') + "\\";
        foreach (var s in list)
        {
            // The registry tells delayed-automatic apart from automatic; WMI's StartMode does not.
            string precise = s.Name.Length > 0 ? Change.GetServiceStart(s.Name) : null;
            s.StartMode = precise ?? FromWmiStartMode(s.StartMode);
            s.Trigger = s.Name.Length > 0 && HasTriggers(s.Name);

            s.Exe = StartupScan.ResolveExe(s.PathName, out bool found);
            s.ExeFound = found;
            if (!found) s.Origin = "";
            else
            {
                string company = StartupScan.CompanyOf(s.Exe);
                bool ms = company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
                bool inWindows = s.Exe.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
                s.Origin = ms && inWindows ? "Microsoft" : ms ? "Microsoft (outside Windows)" : "Third-party";
            }
        }
        return list;
    }

    /// <summary>Name → (State, ProcessId) for every service. Cheap enough for a 5-second refresh.</summary>
    public static Dictionary<string, (string state, int pid)> LoadStates()
    {
        var map = new Dictionary<string, (string state, int pid)>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT Name, State, ProcessId FROM Win32_Service");
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                string n = S(mo, "Name");
                if (n.Length > 0) map[n] = (S(mo, "State"), I32(mo, "ProcessId"));
            }
        }
        return map;
    }

    /// <summary>State and PID of one service, or ("", 0) if it cannot be read.</summary>
    public static (string state, int pid) StateOf(string name)
    {
        try
        {
            string esc = name.Replace("\\", "\\\\").Replace("'", "\\'");
            using var searcher = new ManagementObjectSearcher("SELECT State, ProcessId FROM Win32_Service WHERE Name = '" + esc + "'");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo) return (S(mo, "State"), I32(mo, "ProcessId"));
            }
        }
        catch (Exception ex) { Log.Write("service state " + name + ": " + ex.Message); }
        return ("", 0);
    }

    public static (string mode, bool trigger) StartOf(string name) => (Change.GetServiceStart(name) ?? "", HasTriggers(name));
}

/// <summary>Every Windows service: state, start type, process, account, origin - with start/stop and start-type changes.</summary>
public sealed class ServicesView : UserControl, IView
{
    readonly ObservableCollection<SvcRow> _rows = new();
    readonly Dictionary<string, SvcRow> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly ListCollectionView _view;
    readonly DataGrid _grid;
    readonly TextBox _filter;
    readonly ComboBox _stateFilter;
    readonly ToggleButton _thirdParty, _telemetry;
    readonly TextBlock _summary;
    readonly Button _refresh, _start, _stop, _restart, _startType, _open, _search, _copy;
    readonly MenuItem _miStart, _miStop, _miRestart, _miStartType, _miOpen, _miSearch, _miCopy;
    readonly DispatcherTimer _timer;
    bool _loaded, _loading, _polling, _shown, _busy;
    string _pending;

    static readonly (string mode, string label)[] StartModes =
    {
        ("Automatic", "Automatic"), ("AutomaticDelayed", "Automatic (delayed)"), ("Manual", "Manual"), ("Disabled", "Disabled"),
    };

    public ServicesView()
    {
        _view = new ListCollectionView(_rows) { Filter = Matches };
        _view.SortDescriptions.Add(new SortDescription("DisplayName", ListSortDirection.Ascending));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = Ui.Header("Services",
            "Every Windows service, what state it is in and how it starts. Start-type changes are recorded and can be undone; " +
            "services Windows depends on cannot be stopped or disabled here.");
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ---- toolbar
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        _refresh = Bar(bar, Ui.Button("Refresh", (_, _) => Ui.Run(LoadAsync, "Services refresh"), null, "Read every service again"));
        _start = Bar(bar, Ui.Button("Start", (_, _) => Ui.Run(StartAsync, "Start service")));
        _stop = Bar(bar, Ui.Button("Stop", (_, _) => Ui.Run(StopAsync, "Stop service")));
        _restart = Bar(bar, Ui.Button("Restart", (_, _) => Ui.Run(RestartAsync, "Restart service")));
        _startType = Bar(bar, Ui.Button("Start type ▾", (_, _) => ShowStartTypeMenu(), null, "Automatic, delayed, manual or disabled"));
        _open = Bar(bar, Ui.Button("Open file location", (_, _) => OpenLocation()));
        _search = Bar(bar, Ui.Button("Search online", (_, _) => SearchOnline()));
        _copy = Bar(bar, Ui.Button("Copy", (_, _) => CopySelected(), null, "Copy the selected rows (Ctrl+C)"));

        var filterLabel = Ui.Text("Filter", "Sub");
        filterLabel.Margin = new Thickness(10, 0, 6, 6);
        bar.Children.Add(filterLabel);
        _filter = new TextBox { Width = 190, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Name, display name, description or path" };
        _filter.TextChanged += (_, _) => ApplyFilter();
        bar.Children.Add(_filter);

        _stateFilter = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 8, 6) };
        _stateFilter.Items.Add(new ComboBoxItem { Content = "All", Tag = "" });
        _stateFilter.Items.Add(new ComboBoxItem { Content = "Running", Tag = "Running" });
        _stateFilter.Items.Add(new ComboBoxItem { Content = "Stopped", Tag = "Stopped" });
        _stateFilter.SelectedIndex = 0;
        _stateFilter.SelectionChanged += (_, _) => ApplyFilter();
        bar.Children.Add(_stateFilter);

        _thirdParty = new ToggleButton { Content = "Third-party only", Margin = new Thickness(0, 0, 4, 6),
            ToolTip = "Hide services whose program is Microsoft's and lives in the Windows folder" };
        _thirdParty.Checked += (_, _) => ApplyFilter();
        _thirdParty.Unchecked += (_, _) => ApplyFilter();
        bar.Children.Add(_thirdParty);
        _telemetry = new ToggleButton { Content = "Telemetry only", Margin = new Thickness(0, 0, 4, 6),
            ToolTip = "Services Microsoft documents as sending diagnostic data" };
        _telemetry.Checked += (_, _) => ApplyFilter();
        _telemetry.Unchecked += (_, _) => ApplyFilter();
        bar.Children.Add(_telemetry);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        _summary = Ui.Text("Not read yet.", "Sub");
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(_summary, 2);
        root.Children.Add(_summary);

        // ---- grid
        _grid = new DataGrid { ItemsSource = _view };
        _grid.Columns.Add(TextCol("Display name", "DisplayName", 250, true));
        _grid.Columns.Add(TextCol("Name", "Name", 150, false));
        _grid.Columns.Add(ToneCol("Status", "StateText", "StateTone", 90));
        _grid.Columns.Add(ToneCol("Start type", "StartText", "StartTone", 150));
        _grid.Columns.Add(PidColumn());
        _grid.Columns.Add(TextCol("Log on as", "LogOn", 170, true));
        _grid.Columns.Add(TelemetryColumn());
        _grid.Columns.Add(ToneCol("Origin", "Origin", "OriginTone", 130));
        _grid.Columns.Add(TextCol("Description", "Description", 360, true));
        _grid.Columns.Add(TextCol("Path", "PathName", 360, true));
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (Ui.Ctrl(e, Key.C)) { CopySelected(); e.Handled = true; }
        };

        var menu = new ContextMenu();
        _miStart = AddItem(menu, "Start", (_, _) => Ui.Run(StartAsync, "Start service"));
        _miStop = AddItem(menu, "Stop", (_, _) => Ui.Run(StopAsync, "Stop service"));
        _miRestart = AddItem(menu, "Restart", (_, _) => Ui.Run(RestartAsync, "Restart service"));
        menu.Items.Add(new Separator());
        _miStartType = new MenuItem { Header = "Set start type" };
        foreach (var (mode, label) in StartModes)
        {
            var sub = new MenuItem { Header = label, Tag = mode };
            sub.Click += (s, _) => Ui.Run(() => SetStartTypeAsync((string)((MenuItem)s).Tag), "Set start type");
            _miStartType.Items.Add(sub);
        }
        menu.Items.Add(_miStartType);
        menu.Items.Add(new Separator());
        _miOpen = AddItem(menu, "Open file location", (_, _) => OpenLocation());
        _miSearch = AddItem(menu, "Search online", (_, _) => SearchOnline());
        _miCopy = AddItem(menu, "Copy", (_, _) => CopySelected());
        menu.Items.Add(new Separator());
        AddItem(menu, "Refresh", (_, _) => Ui.Run(LoadAsync, "Services refresh"));
        _grid.ContextMenu = menu;

        Grid.SetRow(_grid, 3);
        root.Children.Add(_grid);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Ui.Run(PollStatesAsync, "Service state refresh");

        UpdateButtons();
        Palette.Changed += () => { foreach (var r in _rows) r.Retone(); };
    }

    // ------------------------------------------------------------ building

    static Button Bar(WrapPanel bar, Button b)
    {
        b.Margin = new Thickness(0, 0, 6, 6);
        bar.Children.Add(b);
        return b;
    }

    static MenuItem AddItem(ContextMenu m, string text, RoutedEventHandler click)
    {
        var mi = new MenuItem { Header = text };
        mi.Click += click;
        m.Items.Add(mi);
        return mi;
    }

    static DataGridTextColumn TextCol(string header, string path, double width, bool tip)
    {
        var st = new Style(typeof(TextBlock));
        st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        st.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        if (tip) st.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path) { Mode = BindingMode.OneWay }));
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            SortMemberPath = path,
            Width = new DataGridLength(width),
            ElementStyle = st,
        };
    }

    static DataGridTemplateColumn ToneCol(string header, string path, string tonePath, double width)
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        f.SetBinding(TextBlock.ForegroundProperty, new Binding(tonePath) { Mode = BindingMode.OneWay, Converter = SvcToneBrush.Instance });
        f.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        f.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = new DataTemplate { VisualTree = f },
            SortMemberPath = path,
            Width = new DataGridLength(width),
        };
    }

    DataGridTemplateColumn PidColumn()
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new Binding("PidText") { Mode = BindingMode.OneWay });
        f.SetBinding(TextBlock.ForegroundProperty, new Binding("PidTone") { Mode = BindingMode.OneWay, Converter = SvcToneBrush.Instance });
        f.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
        f.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        f.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        f.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        f.SetValue(FrameworkElement.ToolTipProperty, "Show this process on the Processes page");
        f.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(PidClicked));
        return new DataGridTemplateColumn
        {
            Header = "PID",
            CellTemplate = new DataTemplate { VisualTree = f },
            SortMemberPath = "Pid",
            Width = new DataGridLength(70),
        };
    }

    static DataGridTemplateColumn TelemetryColumn()
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new Binding("Telemetry") { Mode = BindingMode.OneWay });
        f.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TelemetryTip") { Mode = BindingMode.OneWay });
        f.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        f.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        f.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataGridTemplateColumn
        {
            Header = "Telemetry",
            CellTemplate = new DataTemplate { VisualTree = f },
            SortMemberPath = "Telemetry",
            Width = new DataGridLength(84),
        };
    }

    void PidClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SvcRow r && r.Pid > 0)
        {
            e.Handled = true;
            Nav.Go("processes", r.Pid);
        }
    }

    // ------------------------------------------------------------ IView

    public void OnShown()
    {
        _shown = true;
        if (!_loaded && !_loading) Ui.Run(LoadAsync, "Services scan");
        _timer.Start();
    }

    public void OnHidden()
    {
        _shown = false;
        _timer.Stop();
    }

    public void Receive(object arg)
    {
        if (arg is not string name || name.Length == 0) return;
        if (!_loaded) { _pending = name; return; }
        SelectService(name);
    }

    void SelectService(string name)
    {
        if (!_byName.TryGetValue(name, out var row))
        {
            Status.Set("No service called " + name + " on this PC.", StatusKind.Warn);
            return;
        }
        if (!Matches(row))
        {
            // Clear the filters so the service can be shown.
            _filter.Text = "";
            _stateFilter.SelectedIndex = 0;
            _thirdParty.IsChecked = false;
            _telemetry.IsChecked = false;
            ApplyFilter();
        }
        _grid.SelectedItems.Clear();
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row);
        _grid.Focus();
    }

    // ------------------------------------------------------------ loading

    async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _refresh.IsEnabled = false;
        _summary.Text = "Reading services…";
        Status.Set("Reading services…");
        try
        {
            List<SvcInfo> list;
            try { list = await Task.Run(() => SvcQuery.LoadAll()); }
            catch (Exception ex)
            {
                Log.Write("services load: " + ex);
                _summary.Text = "Services could not be read: " + ex.Message;
                Status.Set("Services could not be read: " + ex.Message, StatusKind.Bad);
                return;
            }

            var keep = new HashSet<string>(Selected().Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            _rows.Clear();
            _byName.Clear();
            foreach (var i in list)
            {
                if (i.Name.Length == 0 || _byName.ContainsKey(i.Name)) continue;
                var row = new SvcRow(i);
                _byName[i.Name] = row;
                _rows.Add(row);
            }
            foreach (var r in _rows)
                if (keep.Contains(r.Name) && _view.Contains(r)) _grid.SelectedItems.Add(r);
            _loaded = true;
            Status.Set(_rows.Count + " services", StatusKind.Good);
        }
        finally
        {
            _loading = false;
            _refresh.IsEnabled = true;
            UpdateSummary();
            UpdateButtons();
        }
        if (_pending != null)
        {
            string p = _pending;
            _pending = null;
            SelectService(p);
        }
    }

    async Task PollStatesAsync()
    {
        if (!_shown || !_loaded || _loading || _polling || _busy) return;
        _polling = true;
        try
        {
            Dictionary<string, (string state, int pid)> states;
            try { states = await Task.Run(() => SvcQuery.LoadStates()); }
            catch (Exception ex) { Log.Write("service poll: " + ex.Message); return; }
            if (_loading) return;
            bool changed = false;
            foreach (var kv in states)
            {
                if (!_byName.TryGetValue(kv.Key, out var row)) continue;
                if (row.I.State != kv.Value.state || row.I.Pid != kv.Value.pid)
                {
                    row.SetState(kv.Value.state, kv.Value.pid);
                    changed = true;
                }
            }
            if (changed)
            {
                // Only a state filter depends on the polled values.
                if (StateFilter.Length > 0) _view.Refresh();
                UpdateSummary();
                UpdateButtons();
            }
        }
        finally { _polling = false; }
    }

    // ------------------------------------------------------------ filter + summary

    string StateFilter => (_stateFilter?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    bool Matches(object o)
    {
        if (o is not SvcRow r) return false;
        string sf = StateFilter;
        if (sf.Length > 0 && r.I.State != sf) return false;
        if (_thirdParty != null && _thirdParty.IsChecked == true && r.I.Origin == "Microsoft") return false;
        if (_telemetry != null && _telemetry.IsChecked == true && r.Telemetry.Length == 0) return false;
        string q = _filter?.Text.Trim() ?? "";
        if (q.Length == 0) return true;
        return Has(r.Name, q) || Has(r.DisplayName, q) || Has(r.I.Description, q) || Has(r.I.PathName, q) || Has(r.I.StartName, q);
    }

    static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

    void ApplyFilter()
    {
        if (_view == null) return;
        _view.Refresh();
        UpdateSummary();
    }

    void UpdateSummary()
    {
        if (_summary == null || !_loaded) return;
        int total = _rows.Count;
        int running = _rows.Count(r => r.I.State == "Running");
        int disabled = _rows.Count(r => r.I.StartMode == "Disabled");
        int third = _rows.Count(r => r.I.Origin != "Microsoft");
        int shown = _view.Count;
        var sb = new StringBuilder();
        sb.Append(total).Append(" services · ").Append(running).Append(" running · ").Append(disabled).Append(" disabled · ")
          .Append(third).Append(" not from Windows itself");
        if (shown != total) sb.Append(" · ").Append(shown).Append(" shown");
        if (!Elevation.IsAdmin) sb.Append(" · not elevated: starting, stopping and start-type changes will be refused by Windows");
        _summary.Text = sb.ToString();
    }

    // ------------------------------------------------------------ actions

    List<SvcRow> Selected() => _grid.SelectedItems.OfType<SvcRow>().ToList();

    void UpdateButtons()
    {
        var sel = Selected();
        bool any = sel.Count > 0 && !_busy;
        _start.IsEnabled = _miStart.IsEnabled = any && sel.Any(r => r.I.State != "Running" && r.I.StartMode != "Disabled");
        _stop.IsEnabled = _miStop.IsEnabled = any && sel.Any(r => r.I.State == "Running");
        _restart.IsEnabled = _miRestart.IsEnabled = any && sel.Any(r => r.I.State == "Running");
        _startType.IsEnabled = _miStartType.IsEnabled = any;
        _open.IsEnabled = _miOpen.IsEnabled = sel.Count > 0;
        _search.IsEnabled = _miSearch.IsEnabled = sel.Count > 0;
        _copy.IsEnabled = _miCopy.IsEnabled = sel.Count > 0;
    }

    static string Names(IEnumerable<SvcRow> rows)
    {
        var all = rows.ToList();
        var lines = all.Take(12).Select(r => "  • " + r.DisplayName + "  (" + r.Name + ")").ToList();
        if (all.Count > 12) lines.Add("  … and " + (all.Count - 12) + " more");
        return string.Join("\n", lines);
    }

    /// <summary>Tell the user which selected services Winnow will not stop or disable, and why. Returns the rest.</summary>
    static List<SvcRow> WithoutProtected(List<SvcRow> rows, string verb)
    {
        var prot = rows.Where(r => r.IsProtected).ToList();
        if (prot.Count > 0)
        {
            Ui.Info("Winnow will not " + verb + " these services:\n\n" + Names(prot) +
                    "\n\nWindows needs them to boot, sign in, keep its network and security working, or install updates. " +
                    "Stopping or disabling them can leave the PC unusable or unprotected.",
                    "Protected services");
        }
        return rows.Where(r => !r.IsProtected).ToList();
    }

    static string Brief(string output)
    {
        var lines = (output ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return "no output";
        // sc.exe puts the reason on the last line, e.g. "Access is denied."
        return lines.Count >= 2 ? lines[0] + " - " + lines[lines.Count - 1] : lines[0];
    }

    /// <summary>Wait (up to the timeout) for a service to reach a state, updating its row as it goes.</summary>
    async Task<string> WaitForState(SvcRow row, string want, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string state = "";
        while (true)
        {
            var name = row.Name;
            var (st, pid) = await Task.Run(() => SvcQuery.StateOf(name));
            state = st;
            if (st.Length > 0) row.SetState(st, pid);
            if (st == want || sw.ElapsedMilliseconds >= timeoutMs) break;
            await Task.Delay(500);
        }
        return state;
    }

    async Task RunBusy(Func<Task> work)
    {
        _busy = true;
        UpdateButtons();
        try { await work(); }
        finally
        {
            _busy = false;
            UpdateSummary();
            UpdateButtons();
        }
    }

    async Task<bool> ScStartAsync(SvcRow r, List<string> errors)
    {
        var res = await Shell.RunAsync(Paths.SystemExe("sc.exe"), "start \"" + r.Name + "\"", 30_000);
        if (!res.Ok && !res.Output.Contains("1056"))   // 1056 = already running
        {
            errors.Add(r.DisplayName + ": " + Brief(res.Output));
            await WaitForState(r, "Running", 0);
            return false;
        }
        string st = await WaitForState(r, "Running", 15_000);
        if (st != "Running") { errors.Add(r.DisplayName + ": still " + (st.Length > 0 ? st : "in an unknown state") + " after 15 s"); return false; }
        return true;
    }

    async Task<bool> ScStopAsync(SvcRow r, List<string> errors)
    {
        var res = await Shell.RunAsync(Paths.SystemExe("sc.exe"), "stop \"" + r.Name + "\"", 30_000);
        if (!res.Ok && !res.Output.Contains("1062"))   // 1062 = not started
        {
            string why = res.Output.Contains("1051") ? "other running services depend on it - stop those first" : Brief(res.Output);
            errors.Add(r.DisplayName + ": " + why);
            await WaitForState(r, "Stopped", 0);
            return false;
        }
        string st = await WaitForState(r, "Stopped", 20_000);
        if (st != "Stopped") { errors.Add(r.DisplayName + ": still " + (st.Length > 0 ? st : "in an unknown state") + " after 20 s"); return false; }
        return true;
    }

    void Report(string verb, int ok, List<string> errors)
    {
        if (errors.Count == 0)
        {
            if (ok > 0) Status.Set(verb + " " + ok + (ok == 1 ? " service" : " services"), StatusKind.Good);
            return;
        }
        Status.Set(verb + " " + ok + ", " + errors.Count + " failed: " + errors[0], StatusKind.Bad);
        if (errors.Count > 1) Ui.Error(string.Join("\n", errors), "Some services were not changed");
    }

    async Task StartAsync()
    {
        var rows = Selected().Where(r => r.I.State != "Running").ToList();
        var disabled = rows.Where(r => r.I.StartMode == "Disabled").ToList();
        rows = rows.Where(r => r.I.StartMode != "Disabled").ToList();
        if (disabled.Count > 0)
            Status.Set(disabled[0].DisplayName + " is disabled - set its start type to Manual or Automatic first.", StatusKind.Warn);
        if (rows.Count == 0) return;
        await RunBusy(async () =>
        {
            int ok = 0;
            var errors = new List<string>();
            foreach (var r in rows)
            {
                Status.Set("Starting " + r.DisplayName + "…");
                if (await ScStartAsync(r, errors)) ok++;
            }
            Report("Started", ok, errors);
        });
    }

    async Task StopAsync()
    {
        var rows = WithoutProtected(Selected().Where(r => r.I.State == "Running").ToList(), "stop");
        if (rows.Count == 0) return;
        string msg = "Stop " + (rows.Count == 1 ? "this service" : "these " + rows.Count + " services") + "?\n\n" + Names(rows) +
                     "\n\nAnything that relies on " + (rows.Count == 1 ? "it" : "them") + " stops working until " +
                     (rows.Count == 1 ? "it is" : "they are") + " started again. The start type is not changed, so " +
                     (rows.Count == 1 ? "it" : "they") + " may start again on the next restart.";
        if (!Ui.Confirm(msg, "Stop services", "Stop", danger: true)) return;
        await RunBusy(async () =>
        {
            int ok = 0;
            var errors = new List<string>();
            foreach (var r in rows)
            {
                Status.Set("Stopping " + r.DisplayName + "…");
                if (await ScStopAsync(r, errors)) ok++;
            }
            Report("Stopped", ok, errors);
        });
    }

    async Task RestartAsync()
    {
        var rows = WithoutProtected(Selected().Where(r => r.I.State == "Running").ToList(), "restart");
        if (rows.Count == 0) return;
        string msg = "Restart " + (rows.Count == 1 ? "this service" : "these " + rows.Count + " services") + "?\n\n" + Names(rows) +
                     "\n\nEach one is stopped and started again. Anything using it is interrupted for a moment.";
        if (!Ui.Confirm(msg, "Restart services", "Restart", danger: true)) return;
        await RunBusy(async () =>
        {
            int ok = 0;
            var errors = new List<string>();
            foreach (var r in rows)
            {
                Status.Set("Restarting " + r.DisplayName + "…");
                if (await ScStopAsync(r, errors) && await ScStartAsync(r, errors)) ok++;
            }
            Report("Restarted", ok, errors);
        });
    }

    void ShowStartTypeMenu()
    {
        var m = new ContextMenu { PlacementTarget = _startType, Placement = PlacementMode.Bottom };
        foreach (var (mode, label) in StartModes)
        {
            var mi = new MenuItem { Header = label, Tag = mode };
            mi.Click += (s, _) => Ui.Run(() => SetStartTypeAsync((string)((MenuItem)s).Tag), "Set start type");
            m.Items.Add(mi);
        }
        m.IsOpen = true;
    }

    async Task SetStartTypeAsync(string mode)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        string label = StartModes.First(x => x.mode == mode).label;
        var rows = mode == "Disabled" ? WithoutProtected(sel, "disable") : sel;
        rows = rows.Where(r => r.I.StartMode != mode).ToList();
        if (rows.Count == 0) { Status.Set("Already " + label + ".", StatusKind.Info); return; }

        if (mode == "Disabled")
        {
            string msg = "Disable " + (rows.Count == 1 ? "this service" : "these " + rows.Count + " services") + "?\n\n" + Names(rows) +
                         "\n\n" + (rows.Count == 1 ? "It is" : "They are") + " stopped now and will not start again - not even when " +
                         "Windows or an app asks for " + (rows.Count == 1 ? "it" : "them") + " - until the start type is changed back. " +
                         "Features that depend on " + (rows.Count == 1 ? "it" : "them") + " stop working.\n\n" +
                         "This is recorded and can be undone from the change history.";
            if (!Ui.Confirm(msg, "Disable services", "Disable", danger: true)) return;
        }
        else if (mode == "Manual" && rows.Any(r => r.IsProtected))
        {
            var prot = rows.Where(r => r.IsProtected).ToList();
            string msg = "These are services Windows depends on:\n\n" + Names(prot) +
                         "\n\nSet to Manual they only start when something asks for them, which can slow sign-in or break features " +
                         "that expect them already running. This is recorded and can be undone from the change history.";
            if (!Ui.Confirm(msg, "Change a protected service", "Set to Manual", danger: true)) return;
        }

        await RunBusy(async () =>
        {
            int ok = 0;
            var errors = new List<string>();
            foreach (var r in rows)
            {
                Status.Set("Setting " + r.DisplayName + " to " + label + "…");
                var (good, message) = await Change.SetServiceStartAsync(r.Name, mode, "services." + r.Name,
                    "Start type " + r.DisplayName + " → " + label);
                var name = r.Name;
                var (now, trig) = await Task.Run(() => SvcQuery.StartOf(name));
                r.SetStart(now, trig);
                if (good && now == mode) ok++;
                else if (good) errors.Add(r.DisplayName + ": Windows still reports " + SvcQuery.StartLabel(now, false));
                else errors.Add(r.DisplayName + ": " + message);
                if (mode == "Disabled") await WaitForState(r, "Stopped", 10_000);
            }
            Report("Set " + label + " on", ok, errors);
        });
    }

    void OpenLocation()
    {
        var r = Selected().FirstOrDefault();
        if (r == null) return;
        if (r.I.ExeFound) Shell.Reveal(r.I.Exe);
        else Status.Set("The program for " + r.DisplayName + " was not found on disk.", StatusKind.Warn);
    }

    void SearchOnline()
    {
        var r = Selected().FirstOrDefault();
        if (r == null) return;
        string q = r.DisplayName.Equals(r.Name, StringComparison.OrdinalIgnoreCase)
            ? r.Name + " Windows service"
            : "\"" + r.DisplayName + "\" " + r.Name + " service";
        Shell.Open("https://duckduckgo.com/?q=" + Uri.EscapeDataString(q));
    }

    void CopySelected()
    {
        var rows = Selected();
        if (rows.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("Display name\tName\tStatus\tStart type\tPID\tLog on as\tOrigin\tDescription\tPath");
        foreach (var r in rows)
            sb.Append(r.DisplayName).Append('\t').Append(r.Name).Append('\t').Append(r.StateText).Append('\t').Append(r.StartText).Append('\t')
              .Append(r.PidText).Append('\t').Append(r.LogOn).Append('\t').Append(r.Origin).Append('\t').Append(r.Description).Append('\t')
              .Append(r.PathName).AppendLine();
        Ui.CopyToClipboard(sb.ToString());
    }
}
