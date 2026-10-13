using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Winnow.Core;
using Winnow.Theme;

namespace Winnow.Views;

/// <summary>One row in the Analyse grids: a scanned folder or a file.</summary>
public sealed class DiskEntryRow : INotifyPropertyChanged
{
    public DiskEntryRow(string name, string fullPath, bool isDir, long size, long files, DateTime modified, double pct,
                        FolderNode node, bool unreadable)
    {
        Name = name;
        FullPath = fullPath;
        IsDir = isDir;
        Size = size;
        Files = files;
        Modified = modified;
        Pct = pct;
        Node = node;
        Unreadable = unreadable;
    }

    public static DiskEntryRow ForDir(FolderNode n, double parentSize) =>
        new DiskEntryRow(n.Denied ? n.Name + "  (could not be read)" : n.Name, n.FullPath, true, n.Size, n.Files, n.Newest,
                         parentSize > 0 ? n.Size / parentSize : double.NaN, n,
                         n.Denied && n.Size == 0 && n.Children.Count == 0);

    public static DiskEntryRow ForFile(FolderFile f, double parentSize) =>
        new DiskEntryRow(f.Name, f.FullPath, false, f.Size, 1, f.Modified,
                         parentSize > 0 ? f.Size / parentSize : double.NaN, null, false);

    public FolderNode Node { get; }          // null for files
    public string Name { get; }
    public string FullPath { get; }
    public string Folder => System.IO.Path.GetDirectoryName(FullPath) ?? "";
    public bool IsDir { get; }
    public bool Unreadable { get; }
    public string Glyph => IsDir ? "" : "";   // Segoe icons: folder / page
    public long Size { get; }
    public string SizeText => Unreadable ? Fmt.Na : Fmt.Bytes(Size);
    public double Pct { get; }                // 0..1 of the folder being viewed, NaN when not meaningful
    public long Files { get; }
    public string FilesText => IsDir ? (Unreadable ? Fmt.Na : Files.ToString("N0")) : "";
    public DateTime Modified { get; }
    public string ModifiedText => Modified == DateTime.MinValue ? Fmt.Na : Modified.ToString("g");

    public event PropertyChangedEventHandler PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>A thin "% of parent" bar with the number drawn on it, for a DataGrid cell.</summary>
public sealed class DiskPctBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DiskPctBar),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1, or NaN for no value.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public DiskPctBar()
    {
        Height = 15;
        MinWidth = 50;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => { Palette.Changed += InvalidateVisual; };
        Unloaded += (_, _) => { Palette.Changed -= InvalidateVisual; };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        var p = Palette.Current;
        dc.DrawRectangle(Palette.Brush(p.Grid), null, new Rect(0, 0, w, h));
        double v = Value;
        string text;
        if (double.IsNaN(v)) text = Fmt.Na;
        else
        {
            double c = Math.Clamp(v, 0, 1);
            if (c > 0) dc.DrawRectangle(Palette.Brush(p.Accent, 0x8C), null, new Rect(0, 0, Math.Max(1, w * c), h));
            text = (c * 100).ToString(c > 0 && c < 0.001 ? "0.00" : "0.0", CultureInfo.CurrentCulture) + "%";
        }
        var font = Application.Current.Resources["NumFont"] as FontFamily ?? new FontFamily("Consolas");
        var face = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11,
                                   Palette.Brush(p.Text), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(4, Math.Max(0, (h - ft.Height) / 2)));
    }
}
