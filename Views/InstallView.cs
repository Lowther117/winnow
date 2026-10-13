using System.Text;
using System.Windows;
using System.Windows.Controls;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Ninite-style installer: tick apps from the catalogue and Winnow installs them
/// one after another with winget, silently, streaming winget's output.
/// </summary>
public sealed class InstallView : UserControl, IView
{
    const string AppInstallerStore = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

    readonly StackPanel _sections = new();
    readonly List<InstallTile> _tiles = new();
    readonly List<InstallSection> _sectionUis = new();
    readonly TextBox _search;
    readonly TextBlock _summary;
    readonly TextBlock _origin;
    readonly CheckBox _machineScope;
    readonly Button _installBtn, _upgradeSelBtn, _upgradeAllBtn, _cancelBtn, _recheckBtn, _reloadBtn, _customiseBtn;
    readonly Border _wingetMissing;
    readonly InstallLog _log;

    CatalogueFile _catalogue = new();
    HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    bool _checked, _checking, _running;
    CancellationTokenSource _cts;

    public InstallView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // winget missing
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // toolbar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // summary
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // tiles
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // log

        root.Children.Add(Ui.Header("Install",
            "Tick the apps you want and Winnow installs them with winget, silently, one after another. Apps already on this PC are marked Installed."));

        // ---- winget missing
        var missing = new StackPanel();
        var mt = Ui.Text("winget (App Installer) is not installed, so Winnow cannot install apps. Get App Installer from the Microsoft Store, then press Check again.", "Sub");
        mt.TextWrapping = TextWrapping.Wrap;
        missing.Children.Add(mt);
        var mb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        mb.Children.Add(Ui.Button("Get App Installer", (_, _) => Shell.Open(AppInstallerStore), "Accent", "Opens the Microsoft Store page for App Installer"));
        mb.Children.Add(Ui.Button("Check again", (_, _) => { _checked = false; Ui.Run(CheckInstalledAsync, "Checking installed apps"); }));
        missing.Children.Add(mb);
        _wingetMissing = new Border { Child = missing, Style = (Style)Application.Current.FindResource("HintBox"), Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        Grid.SetRow(_wingetMissing, 1);
        root.Children.Add(_wingetMissing);

        // ---- toolbar
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var sl = Ui.Text("Search", "Sub");
        sl.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(sl);
        _search = new TextBox { Width = 200, Margin = new Thickness(0, 0, 12, 4), ToolTip = "Filter by name, description, id or tag" };
        _search.TextChanged += (_, _) => ApplyFilter();
        bar.Children.Add(_search);
        _installBtn = Tool(bar, Ui.Button("Install selected", (_, _) => Ui.Run(() => RunBatchAsync(false), "Install"), "Accent"));
        _upgradeSelBtn = Tool(bar, Ui.Button("Upgrade selected", (_, _) => Ui.Run(() => RunBatchAsync(true), "Upgrade"), null,
            "Upgrade the ticked apps that are already installed"));
        _upgradeAllBtn = Tool(bar, Ui.Button("Upgrade all", (_, _) => Ui.Run(UpgradeAllAsync, "Upgrade all"), null,
            "Upgrade every app on this PC that winget knows a newer version of"));
        _cancelBtn = Tool(bar, Ui.Button("Cancel", (_, _) => _cts?.Cancel(), "Danger", "Stop the current winget and skip the rest"));
        _cancelBtn.IsEnabled = false;
        Tool(bar, Ui.Button("Clear ticks", (_, _) => { foreach (var t in _tiles) t.Box.IsChecked = false; }, "Ghost"));
        _machineScope = new CheckBox { Content = "Install for all users", Margin = new Thickness(4, 0, 14, 4),
            ToolTip = "Adds --scope machine. Some apps only offer a per-user installer and will fail with this ticked." };
        bar.Children.Add(_machineScope);
        _recheckBtn = Tool(bar, Ui.Button("Re-check installed", (_, _) => { _checked = false; Ui.Run(CheckInstalledAsync, "Checking installed apps"); }, "Ghost"));
        _customiseBtn = Tool(bar, Ui.Button("Customise list…", (_, _) => CustomiseList(), "Ghost",
            "Save the list as winnow-catalogue.json beside Winnow.exe and open it for editing"));
        _reloadBtn = Tool(bar, Ui.Button("Reload list", (_, _) => LoadCatalogue(), "Ghost", "Read the list again after editing it"));
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        // ---- summary
        var sum = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        _origin = Ui.Text("", "Sub");
        _origin.Margin = new Thickness(12, 0, 0, 0);
        _origin.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(_origin, Dock.Right);
        sum.Children.Add(_origin);
        _summary = Ui.Text("", "Sub");
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        sum.Children.Add(_summary);
        Grid.SetRow(sum, 3);
        root.Children.Add(sum);

        // ---- tiles
        var scroll = new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 4);
        root.Children.Add(scroll);

