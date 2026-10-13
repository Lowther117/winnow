using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

/// <summary>One row of the Startup apps grid.</summary>
public sealed class StartupRow : INotifyPropertyChanged
{
    public StartupRow(StartupEntry e) { E = e; }

    public StartupEntry E { get; }
    public event PropertyChangedEventHandler PropertyChanged;
    void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    ImageSource _icon;
    public ImageSource Icon { get => _icon; set { _icon = value; On(nameof(Icon)); } }

    string _running = Fmt.Na;
    public string Running { get => _running; set { _running = value; On(nameof(Running)); } }

    public string Name => E.Name;
    public string Publisher => E.Publisher.Length > 0 ? E.Publisher : Fmt.Na;
    public string StateText => E.Kind == "RunOnce" ? "Runs once" : E.Enabled ? "Enabled" : "Disabled";
    public string StateTone => E.Kind == "RunOnce" ? "Text" : E.Enabled ? "Good" : "Dim";
    public string Impact => "Not measured";
    public string ImpactTone => "Dim";
    public string Source => E.Kind == "Task" && E.Trigger.Length > 0 ? "Scheduled task (" + E.Trigger.ToLowerInvariant() + ")" : E.Source;
    public string Command => E.Command;
    public string Location => E.Location;
    public string Program => E.Target.Length == 0 ? Fmt.Na : E.TargetMissing ? "File missing: " + E.Target : E.Target;
    public string ProgramTone => E.TargetMissing ? "Bad" : "Text";
    public bool IsMicrosoft => E.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Stable identity across rescans, for keeping the selection.</summary>
    public string Key => E.Kind + "|" + E.Hive + "|" + E.KeyPath + "|" + E.ValueName + "|" + E.TaskPath + "|" + E.FilePath;

    public void StateChanged() { On(nameof(StateText)); On(nameof(StateTone)); }
    public void Retone() { On(nameof(StateTone)); On(nameof(ProgramTone)); On(nameof(ImpactTone)); }
}

/// <summary>Palette key name ("Good", "Bad", "Dim"...) to the current theme brush.</summary>
public sealed class StartupToneBrush : IValueConverter
{
    public static readonly StartupToneBrush Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string key && key.Length > 0 && Application.Current != null && Application.Current.TryFindResource(key) is Brush b)
            return b;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Everything that starts with Windows - Task Manager's Startup apps plus the other usual places.</summary>
public sealed class StartupView : UserControl, IView
{
    readonly ObservableCollection<StartupRow> _rows = new();
    readonly ListCollectionView _view;
    readonly DataGrid _grid;
    readonly TextBox _filter;
    readonly CheckBox _hideMs;
    readonly TextBlock _summary;
    readonly Button _refresh, _enable, _disable, _open, _search, _copy, _delete;
    readonly MenuItem _miEnable, _miDisable, _miOpen, _miSearch, _miCopy, _miDelete;
    string _taskError = "";
    bool _loaded, _loading;

