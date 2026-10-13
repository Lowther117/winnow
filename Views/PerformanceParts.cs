using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Winnow.Controls;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>One subsystem in the Performance page's left-hand list: a sparkline, a title and a live value.</summary>
internal sealed class PerfNav
{
    public string Key = "";
    public ListBoxItem Item;
    public Graph Spark;
    public TextBlock TitleText, ValueText;
    /// <summary>One value per sparkline series for a snapshot (NaN = no reading).</summary>
    public Func<Snapshot, double[]> Values;
    /// <summary>The short live readout under the title.</summary>
    public Func<Snapshot, string> Text;
}

/// <summary>Small builders shared by the Performance page.</summary>
internal static class PerfUi
{
    public static TextBlock Small(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    /// <summary>A colour square and a label, for graph legends.</summary>
    public static StackPanel Swatch(string colorKey, string text, double leftMargin = 10)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(leftMargin, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var sq = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        sq.SetResourceReference(Border.BackgroundProperty, colorKey);
        sp.Children.Add(sq);
        var t = Small(text);
        t.Margin = new Thickness(5, 0, 0, 0);
        sp.Children.Add(t);
        return sp;
    }

    /// <summary>Panel title on the left, a dim detail line (model name etc.) on the right.</summary>
    public static DockPanel Title(string text, out TextBlock title, out TextBlock sub)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        title = new TextBlock
        {
            Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        sub = new TextBlock
        {
            FontSize = 12.5, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 3),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        DockPanel.SetDock(title, Dock.Left);
        dp.Children.Add(title);
        dp.Children.Add(sub);
        return dp;
    }

    /// <summary>Append a row to a Grid and place the element in it.</summary>
    public static void AddRow(Grid g, UIElement e, GridLength height, double minHeight = 0)
    {
        var rd = new RowDefinition { Height = height };
        if (minHeight > 0) rd.MinHeight = minHeight;
        g.RowDefinitions.Add(rd);
        Grid.SetRow(e, g.RowDefinitions.Count - 1);
        g.Children.Add(e);
    }

    public static ScrollViewer Scroll(UIElement content, double maxHeight)
    {
        return new ScrollViewer
        {
            Content = content, MaxHeight = maxHeight, Margin = new Thickness(0, 10, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    public static TextBlock Wrapped(string text = "")
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }
}

/// <summary>
/// A graph with Task Manager-style captions: what it shows and the current
/// full-scale value above it, the time span and zero below it.
/// </summary>
internal sealed class PerfGraphBlock : Grid
{
    public Graph Plot { get; }
    readonly TextBlock _scale, _span;
    readonly Func<double, string> _fmt;
    readonly int _samples;

    public PerfGraphBlock(string caption, Graph plot, Func<double, string> scaleFormat, params (string colorKey, string name)[] legend)
    {
        Plot = plot;
        _fmt = scaleFormat;
        _samples = plot.Capacity;

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        _scale = PerfUi.Small("");
        DockPanel.SetDock(_scale, Dock.Right);
        top.Children.Add(_scale);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(PerfUi.Small(caption));
        foreach (var (colorKey, name) in legend) left.Children.Add(PerfUi.Swatch(colorKey, name, 12));
        top.Children.Add(left);
        PerfUi.AddRow(this, top, GridLength.Auto);

        PerfUi.AddRow(this, plot, new GridLength(1, GridUnitType.Star), 50);

        var bottom = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var zero = PerfUi.Small("0");
        DockPanel.SetDock(zero, Dock.Right);
        bottom.Children.Add(zero);
        _span = PerfUi.Small("");
        bottom.Children.Add(_span);
        PerfUi.AddRow(this, bottom, GridLength.Auto);

        RefreshSpan();
        UpdateScale();
    }

    /// <summary>"60 seconds" at 1 s refresh, "30 seconds" at 0.5 s, and so on.</summary>
    public void RefreshSpan()
    {
        double secs = _samples * Settings.Current.RefreshMs / 1000.0;
        _span.Text = secs >= 120 ? (secs / 60).ToString("0") + " minutes" : secs.ToString("0") + " seconds";
    }

    public void UpdateScale()
    {
        string t = _fmt != null ? _fmt(ScaleOf(Plot)) : "";
        if (_scale.Text != t) _scale.Text = t;
    }

    /// <summary>The full-scale value the graph is drawing with (mirrors Graph.OnRender).</summary>
    public static double ScaleOf(Graph g)
    {
        if (!g.AutoMax) return g.Max;
        double m = g.AutoFloor;
        foreach (var s in g.Series)
            foreach (var v in s.Data)
                if (!double.IsNaN(v) && v > m) m = v;
        return Graph.NiceCeiling(m * 1.1);
    }
}

/// <summary>Label-over-value readouts that wrap into columns; created on first Set, updated in place after.</summary>
internal sealed class PerfStatPanel : WrapPanel
{
    readonly Dictionary<string, TextBlock> _values = new();
    public double CellWidth = 128;

    public void Set(string label, string value, string tip = null)
    {
        value ??= Fmt.Na;
        if (!_values.TryGetValue(label, out var tb))
        {
            var sp = new StackPanel { Width = CellWidth, Margin = new Thickness(0, 0, 10, 8) };
            sp.Children.Add(Ui.Text(label, "Label"));
            tb = Ui.Text(value, "ValueSmall");
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(tb);
            Children.Add(sp);
            _values[label] = tb;
        }
        if (tb.Text != value)
        {
            tb.Text = value;
            tb.ToolTip = value.Length > 15 ? value : null;
        }
        if (tip != null) tb.ToolTip = tip;
    }
}

/// <summary>One hardware sensor as a DataGrid row, updated in place each tick.</summary>
public sealed class PerfSensorRow : INotifyPropertyChanged
{
    static readonly SensorReading Scratch = new();   // UI thread only

    public PerfSensorRow(SensorReading r)
    {
        Id = r.Id;
        Hardware = r.Hardware ?? "";
        HardwareType = r.HardwareType ?? "";
        Type = r.Type ?? "";
        Name = r.Name ?? "";
        Update(r);
    }

    public string Id { get; }
    public string Hardware { get; }
    public string HardwareType { get; }
    public string Type { get; }
    public string Name { get; }

    public string ValueText { get; private set; } = Fmt.Na;
    public string MinText { get; private set; } = Fmt.Na;
    public string MaxText { get; private set; } = Fmt.Na;
    public double ValueNum { get; private set; } = double.NaN;
    public double MinNum { get; private set; } = double.NaN;
    public double MaxNum { get; private set; } = double.NaN;

    public event PropertyChangedEventHandler PropertyChanged;

    public void Update(SensorReading r)
    {
        string v = Fmt.Sensor(r);
        if (v != ValueText)
        {
            ValueText = v; ValueNum = Num(r.Value);
            Raise(nameof(ValueText)); Raise(nameof(ValueNum));
        }
        Scratch.Type = r.Type;
        Scratch.Value = r.Min;
        string mn = Fmt.Sensor(Scratch);
        if (mn != MinText)
        {
            MinText = mn; MinNum = Num(r.Min);
            Raise(nameof(MinText)); Raise(nameof(MinNum));
        }
        Scratch.Value = r.Max;
        string mx = Fmt.Sensor(Scratch);
        if (mx != MaxText)
        {
            MaxText = mx; MaxNum = Num(r.Max);
            Raise(nameof(MaxText)); Raise(nameof(MaxNum));
        }
    }

    static double Num(float? f) => f.HasValue ? f.Value : double.NaN;

    void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
