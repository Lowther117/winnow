using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>
/// Every page in the sidebar is a UserControl implementing this. Pages are
/// created the first time they are opened and kept; OnShown/OnHidden tell a
/// page when to subscribe to Sampler.Tick and when to stop, so hidden pages
/// cost nothing.
/// </summary>
public interface IView
{
    void OnShown();
    void OnHidden();
    /// <summary>Optional argument from Nav.Go (e.g. a pid to select).</summary>
    void Receive(object arg) { }
}

public enum StatusKind { Info, Good, Warn, Bad }

/// <summary>The status line at the bottom of the window.</summary>
public static class Status
{
    public static event Action<string, StatusKind> Changed;
    public static void Set(string text, StatusKind kind = StatusKind.Info)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.BeginInvoke(new Action(() => Changed?.Invoke(text, kind)));
            return;
        }
        Changed?.Invoke(text, kind);
    }
}

/// <summary>Move between pages from anywhere: Nav.Go("processes", pid).</summary>
public static class Nav
{
    public static Action<string, object> Handler;
    public static void Go(string key, object arg = null) => Handler?.Invoke(key, arg);
}

/// <summary>Small UI helpers shared by every page.</summary>
public static class Ui
{
    static Window Owner => Application.Current?.MainWindow;

    /// <summary>
    /// Look up a named style. "Accent" is also the accent brush's key, so the accent
    /// button style is stored as "AccentButton"; asking for "Accent" here gets that style.
    /// </summary>
    static Style FindStyle(string key)
    {
        if (key == "Accent") key = "AccentButton";
        return (Style)Application.Current.FindResource(key);
    }

    // ------------------------------------------------------------ dialogs

    /// <summary>Themed yes/no. Returns true for the confirm button.</summary>
    public static bool Confirm(string message, string title = "Winnow", string yes = "Continue", string no = "Cancel", bool danger = false)
        => Dialog(title, message, yes, no, danger) == true;

    public static void Info(string message, string title = "Winnow") => Dialog(title, message, "OK", null, false);

    public static void Error(string message, string title = "Winnow") => Dialog(title, message, "OK", null, true);

    static bool? Dialog(string title, string message, string yes, string no, bool danger)
    {
        var w = new Window
        {
            Title = title,
            Owner = Owner,
            WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MaxWidth = 620,
            MinWidth = 340,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Bg");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        w.SourceInitialized += (_, _) => MainWindow.ApplyTitleBar(w);

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 18) };
        root.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = yes, IsDefault = true, MinWidth = 90, Margin = new Thickness(0) };
        ok.Style = FindStyle(danger && no != null ? "Danger" : "Accent");
        ok.Click += (_, _) => { w.DialogResult = true; };
        if (no != null)
        {
            var cancel = new Button { Content = no, IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            buttons.Children.Add(cancel);
        }
        else ok.IsCancel = true;
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        w.Content = root;
        return w.ShowDialog();
    }

    /// <summary>Ask for a line of text. Null if cancelled.</summary>
    public static string Prompt(string message, string initial = "", string title = "Winnow")
    {
        var w = new Window
        {
            Title = title, Owner = Owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, MinWidth = 380,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Bg");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.SourceInitialized += (_, _) => MainWindow.ApplyTitleBar(w);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Text = initial, MinWidth = 340 };
        root.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Style = FindStyle("Accent") };
        ok.Click += (_, _) => w.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        root.Children.Add(buttons);
        w.Content = root;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>Save dialog starting in the default save folder (Downloads\Winnow unless changed).</summary>
    public static string SaveFile(string defaultName, string filter = "All files|*.*")
    {
        var d = new Microsoft.Win32.SaveFileDialog
        {
            FileName = defaultName, Filter = filter, InitialDirectory = Paths.DefaultSaveDir(), AddExtension = true,
        };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string OpenFile(string filter = "All files|*.*")
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter, InitialDirectory = Paths.DefaultSaveDir() };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string PickFolder(string initial = null)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = initial ?? Paths.DefaultSaveDir() };
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    // ------------------------------------------------------------ async

    /// <summary>Run async work from a click handler: errors go to the status line, not a crash.</summary>
    public static async void Run(Func<Task> work, string what = null)
    {
        try { await work(); }
        catch (Exception ex)
        {
            Log.Write((what ?? "action") + ": " + ex);
            Status.Set((what ?? "That") + " failed: " + ex.Message, StatusKind.Bad);
        }
    }

    /// <summary>Disable a button while async work runs, with optional busy text.</summary>
    public static async Task Busy(Button b, Func<Task> work, string busyText = null)
    {
        object old = b.Content;
        b.IsEnabled = false;
        if (busyText != null) b.Content = busyText;
        try { await work(); }
        finally { b.Content = old; b.IsEnabled = true; }
    }

    // ------------------------------------------------------------ builders

    public static TextBlock Text(string text, string style = null, double size = 0)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        if (style != null) t.Style = FindStyle(style);
        if (size > 0) t.FontSize = size;
        return t;
    }

    /// <summary>Set a brush from the palette by key so it follows theme changes.</summary>
    public static T Brush<T>(this T el, DependencyProperty prop, string key) where T : FrameworkElement
    {
        el.SetResourceReference(prop, key);
        return el;
    }

    public static Border Card(UIElement child, double padding = -1)
    {
        var b = new Border { Child = child, Style = (Style)Application.Current.FindResource("Card") };
        if (padding >= 0) b.Padding = new Thickness(padding);
        return b;
    }

    public static Button Button(string text, RoutedEventHandler click, string style = null, string tip = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.Style = FindStyle(style);
        if (tip != null) b.ToolTip = tip;
        b.Click += click;
        return b;
    }

    /// <summary>Header block used at the top of every page: title + one-line explanation.</summary>
    public static StackPanel Header(string title, string subtitle)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        sp.Children.Add(Text(title, "H1"));
        if (!string.IsNullOrEmpty(subtitle))
        {
            var s = Text(subtitle, "Sub");
            s.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(s);
        }
        return sp;
    }

    public static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text ?? ""); Status.Set("Copied", StatusKind.Good); }
        catch (Exception ex) { Status.Set("Could not copy: " + ex.Message, StatusKind.Warn); }
    }

    /// <summary>True when the key event is Ctrl+the given key.</summary>
    public static bool Ctrl(KeyEventArgs e, Key k) => e.Key == k && (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    /// <summary>Colour helper for code-built elements: a frozen brush from the current palette.</summary>
    public static SolidColorBrush PaletteBrush(Func<Theme.Palette, Color> pick) => Theme.Palette.Brush(pick(Theme.Palette.Current));
}
