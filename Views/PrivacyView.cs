using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Privacy: switch off Windows' tracking, adverts and AI data collection, one
/// undoable tweak at a time, plus the History tab listing every change Winnow
/// has made on any page.
/// </summary>
public sealed class PrivacyView : UserControl, IView
{
    // ---- tweaks tab
    readonly List<PrivacyTweakRow> _rows = new();
    TabControl _tabs;
    TabItem _tweaksTab, _historyTab;
    Button _apply, _undoAll, _recheck;
    TextBlock _selCount, _checkInfo;
    ListBox _log;
    Border _logCard;

    // ---- history tab
    DataGrid _grid;
    TextBox _filter;
    CheckBox _showUndone;
    TextBlock _histCount;
    Button _undoSel, _export;
    ListCollectionView _histView;

    // ---- state
    Dictionary<string, string> _taskStates;   // last batched task query (null = not read / failed)
    HashSet<string> _fwRules;                  // Winnow firewall rules present (null = not read)
    bool _busy, _checking, _checkAgain, _shown, _historyDirty = true;
    int _journalPending;
    static bool _restorePointDone;             // one restore point per run

    public PrivacyView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(Ui.Header("Privacy",
            "Turn off Windows' tracking, adverts and AI data collection. Every change is recorded and can be undone on its own. " +
            "Security is left alone: nothing here touches Windows Update, Microsoft Defender, SmartScreen, UAC or the firewall's protection."));

        _tabs = new TabControl();
        Grid.SetRow(_tabs, 1);
        _tweaksTab = new TabItem { Header = "Tweaks", Content = BuildTweaksTab() };
        _historyTab = new TabItem { Header = "History", Content = BuildHistoryTab() };
        _tabs.Items.Add(_tweaksTab);
        _tabs.Items.Add(_historyTab);
        _tabs.SelectionChanged += (s, e) =>
        {
            if (e.OriginalSource == _tabs && _tabs.SelectedItem == _historyTab && _historyDirty) RebuildHistory();
        };
        root.Children.Add(_tabs);

