using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Installed Apps: uninstall classic programs and Store apps, remove preinstalled
/// extras (Bloatware), and find what uninstalled software left behind (Leftovers).
/// </summary>
public sealed class InstalledAppsView : UserControl, IView
{
    readonly TabControl _tabs = new();
    TabItem _programsTab, _storeTab, _bloatTab, _leftTab;

    // ---- Programs
    readonly ObservableCollection<AppsProgram> _programs = new();
    readonly ListCollectionView _programsView;
    DataGrid _programGrid;
    TextBox _programFilter;
    TextBlock _programCount;
    Button _uninstallBtn;
    bool _programsLoading, _programsLoaded, _uninstalling;

    // ---- Store apps
    readonly ObservableCollection<AppsStorePackage> _store = new();
    readonly ListCollectionView _storeView;
    DataGrid _storeGrid;
    TextBox _storeFilter;
    TextBlock _storeCount;
    CheckBox _storeAllUsers, _storeDeprovision;
    Button _storeRemoveBtn;
    AppsLog _storeLog;
    List<AppsStorePackage> _allPackages = new();
    List<AppsProvisioned> _provisioned = new();
    bool _storeLoading, _storeLoaded, _removing;

    // ---- Bloatware
    readonly StackPanel _bloatRows = new();
    readonly List<BloatRowUi> _bloat = new();
    TextBox _bloatFilter;
    TextBlock _bloatCount;
    CheckBox _bloatAllUsers, _bloatDeprovision;
    Button _bloatRemoveBtn;
    AppsLog _bloatLog;

    // ---- Leftovers
    readonly ObservableCollection<LeftoverItem> _left = new();
    readonly ListCollectionView _leftView;
    DataGrid _leftGrid;
    TextBox _leftFilter;
    TextBlock _leftStatus;
    ProgressBar _leftProgress;
    Button _scanBtn, _cancelBtn, _leftRemoveBtn;
    StackPanel _leftNotes;
    CancellationTokenSource _scanCts;
    int _scanId;

