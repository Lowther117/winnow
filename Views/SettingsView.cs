using System.Windows;
using System.Windows.Controls;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

public sealed class SettingsView : UserControl, IView
{
    readonly TextBlock _saveDir = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _sensorState = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button _pawnBtn;
    bool _pawnBusy;            // an install is running: status refreshes must not re-enable the button

    public SettingsView()
    {
        var root = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(Ui.Header("Settings", "How Winnow looks and measures. Saved to winnow-settings.json beside Winnow.exe."));

        // ---- appearance
        var look = new StackPanel();
        look.Children.Add(Ui.Text("Appearance", "H2"));
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (key, label) in new[] { ("phosphor", "Phosphor purple"), ("dark", "Dark"), ("light", "Light") })
        {
            var rb = new RadioButton { Content = label, GroupName = "theme", IsChecked = Settings.Current.Theme == key, Tag = key };
            rb.Checked += (s, _) =>
            {
                Settings.Current.Theme = (string)((RadioButton)s).Tag;
                Settings.Save();
                Palette.Apply(Settings.Current.Theme);
            };
            themes.Children.Add(rb);
        }
        look.Children.Add(themes);
        look.Children.Add(Check("Phosphor glow and scanlines", Settings.Current.Glow, v =>
        {
            Settings.Current.Glow = v; Settings.Save(); Palette.Apply(Settings.Current.Theme);
        }));
        look.Children.Add(Check("Smooth scrolling graphs (redraw at display refresh rate between samples)", Settings.Current.SmoothGraphs,
            v => { Settings.Current.SmoothGraphs = v; Settings.Save(); }));
        look.Children.Add(Ui.Text("Ctrl+D cycles the three themes. Ctrl+1…9 jumps to a page.", "Sub"));
        root.Children.Add(Ui.Card(look));

        // ---- measuring
        var meas = new StackPanel();
        meas.Children.Add(Ui.Text("Measuring", "H2"));
        var rate = new StackPanel { Orientation = Orientation.Horizontal };
        rate.Children.Add(Ui.Text("Update every  ", null));
        foreach (var ms in new[] { 500, 1000, 2000 })
        {
            var rb = new RadioButton { Content = ms == 500 ? "0.5 s" : (ms / 1000) + " s", GroupName = "rate", IsChecked = Settings.Current.RefreshMs == ms, Tag = ms };
            rb.Checked += (s, _) =>
            {
                Settings.Current.RefreshMs = (int)((RadioButton)s).Tag;
                Settings.Save();
                Sampler.ResizeHistory();
            };
            rate.Children.Add(rb);
        }
        meas.Children.Add(rate);

