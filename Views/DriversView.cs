using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Every driver on the PC. Kernel drivers come from Win32_SystemDriver (the
/// services the kernel loads); device drivers from Win32_PnPSignedDriver (what
/// each device is using, with its signature). Flags unsigned drivers, missing
/// driver files and well-known vulnerable ring-0 helpers. Read-only.
/// </summary>
public sealed class DriversView : UserControl, IView
{
    readonly DataGrid _kGrid, _dGrid;
    ListCollectionView _kView, _dView;
    List<DriversKernelRow> _kernel = new();
    List<DriversDeviceRow> _devices = new();
    readonly TabControl _tabs = new();
    readonly TabItem _kTab, _dTab;
    readonly TextBox _filter = new() { Width = 260, ToolTip = "Filter by name, provider, path or note" };
    readonly CheckBox _thirdParty = new() { Content = "Third-party only", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _problems = new() { Content = "Problems only", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _progress = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    readonly Button _refresh, _export;
    bool _loaded, _loading;

    public DriversView()
    {
        _progress.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(Ui.Header("Drivers",
            "Kernel drivers and the drivers your devices use, with signatures. Red: unsigned, or a known vulnerable " +
            "ring-0 helper that malware can abuse. Amber: the driver file is missing. Read-only."));

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var filterLabel = Ui.Text("Filter ", null);
        bar.Children.Add(filterLabel);
        bar.Children.Add(_filter);
        bar.Children.Add(_thirdParty);
        bar.Children.Add(_problems);
        var spacer = new Border { Width = 16 };
        bar.Children.Add(spacer);
        _refresh = Ui.Button("Refresh", (_, _) => Ui.Run(LoadAsync, "Reading drivers"));
        _export = Ui.Button("Export CSV…", (_, _) => Ui.Run(ExportCsv, "CSV export"), null, "Export the rows shown in the current tab");
        bar.Children.Add(_refresh);
        bar.Children.Add(_export);
        top.Children.Add(bar);
        top.Children.Add(_progress);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        _filter.TextChanged += (_, _) => RefreshViews();
        _thirdParty.Checked += (_, _) => RefreshViews();
        _thirdParty.Unchecked += (_, _) => RefreshViews();
        _problems.Checked += (_, _) => RefreshViews();
        _problems.Unchecked += (_, _) => RefreshViews();

        // ---- kernel drivers
        _kGrid = new DataGrid { RowStyle = SysInfoUi.KindRowStyle(), SelectionMode = DataGridSelectionMode.Extended };
        _kGrid.Columns.Add(DrvCol("Name", "Name", null, 150));
        _kGrid.Columns.Add(DrvCol("Display name", "DisplayName", null, 220));
        _kGrid.Columns.Add(DrvCol("State", "State", null, 76));
        _kGrid.Columns.Add(DrvCol("Start", "StartMode", null, 76));
        _kGrid.Columns.Add(DrvCol("Signed", "Signed", null, 70));
        _kGrid.Columns.Add(DrvCol("Provider", "Provider", null, 170));
        _kGrid.Columns.Add(DrvCol("Version", "Version", null, 130));
        _kGrid.Columns.Add(DrvCol("Note", "Note", "Severity", 260));
        _kGrid.Columns.Add(DrvCol("File", "FilePath", null, 360));
        _kGrid.Columns.Add(DrvCol("Type", "Type", null, 120));
        _kGrid.ContextMenu = KernelMenu();
        _kGrid.MouseDoubleClick += (_, _) => { if (_kGrid.SelectedItem is DriversKernelRow r) Reveal(r.FilePath); };

        // ---- device drivers
        _dGrid = new DataGrid { RowStyle = SysInfoUi.KindRowStyle(), SelectionMode = DataGridSelectionMode.Extended };
        _dGrid.Columns.Add(DrvCol("Device", "Device", null, 260));
        _dGrid.Columns.Add(DrvCol("Class", "DeviceClass", null, 110));
        _dGrid.Columns.Add(DrvCol("Provider", "Provider", null, 170));
        _dGrid.Columns.Add(DrvCol("Version", "Version", null, 130));
        var dateCol = new DataGridTextColumn
        {
            Header = "Date",
            Binding = new Binding("Date") { Mode = BindingMode.OneWay, StringFormat = "yyyy-MM-dd", TargetNullValue = Fmt.Na },
            Width = new DataGridLength(92),
        };
        _dGrid.Columns.Add(dateCol);
        _dGrid.Columns.Add(DrvCol("Signed", "Signed", null, 70));
        _dGrid.Columns.Add(DrvCol("Signer", "Signer", null, 220));
        _dGrid.Columns.Add(DrvCol("INF", "Inf", null, 110));
        _dGrid.Columns.Add(DrvCol("Service", "Service", null, 110));
        _dGrid.Columns.Add(DrvCol("Note", "Note", "Severity", 220));
        _dGrid.ContextMenu = DeviceMenu();

        _kTab = new TabItem { Header = "Kernel drivers", Content = _kGrid };
        _dTab = new TabItem { Header = "Device drivers", Content = _dGrid };
        _tabs.Items.Add(_kTab);
        _tabs.Items.Add(_dTab);
        root.Children.Add(_tabs);

        Content = root;
        _progress.Text = "Drivers are read when this page opens.";
    }

    static DataGridTextColumn DrvCol(string header, string path, string sortPath, double width)
    {
        var c = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay, TargetNullValue = Fmt.Na },
            Width = new DataGridLength(width),
        };
        if (sortPath != null) c.SortMemberPath = sortPath;
        return c;
    }

    // ------------------------------------------------------------------ IView

    public void OnShown()
    {
        if (!_loaded && !_loading) Ui.Run(LoadAsync, "Reading drivers");
    }

    public void OnHidden() { }

    // ------------------------------------------------------------------ loading

    async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _refresh.IsEnabled = false;
        _progress.Text = "Reading drivers…";
        try
        {
            var progress = new Progress<string>(t => _progress.Text = t);
            var data = await Task.Run(() => DriversReader.Read(progress));
            _kernel = data.Kernel;
            _devices = data.Devices;
            MakeViews();
            _loaded = true;
            Summarise(data);
        }
        catch (Exception ex)
        {
            _progress.Text = "Could not read the drivers: " + ex.Message;
            throw;
        }
        finally
        {
            _loading = false;
            _refresh.IsEnabled = true;
        }
    }

    void MakeViews()
    {
        _kView = new ListCollectionView(_kernel) { Filter = o => o is DriversKernelRow r && Show(r) };
        _kView.SortDescriptions.Add(new SortDescription(nameof(DriversKernelRow.Severity), ListSortDirection.Descending));
        _kView.SortDescriptions.Add(new SortDescription(nameof(DriversKernelRow.Name), ListSortDirection.Ascending));
        _kGrid.ItemsSource = _kView;

        _dView = new ListCollectionView(_devices) { Filter = o => o is DriversDeviceRow r && Show(r) };
        _dView.SortDescriptions.Add(new SortDescription(nameof(DriversDeviceRow.Severity), ListSortDirection.Descending));
        _dView.SortDescriptions.Add(new SortDescription(nameof(DriversDeviceRow.Device), ListSortDirection.Ascending));
        _dGrid.ItemsSource = _dView;
        UpdateTabHeaders();
    }

    void RefreshViews()
    {
        _kView?.Refresh();
        _dView?.Refresh();
        UpdateTabHeaders();
    }

    void UpdateTabHeaders()
    {
        int kShown = _kView?.Count ?? 0, dShown = _dView?.Count ?? 0;
        _kTab.Header = _kernel.Count == 0 ? "Kernel drivers" :
            kShown == _kernel.Count ? $"Kernel drivers ({_kernel.Count})" : $"Kernel drivers ({kShown} of {_kernel.Count})";
        _dTab.Header = _devices.Count == 0 ? "Device drivers" :
            dShown == _devices.Count ? $"Device drivers ({_devices.Count})" : $"Device drivers ({dShown} of {_devices.Count})";
    }

    void Summarise(DriversData d)
    {
        int unsignedK = _kernel.Count(r => r.Signed == "No");
        int unsignedD = _devices.Count(r => r.Signed == "No");
        int missing = _kernel.Count(r => !r.FileExists);
        int known = _kernel.Count(r => r.KnownBad);
        var parts = new List<string>
        {
            $"{_kernel.Count} kernel drivers ({_kernel.Count(r => r.State == "Running")} running)",
            $"{_devices.Count} device drivers",
            $"{unsignedK + unsignedD} unsigned",
            $"{missing} with a missing file",
            $"{known} known vulnerable helper{(known == 1 ? "" : "s")}",
        };
        string text = string.Join(" · ", parts) + $" · read at {DateTime.Now:HH:mm:ss}.";
        if (!d.KernelOk) text += " Win32_SystemDriver could not be read.";
        if (!d.DevicesOk) text += " Win32_PnPSignedDriver could not be read.";
        text += " Signatures for kernel drivers come from the device that uses them; drivers with no device show — (not checked).";
        _progress.Text = text;
        if (known > 0) Status.Set($"Drivers: {known} known vulnerable ring-0 helper{(known == 1 ? "" : "s")} found - see the red rows.", StatusKind.Warn);
    }

    // ------------------------------------------------------------------ filtering

    bool MatchesText(params string[] fields)
    {
        string q = _filter.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        foreach (var f in fields)
            if (!string.IsNullOrEmpty(f) && f.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    bool Show(DriversKernelRow r)
    {
        if (_thirdParty.IsChecked == true && !r.ThirdParty) return false;
        if (_problems.IsChecked == true && r.Severity < 2) return false;
        return MatchesText(r.Name, r.DisplayName, r.Provider, r.FilePath, r.Note, r.Version, r.State);
    }

    bool Show(DriversDeviceRow r)
    {
        if (_thirdParty.IsChecked == true && !r.ThirdParty) return false;
        if (_problems.IsChecked == true && r.Severity < 2) return false;
        return MatchesText(r.Device, r.DeviceClass, r.Provider, r.Signer, r.Inf, r.Note, r.Version, r.Service);
    }

    // ------------------------------------------------------------------ actions

    ContextMenu KernelMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(SysInfoUi.Item("Copy row", () => CopyRows(_kGrid)));
        m.Items.Add(SysInfoUi.Item("Copy file path", () => { if (_kGrid.SelectedItem is DriversKernelRow r) Ui.CopyToClipboard(r.FilePath); }));
        m.Items.Add(SysInfoUi.Item("Open file location", () => { if (_kGrid.SelectedItem is DriversKernelRow r) Reveal(r.FilePath); }));
        m.Items.Add(SysInfoUi.Item("File properties (version, company)…", () =>
        {
            if (_kGrid.SelectedItem is DriversKernelRow r) Ui.Info(FileDetails(r.FilePath), r.Name);
        }));
        return m;
    }

    ContextMenu DeviceMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(SysInfoUi.Item("Copy row", () => CopyRows(_dGrid)));
        m.Items.Add(SysInfoUi.Item("Copy device ID", () => { if (_dGrid.SelectedItem is DriversDeviceRow r) Ui.CopyToClipboard(r.DeviceId); }));
        m.Items.Add(SysInfoUi.Item("Open the INF file location", () => { if (_dGrid.SelectedItem is DriversDeviceRow r) Reveal(r.InfPath); }));
        m.Items.Add(SysInfoUi.Item("Show the kernel driver", () =>
        {
            if (_dGrid.SelectedItem is DriversDeviceRow r && !string.IsNullOrEmpty(r.Service))
            {
                _filter.Text = r.Service;
                _tabs.SelectedItem = _kTab;
            }
        }));
        return m;
    }

    static void CopyRows(DataGrid grid)
    {
        var lines = new List<string>();
        foreach (var item in grid.SelectedItems)
        {
            if (item is DriversKernelRow k) lines.Add(k.ToText());
            else if (item is DriversDeviceRow d) lines.Add(d.ToText());
        }
        if (lines.Count > 0) Ui.CopyToClipboard(string.Join(Environment.NewLine, lines));
    }

    static void Reveal(string path)
    {
        if (string.IsNullOrEmpty(path)) { Status.Set("No file path for that driver.", StatusKind.Warn); return; }
        if (File.Exists(path)) { Shell.Reveal(path); return; }
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            Shell.Reveal(dir);
            Status.Set("The driver file is missing; opened the folder it should be in.", StatusKind.Warn);
        }
        else Status.Set("The driver file and its folder are missing: " + path, StatusKind.Warn);
    }

    static string FileDetails(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "The file is missing:\n" + path;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            var fi = new FileInfo(path);
            return $"{path}\n\nDescription: {Or(vi.FileDescription)}\nCompany: {Or(vi.CompanyName)}\nProduct: {Or(vi.ProductName)}\n" +
                   $"File version: {Or(vi.FileVersion)}\nSize: {Fmt.Bytes(fi.Length)}\nModified: {fi.LastWriteTime:yyyy-MM-dd HH:mm}\n\n" +
                   "Company and version are what the file says about itself; they are not a signature check.";
        }
        catch (Exception ex) { return path + "\n\nCould not read the file: " + ex.Message; }
    }

    static string Or(string s) => string.IsNullOrWhiteSpace(s) ? Fmt.Na : s.Trim();

    async Task ExportCsv()
    {
        bool kernel = ReferenceEquals(_tabs.SelectedItem, _kTab);
        var view = kernel ? _kView : _dView;
        if (view == null || view.Count == 0) { Status.Set("Nothing to export.", StatusKind.Warn); return; }
        var sb = new StringBuilder();
        if (kernel)
        {
            sb.AppendLine("name,display_name,state,start_mode,type,signed,signer,provider,version,note,file,file_exists");
            foreach (var o in view)
                if (o is DriversKernelRow r)
                    sb.AppendLine(string.Join(",", new[] { r.Name, r.DisplayName, r.State, r.StartMode, r.Type, r.Signed, r.Signer, r.Provider, r.Version, r.Note, r.FilePath, r.FileExists ? "yes" : "no" }.Select(SysInfoUi.Csv)));
        }
        else
        {
            sb.AppendLine("device,class,provider,version,date,signed,signer,inf,service,manufacturer,device_id,note");
            foreach (var o in view)
                if (o is DriversDeviceRow r)
                    sb.AppendLine(string.Join(",", new[] { r.Device, r.DeviceClass, r.Provider, r.Version, r.Date.HasValue ? r.Date.Value.ToString("yyyy-MM-dd") : "", r.Signed, r.Signer, r.Inf, r.Service, r.Manufacturer, r.DeviceId, r.Note }.Select(SysInfoUi.Csv)));
        }
        string path = Ui.SaveFile($"Winnow {(kernel ? "kernel" : "device")} drivers {DateTime.Now:yyyy-MM-dd HHmm}.csv", "CSV file|*.csv");
        if (path == null) return;
        string text = sb.ToString();
        await Task.Run(() => File.WriteAllText(path, text, new UTF8Encoding(false)));
        Status.Set($"Exported {view.Count} rows to {path}", StatusKind.Good);
    }
}