    public InstalledAppsView()
    {
        _programsView = new ListCollectionView(_programs);
        _programsView.SortDescriptions.Add(new SortDescription(nameof(AppsProgram.Name), ListSortDirection.Ascending));
        _programsView.Filter = o => o is AppsProgram p && ProgramMatches(p);
        _storeView = new ListCollectionView(_store);
        _storeView.SortDescriptions.Add(new SortDescription(nameof(AppsStorePackage.Name), ListSortDirection.Ascending));
        _storeView.Filter = o => o is AppsStorePackage s && StoreMatches(s);
        _leftView = new ListCollectionView(_left);
        _leftView.Filter = o => o is LeftoverItem l && LeftMatches(l);

        _programsTab = new TabItem { Header = "Programs", Content = BuildProgramsTab() };
        _storeTab = new TabItem { Header = "Store apps", Content = BuildStoreTab() };
        _bloatTab = new TabItem { Header = "Bloatware", Content = BuildBloatTab() };
        _leftTab = new TabItem { Header = "Leftovers", Content = BuildLeftoversTab() };
        _tabs.Items.Add(_programsTab);
        _tabs.Items.Add(_storeTab);
        _tabs.Items.Add(_bloatTab);
        _tabs.Items.Add(_leftTab);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Ui.Header("Installed Apps",
            "Uninstall programs and Store apps, clear out preinstalled extras, and find what uninstalled software left behind."));
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);
        Content = root;
    }

    // =================================================================== IView

    public void OnShown()
    {
        if (!_programsLoaded && !_programsLoading) Ui.Run(LoadProgramsAsync, "Reading installed programs");
        if (!_storeLoaded && !_storeLoading) Ui.Run(LoadStoreAsync, "Reading Store apps");
    }

    public void OnHidden() { }

    public void Receive(object arg)
    {
        if (arg is string s)
        {
            if (s == "leftovers") _tabs.SelectedItem = _leftTab;
            else if (s == "bloat" || s == "bloatware") _tabs.SelectedItem = _bloatTab;
            else if (s == "store") _tabs.SelectedItem = _storeTab;
            else if (s == "programs") _tabs.SelectedItem = _programsTab;
        }
    }

    // =================================================================== shared builders

    static Grid TabGrid(params bool[] starRows)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        foreach (bool star in starRows)
            g.RowDefinitions.Add(new RowDefinition { Height = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        return g;
    }

    static void Put(Grid g, UIElement el, int row)
    {
        Grid.SetRow(el, row);
        g.Children.Add(el);
    }

    static WrapPanel Toolbar() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

    static TextBox FilterBox(WrapPanel bar, Action changed)
    {
        var label = Ui.Text("Filter", "Sub");
        label.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(label);
        var box = new TextBox { Width = 200, Margin = new Thickness(0, 0, 12, 4), ToolTip = "Type to filter the list" };
        box.TextChanged += (_, _) => changed();
        bar.Children.Add(box);
        return box;
    }

    static Button Add(WrapPanel bar, Button b)
    {
        b.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(b);
        return b;
    }

    static CheckBox Option(WrapPanel bar, string text, bool value, bool enabled, string tip)
    {
        var c = new CheckBox { Content = text, IsChecked = value && enabled, IsEnabled = enabled, Margin = new Thickness(4, 0, 14, 4), ToolTip = tip };
        bar.Children.Add(c);
        return c;
    }

    static DataGridTextColumn Col(string header, string path, double width, string sortPath = null, bool numeric = false, bool star = false)
    {
        var c = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            SortMemberPath = sortPath ?? path,
            Width = star ? new DataGridLength(width, DataGridLengthUnitType.Star) : new DataGridLength(width),
        };
        if (numeric) c.ElementStyle = (Style)Application.Current.FindResource("NumCell");
        return c;
    }

    static DataGridTemplateColumn CheckColumn()
    {
        var f = new FrameworkElementFactory(typeof(CheckBox));
        f.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding(nameof(LeftoverItem.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        f.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        f.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        return new DataGridTemplateColumn
        {
            Header = "", CellTemplate = new DataTemplate { VisualTree = f }, Width = new DataGridLength(34), CanUserSort = false,
        };
    }

    static bool Has(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    static string Bullets(IEnumerable<string> lines, int max = 15)
    {
        var list = lines.ToList();
        var sb = new StringBuilder();
        foreach (var l in list.Take(max)) sb.Append("• ").AppendLine(l);
        if (list.Count > max) sb.AppendLine("… and " + (list.Count - max) + " more");
        return sb.ToString().TrimEnd();
    }

    // =================================================================== Programs tab

    UIElement BuildProgramsTab()
    {
        var g = TabGrid(false, true, false);
        var bar = Toolbar();
        _programFilter = FilterBox(bar, () => { _programsView.Refresh(); UpdateProgramCount(); });
        _uninstallBtn = Add(bar, Ui.Button("Uninstall…", (_, _) => Ui.Run(UninstallSelectedAsync, "Uninstall"), "Danger",
            "Run the selected program's own uninstaller"));
        Add(bar, Ui.Button("Open location", (_, _) => OpenProgramLocation(), null, "Open the folder the program is installed in"));
        Add(bar, Ui.Button("Copy uninstall command", (_, _) => CopyUninstallCommand(), null, "Copy the command Winnow would run"));
        Add(bar, Ui.Button("Export CSV…", (_, _) => ExportPrograms(), null, "Save the list (as filtered) to a CSV file"));
        Add(bar, Ui.Button("Refresh", (_, _) => Ui.Run(LoadProgramsAsync, "Reading installed programs"), "Ghost"));
        Put(g, bar, 0);

        _programGrid = new DataGrid { ItemsSource = _programsView, Margin = new Thickness(0, 4, 0, 0) };
        _programGrid.Columns.Add(Col("Name", nameof(AppsProgram.Name), 2, star: true));
        _programGrid.Columns.Add(Col("Publisher", nameof(AppsProgram.Publisher), 1, star: true));
        _programGrid.Columns.Add(Col("Version", nameof(AppsProgram.Version), 110));
        _programGrid.Columns.Add(Col("Installed", nameof(AppsProgram.InstallDateText), 90, nameof(AppsProgram.InstallDateSort)));
        _programGrid.Columns.Add(Col("Size (est.)", nameof(AppsProgram.SizeText), 85, nameof(AppsProgram.SizeSort), numeric: true));
        _programGrid.Columns.Add(Col("Scope", nameof(AppsProgram.Scope), 130));
        _programGrid.Columns.Add(Col("Location", nameof(AppsProgram.InstallLocation), 220));
        _programGrid.MouseDoubleClick += (_, e) =>
        {
            // only a double-click on a row, not on the header or scroll bar
            if (ItemsControl.ContainerFromElement(_programGrid, e.OriginalSource as DependencyObject) is not DataGridRow) return;
            if (_programGrid.SelectedItem is AppsProgram) Ui.Run(UninstallSelectedAsync, "Uninstall");
        };
        Put(g, _programGrid, 1);

        _programCount = Ui.Text("Reading installed programs…", "Sub");
        _programCount.Margin = new Thickness(0, 6, 0, 0);
        Put(g, _programCount, 2);
        return g;
    }

    bool ProgramMatches(AppsProgram p)
    {
        string f = _programFilter?.Text?.Trim() ?? "";
        if (f.Length == 0) return true;
        return Has(p.Name, f) || Has(p.Publisher, f) || Has(p.InstallLocation, f) || Has(p.Version, f);
    }

    void UpdateProgramCount()
    {
        if (_programCount == null) return;
        var shown = _programsView.Cast<AppsProgram>().ToList();
        long kb = shown.Where(p => p.SizeKb.HasValue).Sum(p => p.SizeKb.Value);
        int sized = shown.Count(p => p.SizeKb.HasValue);
        string size = sized > 0 ? " · " + Fmt.Bytes(kb * 1024.0) + " est. (size reported by " + sized + " of them)" : "";
        _programCount.Text = (shown.Count == _programs.Count ? _programs.Count + " programs" : shown.Count + " of " + _programs.Count + " programs") +
                             size + ". Double-click a program to uninstall it.";
    }

    async Task LoadProgramsAsync()
    {
        if (_programsLoading) return;
        _programsLoading = true;
        try
        {
            _programCount.Text = "Reading installed programs…";
            var list = await Task.Run(() => AppInventory.ReadPrograms());
            var keep = _programGrid.SelectedItem as AppsProgram;
            _programs.Clear();
            foreach (var p in list) _programs.Add(p);
            if (keep != null)
            {
                var again = _programs.FirstOrDefault(p => p.FullKey == keep.FullKey);
                if (again != null) _programGrid.SelectedItem = again;
            }
            _programsLoaded = true;
            UpdateProgramCount();
        }
        finally { _programsLoading = false; }
    }

    AppsProgram SelectedProgram()
    {
        if (_programGrid.SelectedItem is AppsProgram p) return p;
        Status.Set("Select a program first.", StatusKind.Warn);
        return null;
    }

    void OpenProgramLocation()
    {
        var p = SelectedProgram();
        if (p == null) return;
        string dir = null;
        if (!string.IsNullOrWhiteSpace(p.InstallLocation) && Directory.Exists(p.InstallLocation)) dir = p.InstallLocation;
        if (dir == null)
        {
            foreach (var cand in new[] { p.UninstallString, p.DisplayIcon })
            {
                string path = AppInventory.ExtractPath(cand);
                if (path == null) continue;
                string d = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d) &&
                    !d.TrimEnd('\\').Equals(Paths.WindowsDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                    !d.StartsWith(System.IO.Path.Combine(Paths.WindowsDir, "Installer"), StringComparison.OrdinalIgnoreCase))
                { dir = d; break; }
            }
        }
        if (dir == null) { Status.Set(p.Name + " does not say where it is installed.", StatusKind.Warn); return; }
        Shell.Open(dir);
    }

    void CopyUninstallCommand()
    {
        var p = SelectedProgram();
        if (p == null) return;
        var (exe, args) = AppInventory.UninstallCommand(p);
        if (exe == null) { Status.Set(p.Name + " has no uninstall command.", StatusKind.Warn); return; }
        Ui.CopyToClipboard((Shell.Quote(exe) + " " + args).Trim());
    }

    void ExportPrograms()
    {
        var shown = _programsView.Cast<AppsProgram>().ToList();
        if (shown.Count == 0) { Status.Set("Nothing to export.", StatusKind.Warn); return; }
        string path = Ui.SaveFile("installed-programs-" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv", "CSV files|*.csv");
        if (path == null) return;
        try
        {
            File.WriteAllText(path, AppInventory.ToCsv(shown), new UTF8Encoding(true));
            Status.Set("Saved " + shown.Count + " programs to " + path, StatusKind.Good);
        }
        catch (Exception ex) { Ui.Error("Could not save the file:\n\n" + ex.Message); }
    }

    async Task UninstallSelectedAsync()
    {
        if (_uninstalling) { Status.Set("An uninstaller is already running.", StatusKind.Warn); return; }
        if (_programGrid.SelectedItems.Count > 1)
        {
            Ui.Info("Select one program at a time - each one runs its own uninstaller.");
            return;
        }
        var p = SelectedProgram();
        if (p == null) return;
        if (!p.HasUninstaller)
        {
            Ui.Error(p.Name + " has no uninstall command in the registry, so Winnow cannot remove it this way.\n\n" +
                     "If its files are already gone, run a scan on the Leftovers tab to remove the stale entry.");
            return;
        }
        var (exe, args) = AppInventory.UninstallCommand(p);
        string cmd = (Shell.Quote(exe) + " " + (args ?? "")).Trim();
        bool quiet = !string.IsNullOrWhiteSpace(p.QuietUninstallString);
        if (!Ui.Confirm("Uninstall " + p.Name + "?\n\n" +
                        "Winnow will run its " + (quiet ? "silent " : "") + "uninstaller:\n" + cmd + "\n\n" +
                        (quiet ? "It should finish without asking anything." : "The program's own uninstaller will open and may ask you questions.") +
                        " When it has finished, Winnow can look for files and settings it left behind.",
                        "Uninstall", "Uninstall", "Cancel", danger: true)) return;

        _uninstalling = true;
        _uninstallBtn.IsEnabled = false;
        try
        {
            Status.Set("Running the uninstaller for " + p.Name + "…");
            var (started, code, error) = await AppInventory.RunUninstallerAsync(p);
            if (!started)
            {
                Status.Set("Uninstall of " + p.Name + " did not start.", StatusKind.Bad);
                Ui.Error(error);
                return;
            }
            // Many uninstallers hand over to a copy of themselves and exit at once, so wait for the entry to go.
            Status.Set("Waiting for " + p.Name + " to disappear from the installed list…");
            bool gone = !AppInventory.StillListed(p);
            for (int i = 0; i < 60 && !gone; i++)
            {
                await Task.Delay(1500);
                gone = !await Task.Run(() => AppInventory.StillListed(p));
            }
            if (gone) Change.Note("apps.uninstall", "Uninstalled " + p.Name, cmd);
            else Change.Note("apps.uninstall", "Ran the uninstaller for " + p.Name + " (still listed afterwards)", cmd);
            await LoadProgramsAsync();

            if (gone)
            {
                Status.Set(p.Name + " uninstalled.", StatusKind.Good);
                if (Ui.Confirm(p.Name + " has been uninstalled.\n\nLook for folders and registry keys it left behind?",
                               "Uninstalled", "Scan for leftovers", "Not now"))
                {
                    _tabs.SelectedItem = _leftTab;
                    await RunScanAsync(new LeftoverFocus { Name = p.Name, Publisher = p.Publisher, InstallLocation = p.InstallLocation });
                }
            }
            else
            {
                string exitText = code.HasValue ? "exit code " + code.Value : "exit code unknown";
                Status.Set(p.Name + " is still installed (" + exitText + ").", StatusKind.Warn);
                Ui.Info("The uninstaller closed (" + exitText + "), but Windows still lists " + p.Name + ".\n\n" +
                        "If its uninstaller is still open, finish it and then press Refresh. If you cancelled it, nothing was changed.\n\n" +
                        "Once it is gone, the Leftovers tab can look for anything it left behind.");
            }
        }
        finally
        {
            _uninstalling = false;
            _uninstallBtn.IsEnabled = true;
        }
    }

    // =================================================================== Store apps tab

    UIElement BuildStoreTab()
    {
        var g = TabGrid(false, false, true, false);
        var bar = Toolbar();
        _storeFilter = FilterBox(bar, () => { _storeView.Refresh(); UpdateStoreCount(); });
        _storeRemoveBtn = Add(bar, Ui.Button("Remove selected…", (_, _) => Ui.Run(RemoveSelectedStoreAsync, "Remove Store apps"), "Danger",
            "Remove the selected Store apps (Ctrl or Shift-click to select several)"));
        Add(bar, Ui.Button("Refresh", (_, _) => Ui.Run(LoadStoreAsync, "Reading Store apps"), "Ghost"));
        Put(g, bar, 0);

        var opts = Toolbar();
        _storeAllUsers = Option(opts, "Remove for all users", false, Elevation.IsAdmin,
            Elevation.IsAdmin ? "Remove the app from every account on this PC (Remove-AppxPackage -AllUsers)" : "Needs Winnow running as administrator");
        _storeDeprovision = Option(opts, "Also stop it reinstalling for new users", false, Elevation.IsAdmin,
            Elevation.IsAdmin ? "Also remove the provisioned copy Windows installs for each new account" : "Needs Winnow running as administrator");
        Put(g, opts, 1);

        _storeGrid = new DataGrid { ItemsSource = _storeView, Margin = new Thickness(0, 4, 0, 0) };
        _storeGrid.Columns.Add(Col("Name", nameof(AppsStorePackage.Name), 2, star: true));
        _storeGrid.Columns.Add(Col("Publisher", nameof(AppsStorePackage.Publisher), 1, star: true));
        _storeGrid.Columns.Add(Col("Version", nameof(AppsStorePackage.Version), 120));
        _storeGrid.Columns.Add(Col("Signed by", nameof(AppsStorePackage.SignatureKind), 80));
        _storeGrid.Columns.Add(Col("Provisioned", nameof(AppsStorePackage.ProvisionedText), 85));
        _storeGrid.Columns.Add(Col("Status", nameof(AppsStorePackage.Status), 140));
        Put(g, _storeGrid, 2);

        var foot = new StackPanel();
        _storeCount = Ui.Text("Reading Store apps…", "Sub");
        _storeCount.Margin = new Thickness(0, 6, 0, 0);
        foot.Children.Add(_storeCount);
        _storeLog = new AppsLog(110);
        foot.Children.Add(_storeLog.View);
        Put(g, foot, 3);
        return g;
    }

    bool StoreMatches(AppsStorePackage s)
    {
        string f = _storeFilter?.Text?.Trim() ?? "";
        if (f.Length == 0) return true;
        return Has(s.Name, f) || Has(s.Publisher, f) || Has(s.FullName, f);
    }

    void UpdateStoreCount()
    {
        if (_storeCount == null || !_storeLoaded) return;
        int shown = _storeView.Count;
        string prov = Elevation.IsAdmin ? _provisioned.Count + " provisioned for new users." : "Run as administrator to see provisioned packages.";
        _storeCount.Text = (shown == _store.Count ? _store.Count + " removable Store apps" : shown + " of " + _store.Count + " Store apps") +
                           " (frameworks and parts of Windows that cannot be removed are hidden). " + prov;
    }

    async Task LoadStoreAsync()
    {
        if (_storeLoading) return;
        _storeLoading = true;
        try
        {
            if (_storeCount != null) _storeCount.Text = "Reading Store apps…";
            var (apps, prov, error) = await AppInventory.ReadStoreAsync();
            _allPackages = apps;
            _provisioned = prov;
            _store.Clear();
            foreach (var a in apps.Where(x => !x.IsFramework && !x.NonRemovable)) _store.Add(a);
            _storeLoaded = true;
            UpdateStoreCount();
            if (error.Length > 0)
            {
                _storeCount.Text = "Could not read Store apps: " + error;
                Status.Set("Store apps: " + error, StatusKind.Warn);
            }
            BuildBloat();
        }
        finally { _storeLoading = false; }
    }

    sealed class AppsRemoval
    {
        public string Name = "";
        public string FullName = "";
        public string Provisioned = "";
        public AppsStorePackage Package;
    }

    async Task<(int ok, int failed, int skipped)> RemovePackagesAsync(List<AppsRemoval> items, bool allUsers, bool deprovision, AppsLog log)
    {
        int ok = 0, failed = 0, skipped = 0;
        log.Append("---- " + DateTime.Now.ToString("HH:mm:ss") + "  removing " + items.Count + " app(s)" +
                   (allUsers ? " for all users" : " for this account") + (deprovision ? ", deprovisioning" : ""));
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            log.Append("[" + (i + 1) + "/" + items.Count + "] " + it.Name);
            Status.Set("Removing " + it.Name + " (" + (i + 1) + " of " + items.Count + ")…");
            if (it.FullName.Length == 0 && (!deprovision || it.Provisioned.Length == 0))
            {
                skipped++;
                log.Append("  skipped - it is only provisioned for new users; tick 'Also stop it reinstalling for new users' to remove that copy");
                continue;
            }
            if (it.Package != null) it.Package.Status = "Removing…";
            var (rok, msg) = await AppInventory.RemoveStoreAsync(it.Name, it.FullName, it.Provisioned, allUsers, deprovision, log.Append);
            if (rok) ok++; else failed++;
            if (it.Package != null) it.Package.Status = rok ? "Removed" : "Failed";
            if (!rok) Log.Write("store remove " + it.Name + ": " + msg);
        }
        log.Append("Done: " + ok + " removed" + (failed > 0 ? ", " + failed + " failed" : "") + (skipped > 0 ? ", " + skipped + " skipped" : "") + ".");
        Status.Set("Removed " + ok + " app(s)" + (failed > 0 ? ", " + failed + " failed - see the log" : "") + ".",
                   failed > 0 ? StatusKind.Warn : StatusKind.Good);
        return (ok, failed, skipped);
    }

    async Task RemoveSelectedStoreAsync()
    {
        if (_removing) { Status.Set("Already removing apps.", StatusKind.Warn); return; }
        var sel = _storeGrid.SelectedItems.OfType<AppsStorePackage>().ToList();
        if (sel.Count == 0) { Status.Set("Select one or more Store apps first.", StatusKind.Warn); return; }
        bool allUsers = _storeAllUsers.IsChecked == true;
        bool deprov = _storeDeprovision.IsChecked == true;
        var prot = sel.Where(s => BloatList.IsProtected(s.Name)).Select(s => s.Name).ToList();
        string msg = "Remove " + (sel.Count == 1 ? "this Store app" : "these " + sel.Count + " Store apps") +
                     (allUsers ? " for every account on this PC" : " for your account") + "?\n\n" +
                     Bullets(sel.Select(s => s.Name + "  (" + s.Publisher + ")")) +
                     (deprov ? "\n\nTheir provisioned copies will also be removed, so new accounts do not get them." : "") +
                     (prot.Count > 0 ? "\n\nWarning: " + string.Join(", ", prot) + (prot.Count == 1 ? " is" : " are") +
                                       " part of Windows or used by other apps. Removing " + (prot.Count == 1 ? "it" : "them") + " can break things." : "") +
                     "\n\nRemoved Store apps can be reinstalled from the Microsoft Store.";
        if (!Ui.Confirm(msg, "Remove Store apps", "Remove", "Cancel", danger: true)) return;

        _removing = true;
        _storeRemoveBtn.IsEnabled = false;
        _bloatRemoveBtn.IsEnabled = false;
        try
        {
            var items = sel.Select(s => new AppsRemoval { Name = s.Name, FullName = s.FullName, Provisioned = s.ProvisionedName, Package = s }).ToList();
            await RemovePackagesAsync(items, allUsers, deprov, _storeLog);
            await LoadStoreAsync();
        }
        finally
        {
            _removing = false;
            _storeRemoveBtn.IsEnabled = true;
            _bloatRemoveBtn.IsEnabled = true;
        }
    }

    // =================================================================== Bloatware tab

    UIElement BuildBloatTab()
    {
        var g = TabGrid(false, false, false, true, false);
        var intro = Ui.Text("Apps that come with Windows 11 or were put there by Microsoft or your PC maker. Only the ones installed on this PC are listed. " +
                            "Xbox parts stay unticked because removing them breaks Game Bar, Game Pass and Xbox sign-in in games.", "Sub");
        intro.TextWrapping = TextWrapping.Wrap;
        intro.Margin = new Thickness(0, 0, 0, 6);
        Put(g, intro, 0);

        var bar = Toolbar();
        _bloatFilter = FilterBox(bar, ApplyBloatFilter);
        Add(bar, Ui.Button("Tick recommended", (_, _) => { foreach (var r in _bloat) r.Box.IsChecked = r.Entry.Recommended; UpdateBloatCount(); }, null,
            "Tick everything marked as safe to remove"));
        Add(bar, Ui.Button("Untick all", (_, _) => { foreach (var r in _bloat) r.Box.IsChecked = false; UpdateBloatCount(); }, "Ghost"));
        _bloatRemoveBtn = Add(bar, Ui.Button("Remove ticked…", (_, _) => Ui.Run(RemoveTickedBloatAsync, "Remove apps"), "Danger"));
        Add(bar, Ui.Button("Refresh", (_, _) => Ui.Run(LoadStoreAsync, "Reading Store apps"), "Ghost"));
        Put(g, bar, 1);

        var opts = Toolbar();
        _bloatAllUsers = Option(opts, "Remove for all users", true, Elevation.IsAdmin,
            Elevation.IsAdmin ? "Remove from every account on this PC" : "Needs Winnow running as administrator");
        _bloatDeprovision = Option(opts, "Also stop it reinstalling for new users", true, Elevation.IsAdmin,
            Elevation.IsAdmin ? "Remove the provisioned copy so new accounts do not get the app" : "Needs Winnow running as administrator");
        _bloatCount = Ui.Text("Reading Store apps…", "Sub");
        _bloatCount.Margin = new Thickness(4, 0, 0, 4);
        opts.Children.Add(_bloatCount);
        Put(g, opts, 2);

        var scroll = new ScrollViewer { Content = _bloatRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var frame = new Border { Child = scroll, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 4, 0, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "Panel");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border");
        Put(g, frame, 3);

        var foot = new StackPanel();
        var never = new Border { Style = (Style)Application.Current.FindResource("HintBox"), Margin = new Thickness(0, 8, 0, 0) };
        var neverText = Ui.Text(BloatList.NeverOffered, "Sub");
        neverText.TextWrapping = TextWrapping.Wrap;
        never.Child = neverText;
        foot.Children.Add(never);
        _bloatLog = new AppsLog(90);
        foot.Children.Add(_bloatLog.View);
        Put(g, foot, 4);
        return g;
    }

    void BuildBloat()
    {
        _bloat.Clear();
        _bloatRows.Children.Clear();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in BloatList.Entries)
        {
            var inst = _allPackages.Where(x => !x.IsFramework && !x.NonRemovable && !claimed.Contains(x.Name) &&
                                               BloatList.Matches(entry.Pattern, x.Name)).ToList();
            var instNames = new HashSet<string>(inst.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            var provOnly = _provisioned.Where(x => !claimed.Contains(x.DisplayName) && !instNames.Contains(x.DisplayName) &&
                                                   BloatList.Matches(entry.Pattern, x.DisplayName)).ToList();
            if (inst.Count == 0 && provOnly.Count == 0) continue;
            foreach (var x in inst) claimed.Add(x.Name);
            foreach (var x in provOnly) claimed.Add(x.DisplayName);
            var row = new BloatRowUi(entry, inst, provOnly);
            row.Box.Checked += (_, _) => UpdateBloatCount();
            row.Box.Unchecked += (_, _) => UpdateBloatCount();
            _bloat.Add(row);
            _bloatRows.Children.Add(row.Root);
        }
        if (_bloat.Count == 0)
        {
            var none = Ui.Text(_allPackages.Count == 0
                ? "No Store apps could be read, so nothing can be listed here."
                : "None of the apps on Winnow's list are installed. Nothing to remove.", "Sub");
            none.Margin = new Thickness(12);
            none.TextWrapping = TextWrapping.Wrap;
            _bloatRows.Children.Add(none);
        }
        ApplyBloatFilter();
        UpdateBloatCount();
    }

    void ApplyBloatFilter()
    {
        string f = _bloatFilter?.Text?.Trim() ?? "";
        foreach (var r in _bloat)
        {
            bool show = f.Length == 0 || Has(r.Entry.Name, f) || Has(r.Entry.Description, f) || Has(r.Entry.Risk, f) ||
                        r.PackageNames.Any(n => Has(n, f));
            r.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void UpdateBloatCount()
    {
        if (_bloatCount == null) return;
        if (!_storeLoaded) { _bloatCount.Text = "Reading Store apps…"; return; }
        int ticked = _bloat.Count(r => r.Box.IsChecked == true);
        _bloatCount.Text = _bloat.Count + " found on this PC · " + ticked + " ticked";
    }

    async Task RemoveTickedBloatAsync()
    {
        if (_removing) { Status.Set("Already removing apps.", StatusKind.Warn); return; }
        var rows = _bloat.Where(r => r.Box.IsChecked == true).ToList();
        if (rows.Count == 0) { Status.Set("Tick one or more apps first.", StatusKind.Warn); return; }
        bool allUsers = _bloatAllUsers.IsChecked == true;
        bool deprov = _bloatDeprovision.IsChecked == true;
        var risky = rows.Where(r => r.Entry.Risk == BloatList.Advanced || r.Entry.Risk == BloatList.Moderate).Select(r => r.Entry.Name).ToList();
        string msg = "Remove " + rows.Count + (rows.Count == 1 ? " app" : " apps") + (allUsers ? " for every account on this PC" : " for your account") + "?\n\n" +
                     Bullets(rows.Select(r => r.Entry.Name + "  [" + r.Entry.Risk + "]"), 20) +
                     (deprov ? "\n\nProvisioned copies will also be removed, so new accounts do not get them." : "") +
                     (risky.Count > 0 ? "\n\nCheck these first: " + string.Join(", ", risky) + ". See each one's note on the list." : "") +
                     "\n\nStore apps can be reinstalled from the Microsoft Store.";
        if (!Ui.Confirm(msg, "Remove preinstalled apps", "Remove", "Cancel", danger: true)) return;

        var items = new List<AppsRemoval>();
        foreach (var r in rows)
        {
            foreach (var p in r.Installed)
                items.Add(new AppsRemoval { Name = p.Name, FullName = p.FullName, Provisioned = p.ProvisionedName, Package = p });
            foreach (var p in r.ProvisionedOnly)
                items.Add(new AppsRemoval { Name = p.DisplayName, FullName = "", Provisioned = p.PackageName });
        }

        _removing = true;
        _storeRemoveBtn.IsEnabled = false;
        _bloatRemoveBtn.IsEnabled = false;
        try
        {
            await RemovePackagesAsync(items, allUsers, deprov, _bloatLog);
            await LoadStoreAsync();
        }
        finally
        {
            _removing = false;
            _storeRemoveBtn.IsEnabled = true;
            _bloatRemoveBtn.IsEnabled = true;
        }
    }

    // =================================================================== Leftovers tab

    UIElement BuildLeftoversTab()
    {
        var g = TabGrid(false, false, false, false, true);
        var intro = Ui.Text("Finds folders and registry keys left by programs that are no longer installed. Anything that matches an installed program, " +
                            "Store app, running process, service, startup entry or scheduled task is left out, and Windows, Microsoft and driver folders " +
                            "are never offered. Folders go to the Recycle Bin; registry keys are saved as .reg files in Winnow-removed-registry beside " +
                            "Winnow.exe before removal. Only High-confidence items are ticked. AppData folders can hold settings or saved games - check before removing.", "Sub");
        intro.TextWrapping = TextWrapping.Wrap;
        intro.Margin = new Thickness(0, 0, 0, 6);
        Put(g, intro, 0);

        var bar = Toolbar();
        _scanBtn = Add(bar, Ui.Button("Scan for leftovers", (_, _) => Ui.Run(() => RunScanAsync(null), "Leftover scan"), "Accent"));
        _cancelBtn = Add(bar, Ui.Button("Cancel", (_, _) => _scanCts?.Cancel(), null, "Stop the scan"));
        _cancelBtn.IsEnabled = false;
        _leftFilter = FilterBox(bar, () => _leftView.Refresh());
        Add(bar, Ui.Button("Tick High only", (_, _) => { foreach (var l in _left) l.Selected = l.Confidence == "High" && l.Status.Length == 0; }, "Ghost"));
        Add(bar, Ui.Button("Untick all", (_, _) => { foreach (var l in _left) l.Selected = false; }, "Ghost"));
        Add(bar, Ui.Button("Open", (_, _) => OpenLeftover(), null, "Open the folder, or copy the registry key path"));
        _leftRemoveBtn = Add(bar, Ui.Button("Remove ticked…", (_, _) => Ui.Run(RemoveLeftoversAsync, "Remove leftovers"), "Danger"));
        Put(g, bar, 1);

        var prog = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        _leftProgress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
        prog.Children.Add(_leftProgress);
        _leftStatus = Ui.Text("Press Scan for leftovers. Nothing is removed until you choose to.", "Sub");
        _leftStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        prog.Children.Add(_leftStatus);
        Put(g, prog, 2);

        _leftNotes = new StackPanel();
        Put(g, _leftNotes, 3);

        _leftGrid = new DataGrid { ItemsSource = _leftView, Margin = new Thickness(0, 4, 0, 0) };
        _leftGrid.Columns.Add(CheckColumn());
        _leftGrid.Columns.Add(Col("Confidence", nameof(LeftoverItem.Confidence), 85, nameof(LeftoverItem.ConfidenceRank)));
        _leftGrid.Columns.Add(Col("Kind", nameof(LeftoverItem.KindText), 105));
        _leftGrid.Columns.Add(Col("Location", nameof(LeftoverItem.Location), 2, star: true));
        _leftGrid.Columns.Add(Col("Size", nameof(LeftoverItem.SizeText), 80, nameof(LeftoverItem.SizeSort), numeric: true));
        _leftGrid.Columns.Add(Col("Modified", nameof(LeftoverItem.ModifiedText), 90, nameof(LeftoverItem.ModifiedSort)));
        _leftGrid.Columns.Add(Col("Why", nameof(LeftoverItem.Reason), 1.5, star: true));
        _leftGrid.Columns.Add(Col("Result", nameof(LeftoverItem.Status), 140));
        _leftGrid.MouseDoubleClick += (_, _) => OpenLeftover();
        Put(g, _leftGrid, 4);
        return g;
    }

    bool LeftMatches(LeftoverItem l)
    {
        string f = _leftFilter?.Text?.Trim() ?? "";
        if (f.Length == 0) return true;
        return Has(l.Location, f) || Has(l.Reason, f) || Has(l.Confidence, f) || Has(l.KindText, f);
    }

    void OpenLeftover()
    {
        if (_leftGrid.SelectedItem is not LeftoverItem l) { Status.Set("Select an item first.", StatusKind.Warn); return; }
        if (l.IsRegistry) { Ui.CopyToClipboard(l.Location); return; }
        if (Directory.Exists(l.Location)) Shell.Open(l.Location);
        else Status.Set("That folder no longer exists.", StatusKind.Warn);
    }

    async Task RunScanAsync(LeftoverFocus focus)
    {
        if (_scanCts != null) { Status.Set("A scan is already running.", StatusKind.Warn); return; }
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        int id = ++_scanId;
        _scanBtn.IsEnabled = false;
        _leftRemoveBtn.IsEnabled = false;
        _cancelBtn.IsEnabled = true;
        _leftProgress.Visibility = Visibility.Visible;
        _left.Clear();
        _leftNotes.Children.Clear();
        _leftStatus.Text = focus == null ? "Scanning…" : "Looking for anything " + focus.Name + " left behind…";
        var progress = new Progress<string>(s => { if (id == _scanId && _scanCts != null) _leftStatus.Text = s; });
        var storeNames = _allPackages.Select(p => p.Name).ToList();
        try
        {
            var result = await Task.Run(() => LeftoverScan.Run(focus, storeNames, progress, ct));
            foreach (var item in result.Items) _left.Add(item);
            foreach (var note in result.Notes)
            {
                var t = Ui.Text(note, "Sub");
                t.TextWrapping = TextWrapping.Wrap;
                _leftNotes.Children.Add(new Border { Style = (Style)Application.Current.FindResource("HintBox"), Child = t, Margin = new Thickness(0, 4, 0, 2) });
            }
            int high = result.Items.Count(i => i.Confidence == "High");
            if (focus == null)
                _leftStatus.Text = result.Items.Count == 0
                    ? "Nothing left behind was found."
                    : result.Items.Count + " possible leftovers found, " + high + " with high confidence (ticked). Nothing has been removed yet.";
            else
                _leftStatus.Text = result.Items.Count == 0
                    ? "Nothing left behind by " + focus.Name + " was found."
                    : result.Items.Count + " leftovers of " + focus.Name + " found, " + high + " ticked. Nothing has been removed yet.";
            Status.Set("Leftover scan finished: " + result.Items.Count + " found.", StatusKind.Good);
        }
        catch (OperationCanceledException)
        {
            _leftStatus.Text = "Scan cancelled.";
            Status.Set("Leftover scan cancelled.", StatusKind.Warn);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            _scanBtn.IsEnabled = true;
            _leftRemoveBtn.IsEnabled = true;
            _cancelBtn.IsEnabled = false;
            _leftProgress.Visibility = Visibility.Collapsed;
        }
    }

    async Task RemoveLeftoversAsync()
    {
        if (_scanCts != null) { Status.Set("Wait for the scan to finish.", StatusKind.Warn); return; }
        var items = _left.Where(l => l.Selected && l.Status != "Removed" && !l.Status.StartsWith("Moved")).ToList();
        if (items.Count == 0) { Status.Set("Tick one or more items first.", StatusKind.Warn); return; }
        int folders = items.Count(i => !i.IsRegistry), keys = items.Count - folders;
        string msg = "Remove " + items.Count + (items.Count == 1 ? " item" : " items") + "?\n\n" +
                     Bullets(items.Select(i => "[" + i.KindText + "] " + i.Location), 20) + "\n\n" +
                     (folders > 0 ? "Folders are moved to the Recycle Bin, so they can be restored from there. " : "") +
                     (keys > 0 ? "Registry keys are saved as .reg files in the Winnow-removed-registry folder beside Winnow.exe first; " +
                                 "double-click one to put the key back. " : "") +
                     "\n\nIf a program you still use stops working, restore its items.";
        if (!Ui.Confirm(msg, "Remove leftovers", "Remove", "Cancel", danger: true)) return;

        _leftRemoveBtn.IsEnabled = false;
        _scanBtn.IsEnabled = false;
        int ok = 0, failed = 0;
        bool entries = false;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                Status.Set("Removing " + (i + 1) + " of " + items.Count + "…");
                item.Status = "Removing…";
                var (rok, message) = await LeftoverScan.RemoveAsync(item);
                if (rok)
                {
                    ok++;
                    item.Selected = false;
                    item.Status = message.Length > 0 ? char.ToUpper(message[0]) + message.Substring(1) : "Removed";
                    if (item.Kind == LeftoverKind.UninstallEntry) entries = true;
                }
                else
                {
                    failed++;
                    item.Status = "Failed: " + message;
                }
            }
        }
        finally
        {
            _leftRemoveBtn.IsEnabled = true;
            _scanBtn.IsEnabled = true;
        }
        Status.Set("Removed " + ok + (ok == 1 ? " item" : " items") + (failed > 0 ? ", " + failed + " failed - see the Result column" : "") + ".",
                   failed > 0 ? StatusKind.Warn : StatusKind.Good);
        if (entries) await LoadProgramsAsync();
    }
}

/// <summary>One row on the Bloatware tab.</summary>
internal sealed class BloatRowUi
{
    public readonly BloatEntry Entry;
    public readonly List<AppsStorePackage> Installed;
    public readonly List<AppsProvisioned> ProvisionedOnly;
    public readonly CheckBox Box;
    public readonly Border Root;

    public IEnumerable<string> PackageNames => Installed.Select(p => p.Name).Concat(ProvisionedOnly.Select(p => p.DisplayName));

    public BloatRowUi(BloatEntry entry, List<AppsStorePackage> installed, List<AppsProvisioned> provisionedOnly)
    {
        Entry = entry;
        Installed = installed;
        ProvisionedOnly = provisionedOnly;

        var text = new StackPanel();
        var name = new TextBlock { Text = entry.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        text.Children.Add(name);
        var desc = new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        desc.Style = (Style)Application.Current.FindResource("Sub");
        text.Children.Add(desc);
        string pk = string.Join(" · ", installed.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        if (provisionedOnly.Count > 0)
            pk = (pk.Length > 0 ? pk + " · " : "") + string.Join(" · ", provisionedOnly.Select(p => p.DisplayName + " (new users only)"));
        var pkgs = new TextBlock { Text = pk, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        pkgs.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        pkgs.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        text.Children.Add(pkgs);

        Box = new CheckBox { Content = text, IsChecked = entry.Recommended, VerticalAlignment = VerticalAlignment.Top, VerticalContentAlignment = VerticalAlignment.Top };

        string colour = entry.Risk == BloatList.Safe ? "Good" : entry.Risk == BloatList.Advanced ? "Bad" : "Warn";
        var chipText = new TextBlock { Text = entry.Risk, FontSize = 11, FontWeight = FontWeights.SemiBold };
        chipText.SetResourceReference(TextBlock.ForegroundProperty, colour);
        var chip = new Border { Child = chipText, Style = (Style)Application.Current.FindResource("Chip"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 2, 0, 0) };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Box);
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        Root = new Border { Child = grid, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
        Root.SetResourceReference(Border.BorderBrushProperty, "Grid");
    }
}

/// <summary>A small read-only log panel. Append is safe from any thread.</summary>
internal sealed class AppsLog
{
    readonly StringBuilder _sb = new();
    readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(8, 6, 8, 6) };
    readonly ScrollViewer _scroll;
    public readonly Border View;

    public AppsLog(double height)
    {
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _scroll = new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        View = new Border { Child = _scroll, Height = height, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 6, 0, 0) };
        View.SetResourceReference(Border.BackgroundProperty, "Code");
        View.SetResourceReference(Border.BorderBrushProperty, "Border");
    }

    public void Append(string line)
    {
        if (!View.Dispatcher.CheckAccess())
        {
            View.Dispatcher.BeginInvoke(new Action(() => Append(line)));
            return;
        }
        if (_sb.Length > 0) _sb.Append('\n');
        _sb.Append(line);
        if (_sb.Length > 120_000) _sb.Remove(0, _sb.Length - 100_000);
        _text.Text = _sb.ToString();
        _scroll.ScrollToEnd();
    }
}