    // Programs that host other things: "running" says nothing about this entry.
    static readonly HashSet<string> HostExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "explorer.exe", "conhost.exe", "svchost.exe", "regsvr32.exe", "msiexec.exe", "schtasks.exe", "reg.exe",
    };

    public StartupView()
    {
        _view = new ListCollectionView(_rows) { Filter = Matches };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = Ui.Header("Startup apps",
            "Everything that starts when Windows does: Run keys, the Startup folders and scheduled tasks that run at log on or boot. " +
            "Turning an entry off uses the same switch as Task Manager and can be undone.");
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ---- toolbar
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        _refresh = Bar(bar, Ui.Button("Refresh", (_, _) => Ui.Run(LoadAsync, "Startup refresh"), null, "Read every startup location again"));
        _enable = Bar(bar, Ui.Button("Enable", (_, _) => Ui.Run(() => SetEnabledAsync(true), "Enable startup entry")));
        _disable = Bar(bar, Ui.Button("Disable", (_, _) => Ui.Run(() => SetEnabledAsync(false), "Disable startup entry")));
        _open = Bar(bar, Ui.Button("Open file location", (_, _) => OpenLocation()));
        _search = Bar(bar, Ui.Button("Search online", (_, _) => SearchOnline()));
        _copy = Bar(bar, Ui.Button("Copy", (_, _) => CopySelected(), null, "Copy the selected rows (Ctrl+C)"));
        _delete = Bar(bar, Ui.Button("Delete entry…", (_, _) => DeleteSelected(), "Danger",
            "Delete the registry value or move the Startup-folder file out of the way (undoable)"));

        var filterLabel = Ui.Text("Filter", "Sub");
        filterLabel.Margin = new Thickness(10, 0, 6, 6);
        bar.Children.Add(filterLabel);
        _filter = new TextBox { Width = 200, Margin = new Thickness(0, 0, 10, 6), ToolTip = "Name, publisher, command or location" };
        _filter.TextChanged += (_, _) => { _view.Refresh(); UpdateSummary(); };
        bar.Children.Add(_filter);
        _hideMs = new CheckBox
        {
            Content = "Hide Microsoft entries",
            IsChecked = Settings.Current.Get("startup.hide_ms") == "1",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
        };
        _hideMs.Checked += (_, _) => HideMsChanged(true);
        _hideMs.Unchecked += (_, _) => HideMsChanged(false);
        bar.Children.Add(_hideMs);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        _summary = Ui.Text("Not read yet.", "Sub");
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(_summary, 2);
        root.Children.Add(_summary);

        // ---- grid
        _grid = new DataGrid { ItemsSource = _view };
        _grid.Columns.Add(IconColumn());
        _grid.Columns.Add(TextCol("Name", "Name", 190, false));
        _grid.Columns.Add(TextCol("Publisher", "Publisher", 160, false));
        _grid.Columns.Add(ToneCol("Status", "StateText", "StateTone", 86));
        _grid.Columns.Add(ToneCol("Startup impact", "Impact", "ImpactTone", 104,
            "Windows does not expose Task Manager's boot-time measurements, so Winnow does not guess at them."));
        _grid.Columns.Add(TextCol("Running now", "Running", 120, false));
        _grid.Columns.Add(TextCol("Source", "Source", 190, true));
        _grid.Columns.Add(ToneCol("Program", "Program", "ProgramTone", 260, null, true));
        _grid.Columns.Add(TextCol("Command", "Command", 300, true));
        _grid.Columns.Add(TextCol("Location", "Location", 300, true));
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindRow(d) != null) OpenLocation();
        };
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (Ui.Ctrl(e, Key.C)) { CopySelected(); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        };

        var menu = new ContextMenu();
        _miEnable = AddItem(menu, "Enable", (_, _) => Ui.Run(() => SetEnabledAsync(true), "Enable startup entry"));
        _miDisable = AddItem(menu, "Disable", (_, _) => Ui.Run(() => SetEnabledAsync(false), "Disable startup entry"));
        menu.Items.Add(new Separator());
        _miOpen = AddItem(menu, "Open file location", (_, _) => OpenLocation());
        _miSearch = AddItem(menu, "Search online", (_, _) => SearchOnline());
        _miCopy = AddItem(menu, "Copy", (_, _) => CopySelected());
        menu.Items.Add(new Separator());
        _miDelete = AddItem(menu, "Delete entry…", (_, _) => DeleteSelected());
        menu.Items.Add(new Separator());
        AddItem(menu, "Refresh", (_, _) => Ui.Run(LoadAsync, "Startup refresh"));
        _grid.ContextMenu = menu;

        Grid.SetRow(_grid, 3);
        root.Children.Add(_grid);

        Content = root;
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

    static DataGridTemplateColumn IconColumn()
    {
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new Binding("Icon") { Mode = BindingMode.OneWay });
        img.SetValue(FrameworkElement.WidthProperty, 16.0);
        img.SetValue(FrameworkElement.HeightProperty, 16.0);
        img.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataGridTemplateColumn
        {
            Header = "",
            CellTemplate = new DataTemplate { VisualTree = img },
            Width = new DataGridLength(34),
            CanUserSort = false,
            CanUserResize = false,
        };
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

    static DataGridTemplateColumn ToneCol(string header, string path, string tonePath, double width, string headerTip = null, bool tip = false)
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        f.SetBinding(TextBlock.ForegroundProperty, new Binding(tonePath) { Mode = BindingMode.OneWay, Converter = StartupToneBrush.Instance });
        f.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        f.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        if (tip) f.SetBinding(FrameworkElement.ToolTipProperty, new Binding(path) { Mode = BindingMode.OneWay });
        object head = header;
        if (headerTip != null) head = new TextBlock { Text = header, ToolTip = headerTip };
        return new DataGridTemplateColumn
        {
            Header = head,
            CellTemplate = new DataTemplate { VisualTree = f },
            SortMemberPath = path,
            Width = new DataGridLength(width),
        };
    }

    static DataGridRow FindRow(DependencyObject d)
    {
        while (d != null && d is not DataGridRow)
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as DataGridRow;
    }

    // ------------------------------------------------------------ IView

    public void OnShown()
    {
        if (!_loaded && !_loading) Ui.Run(LoadAsync, "Startup scan");
    }

    public void OnHidden() { }

    // ------------------------------------------------------------ loading

    async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _refresh.IsEnabled = false;
        _summary.Text = "Reading Run keys, Startup folders and scheduled tasks…";
        Status.Set("Reading startup entries…");
        try
        {
            var regTask = Task.Run(() => StartupScan.ScanRegistryAndFolders());
            var taskTask = Task.Run(() => StartupScan.ScanTasksAsync());
            var list = await regTask;
            var (tasks, taskError) = await taskTask;
            list.AddRange(tasks);
            _taskError = taskError ?? "";
            var running = await Task.Run(() => RunningByTarget(list));
            bool sampling = Sampler.Latest != null;

            var keep = new HashSet<string>(Selected().Select(r => r.Key));
            _rows.Clear();
            foreach (var e in list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new StartupRow(e);
                if (running.TryGetValue(e.Target, out var rt)) row.Running = rt;
                else if (sampling && e.TargetMissing) row.Running = "No";
                else row.Running = Fmt.Na;
                string iconPath = e.Target.Length > 0 && File.Exists(e.Target) ? e.Target : e.FilePath;
                row.Icon = ProcessInfo.Icon(iconPath);
                _rows.Add(row);
            }
            foreach (var r in _rows)
                if (keep.Contains(r.Key) && _view.Contains(r)) _grid.SelectedItems.Add(r);

            _loaded = true;
            if (_taskError.Length > 0)
                Status.Set("Startup: scheduled tasks could not be read (" + _taskError + ")", StatusKind.Warn);
            else
                Status.Set(_rows.Count + " startup entries found", StatusKind.Good);
        }
        finally
        {
            _loading = false;
            _refresh.IsEnabled = true;
            UpdateSummary();
            UpdateButtons();
        }
    }

    /// <summary>For each program path: "Yes · 120 MB" when a process from exactly that file is running now, else "No".</summary>
    static Dictionary<string, string> RunningByTarget(List<StartupEntry> list)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var snap = Sampler.Latest;
        if (snap == null) return res;
        var want = new HashSet<string>(
            list.Where(e => e.Target.Length > 0 && !e.TargetMissing && !HostExes.Contains(Path.GetFileName(e.Target)))
                .Select(e => e.Target),
            StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(want.Select(t => Path.GetFileName(t)), StringComparer.OrdinalIgnoreCase);
        var hits = new Dictionary<string, (int n, ulong mem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in snap.Processes.ToList())
        {
            if (p == null || !names.Contains(p.Name)) continue;
            var d = ProcessInfo.Get(p);
            if (string.IsNullOrEmpty(d.Path) || !want.Contains(d.Path)) continue;
            hits.TryGetValue(d.Path, out var c);
            hits[d.Path] = (c.n + 1, c.mem + p.PrivateWorkingSet);
        }
        foreach (var t in want)
        {
            if (hits.TryGetValue(t, out var h))
                res[t] = (h.n == 1 ? "Yes · " : "Yes (" + h.n + ") · ") + Fmt.Bytes(h.mem);
            else
                res[t] = "No";
        }
        return res;
    }

    // ------------------------------------------------------------ filter + summary

    bool Matches(object o)
    {
        if (o is not StartupRow r) return false;
        if (_hideMs != null && _hideMs.IsChecked == true && r.IsMicrosoft) return false;
        string q = _filter?.Text.Trim() ?? "";
        if (q.Length == 0) return true;
        return Has(r.Name, q) || Has(r.Publisher, q) || Has(r.Command, q) || Has(r.Location, q) || Has(r.Source, q) || Has(r.Program, q);
    }

    static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

    void HideMsChanged(bool on)
    {
        Settings.Current.Set("startup.hide_ms", on ? "1" : "0");
        _view.Refresh();
        UpdateSummary();
    }

    void UpdateSummary()
    {
        if (!_loaded) return;
        int total = _rows.Count;
        int enabled = _rows.Count(r => r.E.Enabled && r.E.Kind != "RunOnce");
        int disabled = _rows.Count(r => !r.E.Enabled);
        int missing = _rows.Count(r => r.E.TargetMissing);
        int shown = _view.Count;
        var sb = new StringBuilder();
        sb.Append(total).Append(total == 1 ? " entry" : " entries").Append(" · ")
          .Append(enabled).Append(" enabled · ").Append(disabled).Append(" disabled");
        if (missing > 0) sb.Append(" · ").Append(missing).Append(missing == 1 ? " points at a missing file" : " point at missing files");
        if (shown != total) sb.Append(" · ").Append(shown).Append(" shown");
        if (_taskError.Length > 0) sb.Append(" · scheduled tasks could not be read: ").Append(_taskError);
        if (!Elevation.IsAdmin) sb.Append(" · not elevated: HKLM entries and all-users items cannot be changed");
        _summary.Text = sb.ToString();
    }

    // ------------------------------------------------------------ actions

    List<StartupRow> Selected() => _grid.SelectedItems.OfType<StartupRow>().ToList();

    void UpdateButtons()
    {
        var sel = Selected();
        bool any = sel.Count > 0;
        bool canEnable = sel.Any(r => r.E.CanToggle && !r.E.Enabled);
        bool canDisable = sel.Any(r => r.E.CanToggle && r.E.Enabled);
        bool canDelete = sel.Any(r => r.E.CanDelete);
        _enable.IsEnabled = _miEnable.IsEnabled = canEnable;
        _disable.IsEnabled = _miDisable.IsEnabled = canDisable;
        _delete.IsEnabled = _miDelete.IsEnabled = canDelete;
        _open.IsEnabled = _miOpen.IsEnabled = any;
        _search.IsEnabled = _miSearch.IsEnabled = any;
        _copy.IsEnabled = _miCopy.IsEnabled = any;
    }

    static string Names(List<StartupRow> rows)
    {
        var lines = rows.Take(12).Select(r => "  • " + r.Name + "  (" + r.Source + ")").ToList();
        if (rows.Count > 12) lines.Add("  … and " + (rows.Count - 12) + " more");
        return string.Join("\n", lines);
    }

    async Task SetEnabledAsync(bool enable)
    {
        var rows = Selected().Where(r => r.E.CanToggle && r.E.Enabled != enable).ToList();
        if (rows.Count == 0)
        {
            Status.Set(enable ? "Nothing selected is turned off." : "Nothing selected can be turned off. RunOnce entries can only be deleted.", StatusKind.Warn);
            return;
        }
        if (!enable)
        {
            string msg = "Stop " + (rows.Count == 1 ? "this" : "these " + rows.Count) + " from starting with Windows?\n\n" + Names(rows) +
                         "\n\nNothing is deleted. Run-key and Startup-folder entries are switched off the same way Task Manager does it, " +
                         "and scheduled tasks are disabled. Turn them back on here, or undo from the change history.";
            if (!Ui.Confirm(msg, "Disable startup entries", "Disable", danger: true)) return;
        }

        int ok = 0;
        var errors = new List<string>();
        foreach (var r in rows)
        {
            if (r.E.Kind == "Task")
            {
                var res = await Change.SetTasksEnabledAsync(new[] { r.E.TaskPath }, enable, "startup." + r.E.Name,
                    (enable ? "Enable startup task: " : "Disable startup task: ") + r.E.Name);
                if (res.changed > 0) { r.E.Enabled = enable; ok++; }
                else if (res.missing > 0) errors.Add(r.E.Name + ": the task no longer exists");
                else if (!string.IsNullOrEmpty(res.detail)) errors.Add(r.E.Name + ": " + res.detail.Trim());
                else { r.E.Enabled = enable; ok++; }
            }
            else
            {
                var (good, message) = StartupScan.SetApproved(r.E, enable);
                if (good) { r.E.Enabled = enable; ok++; }
                else errors.Add(r.E.Name + ": " + message);
            }
            r.StateChanged();
        }

        string verb = enable ? "Enabled " : "Disabled ";
        if (errors.Count == 0)
            Status.Set(verb + ok + (ok == 1 ? " startup entry" : " startup entries"), StatusKind.Good);
        else
        {
            Status.Set(verb + ok + ", " + errors.Count + " failed: " + errors[0], StatusKind.Bad);
            if (errors.Count > 1) Ui.Error(string.Join("\n", errors), "Some entries were not changed");
        }
        UpdateSummary();
        UpdateButtons();
    }

    void DeleteSelected()
    {
        var sel = Selected();
        var rows = sel.Where(r => r.E.CanDelete).ToList();
        if (rows.Count == 0)
        {
            Status.Set(sel.Any(r => r.E.Kind == "Task")
                ? "Scheduled tasks are not deleted here. Disable them instead - that can be undone."
                : "Select an entry first.", StatusKind.Warn);
            return;
        }
        string dir = Path.Combine(Paths.AppDir, "Winnow-removed-startup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        bool anyFiles = rows.Any(r => r.E.Kind == "Folder");
        string msg = "Remove " + (rows.Count == 1 ? "this startup entry" : "these " + rows.Count + " startup entries") + "?\n\n" + Names(rows) +
                     "\n\nRegistry entries are deleted." +
                     (anyFiles ? " Startup-folder files are moved to:\n" + dir : "") +
                     "\n\nEach one can be put back from the change history." +
                     (rows.Count < sel.Count ? "\n\nScheduled tasks in the selection are skipped - disable them instead." : "");
        if (!Ui.Confirm(msg, "Remove startup entries", "Remove", danger: true)) return;

        int ok = 0;
        var errors = new List<string>();
        foreach (var r in rows)
        {
            (bool good, string message) res;
            if (r.E.Kind == "Folder")
                res = Change.MoveFile(r.E.FilePath, Path.Combine(dir, r.E.ValueName), "startup." + r.E.Name,
                    "Remove startup file: " + r.E.Name + " (moved to " + dir + ")");
            else
                res = Change.DeleteRegistryValue(r.E.Hive, r.E.KeyPath, r.E.ValueName, "startup." + r.E.Name,
                    "Remove startup entry: " + r.E.Name + " from " + r.E.Hive + "\\" + r.E.KeyPath);
            if (res.good) { ok++; _rows.Remove(r); }
            else errors.Add(r.E.Name + ": " + res.message);
        }
        if (errors.Count == 0)
            Status.Set("Removed " + ok + (ok == 1 ? " startup entry" : " startup entries") + " - undo from the change history", StatusKind.Good);
        else
        {
            Status.Set("Removed " + ok + ", " + errors.Count + " failed: " + errors[0], StatusKind.Bad);
            if (errors.Count > 1) Ui.Error(string.Join("\n", errors), "Some entries were not removed");
        }
        UpdateSummary();
        UpdateButtons();
    }

    void OpenLocation()
    {
        var r = Selected().FirstOrDefault();
        if (r == null) return;
        if (r.E.Target.Length > 0 && File.Exists(r.E.Target)) Shell.Reveal(r.E.Target);
        else if (r.E.FilePath.Length > 0 && File.Exists(r.E.FilePath)) Shell.Reveal(r.E.FilePath);
        else Status.Set("The program for " + r.Name + " is not on disk.", StatusKind.Warn);
    }

    void SearchOnline()
    {
        var r = Selected().FirstOrDefault();
        if (r == null) return;
        string exe = r.E.Target.Length > 0 ? Path.GetFileName(r.E.Target) : "";
        string q = exe.Length == 0 ? r.Name
                 : exe.Equals(r.Name + ".exe", StringComparison.OrdinalIgnoreCase) ? exe
                 : r.Name + " " + exe;
        Shell.Open("https://duckduckgo.com/?q=" + Uri.EscapeDataString(q + " startup"));
    }

    void CopySelected()
    {
        var rows = Selected();
        if (rows.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("Name\tPublisher\tStatus\tRunning now\tSource\tProgram\tCommand\tLocation");
        foreach (var r in rows)
            sb.Append(r.Name).Append('\t').Append(r.Publisher).Append('\t').Append(r.StateText).Append('\t').Append(r.Running).Append('\t')
              .Append(r.Source).Append('\t').Append(r.Program).Append('\t').Append(r.Command).Append('\t').Append(r.Location).AppendLine();
        Ui.CopyToClipboard(sb.ToString());
    }
}
