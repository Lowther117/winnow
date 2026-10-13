using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

/// <summary>
/// "Winnow.exe uitest": opens the real window, visits every page for a few
/// seconds of live sampling, saves a screenshot of each (uitest-*.png beside
/// the exe) and writes winnow-uitest.txt listing any page that threw. Used by
/// the GitHub build to catch runtime faults no compiler can see. Changes
/// nothing on the system: it only looks.
/// </summary>
public static class UiTest
{
    static readonly string[] Keys =
    {
        "summary", "performance", "processes", "connections", "recorder", "startup", "services",
        "apps", "disk", "install", "privacy", "sysinfo", "users", "drivers", "settings",
    };

    public static void Run(Application app)
    {
        var report = new StringBuilder();
        report.AppendLine("Winnow UI test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try { if (File.Exists(Paths.CrashLog)) File.Delete(Paths.CrashLog); } catch { }

        MainWindow w;
        try
        {
            w = new MainWindow { Width = 1280, Height = 820, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            app.MainWindow = w;
            w.Show();
        }
        catch (Exception ex)
        {
            report.AppendLine("FAIL  main window: " + ex);
            Finish(app, report, 1);
            return;
        }

        var steps = new List<(string label, Action act, int waitMs)>();
        // make some traffic so per-process network and DNS names have something to show
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            for (int n = 0; n < 6; n++) { try { await http.GetStringAsync("https://github.com/"); } catch { } await Task.Delay(1500); }
        });
        foreach (var k in Keys)
        {
            string key = k;
            steps.Add((key, () => w.Go(key, null), key is "apps" or "install" or "drivers" or "sysinfo" or "privacy" or "startup" or "services" ? 7000 : 3500));
        }
        foreach (var theme in new[] { "dark", "light", "phosphor" })
        {
            string t = theme;
            steps.Add(("summary-" + t, () => { Palette.Apply(t); w.Go("summary", null); }, 2500));
        }
        steps.Add(("performance-cpu-cores", () => w.Go("performance", "cpu"), 2500));
        steps.Add(("performance-thermals", () => w.Go("performance", "thermals"), 2500));
        steps.Add(("privacy-history", () => w.Go("privacy", "history"), 2500));

        int i = 0;
        int failures = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        Action next = null;
        next = () =>
        {
            if (i >= steps.Count)
            {
                report.AppendLine($"ETW: {NetEtw.Status}; events {NetEtw.EventsSeen}, network {NetEtw.NetEventsSeen}, dns {NetEtw.DnsEventsSeen}, parse failures {NetEtw.ParseFailures} {NetEtw.LastUnparsed}");
                var me = Sampler.Latest?.Processes.FirstOrDefault(p => p.Pid == Environment.ProcessId);
                report.AppendLine($"Winnow's own network rate in the last sample: {Fmt.Rate(me?.NetBps)}; host for a github.com IP: {string.Join(", ", System.Net.Dns.GetHostAddresses("github.com").Select(a => a.ToString() + "=" + (NetEtw.HostFor(a.ToString()) ?? "none")))}");
                string crash = "";
                try { if (File.Exists(Paths.CrashLog)) crash = File.ReadAllText(Paths.CrashLog); } catch { }
                if (crash.Length > 0)
                {
                    failures++;
                    report.AppendLine();
                    report.AppendLine("---- winnow-crash.log ----");
                    report.AppendLine(crash);
                }
                Finish(app, report, failures == 0 ? 0 : 1);
                return;
            }
            var (label, act, wait) = steps[i];
            try { act(); report.AppendLine("ok    opened " + label); }
            catch (Exception ex) { failures++; report.AppendLine("FAIL  " + label + ": " + ex); }
            timer.Interval = TimeSpan.FromMilliseconds(wait);
            timer.Tick += Shot;
            timer.Start();

            void Shot(object s, EventArgs e)
            {
                timer.Stop();
                timer.Tick -= Shot;
                try { Save(w, Path.Combine(Paths.AppDir, $"uitest-{i + 1:00}-{label}.png")); }
                catch (Exception ex) { failures++; report.AppendLine("FAIL  screenshot " + label + ": " + ex.Message); }
                i++;
                next();
            }
        };
        next();
    }

    static void Save(Window w, string path)
    {
        if (w.Content is not FrameworkElement root) return;
        int width = (int)Math.Max(1, root.ActualWidth), height = (int)Math.Max(1, root.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Palette.Brush(Palette.Current.Bg), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void Finish(Application app, StringBuilder report, int code)
    {
        report.AppendLine(code == 0 ? "RESULT: all pages opened" : "RESULT: PROBLEMS FOUND");
        try { File.WriteAllText(Path.Combine(Paths.AppDir, "winnow-uitest.txt"), report.ToString()); } catch { }
        app.Shutdown(code);
    }
}
