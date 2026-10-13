using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// System Information, like the classic panel: Windows and its security state,
/// the machine, CPU, memory modules, GPUs, disks, network adapters and the
/// sensor stack. Read once (in the background) the first time the page opens.
/// Anything Winnow could not read shows "—".
/// </summary>
public sealed class SystemInfoView : UserControl, IView
{
    readonly TextBlock _loading = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    readonly StackPanel _col1 = new(), _col2 = new();
    readonly Grid _cols = new();
    readonly Button _refresh, _copyAll, _save;
    List<SysInfoSection> _sections = new();
    readonly List<Border> _cards = new();
    bool _loaded, _busy, _twoCols;

    public SystemInfoView()
    {
        _loading.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(Ui.Header("System Info",
            "What this PC is: Windows and its security state, the hardware, and Winnow's own sensor access. " +
            "Values Winnow could not read show —. Right-click or double-click a row to copy it."));

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _refresh = Ui.Button("Refresh", (_, _) => Ui.Run(LoadAsync, "System information"), null, "Read everything again");
        _copyAll = Ui.Button("Copy all as text", (_, _) => Ui.CopyToClipboard(ReportText()));
        _save = Ui.Button("Save report…", (_, _) => Ui.Run(SaveReport, "Saving the report"), "Accent");
        bar.Children.Add(_refresh);
        bar.Children.Add(_copyAll);
        bar.Children.Add(_save);
        top.Children.Add(bar);
        top.Children.Add(_loading);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_col2, 1);
        _cols.Children.Add(_col1);
        _cols.Children.Add(_col2);
        root.Children.Add(new ScrollViewer
        {
            Content = _cols,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        Content = root;

        SizeChanged += (_, e) =>
        {
            bool two = e.NewSize.Width >= 900;
            if (two != _twoCols) { _twoCols = two; LayoutCards(); }
        };
        _copyAll.IsEnabled = false;
        _save.IsEnabled = false;
    }

    public void OnShown()
    {
        if (!_loaded && !_busy) Ui.Run(LoadAsync, "System information");
    }

    public void OnHidden() { }

    async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _refresh.IsEnabled = false;
        _loading.Visibility = Visibility.Visible;
        _loading.Text = "Reading system information… (WMI can take a few seconds)";
        try
        {
            var sections = await Task.Run(() => SysInfoCollector.Collect());
            _sections = sections;
            _cards.Clear();
            foreach (var s in sections) _cards.Add(BuildCard(s));
            _twoCols = ActualWidth >= 900;
            LayoutCards();
            _loaded = true;
            _loading.Text = Elevation.IsAdmin
                ? $"Read at {DateTime.Now:HH:mm:ss}."
                : $"Read at {DateTime.Now:HH:mm:ss}. Winnow is not running as administrator, so TPM and BitLocker show —.";
            _copyAll.IsEnabled = true;
            _save.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _loading.Text = "Could not read system information: " + ex.Message;
            throw;
        }
        finally
        {
            _busy = false;
            _refresh.IsEnabled = true;
        }
    }

    /// <summary>Spread the cards over one or two columns, filling the shorter column first.</summary>
    void LayoutCards()
    {
        _col1.Children.Clear();
        _col2.Children.Clear();
        _cols.ColumnDefinitions[1].Width = _twoCols ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        int h1 = 0, h2 = 0;
        for (int i = 0; i < _cards.Count && i < _sections.Count; i++)
        {
            int weight = _sections[i].Rows.Count + 3;
            if (!_twoCols || h1 <= h2) { _col1.Children.Add(_cards[i]); h1 += weight; }
            else { _col2.Children.Add(_cards[i]); h2 += weight; }
        }
    }

