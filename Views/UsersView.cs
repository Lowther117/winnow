using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Who is signed in and what each user's processes cost, plus the local
/// accounts and who is in the Administrators group. Read-only.
/// Usage refreshes every tick; accounts are read on demand.
/// </summary>
public sealed class UsersView : UserControl, IView
{
    // ---- usage
    readonly ObservableCollection<UsersRow> _rows = new();
    readonly Dictionary<string, UsersRow> _byKey = new();
    readonly ListCollectionView _view;
    readonly DataGrid _grid;
    readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    readonly Dictionary<string, string> _userByKey = new();   // process key -> owner; only touched by the background aggregation
    int _busy;

    // ---- accounts
    readonly DataGrid _accGrid;
    readonly TextBlock _accStatus = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _admins = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    readonly Button _accBtn;
    readonly TabControl _tabs = new();
    bool _accLoaded, _accBusy;

    public UsersView()
    {
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        _accStatus.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = Ui.Header("Users",
            "Signed-in sessions and what each user's processes are using right now, and the local accounts on this PC. Read-only.");
        root.Children.Add(header);

        // ---- usage tab
        _view = new ListCollectionView(_rows);
        _view.SortDescriptions.Add(new SortDescription(nameof(UsersRow.Cpu), ListSortDirection.Descending));
        _view.IsLiveSorting = true;
        foreach (var p in new[] { "User", "Session", "Processes", "Cpu", "Mem", "Disk", "Net" }) _view.LiveSortingProperties.Add(p);

        _grid = new DataGrid
        {
            ItemsSource = _view,
            SelectionMode = DataGridSelectionMode.Single,
            RowHeight = double.NaN,
            MinRowHeight = 26,
            RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.VisibleWhenSelected,
        };
        _grid.Columns.Add(UsersCol("User", "User", null, 0, false));
        _grid.Columns.Add(UsersCol("Session", "SessionText", "Session", 130, false));
        _grid.Columns.Add(UsersCol("Processes", "Processes", null, 84, true));
        var cpu = UsersCol("CPU", "CpuText", "Cpu", 76, true);
        cpu.SortDirection = ListSortDirection.Descending;
        _grid.Columns.Add(cpu);
        _grid.Columns.Add(UsersCol("Memory", "MemText", "Mem", 90, true));
        _grid.Columns.Add(UsersCol("Disk", "DiskText", "Disk", 90, true));
        _grid.Columns.Add(UsersCol("Network", "NetText", "Net", 90, true));

        var details = new FrameworkElementFactory(typeof(TextBlock));
        details.SetBinding(TextBlock.TextProperty, new Binding("TopText") { Mode = BindingMode.OneWay });
        details.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        details.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        details.SetValue(TextBlock.FontSizeProperty, 11.5);
        details.SetValue(TextBlock.MarginProperty, new Thickness(24, 4, 8, 10));
        _grid.RowDetailsTemplate = new DataTemplate { VisualTree = details };

        var gridMenu = new ContextMenu();
        gridMenu.Items.Add(SysInfoUi.Item("Copy row", () => { if (_grid.SelectedItem is UsersRow r) Ui.CopyToClipboard(r.ToText()); }));
        gridMenu.Items.Add(SysInfoUi.Item("Copy this user's top processes", () => { if (_grid.SelectedItem is UsersRow r) Ui.CopyToClipboard(r.TopText); }));
        gridMenu.Items.Add(SysInfoUi.Item("Show in Processes", () => Nav.Go("processes")));
        _grid.ContextMenu = gridMenu;

        var usage = new DockPanel();
        var hint = Ui.Text("Click a row to see that user's busiest processes. — means the owner could not be read " +
                           "(a protected process, or another user's process while Winnow is not elevated).", "Sub");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Top);
        usage.Children.Add(hint);
        usage.Children.Add(_summary);
        usage.Children.Add(_grid);

