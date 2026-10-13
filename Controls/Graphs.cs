using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Controls;

/// <summary>
/// Shared frame clock. Controls that animate subscribe while they are on
/// screen; nothing runs when nothing is visible.
/// </summary>
public static class FrameClock
{
    static readonly HashSet<FrameworkElement> _subs = new();
    static bool _hooked;

    public static void Add(FrameworkElement e)
    {
        if (!_subs.Add(e) || _hooked) return;
        CompositionTarget.Rendering += OnFrame;
        _hooked = true;
    }

    public static void Remove(FrameworkElement e)
    {
        _subs.Remove(e);
        if (_subs.Count == 0 && _hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
    }

    static void OnFrame(object sender, EventArgs e)
    {
        foreach (var el in _subs.ToList())
            if (el is IAnimated a && a.WantsFrame) el.InvalidateVisual();
    }

    /// <summary>Resolve a palette colour by key: Graph1..4, Accent, Good, Warn, Bad, PCore, ECore, Dim, Text.</summary>
    public static Color ColorFor(string key)
    {
        var p = Palette.Current;
        return key switch
        {
            "Graph1" => p.Graph1, "Graph2" => p.Graph2, "Graph3" => p.Graph3, "Graph4" => p.Graph4,
            "Accent" => p.Accent, "Good" => p.Good, "Warn" => p.Warn, "Bad" => p.Bad,
            "PCore" => p.PCore, "ECore" => p.ECore, "Dim" => p.Dim, "Text" => p.Text,
            _ => p.Graph1
        };
    }
}

internal interface IAnimated { bool WantsFrame { get; } }

public sealed class GraphSeries
{
    public string ColorKey = "Graph1";
    public bool Fill = true;
    public double Thickness = 1.6;
    public string Name = "";
    internal readonly List<double> Data = new();
}

/// <summary>
/// A scrolling history graph drawn directly in OnRender - no chart library.
/// NaN values leave a gap (a stopped measurement is a gap, never a smoothed
/// line). With smooth graphs on, the trace glides left between samples at
/// display refresh rate; only the time axis moves, the values are never
/// interpolated.
/// </summary>
public class Graph : FrameworkElement, IAnimated
{
    public int Capacity { get; set; } = 60;
    public double Max { get; set; } = 100;
    public bool AutoMax { get; set; }
    public double AutoFloor { get; set; } = 1;
    public bool ShowGrid { get; set; } = true;
    public bool ShowFrame { get; set; } = true;
    public int GridLines { get; set; } = 4;
    public List<GraphSeries> Series { get; } = new();

    long _pushes;
    long _lastPushTicks;

    public Graph()
    {
        SnapsToDevicePixels = true;
        Loaded += (_, _) => { Hook(); Palette.Changed += OnPalette; };
        Unloaded += (_, _) => { FrameClock.Remove(this); Palette.Changed -= OnPalette; };
        IsVisibleChanged += (_, _) => Hook();
    }

    void OnPalette() => InvalidateVisual();

    void Hook()
    {
        if (IsVisible && Settings.Current.SmoothGraphs) FrameClock.Add(this);
        else FrameClock.Remove(this);
    }

    public bool WantsFrame => _pushes > 1 && Settings.Current.SmoothGraphs;

    public GraphSeries AddSeries(string colorKey = "Graph1", bool fill = true, double thickness = 1.6, string name = "")
    {
        var s = new GraphSeries { ColorKey = colorKey, Fill = fill, Thickness = thickness, Name = name };
        Series.Add(s);
        return s;
    }

    /// <summary>Add one value per series (NaN = no reading). Call once per sampler tick.</summary>
    public void Push(params double[] values)
    {
        for (int i = 0; i < Series.Count; i++)
        {
            var d = Series[i].Data;
            d.Add(i < values.Length ? values[i] : double.NaN);
            while (d.Count > Capacity + 2) d.RemoveAt(0);
        }
        _pushes++;
        _lastPushTicks = Environment.TickCount64;
        InvalidateVisual();
    }