    Border BuildCard(SysInfoSection sec)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(sec.Title, "H2"));
        foreach (var r in sec.Rows) sp.Children.Add(BuildRow(sec, r));
        return Ui.Card(sp);
    }

    FrameworkElement BuildRow(SysInfoSection sec, SysInfoRow r)
    {
        var g = new Grid { Background = Brushes.Transparent, Margin = new Thickness(-4, 0, -4, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = new TextBlock { Text = r.Key, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 10, 2) };
        k.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var v = new TextBlock { Text = string.IsNullOrWhiteSpace(r.Value) ? Fmt.Na : r.Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 4, 2) };
        if (r.Kind != null) v.SetResourceReference(TextBlock.ForegroundProperty, r.Kind);
        Grid.SetColumn(v, 1);
        g.Children.Add(k);
        g.Children.Add(v);

        var menu = new ContextMenu();
        menu.Items.Add(SysInfoUi.Item("Copy value", () => Ui.CopyToClipboard(v.Text)));
        menu.Items.Add(SysInfoUi.Item("Copy row", () => Ui.CopyToClipboard(r.Key + ": " + v.Text)));
        menu.Items.Add(SysInfoUi.Item("Copy section", () => Ui.CopyToClipboard(SectionText(sec))));
        menu.Items.Add(new Separator());
        menu.Items.Add(SysInfoUi.Item("Copy all as text", () => Ui.CopyToClipboard(ReportText())));
        g.ContextMenu = menu;
        g.MouseEnter += (_, _) => g.SetResourceReference(Panel.BackgroundProperty, "Hover");
        g.MouseLeave += (_, _) => g.Background = Brushes.Transparent;
        g.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Ui.CopyToClipboard(r.Key + ": " + v.Text); e.Handled = true; }
        };
        return g;
    }

    static string SectionText(SysInfoSection sec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== " + sec.Title + " ==");
        foreach (var r in sec.Rows)
            sb.AppendLine($"{r.Key,-30} {(string.IsNullOrWhiteSpace(r.Value) ? Fmt.Na : r.Value)}");
        return sb.ToString();
    }

    string ReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Winnow system report");
        sb.AppendLine($"Created {DateTime.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName}");
        sb.AppendLine();
        foreach (var s in _sections)
        {
            sb.Append(SectionText(s));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    async Task SaveReport()
    {
        if (_sections.Count == 0) { Status.Set("Nothing read yet.", StatusKind.Warn); return; }
        string path = Ui.SaveFile("Winnow system info " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".txt", "Text file|*.txt");
        if (path == null) return;
        string text = ReportText();
        await Task.Run(() => File.WriteAllText(path, text, new UTF8Encoding(false)));
        Status.Set("Saved the system report to " + path, StatusKind.Good);
    }
}

// ============================================================== data model

internal sealed class SysInfoRow
{
    public string Key = "";
    public string Value = "";
    public string Kind;          // null, "Good", "Warn" or "Bad"
}

internal sealed class SysInfoSection
{
    public string Title = "";
    public List<SysInfoRow> Rows = new();

    public SysInfoSection(string title) { Title = title; }

    public void Add(string key, string value, string kind = null) =>
        Rows.Add(new SysInfoRow { Key = key, Value = string.IsNullOrWhiteSpace(value) ? Fmt.Na : value.Trim(), Kind = kind });
}

// ============================================================== collection (background thread)

internal static class SysInfoCollector
{
    const string NeedsAdmin = "— (needs administrator)";

    public static List<SysInfoSection> Collect()
    {
        var list = new List<SysInfoSection>();
        bool hypervisor = false;
        Try(list, "Windows", s => Windows(s));
        Try(list, "Security", s => Security(s));
        Try(list, "Computer", s => hypervisor = Computer(s));
        Try(list, "Processor", s => Processor(s, hypervisor));
        Try(list, "Memory", s => Memory(s));
        Try(list, "Graphics", s => Graphics(s));
        Try(list, "Disks", s => Disks(s));
        Try(list, "Volumes", s => Volumes(s));
        Try(list, "Network adapters", s => Network(s));
        Try(list, "Sensors and Winnow", s => SensorStack(s));
        return list;
    }

    static void Try(List<SysInfoSection> list, string title, Action<SysInfoSection> fill)
    {
        var s = new SysInfoSection(title);
        try { fill(s); }
        catch (Exception ex)
        {
            Log.Write("sysinfo " + title + ": " + ex.Message);
            s.Add("Error", "Could not read this section: " + ex.Message, "Warn");
        }
        if (s.Rows.Count == 0) s.Add("Nothing found", Fmt.Na);
        list.Add(s);
    }

    static string YesNo(bool? b, string yes = "Yes", string no = "No") => b == true ? yes : b == false ? no : Fmt.Na;

    // ---------------------------------------------------------------- Windows