        // ---- accounts tab
        _accGrid = new DataGrid { SelectionMode = DataGridSelectionMode.Single, RowStyle = SysInfoUi.KindRowStyle() };
        _accGrid.Columns.Add(UsersCol("Account", "Name", null, 150, false));
        _accGrid.Columns.Add(UsersCol("Full name", "FullName", null, 150, false));
        _accGrid.Columns.Add(UsersCol("Administrator", "Admin", null, 104, false));
        _accGrid.Columns.Add(UsersCol("Disabled", "Disabled", null, 76, false));
        _accGrid.Columns.Add(UsersCol("Locked out", "LockedOut", null, 84, false));
        _accGrid.Columns.Add(UsersCol("Password required", "PasswordRequired", null, 132, false));
        _accGrid.Columns.Add(UsersCol("Note", "Note", null, 0, false));
        _accGrid.Columns.Add(UsersCol("SID", "Sid", null, 300, false));
        var accMenu = new ContextMenu();
        accMenu.Items.Add(SysInfoUi.Item("Copy row", () => { if (_accGrid.SelectedItem is UsersAccountRow r) Ui.CopyToClipboard(r.ToText()); }));
        accMenu.Items.Add(SysInfoUi.Item("Copy SID", () => { if (_accGrid.SelectedItem is UsersAccountRow r) Ui.CopyToClipboard(r.Sid); }));
        _accGrid.ContextMenu = accMenu;

