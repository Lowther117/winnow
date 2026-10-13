using System.Windows;
using System.Windows.Media;
using Winnow.Core;

namespace Winnow.Theme;

/// <summary>
/// The house palette, ported from theme.py (the shared Tk theme), plus
/// Winnow's own Phosphor Purple. Same key names as theme.py so every app
/// reads as one family: bg, panel, sidebar, text, dim, border, accent,
/// accent_text, field, field_text, field_border, sel, good, warn, bad, code,
/// hint - plus graph colours and the phosphor glow switch.
///
/// Every colour is published two ways into Application.Resources:
///   "Bg"      - a frozen SolidColorBrush   (use {DynamicResource Bg} in XAML)
///   "BgColor" - the Color itself
/// and the current palette is available to code as Palette.Current.
/// </summary>
public sealed class Palette
{
    public string Name = "";
    public Color Bg, Panel, Sidebar, Text, Dim, Border, Accent, AccentText, Field, FieldText, FieldBorder,
                 Sel, Good, Warn, Bad, Code, Hint;
    public Color Graph1, Graph2, Graph3, Graph4, Grid, PCore, ECore;
    public bool Phosphor;      // glow, scanlines, mono numerals
    public bool IsDark = true;

    public static Palette Current { get; private set; } = PhosphorPurple();
    public static event Action Changed;

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // theme.py DARK - verbatim values
    public static Palette Dark() => new()
    {
        Name = "dark",
        Bg = C("#14161b"), Panel = C("#1c1f27"), Sidebar = C("#101218"), Text = C("#e9ebf0"), Dim = C("#98a2b3"),
        Border = C("#333b49"), Accent = C("#5b9cff"), AccentText = C("#0a0c10"), Field = C("#0a0c11"),
        FieldText = C("#f2f4f8"), FieldBorder = C("#495364"), Sel = C("#24344d"), Good = C("#4ac26b"),
        Warn = C("#e0b44a"), Bad = C("#ff6f61"), Code = C("#0f1116"), Hint = C("#182339"),
        Graph1 = C("#5b9cff"), Graph2 = C("#4ac26b"), Graph3 = C("#e0b44a"), Graph4 = C("#c678dd"),
        Grid = C("#262b36"), PCore = C("#5b9cff"), ECore = C("#4ac26b"),
        Phosphor = false, IsDark = true,
    };

    // theme.py LIGHT - verbatim values
    public static Palette Light() => new()
    {
        Name = "light",
        Bg = C("#f1f3f6"), Panel = C("#ffffff"), Sidebar = C("#e7eaef"), Text = C("#14171c"), Dim = C("#596273"),
        Border = C("#ccd2dc"), Accent = C("#1f6feb"), AccentText = C("#ffffff"), Field = C("#ffffff"),
        FieldText = C("#14171c"), FieldBorder = C("#a9b2c0"), Sel = C("#d6e4ff"), Good = C("#146c2e"),
        Warn = C("#8a5a00"), Bad = C("#b42318"), Code = C("#f5f7fa"), Hint = C("#eef4ff"),
        Graph1 = C("#1f6feb"), Graph2 = C("#1a7f37"), Graph3 = C("#9a6700"), Graph4 = C("#8250df"),
        Grid = C("#e3e7ed"), PCore = C("#1f6feb"), ECore = C("#1a7f37"),
        Phosphor = false, IsDark = false,
    };

    // Winnow's default: violet phosphor on near-black, like an old vector display.
    // Inputs still get their own darker shade, a visible border and an accent ring (house rule 3).
    public static Palette PhosphorPurple() => new()
    {
        Name = "phosphor",
        Bg = C("#0b0812"), Panel = C("#130e1e"), Sidebar = C("#08060d"), Text = C("#eadcff"), Dim = C("#9b88c6"),
        Border = C("#2b2040"), Accent = C("#b77dff"), AccentText = C("#0b0812"), Field = C("#06040a"),
        FieldText = C("#f3ebff"), FieldBorder = C("#4d3a73"), Sel = C("#2c1b48"), Good = C("#7de3a6"),
        Warn = C("#ffc56b"), Bad = C("#ff6b8e"), Code = C("#07050c"), Hint = C("#1b1230"),
        Graph1 = C("#b77dff"), Graph2 = C("#e58cff"), Graph3 = C("#7f9dff"), Graph4 = C("#ffc56b"),
        Grid = C("#1d1530"), PCore = C("#b77dff"), ECore = C("#7f9dff"),
        Phosphor = true, IsDark = true,
    };

    public static Palette ByName(string name) => name switch
    {
        "dark" => Dark(),
        "light" => Light(),
        _ => PhosphorPurple(),
    };

    /// <summary>Cycle Phosphor -> Dark -> Light (Ctrl+D).</summary>
    public static string NextName(string name) => name switch { "phosphor" => "dark", "dark" => "light", _ => "phosphor" };

    public static void Apply(string name)
    {
        var p = ByName(name);
        Current = p;
        var res = Application.Current.Resources;
        void Put(string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            res[key] = b;
            res[key + "Color"] = c;
        }
        Put("Bg", p.Bg); Put("Panel", p.Panel); Put("Sidebar", p.Sidebar); Put("Text", p.Text); Put("Dim", p.Dim);
        Put("Border", p.Border); Put("Accent", p.Accent); Put("AccentText", p.AccentText); Put("Field", p.Field);
        Put("FieldText", p.FieldText); Put("FieldBorder", p.FieldBorder); Put("Sel", p.Sel); Put("Good", p.Good);
        Put("Warn", p.Warn); Put("Bad", p.Bad); Put("Code", p.Code); Put("Hint", p.Hint);
        Put("Graph1", p.Graph1); Put("Graph2", p.Graph2); Put("Graph3", p.Graph3); Put("Graph4", p.Graph4);
        Put("Grid", p.Grid); Put("PCore", p.PCore); Put("ECore", p.ECore);
        Put("AccentSoft", WithAlpha(p.Accent, 0x33));
        Put("Hover", p.IsDark ? Blend(p.Panel, p.Accent, 0.10) : Blend(p.Panel, p.Accent, 0.07));

        res["UiFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI");
        res["MonoFont"] = new FontFamily("Cascadia Mono, Consolas, Courier New");
        // Phosphor mode draws numbers in the mono face, like a terminal; the others use the UI face.
        res["NumFont"] = p.Phosphor ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI Variable Text, Segoe UI");
        res["IconFont"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        res["GlowRadius"] = p.Phosphor && Settings.Current.Glow ? 14.0 : 0.0;
        res["GlowOpacity"] = p.Phosphor && Settings.Current.Glow ? 0.85 : 0.0;

        try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("theme changed: " + ex.Message); }
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>A frozen brush, for code that draws.</summary>
    public static SolidColorBrush Brush(Color c, byte alpha = 255)
    {
        var b = new SolidColorBrush(alpha == 255 ? c : WithAlpha(c, alpha));
        b.Freeze();
        return b;
    }

    /// <summary>Good below warnAt, warn below badAt, bad above.</summary>
    public Color Level(double value, double warnAt, double badAt) => value >= badAt ? Bad : value >= warnAt ? Warn : Good;
}
