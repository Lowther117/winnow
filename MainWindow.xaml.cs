using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Winnow.Controls;
using Winnow.Core;
using Winnow.Theme;
using Winnow.Views;

namespace Winnow;

public partial class MainWindow : Window
{
    sealed record Page(string Key, string Label, string Glyph, string Section, Func<FrameworkElement> Make);

    // The sidebar, in TMOG's spirit: monitor first, then manage, privacy, system.
    static readonly Page[] Pages =
    {
        new("summary",     "Summary",          "", "MONITOR", () => new SummaryView()),
        new("performance", "Performance",      "", "MONITOR", () => new PerformanceView()),
        new("processes",   "Processes",        "", "MONITOR", () => new ProcessesView()),
        new("connections", "Connections",      "", "MONITOR", () => new ConnectionsView()),
        new("recorder",    "Flight Recorder",  "", "MONITOR", () => new FlightRecorderView()),
        new("startup",     "Startup apps",     "", "MANAGE",  () => new StartupView()),
        new("services",    "Services",         "", "MANAGE",  () => new ServicesView()),
        new("apps",        "Installed Apps",   "", "MANAGE",  () => new InstalledAppsView()),
        new("disk",        "Disk Space",       "", "MANAGE",  () => new DiskSpaceView()),
        new("install",     "Install",          "", "MANAGE",  () => new InstallView()),
        new("privacy",     "Privacy",          "", "PRIVACY", () => new PrivacyView()),
        new("sysinfo",     "System Info",      "", "SYSTEM",  () => new SystemInfoView()),
        new("users",       "Users",            "", "SYSTEM",  () => new UsersView()),
        new("drivers",     "Drivers",          "", "SYSTEM",  () => new DriversView()),
        new("settings",    "Settings",         "", "SYSTEM",  () => new SettingsView()),
    };

    readonly Dictionary<string, FrameworkElement> _made = new();
    IView _current;
    string _currentKey;
    readonly Meter _miniCpu = new() { LevelColors = true, Height = 6 };
    readonly Meter _miniMem = new() { LevelColors = true, Height = 6 };
    bool _navigating;

    public MainWindow()
    {
        InitializeComponent();
        MiniCpuHost.Child = _miniCpu;
        MiniMemHost.Child = _miniMem;
        BrandSub.Text = $"{(WinVer.IsWindows11 ? "Windows 11" : "Windows")} {WinVer.DisplayVersion} · {WinVer.EditionName}";

        BuildNav();
        RestoreWindowBounds();
        SourceInitialized += (_, _) => ApplyTitleBar(this);
        Palette.Changed += OnThemeChanged;
        OnThemeChanged();

        Status.Changed += (text, kind) =>
        {
            StatusText.Text = text;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty,
                kind switch { StatusKind.Good => "Good", StatusKind.Warn => "Warn", StatusKind.Bad => "Bad", _ => "Dim" });
        };
        Nav.Handler = Go;
        Sampler.Tick += OnTick;
        Sensors.StatusChanged += () => Dispatcher.BeginInvoke(new Action(UpdateChips));
        UpdateChips();

        PreviewKeyDown += OnKeys;
        Closing += (_, _) => SaveWindowBounds();

        string start = Pages.Any(p => p.Key == Settings.Current.StartView) ? Settings.Current.StartView : "summary";
        Go(start, null);