        // ---- log
        var foot = new StackPanel();
        _log = new InstallLog(130);
        foot.Children.Add(_log.View);
        var note = Ui.Text("winget runs with Winnow's administrator rights. A few installers refuse to run elevated - Winnow says so, and those can be " +
                           "installed from a normal Command Prompt with winget install --id <id>.", "Sub");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 4, 0, 0);
        foot.Children.Add(note);
        Grid.SetRow(foot, 5);
        root.Children.Add(foot);

        Content = root;
        LoadCatalogue();
    }

    static Button Tool(WrapPanel bar, Button b)
    {
        b.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(b);
        return b;
    }

    // =================================================================== IView

    public void OnShown()
    {
        if (!_checked && !_checking && !_running) Ui.Run(CheckInstalledAsync, "Checking installed apps");
    }

    public void OnHidden() { }

    // =================================================================== catalogue

    void LoadCatalogue()
    {
        if (_running) { Status.Set("Wait for the current installs to finish.", StatusKind.Warn); return; }
        var ticked = new HashSet<string>(_tiles.Where(t => t.Box.IsChecked == true).Select(t => t.App.Id), StringComparer.OrdinalIgnoreCase);
        var (file, origin, warning) = Catalogue.Load();
        _catalogue = file;
        _origin.Text = origin;
        _origin.ToolTip = origin;
        if (warning.Length > 0) Status.Set(warning, StatusKind.Warn);

        _tiles.Clear();
        _sectionUis.Clear();
        _sections.Children.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in _catalogue.Categories)
        {
            var apps = cat.Apps.Where(a => seen.Add(a.Id)).ToList();   // an id listed twice gets one tile
            if (apps.Count == 0) continue;
            var section = new InstallSection(cat.Name);
            section.SelectAll.Click += (_, _) => ToggleSection(section);
            foreach (var app in apps)
            {
                var tile = new InstallTile(app, cat.Name);
                tile.Box.IsChecked = ticked.Contains(app.Id);
                tile.Box.Checked += (_, _) => { tile.RefreshBorder(); UpdateCounts(); };
                tile.Box.Unchecked += (_, _) => { tile.RefreshBorder(); UpdateCounts(); };
                tile.RefreshBorder();
                tile.SetInstalled(_installed.Contains(app.Id));
                section.Tiles.Add(tile);
                section.Wrap.Children.Add(tile.Root);
                _tiles.Add(tile);
            }
            _sectionUis.Add(section);
            _sections.Children.Add(section.Root);
        }
        if (_tiles.Count == 0)
        {
            var none = Ui.Text("The app list is empty. Use Customise list… to add apps, or delete winnow-catalogue.json beside Winnow.exe to get the built-in list back.", "Sub");
            none.TextWrapping = TextWrapping.Wrap;
            _sections.Children.Add(none);
        }
        ApplyFilter();
    }

    void CustomiseList()
    {
        var (ok, message) = Catalogue.WriteOverride();
        if (!ok)
        {
            Ui.Error("Could not create " + Paths.CatalogueOverride + ":\n\n" + message +
                     "\n\nIf Winnow.exe is in a protected folder, move it somewhere you can write to.");
            return;
        }
        Shell.Open(Paths.CatalogueOverride);
        Status.Set("Edit winnow-catalogue.json, save it, then press Reload list. Each app needs a winget id (search winget.run or winstall.app).", StatusKind.Info);
        if (message == "created") LoadCatalogue();
    }

    void ToggleSection(InstallSection section)
    {
        var visible = section.Tiles.Where(t => t.Root.Visibility == Visibility.Visible).ToList();
        bool all = visible.Count > 0 && visible.All(t => t.Box.IsChecked == true);
        foreach (var t in visible) t.Box.IsChecked = !all;
        UpdateCounts();
    }

    void ApplyFilter()
    {
        string f = _search?.Text?.Trim() ?? "";
        foreach (var s in _sectionUis)
        {
            int shown = 0;
            foreach (var t in s.Tiles)
            {
                bool show = f.Length == 0 || t.Matches(f);
                t.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show) shown++;
            }
            s.Root.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateCounts();
    }

    void UpdateCounts()
    {
        foreach (var s in _sectionUis)
        {
            int ticked = s.Tiles.Count(t => t.Box.IsChecked == true);
            int inst = s.Tiles.Count(t => _installed.Contains(t.App.Id));
            s.Count.Text = s.Tiles.Count + " apps · " + ticked + " ticked" + (_checked ? " · " + inst + " installed" : "");
            var visible = s.Tiles.Where(t => t.Root.Visibility == Visibility.Visible).ToList();
            s.SelectAll.Content = visible.Count > 0 && visible.All(t => t.Box.IsChecked == true) ? "Clear" : "Select all";
        }
        if (_checking || _summary == null) return;
        int total = _tiles.Count, sel = _tiles.Count(t => t.Box.IsChecked == true);
        int installed = _tiles.Count(t => _installed.Contains(t.App.Id));
        _summary.Text = sel + " selected of " + total + " apps" + (_checked ? " · " + installed + " already installed" : "");
        if (_installBtn != null && !_running) _installBtn.Content = sel > 0 ? "Install selected (" + sel + ")" : "Install selected";
    }

    // =================================================================== installed check

    async Task CheckInstalledAsync()
    {
        if (_checking || _running) return;
        string winget = Shell.WingetPath();
        _wingetMissing.Visibility = winget == null ? Visibility.Visible : Visibility.Collapsed;
        SetButtons();
        if (winget == null)
        {
            _summary.Text = "winget is not installed.";
            return;
        }
        _checking = true;
        _recheckBtn.IsEnabled = false;
        _summary.Text = "Checking what's installed…";
        try
        {
            var r = await Task.Run(() => Shell.RunAsync(winget, "list --accept-source-agreements --disable-interactivity", 180_000));
            var apps = _tiles.Select(t => t.App).ToList();
            var found = await Task.Run(() => Catalogue.InstalledFromWingetList(r.Output, apps));
            if (r.ExitCode != 0 && found.Count == 0)
            {
                string first = (r.Output ?? "").Split('\n').Select(l => Catalogue.CleanLine(l)).FirstOrDefault(l => l != null) ?? "no output";
                Status.Set("Could not check installed apps (winget list: " + first.Trim() + ")", StatusKind.Warn);
            }
            _installed = found;
            foreach (var t in _tiles) t.SetInstalled(_installed.Contains(t.App.Id));
            _checked = true;
        }
        finally
        {
            _checking = false;
            _recheckBtn.IsEnabled = !_running;
            UpdateCounts();
        }
    }

    // =================================================================== installing

    void SetButtons()
    {
        bool have = _wingetMissing.Visibility != Visibility.Visible;
        _installBtn.IsEnabled = have && !_running;
        _upgradeSelBtn.IsEnabled = have && !_running;
        _upgradeAllBtn.IsEnabled = have && !_running;
        _recheckBtn.IsEnabled = have && !_running && !_checking;
        _reloadBtn.IsEnabled = !_running;
        _customiseBtn.IsEnabled = !_running;
        _cancelBtn.IsEnabled = _running;
    }

    string ArgsFor(CatalogueApp a, bool upgrade)
    {
        string src = a.SourceOrDefault;
        var sb = new StringBuilder(upgrade ? "upgrade" : "install");
        sb.Append(" --id ").Append(Shell.Quote(a.Id)).Append(" -e");
        sb.Append(" --source ").Append(Shell.Quote(src));
        sb.Append(" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
        if (!upgrade && _machineScope.IsChecked == true && src.Equals("winget", StringComparison.OrdinalIgnoreCase))
            sb.Append(" --scope machine");
        return sb.ToString();
    }

    async Task RunBatchAsync(bool upgrade)
    {
        if (_running || _checking) { Status.Set("winget is busy - wait for it to finish.", StatusKind.Warn); return; }
        string winget = Shell.WingetPath();
        if (winget == null) { _wingetMissing.Visibility = Visibility.Visible; SetButtons(); return; }
        var todo = _tiles.Where(t => t.Box.IsChecked == true).ToList();
        if (upgrade && _checked) todo = todo.Where(t => _installed.Contains(t.App.Id)).ToList();
        if (todo.Count == 0)
        {
            Status.Set(upgrade ? "Tick one or more installed apps to upgrade." : "Tick one or more apps to install.", StatusKind.Warn);
            return;
        }
        if (!upgrade)
        {
            var already = todo.Where(t => _installed.Contains(t.App.Id)).Select(t => t.App.Name).ToList();
            string msg = "Install " + todo.Count + (todo.Count == 1 ? " app" : " apps") + " with winget?\n\n" +
                         string.Join("\n", todo.Take(25).Select(t => "• " + t.App.Name)) +
                         (todo.Count > 25 ? "\n… and " + (todo.Count - 25) + " more" : "") +
                         (already.Count > 0 ? "\n\nAlready installed (winget will check for a newer version): " + string.Join(", ", already) : "") +
                         "\n\nInstallers run silently and accept the apps' licence agreements.";
            if (!Ui.Confirm(msg, "Install apps", "Install")) return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetButtons();
        var results = new List<(string name, CatalogueOutcome outcome)>();
        try
        {
            _log.Append("---- " + DateTime.Now.ToString("HH:mm:ss") + (upgrade ? "  upgrading " : "  installing ") + todo.Count + " app(s)");
            for (int i = 0; i < todo.Count; i++)
            {
                var tile = todo[i];
                var app = tile.App;
                _log.Append("");
                _log.Append("==> [" + (i + 1) + "/" + todo.Count + "] " + (upgrade ? "Upgrading " : "Installing ") + app.Name + "  (" + app.Id + ")");
                Status.Set((upgrade ? "Upgrading " : "Installing ") + app.Name + " (" + (i + 1) + " of " + todo.Count + ")…");
                tile.SetBusy(upgrade ? "Upgrading…" : "Installing…");
                string last = "";
                string args = ArgsFor(app, upgrade);
                var r = await Task.Run(() => Shell.RunAsync(winget, args, 3_600_000, line =>
                {
                    string c = Catalogue.CleanLine(line);
                    if (c == null) return;
                    last = c.Trim();
                    _log.Append("    " + c.Trim());
                }, ct));
                bool cancelled = ct.IsCancellationRequested;
                var o = Catalogue.Describe(r, cancelled, upgrade, last);
                _log.Append("    -> " + o.Text);
                results.Add((app.Name, o));
                if (o.Ok)
                {
                    _installed.Add(app.Id);
                    tile.Box.IsChecked = false;
                    if (!upgrade) Change.Note("install.winget", "Installed " + app.Name + " with winget", app.Id);
                    else Change.Note("install.winget", "Upgraded " + app.Name + " with winget", app.Id);
                }
                tile.SetResult(o.Ok, o.Text, _installed.Contains(app.Id));
                if (cancelled) break;
            }
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
            SetButtons();
            UpdateCounts();
        }

        int ok = results.Count(x => x.outcome.Ok);
        var failed = results.Where(x => !x.outcome.Ok && !x.outcome.Cancelled).ToList();
        bool wasCancelled = results.Any(x => x.outcome.Cancelled) || results.Count < todo.Count;
        _log.Append("");
        _log.Append("==> Summary: " + ok + " of " + todo.Count + " done" + (failed.Count > 0 ? ", " + failed.Count + " failed" : "") +
                    (wasCancelled ? ", cancelled" : ""));
        foreach (var f in failed) _log.Append("    " + f.name + ": " + f.outcome.Text);
        Status.Set((upgrade ? "Upgrades" : "Installs") + " finished: " + ok + " of " + todo.Count + " done" +
                   (failed.Count > 0 ? ", " + failed.Count + " failed" : "") + (wasCancelled ? " (cancelled)" : "") + ".",
                   failed.Count > 0 || wasCancelled ? StatusKind.Warn : StatusKind.Good);
        var report = new StringBuilder();
        report.Append(ok).Append(" of ").Append(todo.Count).Append(upgrade ? " upgraded or already up to date." : " installed.");
        if (wasCancelled) report.Append("\n\nCancelled before the end.");
        if (failed.Count > 0)
        {
            report.Append("\n\nNot done:");
            foreach (var f in failed.Take(15)) report.Append("\n• ").Append(f.name).Append(" - ").Append(f.outcome.Text);
            if (failed.Count > 15) report.Append("\n… see the log for the rest");
        }
        Ui.Info(report.ToString(), upgrade ? "Upgrades finished" : "Installs finished");
    }

    async Task UpgradeAllAsync()
    {
        if (_running || _checking) { Status.Set("winget is busy - wait for it to finish.", StatusKind.Warn); return; }
        string winget = Shell.WingetPath();
        if (winget == null) { _wingetMissing.Visibility = Visibility.Visible; SetButtons(); return; }
        if (!Ui.Confirm("Upgrade every app on this PC that winget has a newer version of?\n\n" +
                        "This covers all apps winget recognises, not just the ones on this list. Installers run silently; " +
                        "apps that are open may need closing first.", "Upgrade all", "Upgrade all")) return;

        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetButtons();
        CatalogueOutcome o;
        try
        {
            _log.Append("---- " + DateTime.Now.ToString("HH:mm:ss") + "  winget upgrade --all");
            Status.Set("Upgrading all apps with winget…");
            string last = "";
            var r = await Task.Run(() => Shell.RunAsync(winget,
                "upgrade --all --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                3 * 3_600_000, line =>
                {
                    string c = Catalogue.CleanLine(line);
                    if (c == null) return;
                    last = c.Trim();
                    _log.Append("    " + c.Trim());
                }, ct));
            o = Catalogue.Describe(r, ct.IsCancellationRequested, true, last);
            _log.Append("==> " + (o.Ok && r.ExitCode == 0 ? "Upgrade all finished" : o.Text));
            if (o.Ok) Change.Note("install.winget", "Upgraded all apps with winget", "winget upgrade --all");
        }
        finally
        {
            _running = false;
            _cts.Dispose();
            _cts = null;
            SetButtons();
        }
        Status.Set(o.Ok ? "Upgrade all finished." : "Upgrade all: " + o.Text, o.Ok ? StatusKind.Good : StatusKind.Warn);
        _checked = false;
        await CheckInstalledAsync();
    }
}

/// <summary>One category on the Install page.</summary>
internal sealed class InstallSection
{
    public readonly List<InstallTile> Tiles = new();
    public readonly StackPanel Root = new() { Margin = new Thickness(0, 0, 0, 10) };
    public readonly WrapPanel Wrap = new() { Orientation = Orientation.Horizontal };
    public readonly TextBlock Count;
    public readonly Button SelectAll;

    public InstallSection(string name)
    {
        var head = new DockPanel { Margin = new Thickness(0, 4, 0, 6), LastChildFill = true };
        SelectAll = new Button { Content = "Select all", Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
                                 ToolTip = "Tick or untick every app shown in this category" };
        SelectAll.Style = (Style)Application.Current.FindResource("Ghost");
        DockPanel.SetDock(SelectAll, Dock.Right);
        head.Children.Add(SelectAll);
        var title = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        title.Style = (Style)Application.Current.FindResource("H2");
        title.Margin = new Thickness(0);
        head.Children.Add(title);
        Count = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Count.Style = (Style)Application.Current.FindResource("Sub");
        head.Children.Add(Count);
        // DockPanel fills with the last child; keep the title and count left-aligned together
        DockPanel.SetDock(title, Dock.Left);
        Root.Children.Add(head);
        Root.Children.Add(Wrap);
    }
}

/// <summary>One tick-box tile on the Install page.</summary>
internal sealed class InstallTile
{
    public readonly CatalogueApp App;
    public readonly string Category;
    public readonly CheckBox Box;
    public readonly Border Root;
    readonly Border _badge;
    readonly TextBlock _badgeText;

    public InstallTile(CatalogueApp app, string category)
    {
        App = app;
        Category = category;

        var nameRow = new DockPanel { LastChildFill = true };
        _badgeText = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold };
        _badge = new Border { Child = _badgeText, Style = (Style)Application.Current.FindResource("Chip"), Margin = new Thickness(6, 0, 0, 0),
                              Visibility = Visibility.Collapsed };
        DockPanel.SetDock(_badge, Dock.Right);
        nameRow.Children.Add(_badge);
        var name = new TextBlock { Text = app.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                                   VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        nameRow.Children.Add(name);

        var desc = new TextBlock { Text = app.Description, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis,
                                   Margin = new Thickness(0, 2, 0, 0) };
        desc.Style = (Style)Application.Current.FindResource("Sub");
        desc.FontSize = 11.5;

        var text = new StackPanel();
        text.Children.Add(nameRow);
        text.Children.Add(desc);

        Box = new CheckBox { Content = text, VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0),
                             HorizontalContentAlignment = HorizontalAlignment.Stretch };

        string tags = app.Tags != null && app.Tags.Count > 0 ? "\nTags: " + string.Join(", ", app.Tags) : "";
        Root = new Border
        {
            Child = Box, Width = 232, MinHeight = 64, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            ToolTip = app.Name + "\nwinget id: " + app.Id + (app.SourceOrDefault != "winget" ? " (source: " + app.SourceOrDefault + ")" : "") + tags,
        };
        Root.SetResourceReference(Border.BackgroundProperty, "Panel");
        RefreshBorder();
    }

    public bool Matches(string f) =>
        Has(App.Name, f) || Has(App.Description, f) || Has(App.Id, f) || Has(Category, f) ||
        (App.Tags != null && App.Tags.Any(t => Has(t, f)));

    static bool Has(string s, string f) => !string.IsNullOrEmpty(s) && s.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

    public void RefreshBorder() => Root.SetResourceReference(Border.BorderBrushProperty, Box.IsChecked == true ? "Accent" : "Border");

    void Badge(string text, string colourKey)
    {
        if (string.IsNullOrEmpty(text)) { _badge.Visibility = Visibility.Collapsed; return; }
        _badgeText.Text = text;
        _badgeText.SetResourceReference(TextBlock.ForegroundProperty, colourKey);
        _badge.Visibility = Visibility.Visible;
    }

    public void SetInstalled(bool installed)
    {
        Badge(installed ? "Installed" : null, "Good");
    }

    public void SetBusy(string text) => Badge(text, "Accent");

    public void SetResult(bool ok, string text, bool installed)
    {
        if (ok) Badge("Installed", "Good");
        else
        {
            Badge(text == "Cancelled" ? "Cancelled" : "Failed", text == "Cancelled" ? "Warn" : "Bad");
            _badge.ToolTip = text;
        }
    }
}

/// <summary>Read-only log panel for winget output. Append is safe from any thread.</summary>
internal sealed class InstallLog
{
    readonly StringBuilder _sb = new();
    readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(8, 6, 8, 6) };
    readonly ScrollViewer _scroll;
    public readonly Border View;

    public InstallLog(double height)
    {
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _text.Text = "winget output appears here.";
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
        if (_sb.Length > 200_000) _sb.Remove(0, _sb.Length - 160_000);
        _text.Text = _sb.ToString();
        _scroll.ScrollToEnd();
    }
}