    static void Windows(SysInfoSection s)
    {
        s.Add("Windows", WinVer.Summary);
        s.Add("Edition", WinVer.EditionName + (WinVer.EditionId.Length > 0 ? " (" + WinVer.EditionId + ")" : ""));
        s.Add("Version", WinVer.DisplayVersion);
        s.Add("Build", WinVer.Build > 0 ? $"{WinVer.Build}.{WinVer.Ubr}" : Fmt.Na);
        s.Add("Architecture", RuntimeInformation.OSArchitecture.ToString());
        s.Add("Installed", InstallDate());
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var boot = DateTime.Now - up;
        s.Add("Last boot", $"{boot:ddd d MMM yyyy HH:mm} (up {Fmt.Duration(up)})");
        s.Add("Computer name", Environment.MachineName);
        s.Add("Signed-in user", Environment.UserDomainName + "\\" + Environment.UserName);

        string where = "";
        SysInfoWmi.Query(null, "SELECT * FROM Win32_ComputerSystem", mo =>
        {
            bool? dom = SysInfoWmi.B(mo, "PartOfDomain");
            if (dom == true) where = "Domain: " + SysInfoWmi.S(mo, "Domain");
            else
            {
                string wg = SysInfoWmi.S(mo, "Workgroup");
                where = "Workgroup: " + (wg.Length > 0 ? wg : SysInfoWmi.S(mo, "Domain"));
            }
        });
        s.Add("Domain or workgroup", where);
        s.Add("Winnow running as administrator", Elevation.IsAdmin ? "Yes" : "No", Elevation.IsAdmin ? "Good" : "Warn");
    }