    /// <summary>Load history without animating (e.g. when a view opens).</summary>
    public void Seed(IEnumerable<double[]> rows)
    {
        foreach (var s in Series) s.Data.Clear();
        foreach (var r in rows)
            for (int i = 0; i < Series.Count; i++)
            {
                Series[i].Data.Add(i < r.Length ? r[i] : double.NaN);
                if (Series[i].Data.Count > Capacity + 2) Series[i].Data.RemoveAt(0);
            }
        _pushes += Series.Count > 0 ? Series[0].Data.Count : 0;
        _lastPushTicks = Environment.TickCount64 - Settings.Current.RefreshMs;
        InvalidateVisual();
    }

    public void Clear()
    {
        foreach (var s in Series) s.Data.Clear();
        _pushes = 0;
        InvalidateVisual();
    }

    /// <summary>Most recent value of a series, or NaN.</summary>
    public double Last(int series = 0) =>
        series < Series.Count && Series[series].Data.Count > 0 ? Series[series].Data[^1] : double.NaN;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        var p = Palette.Current;
        var rect = new Rect(0, 0, w, h);

        dc.DrawRectangle(Palette.Brush(p.Code), null, rect);
        dc.PushClip(new RectangleGeometry(rect));

        double max = Max;
        if (AutoMax)
        {
            double m = AutoFloor;
            foreach (var s in Series) foreach (var v in s.Data) if (!double.IsNaN(v) && v > m) m = v;
            max = NiceCeiling(m * 1.1);
        }
        if (max <= 0) max = 1;

        double step = w / Math.Max(1, Capacity - 1);
        double frac = 0;
        if (Settings.Current.SmoothGraphs && _pushes > 1)
            frac = Math.Clamp((Environment.TickCount64 - _lastPushTicks) / (double)Math.Max(100, Settings.Current.RefreshMs), 0, 1);

        if (ShowGrid)
        {
            var gridPen = new Pen(Palette.Brush(p.Grid), 1);
            gridPen.Freeze();
            for (int i = 1; i < GridLines; i++)
            {
                double y = Math.Round(h * i / GridLines) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
            // vertical lines move with the data, every 10 samples
            const int every = 10;
            double phase = (_pushes % every + frac) * step;
            for (double x = w - phase; x > -step * every; x -= step * every)
            {
                double xx = Math.Round(x) + 0.5;
                if (xx >= 0 && xx <= w) dc.DrawLine(gridPen, new Point(xx, 0), new Point(xx, h));
            }
        }

        bool glow = p.Phosphor && Settings.Current.Glow;
        foreach (var s in Series)
        {
            if (s.Data.Count == 0) continue;
            var c = FrameClock.ColorFor(s.ColorKey);
            // gather contiguous runs (newest at the right edge)
            var runs = new List<List<Point>>();
            List<Point> cur = null;
            int n = s.Data.Count;
            for (int i = 0; i < n; i++)
            {
                int age = n - 1 - i;                       // 0 = newest
                double x = w - (age + frac) * step;
                if (x < -step) { continue; }
                double v = s.Data[i];
                if (double.IsNaN(v)) { cur = null; continue; }
                double y = h - Math.Clamp(v / max, 0, 1) * (h - 2) - 1;
                if (cur == null) { cur = new List<Point>(); runs.Add(cur); }
                cur.Add(new Point(x, y));
            }

            foreach (var run in runs)
            {
                if (run.Count == 1) { run.Add(new Point(run[0].X + 0.01, run[0].Y)); }
                if (s.Fill)
                {
                    var fillGeo = new StreamGeometry();
                    using (var ctx = fillGeo.Open())
                    {
                        ctx.BeginFigure(new Point(run[0].X, h), true, true);
                        foreach (var pt in run) ctx.LineTo(pt, false, false);
                        ctx.LineTo(new Point(run[^1].X, h), false, false);
                    }
                    fillGeo.Freeze();
                    Brush fill;
                    if (glow)
                    {
                        var g = new LinearGradientBrush(Palette.WithAlpha(c, 0x60), Palette.WithAlpha(c, 0x08), 90);
                        g.Freeze();
                        fill = g;
                    }
                    else fill = Palette.Brush(c, 0x38);
                    dc.DrawGeometry(fill, null, fillGeo);
                }

                var lineGeo = new StreamGeometry();
                using (var ctx = lineGeo.Open())
                {
                    ctx.BeginFigure(run[0], false, false);
                    for (int i = 1; i < run.Count; i++) ctx.LineTo(run[i], true, true);
                }
                lineGeo.Freeze();
                if (glow)
                {
                    var halo = new Pen(Palette.Brush(c, 0x40), s.Thickness + 4) { LineJoin = PenLineJoin.Round };
                    halo.Freeze();
                    dc.DrawGeometry(null, halo, lineGeo);
                }
                var pen = new Pen(Palette.Brush(c), s.Thickness) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, lineGeo);
            }
        }
        dc.Pop();

