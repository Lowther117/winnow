using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Winnow.Controls;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Disk Space: every drive at a glance, a measured clean-up list, and a
/// WizTree-style folder analyser. Nothing is deleted without a confirmation
/// that says exactly what will go; clean-up deletions are permanent, the
/// analyser's deletions go to the Recycle Bin.
/// </summary>
public sealed class DiskSpaceView : UserControl, IView
{
    readonly TabControl _tabs = new();
    readonly TabItem _drivesTab, _cleanTab, _analyseTab;

    // ---- drives
    readonly WrapPanel _driveCards = new();

    // ---- clean up
    readonly List<CleanRowUi> _cleanRows = new();
    readonly StackPanel _cleanList = new();
    readonly TextBlock _cleanTotal = new() { FontWeight = FontWeights.SemiBold, FontSize = 13 };
    readonly TextBlock _cleanStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly ProgressBar _cleanBar = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = System.Windows.Visibility.Collapsed };
    readonly TextBox _cleanLog = new()
    {
        IsReadOnly = true, Height = 130, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 0), Visibility = System.Windows.Visibility.Collapsed, FontSize = 11.5,
    };
    readonly CheckBox _resetBase = new()
    {
        Content = new TextBlock
        {
            Text = "Also /ResetBase: frees more, but installed updates can no longer be uninstalled",
            TextWrapping = TextWrapping.Wrap,
        },
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
    };
    Button _measureBtn, _cleanBtn, _cleanCancelBtn, _saveLogBtn;
    CancellationTokenSource _measureCts, _cleanCts;
    bool _measuredOnce, _cleaning;
    int _measuring;                     // measurements / analyses in flight; Clean waits for them
    int _ticks;

    // ---- analyse
    readonly ComboBox _scanPick = new() { Width = 230, Margin = new Thickness(0, 0, 6, 0) };
    readonly TextBlock _scanStatus = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    readonly TextBlock _scanSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    readonly ProgressBar _scanBar = new() { IsIndeterminate = true, Margin = new Thickness(0, 6, 0, 0), Visibility = System.Windows.Visibility.Collapsed };
    readonly WrapPanel _crumbs = new() { VerticalAlignment = System.Windows.VerticalAlignment.Center };
    readonly DataGrid _grid = new();
    readonly DataGrid _bigGrid = new();
    Button _scanBtn, _scanCancelBtn, _upBtn;
    readonly DispatcherTimer _scanTicker;
    FolderScan _scan;
    FolderNode _node;
    FolderSizer _sizer;
    CancellationTokenSource _scanCts;

    public DiskSpaceView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Ui.Header("Disk Space",
            "Your drives, a measured clean-up list and what is using the space. Nothing is deleted until you confirm."));

        _drivesTab = new TabItem { Header = "Drives", Content = BuildDrives() };
        _cleanTab = new TabItem { Header = "Clean up", Content = BuildCleanup() };
        _analyseTab = new TabItem { Header = "Analyse", Content = BuildAnalyse() };
        _tabs.Items.Add(_drivesTab);
        _tabs.Items.Add(_cleanTab);
        _tabs.Items.Add(_analyseTab);
        _tabs.SelectedIndex = 0;
        _tabs.SelectionChanged += OnTabChanged;
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);
        Content = root;

        _scanTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _scanTicker.Tick += (_, _) => UpdateScanProgress();
    }

    // ================================================================== IView

    public void OnShown()
    {
        Sampler.Tick += OnTick;
        Ui.Run(RefreshDrives, "Reading drives");
        if (ReferenceEquals(_tabs.SelectedItem, _cleanTab) && !_measuredOnce && !_cleaning) Ui.Run(MeasureAll, "Measuring");
    }

    public void OnHidden()
    {
        Sampler.Tick -= OnTick;
    }

    /// <summary>Nav.Go("disk", "cleanup") opens the clean-up list; Nav.Go("disk", @"D:\Games") analyses that folder.</summary>
    public void Receive(object arg)
    {
        if (arg is not string s || s.Length == 0) return;
        if (s.Equals("cleanup", StringComparison.OrdinalIgnoreCase)) _tabs.SelectedItem = _cleanTab;
        else if (s.Equals("analyse", StringComparison.OrdinalIgnoreCase)) _tabs.SelectedItem = _analyseTab;
        else if (Directory.Exists(s)) AnalyseFrom(s);
    }

    void OnTick(Snapshot s)
    {
        // browsers opening or closing changes what can be cleaned
        if (++_ticks % 3 == 0 && ReferenceEquals(_tabs.SelectedItem, _cleanTab) && !_cleaning) UpdateRunningNotes();
    }

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _tabs)) return;   // ComboBox / DataGrid selection bubbling up
        if (ReferenceEquals(_tabs.SelectedItem, _cleanTab) && !_measuredOnce && !_cleaning) Ui.Run(MeasureAll, "Measuring");
    }

    void Post(Action a) => Dispatcher.BeginInvoke(a);

    // ================================================================== shared bits

    static Style S(string key) => (Style)Application.Current.FindResource(key);

    static TextBlock Wrap(string text, string style = "Sub")
    {
        var t = Ui.Text(text, style);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    static Border Chip(string text, string colorKey, string tip = null)
    {
        var tb = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        var b = new Border { Child = tb, Style = S("Chip"), Margin = new Thickness(8, 0, 0, 0) };
        if (tip != null) b.ToolTip = tip;
        return b;
    }

    static string Plural(long n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    static string Shorten(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, 14) + "…" + s.Substring(s.Length - (max - 15));
    }

    // ================================================================== drives

    sealed class DriveRow
    {
        public string Root = "", Label = "", Format = "", Type = "";
        public long Total = -1, Free = -1;
        public bool IsSystem;
        public string Display => (string.IsNullOrWhiteSpace(Label) ? (Type == "Fixed" ? "Local Disk" : "Removable Disk") : Label)
                                 + " (" + Root.TrimEnd('\\') + ")";
    }

    UIElement BuildDrives()
    {
        var sp = new StackPanel();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(Ui.Button("Refresh", (_, _) => Ui.Run(RefreshDrives, "Reading drives")));
        bar.Children.Add(Ui.Button("Storage settings", (_, _) => Shell.Open("ms-settings:storagesense"), null,
            "Settings › System › Storage"));
        bar.Children.Add(Ui.Button("Cleanup recommendations", (_, _) => Shell.Open("ms-settings:storagerecommendations"), null,
            "Windows' own list of large files, unused apps and files that are synced to the cloud"));
        sp.Children.Add(bar);
        sp.Children.Add(_driveCards);
        var note = Wrap("Fixed and removable drives that are ready. Used = total minus free space, as Windows reports it.");
        note.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(note);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static List<DriveRow> ReadDrives()
    {
        var list = new List<DriveRow>();
        string sys = Path.GetPathRoot(Paths.WindowsDir) ?? "";
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); }
        catch { return list; }
        foreach (var d in all)
        {
            try
            {
                if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                if (!d.IsReady) continue;
                var r = new DriveRow { Root = d.Name, Type = d.DriveType == DriveType.Fixed ? "Fixed" : "Removable" };
                try { r.Label = d.VolumeLabel ?? ""; } catch { r.Label = ""; }
                try { r.Format = d.DriveFormat ?? ""; } catch { r.Format = ""; }
                try { r.Total = d.TotalSize; r.Free = d.TotalFreeSpace; } catch { r.Total = -1; r.Free = -1; }
                r.IsSystem = string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase);
                list.Add(r);
            }
            catch { }
        }
        return list;
    }

    async Task RefreshDrives()
    {
        var drives = await Task.Run(() => ReadDrives());
        _driveCards.Children.Clear();
        foreach (var d in drives) _driveCards.Children.Add(DriveCard(d));
        if (drives.Count == 0) _driveCards.Children.Add(Wrap("No ready drives found."));
        FillScanPicker(drives);
    }

    Border DriveCard(DriveRow d)
    {
        var sp = new StackPanel { Width = 290 };

        var top = new DockPanel();
        if (d.IsSystem)
        {
            var chip = Chip("WINDOWS", "Accent", "Windows is installed on this drive");
            DockPanel.SetDock(chip, Dock.Right);
            top.Children.Add(chip);
        }
        var title = Ui.Text(d.Display, "H2");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        top.Children.Add(title);
        sp.Children.Add(top);

        string fs = d.Format.Length > 0 ? d.Format : Fmt.Na;
        sp.Children.Add(Ui.Text(fs + " · " + (d.Type == "Fixed" ? "fixed drive" : "removable drive"), "Sub"));

        bool known = d.Total > 0 && d.Free >= 0;
        double used = known ? d.Total - d.Free : double.NaN;
        double fill = known ? used / d.Total : double.NaN;
        var meter = new Meter
        {
            LevelColors = true, WarnAt = 0.85, BadAt = 0.95, Height = 10,
            Margin = new Thickness(0, 10, 0, 4), Value = fill,
        };
        sp.Children.Add(meter);
        var pct = Ui.Text(known ? Fmt.Pct(fill * 100) + " full" : "Size unavailable", "Sub");
        pct.Margin = new Thickness(0, 0, 0, 8);
        sp.Children.Add(pct);

        var stats = new UniformGrid { Columns = 3 };
        stats.Children.Add(Stat("Used", known ? Fmt.Bytes(used) : Fmt.Na));
        stats.Children.Add(Stat("Free", known ? Fmt.Bytes(d.Free) : Fmt.Na));
        stats.Children.Add(Stat("Total", known ? Fmt.Bytes(d.Total) : Fmt.Na));
        sp.Children.Add(stats);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        string rootPath = d.Root;
        buttons.Children.Add(Ui.Button("Analyse", (_, _) => AnalyseFrom(rootPath), "Accent", "See which folders and files use the space"));
        if (d.IsSystem) buttons.Children.Add(Ui.Button("Clean up", (_, _) => _tabs.SelectedItem = _cleanTab));
        sp.Children.Add(buttons);
        return Ui.Card(sp);
    }

    static StackPanel Stat(string label, string value)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(label, "Label"));
        sp.Children.Add(Ui.Text(value, "ValueSmall"));
        return sp;
    }

    // ================================================================== clean up

    UIElement BuildCleanup()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _measureBtn = Ui.Button("Measure again", (_, _) => Ui.Run(MeasureAll, "Measuring"), null, "Measure every item again. Nothing is deleted.");
        bar.Children.Add(_measureBtn);
        bar.Children.Add(Ui.Button("Tick the safe ones", (_, _) => SelectRecommended(), null, "Tick the items marked SAFE that have something to clean"));
        bar.Children.Add(Ui.Button("Untick all", (_, _) => { foreach (var r in _cleanRows) r.Check.IsChecked = false; }));
        g.Children.Add(bar);

        var hint = new Border
        {
            Style = S("HintBox"), Margin = new Thickness(0, 0, 0, 8),
            Child = Wrap("Everything is measured first and nothing is deleted until you press Clean. Deleting here is permanent: files do not go " +
                         "to the Recycle Bin. Files in use, and temporary files changed in the last 24 hours, are left alone.", null),
        };
        Grid.SetRow(hint, 1);
        g.Children.Add(hint);

        foreach (var item in CleanupCatalog.Build())
        {
            var row = new CleanRowUi(item, UpdateTotals);
            if (item.Id == "winsxs")
            {
                Button analyse = null;
                analyse = Ui.Button("Analyse", (_, _) => Ui.Run(() => AnalyseOnDemand(row, analyse), "Component store analysis"),
                                    null, "Run DISM /AnalyzeComponentStore (takes a minute or two)");
                analyse.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                row.Extra.Children.Add(analyse);
                _resetBase.Margin = new Thickness(6, 0, 0, 0);
                row.Extra.Children.Add(_resetBase);
                row.Extra.Visibility = System.Windows.Visibility.Visible;
            }
            else if (item.Id == "windowsold")
            {
                var open = Ui.Button("Open Storage settings", (_, _) => Shell.Open("ms-settings:storagesense"), null,
                    "Storage › Temporary files › Previous Windows installation(s) does the same job");
                row.Extra.Children.Add(open);
                row.Extra.Visibility = System.Windows.Visibility.Visible;
            }
            _cleanRows.Add(row);
            _cleanList.Children.Add(row.Root);
        }
        var scroll = new ScrollViewer
        {
            Content = _cleanList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 2);
        g.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        left.Children.Add(_cleanTotal);
        _cleanStatus.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        left.Children.Add(_cleanStatus);
        footer.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        _saveLogBtn = Ui.Button("Save log…", (_, _) => SaveLog(), null, "Save the clean-up log as a text file");
        _saveLogBtn.Visibility = System.Windows.Visibility.Collapsed;
        _cleanCancelBtn = Ui.Button("Stop", (_, _) => StopCleaning(), null, "Stop after the current file");
        _cleanCancelBtn.Visibility = System.Windows.Visibility.Collapsed;
        _cleanBtn = Ui.Button("Clean selected…", (_, _) => Ui.Run(CleanSelected, "Clean-up"), "Danger",
            "Delete the ticked items permanently, after a confirmation");
        _cleanBtn.IsEnabled = false;
        right.Children.Add(_saveLogBtn);
        right.Children.Add(_cleanCancelBtn);
        right.Children.Add(_cleanBtn);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        Grid.SetRow(_cleanBar, 1);
        Grid.SetColumnSpan(_cleanBar, 2);
        footer.Children.Add(_cleanBar);
        Grid.SetRow(footer, 3);
        g.Children.Add(footer);

        _cleanLog.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        Grid.SetRow(_cleanLog, 4);
        g.Children.Add(_cleanLog);

        _cleanTotal.Text = "Not measured yet";
        return g;
    }

    void SelectRecommended()
    {
        foreach (var r in _cleanRows)
            r.Check.IsChecked = r.Check.IsEnabled && r.Item.Risk == CleanRisk.Safe && r.Measure != null && r.Measure.Available;
        UpdateTotals();
    }

    void UpdateTotals()
    {
        var sel = _cleanRows.Where(r => r.Selected).ToList();
        long bytes = sel.Where(r => r.HasExactSize).Sum(r => r.Measure.Bytes.Value);
        bool inexact = sel.Any(r => !r.HasExactSize);
        _cleanTotal.Text = sel.Count == 0
            ? "Nothing ticked"
            : $"{Plural(sel.Count, "item")} ticked · {Fmt.Bytes(bytes)} measured" + (inexact ? " plus items without an exact size" : "");
        if (_cleanBtn != null) _cleanBtn.IsEnabled = !_cleaning && _measuring == 0 && sel.Count > 0;
    }

    void UpdateRunningNotes()
    {
        foreach (var r in _cleanRows)
        {
            if (r.Item.Processes.Length == 0 || r.Measure == null || !r.Measure.Available) continue;
            string running = CleanupCatalog.RunningApp(r.Item);
            if (running != null)
                r.SetNote($"{r.Item.AppName} is running ({running}). Close it first - this item is skipped while it runs.", "Warn");
            else r.SetNote(r.Measure.Note, "Dim");
        }
    }

    async Task MeasureAll()
    {
        if (_cleaning) return;
        _measureCts?.Cancel();
        var cts = new CancellationTokenSource();
        _measureCts = cts;
        _measuredOnce = true;
        _measureBtn.IsEnabled = false;
        _measuring++;
        UpdateTotals();
        try
        {
            int i = 0;
            foreach (var r in _cleanRows)
            {
                i++;
                if (cts.IsCancellationRequested) return;
                if (r.Item.NeedsAdmin && !Elevation.IsAdmin)
                {
                    r.SetUnavailable("Needs administrator rights. Restart Winnow and accept the prompt to clean this.");
                    continue;
                }
                if (r.Item.MeasureOnDemand)
                {
                    if (r.Measure == null) r.SetOnDemand("Press Analyse to see what it could free (DISM, a minute or two). You can also clean without analysing.");
                    continue;
                }
                _cleanStatus.Text = $"Measuring {i} of {_cleanRows.Count}: {r.Item.Name}…";
                r.SetMeasuring("measuring…");
                var m = await CleanupCatalog.MeasureAsync(r.Item, cts.Token);
                if (cts.IsCancellationRequested) return;
                r.Apply(m);
            }
            _cleanStatus.Text = "Measured at " + DateTime.Now.ToString("HH:mm") + ". Nothing has been deleted.";
        }
        finally
        {
            _measuring--;
            if (ReferenceEquals(_measureCts, cts)) _measureBtn.IsEnabled = !_cleaning;
            UpdateRunningNotes();
            UpdateTotals();
        }
    }

    async Task AnalyseOnDemand(CleanRowUi row, Button button)
    {
        if (!Elevation.IsAdmin) { Status.Set("DISM needs administrator rights.", StatusKind.Warn); return; }
        if (_cleaning) { Status.Set("Wait for the clean-up to finish first.", StatusKind.Warn); return; }
        await Ui.Busy(button, async () =>
        {
            row.SetMeasuring("analysing… (a minute or two)");
            _measuring++;
            UpdateTotals();
            CleanMeasure m;
            try { m = await CleanupCatalog.MeasureAsync(row.Item, CancellationToken.None); }
            finally { _measuring--; }
            row.Apply(m);
            if (!m.Bytes.HasValue && !string.IsNullOrEmpty(m.Raw))
            {
                LogLine("DISM /AnalyzeComponentStore output:");
                LogLine(m.Raw);
            }
            UpdateTotals();
        }, "Analysing…");
    }

    string ConfirmNoteFor(CleanRowUi r)
    {
        if (r.Item.Id == "winsxs" && _resetBase.IsChecked == true)
            return r.Item.ConfirmNote + " /ResetBase is on: installed updates can no longer be uninstalled.";
        return r.Item.ConfirmNote;
    }

    async Task CleanSelected()
    {
        if (_cleaning || _measuring > 0) return;
        var sel = _cleanRows.Where(r => r.Selected).ToList();
        if (sel.Count == 0) { Status.Set("Tick at least one item first.", StatusKind.Warn); return; }

        long total = sel.Where(r => r.HasExactSize).Sum(r => r.Measure.Bytes.Value);
        var sb = new StringBuilder();
        sb.AppendLine("These will be deleted permanently. They do not go to the Recycle Bin and cannot be got back:");
        sb.AppendLine();
        foreach (var r in sel) sb.AppendLine("•  " + r.Item.Name + "  -  " + r.SizeLabel);
        sb.AppendLine();
        sb.AppendLine("Measured total: " + Fmt.Bytes(total));
        var notes = sel.Select(ConfirmNoteFor).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in notes) sb.AppendLine(n);
        }
        var running = sel.Select(r => CleanupCatalog.RunningApp(r.Item) != null ? r.Item.AppName : null).Where(n => n != null).ToList();
        if (running.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Running now, so it will be skipped: " + string.Join(", ", running) + ".");
        }
        sb.AppendLine();
        sb.Append("Files in use are skipped and counted.");
        if (!Ui.Confirm(sb.ToString(), "Clean up", "Delete permanently", "Cancel", danger: true)) return;

        _measureCts?.Cancel();
        _cleaning = true;
        var cts = new CancellationTokenSource();
        _cleanCts = cts;
        _cleanBtn.IsEnabled = false;
        _measureBtn.IsEnabled = false;
        _cleanCancelBtn.Visibility = System.Windows.Visibility.Visible;
        _cleanCancelBtn.IsEnabled = true;
        _cleanBar.Visibility = System.Windows.Visibility.Visible;
        _cleanBar.IsIndeterminate = false;
        _cleanBar.Minimum = 0;
        _cleanBar.Maximum = sel.Count;
        _cleanBar.Value = 0;
        LogLine($"Clean-up started {DateTime.Now:HH:mm:ss}");

        long freed = 0, deleted = 0, skipped = 0, skippedBytes = 0, measured = 0;
        long? estimated = null;
        var done = new List<CleanRowUi>();
        var detail = new StringBuilder();
        try
        {
            for (int i = 0; i < sel.Count; i++)
            {
                var r = sel[i];
                _cleanBar.Value = i;
                if (cts.IsCancellationRequested) { LogLine("Stopped before " + r.Item.Name + "."); break; }
                string app = CleanupCatalog.RunningApp(r.Item);
                if (app != null)
                {
                    LogLine($"{r.Item.Name}: skipped - {app} is running.");
                    detail.AppendLine(r.Item.Name + ": skipped, " + app + " running");
                    continue;
                }
                _cleanStatus.Text = $"Cleaning {i + 1} of {sel.Count}: {r.Item.Name}…" + (r.Item.CannotCancel ? " (this one cannot be stopped part-way)" : "");
                LogLine(r.Item.Name + "…");
                var ctx = new CleanContext
                {
                    Token = cts.Token,
                    ResetBase = _resetBase.IsChecked == true,
                    Measured = r.Measure,
                    Say = line => Post(() => LogLine(line)),
                    Progress = text => Post(() => { if (_cleaning) _cleanStatus.Text = text; }),
                };
                try { await CleanupCatalog.CleanAsync(r.Item, ctx); }
                catch (Exception ex)
                {
                    LogLine("  failed: " + ex.Message);
                    Log.Write("disk clean " + r.Item.Id + ": " + ex);
                }
                var t = ctx.Tally;
                freed += t.Freed;
                deleted += t.Deleted;
                skipped += t.Skipped;
                skippedBytes += t.SkippedBytes;
                if (t.FreedEstimate.HasValue) estimated = (estimated ?? 0) + t.FreedEstimate.Value;
                if (r.HasExactSize) measured += r.Measure.Bytes.Value;
                string line1 = $"  {r.Item.Name}: freed {Fmt.Bytes(t.Freed)}, {Plural(t.Deleted, "file")} deleted" +
                               (t.Skipped > 0 ? $", {Plural(t.Skipped, "file")} skipped ({Fmt.Bytes(t.SkippedBytes)}) because they were in use or protected" : "") +
                               (t.FreedEstimate.HasValue ? $", free space rose by {Fmt.Bytes(t.FreedEstimate.Value)} (est.)" : "");
                LogLine(line1);
                detail.AppendLine(line1.Trim());
                done.Add(r);
            }
            _cleanBar.Value = sel.Count;
        }
        finally
        {
            _cleaning = false;
            _cleanCancelBtn.Visibility = System.Windows.Visibility.Collapsed;
            _cleanBar.Visibility = System.Windows.Visibility.Collapsed;
            _measureBtn.IsEnabled = true;
        }

        string result = $"Freed {Fmt.Bytes(freed)} of {Fmt.Bytes(measured)}" +
                        (skipped > 0 ? $" ({Plural(skipped, "file")} skipped because they were in use)" : "") + ".";
        if (estimated.HasValue) result += $" Component store clean-up: free space rose by about {Fmt.Bytes(estimated.Value)} (est.).";
        if (cts.IsCancellationRequested) result += " Stopped early.";
        LogLine(result);
        _cleanStatus.Text = result;
        Status.Set(result, skipped > 0 || cts.IsCancellationRequested ? StatusKind.Warn : StatusKind.Good);
        if (done.Count > 0)
            Change.Note("disk.cleanup", "Disk clean-up: " + string.Join(", ", done.Select(r => r.Item.Name)),
                        result + Environment.NewLine + detail.ToString().TrimEnd());

        // measure again so the list shows what is left
        _measuring++;
        UpdateTotals();
        try
        {
            foreach (var r in done)
            {
                if (r.Item.MeasureOnDemand) { r.SetOnDemand("Cleaned. Press Analyse to see the store as it is now."); continue; }
                r.SetMeasuring("measuring…");
                r.Apply(await CleanupCatalog.MeasureAsync(r.Item, CancellationToken.None));
            }
        }
        finally { _measuring--; }
        UpdateRunningNotes();
        UpdateTotals();
    }

    void StopCleaning()
    {
        _cleanCts?.Cancel();
        _cleanCancelBtn.IsEnabled = false;
        LogLine("Stopping after the current file… (DISM and Disk Cleanup always run to the end)");
    }

    void LogLine(string s)
    {
        _cleanLog.Visibility = System.Windows.Visibility.Visible;
        if (_saveLogBtn != null) _saveLogBtn.Visibility = System.Windows.Visibility.Visible;
        if (_cleanLog.Text.Length > 400_000) _cleanLog.Text = _cleanLog.Text.Substring(_cleanLog.Text.Length - 200_000);
        _cleanLog.AppendText(s + Environment.NewLine);
        _cleanLog.ScrollToEnd();
    }

    void SaveLog()
    {
        string path = Ui.SaveFile("winnow-cleanup-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt", "Text files|*.txt|All files|*.*");
        if (path == null) return;
        try
        {
            File.WriteAllText(path, _cleanLog.Text);
            Status.Set("Saved " + path, StatusKind.Good);
        }
        catch (Exception ex) { Status.Set("Could not save: " + ex.Message, StatusKind.Bad); }
    }

    /// <summary>One clean-up item on screen. Built once; updated in place.</summary>
    sealed class CleanRowUi
    {
        public readonly CleanItem Item;
        public CleanMeasure Measure;
        public readonly CheckBox Check;
        public readonly TextBlock SizeText, FilesText, NoteText;
        public readonly StackPanel Extra;
        public readonly Border Root;
        bool _defaulted;                     // default tick applied once, on the first successful measure

        public bool Selected => Check.IsChecked == true && Check.IsEnabled;
        public bool HasExactSize => Measure != null && Measure.Bytes.HasValue && !Measure.Estimate;

        public string SizeLabel =>
            Measure == null || !Measure.Bytes.HasValue ? Fmt.Na
            : Measure.Estimate ? "up to " + Fmt.Bytes(Measure.Bytes.Value) + " (est.)"
            : Fmt.Bytes(Measure.Bytes.Value);

        public CleanRowUi(CleanItem item, Action changed)
        {
            Item = item;
            Check = new CheckBox { VerticalAlignment = System.Windows.VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0), ToolTip = "Include in the clean-up" };
            Check.Checked += (_, _) => changed();
            Check.Unchecked += (_, _) => changed();

            var title = new WrapPanel { Orientation = Orientation.Horizontal };
            var name = new TextBlock { Text = item.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            if (item.Where.Length > 0) name.ToolTip = item.Where;
            name.Cursor = Cursors.Hand;
            name.MouseLeftButtonUp += (_, _) => { if (Check.IsEnabled) Check.IsChecked = Check.IsChecked != true; };
            title.Children.Add(name);
            var (riskText, riskKey, riskTip) = item.Risk switch
            {
                CleanRisk.Safe => ("SAFE", "Good", "Nothing you need is lost; anything required is rebuilt automatically."),
                CleanRisk.Moderate => ("MODERATE", "Warn", "Safe to remove, but there is a cost - read the description."),
                _ => ("ADVANCED", "Bad", "Cannot be undone and removes a way back. Read the description first."),
            };
            title.Children.Add(Chip(riskText, riskKey, riskTip));
            if (item.NeedsAdmin) title.Children.Add(Chip("ADMIN", "Dim", "Needs Winnow to run as administrator"));

            var main = new StackPanel();
            main.Children.Add(title);
            var desc = new TextBlock { Text = item.Description, Style = S("Sub"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 0) };
            main.Children.Add(desc);
            NoteText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 12, 0), Visibility = System.Windows.Visibility.Collapsed };
            main.Children.Add(NoteText);
            Extra = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = System.Windows.Visibility.Collapsed };
            main.Children.Add(Extra);
            Grid.SetColumn(main, 1);

            var nums = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            SizeText = new TextBlock { Text = Fmt.Na, Style = S("ValueSmall"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap };
            FilesText = new TextBlock { Text = "", Style = S("Sub"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            nums.Children.Add(SizeText);
            nums.Children.Add(FilesText);
            Grid.SetColumn(nums, 2);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.Children.Add(Check);
            grid.Children.Add(main);
            grid.Children.Add(nums);

            Root = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(4, 9, 4, 9) };
            Root.SetResourceReference(Border.BorderBrushProperty, "Border");
        }

        public void SetNote(string text, string colorKey)
        {
            NoteText.Text = text ?? "";
            NoteText.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            NoteText.Visibility = string.IsNullOrEmpty(text) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }

        public void SetMeasuring(string what)
        {
            SizeText.Text = what;
            FilesText.Text = "";
        }

        public void SetUnavailable(string note)
        {
            Measure = null;
            Check.IsChecked = false;
            Check.IsEnabled = false;
            Extra.IsEnabled = false;
            SizeText.Text = Fmt.Na;
            FilesText.Text = "";
            SetNote(note, "Warn");
        }

        public void SetOnDemand(string note)
        {
            Measure = null;
            Check.IsEnabled = true;
            SizeText.Text = Fmt.Na;
            FilesText.Text = "";
            SetNote(note, "Dim");
        }

        public void Apply(CleanMeasure m)
        {
            Measure = m;
            if (!m.Available)
            {
                Check.IsChecked = false;
                Check.IsEnabled = false;
            }
            else
            {
                Check.IsEnabled = true;
                if (!_defaulted)
                {
                    _defaulted = true;
                    Check.IsChecked = Item.DefaultOn;
                }
            }
            SizeText.Text = SizeLabel;
            FilesText.Text = m.Available && m.Files.HasValue ? Plural(m.Files.Value, Item.Id == "recycle" ? "item" : "file") : "";
            SetNote(m.Note, "Dim");
        }
    }

    // ================================================================== analyse

    UIElement BuildAnalyse()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_scanPick);
        bar.Children.Add(Ui.Button("Choose folder…", (_, _) => ChooseFolder(), null, "Analyse one folder instead of a whole drive"));
        _scanBtn = Ui.Button("Scan", (_, _) => Ui.Run(() => StartScan(PickedPath()), "Disk scan"), "Accent");
        bar.Children.Add(_scanBtn);
        _scanCancelBtn = Ui.Button("Stop", (_, _) => _scanCts?.Cancel(), null, "Stop the scan and show what was found so far");
        _scanCancelBtn.Visibility = System.Windows.Visibility.Collapsed;
        bar.Children.Add(_scanCancelBtn);
        g.Children.Add(bar);

        var prog = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        _scanStatus.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        _scanStatus.Text = "Pick a drive or folder and press Scan. Sizes are file sizes, not size on disk.";
        prog.Children.Add(_scanStatus);
        prog.Children.Add(_scanBar);
        Grid.SetRow(prog, 1);
        g.Children.Add(prog);

        var nav = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _upBtn = Ui.Button("↑ Up", (_, _) => GoUp(), null, "Up one folder (Backspace)");
        _upBtn.IsEnabled = false;
        DockPanel.SetDock(_upBtn, Dock.Left);
        nav.Children.Add(_upBtn);
        nav.Children.Add(_crumbs);
        Grid.SetRow(nav, 2);
        g.Children.Add(nav);

        SetupGrid(_grid, true);
        SetupGrid(_bigGrid, false);
        var inner = new TabControl();
        inner.Items.Add(new TabItem { Header = "Folders and files", Content = _grid });
        inner.Items.Add(new TabItem { Header = "Largest files", Content = _bigGrid });
        inner.SelectedIndex = 0;
        Grid.SetRow(inner, 3);
        g.Children.Add(inner);

        _scanSummary.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        Grid.SetRow(_scanSummary, 4);
        g.Children.Add(_scanSummary);
        return g;
    }

    static DataGridTextColumn TextCol(string header, string binding, DataGridLength width, string sortBy = null, bool numeric = false)
    {
        var c = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(binding),
            Width = width,
            SortMemberPath = sortBy ?? binding,
        };
        if (numeric) c.ElementStyle = S("NumCell");
        return c;
    }

    void SetupGrid(DataGrid dg, bool drill)
    {
        dg.AutoGenerateColumns = false;
        dg.IsReadOnly = true;
        dg.SelectionMode = DataGridSelectionMode.Extended;
        dg.HeadersVisibility = DataGridHeadersVisibility.Column;

        var glyph = TextCol("", nameof(DiskEntryRow.Glyph), new DataGridLength(30), nameof(DiskEntryRow.IsDir));
        glyph.ElementStyle = S("Icon");
        glyph.CanUserSort = false;
        dg.Columns.Add(glyph);
        dg.Columns.Add(TextCol("Name", nameof(DiskEntryRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));
        dg.Columns.Add(TextCol("Size", nameof(DiskEntryRow.SizeText), new DataGridLength(90), nameof(DiskEntryRow.Size), true));
        if (drill)
        {
            var bar = new FrameworkElementFactory(typeof(DiskPctBar));
            bar.SetBinding(DiskPctBar.ValueProperty, new Binding(nameof(DiskEntryRow.Pct)));
            dg.Columns.Add(new DataGridTemplateColumn
            {
                Header = "% of folder",
                Width = new DataGridLength(130),
                SortMemberPath = nameof(DiskEntryRow.Size),
                CellTemplate = new DataTemplate { VisualTree = bar },
            });
            dg.Columns.Add(TextCol("Files", nameof(DiskEntryRow.FilesText), new DataGridLength(90), nameof(DiskEntryRow.Files), true));
            dg.Columns.Add(TextCol("Last modified", nameof(DiskEntryRow.ModifiedText), new DataGridLength(140), nameof(DiskEntryRow.Modified)));
        }
        else
        {
            dg.Columns.Add(TextCol("Last modified", nameof(DiskEntryRow.ModifiedText), new DataGridLength(140), nameof(DiskEntryRow.Modified)));
            dg.Columns.Add(TextCol("Folder", nameof(DiskEntryRow.Folder), new DataGridLength(1.4, DataGridLengthUnitType.Star)));
        }

        dg.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(dg, e.OriginalSource as DependencyObject) is not DataGridRow row) return;
            if (row.Item is not DiskEntryRow r) return;
            if (drill && r.IsDir && r.Node != null) Ui.Run(() => ShowNode(r.Node), "Open folder");
            else Shell.Reveal(r.FullPath);
            e.Handled = true;
        };
        if (drill)
        {
            dg.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Back) { GoUp(); e.Handled = true; }
                else if (e.Key == Key.Enter && dg.SelectedItem is DiskEntryRow r && r.IsDir && r.Node != null)
                {
                    Ui.Run(() => ShowNode(r.Node), "Open folder");
                    e.Handled = true;
                }
            };
        }
        dg.ContextMenu = EntryMenu(dg);
    }

    static List<DiskEntryRow> Selected(DataGrid dg) => dg.SelectedItems.OfType<DiskEntryRow>().ToList();

    ContextMenu EntryMenu(DataGrid dg)
    {
        var cm = new ContextMenu();
        var open = new MenuItem { Header = "Open in Explorer" };
        open.Click += (_, _) => { if (dg.SelectedItem is DiskEntryRow r) Shell.Reveal(r.FullPath); };
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += (_, _) =>
        {
            var paths = Selected(dg).Select(r => r.FullPath).ToList();
            if (paths.Count > 0) Ui.CopyToClipboard(string.Join(Environment.NewLine, paths));
        };
        var del = new MenuItem { Header = "Delete to Recycle Bin…" };
        del.Click += (_, _) => Ui.Run(() => RecycleSelected(dg), "Delete to Recycle Bin");
        cm.Items.Add(open);
        cm.Items.Add(copy);
        cm.Items.Add(new Separator());
        cm.Items.Add(del);
        cm.Opened += (_, _) =>
        {
            var sel = Selected(dg);
            open.IsEnabled = sel.Count >= 1;
            copy.IsEnabled = sel.Count >= 1;
            string why = sel.Select(r => DeleteBlockReason(r.FullPath)).FirstOrDefault(x => x != null);
            del.IsEnabled = sel.Count >= 1 && why == null;
            del.Header = why == null ? "Delete to Recycle Bin…" : "Delete not offered: " + why;
        };
        return cm;
    }

    /// <summary>Why a path must not be deleted from here, or null if it may be.</summary>
    static string DeleteBlockReason(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return "not a valid path"; }
        string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
        if (full.Length == 0 || full.Equals(root, StringComparison.OrdinalIgnoreCase)) return "it is a drive root";

        string win = Paths.WindowsDir.TrimEnd('\\');
        if (full.Equals(win, StringComparison.OrdinalIgnoreCase) || full.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase))
            return "it is inside the Windows folder";

        var protectedFolders = new[]
        {
            Paths.ProgramFiles, Paths.ProgramFilesX86, Environment.GetEnvironmentVariable("ProgramFiles(Arm)"), Paths.ProgramData,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root + "\\Users",
        };
        foreach (var p in protectedFolders)
            if (!string.IsNullOrEmpty(p) && full.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "it is a system folder";

        string parent = (Path.GetDirectoryName(full) ?? "").TrimEnd('\\');
        if (parent.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(full);
            if (name.StartsWith("$") || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Boot", StringComparison.OrdinalIgnoreCase) || name.Equals("bootmgr", StringComparison.OrdinalIgnoreCase))
                return "it is a Windows system item";
        }
        return null;
    }

    async Task RecycleSelected(DataGrid dg)
    {
        var sel = Selected(dg);
        if (sel.Count == 0) return;
        foreach (var r in sel)
        {
            string why = DeleteBlockReason(r.FullPath);
            if (why != null) { Ui.Info($"{r.FullPath} cannot be deleted from here: {why}."); return; }
        }
        long bytes = sel.Sum(r => r.Size);
        string list = string.Join("\n", sel.Take(12).Select(r => "•  " + r.FullPath)) +
                      (sel.Count > 12 ? $"\n…and {sel.Count - 12:N0} more" : "");
        string msg = $"Send {Plural(sel.Count, "item")} ({Fmt.Bytes(bytes)}) to the Recycle Bin?\n\n{list}\n\n" +
                     "You can restore them from the Recycle Bin. Anything too big for the Recycle Bin would be deleted for good - " +
                     "Windows asks you first if so.";
        if (!Ui.Confirm(msg, "Delete to Recycle Bin", "Send to Recycle Bin", "Cancel", danger: true)) return;

        var (ok, message) = await DiskNative.SendToRecycleBin(sel.Select(r => r.FullPath).ToList());
        var gone = sel.Where(r => !File.Exists(r.FullPath) && !Directory.Exists(r.FullPath)).ToList();
        foreach (var r in gone) RemoveFromTree(r);
        long goneBytes = gone.Sum(r => r.Size);
        if (gone.Count > 0)
            Change.Note("disk.analyse", $"Sent {Plural(gone.Count, "item")} ({Fmt.Bytes(goneBytes)}) to the Recycle Bin",
                        string.Join(Environment.NewLine, gone.Select(r => r.FullPath)));
        if (gone.Count == sel.Count)
            Status.Set($"Sent {Plural(gone.Count, "item")} ({Fmt.Bytes(goneBytes)}) to the Recycle Bin.", StatusKind.Good);
        else
            Status.Set($"Sent {gone.Count:N0} of {sel.Count:N0} to the Recycle Bin" + (ok ? "." : ": " + message), StatusKind.Warn);
        RefreshLargest();
        if (_node != null) await ShowNode(_node);
        if (_scan != null) _scanSummary.Text = SummaryText(_scan);
    }

    void RemoveFromTree(DiskEntryRow r)
    {
        if (_scan == null) return;
        if (r.IsDir && r.Node != null)
        {
            var n = r.Node;
            var p = n.Parent;
            if (p == null) return;
            p.Children.Remove(n);
            for (var a = p; a != null; a = a.Parent)
            {
                a.Size -= n.Size;
                a.Files -= n.Files;
                a.Folders -= n.Folders + 1;
            }
            string prefix = n.FullPath.TrimEnd('\\') + "\\";
            _scan.Largest.RemoveAll(f => f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (_node != null && (_node == n || _node.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) _node = p;
        }
        else
        {
            var dir = FolderSizer.Find(_scan.Root, Path.GetDirectoryName(r.FullPath));
            if (dir != null)
            {
                dir.OwnSize -= r.Size;
                dir.OwnFiles--;
                for (var a = dir; a != null; a = a.Parent)
                {
                    a.Size -= r.Size;
                    a.Files--;
                }
            }
            _scan.Largest.RemoveAll(f => string.Equals(f.FullPath, r.FullPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    void FillScanPicker(List<DriveRow> drives)
    {
        string keep = PickedPath();
        _scanPick.Items.Clear();
        foreach (var d in drives)
            _scanPick.Items.Add(new ComboBoxItem { Content = d.Display, Tag = d.Root });
        if (keep != null) SelectInPicker(keep);
        else
        {
            var sys = drives.FirstOrDefault(d => d.IsSystem) ?? drives.FirstOrDefault();
            if (sys != null) SelectInPicker(sys.Root);
        }
    }

    string PickedPath() => (_scanPick.SelectedItem as ComboBoxItem)?.Tag as string;

    void SelectInPicker(string path)
    {
        var hit = _scanPick.Items.OfType<ComboBoxItem>()
                          .FirstOrDefault(i => string.Equals(i.Tag as string, path, StringComparison.OrdinalIgnoreCase));
        if (hit == null)
        {
            hit = new ComboBoxItem { Content = path, Tag = path };
            _scanPick.Items.Add(hit);
        }
        _scanPick.SelectedItem = hit;
    }

    void ChooseFolder()
    {
        string start = PickedPath();
        string f = Ui.PickFolder(start != null && Directory.Exists(start) ? start : null);
        if (f == null) return;
        SelectInPicker(f);
        Ui.Run(() => StartScan(f), "Disk scan");
    }

    void AnalyseFrom(string path)
    {
        _tabs.SelectedItem = _analyseTab;
        SelectInPicker(path);
        Ui.Run(() => StartScan(path), "Disk scan");
    }

    async Task StartScan(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Status.Set("Pick a drive or folder that exists.", StatusKind.Warn);
            return;
        }
        _tabs.SelectedItem = _analyseTab;
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var sizer = new FolderSizer();
        _sizer = sizer;
        _scanBtn.IsEnabled = false;
        _scanCancelBtn.Visibility = System.Windows.Visibility.Visible;
        _scanBar.Visibility = System.Windows.Visibility.Visible;
        _scanSummary.Text = "";
        _scanStatus.Text = "Scanning " + path + "…";
        _scanTicker.Start();
        FolderScan scan;
        try { scan = await sizer.ScanAsync(path, cts.Token); }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                _scanTicker.Stop();
                _scanBtn.IsEnabled = true;
                _scanCancelBtn.Visibility = System.Windows.Visibility.Collapsed;
                _scanBar.Visibility = System.Windows.Visibility.Collapsed;
            }
        }
        if (!ReferenceEquals(_scanCts, cts)) return;   // a newer scan replaced this one

        _scan = scan;
        _scanStatus.Text = scan.Cancelled
            ? $"Scan stopped after {Plural(scan.Root.Files, "file")} - the sizes below are incomplete."
            : $"Scanned {path} in {scan.Elapsed.TotalSeconds:0.0} s. Double-click a folder to open it.";
        _scanSummary.Text = SummaryText(scan);
        RefreshLargest();
        await ShowNode(scan.Root);
        Status.Set($"{path}: {Fmt.Bytes(scan.Root.Size)} in {Plural(scan.Root.Files, "file")}", scan.Cancelled ? StatusKind.Warn : StatusKind.Good);
    }

    void UpdateScanProgress()
    {
        var s = _sizer;
        if (s == null) return;
        _scanStatus.Text = $"Scanning… {s.FilesSeen:N0} files · {Fmt.Bytes(s.BytesSeen)} · {Shorten(s.Current, 90)}";
    }

    static string SummaryText(FolderScan s)
    {
        var r = s.Root;
        var sb = new StringBuilder();
        sb.Append($"{r.FullPath}: {Fmt.Bytes(r.Size)} in {Plural(r.Files, "file")} and {Plural(r.Folders, "folder")}.");
        if (r.DeniedBelow > 0) sb.Append($" {Plural(r.DeniedBelow, "folder")} could not be read, so totals are a minimum.");
        if (s.Links > 0) sb.Append($" {Plural(s.Links, "link")} (junctions and symbolic links) were not followed.");
        if (s.CloudFiles > 0) sb.Append($" {Plural(s.CloudFiles, "cloud-only file")} not stored on this disk were left out.");
        sb.Append(" Sizes are file sizes, not size on disk; a hard-linked file (common in C:\\Windows) counts once per link.");
        return sb.ToString();
    }

    void RefreshLargest()
    {
        if (_scan == null) { _bigGrid.ItemsSource = null; return; }
        var rows = _scan.Largest.Select(f => DiskEntryRow.ForFile(f, double.NaN)).ToList();
        var view = new ListCollectionView(rows);
        view.SortDescriptions.Add(new SortDescription(nameof(DiskEntryRow.Size), ListSortDirection.Descending));
        _bigGrid.ItemsSource = view;
    }

    void SetFolderRows(List<DiskEntryRow> rows)
    {
        var view = new ListCollectionView(rows);
        view.SortDescriptions.Add(new SortDescription(nameof(DiskEntryRow.Size), ListSortDirection.Descending));
        _grid.ItemsSource = view;
    }

    async Task ShowNode(FolderNode node)
    {
        if (node == null) return;
        _node = node;
        BuildCrumbs();
        _upBtn.IsEnabled = node.Parent != null;
        double parentSize = node.Size;
        var dirs = node.Children.Select(c => DiskEntryRow.ForDir(c, parentSize)).ToList();
        SetFolderRows(dirs.ToList());
        string dirPath = node.FullPath;
        var files = await Task.Run(() => FolderSizer.ListFiles(dirPath));
        if (!ReferenceEquals(_node, node)) return;   // the user moved on
        if (files.Count == 0) return;
        dirs.AddRange(files.Select(f => DiskEntryRow.ForFile(f, parentSize)));
        SetFolderRows(dirs);
    }

    void GoUp()
    {
        var parent = _node?.Parent;
        if (parent != null) Ui.Run(() => ShowNode(parent), "Open folder");
    }

    void BuildCrumbs()
    {
        _crumbs.Children.Clear();
        var chain = new List<FolderNode>();
        for (var x = _node; x != null; x = x.Parent) chain.Insert(0, x);
        for (int i = 0; i < chain.Count; i++)
        {
            var n = chain[i];
            if (i > 0)
            {
                var sep = Ui.Text("›", "Sub");
                sep.Margin = new Thickness(2, 0, 2, 0);
                _crumbs.Children.Add(sep);
            }
            var b = Ui.Button(i == 0 ? n.FullPath : n.Name, (_, _) => Ui.Run(() => ShowNode(n), "Open folder"), "Ghost");
            b.Margin = new Thickness(0);
            if (ReferenceEquals(n, _node)) b.FontWeight = FontWeights.SemiBold;
            _crumbs.Children.Add(b);
        }
    }
}