// ============================================================== rows

public sealed class DriversKernelRow
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string State { get; set; } = "";
    public string StartMode { get; set; } = "";
    public string Type { get; set; } = "";
    public string Signed { get; set; } = Fmt.Na;
    public string Signer { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Version { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool FileExists { get; set; }
    public bool ThirdParty { get; set; }
    public bool KnownBad { get; set; }
    public string Note { get; set; } = "";
    public string Kind { get; set; }        // Good / Warn / Bad
    public int Severity { get; set; }       // 0 none, 1 good, 2 warn, 3 bad

    public string ToText() => string.Join("\t", Name, DisplayName, State, StartMode, Signed, Provider, Version, Note, FilePath);
}

public sealed class DriversDeviceRow
{
    public string Device { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? Date { get; set; }
    public string Signed { get; set; } = Fmt.Na;
    public string Signer { get; set; } = "";
    public string Inf { get; set; } = "";
    public string InfPath { get; set; } = "";
    public string Service { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public bool ThirdParty { get; set; }
    public string Note { get; set; } = "";
    public string Kind { get; set; }
    public int Severity { get; set; }

    public string ToText() => string.Join("\t", Device, DeviceClass, Provider, Version,
        Date.HasValue ? Date.Value.ToString("yyyy-MM-dd") : Fmt.Na, Signed, Signer, Inf, Service, Note);
}

internal sealed class DriversData
{
    public List<DriversKernelRow> Kernel = new();
    public List<DriversDeviceRow> Devices = new();
    public bool KernelOk, DevicesOk;
}

// ============================================================== reading (background thread)

internal static class DriversReader
{
    /// <summary>Well-known ring-0 helpers. Prefix entries match any name starting with the pattern.</summary>
    static readonly (string pattern, bool prefix, string note)[] Known =
    {
        ("WinRing0", true, "WinRing0 - old open hardware-access driver; Defender blocks it"),
        ("RTCore", true, "MSI Afterburner / RivaTuner RTCore driver"),
        ("gdrv", true, "GIGABYTE gdrv hardware-access driver"),
        ("AsIO", true, "ASUS AsIO hardware-access driver"),
        ("ene", false, "ENE RGB controller driver (ene.sys)"),
        ("cpuz", true, "CPU-Z driver (older versions are vulnerable)"),
        ("HwRwDrv", false, "HwRwDrv hardware read/write driver"),
        ("inpoutx64", false, "InpOut x64 port I/O driver"),
        ("dbutil_2_3", false, "Dell dbutil_2_3 BIOS utility driver"),
        ("iqvw64e", false, "Intel network adapter diagnostic driver"),
        ("mhyprot", true, "mhyprot anti-cheat driver, abused by ransomware"),
        ("NTIOLib", true, "MSI NTIOLib hardware-access driver"),
        ("AsrDrv", true, "ASRock AsrDrv hardware-access driver"),
        ("WinIo", true, "WinIo port and memory access driver"),
    };

    const string PawnIoNote = "Sensor driver used by Winnow (Microsoft-signed)";

    static string KnownNote(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var (pattern, prefix, note) in Known)
        {
            if (prefix ? name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                       : name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return note;
        }
        return null;
    }

    static bool IsMicrosoft(string provider) =>
        !string.IsNullOrWhiteSpace(provider) && provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turn a service ImagePath into a real file path.</summary>
    public static string NormalisePath(string raw, string serviceName)
    {
        string win = Paths.WindowsDir;
        string p = (raw ?? "").Trim().Trim('"');
        if (p.Length == 0) return Path.Combine(win, "System32", "drivers", serviceName + ".sys");
        if (p.StartsWith(@"\??\", StringComparison.Ordinal) || p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(win, p.Substring(12));
        else if (p.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(win, p.Substring(11));
        try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
        if (p.StartsWith(@"\", StringComparison.Ordinal) && !p.StartsWith(@"\\", StringComparison.Ordinal))
            p = (Path.GetPathRoot(win) ?? @"C:\").TrimEnd('\\') + p;              // \Windows\... on the system drive
        else if (!Path.IsPathRooted(p))
            p = Path.Combine(win, p);                                            // System32\drivers\x.sys
        // A driver path never carries arguments, but strip anything after ".sys " defensively.
        int sys = p.IndexOf(".sys ", StringComparison.OrdinalIgnoreCase);
        if (sys > 0) p = p.Substring(0, sys + 4);
        return p;
    }

    public static DriversData Read(IProgress<string> progress)
    {
        var data = new DriversData();

        // 1. Device drivers, with signatures
        progress?.Report("Reading device drivers (Win32_PnPSignedDriver) - this can take a few seconds…");
        var byDeviceId = new Dictionary<string, DriversDeviceRow>(StringComparer.OrdinalIgnoreCase);
        data.DevicesOk = SysInfoWmi.Query(null,
            "SELECT DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName, IsSigned, Signer, InfName, DeviceID, Manufacturer FROM Win32_PnPSignedDriver",
            mo =>
            {
                string device = SysInfoWmi.S(mo, "DeviceName");
                string inf = SysInfoWmi.S(mo, "InfName");
                string ver = SysInfoWmi.S(mo, "DriverVersion");
                if (device.Length == 0 && inf.Length == 0 && ver.Length == 0) return;   // a device with no driver
                var r = new DriversDeviceRow
                {
                    Device = device.Length > 0 ? device : Fmt.Na,
                    DeviceClass = SysInfoWmi.S(mo, "DeviceClass"),
                    Provider = SysInfoWmi.S(mo, "DriverProviderName"),
                    Version = ver,
                    Date = SysInfoWmi.CimDate(SysInfoWmi.S(mo, "DriverDate")),
                    Signer = SysInfoWmi.S(mo, "Signer"),
                    Inf = inf,
                    InfPath = inf.Length > 0 ? Path.Combine(Paths.WindowsDir, "INF", inf) : "",
                    DeviceId = SysInfoWmi.S(mo, "DeviceID"),
                    Manufacturer = SysInfoWmi.S(mo, "Manufacturer"),
                };
                bool? signed = SysInfoWmi.B(mo, "IsSigned");
                r.Signed = signed == true ? "Yes" : signed == false ? "No" : Fmt.Na;
                r.ThirdParty = !IsMicrosoft(r.Provider);
                if (signed == false) { r.Note = "Unsigned driver"; r.Kind = "Bad"; r.Severity = 3; }
                data.Devices.Add(r);
                if (r.DeviceId.Length > 0) byDeviceId[r.DeviceId] = r;
            });

        // 2. Which service (kernel driver) each device runs on
        progress?.Report("Matching devices to their kernel drivers (Win32_PnPEntity)…");
        var byService = new Dictionary<string, List<DriversDeviceRow>>(StringComparer.OrdinalIgnoreCase);
        SysInfoWmi.Query(null, "SELECT DeviceID, Service FROM Win32_PnPEntity", mo =>
        {
            string id = SysInfoWmi.S(mo, "DeviceID");
            string svc = SysInfoWmi.S(mo, "Service");
            if (id.Length == 0 || svc.Length == 0) return;
            if (!byDeviceId.TryGetValue(id, out var dev)) return;
            dev.Service = svc;
            if (!byService.TryGetValue(svc, out var list)) byService[svc] = list = new List<DriversDeviceRow>();
            list.Add(dev);
        });

        // 3. Kernel drivers
        progress?.Report("Reading kernel drivers (Win32_SystemDriver) and checking their files…");
        data.KernelOk = SysInfoWmi.Query(null,
            "SELECT Name, DisplayName, State, StartMode, PathName, ServiceType FROM Win32_SystemDriver", mo =>
            {
                string name = SysInfoWmi.S(mo, "Name");
                var r = new DriversKernelRow
                {
                    Name = name,
                    DisplayName = SysInfoWmi.S(mo, "DisplayName"),
                    State = SysInfoWmi.S(mo, "State"),
                    StartMode = SysInfoWmi.S(mo, "StartMode"),
                    Type = SysInfoWmi.S(mo, "ServiceType"),
                };
                r.FilePath = NormalisePath(SysInfoWmi.S(mo, "PathName"), name);
                try { r.FileExists = File.Exists(r.FilePath); } catch { r.FileExists = false; }

                string company = "", fileVer = "";
                if (r.FileExists)
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(r.FilePath);
                        company = (vi.CompanyName ?? "").Trim();
                        fileVer = (vi.FileVersion ?? "").Trim();
                        int sp = fileVer.IndexOf(' ');
                        if (sp > 0) fileVer = fileVer.Substring(0, sp);   // "10.0.26100.1 (WinBuild...)"
                    }
                    catch { }
                }

                // Signature, provider and version from a device that uses this driver, when there is one.
                if (byService.TryGetValue(name, out var devs) && devs.Count > 0)
                {
                    var d = devs[0];
                    r.Signed = d.Signed;
                    r.Signer = d.Signer;
                    r.Provider = d.Provider.Length > 0 ? d.Provider : company;
                    r.Version = d.Version.Length > 0 ? d.Version : fileVer;
                }
                else
                {
                    r.Signed = Fmt.Na;
                    r.Provider = company;
                    r.Version = fileVer;
                }
                r.ThirdParty = !IsMicrosoft(r.Provider) && !IsMicrosoft(company);

                var notes = new List<string>();
                int sev = 0;
                string fileBase = "";
                try { fileBase = Path.GetFileNameWithoutExtension(r.FilePath) ?? ""; } catch { }
                bool pawn = name.Equals("PawnIO", StringComparison.OrdinalIgnoreCase) || fileBase.Equals("PawnIO", StringComparison.OrdinalIgnoreCase);
                if (pawn) { notes.Add(PawnIoNote); sev = Math.Max(sev, 1); }
                else
                {
                    string known = KnownNote(name) ?? KnownNote(fileBase);
                    if (known != null)
                    {
                        notes.Add("Known vulnerable or abusable ring-0 helper: " + known);
                        r.KnownBad = true;
                        sev = 3;
                    }
                }
                if (r.Signed == "No") { notes.Add("Unsigned"); sev = 3; }
                if (!r.FileExists)
                {
                    notes.Add(r.State.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "Driver file missing (but loaded)" : "Driver file missing");
                    sev = Math.Max(sev, 2);
                }
                r.Note = string.Join("; ", notes);
                r.Severity = sev;
                r.Kind = sev == 3 ? "Bad" : sev == 2 ? "Warn" : sev == 1 ? "Good" : null;
                data.Kernel.Add(r);
            });

        // A device whose service is a flagged kernel driver gets the same flag.
        var flagged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in data.Kernel)
            if (k.KnownBad && !string.IsNullOrEmpty(k.Name)) flagged[k.Name] = k.Note;
        foreach (var dev in data.Devices)
        {
            if (dev.Service.Length > 0 && flagged.TryGetValue(dev.Service, out var note))
            {
                dev.Note = dev.Note.Length > 0 ? dev.Note + "; " + note : note;
                dev.Kind = "Bad";
                dev.Severity = 3;
            }
            else if (dev.Service.Equals("PawnIO", StringComparison.OrdinalIgnoreCase) && dev.Severity == 0)
            {
                dev.Note = PawnIoNote;
                dev.Kind = "Good";
                dev.Severity = 1;
            }
        }

        progress?.Report("Done.");
        return data;
    }
}