        if (ShowFrame)
        {
            var framePen = new Pen(Palette.Brush(p.Border), 1);
            framePen.Freeze();
            dc.DrawRectangle(null, framePen, new Rect(0.5, 0.5, w - 1, h - 1));
        }
    }

    /// <summary>Round a maximum up to 1, 2 or 5 x 10^n so the scale reads cleanly.</summary>
    public static double NiceCeiling(double v)
    {
        if (v <= 0 || double.IsNaN(v)) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / mag;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nice * mag;
    }
}

/// <summary>
/// A bar meter. Value is 0..1, or NaN for "unavailable" (drawn hatched, not as an empty bar).
/// The bar eases toward a new value over a quarter-second; the number beside it
/// (drawn by the view) is always the exact reading.
/// </summary>
public class Meter : FrameworkElement, IAnimated
{
    double _value = double.NaN, _shown;
    long _setTicks;
    double _from;

    public string ColorKey { get; set; } = "Accent";
    /// <summary>0 = a smooth bar; otherwise that many lit segments, VFD style.</summary>
    public int Segments { get; set; }
    /// <summary>Colour by level (good / warn / bad) instead of ColorKey.</summary>
    public bool LevelColors { get; set; }
    public double WarnAt { get; set; } = 0.75;
    public double BadAt { get; set; } = 0.9;

    public Meter()
    {
        Height = 8;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => { FrameClock.Add(this); Palette.Changed += InvalidateVisual; };
        Unloaded += (_, _) => { FrameClock.Remove(this); Palette.Changed -= InvalidateVisual; };
    }

    public double Value
    {
        get => _value;
        set
        {
            if (value.Equals(_value)) return;
            _from = double.IsNaN(_shown) ? 0 : _shown;
            _value = value;
            _setTicks = Environment.TickCount64;
            InvalidateVisual();
        }
    }

    public bool WantsFrame => Environment.TickCount64 - _setTicks < 300;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        var p = Palette.Current;
        var rect = new Rect(0, 0, w, h);
        double r = Math.Min(3, h / 2);
        var borderPen = new Pen(Palette.Brush(p.Border), 1);
        borderPen.Freeze();
        dc.DrawRoundedRectangle(Palette.Brush(p.Field), borderPen, rect, r, r);

        if (double.IsNaN(_value))
        {
            // hatched = no reading
            dc.PushClip(new RectangleGeometry(rect, r, r));
            var hp = new Pen(Palette.Brush(p.Dim, 0x50), 1);
            hp.Freeze();
            for (double x = -h; x < w; x += 6) dc.DrawLine(hp, new Point(x, h), new Point(x + h, 0));
            dc.Pop();
            _shown = double.NaN;
            return;
        }

        double t = Math.Clamp((Environment.TickCount64 - _setTicks) / 250.0, 0, 1);
        t = 1 - Math.Pow(1 - t, 3); // ease out
        _shown = _from + (Math.Clamp(_value, 0, 1) - _from) * t;

        var c = LevelColors ? p.Level(_value, WarnAt, BadAt) : FrameClock.ColorFor(ColorKey);
        bool glow = p.Phosphor && Settings.Current.Glow;