        var rec = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        rec.Children.Add(Ui.Text("Flight Recorder keeps the last  ", null));
        var minutes = new ComboBox { Width = 90 };
        foreach (var m in new[] { 15, 30, 60, 120, 240 }) minutes.Items.Add(new ComboBoxItem { Content = m + " min", Tag = m });
        minutes.SelectedItem = minutes.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == Settings.Current.RecorderMinutes)
                               ?? minutes.Items[2];
        minutes.SelectionChanged += (_, _) =>
        {
            if (minutes.SelectedItem is ComboBoxItem { Tag: int m })
            {
                Settings.Current.RecorderMinutes = m; Settings.Save(); Sampler.ResizeHistory();
            }
        };
        rec.Children.Add(minutes);
        rec.Children.Add(Ui.Text("  in memory", null));
        meas.Children.Add(rec);
        meas.Children.Add(Check("Include Winnow itself in process lists", Settings.Current.ShowSelf,
            v => { Settings.Current.ShowSelf = v; Settings.Save(); }));
        root.Children.Add(Ui.Card(meas));

        // ---- sensors
        var sens = new StackPanel();
        sens.Children.Add(Ui.Text("Hardware sensors", "H2"));
        sens.Children.Add(Check("Read temperatures, clocks, fans and power (LibreHardwareMonitor)", Settings.Current.SensorsEnabled, v =>
        {
            Settings.Current.SensorsEnabled = v; Settings.Save();
            if (v) Sensors.Start(); else Sensors.Stop();
            RefreshSensorState();
        }));
        sens.Children.Add(Check("Install the PawnIO driver automatically when it is missing", Settings.Current.PawnIoAutoInstall,
            v => { Settings.Current.PawnIoAutoInstall = v; Settings.Save(); }));
        var sub = Ui.Text("CPU temperatures and power need ring-0 access. Winnow uses PawnIO - a Microsoft-signed driver that works with " +
                          "Memory Integrity and is not flagged by Defender - instead of the old WinRing0 driver.", "Sub");
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 4, 0, 8);
        sens.Children.Add(sub);
        sens.Children.Add(_sensorState);
        _pawnBtn = Ui.Button("Install PawnIO now", (_, _) => Ui.Run(InstallPawn, "PawnIO install"));
        _pawnBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _pawnBtn.Margin = new Thickness(0, 8, 0, 0);
        sens.Children.Add(_pawnBtn);
        root.Children.Add(Ui.Card(sens));

        // ---- changes
        var chg = new StackPanel();
        chg.Children.Add(Ui.Text("Changes", "H2"));
        chg.Children.Add(Check("Create a System Restore point before applying privacy changes (one per run)", Settings.Current.RestorePointBeforeApply,
            v => { Settings.Current.RestorePointBeforeApply = v; Settings.Save(); }));
        var hist = Ui.Button("Open change history…", (_, _) => Nav.Go("privacy", "history"));
        hist.HorizontalAlignment = HorizontalAlignment.Left;
        hist.Margin = new Thickness(0, 8, 0, 0);
        chg.Children.Add(hist);
        root.Children.Add(Ui.Card(chg));

        // ---- output
        var outp = new StackPanel();
        outp.Children.Add(Ui.Text("Where saved files go", "H2"));
        var row = new DockPanel();
        var choose = Ui.Button("Change…", (_, _) =>
        {
            var f = Ui.PickFolder();
            if (f != null) { Settings.Current.SaveDir = f; Settings.Save(); UpdateSaveDir(); }
        });
        var reset = Ui.Button("Reset to Downloads", (_, _) => { Settings.Current.SaveDir = ""; Settings.Save(); UpdateSaveDir(); });
        DockPanel.SetDock(reset, Dock.Right);
        DockPanel.SetDock(choose, Dock.Right);
        row.Children.Add(reset);
        row.Children.Add(choose);
        row.Children.Add(_saveDir);
        outp.Children.Add(row);
        var open = Ui.Button("Open Winnow's own folder (settings, change journal, logs)", (_, _) => Shell.Open(Paths.AppDir));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 10, 0, 0);
        outp.Children.Add(open);
        root.Children.Add(Ui.Card(outp));

        root.Children.Add(Ui.Text($"Winnow {typeof(SettingsView).Assembly.GetName().Version?.ToString(3)} · {WinVer.Summary}", "Sub"));

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UpdateSaveDir();
        RefreshSensorState();
        Sensors.StatusChanged += () => Dispatcher.BeginInvoke(new Action(RefreshSensorState));
    }

    static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    void UpdateSaveDir() => _saveDir.Text = Paths.DefaultSaveDir();

    void RefreshSensorState()
    {
        var pawn = Sensors.PawnIoState();
        string state = pawn switch
        {
            Sensors.PawnState.Installed => "installed",
            Sensors.PawnState.Broken => "broken - registered, but its driver file is missing",
            _ => "not installed",
        };
        _sensorState.Text = $"Sensors: {Sensors.Status}\nPawnIO driver: {state}" +
                            (Elevation.IsAdmin ? "" : "\n(Run Winnow as administrator to install or repair PawnIO.)") +
                            $"\nPer-process network (ETW): {NetEtw.Status}";
        if (_pawnBtn != null && !_pawnBusy)
        {
            // always available to an administrator: Install when missing, Repair when broken, Reinstall when fine
            _pawnBtn.Content = pawn switch
            {
                Sensors.PawnState.Installed => "Reinstall PawnIO",
                Sensors.PawnState.Broken => "Repair PawnIO",
                _ => "Install PawnIO now",
            };
            _pawnBtn.IsEnabled = Elevation.IsAdmin;
        }
    }

    async Task InstallPawn()
    {
        bool force = Sensors.PawnIoState() == Sensors.PawnState.Installed;
        _pawnBusy = true;
        try
        {
            await Ui.Busy(_pawnBtn, async () =>
            {
                var (ok, msg) = await Sensors.InstallPawnIoAsync(force);
                Status.Set(ok ? "PawnIO " + msg + " - restarting sensors" : "PawnIO: " + msg, ok ? StatusKind.Good : StatusKind.Bad);
                if (ok) await Task.Run(Sensors.Restart);
                else if (Settings.Current.SensorsEnabled && !Sensors.Running) Sensors.Start();   // a repair stopped them first
            }, force ? "Reinstalling…" : "Installing…");
        }
        finally { _pawnBusy = false; }
        RefreshSensorState();
    }

    public void OnShown() => RefreshSensorState();
    public void OnHidden() { }
}