        _accBtn = Ui.Button("Refresh accounts", (_, _) => Ui.Run(LoadAccounts, "Reading accounts"));
        var accBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_accBtn, Dock.Left);
        accBar.Children.Add(_accBtn);
        accBar.Children.Add(_accStatus);
        _admins.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        var accounts = new DockPanel();
        DockPanel.SetDock(accBar, Dock.Top);
        var adminsCard = Ui.Card(_admins, 10);
        adminsCard.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(adminsCard, Dock.Top);
        accounts.Children.Add(accBar);
        accounts.Children.Add(adminsCard);
        accounts.Children.Add(_accGrid);

        _tabs.Items.Add(new TabItem { Header = "Usage by user", Content = usage });
        var accTab = new TabItem { Header = "Local accounts", Content = accounts };
        _tabs.Items.Add(accTab);
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, _tabs)) return;   // ignore selection changes bubbling up from the grids
            if (ReferenceEquals(_tabs.SelectedItem, accTab) && !_accLoaded && !_accBusy) Ui.Run(LoadAccounts, "Reading accounts");
        };
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);

        _admins.Text = "Administrators: not read yet.";
        _accStatus.Text = "Accounts are read when this tab opens.";
        Content = root;
    }

    static DataGridTextColumn UsersCol(string header, string path, string sortPath, double width, bool numeric)
    {
        var c = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay, TargetNullValue = Fmt.Na },
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
        var latest = Sampler.Latest;
        if (latest != null) OnTick(latest);
    }

    public void OnHidden() => Sampler.Tick -= OnTick;

    // ------------------------------------------------------------------ usage

    sealed class UsersAgg
    {
        public string User = "";
        public int Session;
        public int Count;
        public double Cpu, Mem, Disk, Net;
        public bool NetKnown;
        public List<ProcSample> Procs = new();
    }

    void OnTick(Snapshot s)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // the previous tick is still being grouped
        Ui.Run(async () =>
        {
            try
            {
                var groups = await Task.Run(() => Aggregate(s));
                Apply(groups, s);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }, "Users");
    }

    /// <summary>Background thread: group processes by owner and session. Owner lookups are cached per process key.</summary>
    List<UsersAgg> Aggregate(Snapshot s)
    {
        var map = new Dictionary<string, UsersAgg>();
        int self = Environment.ProcessId;
        bool showSelf = Settings.Current.ShowSelf;
        if (_userByKey.Count > 5000) _userByKey.Clear();
        foreach (var p in s.Processes)
        {
            if (p == null) continue;
            if (!showSelf && p.Pid == self) continue;
            string key = p.Key;
            if (!_userByKey.TryGetValue(key, out var user))
            {
                user = "";
                try { user = ProcessInfo.Get(p).User ?? ""; } catch { }
                _userByKey[key] = user;
            }
            string group = user + "|" + p.SessionId;
            if (!map.TryGetValue(group, out var a)) map[group] = a = new UsersAgg { User = user, Session = p.SessionId };
            a.Count++;
            a.Cpu += p.Cpu;
            a.Mem += p.PrivateWorkingSet;
            a.Disk += p.ReadBps + p.WriteBps;
            if (p.NetBps.HasValue) { a.Net += p.NetBps.Value; a.NetKnown = true; }
            a.Procs.Add(p);
        }
        return map.Values.ToList();
    }

    void Apply(List<UsersAgg> groups, Snapshot s)
    {
        uint console = UsersNative.ConsoleSession();
        var seen = new HashSet<string>();
        foreach (var a in groups)
        {
            string key = a.User + "|" + a.Session;
            seen.Add(key);
            if (!_byKey.TryGetValue(key, out var row))
            {
                row = new UsersRow(a.User, a.Session, console);
                _byKey[key] = row;
                _rows.Add(row);
            }
            row.Update(a.Count, a.Cpu, a.Mem, a.Disk, a.NetKnown && s.PerProcessNetAvailable ? a.Net : (double?)null, TopText(a.Procs, s.PerProcessNetAvailable));
        }
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            string key = _rows[i].GroupKey;
            if (seen.Contains(key)) continue;
            _byKey.Remove(key);
            _rows.RemoveAt(i);
        }

        int sessions = groups.Select(g => g.Session).Distinct().Count();
        int users = groups.Where(g => g.User.Length > 0).Select(g => g.User).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string me = Environment.UserName;
        int mySession = -1;
        try { mySession = System.Diagnostics.Process.GetCurrentProcess().SessionId; } catch { }
        _summary.Text = $"{users} user account{(users == 1 ? "" : "s")} across {sessions} session{(sessions == 1 ? "" : "s")} · " +
                        $"{s.Processes.Count} processes · you are {me} in session {(mySession >= 0 ? mySession.ToString() : Fmt.Na)}" +
                        (console != 0xFFFFFFFF ? $" · console session {console}" : "") +
                        (s.PerProcessNetAvailable ? "" : " · per-process network unavailable (ETW not running)");
    }

    static string TopText(List<ProcSample> procs, bool netKnown)
    {
        var sb = new StringBuilder();
        sb.Append("Busiest processes".PadRight(34)).Append("PID".PadLeft(8)).Append("CPU".PadLeft(9))
          .Append("Memory".PadLeft(11)).Append("Disk".PadLeft(13));
        if (netKnown) sb.Append("Network".PadLeft(13));
        foreach (var p in procs.OrderByDescending(q => q.Cpu).ThenByDescending(q => q.PrivateWorkingSet).Take(10))
        {
            sb.AppendLine();
            string name = p.Name ?? "";
            if (name.Length > 33) name = name.Substring(0, 32) + "…";
            sb.Append(name.PadRight(34))
              .Append(p.Pid.ToString().PadLeft(8))
              .Append(Fmt.Pct(p.Cpu, 1).PadLeft(9))
              .Append(Fmt.Bytes(p.PrivateWorkingSet).PadLeft(11))
              .Append(Fmt.Rate(p.ReadBps + p.WriteBps).PadLeft(13));
            if (netKnown) sb.Append(Fmt.Rate(p.NetBps).PadLeft(13));
        }
        if (procs.Count > 10) sb.AppendLine().Append($"… and {procs.Count - 10} more");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ accounts

    sealed class UsersAccountsResult
    {
        public List<UsersAccountRow> Accounts = new();
        public List<string> Admins = new();
        public string AdminGroup = "Administrators";
        public bool AccountsOk, AdminsOk;
    }

    async Task LoadAccounts()
    {
        if (_accBusy) return;
        _accBusy = true;
        _accBtn.IsEnabled = false;
        _accStatus.Text = "Reading local accounts…";
        try
        {
            var r = await Task.Run(() => ReadAccounts());
            var view = new ListCollectionView(r.Accounts);
            view.SortDescriptions.Add(new SortDescription(nameof(UsersAccountRow.Name), ListSortDirection.Ascending));
            _accGrid.ItemsSource = view;
            if (!r.AdminsOk) _admins.Text = $"{r.AdminGroup}: {Fmt.Na} (could not read the group's members)";
            else if (r.Admins.Count == 0) _admins.Text = $"{r.AdminGroup}: no members found";
            else _admins.Text = $"Members of {r.AdminGroup} ({r.Admins.Count}): " + string.Join(", ", r.Admins);
            _accStatus.Text = r.AccountsOk
                ? $"{r.Accounts.Count} local account{(r.Accounts.Count == 1 ? "" : "s")} · read at {DateTime.Now:HH:mm:ss}"
                : "Could not read the local accounts (WMI Win32_UserAccount failed).";
            _accLoaded = true;
        }
        finally
        {
            _accBusy = false;
            _accBtn.IsEnabled = true;
        }
    }

    static readonly Regex PartRx = new("(?<cls>Win32_\\w+)\\.Domain=\"(?<d>[^\"]*)\",Name=\"(?<n>[^\"]*)\"", RegexOptions.Compiled);

    static UsersAccountsResult ReadAccounts()
    {
        var res = new UsersAccountsResult();

        // The local Administrators group by its well-known SID, so the name can be in any language.
        string gName = null, gDomain = null;
        SysInfoWmi.Query(null, "SELECT Name, Domain FROM Win32_Group WHERE LocalAccount = TRUE AND SID = 'S-1-5-32-544'", mo =>
        {
            gName = SysInfoWmi.S(mo, "Name");
            gDomain = SysInfoWmi.S(mo, "Domain");
        });

        var adminKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // "DOMAIN\name"
        if (!string.IsNullOrEmpty(gName))
        {
            res.AdminGroup = gName;
            string esc(string v) => (v ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
            string wql = "SELECT PartComponent FROM Win32_GroupUser WHERE GroupComponent = \"Win32_Group.Domain='" +
                         esc(gDomain) + "',Name='" + esc(gName) + "'\"";
            res.AdminsOk = SysInfoWmi.Query(null, wql, mo =>
            {
                var m = PartRx.Match(SysInfoWmi.S(mo, "PartComponent"));
                if (!m.Success) return;
                string kind = m.Groups["cls"].Value switch
                {
                    "Win32_Group" => " (group)",
                    "Win32_SystemAccount" => " (system)",
                    _ => ""
                };
                string d = m.Groups["d"].Value, n = m.Groups["n"].Value;
                adminKeys.Add(d + "\\" + n);
                res.Admins.Add(d + "\\" + n + kind);
            });

            // Fallback: walk the association instead of filtering the link class.
            if (res.Admins.Count == 0)
            {
                string assoc = "ASSOCIATORS OF {Win32_Group.Domain='" + esc(gDomain) + "',Name='" + esc(gName) +
                               "'} WHERE AssocClass = Win32_GroupUser Role = GroupComponent";
                res.AdminsOk |= SysInfoWmi.Query(null, assoc, mo =>
                {
                    string d = SysInfoWmi.S(mo, "Domain"), n = SysInfoWmi.S(mo, "Name");
                    if (n.Length == 0) return;
                    adminKeys.Add(d + "\\" + n);
                    res.Admins.Add(d + "\\" + n);
                });
            }
        }

        res.AccountsOk = SysInfoWmi.Query(null, "SELECT * FROM Win32_UserAccount WHERE LocalAccount = TRUE", mo =>
        {
            string name = SysInfoWmi.S(mo, "Name");
            string domain = SysInfoWmi.S(mo, "Domain");
            string sid = SysInfoWmi.S(mo, "SID");
            bool? disabled = SysInfoWmi.B(mo, "Disabled");
            bool? locked = SysInfoWmi.B(mo, "Lockout");
            bool? pwReq = SysInfoWmi.B(mo, "PasswordRequired");
            bool admin = adminKeys.Contains(domain + "\\" + name);

            string note = "";
            string kind = null;
            if (sid.EndsWith("-500") && disabled == false) { note = "Built-in Administrator account is enabled"; kind = "Warn"; }
            else if (sid.EndsWith("-501") && disabled == false) { note = "Guest account is enabled"; kind = "Warn"; }
            else if (sid.EndsWith("-500")) note = "Built-in Administrator";
            else if (sid.EndsWith("-501")) note = "Built-in Guest";
            else if (sid.EndsWith("-503")) note = "Used by Windows (DefaultAccount)";
            else if (sid.EndsWith("-504")) note = "Used by Windows Defender Application Guard";
            if (locked == true) { note = note.Length > 0 ? note + "; locked out" : "Locked out"; kind = "Bad"; }
            if (disabled == false && pwReq == false && admin) { note = note.Length > 0 ? note + "; no password required" : "Administrator with no password required"; kind = "Bad"; }

            res.Accounts.Add(new UsersAccountRow
            {
                Name = name,
                FullName = SysInfoWmi.S(mo, "FullName"),
                Admin = adminKeys.Count == 0 && !res.AdminsOk ? Fmt.Na : admin ? "Yes" : "",
                Disabled = YesNo(disabled),
                LockedOut = YesNo(locked),
                PasswordRequired = YesNo(pwReq),
                Sid = sid,
                Note = note,
                Kind = kind,
            });
        });
        return res;
    }

    static string YesNo(bool? b) => b == true ? "Yes" : b == false ? "No" : Fmt.Na;
}

/// <summary>One (user, session) group, updated in place every tick.</summary>
public sealed class UsersRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    public string GroupKey { get; }
    public string User { get; }
    public int Session { get; }
    public string SessionText { get; }

    int _processes;
    double _cpu, _mem, _disk, _net;
    string _cpuText = "", _memText = "", _diskText = "", _netText = "", _topText = "";

    public int Processes { get => _processes; private set => Set(ref _processes, value, nameof(Processes)); }
    public double Cpu { get => _cpu; private set => Set(ref _cpu, value, nameof(Cpu)); }
    public double Mem { get => _mem; private set => Set(ref _mem, value, nameof(Mem)); }
    public double Disk { get => _disk; private set => Set(ref _disk, value, nameof(Disk)); }
    public double Net { get => _net; private set => Set(ref _net, value, nameof(Net)); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value, nameof(CpuText)); }
    public string MemText { get => _memText; private set => Set(ref _memText, value, nameof(MemText)); }
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value, nameof(DiskText)); }
    public string NetText { get => _netText; private set => Set(ref _netText, value, nameof(NetText)); }
    public string TopText { get => _topText; private set => Set(ref _topText, value, nameof(TopText)); }

    public UsersRow(string user, int session, uint consoleSession)
    {
        GroupKey = (user ?? "") + "|" + session;
        User = string.IsNullOrEmpty(user) ? Fmt.Na : user;
        Session = session;
        SessionText = session == 0 ? "0 (services)" : (uint)session == consoleSession ? session + " (console)" : session.ToString();
    }

    public void Update(int count, double cpu, double mem, double disk, double? net, string top)
    {
        Processes = count;
        Cpu = cpu;
        CpuText = Fmt.Pct(Math.Min(100, cpu), 1);
        Mem = mem;
        MemText = Fmt.Bytes(mem);
        Disk = disk;
        DiskText = Fmt.Rate(disk);
        Net = net ?? -1;
        NetText = Fmt.Rate(net);
        TopText = top;
    }

    public string ToText() => $"{User}\t{SessionText}\t{Processes}\t{CpuText}\t{MemText}\t{DiskText}\t{NetText}";

    void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A local account (read once; not live).</summary>
public sealed class UsersAccountRow
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Admin { get; set; } = "";
    public string Disabled { get; set; } = "";
    public string LockedOut { get; set; } = "";
    public string PasswordRequired { get; set; } = "";
    public string Sid { get; set; } = "";
    public string Note { get; set; } = "";
    public string Kind { get; set; }

    public string ToText() => $"{Name}\t{FullName}\tadmin: {(Admin.Length > 0 ? Admin : "No")}\tdisabled: {Disabled}\tlocked out: {LockedOut}\tpassword required: {PasswordRequired}\t{Sid}\t{Note}";
}

internal static class UsersNative
{
    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    /// <summary>The session attached to the physical console, or 0xFFFFFFFF if none.</summary>
    public static uint ConsoleSession()
    {
        try { return WTSGetActiveConsoleSessionId(); }
        catch { return 0xFFFFFFFF; }
    }
}