    static string InstallDate()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k?.GetValue("InstallDate") is int secs && secs != 0)
                return DateTimeOffset.FromUnixTimeSeconds((uint)secs).LocalDateTime.ToString("ddd d MMM yyyy HH:mm");
            if (k?.GetValue("InstallTime") is long ft && ft > 0)
                return DateTime.FromFileTimeUtc(ft).ToLocalTime().ToString("ddd d MMM yyyy HH:mm");
        }
        catch { }
        return Fmt.Na;
    }

    // ---------------------------------------------------------------- Security

    static void Security(SysInfoSection s)
    {
        // Firmware type: 1 = legacy BIOS, 2 = UEFI
        int fw = 0;
        try { if (!SysInfoNative.GetFirmwareType(out fw)) fw = 0; } catch { fw = 0; }
        s.Add("Firmware", fw == 2 ? "UEFI" : fw == 1 ? "Legacy BIOS" : Fmt.Na, fw == 1 ? "Warn" : null);

        // Secure Boot
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            object v = k?.GetValue("UEFISecureBootEnabled");
            if (v is int sb) s.Add("Secure Boot", sb == 1 ? "On" : "Off", sb == 1 ? "Good" : "Warn");
            else if (fw == 1) s.Add("Secure Boot", "Not available (legacy BIOS boot)", "Warn");
            else s.Add("Secure Boot", Fmt.Na);
        }
        catch { s.Add("Secure Boot", Fmt.Na); }

        // TPM
        bool tpmFound = false;
        bool tpmOk = SysInfoWmi.Query(@"root\cimv2\Security\MicrosoftTpm", "SELECT * FROM Win32_Tpm", mo =>
        {
            tpmFound = true;
            bool? en = SysInfoWmi.B(mo, "IsEnabled_InitialValue");
            bool? act = SysInfoWmi.B(mo, "IsActivated_InitialValue");
            string spec = SysInfoWmi.S(mo, "SpecVersion");
            string ver = spec.Length > 0 ? spec.Split(',')[0].Trim() : "";
            string maker = SysInfoWmi.S(mo, "ManufacturerIdTxt");
            var parts = new List<string> { en == true ? "Enabled" : en == false ? "Disabled" : "Present" };
            if (act == false) parts.Add("not activated");
            if (ver.Length > 0) parts.Add("version " + ver);
            if (maker.Length > 0) parts.Add(maker);
            s.Add("TPM", string.Join(" · ", parts), en == true ? "Good" : en == false ? "Warn" : null);
        });
        if (!tpmOk) s.Add("TPM", Elevation.IsAdmin ? Fmt.Na : NeedsAdmin);
        else if (!tpmFound) s.Add("TPM", "None found", "Warn");

        // Virtualisation-based security and memory integrity
        bool dgOk = SysInfoWmi.Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard", mo =>
        {
            long? vbs = SysInfoWmi.L(mo, "VirtualizationBasedSecurityStatus");
            int vbsI = vbs.HasValue ? (int)vbs.Value : -1;
            var running = SysInfoWmi.Ints(mo, "SecurityServicesRunning");
            var configured = SysInfoWmi.Ints(mo, "SecurityServicesConfigured");
            s.Add("Virtualisation-based security", vbsI == 2 ? "Running" : vbsI == 1 ? "Enabled, not running" : vbsI == 0 ? "Off" : Fmt.Na,
                  vbsI == 2 ? "Good" : null);
            s.Add("Memory integrity (HVCI)", running.Contains(2) ? "Running" : configured.Contains(2) ? "Configured, not running" : "Off",
                  running.Contains(2) ? "Good" : null);
            s.Add("Credential Guard", running.Contains(1) ? "Running" : configured.Contains(1) ? "Configured, not running" : "Off");
        });
        if (!dgOk) s.Add("Virtualisation-based security", Fmt.Na);

        // BitLocker on the system drive
        string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\');
        bool blFound = false;
        bool blOk = SysInfoWmi.Query(@"root\cimv2\Security\MicrosoftVolumeEncryption",
            "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '" + sysDrive + "'", mo =>
            {
                blFound = true;
                long? ps = SysInfoWmi.L(mo, "ProtectionStatus");
                string text = ps == 1 ? "On (protected)" : ps == 0 ? "Off or suspended" : ps == 2 ? "Unknown (volume locked)" : Fmt.Na;
                s.Add("BitLocker (" + sysDrive + ")", text, ps == 1 ? "Good" : null);
            });
        if (!blOk) s.Add("BitLocker (" + sysDrive + ")", Elevation.IsAdmin ? Fmt.Na : NeedsAdmin);
        else if (!blFound) s.Add("BitLocker (" + sysDrive + ")", "Not reported for this volume");

        // Microsoft Defender
        bool defFound = false;
        bool defOk = SysInfoWmi.Query(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus", mo =>
        {
            defFound = true;
            bool? av = SysInfoWmi.B(mo, "AntivirusEnabled");
            bool? rtp = SysInfoWmi.B(mo, "RealTimeProtectionEnabled");
            bool? tamper = SysInfoWmi.B(mo, "IsTamperProtected");
            string mode = SysInfoWmi.S(mo, "AMRunningMode");
            string sigVer = SysInfoWmi.S(mo, "AntivirusSignatureVersion");
            DateTime? sig = SysInfoWmi.CimDateTime(SysInfoWmi.S(mo, "AntivirusSignatureLastUpdated"));

            s.Add("Defender antivirus", YesNo(av, "On", "Off") + (mode.Length > 0 ? " · " + mode : ""),
                  av == true ? "Good" : av == false ? "Warn" : null);
            s.Add("Defender real-time protection", YesNo(rtp, "On", "Off"), rtp == true ? "Good" : rtp == false ? "Warn" : null);
            s.Add("Tamper protection", YesNo(tamper, "On", "Off"));
            if (sig.HasValue)
            {
                double days = (DateTime.Now - sig.Value).TotalDays;
                string age = days < 1 ? "today" : $"{days:0} day{(days >= 1.5 ? "s" : "")} ago";
                s.Add("Defender signatures", $"{sig.Value:ddd d MMM yyyy HH:mm} ({age})" + (sigVer.Length > 0 ? " · " + sigVer : ""),
                      days > 7 ? "Warn" : null);
            }
            else s.Add("Defender signatures", sigVer.Length > 0 ? sigVer : Fmt.Na);
        });
        if (!defOk || !defFound) s.Add("Defender antivirus", Fmt.Na);

        // Antivirus products registered with Windows Security Center (workstations only)
        var avProducts = new List<string>();
        SysInfoWmi.Query(@"root\SecurityCenter2", "SELECT * FROM AntiVirusProduct", mo =>
        {
            string name = SysInfoWmi.S(mo, "displayName");
            long? state = SysInfoWmi.L(mo, "productState");
            string on = state.HasValue ? (((state.Value >> 8) & 0x10) != 0 ? "on" : "off") : "";
            if (name.Length > 0) avProducts.Add(on.Length > 0 ? $"{name} ({on})" : name);
        });
        s.Add("Antivirus products", avProducts.Count > 0 ? string.Join(", ", avProducts) : Fmt.Na);
    }

    // ---------------------------------------------------------------- Computer

    /// <summary>Returns true when a hypervisor is running (Hyper-V, VBS).</summary>
    static bool Computer(SysInfoSection s)
    {
        bool hyper = false;
        SysInfoWmi.Query(null, "SELECT * FROM Win32_ComputerSystem", mo =>
        {
            s.Add("Manufacturer", SysInfoWmi.S(mo, "Manufacturer"));
            s.Add("Model", SysInfoWmi.S(mo, "Model"));
            s.Add("System type", SysInfoWmi.S(mo, "SystemType"));
            hyper = SysInfoWmi.B(mo, "HypervisorPresent") == true;
        });
        SysInfoWmi.Query(null, "SELECT * FROM Win32_BaseBoard", mo =>
        {
            string board = string.Join(" ", new[] { SysInfoWmi.S(mo, "Manufacturer"), SysInfoWmi.S(mo, "Product") }.Where(x => x.Length > 0));
            string ver = SysInfoWmi.S(mo, "Version");
            s.Add("Motherboard", board + (ver.Length > 0 && !ver.Equals("Default string", StringComparison.OrdinalIgnoreCase) ? " (rev " + ver + ")" : ""));
        });
        SysInfoWmi.Query(null, "SELECT * FROM Win32_BIOS", mo =>
        {
            DateTime? date = SysInfoWmi.CimDate(SysInfoWmi.S(mo, "ReleaseDate"));
            var parts = new[] { SysInfoWmi.S(mo, "Manufacturer"), SysInfoWmi.S(mo, "SMBIOSBIOSVersion"), date.HasValue ? date.Value.ToString("yyyy-MM-dd") : "" }
                        .Where(x => x.Length > 0);
            s.Add("BIOS / UEFI firmware", string.Join(" · ", parts));
        });
        s.Add("Hypervisor running", hyper ? "Yes (Hyper-V or virtualisation-based security)" : "No");
        return hyper;
    }

    // ---------------------------------------------------------------- Processor

    static void Processor(SysInfoSection s, bool hypervisor)
    {
        int n = 0;
        SysInfoWmi.Query(null, "SELECT * FROM Win32_Processor", mo =>
        {
            n++;
            string prefix = n > 1 ? $"CPU {n} " : "";
            string name = SysInfoWmi.S(mo, "Name");
            while (name.Contains("  ")) name = name.Replace("  ", " ");
            s.Add(prefix + "Name", name);
            long? cores = SysInfoWmi.L(mo, "NumberOfCores");
            long? logical = SysInfoWmi.L(mo, "NumberOfLogicalProcessors");
            s.Add(prefix + "Cores / logical processors", $"{(cores.HasValue ? cores.Value.ToString() : Fmt.Na)} / {(logical.HasValue ? logical.Value.ToString() : Fmt.Na)}");
            long? mhz = SysInfoWmi.L(mo, "MaxClockSpeed");
            s.Add(prefix + "Base clock", mhz.HasValue && mhz.Value > 0 ? Fmt.Mhz(mhz.Value) : Fmt.Na);
            long? l2 = SysInfoWmi.L(mo, "L2CacheSize");
            long? l3 = SysInfoWmi.L(mo, "L3CacheSize");
            s.Add(prefix + "L2 cache", l2.HasValue && l2.Value > 0 ? Fmt.Bytes(l2.Value * 1024) : Fmt.Na);
            s.Add(prefix + "L3 cache", l3.HasValue && l3.Value > 0 ? Fmt.Bytes(l3.Value * 1024) : Fmt.Na);
            s.Add(prefix + "Socket", SysInfoWmi.S(mo, "SocketDesignation"));
            bool? virt = SysInfoWmi.B(mo, "VirtualizationFirmwareEnabled");
            if (hypervisor) s.Add(prefix + "Virtualisation", "Enabled (a hypervisor is running)", "Good");
            else s.Add(prefix + "Virtualisation", virt == true ? "Enabled in firmware" : virt == false ? "Disabled in firmware" : Fmt.Na,
                       virt == true ? "Good" : null);
        });

        var classes = Sampler.EfficiencyClass;
        if (Sampler.IsHybrid && classes.Length > 0)
        {
            byte max = classes.Max();
            int p = classes.Count(c => c == max);
            s.Add("Hybrid design", $"Yes - {p} performance and {classes.Length - p} efficiency logical processors");
        }
        else s.Add("Hybrid design", classes.Length > 0 ? "No" : Fmt.Na);
    }

    // ---------------------------------------------------------------- Memory

    static string MemType(long? t) => (int)(t ?? -1) switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3",
        30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => ""
    };

    static void Memory(SysInfoSection s)
    {
        ulong total = 0;
        int modules = 0;
        var rows = new List<(string key, string value)>();
        SysInfoWmi.Query(null, "SELECT * FROM Win32_PhysicalMemory", mo =>
        {
            modules++;
            ulong cap = SysInfoWmi.U(mo, "Capacity") ?? 0;
            total += cap;
            long? speed = SysInfoWmi.L(mo, "Speed");
            long? conf = SysInfoWmi.L(mo, "ConfiguredClockSpeed");
            string type = MemType(SysInfoWmi.L(mo, "SMBIOSMemoryType"));
            string maker = SysInfoWmi.S(mo, "Manufacturer");
            string part = SysInfoWmi.S(mo, "PartNumber");
            var parts = new List<string> { cap > 0 ? Fmt.Bytes(cap) : Fmt.Na };
            if (type.Length > 0) parts.Add(type);
            if (speed.HasValue && speed.Value > 0)
                parts.Add(speed.Value + " MT/s" + (conf.HasValue && conf.Value > 0 && conf.Value != speed.Value ? $" (running at {conf.Value})" : ""));
            if (maker.Length > 0 && !maker.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) parts.Add(maker);
            if (part.Length > 0) parts.Add(part);
            string loc = SysInfoWmi.S(mo, "DeviceLocator");
            string bank = SysInfoWmi.S(mo, "BankLabel");
            string key = loc.Length > 0 ? loc : bank.Length > 0 ? bank : "Module " + modules;
            rows.Add((key, string.Join(" · ", parts)));
        });

        int slots = 0, anySlots = 0;
        SysInfoWmi.Query(null, "SELECT * FROM Win32_PhysicalMemoryArray", mo =>
        {
            int dev = (int)(SysInfoWmi.L(mo, "MemoryDevices") ?? 0);
            anySlots += dev;
            if (SysInfoWmi.L(mo, "Use") == 3) slots += dev;   // 3 = system memory
        });
        if (slots == 0) slots = anySlots;

        s.Add("Installed", total > 0 ? Fmt.Bytes(total) : Fmt.Na);
        var latest = Sampler.Latest;
        s.Add("Usable by Windows", latest != null && latest.MemTotal > 0 ? Fmt.Bytes(latest.MemTotal) : Fmt.Na);
        s.Add("Slots used", slots > 0 ? $"{modules} of {slots}" : modules > 0 ? modules.ToString() : Fmt.Na);
        foreach (var (key, value) in rows) s.Add(key, value);
    }

    // ---------------------------------------------------------------- Graphics

    static Dictionary<string, ulong> VideoMemory()
    {
        var d = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return d;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;
                try
                {
                    using var k = cls.OpenSubKey(name);
                    if (k == null) continue;
                    string desc = k.GetValue("DriverDesc") as string;
                    if (string.IsNullOrEmpty(desc)) continue;
                    object q = k.GetValue("HardwareInformation.qwMemorySize");
                    ulong mem = 0;
                    if (q is long l) mem = (ulong)l;
                    else if (q is byte[] b && b.Length >= 8) mem = BitConverter.ToUInt64(b, 0);
                    else if (q is int i) mem = (uint)i;
                    if (mem == 0)
                    {
                        object m = k.GetValue("HardwareInformation.MemorySize");
                        if (m is int mi) mem = (uint)mi;
                        else if (m is byte[] mb && mb.Length >= 4) mem = BitConverter.ToUInt32(mb, 0);
                    }
                    if (mem > 0 && (!d.TryGetValue(desc, out var have) || mem > have)) d[desc] = mem;
                }
                catch { }
            }
        }
        catch { }
        return d;
    }

    static void Graphics(SysInfoSection s)
    {
        var vram = VideoMemory();
        int n = 0;
        SysInfoWmi.Query(null, "SELECT * FROM Win32_VideoController", mo =>
        {
            n++;
            string name = SysInfoWmi.S(mo, "Name");
            s.Add("GPU " + n, name);
            string ver = SysInfoWmi.S(mo, "DriverVersion");
            DateTime? date = SysInfoWmi.CimDate(SysInfoWmi.S(mo, "DriverDate"));
            s.Add("  Driver", (ver.Length > 0 ? ver : Fmt.Na) + (date.HasValue ? " · " + date.Value.ToString("yyyy-MM-dd") : ""));
            s.Add("  Video memory", vram.TryGetValue(name, out var mem) ? Fmt.Bytes(mem) : Fmt.Na);
            long? w = SysInfoWmi.L(mo, "CurrentHorizontalResolution");
            long? h = SysInfoWmi.L(mo, "CurrentVerticalResolution");
            long? hz = SysInfoWmi.L(mo, "CurrentRefreshRate");
            if (w > 0 && h > 0)
                s.Add("  Display", $"{w} × {h}" + (hz > 1 ? $" at {hz} Hz" : ""));
        });
        if (n == 0) s.Add("GPU", Fmt.Na);
    }

    // ---------------------------------------------------------------- Disks

    static string BusName(long? b) => (int)(b ?? -1) switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "FireWire", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "Virtual (file)", 16 => "Storage Spaces",
        17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => ""
    };

    static void Disks(SysInfoSection s)
    {
        // Storage namespace: media type and bus, keyed by disk number
        var phys = new Dictionary<string, (string media, string bus, long? health)>();
        SysInfoWmi.Query(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk", mo =>
        {
            string id = SysInfoWmi.S(mo, "DeviceId");
            long? media = SysInfoWmi.L(mo, "MediaType");
            string m = media == 3 ? "HDD" : media == 4 ? "SSD" : media == 5 ? "SCM" : "";
            phys[id] = (m, BusName(SysInfoWmi.L(mo, "BusType")), SysInfoWmi.L(mo, "HealthStatus"));
        });

        var disks = new List<(long index, string value, string kind)>();
        SysInfoWmi.Query(null, "SELECT * FROM Win32_DiskDrive", mo =>
        {
            long index = SysInfoWmi.L(mo, "Index") ?? -1;
            var parts = new List<string> { SysInfoWmi.S(mo, "Model") };
            ulong? size = SysInfoWmi.U(mo, "Size");
            parts.Add(size.HasValue && size.Value > 0 ? Fmt.Bytes(size.Value) : Fmt.Na);
            string kind = null;
            if (phys.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out var p))
            {
                string busMedia = string.Join(" ", new[] { p.bus, p.media }.Where(x => x.Length > 0));
                if (busMedia.Length > 0) parts.Add(busMedia);
                if (p.health == 0) parts.Add("healthy");
                else if (p.health == 1) { parts.Add("health warning"); kind = "Warn"; }
                else if (p.health == 2) { parts.Add("unhealthy"); kind = "Bad"; }
            }
            else
            {
                string iface = SysInfoWmi.S(mo, "InterfaceType");
                if (iface.Length > 0) parts.Add(iface);
            }
            string fwRev = SysInfoWmi.S(mo, "FirmwareRevision");
            if (fwRev.Length > 0) parts.Add("firmware " + fwRev);
            disks.Add((index, string.Join(" · ", parts.Where(x => x.Length > 0)), kind));
        });
        foreach (var d in disks.OrderBy(x => x.index))
            s.Add(d.index >= 0 ? "Disk " + d.index : "Disk", d.value, d.kind);
    }

    static void Volumes(SysInfoSection s)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                if (!d.IsReady) { s.Add(d.Name.TrimEnd('\\'), "Not ready"); continue; }
                double freePct = d.TotalSize > 0 ? 100.0 * d.AvailableFreeSpace / d.TotalSize : 0;
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : d.VolumeLabel + " · ";
                s.Add(d.Name.TrimEnd('\\'),
                      $"{label}{d.DriveFormat} · {Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)} ({freePct:0}%)" +
                      (d.DriveType == DriveType.Removable ? " · removable" : ""),
                      freePct < 10 ? "Warn" : null);
            }
            catch (Exception ex) { s.Add(d.Name.TrimEnd('\\'), Fmt.Na + " (" + ex.Message + ")"); }
        }
    }

    // ---------------------------------------------------------------- Network

    static void Network(SysInfoSection s)
    {
        var ips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var v4 = nic.GetIPProperties().UnicastAddresses
                                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                .Select(a => a.Address.ToString()).ToList();
                    if (v4.Count > 0) ips[nic.Description] = string.Join(", ", v4);
                }
                catch { }
            }
        }
        catch { }

        int n = 0;
        SysInfoWmi.Query(null, "SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE AND NetEnabled = TRUE", mo =>
        {
            n++;
            string name = SysInfoWmi.S(mo, "Name");
            string conn = SysInfoWmi.S(mo, "NetConnectionID");
            ulong? speed = SysInfoWmi.U(mo, "Speed");
            string mac = SysInfoWmi.S(mo, "MACAddress");
            var parts = new List<string> { name };
            if (speed.HasValue && speed.Value > 0 && speed.Value < 1_000_000_000_000UL) parts.Add(Fmt.Bits(speed.Value / 8.0));
            if (ips.TryGetValue(name, out var ip)) parts.Add(ip);
            if (mac.Length > 0) parts.Add("MAC " + mac);
            s.Add(conn.Length > 0 ? conn : "Adapter " + n, string.Join(" · ", parts.Where(x => x.Length > 0)));
        });
        if (n == 0) s.Add("Adapters", "No connected physical adapters found");
    }

    // ---------------------------------------------------------------- Sensors

    static void SensorStack(SysInfoSection s)
    {
        s.Add("Hardware sensors", Settings.Current.SensorsEnabled ? Sensors.Status : "Off (Settings)",
              Sensors.Status == "OK" ? "Good" : Settings.Current.SensorsEnabled ? "Warn" : null);
        bool pawn = Sensors.IsPawnIoInstalled();
        s.Add("PawnIO sensor driver", pawn ? "Installed (Microsoft-signed)" : "Not installed - CPU temperatures and power are unavailable",
              pawn ? "Good" : "Warn");
        var readings = Sensors.Latest;
        s.Add("Sensor readings", readings != null && readings.Count > 0 ? readings.Count.ToString() : Fmt.Na);
        var latest = Sampler.Latest;
        s.Add("CPU temperature now", Fmt.Temp(latest?.CpuTempC));
        s.Add("GPU temperature now", Fmt.Temp(latest?.GpuTempC));
        s.Add("CPU package power now", Fmt.Watts(latest?.CpuWatts));
        s.Add("Per-process network (ETW)", NetEtw.Status, NetEtw.Running ? "Good" : "Warn");
        s.Add("Per-process GPU counters", latest == null ? Fmt.Na : latest.PerProcessGpuAvailable ? "Available" : "Unavailable");
        s.Add("Winnow folder", Paths.AppDir);
    }
}

