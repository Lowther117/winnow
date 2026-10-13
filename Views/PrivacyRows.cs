using System.Windows;
using System.Windows.Controls;
using Winnow.Core;

namespace Winnow.Views;

/// <summary>Small builders shared by the Privacy page.</summary>
static class PrivacyUi
{
    public static Style St(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>A rounded chip with coloured text. The label is returned so its text/colour can change later.</summary>
    public static Border Chip(string text, string colorKey, out TextBlock label, string tip = null)
    {
        label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        var b = new Border { Child = label, Style = St("Chip"), Margin = new Thickness(0, 2, 6, 2) };
        if (tip != null) b.ToolTip = tip;
        return b;
    }

    /// <summary>A borderless, accent-coloured button that reads like a link.</summary>
    public static Button Link(string text, RoutedEventHandler click, string tip = null)
    {
        var b = new Button { Content = text, Style = St("Ghost"), Padding = new Thickness(6, 2, 6, 2), FontSize = 12, Margin = new Thickness(0, 0, 4, 0) };
        b.SetResourceReference(Control.ForegroundProperty, "Accent");
        if (tip != null) b.ToolTip = tip;
        b.Click += click;
        return b;
    }

    /// <summary>A wrapping text block in a palette colour.</summary>
    public static TextBlock Para(string text, string colorKey = "Dim", double size = 12, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        if (margin.HasValue) t.Margin = margin.Value;
        return t;
    }

    public static string RiskColour(string risk) => risk switch { "Safe" => "Good", "Moderate" => "Warn", _ => "Bad" };

    public static string StateLabel(PrivacyState s) => s switch
    {
        PrivacyState.Applied => "Applied",
        PrivacyState.NotApplied => "Not applied",
        PrivacyState.Partly => "Partly applied",
        PrivacyState.NotPresent => "Not present on this PC",
        PrivacyState.Unknown => "State unknown",
        _ => "Checking…"
    };

    public static string StateColour(PrivacyState s) => s switch
    {
        PrivacyState.Applied => "Good",
        PrivacyState.Partly => "Warn",
        _ => "Dim"
    };
}

/// <summary>One tweak on the Tweaks tab: tick box, chips, description, details and undo link.</summary>
sealed class PrivacyTweakRow
{
    public PrivacyTweak Tweak { get; }
    public CheckBox Check { get; }
    public Border Root { get; }
    public PrivacyState State { get; private set; } = PrivacyState.Checking;

    readonly TextBlock _stateLabel;
    readonly Button _undo;
    readonly Button _detailsBtn;
    readonly Border _detailsBox;
    readonly TextBlock _detailsText;
    readonly Func<PrivacyTweak, List<string>> _describe;

    public PrivacyTweakRow(PrivacyTweak t, Func<PrivacyTweak, List<string>> describe, Action<PrivacyTweakRow> undo, Action selectionChanged)
    {
        Tweak = t;
        _describe = describe;

        var title = new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Check = new CheckBox { Content = title, Margin = new Thickness(0, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
        Check.Checked += (_, _) => selectionChanged();
        Check.Unchecked += (_, _) => selectionChanged();

        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Check);
        head.Children.Add(PrivacyUi.Chip(t.Risk, PrivacyUi.RiskColour(t.Risk), out _,
            t.Risk switch
            {
                "Safe" => "Safe: no features you are likely to use are lost.",
                "Moderate" => "Moderate: switches off a feature some people use - see the description.",
                _ => "Advanced: switches off features many people rely on - read the description first."
            }));
        head.Children.Add(PrivacyUi.Chip(PrivacyUi.StateLabel(State), PrivacyUi.StateColour(State), out _stateLabel,
            "What Winnow found on this PC the last time it checked."));
        head.Children.Add(PrivacyUi.Chip(t.PresetName, "Dim", out _,
            t.Level > 0 ? "Ticked by the " + string.Join(", ", t.Presets) + " preset" + (t.Presets.Count > 1 ? "s" : "") + "."
                        : "Not ticked by any preset - tick it yourself if you want it."));

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(PrivacyUi.Para(t.Description, "Dim", 12, new Thickness(24, 2, 0, 0)));
        if (t.ShowEditionNote)
            body.Children.Add(PrivacyUi.Para(t.EditionNote, "Warn", 11.5, new Thickness(24, 3, 0, 0)));

        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 2, 0, 0) };
        _detailsBtn = PrivacyUi.Link("Show what will change", (_, _) => ToggleDetails());
        links.Children.Add(_detailsBtn);
        _undo = PrivacyUi.Link("Undo", (_, _) => undo(this), "Put back every change this tweak made, newest first.");
        _undo.Visibility = Visibility.Collapsed;
        links.Children.Add(_undo);
        body.Children.Add(links);