        if (Segments <= 0)
        {
            double fw = Math.Max(0, (w - 2) * _shown);
            if (fw > 0.5)
            {
                if (glow) dc.DrawRoundedRectangle(Palette.Brush(c, 0x40), null, new Rect(0, 0, fw + 2, h), r, r);
                dc.DrawRoundedRectangle(Palette.Brush(c), null, new Rect(1, 1, fw, h - 2), r, r);
            }
        }
        else
        {
            double gap = 2;
            double sw = (w - gap * (Segments + 1)) / Segments;
            int lit = (int)Math.Round(_shown * Segments);
            for (int i = 0; i < Segments; i++)
            {
                var seg = new Rect(gap + i * (sw + gap), 1.5, Math.Max(1, sw), h - 3);
                if (i < lit)
                {
                    var sc = LevelColors ? p.Level((i + 1.0) / Segments, WarnAt, BadAt) : c;
                    if (glow) dc.DrawRectangle(Palette.Brush(sc, 0x50), null, new Rect(seg.X - 1, seg.Y - 1, seg.Width + 2, seg.Height + 2));
                    dc.DrawRectangle(Palette.Brush(sc), null, seg);
                }
                else dc.DrawRectangle(Palette.Brush(p.Grid), null, seg);
            }
        }
    }
}

/// <summary>A 270-degree arc gauge with a value and caption in the middle (Summary page).</summary>
public class Dial : FrameworkElement, IAnimated
{
    double _value = double.NaN, _shown, _from;
    long _setTicks;

    public string ColorKey { get; set; } = "Accent";
    public bool LevelColors { get; set; } = true;
    public double WarnAt { get; set; } = 0.75;
    public double BadAt { get; set; } = 0.9;
    public string Text { get; set; } = Fmt.Na;
    public string Caption { get; set; } = "";

    public Dial()
    {
        Width = 120; Height = 120;
        Loaded += (_, _) => { FrameClock.Add(this); Palette.Changed += InvalidateVisual; };
        Unloaded += (_, _) => { FrameClock.Remove(this); Palette.Changed -= InvalidateVisual; };
    }

    /// <summary>0..1, or NaN for no reading.</summary>
    public void Set(double value, string text, string caption = null)
    {
        _from = double.IsNaN(_shown) ? 0 : _shown;
        _value = value;
        Text = text;
        if (caption != null) Caption = caption;
        _setTicks = Environment.TickCount64;
        InvalidateVisual();
    }

    public bool WantsFrame => Environment.TickCount64 - _setTicks < 400;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double size = Math.Min(w, h);
        if (size < 20) return;
        var p = Palette.Current;
        var center = new Point(w / 2, h / 2);
        double radius = size / 2 - 8;
        double thick = Math.Max(6, size / 14);
        bool glow = p.Phosphor && Settings.Current.Glow;

        DrawArc(dc, center, radius, 135, 405, p.Grid, thick, false);

        if (!double.IsNaN(_value))
        {
            double t = Math.Clamp((Environment.TickCount64 - _setTicks) / 350.0, 0, 1);
            t = 1 - Math.Pow(1 - t, 3);
            _shown = _from + (Math.Clamp(_value, 0, 1) - _from) * t;
            var c = LevelColors ? p.Level(_value, WarnAt, BadAt) : FrameClock.ColorFor(ColorKey);
            if (_shown > 0.002)
            {
                if (glow) DrawArc(dc, center, radius, 135, 135 + 270 * _shown, Palette.WithAlpha(c, 0x45), thick + 6, true);
                DrawArc(dc, center, radius, 135, 135 + 270 * _shown, c, thick, true);
            }
        }
        else _shown = double.NaN;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface((FontFamily)Application.Current.Resources["NumFont"], FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var big = new FormattedText(Text ?? Fmt.Na, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face,
                                    Math.Max(12, size / 6), Palette.Brush(p.Text), dpi);
        dc.DrawText(big, new Point(center.X - big.Width / 2, center.Y - big.Height / 2 - size * 0.04));
        if (!string.IsNullOrEmpty(Caption))
        {
            var ui = new Typeface((FontFamily)Application.Current.Resources["UiFont"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var cap = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ui,
                                        Math.Max(9, size / 12), Palette.Brush(p.Dim), dpi);
            dc.DrawText(cap, new Point(center.X - cap.Width / 2, center.Y + big.Height / 2 - size * 0.02));
        }
    }

    static void DrawArc(DrawingContext dc, Point c, double r, double fromDeg, double toDeg, Color color, double thickness, bool round)
    {
        if (toDeg - fromDeg < 0.1) return;
        Point P(double deg) { double a = deg * Math.PI / 180; return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)); }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(P(fromDeg), false, false);
            ctx.ArcTo(P(toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        var pen = new Pen(Palette.Brush(color), thickness)
        {
            StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