// ============================================================== shared helpers (also used by Users and Drivers)

internal static class SysInfoNative
{
    /// <summary>FIRMWARE_TYPE: 1 = BIOS, 2 = UEFI.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFirmwareType(out int firmwareType);
}

/// <summary>WMI with every failure caught: a query that cannot run returns false, never throws.</summary>
internal static class SysInfoWmi
{
    public static bool Query(string ns, string wql, Action<ManagementBaseObject> each, int timeoutSec = 30)
    {
        try
        {
            using var searcher = ns == null
                ? new ManagementObjectSearcher(wql)
                : new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(wql));
            searcher.Options.Timeout = TimeSpan.FromSeconds(timeoutSec);
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                using (mo)
                {
                    try { each(mo); }
                    catch (Exception ex) { Log.Write("wmi row (" + wql + "): " + ex.Message); }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("wmi " + (ns ?? @"root\cimv2") + " (" + wql + "): " + ex.Message);
            return false;
        }
    }

    public static object P(ManagementBaseObject mo, string name)
    {
        try { return mo[name]; }
        catch { return null; }
    }

    public static string S(ManagementBaseObject mo, string name) => (P(mo, name)?.ToString() ?? "").Trim();

    public static bool? B(ManagementBaseObject mo, string name)
    {
        object o = P(mo, name);
        if (o is bool b) return b;
        return null;
    }