        Content = root;
        Journal.Changed += OnJournalChanged;
        UpdateButtons();
    }

    // =================================================================== Tweaks tab

    UIElement BuildTweaksTab()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // info
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // toolbar
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // list
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // progress log

        // ---- info
        var info = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        info.Children.Add(PrivacyUi.Para(WinVer.Summary, "Text", 12.5));
        string edition = PrivacyCatalog.EditionSummary();
        if (edition.Length > 0) info.Children.Add(PrivacyUi.Para(edition, "Warn", 12, new Thickness(0, 3, 0, 0)));
        info.Children.Add(PrivacyUi.Para("Per-user settings apply to the Windows account Winnow is running as. Some changes take effect after you sign out or restart.",
            "Dim", 12, new Thickness(0, 3, 0, 0)));
        if (!Elevation.IsAdmin)
        {
            var hintStack = new StackPanel();
            hintStack.Children.Add(PrivacyUi.Para(
                "Winnow is not running as administrator, so this page can show what is set but cannot change or undo anything. " +
                "Close Winnow and start it again, and accept the Windows prompt.", "Text", 12.5));
            var restart = Ui.Button("Restart as administrator", (_, _) => RestartElevated());
            restart.HorizontalAlignment = HorizontalAlignment.Left;
            restart.Margin = new Thickness(0, 8, 0, 0);
            hintStack.Children.Add(restart);
            var hint = new Border { Child = hintStack, Style = PrivacyUi.St("HintBox"), Margin = new Thickness(0, 8, 10, 0) };
            info.Children.Add(hint);
        }
        g.Children.Add(info);

        // ---- toolbar
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(PresetButton("Light", 1, "Safe changes that switch off data collection and adverts without losing features you are likely to use."));
        bar.Children.Add(PresetButton("Recommended", 2, "Light plus the telemetry service and tasks, error reporting, Edge background running, Widgets and more. What most people should apply."));
        bar.Children.Add(PresetButton("Aggressive", 3, "Recommended plus changes that switch off features some people use (cloud search, AI in Paint and Notepad, Edge news and suggestions, telemetry firewall rules). Manual-only items are still left for you to tick."));
        var clear = Ui.Button("Clear selection", (_, _) => { foreach (var r in _rows) r.IsSelected = false; UpdateButtons(); });
        clear.Margin = new Thickness(0, 0, 14, 4);
        bar.Children.Add(clear);

        _selCount = PrivacyUi.Para("0 changes selected", "Dim", 12, new Thickness(0, 0, 14, 4));
        _selCount.VerticalAlignment = VerticalAlignment.Center;
        _selCount.TextWrapping = TextWrapping.NoWrap;
        bar.Children.Add(_selCount);

        _recheck = Ui.Button("Check again", (_, _) => Ui.Run(CheckStatesAsync, "Privacy check"), null, "Read the current state of every tweak from the system.");
        _recheck.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(_recheck);
        _undoAll = Ui.Button("Undo all privacy changes", (_, _) => Ui.Run(UndoAllAsync, "Undo privacy changes"), "Danger",
            "Put back every privacy change Winnow has made, newest first.");
        _undoAll.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(_undoAll);
        _apply = Ui.Button("Apply selected", (_, _) => Ui.Run(ApplyAsync, "Apply privacy changes"), "Accent");
        _apply.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(_apply);
        Grid.SetRow(bar, 1);
        g.Children.Add(bar);

        // ---- the list, grouped by category
        var list = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        _checkInfo = PrivacyUi.Para("Checking what is already set on this PC…", "Dim", 12, new Thickness(0, 0, 0, 8));
        list.Children.Add(_checkInfo);
        foreach (var cat in PrivacyCatalog.Categories)
        {
            var tweaks = PrivacyCatalog.All.Where(t => t.Category == cat).ToList();
            if (tweaks.Count == 0) continue;
            var sec = new StackPanel();
            var h = new TextBlock { Text = cat, Style = PrivacyUi.St("H2") };
            sec.Children.Add(h);
            string note = PrivacyCatalog.CategoryNote(cat);
            if (note.Length > 0) sec.Children.Add(PrivacyUi.Para(note, "Dim", 12, new Thickness(0, 0, 0, 4)));
            foreach (var t in tweaks)
            {
                var row = new PrivacyTweakRow(t, Describe, r => Ui.Run(() => UndoTweakAsync(r), "Undo"), UpdateButtons);
                _rows.Add(row);
                sec.Children.Add(row.Root);
            }
            list.Children.Add(Ui.Card(sec));
        }
        var scroll = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 2);
        g.Children.Add(scroll);

        // ---- progress log (shown once something runs)
        var logStack = new DockPanel();
        var logHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var copy = Ui.Button("Copy log", (_, _) => Ui.CopyToClipboard(string.Join(Environment.NewLine, _log.Items.OfType<string>())), "Ghost");
        var hide = Ui.Button("Hide", (_, _) => _logCard.Visibility = Visibility.Collapsed, "Ghost");
        DockPanel.SetDock(hide, Dock.Right);
        DockPanel.SetDock(copy, Dock.Right);
        logHead.Children.Add(hide);
        logHead.Children.Add(copy);
        logHead.Children.Add(new TextBlock { Text = "Progress", Style = PrivacyUi.St("H2"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(logHead, Dock.Top);
        logStack.Children.Add(logHead);
        _log = new ListBox { Height = 140 };
        _log.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        _log.FontSize = 11.5;
        logStack.Children.Add(_log);
        _logCard = Ui.Card(logStack);
        _logCard.Margin = new Thickness(0, 8, 10, 0);
        _logCard.Visibility = Visibility.Collapsed;
        Grid.SetRow(_logCard, 3);
        g.Children.Add(_logCard);

        return g;
    }

    Button PresetButton(string name, int level, string tip)
    {
        var b = Ui.Button(name, (_, _) =>
        {
            foreach (var r in _rows) r.IsSelected = r.Tweak.InPreset(level);
            UpdateButtons();
            Status.Set($"{name} preset: {_rows.Count(r => r.IsSelected)} changes ticked. Nothing changes until you press Apply.");
        }, null, tip);
        b.Margin = new Thickness(0, 0, 6, 4);
        return b;
    }

    List<string> Describe(PrivacyTweak t) => t.DescribeActions(_taskStates, _fwRules);

    void UpdateButtons()
    {
        if (_apply == null) return;   // still building
        int n = _rows.Count(r => r.IsSelected);
        _selCount.Text = n == 1 ? "1 change selected" : n + " changes selected";
        bool can = Elevation.IsAdmin && !_busy;
        _apply.IsEnabled = can && n > 0;
        _apply.Content = n > 0 ? $"Apply {n} selected" : "Apply selected";
        _undoAll.IsEnabled = can;
        _recheck.IsEnabled = !_busy && !_checking;
        if (_undoSel != null) _undoSel.IsEnabled = can;
        RefreshUndoLinks();
    }

    void RefreshUndoLinks()
    {
        HashSet<string> live;
        try
        {
            live = new HashSet<string>(Journal.Entries.Where(e => !e.Reverted).Select(e => e.Group ?? ""), StringComparer.Ordinal);
        }
        catch { live = new HashSet<string>(); }
        bool can = Elevation.IsAdmin && !_busy;
        foreach (var r in _rows) r.SetUndo(live.Contains(r.Tweak.Group), can);
    }

    void ShowLog(bool clear)
    {
        if (clear) _log.Items.Clear();
        _logCard.Visibility = Visibility.Visible;
    }

    void LogLine(string line)
    {
        _log.Items.Add(line);
        if (_log.Items.Count > 2000) _log.Items.RemoveAt(0);
        _log.ScrollIntoView(_log.Items[_log.Items.Count - 1]);
    }

    static string Short(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 300 ? s.Substring(0, 297) + "…" : s;
    }

    // ------------------------------------------------------------ state check

    async Task CheckStatesAsync()
    {
        // A check already running may have read the system before an apply/undo finished: run once more after it.
        if (_checking) { _checkAgain = true; return; }
        _checking = true;
        _checkAgain = false;
        UpdateButtons();
        _checkInfo.Text = "Checking what is already set on this PC…";
        foreach (var r in _rows) r.SetState(PrivacyState.Checking);
        try
        {
            var tweaks = _rows.Select(r => r.Tweak).ToList();
            var (qTasks, qFw, qStates) = await Task.Run(async () =>
            {
                var taskStates = await PrivacyCatalog.QueryTaskStatesAsync();   // one batched PowerShell call for every task
                var fwRules = await PrivacyCatalog.QueryFirewallRulesAsync();
                var states = new Dictionary<string, PrivacyState>();
                foreach (var tw in tweaks) states[tw.Id] = tw.CheckState(taskStates, fwRules);
                return (taskStates, fwRules, states);
            });
            _taskStates = qTasks;
            _fwRules = qFw;
            foreach (var r in _rows)
                r.SetState(qStates.TryGetValue(r.Tweak.Id, out var st) ? st : PrivacyState.Unknown);

            int applied = _rows.Count(r => r.State == PrivacyState.Applied);
            int partly = _rows.Count(r => r.State == PrivacyState.Partly);
            string extra = "";
            if (_taskStates == null) extra += " Scheduled tasks could not be read, so task-based tweaks show what their other settings say.";
            if (_fwRules == null) extra += " Firewall rules could not be read.";
            _checkInfo.Text = $"Checked at {DateTime.Now:HH:mm:ss}: {applied} of {_rows.Count} tweaks applied" +
                              (partly > 0 ? $", {partly} partly applied." : ".") + extra;
        }
        catch (Exception ex)
        {
            Log.Write("privacy check: " + ex);
            _checkInfo.Text = "Could not check the current state: " + ex.Message;
            foreach (var r in _rows) if (r.State == PrivacyState.Checking) r.SetState(PrivacyState.Unknown);
        }
        finally
        {
            _checking = false;
            UpdateButtons();
        }
        if (_checkAgain) await CheckStatesAsync();
    }

    // ------------------------------------------------------------ apply

    async Task ApplyAsync()
    {
        if (_busy) return;
        if (!Elevation.IsAdmin)
        {
            Ui.Error("Winnow needs to run as administrator to change these settings. Close it and start it again, accepting the Windows prompt.");
            return;
        }
        var chosen = _rows.Where(r => r.IsSelected).Select(r => r.Tweak).ToList();
        if (chosen.Count == 0)
        {
            Status.Set("Tick at least one change first.", StatusKind.Warn);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(chosen.Count == 1 ? "Apply this privacy change?" : $"Apply these {chosen.Count} privacy changes?");
        sb.AppendLine();
        foreach (var t in chosen.Take(14)) sb.AppendLine("•  " + t.Title);
        if (chosen.Count > 14) sb.AppendLine($"…and {chosen.Count - 14} more");
        int advanced = chosen.Count(t => t.Risk == "Advanced");
        int moderate = chosen.Count(t => t.Risk == "Moderate");
        if (advanced + moderate > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{advanced + moderate} of these switch off features some people use ({advanced} Advanced, {moderate} Moderate). Each description says what you lose.");
        }
        sb.AppendLine();
        sb.Append("Every change is recorded and can be undone one by one here or on the History tab.");
        if (Settings.Current.RestorePointBeforeApply && !_restorePointDone)
            sb.Append(" A System Restore point is created first.");
        if (!Ui.Confirm(sb.ToString(), "Apply privacy changes", "Apply", "Cancel")) return;

        _busy = true;
        UpdateButtons();
        ShowLog(clear: true);
        var failures = new List<string>();
        string restoreNote = "";
        try
        {
            // ---- restore point (once per run; carry on if it fails, but say so)
            if (Settings.Current.RestorePointBeforeApply && !_restorePointDone)
            {
                LogLine("Creating a System Restore point (this can take a minute)…");
                Status.Set("Creating a System Restore point…");
                (bool ok, string message) rp;
                try { rp = await Change.CreateRestorePointAsync("Winnow - before privacy changes"); }
                catch (Exception ex) { rp = (false, ex.Message); }
                if (rp.ok)
                {
                    _restorePointDone = true;
                    LogLine("  OK      restore point created");
                    Status.Set("Restore point created. Applying privacy changes…", StatusKind.Good);
                }
                else
                {
                    LogLine("  FAILED  restore point: " + Short(rp.message));
                    restoreNote = "The System Restore point could not be created (" + Short(rp.message) + "). The changes were still applied and each one can be undone from Winnow.";
                    Status.Set("Restore point failed - carrying on; every change can still be undone in Winnow.", StatusKind.Warn);
                }
            }

            int i = 0;
            foreach (var t in chosen)
            {
                i++;
                Status.Set($"Applying {i} of {chosen.Count}: {t.Title}");
                LogLine($"[{i}/{chosen.Count}] {t.Title}");
                await ApplyTweakAsync(t, failures);
            }
        }
        catch (Exception ex)
        {
            Log.Write("privacy apply: " + ex);
            failures.Add("Unexpected error: " + ex.Message);
            LogLine("  FAILED  unexpected error: " + Short(ex.Message));
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        LogLine("Checking the results…");
        await CheckStatesAsync();
        LogLine("Done.");

        const string after = "Some changes take effect after you sign out or restart (services, Recall, Start and taskbar settings). " +
                             "Edge picks up its policies when it is restarted.";
        if (failures.Count == 0)
        {
            Status.Set($"Applied {chosen.Count} privacy change{(chosen.Count == 1 ? "" : "s")}. Sign out or restart for all of them to take effect.", StatusKind.Good);
            Ui.Info($"Applied {chosen.Count} privacy change{(chosen.Count == 1 ? "" : "s")}.\n\n{after}" + (restoreNote.Length > 0 ? "\n\n" + restoreNote : ""), "Privacy changes applied");
        }
        else
        {
            Status.Set($"Applied with {failures.Count} problem{(failures.Count == 1 ? "" : "s")} - see the progress log.", StatusKind.Warn);
            var fb = new StringBuilder();
            fb.AppendLine($"{failures.Count} action{(failures.Count == 1 ? "" : "s")} did not work:");
            fb.AppendLine();
            foreach (var f in failures.Take(15)) fb.AppendLine("•  " + f);
            if (failures.Count > 15) fb.AppendLine($"…and {failures.Count - 15} more (see the progress log)");
            fb.AppendLine();
            fb.Append("Everything else was applied. " + after);
            if (restoreNote.Length > 0) fb.Append("\n\n" + restoreNote);
            Ui.Error(fb.ToString(), "Privacy changes");
        }
    }

    /// <summary>Apply one tweak through Change.*. Never throws; problems go into failures.</summary>
    async Task ApplyTweakAsync(PrivacyTweak t, List<string> failures)
    {
        string group = t.Group;

        foreach (var a in t.Reg)
        {
            try
            {
                (bool ok, string message) r = await Task.Run(() => Change.SetRegistry(a.Hive, a.Path, a.Name, a.Value, a.Kind, group,
                                                                t.Title + ": " + a.Target + " = " + a.ValueText));
                if (r.ok) LogLine($"  OK      {a.Target} = {a.ValueText} ({r.message})");
                else { LogLine($"  FAILED  {a.Target}: {Short(r.message)}"); failures.Add($"{t.Title}: {a.Target} - {Short(r.message)}"); }
            }
            catch (Exception ex) { LogLine($"  FAILED  {a.Target}: {Short(ex.Message)}"); failures.Add($"{t.Title}: {a.Target} - {Short(ex.Message)}"); }
        }

        foreach (var s in t.Services)
        {
            try
            {
                var r = await Change.SetServiceStartAsync(s.Name, s.Mode, group, $"{t.Title}: service {s.Name} start type {s.Mode}");
                if (r.ok) LogLine($"  OK      service {s.Name}: {r.message}");
                else { LogLine($"  FAILED  service {s.Name}: {Short(r.message)}"); failures.Add($"{t.Title}: service {s.Name} - {Short(r.message)}"); }
            }
            catch (Exception ex) { LogLine($"  FAILED  service {s.Name}: {Short(ex.Message)}"); failures.Add($"{t.Title}: service {s.Name} - {Short(ex.Message)}"); }
        }

        if (t.Tasks.Count > 0)
        {
            try
            {
                var r = await Change.SetTasksEnabledAsync(t.Tasks, false, group, t.Title + ": disable scheduled task");
                int already = t.Tasks.Count - r.changed - r.missing;
                LogLine($"  {(string.IsNullOrWhiteSpace(r.detail) ? "OK    " : "PARTLY")}  scheduled tasks: {r.changed} disabled" +
                        (already > 0 ? $", {already} already off or failed" : "") +
                        (r.missing > 0 ? $", {r.missing} not on this PC" : ""));
                if (!string.IsNullOrWhiteSpace(r.detail))
                    foreach (var line in r.detail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        // ERR|<task path>|<message>
                        var parts = line.Trim().Split('|');
                        string what = parts.Length >= 3 ? parts[1] + " - " + parts[2] : line.Trim();
                        LogLine("  FAILED  task " + Short(what));
                        failures.Add($"{t.Title}: task {Short(what)}");
                    }
            }
            catch (Exception ex) { LogLine($"  FAILED  scheduled tasks: {Short(ex.Message)}"); failures.Add($"{t.Title}: scheduled tasks - {Short(ex.Message)}"); }
        }

        foreach (var f in t.Firewall)
        {
            try
            {
                string prog = f.Program;
                if (!File.Exists(prog)) { LogLine($"  SKIP    {prog} is not on this PC"); continue; }
                if (_fwRules != null && _fwRules.Contains(f.RuleName)) { LogLine($"  OK      firewall rule \"{f.RuleName}\" already present"); continue; }
                var r = await Change.AddFirewallBlockAsync(f.RuleName, prog, null, group,
                                                           $"{t.Title}: block outbound for {Path.GetFileName(prog)}");
                if (r.ok) LogLine($"  OK      firewall: outbound blocked for {prog}");
                else { LogLine($"  FAILED  firewall rule for {Path.GetFileName(prog)}: {Short(r.message)}"); failures.Add($"{t.Title}: firewall rule for {Path.GetFileName(prog)} - {Short(r.message)}"); }
            }
            catch (Exception ex) { LogLine($"  FAILED  firewall: {Short(ex.Message)}"); failures.Add($"{t.Title}: firewall - {Short(ex.Message)}"); }
        }
    }

    // ------------------------------------------------------------ undo

    async Task UndoTweakAsync(PrivacyTweakRow row)
    {
        if (_busy || !Elevation.IsAdmin) return;
        var t = row.Tweak;
        int live = Journal.ForGroup(t.Group).Count(e => e.Revertible);
        if (live == 0)
        {
            Status.Set("Nothing to undo for " + t.Title + ".");
            RefreshUndoLinks();
            return;
        }
        if (!Ui.Confirm($"Undo \"{t.Title}\"?\n\nThis puts back {live} recorded change{(live == 1 ? "" : "s")} to how {(live == 1 ? "it was" : "they were")} before Winnow changed {(live == 1 ? "it" : "them")}.",
                        "Undo privacy change", "Undo", "Cancel"))
            return;

        _busy = true;
        UpdateButtons();
        ShowLog(clear: true);
        LogLine("Undo: " + t.Title);
        try
        {
            var (ok, failed, detail) = await Journal.RevertGroupAsync(t.Group);
            LogLine($"  {ok} change{(ok == 1 ? "" : "s")} put back" + (failed > 0 ? $", {failed} failed" : ""));
            if (!string.IsNullOrWhiteSpace(detail))
                foreach (var line in detail.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LogLine("  FAILED  " + Short(line));
            if (failed == 0) Status.Set($"Undone: {t.Title}. Some settings need a sign-out or restart to fully return.", StatusKind.Good);
            else
            {
                Status.Set($"Undo of {t.Title}: {failed} change{(failed == 1 ? "" : "s")} could not be put back.", StatusKind.Warn);
                Ui.Error($"{failed} change{(failed == 1 ? "" : "s")} could not be put back:\n\n{detail.Trim()}", "Undo");
            }
        }
        catch (Exception ex)
        {
            Log.Write("privacy undo: " + ex);
            Status.Set("Undo failed: " + ex.Message, StatusKind.Bad);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
        await CheckStatesAsync();
    }

    async Task UndoAllAsync()
    {
        if (_busy || !Elevation.IsAdmin) return;
        var live = Journal.Entries
            .Where(e => (e.Group ?? "").StartsWith("privacy.", StringComparison.Ordinal) && !e.Reverted && e.Revertible)
            .ToList();
        if (live.Count == 0)
        {
            Ui.Info("There are no privacy changes to undo.", "Undo privacy changes");
            return;
        }
        // Groups newest first, so later tweaks are taken off before earlier ones.
        var groups = live.GroupBy(e => e.Group).OrderByDescending(gr => gr.Max(e => e.Time)).Select(gr => gr.Key).ToList();
        if (!Ui.Confirm($"Undo all {live.Count} privacy change{(live.Count == 1 ? "" : "s")} Winnow has made ({groups.Count} tweak{(groups.Count == 1 ? "" : "s")})?\n\n" +
                        "Each setting, service and scheduled task is put back to how it was before Winnow changed it, and Winnow's firewall rules are removed.",
                        "Undo all privacy changes", "Undo all", "Cancel", danger: true))
            return;

        _busy = true;
        UpdateButtons();
        ShowLog(clear: true);
        int okTotal = 0, failedTotal = 0;
        var problems = new StringBuilder();
        try
        {
            foreach (var g in groups)
            {
                string title = PrivacyCatalog.Find(g.Substring("privacy.".Length))?.Title ?? g;
                LogLine("Undo: " + title);
                Status.Set("Undoing " + title + "…");
                var (ok, failed, detail) = await Journal.RevertGroupAsync(g);
                okTotal += ok;
                failedTotal += failed;
                LogLine($"  {ok} put back" + (failed > 0 ? $", {failed} failed" : ""));
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    problems.Append(detail);
                    foreach (var line in detail.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LogLine("  FAILED  " + Short(line));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("privacy undo all: " + ex);
            failedTotal++;
            problems.AppendLine("Unexpected error: " + ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        await CheckStatesAsync();
        if (failedTotal == 0)
        {
            Status.Set($"Undid {okTotal} privacy change{(okTotal == 1 ? "" : "s")}. Sign out or restart for all of them to take effect.", StatusKind.Good);
        }
        else
        {
            Status.Set($"Undid {okTotal}; {failedTotal} could not be put back.", StatusKind.Warn);
            Ui.Error($"{failedTotal} change{(failedTotal == 1 ? "" : "s")} could not be put back:\n\n{Short(problems.ToString())}", "Undo all privacy changes");
        }
    }

    void RestartElevated()
    {
        try
        {
            var args = Winnow.App.Args.Where(a => a != "--elevated" && a != "--no-elevate").ToArray();
            if (Elevation.RelaunchElevated(args)) Application.Current.Shutdown();
            else Status.Set("Windows did not start Winnow as administrator (the prompt may have been declined).", StatusKind.Warn);
        }
        catch (Exception ex)
        {
            Status.Set("Could not restart as administrator: " + ex.Message, StatusKind.Bad);
        }
    }

    // =================================================================== History tab

    UIElement BuildHistoryTab()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var fl = PrivacyUi.Para("Filter", "Dim", 12, new Thickness(0, 0, 6, 4));
        fl.VerticalAlignment = VerticalAlignment.Center;
        fl.TextWrapping = TextWrapping.NoWrap;
        bar.Children.Add(fl);
        _filter = new TextBox { Width = 220, Margin = new Thickness(0, 0, 10, 4), ToolTip = "Matches group, description, target, values and status" };
        _filter.TextChanged += (_, _) => { _histView?.Refresh(); UpdateHistCount(); };
        bar.Children.Add(_filter);
        _showUndone = new CheckBox { Content = "Show undone", IsChecked = false, Margin = new Thickness(0, 0, 14, 4), VerticalAlignment = VerticalAlignment.Center };
        _showUndone.Checked += (_, _) => { _histView?.Refresh(); UpdateHistCount(); };
        _showUndone.Unchecked += (_, _) => { _histView?.Refresh(); UpdateHistCount(); };
        bar.Children.Add(_showUndone);
        _undoSel = Ui.Button("Undo selected", (_, _) => Ui.Run(UndoSelectedAsync, "Undo"), null,
            "Put the selected changes back, newest first. Ctrl+click or Shift+click to select several.");
        _undoSel.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(_undoSel);
        _export = Ui.Button("Export history…", (_, _) => Ui.Run(ExportAsync, "Export history"), null,
            "Save every recorded change, including undone ones, as a CSV file.");
        _export.Margin = new Thickness(0, 0, 6, 4);
        bar.Children.Add(_export);
        g.Children.Add(bar);

        _grid = new DataGrid();
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Time", Binding = new Binding("Time") { StringFormat = "yyyy-MM-dd HH:mm:ss" },
            Width = new DataGridLength(140), SortDirection = System.ComponentModel.ListSortDirection.Descending,
        });
        _grid.Columns.Add(TextCol("Group", "Group", 150, false));
        _grid.Columns.Add(TextCol("Description", "Description", 2, true));
        _grid.Columns.Add(TextCol("Kind", "Kind", 70, false));
        _grid.Columns.Add(TextCol("Target", "Target", 2, true));
        _grid.Columns.Add(TextCol("Before → After", "BeforeAfter", 1.3, true));
        _grid.Columns.Add(TextCol("Status", "StatusText", 90, false));
        _grid.SelectionChanged += (_, _) => UpdateHistCount();
        Grid.SetRow(_grid, 1);
        g.Children.Add(_grid);

        _histCount = PrivacyUi.Para("", "Dim", 12, new Thickness(0, 6, 0, 0));
        Grid.SetRow(_histCount, 2);
        g.Children.Add(_histCount);
        return g;
    }

    static DataGridTextColumn TextCol(string header, string path, double width, bool star)
    {
        var cellStyle = new Style(typeof(TextBlock));
        cellStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        cellStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path)));
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = star ? new DataGridLength(width, DataGridLengthUnitType.Star) : new DataGridLength(width),
            ElementStyle = cellStyle,
        };
    }

    void RebuildHistory()
    {
        _historyDirty = false;
        try
        {
            var selected = new HashSet<string>(_grid.SelectedItems.OfType<PrivacyHistoryRow>().Select(h => h.Id));
            var rows = Journal.Entries.Select(e => new PrivacyHistoryRow(e)).ToList();
            var view = new ListCollectionView(rows);
            // keep the user's column sort if they chose one, otherwise newest first
            var sortCol = _grid.Columns.FirstOrDefault(c => c.SortDirection != null);
            string sortPath = sortCol == null ? "Time" : (sortCol.SortMemberPath ?? "Time");
            if (string.IsNullOrEmpty(sortPath)) sortPath = "Time";
            var dir = sortCol?.SortDirection ?? System.ComponentModel.ListSortDirection.Descending;
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(sortPath, dir));
            view.Filter = HistoryFilter;
            _histView = view;
            _grid.ItemsSource = view;
            // Replacing ItemsSource clears the header sort arrows; show the sort the view actually has.
            foreach (var c in _grid.Columns) c.SortDirection = null;
            var match = _grid.Columns.FirstOrDefault(c => string.Equals(c.SortMemberPath, sortPath, StringComparison.Ordinal));
            if (match != null) match.SortDirection = dir;
            if (selected.Count > 0)
                foreach (var row in rows.Where(h => selected.Contains(h.Id))) _grid.SelectedItems.Add(row);
        }
        catch (Exception ex)
        {
            Log.Write("privacy history: " + ex);
            Status.Set("Could not show the change history: " + ex.Message, StatusKind.Bad);
        }
        UpdateHistCount();
    }

    bool HistoryFilter(object o)
    {
        if (o is not PrivacyHistoryRow r) return false;
        if (r.IsUndone && _showUndone.IsChecked != true) return false;
        return r.Matches((_filter.Text ?? "").Trim().ToLowerInvariant());
    }

    void UpdateHistCount()
    {
        if (_histCount == null) return;
        var all = Journal.Entries;
        int live = all.Count(e => !e.Reverted && e.Revertible);
        int shown = _histView?.Count ?? 0;
        int sel = _grid?.SelectedItems.Count ?? 0;
        _histCount.Text = $"{shown} shown of {all.Count} recorded change{(all.Count == 1 ? "" : "s")} · {live} live (can be undone)" +
                          (sel > 0 ? $" · {sel} selected" : "") +
                          " · Changes from every page (privacy, startup, services, firewall, apps) are listed here.";
    }

    async Task UndoSelectedAsync()
    {
        if (_busy || !Elevation.IsAdmin) return;
        var picked = _grid.SelectedItems.OfType<PrivacyHistoryRow>().ToList();
        var todo = picked.Where(r => r.CanUndo).Select(r => r.Entry).OrderByDescending(e => e.Time).ToList();
        if (todo.Count == 0)
        {
            Status.Set(picked.Count == 0 ? "Select one or more changes first." : "None of the selected changes can be undone (already undone, or not undoable).", StatusKind.Warn);
            return;
        }
        int skipped = picked.Count - todo.Count;
        if (!Ui.Confirm($"Undo {todo.Count} change{(todo.Count == 1 ? "" : "s")}, newest first?\n\nEach is put back to the value recorded before Winnow changed it." +
                        (skipped > 0 ? $"\n\n{skipped} selected row{(skipped == 1 ? " is" : "s are")} already undone or not undoable and will be skipped." : ""),
                        "Undo changes", "Undo", "Cancel"))
            return;

        _busy = true;
        UpdateButtons();
        _undoSel.IsEnabled = false;
        int ok = 0;
        var problems = new List<string>();
        try
        {
            foreach (var e in todo)
            {
                Status.Set("Undoing " + e.Description + "…");
                (bool ok, string message) r;
                try { r = await Journal.RevertAsync(e); }
                catch (Exception ex) { r = (false, ex.Message); }
                if (r.ok) ok++;
                else problems.Add(e.Description + ": " + r.message);
            }
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
        RebuildHistory();
        if (problems.Count == 0)
            Status.Set($"Undid {ok} change{(ok == 1 ? "" : "s")}. Some need a sign-out or restart to fully return.", StatusKind.Good);
        else
        {
            Status.Set($"Undid {ok}; {problems.Count} could not be put back.", StatusKind.Warn);
            Ui.Error(string.Join("\n", problems.Take(15).Select(p => "•  " + Short(p))) + (problems.Count > 15 ? $"\n…and {problems.Count - 15} more" : ""),
                     "Some changes could not be undone");
        }
        await CheckStatesAsync();
    }

    async Task ExportAsync()
    {
        string path = Ui.SaveFile($"winnow-change-history-{DateTime.Now:yyyyMMdd-HHmm}.csv", "CSV files|*.csv");
        if (path == null) return;
        var sb = new StringBuilder();
        sb.AppendLine("Time,Group,Description,Kind,Target,Before,After,Status");
        foreach (var e in Journal.Entries.OrderBy(x => x.Time))
        {
            string before = e.Kind == "Registry" && !e.BeforeExists ? "(not set)" : (e.BeforeData ?? "");
            string status = !e.Revertible ? "Not undoable" : e.Reverted ? "Undone" : "Live";
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(e.Time.ToString("yyyy-MM-dd HH:mm:ss")), Csv(e.Group), Csv(e.Description), Csv(e.Kind),
                Csv(PrivacyHistoryRow.TargetOf(e)), Csv(before.Replace("\u0001", "; ")), Csv((e.AfterData ?? "").Replace("\u0001", "; ")), Csv(status),
            }));
        }
        try
        {
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
            Status.Set("Saved change history to " + path, StatusKind.Good);
        }
        catch (Exception ex)
        {
            Status.Set("Could not save the history: " + ex.Message, StatusKind.Bad);
        }
    }

    static string Csv(string s)
    {
        s ??= "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // =================================================================== journal / IView

    void OnJournalChanged()
    {
        // May arrive on any thread and in bursts while applying; coalesce onto the UI thread.
        if (Interlocked.Exchange(ref _journalPending, 1) == 1) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _journalPending, 0);
                try
                {
                    RefreshUndoLinks();
                    if (_shown && _tabs.SelectedItem == _historyTab) RebuildHistory();
                    else _historyDirty = true;
                }
                catch (Exception ex) { Log.Write("privacy journal refresh: " + ex.Message); }
            }), DispatcherPriority.Background);
        }
        catch { Interlocked.Exchange(ref _journalPending, 0); }
    }

    public void OnShown()
    {
        _shown = true;
        RefreshUndoLinks();
        if (_tabs.SelectedItem == _historyTab && _historyDirty) RebuildHistory();
        Ui.Run(CheckStatesAsync, "Privacy check");
    }

    public void OnHidden() => _shown = false;

    public void Receive(object arg)
    {
        if (arg is string s && s.Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            _tabs.SelectedItem = _historyTab;
            RebuildHistory();
        }
    }
}