        _detailsText = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = PrivacyUi.St("Mono"), FontSize = 11.5 };
        _detailsText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _detailsBox = new Border
        {
            Child = _detailsText, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(24, 4, 0, 2),
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
        };
        _detailsBox.SetResourceReference(Border.BackgroundProperty, "Code");
        _detailsBox.SetResourceReference(Border.BorderBrushProperty, "Border");
        body.Children.Add(_detailsBox);

        Root = new Border { Child = body, Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
        Root.SetResourceReference(Border.BorderBrushProperty, "Grid");
    }

    public bool IsSelected
    {
        get => Check.IsChecked == true;
        set => Check.IsChecked = value;
    }

    public bool DetailsOpen => _detailsBox.Visibility == Visibility.Visible;

    void ToggleDetails()
    {
        if (DetailsOpen)
        {
            _detailsBox.Visibility = Visibility.Collapsed;
            _detailsBtn.Content = "Show what will change";
        }
        else
        {
            RefreshDetails();
            _detailsBox.Visibility = Visibility.Visible;
            _detailsBtn.Content = "Hide details";
        }
    }

    /// <summary>Rebuild the details text (reads the registry and services - cheap).</summary>
    public void RefreshDetails()
    {
        try { _detailsText.Text = string.Join("\n", _describe(Tweak)); }
        catch (Exception ex) { _detailsText.Text = "Could not read the current state: " + ex.Message; }
    }

    public void SetState(PrivacyState s)
    {
        State = s;
        _stateLabel.Text = PrivacyUi.StateLabel(s);
        _stateLabel.SetResourceReference(TextBlock.ForegroundProperty, PrivacyUi.StateColour(s));
        if (DetailsOpen) RefreshDetails();
    }

    public void SetUndo(bool visible, bool enabled)
    {
        _undo.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _undo.IsEnabled = enabled;
    }
}

/// <summary>One journal entry on the History tab.</summary>
public sealed class PrivacyHistoryRow
{
    public JournalEntry Entry { get; }
    public string Id => Entry.Id;
    public DateTime Time => Entry.Time;
    public string Group => Entry.Group ?? "";
    public string Description => Entry.Description ?? "";
    public string Kind => Entry.Kind ?? "";
    public string Target { get; }
    public string BeforeAfter { get; }
    public string StatusText { get; }
    public bool IsUndone => Entry.Reverted;
    public bool CanUndo => !Entry.Reverted && Entry.Revertible;
    readonly string _search;

    public PrivacyHistoryRow(JournalEntry e)
    {
        Entry = e;
        Target = TargetOf(e);
        BeforeAfter = ChangeOf(e);
        StatusText = !e.Revertible ? "Not undoable" : e.Reverted ? "Undone" : "Live";
        _search = string.Join("\u0001", Group, Description, Kind, Target, BeforeAfter, StatusText).ToLowerInvariant();
    }

    public bool Matches(string lowerFilter) => string.IsNullOrEmpty(lowerFilter) || _search.Contains(lowerFilter);

    public static string TargetOf(JournalEntry e) => (e.Kind ?? "") switch
    {
        "Registry" => (e.Hive ?? "") + "\\" + (e.Path ?? "") + "\\" + (e.Name ?? ""),
        "Service" => "service " + e.Path,
        "Task" => "task " + e.Path,
        "Firewall" => "firewall rule " + e.Path,
        "File" => e.Path + " -> " + e.Name,
        _ => e.Path ?? ""
    };

    public static string ChangeOf(JournalEntry e)
    {
        switch (e.Kind ?? "")
        {
            case "Registry":
                return (e.BeforeExists ? Show(e.BeforeData) : "(not set)") + "  →  " + Show(e.AfterData);
            case "Service":
            case "Task":
                return Show(e.BeforeData) + "  →  " + Show(e.AfterData);
            case "Firewall":
                return "(no rule)  →  " + Show(e.AfterData);
            case "File":
                return "moved";
            default:
                return e.AfterData ?? "";
        }
    }

    static string Show(string s)
    {
        if (s == null) return "";
        s = s.Replace("\u0001", "; ");
        return s.Length > 120 ? s.Substring(0, 117) + "…" : s;
    }
}