    public static long? L(ManagementBaseObject mo, string name)
    {
        object o = P(mo, name);
        if (o == null) return null;
        try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static ulong? U(ManagementBaseObject mo, string name)
    {
        object o = P(mo, name);
        if (o == null) return null;
        try { return Convert.ToUInt64(o, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    public static List<int> Ints(ManagementBaseObject mo, string name)
    {
        var list = new List<int>();
        if (P(mo, name) is System.Array arr)
            foreach (object o in arr)
            {
                try { list.Add(Convert.ToInt32(o, CultureInfo.InvariantCulture)); } catch { }
            }
        return list;
    }

    /// <summary>The date part of a CIM datetime (yyyymmddHHMMSS.mmmmmm+UUU). Driver and BIOS dates are nominal dates, so no time zone shift.</summary>
    public static DateTime? CimDate(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 8) return null;
        if (DateTime.TryParseExact(s.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return null;
    }

    /// <summary>A full CIM datetime as local time.</summary>
    public static DateTime? CimDateTime(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { }
        if (DateTime.TryParseExact(s.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return null;
    }
}

internal static class SysInfoUi
{
    public static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    /// <summary>A DataGrid row style that colours rows by their "Kind" property (Good / Warn / Bad), on top of the theme's row style.</summary>
    public static Style KindRowStyle()
    {
        var baseStyle = Application.Current.TryFindResource(typeof(DataGridRow)) as Style;
        var st = baseStyle != null ? new Style(typeof(DataGridRow), baseStyle) : new Style(typeof(DataGridRow));
        foreach (var k in new[] { "Good", "Warn", "Bad" })
        {
            var t = new DataTrigger { Binding = new Binding("Kind"), Value = k };
            t.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(k)));
            st.Triggers.Add(t);
        }
        return st;
    }

    /// <summary>Quote a CSV field when it needs it.</summary>
    public static string Csv(string v)
    {
        v ??= "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