        if (!Elevation.IsAdmin)
            Status.Set("Running without administrator rights - changes, sensors and per-process network are limited. Restart Winnow and accept the prompt for everything.", StatusKind.Warn);
    }

    // ------------------------------------------------------------ navigation

    void BuildNav()
    {
        string section = null;
        foreach (var p in Pages)
        {
            if (p.Section != section)
            {
                section = p.Section;
                var head = new ListBoxItem
                {
                    Content = new TextBlock { Text = section, Style = (Style)FindResource("Label"), Margin = new Thickness(6, section == "MONITOR" ? 2 : 12, 0, 2) },
                    IsEnabled = false, Focusable = false, IsHitTestVisible = false,
                };
                NavList.Items.Add(head);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock { Text = p.Glyph, Style = (Style)FindResource("Icon"), Width = 26, FontSize = 15 };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = p.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            NavList.Items.Add(new ListBoxItem { Content = row, Tag = p.Key, Padding = new Thickness(10, 7, 8, 7) });
        }
    }

    void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating) return;
        if (NavList.SelectedItem is ListBoxItem { Tag: string key }) Go(key, null);
    }

    /// <summary>Show a page by key, passing an optional argument to it.</summary>
    public void Go(string key, object arg)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key);
        if (page == null) return;
        if (_currentKey != key)
        {
            try { _current?.OnHidden(); } catch (Exception ex) { Log.Write("OnHidden: " + ex); }
            if (!_made.TryGetValue(key, out var el))
            {
                try { el = page.Make(); }
                catch (Exception ex)
                {
                    Log.Crash("open page " + key, ex);
                    el = new TextBlock { Text = $"This page failed to open: {ex.Message}\n\nDetails are in winnow-crash.log.", TextWrapping = TextWrapping.Wrap };
                }
                _made[key] = el;
            }
            Host.Content = el;
            _current = el as IView;
            _currentKey = key;
            try { _current?.OnShown(); } catch (Exception ex) { Log.Crash("OnShown " + key, ex); }

            _navigating = true;
            foreach (var item in NavList.Items.OfType<ListBoxItem>())
                if (item.Tag as string == key) { NavList.SelectedItem = item; break; }
            _navigating = false;
        }
        if (arg != null)
        {
            try { _current?.Receive(arg); } catch (Exception ex) { Log.Write("Receive: " + ex); }
        }
    }

    // ------------------------------------------------------------ live readout

    void OnTick(Snapshot s)
    {
        _miniCpu.Value = s.CpuTotal / 100.0;
        MiniCpu.Text = Fmt.Pct(s.CpuTotal);
        _miniMem.Value = s.MemLoadPct / 100.0;
        MiniMem.Text = Fmt.Pct(s.MemLoadPct);
        MiniTemp.Text = s.CpuTempC.HasValue || s.GpuTempC.HasValue
            ? $"CPU {Fmt.Temp(s.CpuTempC)}  GPU {Fmt.Temp(s.GpuTempC)}"
            : "unavailable";
        StatusRight.Text = $"{s.ProcessCount} processes · {s.ThreadCount:N0} threads · up {Fmt.Duration(s.Uptime)}";
    }

    void UpdateChips()
    {
        Chips.Children.Clear();
        AddChip(Elevation.IsAdmin ? "ADMIN" : "LIMITED", Elevation.IsAdmin ? "Good" : "Warn",
            Elevation.IsAdmin ? "Running as administrator" : "Not elevated - most changes are unavailable");
        bool sensorsOk = Sensors.Status == "OK";
        AddChip("SENSORS", sensorsOk ? "Good" : Settings.Current.SensorsEnabled ? "Warn" : "Dim", "Sensors: " + Sensors.Status);
        AddChip("NET", NetEtw.Running ? "Good" : "Warn", "Per-process network: " + NetEtw.Status);
    }

    void AddChip(string text, string colorKey, string tip)
    {
        var tb = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold };
        tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        var b = new Border { Child = tb, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 5, 5), ToolTip = tip };
        Chips.Children.Add(b);
    }

    // ------------------------------------------------------------ theme

    void OnThemeChanged()
    {
        var p = Palette.Current;
        BrandGlow.Color = p.Accent;
        BrandGlow.BlurRadius = p.Phosphor && Settings.Current.Glow ? 14 : 0;
        BrandGlow.Opacity = p.Phosphor && Settings.Current.Glow ? 0.85 : 0;
        Scanlines.Visibility = p.Phosphor && Settings.Current.Glow ? Visibility.Visible : Visibility.Collapsed;
        ApplyTitleBar(this);
        foreach (Window w in Application.Current.Windows) if (w != this) ApplyTitleBar(w);
    }

    /// <summary>Colour the Windows 11 title bar to match the theme.</summary>
    public static void ApplyTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var p = Palette.Current;
            int dark = p.IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            int caption = p.Sidebar.R | (p.Sidebar.G << 8) | (p.Sidebar.B << 16);   // COLORREF 0x00BBGGRR
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, 4);
            int text = p.Text.R | (p.Text.G << 8) | (p.Text.B << 16);
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_TEXT_COLOR, ref text, 4);
            int border = p.Border.R | (p.Border.G << 8) | (p.Border.B << 16);
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, 4);
        }
        catch { }
    }

    // ------------------------------------------------------------ keys

    void OnKeys(object sender, KeyEventArgs e)
    {
        if (Ui.Ctrl(e, Key.D) && Keyboard.FocusedElement is not TextBox)
        {
            Settings.Current.Theme = Palette.NextName(Palette.Current.Name);
            Settings.Save();
            Palette.Apply(Settings.Current.Theme);
            Status.Set("Theme: " + Settings.Current.Theme);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int i = e.Key - Key.D1;
            if (i < Pages.Length) { Go(Pages[i].Key, null); e.Handled = true; }
        }
    }

    // ------------------------------------------------------------ window bounds

    void RestoreWindowBounds()
    {
        var b = Settings.Current.Window;
        if (b is not { Length: 4 }) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(b[0], b[1], Math.Max(MinWidth, b[2]), Math.Max(MinHeight, b[3]));
        if (!vs.IntersectsWith(r)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
    }

    void SaveWindowBounds()
    {
        if (WindowState == WindowState.Normal)
            Settings.Current.Window = new[] { Left, Top, Width, Height };
        Settings.Current.StartView = _currentKey ?? "summary";
        Settings.Save();
    }
}
