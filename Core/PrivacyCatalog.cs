using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Winnow.Core;

// =============================================================================
// The Privacy page's tweak catalogue.
//
// Every tweak is a list of plain actions (registry values, service start
// types, scheduled tasks, outbound firewall blocks). The Privacy page applies
// them through Change.* with the group "privacy.<Id>", so each tweak can be
// undone on its own, and each tweak can read the system to report whether it
// is applied.
//
// Rules for what goes in here:
//   * Only settings documented by Microsoft (Policy CSP / ADMX references,
//     "Manage connections from Windows operating system components to
//     Microsoft services", Edge policy reference) or very well-established
//     community sources (ElevenForum tutorials, Chris Titus WinUtil). The
//     source is in a comment above each tweak and in its Sources property.
//   * Nothing that switches off Windows Update, Microsoft Defender,
//     SmartScreen, UAC or the firewall. Security is left alone.
// =============================================================================

public enum PrivacyState { Checking, Applied, NotApplied, Partly, NotPresent, Unknown }

/// <summary>Set one registry value.</summary>
public sealed class PrivacyRegAction
{
    public string Hive { get; }
    public string Path { get; }
    public string Name { get; }
    public object Value { get; }
    public RegistryValueKind Kind { get; }

    public PrivacyRegAction(string hive, string path, string name, object value, RegistryValueKind kind)
    {
        Hive = hive; Path = path; Name = name; Value = value; Kind = kind;
    }

    public string Target => Hive + "\\" + Path + "\\" + Name;

    public string ValueText => Value is string s ? "\"" + s + "\"" : Convert.ToString(Value, CultureInfo.InvariantCulture);

    public bool IsApplied() => Change.RegistryEquals(Hive, Path, Name, Value);

    public string CurrentText()
    {
        var v = Change.GetRegistry(Hive, Path, Name);
        if (v == null) return "not set";
        if (v is string[] arr) return string.Join("; ", arr);
        if (v is byte[] bytes) return bytes.Length + " bytes";
        if (v is string str) return "\"" + str + "\"";
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }
}

/// <summary>Set a service's start type.</summary>
public sealed class PrivacyServiceAction
{
    public string Name { get; }
    public string Mode { get; }   // Disabled | Manual

    public PrivacyServiceAction(string name, string mode) { Name = name; Mode = mode; }

    /// <summary>True when the current start type is the wanted one or stricter (Disabled satisfies Manual).</summary>
    public bool IsSatisfiedBy(string current) =>
        current == Mode || (Mode == "Manual" && current == "Disabled");
}

/// <summary>Block one program from making outbound connections.</summary>
public sealed class PrivacyFirewallAction
{
    public string RuleName { get; }
    public string ProgramTemplate { get; }   // may contain %SystemRoot%

    public PrivacyFirewallAction(string ruleName, string programTemplate) { RuleName = ruleName; ProgramTemplate = programTemplate; }

    public string Program => Environment.ExpandEnvironmentVariables(ProgramTemplate);
}

public sealed class PrivacyTweak
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Safe | Moderate | Advanced</summary>
    public string Risk { get; init; } = "Safe";
    /// <summary>Lowest preset that includes this tweak: 1 Light, 2 Recommended, 3 Aggressive, 0 = only when ticked by hand.</summary>
    public int Level { get; init; }
    public string EditionNote { get; init; } = "";
    /// <summary>When the edition note applies. Null = always.</summary>
    public Func<bool> NoteWhen { get; init; }
    /// <summary>Where the settings come from (space-separated URLs).</summary>
    public string Sources { get; init; } = "";

    public List<PrivacyRegAction> Reg { get; } = new();
    public List<PrivacyServiceAction> Services { get; } = new();
    public List<string> Tasks { get; } = new();
    public List<PrivacyFirewallAction> Firewall { get; } = new();

    public string Group => "privacy." + Id;

    public bool InPreset(int level) => Level > 0 && Level <= level;

    /// <summary>Names of the presets this tweak is part of (Light is inside Recommended, which is inside Aggressive).</summary>
    public List<string> Presets
    {
        get
        {
            var l = new List<string>();
            if (InPreset(1)) l.Add("Light");
            if (InPreset(2)) l.Add("Recommended");
            if (InPreset(3)) l.Add("Aggressive");
            return l;
        }
    }

    public string PresetName => Level switch { 1 => "Light", 2 => "Recommended", 3 => "Aggressive", _ => "Manual only" };

    public bool ShowEditionNote => !string.IsNullOrEmpty(EditionNote) && (NoteWhen == null || SafeNote());

    bool SafeNote() { try { return NoteWhen(); } catch { return true; } }

    public int ActionCount => Reg.Count + Services.Count + Tasks.Count + Firewall.Count;

    /// <summary>
    /// Read the system and say how much of this tweak is in place. taskStates comes from one batched
    /// query for every task in the catalogue (null = the query failed); fwRules is the set of Winnow
    /// firewall rules that exist (null = could not be read). Safe to call from a background thread.
    /// </summary>
    public PrivacyState CheckState(Dictionary<string, string> taskStates, HashSet<string> fwRules)
    {
        int total = 0, done = 0;
        bool unknownParts = false;
        try
        {
            foreach (var r in Reg)
            {
                total++;
                if (r.IsApplied()) done++;
            }
            foreach (var s in Services)
            {
                string cur = Change.GetServiceStart(s.Name);
                if (cur == null) continue;   // service not on this PC
                total++;
                if (s.IsSatisfiedBy(cur)) done++;
            }
            if (taskStates == null) unknownParts |= Tasks.Count > 0;
            else
                foreach (var t in Tasks)
                {
                    if (!taskStates.TryGetValue(t, out var st) || st == null) continue;   // task not on this PC
                    total++;
                    if (st == "Disabled") done++;
                }
            foreach (var f in Firewall)
            {
                if (!File.Exists(f.Program)) continue;   // program not on this PC
                if (fwRules == null) { unknownParts = true; continue; }
                total++;
                if (fwRules.Contains(f.RuleName)) done++;
            }
        }
        catch (Exception ex)
        {
            Log.Write("privacy check " + Id + ": " + ex.Message);
            return PrivacyState.Unknown;
        }
        if (total == 0) return unknownParts ? PrivacyState.Unknown : PrivacyState.NotPresent;
        if (done == total) return PrivacyState.Applied;
        if (done == 0) return PrivacyState.NotApplied;
        return PrivacyState.Partly;
    }

    /// <summary>One line per action, with what is there now. Registry and service reads only - cheap.</summary>
    public List<string> DescribeActions(Dictionary<string, string> taskStates, HashSet<string> fwRules)
    {
        var lines = new List<string>();
        foreach (var r in Reg)
            lines.Add($"Registry   {r.Target} = {r.ValueText} ({r.Kind})    now: {r.CurrentText()}");
        foreach (var s in Services)
        {
            string cur = Change.GetServiceStart(s.Name);
            lines.Add($"Service    {s.Name} start type -> {s.Mode}    now: {cur ?? "not on this PC"}");
        }
        foreach (var t in Tasks)
        {
            string now;
            if (taskStates == null) now = "not checked yet";
            else if (!taskStates.TryGetValue(t, out var st) || st == null) now = "not on this PC";
            else now = st;
            lines.Add($"Task       {t} -> Disabled    now: {now}");
        }
        foreach (var f in Firewall)
        {
            string prog = f.Program;
            string now = !File.Exists(prog) ? "program not on this PC"
                       : fwRules == null ? "not checked yet"
                       : fwRules.Contains(f.RuleName) ? "rule present" : "no rule";
            lines.Add($"Firewall   block outbound for {prog} (rule \"{f.RuleName}\")    now: {now}");
        }
        if (!string.IsNullOrWhiteSpace(Sources))
            foreach (var u in Sources.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                lines.Add("Reference  " + u);
        return lines;
    }
}

public static class PrivacyCatalog
{
    // ------------------------------------------------------------ categories

    public const string CatTelemetry = "Telemetry & diagnostics";
    public const string CatAds = "Advertising & suggestions";
    public const string CatSearchAi = "Search & Copilot & AI";
    public const string CatActivity = "Activity & history";
    public const string CatApps = "Apps & permissions";
    public const string CatEdge = "Edge";
    public const string CatUpdates = "Updates & delivery";
    public const string CatStart = "Lock screen & Start";

    public static readonly string[] Categories =
        { CatTelemetry, CatAds, CatSearchAi, CatActivity, CatApps, CatEdge, CatUpdates, CatStart };

    /// <summary>A short note shown under a category heading, or "".</summary>
    public static string CategoryNote(string category) => category switch
    {
        CatTelemetry => WinVer.HonoursTelemetryZero ? "This edition honours diagnostic data off (level 0)." : "",
        CatSearchAi => "Copilot is an ordinary app on Windows 11 24H2 and later. To remove it, use the Installed Apps page.",
        CatEdge => "Once any Edge policy is set, Edge says it is \"managed by your organisation\" - that is expected. Edge reads policies when it restarts; edge://policy lists what is set.",
        CatApps => "The Advanced items here switch off features some people rely on, so no preset ticks them.",
        _ => ""
    };

    /// <summary>Text for the page header about this PC's edition.</summary>
    public static string EditionSummary()
    {
        var sb = new StringBuilder();
        if (!WinVer.HonoursTelemetryZero)
            sb.Append("On Home and Pro, Windows will not go below Required diagnostic data; 0 is only honoured on Enterprise/Education - the services and tasks below do most of the work.");
        if (WinVer.IsHome)
            sb.Append((sb.Length > 0 ? " " : "") +
                      "Home edition may ignore some policy values; Winnow also sets the per-user equivalents where they exist, and each tweak's state shows what is actually set.");
        return sb.ToString();
    }

    static bool NotEnterpriseOrEducation() => !WinVer.HonoursTelemetryZero;

    // ------------------------------------------------------------- sources

    const string CspSystem = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system";
    const string CspExperience = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience";
    const string CspPrivacy = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy";
    const string CspSearch = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search";
    const string CspWindowsAi = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai";
    const string CspNews = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests";
    const string CspDelivery = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-deliveryoptimization";
    const string CspTextInput = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-textinput";
    const string CspErrorReporting = "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-errorreporting";
    const string ManageConnections = "https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services";
    const string EdgePolicy = "https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/";
    const string NotepadDoc = "https://learn.microsoft.com/en-us/windows/client-management/manage-notepad";
    const string WinUtilTelemetry = "https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry";
    const string WinUtilAi = "https://winutil.christitus.com/dev/tweaks/z--advanced-tweaks---caution/windowsai/";
    const string PdqAds = "https://www.pdq.com/blog/how-to-disable-ads-on-windows/";

    // ------------------------------------------------------------- helpers

    const string HKLM = "HKLM", HKCU = "HKCU";
    const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    const string WindowsAiPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";
    const string SearchPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    const string SystemPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";
    const string AppPrivacyPolicy = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";

    static PrivacyRegAction D(string hive, string path, string name, int value) =>
        new PrivacyRegAction(hive, path, name, value, RegistryValueKind.DWord);

    static PrivacyServiceAction Svc(string name, string mode) => new PrivacyServiceAction(name, mode);

    static PrivacyFirewallAction Fw(string exe) =>
        new PrivacyFirewallAction("Winnow - block " + exe, @"%SystemRoot%\System32\" + exe);

    // ------------------------------------------------------------- catalogue

    public static List<PrivacyTweak> All { get; } = Build();

    public static PrivacyTweak Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Every scheduled task the catalogue touches, for one batched state query.</summary>
    public static List<string> AllTasks =>
        All.SelectMany(t => t.Tasks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static List<PrivacyTweak> Build()
    {
        var l = new List<PrivacyTweak>();

        // ======================================================== Telemetry & diagnostics

        // AllowTelemetry: Policy CSP System/AllowTelemetry (DataCollection.admx,
        //   Software\Policies\Microsoft\Windows\DataCollection). 0 is honoured only on Enterprise/Education/Server.
        // The HKLM\...\CurrentVersion\Policies\DataCollection pair (AllowTelemetry / MaxTelemetryAllowed) is
        //   what the Settings app reads; WinUtil sets AllowTelemetry there. MaxTelemetryAllowed is
        //   community-established, not documented by Microsoft.
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
        //          https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        l.Add(new PrivacyTweak
        {
            Id = "telemetry-level", Category = CatTelemetry, Risk = "Safe", Level = 1,
            Title = "Send the least diagnostic data Windows allows",
            Description = "Sets the diagnostic data level to its minimum: off on Enterprise and Education, Required on Home and Pro. " +
                          "Optional diagnostic data (app and feature use, browsing in Edge, extra crash detail) stops being sent. " +
                          "Settings will show that some settings are managed by your organisation.",
            EditionNote = "On Home and Pro, Windows will not go below Required diagnostic data; 0 is only honoured on Enterprise/Education - the services and tasks below do most of the work.",
            NoteWhen = NotEnterpriseOrEducation,
            Sources = CspSystem + " " + WinUtilTelemetry,
            Reg =
            {
                D(HKLM, DataCollectionPolicy, "AllowTelemetry", 0),
                D(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0),
                D(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "MaxTelemetryAllowed", 0),
            },
        });

        // DiagTrack = Connected User Experiences and Telemetry, the service that uploads diagnostic data.
        // Sources: https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry (service list on Acer's guide:
        //          https://blog.acer.com/en/discussion/4198/how-to-disable-telemetry-in-windows-11-for-advanced-users)
        l.Add(new PrivacyTweak
        {
            Id = "telemetry-service", Category = CatTelemetry, Risk = "Safe", Level = 1,
            Title = "Stop the telemetry service (DiagTrack)",
            Description = "Disables Connected User Experiences and Telemetry, the service that uploads diagnostic data, on every edition. " +
                          "Side effects: the Diagnostic Data Viewer and some Feedback Hub features stop working, and Windows Insider builds need it on.",
            Sources = "https://blog.acer.com/en/discussion/4198/how-to-disable-telemetry-in-windows-11-for-advanced-users",
            Services = { Svc("DiagTrack", "Disabled") },
        });

        // dmwappushservice = Device Management WAP Push message Routing Service. Used by MDM (Intune) enrolment.
        // Source: long-standing community debloat lists (O&O ShutUp10, privacy.sexy); not documented by Microsoft as telemetry.
        l.Add(new PrivacyTweak
        {
            Id = "wap-push-service", Category = CatTelemetry, Risk = "Moderate", Level = 2,
            Title = "Disable the WAP push routing service (dmwappushservice)",
            Description = "Disables Device Management WAP Push message Routing, which routes device-management messages and has long been tied to telemetry. " +
                          "Side effect: work or school device management (Intune/MDM enrolment) will not work while it is off.",
            Services = { Svc("dmwappushservice", "Disabled") },
        });

        // Telemetry scheduled tasks. Task list from WinUtil's Telemetry tweak and Acer's guide (Compatibility
        // Appraiser, ProgramDataUpdater "if you have it"); Device Information tasks run DeviceCensus.exe
        // (winhelponline). Tasks not on this PC are reported as such and skipped.
        // Sources: https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        //          https://blog.acer.com/en/discussion/4198/how-to-disable-telemetry-in-windows-11-for-advanced-users
        //          https://www.winhelponline.com/blog/device-census-using-webcam-telemetry-kaspersky-warning/
        l.Add(new PrivacyTweak
        {
            Id = "telemetry-tasks", Category = CatTelemetry, Risk = "Safe", Level = 2,
            Title = "Turn off telemetry scheduled tasks",
            Description = "Disables the scheduled tasks that gather compatibility, usage, disk, USB and device-inventory data for Microsoft: " +
                          "Compatibility Appraiser, Customer Experience Improvement Program, Feedback (Siuf), Device Census and Autochk SQM upload. " +
                          "Side effect: Windows' upgrade-compatibility check runs only when a feature update is offered; updates still install.",
            Sources = WinUtilTelemetry + " https://blog.acer.com/en/discussion/4198/how-to-disable-telemetry-in-windows-11-for-advanced-users" +
                      " https://www.winhelponline.com/blog/device-census-using-webcam-telemetry-kaspersky-warning/",
            Tasks =
            {
                @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
                @"\Microsoft\Windows\Application Experience\MareBackup",
                @"\Microsoft\Windows\Autochk\Proxy",
                @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
                @"\Microsoft\Windows\Feedback\Siuf\DmClient",
                @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
                @"\Microsoft\Windows\Device Information\Device",
                @"\Microsoft\Windows\Device Information\Device User",
            },
        });

        // DoNotShowFeedbackNotifications: Policy CSP Experience (FeedbackNotifications.admx,
        //   Software\Policies\Microsoft\Windows\DataCollection). NumberOfSIUFInPeriod=0 is the Settings
        //   "Feedback frequency: Never" value (WinUtil).
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
        //          https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        l.Add(new PrivacyTweak
        {
            Id = "feedback-requests", Category = CatTelemetry, Risk = "Safe", Level = 1,
            Title = "Stop feedback requests",
            Description = "Sets feedback frequency to Never and turns off the pop-ups asking you to rate Windows. Feedback Hub still works if you open it yourself.",
            Sources = CspExperience + " " + WinUtilTelemetry,
            Reg =
            {
                D(HKLM, DataCollectionPolicy, "DoNotShowFeedbackNotifications", 1),
                D(HKCU, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
            },
        });

        // DisableTailoredExperiencesWithDiagnosticData: Policy CSP Experience/AllowTailoredExperiencesWithDiagnosticData
        //   (CloudContent.admx, user). TailoredExperiencesWithDiagnosticDataEnabled = the Settings toggle (ElevenForum).
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
        //          https://www.elevenforum.com/t/enable-or-disable-tailored-experiences-in-windows-11.7554/
        l.Add(new PrivacyTweak
        {
            Id = "tailored-experiences", Category = CatTelemetry, Risk = "Safe", Level = 1,
            Title = "No tailored experiences from diagnostic data",
            Description = "Stops Microsoft using your diagnostic data to personalise tips, adverts and recommendations (Settings > Privacy & security > General / Diagnostics & feedback).",
            Sources = CspExperience + " https://www.elevenforum.com/t/enable-or-disable-tailored-experiences-in-windows-11.7554/",
            Reg =
            {
                D(HKCU, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
                D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
            },
        });

        // AllowLinguisticDataCollection: Policy CSP TextInput (Software\Microsoft\Windows\CurrentVersion\Policies\TextInput).
        // Input\TIPC Enabled = the "Improve inking and typing" toggle (GeekRewind / ElevenForum).
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-textinput
        //          https://geekrewind.com/how-to-disable-or-enable-improve-inking-and-typing-in-windows-11/
        l.Add(new PrivacyTweak
        {
            Id = "improve-inking", Category = CatTelemetry, Risk = "Safe", Level = 1,
            Title = "Don't send inking and typing data to Microsoft",
            Description = "Turns off \"Improve inking and typing\", which sends samples of what you type and write to Microsoft to improve recognition.",
            Sources = CspTextInput + " https://geekrewind.com/how-to-disable-or-enable-improve-inking-and-typing-in-windows-11/",
            Reg =
            {
                D(HKLM, @"Software\Microsoft\Windows\CurrentVersion\Policies\TextInput", "AllowLinguisticDataCollection", 0),
                D(HKCU, @"Software\Microsoft\Input\TIPC", "Enabled", 0),
            },
        });

        // LimitDiagnosticLogCollection / LimitDumpCollection: Policy CSP System (DataCollection.admx).
        // Value names are the policy names (also used by the CIS benchmarks).
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
        l.Add(new PrivacyTweak
        {
            Id = "diag-limits", Category = CatTelemetry, Risk = "Safe", Level = 2,
            Title = "Limit diagnostic logs and crash dumps",
            Description = "Stops Windows collecting extra diagnostic logs, and limits crash reports to small dumps instead of full memory dumps that can contain open documents.",
            Sources = CspSystem,
            Reg =
            {
                D(HKLM, DataCollectionPolicy, "LimitDiagnosticLogCollection", 1),
                D(HKLM, DataCollectionPolicy, "LimitDumpCollection", 1),
            },
        });

        // DisableOneSettingsDownloads: Policy CSP System (DataCollection.admx), Windows 11 21H2+.
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
        l.Add(new PrivacyTweak
        {
            Id = "onesettings", Category = CatTelemetry, Risk = "Moderate", Level = 3,
            Title = "Stop OneSettings configuration downloads",
            Description = "Stops Windows checking in with Microsoft's OneSettings service, which pushes diagnostic and feature configuration to the PC. " +
                          "Side effect: Microsoft can no longer adjust some feature settings remotely; updates are not affected.",
            Sources = CspSystem,
            Reg = { D(HKLM, DataCollectionPolicy, "DisableOneSettingsDownloads", 1) },
        });

        // Windows Error Reporting: ErrorReporting.admx "Disable Windows Error Reporting"
        //   (SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting\Disabled) plus the non-policy WER
        //   "Disabled" value and the QueueReporting task (WinUtil).
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-errorreporting
        //          https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        l.Add(new PrivacyTweak
        {
            Id = "error-reporting", Category = CatTelemetry, Risk = "Moderate", Level = 2,
            Title = "Turn off Windows Error Reporting",
            Description = "Stops crash and hang reports being sent to Microsoft. " +
                          "Side effects: Microsoft cannot offer solutions for crashes, and some crash details may no longer appear in Reliability Monitor.",
            Sources = CspErrorReporting + " " + WinUtilTelemetry,
            Reg =
            {
                D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
                D(HKLM, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
            },
            Tasks = { @"\Microsoft\Windows\Windows Error Reporting\QueueReporting" },
        });

        // Outbound blocks for the two telemetry collectors. Firewall approach for DeviceCensus.exe is discussed by
        // winhelponline; CompatTelRunner.exe is the Compatibility Appraiser runner.
        // Source: https://www.winhelponline.com/blog/device-census-using-webcam-telemetry-kaspersky-warning/
        l.Add(new PrivacyTweak
        {
            Id = "telemetry-firewall", Category = CatTelemetry, Risk = "Advanced", Level = 3,
            Title = "Block telemetry programs in the firewall",
            Description = "Adds Windows Firewall rules that stop CompatTelRunner.exe (compatibility telemetry) and DeviceCensus.exe (device inventory) making outbound connections, " +
                          "in case Windows re-enables their tasks. The rules are in the firewall group \"Winnow\". " +
                          "Side effect: Windows Update's upgrade-readiness check cannot report back, so a feature update may be offered later than usual.",
            Sources = "https://www.winhelponline.com/blog/device-census-using-webcam-telemetry-kaspersky-warning/",
            Firewall = { Fw("CompatTelRunner.exe"), Fw("DeviceCensus.exe") },
        });

        // ======================================================== Advertising & suggestions

        // DisabledByGroupPolicy: Policy CSP Privacy/DisableAdvertisingId and "Manage connections" 18.1 General.
        // HKCU AdvertisingInfo\Enabled is the per-user Settings toggle (HKLM equivalent listed in "Manage connections").
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy
        //          https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "advertising-id", Category = CatAds, Risk = "Safe", Level = 1,
            Title = "Turn off the advertising ID",
            Description = "Stops apps using your advertising ID to track you across apps and show personalised adverts. Adverts still appear; they are just not tied to you.",
            Sources = CspPrivacy + " " + ManageConnections,
            Reg =
            {
                D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1),
                D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
            },
        });

        // HttpAcceptLanguageOptOut: "Manage connections" 18.1 General.
        // Source: https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "language-list", Category = CatAds, Risk = "Safe", Level = 1,
            Title = "Don't share my language list with websites",
            Description = "Turns off \"Let websites show me locally relevant content by accessing my language list\". Websites see only your browser's language.",
            Sources = ManageConnections,
            Reg = { D(HKCU, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1) },
        });

        // SubscribedContent-338389 / -310093 (PDQ, WinUtil), SoftLandingEnabled (WinUtil-era CDM value),
        // UserProfileEngagement\ScoobeSystemSettingEnabled ("Suggest ways to finish setting up my device", WinUtil).
        // Sources: https://www.pdq.com/blog/how-to-disable-ads-on-windows/
        //          https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        l.Add(new PrivacyTweak
        {
            Id = "tips-suggestions", Category = CatAds, Risk = "Safe", Level = 1,
            Title = "Turn off tips, tricks and setup nags",
            Description = "Stops \"Get tips and suggestions when using Windows\", the welcome experience after updates, and the \"finish setting up your device\" screens that push Microsoft 365, OneDrive and Game Pass.",
            Sources = PdqAds + " " + WinUtilTelemetry,
            Reg =
            {
                D(HKCU, CDM, "SubscribedContent-338389Enabled", 0),
                D(HKCU, CDM, "SubscribedContent-310093Enabled", 0),
                D(HKCU, CDM, "SoftLandingEnabled", 0),
                D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
            },
        });

        // SubscribedContent-338393/-353694/-353696 = "Show me suggested content in the Settings app" (PDQ).
        // AllowOnlineTips: "Manage connections" (HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer).
        // Sources: https://www.pdq.com/blog/how-to-disable-ads-on-windows/
        //          https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "settings-suggestions", Category = CatAds, Risk = "Safe", Level = 1,
            Title = "No suggested content in Settings",
            Description = "Removes promoted content and online tips from the Settings app.",
            Sources = PdqAds + " " + ManageConnections,
            Reg =
            {
                D(HKCU, CDM, "SubscribedContent-338393Enabled", 0),
                D(HKCU, CDM, "SubscribedContent-353694Enabled", 0),
                D(HKCU, CDM, "SubscribedContent-353696Enabled", 0),
                D(HKLM, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "AllowOnlineTips", 0),
            },
        });

        // SilentInstalledAppsEnabled & pre-installed app values (WinUtil). DisableWindowsConsumerFeatures:
        // Policy CSP Experience/AllowWindowsConsumerFeatures - Enterprise/Education only per Microsoft.
        // Sources: https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        //          https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
        l.Add(new PrivacyTweak
        {
            Id = "silent-apps", Category = CatAds, Risk = "Safe", Level = 1,
            Title = "Stop Windows installing suggested apps",
            Description = "Stops Windows quietly installing promoted Store apps and games after setup or updates. " +
                          "Apps already installed stay - remove them on the Installed Apps page.",
            EditionNote = "Microsoft documents the \"Microsoft consumer experiences\" policy as Enterprise and Education only. On Home and Pro the per-user values in this tweak do the work.",
            NoteWhen = NotEnterpriseOrEducation,
            Sources = WinUtilTelemetry + " " + CspExperience,
            Reg =
            {
                D(HKCU, CDM, "SilentInstalledAppsEnabled", 0),
                D(HKCU, CDM, "PreInstalledAppsEnabled", 0),
                D(HKCU, CDM, "PreInstalledAppsEverEnabled", 0),
                D(HKCU, CDM, "OemPreInstalledAppsEnabled", 0),
                D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1),
            },
        });

        // Start_AccountNotifications (ElevenForum, build 22621.1928+) and
        // SystemSettings\AccountNotifications\EnableAccountNotifications (ElevenForum).
        // Sources: https://www.elevenforum.com/t/enable-or-disable-notification-badging-for-microsoft-accounts-on-start-menu-in-windows-11.14645/
        //          https://www.elevenforum.com/t/enable-or-disable-show-notifications-in-settings-app-in-windows-11.25835/
        l.Add(new PrivacyTweak
        {
            Id = "account-notifications", Category = CatAds, Risk = "Safe", Level = 1,
            Title = "No Microsoft account nags in Start and Settings",
            Description = "Stops the badges and prompts in Start and Settings that push you to sign in with, back up to or upgrade your Microsoft account.",
            Sources = "https://www.elevenforum.com/t/enable-or-disable-notification-badging-for-microsoft-accounts-on-start-menu-in-windows-11.14645/" +
                      " https://www.elevenforum.com/t/enable-or-disable-show-notifications-in-settings-app-in-windows-11.25835/",
            Reg =
            {
                D(HKCU, ExplorerAdvanced, "Start_AccountNotifications", 0),
                D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0),
            },
        });

        // ======================================================== Search & Copilot & AI

        // BingSearchEnabled + DisableSearchBoxSuggestions (ElevenForum). DisableWebSearch / ConnectedSearchUseWeb /
        // AllowSearchToUseLocation: "Manage connections" Cortana and Search; DoNotUseWebResults (ConnectedSearchUseWeb)
        // is Enterprise/Education only per Policy CSP Search.
        // Sources: https://www.elevenforum.com/t/enable-or-disable-bing-web-suggested-search-results-in-windows-search-on-windows-11.5309/
        //          https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        //          https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search
        l.Add(new PrivacyTweak
        {
            Id = "web-search", Category = CatSearchAi, Risk = "Safe", Level = 1,
            Title = "No Bing web results in Start search",
            Description = "Stops Start and taskbar search sending what you type to Bing and showing web results, and stops search using your location; " +
                          "search finds only apps, files and settings on this PC. " +
                          "Side effect: File Explorer's search box stops suggesting recent searches.",
            EditionNote = "Microsoft documents the machine-wide \"don't search the web\" policy as Enterprise/Education only; the per-user values in this tweak cover Home and Pro.",
            NoteWhen = NotEnterpriseOrEducation,
            Sources = "https://www.elevenforum.com/t/enable-or-disable-bing-web-suggested-search-results-in-windows-search-on-windows-11.5309/ " +
                      ManageConnections + " " + CspSearch,
            Reg =
            {
                D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
                D(HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                D(HKLM, SearchPolicy, "DisableWebSearch", 1),
                D(HKLM, SearchPolicy, "ConnectedSearchUseWeb", 0),
                D(HKLM, SearchPolicy, "AllowSearchToUseLocation", 0),
            },
        });

        // EnableDynamicContentInWSB: Policy CSP Search/AllowSearchHighlights. IsDynamicSearchBoxEnabled = Settings toggle (PDQ).
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search
        //          https://www.pdq.com/blog/how-to-disable-ads-on-windows/
        l.Add(new PrivacyTweak
        {
            Id = "search-highlights", Category = CatSearchAi, Risk = "Safe", Level = 1,
            Title = "Turn off search highlights",
            Description = "Removes the trending searches, illustrations and promoted content from the search box and search home.",
            Sources = CspSearch + " " + PdqAds,
            Reg =
            {
                D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0),
                D(HKLM, SearchPolicy, "EnableDynamicContentInWSB", 0),
            },
        });

        // AllowCloudSearch: Policy CSP Search/AllowCloudSearch (Search.admx). Value name = policy name.
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search
        l.Add(new PrivacyTweak
        {
            Id = "cloud-search", Category = CatSearchAi, Risk = "Moderate", Level = 3,
            Title = "Don't search my cloud content",
            Description = "Stops Windows search querying OneDrive, SharePoint and Outlook online for results. " +
                          "Side effect: files that exist only in OneDrive or work SharePoint will not show up in Start search.",
            Sources = CspSearch,
            Reg = { D(HKLM, SearchPolicy, "AllowCloudSearch", 0) },
        });

        // TurnOffWindowsCopilot: Policy CSP WindowsAI (user scope, deprecated - does not apply to the new Copilot app).
        // ShowCopilotButton = taskbar "Copilot (preview)" toggle on 23H2 (community-established).
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai
        l.Add(new PrivacyTweak
        {
            Id = "copilot", Category = CatSearchAi, Risk = "Safe", Level = 1,
            Title = "Turn off Windows Copilot (policy and taskbar button)",
            Description = "Sets the Turn off Windows Copilot policy and hides the taskbar Copilot button. This stops the older built-in Copilot sidebar.",
            EditionNote = "On 24H2 and later Copilot is an ordinary app and this policy does not stop it. Remove it on the Installed Apps page.",
            NoteWhen = () => WinVer.Build >= 26100,
            Sources = CspWindowsAi,
            Reg =
            {
                D(HKCU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                D(HKCU, ExplorerAdvanced, "ShowCopilotButton", 0),
            },
        });

        // AllowRecallEnablement (device) and DisableAIDataAnalysis (device and user): Policy CSP WindowsAI,
        // WindowsCopilot.admx, SOFTWARE\Policies\Microsoft\Windows\WindowsAI. 24H2 KB5055627 (26100.3915)+.
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai
        l.Add(new PrivacyTweak
        {
            Id = "recall", Category = CatSearchAi, Risk = "Safe", Level = 1,
            Title = "Turn off Recall and remove it",
            Description = "Stops Recall saving snapshots of your screen (existing snapshots are deleted) and makes Recall unavailable, which removes it at the next restart.",
            EditionNote = "Recall exists only on Copilot+ PCs. Elsewhere this simply keeps it off.",
            Sources = CspWindowsAi,
            Reg =
            {
                D(HKLM, WindowsAiPolicy, "AllowRecallEnablement", 0),
                D(HKLM, WindowsAiPolicy, "DisableAIDataAnalysis", 1),
                D(HKCU, @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
            },
        });

        // DisableClickToDo (device and user): Policy CSP WindowsAI, WindowsCopilot.admx.
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai
        l.Add(new PrivacyTweak
        {
            Id = "click-to-do", Category = CatSearchAi, Risk = "Safe", Level = 1,
            Title = "Turn off Click to Do",
            Description = "Disables Click to Do, which analyses what is on your screen to offer AI actions.",
            EditionNote = "Click to Do exists only on Copilot+ PCs. Elsewhere this simply keeps it off.",
            Sources = CspWindowsAi,
            Reg =
            {
                D(HKLM, WindowsAiPolicy, "DisableClickToDo", 1),
                D(HKCU, @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1),
            },
        });

        // Paint: DisableCocreator / DisableGenerativeFill / DisableImageCreator - Policy CSP WindowsAI
        // (Software\Microsoft\Windows\CurrentVersion\Policies\Paint). Notepad: DisableAIFeaturesInNotepad (Microsoft doc
        // gives no registry path; HKLM\SOFTWARE\Policies\WindowsNotepad\DisableAIFeatures per WinUtil).
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai
        //          https://learn.microsoft.com/en-us/windows/client-management/manage-notepad
        //          https://winutil.christitus.com/dev/tweaks/z--advanced-tweaks---caution/windowsai/
        l.Add(new PrivacyTweak
        {
            Id = "app-ai", Category = CatSearchAi, Risk = "Moderate", Level = 3,
            Title = "Turn off AI features in Paint and Notepad",
            Description = "Disables Cocreator, Image Creator and generative fill in Paint, and Copilot/Rewrite in Notepad. These send your text and images to Microsoft's cloud when used. " +
                          "Side effect: those features disappear from the two apps.",
            Sources = CspWindowsAi + " " + NotepadDoc + " " + WinUtilAi,
            Reg =
            {
                D(HKLM, @"Software\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
                D(HKLM, @"Software\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
                D(HKLM, @"Software\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
                D(HKLM, @"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1),
            },
        });

        // ======================================================== Activity & history

        // PublishUserActivities / UploadUserActivities / EnableActivityFeed (OSPolicy.admx,
        // Software\Policies\Microsoft\Windows\System). EnableActivityFeed in Policy CSP Privacy.
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy
        //          https://www.elevenforum.com/t/enable-or-disable-store-activity-history-on-device-in-windows-11.7812/
        l.Add(new PrivacyTweak
        {
            Id = "activity-history", Category = CatActivity, Risk = "Safe", Level = 1,
            Title = "Turn off activity history",
            Description = "Stops Windows recording the apps, files and websites you use and uploading that history to your Microsoft account.",
            Sources = CspPrivacy + " https://www.elevenforum.com/t/enable-or-disable-store-activity-history-on-device-in-windows-11.7812/",
            Reg =
            {
                D(HKLM, SystemPolicy, "EnableActivityFeed", 0),
                D(HKLM, SystemPolicy, "PublishUserActivities", 0),
                D(HKLM, SystemPolicy, "UploadUserActivities", 0),
            },
        });

        // Start_TrackProgs: "Manage connections" 18.1 General.
        // Source: https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "app-launch-tracking", Category = CatActivity, Risk = "Safe", Level = 1,
            Title = "Don't track app launches",
            Description = "Turns off \"Let Windows improve Start and search results by tracking app launches\". " +
                          "Side effect: Start no longer shows a Most used list.",
            Sources = ManageConnections,
            Reg = { D(HKCU, ExplorerAdvanced, "Start_TrackProgs", 0) },
        });

        // "Custom inking and typing dictionary" off - ElevenForum Turn_OFF .reg values.
        // Source: https://www.elevenforum.com/t/enable-or-disable-custom-inking-and-typing-dictionary-in-windows-11.5564/
        l.Add(new PrivacyTweak
        {
            Id = "inking-personalisation", Category = CatActivity, Risk = "Safe", Level = 2,
            Title = "Turn off inking and typing personalisation",
            Description = "Stops Windows building a personal dictionary from your typing, handwriting and contacts. " +
                          "Side effect: handwriting recognition and typing suggestions no longer learn your words.",
            Sources = "https://www.elevenforum.com/t/enable-or-disable-custom-inking-and-typing-dictionary-in-windows-11.5564/",
            Reg =
            {
                D(HKCU, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                D(HKCU, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                D(HKCU, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
                D(HKCU, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
                D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\CPSS\Store\InkingAndTypingPersonalization", "Value", 0),
            },
        });

        // OnlineSpeechPrivacy\HasAccepted: "Manage connections" 18.6 Speech. AllowInputPersonalization:
        // Policy CSP Privacy (Globalization.admx, Software\Policies\Microsoft\InputPersonalization).
        // Sources: https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        //          https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy
        l.Add(new PrivacyTweak
        {
            Id = "online-speech", Category = CatActivity, Risk = "Moderate", Level = 2,
            Title = "Turn off online speech recognition",
            Description = "Stops your voice being sent to Microsoft's cloud for recognition. " +
                          "Side effect: voice typing (Win+H) and some voice features stop working or fall back to on-device recognition where available.",
            Sources = ManageConnections + " " + CspPrivacy,
            Reg =
            {
                D(HKCU, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0),
                D(HKLM, @"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", 0),
            },
        });

        // AllowCrossDeviceClipboard: Policy CSP Privacy (OSPolicy.admx, Software\Policies\Microsoft\Windows\System).
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy
        l.Add(new PrivacyTweak
        {
            Id = "cloud-clipboard", Category = CatActivity, Risk = "Safe", Level = 1,
            Title = "Don't sync the clipboard to the cloud",
            Description = "Stops what you copy being uploaded and synced to your other devices. Clipboard history on this PC (Win+V) still works.",
            Sources = CspPrivacy,
            Reg = { D(HKLM, SystemPolicy, "AllowCrossDeviceClipboard", 0) },
        });

        // PreventHandwritingDataSharing (HKLM\Software\Policies\Microsoft\Windows\TabletPC) and
        // PreventHandwritingErrorReports (Software\Policies\Microsoft\Windows\HandwritingErrorReports) - Internet
        // Communication Management ADMX, as documented by gp.kapilarya.com.
        // Sources: https://gp.kapilarya.com/turn-off-handwriting-personalization-data-sharing
        //          https://gp.kapilarya.com/?p=16469
        l.Add(new PrivacyTweak
        {
            Id = "handwriting-sharing", Category = CatActivity, Risk = "Safe", Level = 2,
            Title = "Don't share handwriting samples",
            Description = "Stops handwriting samples and handwriting recognition error reports being sent to Microsoft.",
            Sources = "https://gp.kapilarya.com/turn-off-handwriting-personalization-data-sharing https://gp.kapilarya.com/?p=16469",
            Reg =
            {
                D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\TabletPC", "PreventHandwritingDataSharing", 1),
                D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports", "PreventHandwritingErrorReports", 1),
            },
        });

        // ======================================================== Apps & permissions

        // LetApps* (AppPrivacy.admx, Software\Policies\Microsoft\Windows\AppPrivacy): 0 user in control, 1 force allow,
        // 2 force deny. LetAppsAccessAccountInfo / LetAppsAccessLocation = 2 in "Manage connections"; LetAppsGetDiagnosticInfo
        // and LetAppsRunInBackground follow the same documented pattern.
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy
        //          https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "app-diagnostics", Category = CatApps, Risk = "Safe", Level = 2,
            Title = "Stop apps reading diagnostic info about other apps",
            Description = "Store apps can no longer see which other apps are running or read their diagnostic information.",
            Sources = CspPrivacy,
            Reg = { D(HKLM, AppPrivacyPolicy, "LetAppsGetDiagnosticInfo", 2) },
        });

        l.Add(new PrivacyTweak
        {
            Id = "app-account-info", Category = CatApps, Risk = "Moderate", Level = 3,
            Title = "Stop apps reading my account info",
            Description = "Store apps can no longer read your name, picture and account details. " +
                          "Side effect: some apps will ask you to type your details or show a generic profile.",
            Sources = CspPrivacy + " " + ManageConnections,
            Reg = { D(HKLM, AppPrivacyPolicy, "LetAppsAccessAccountInfo", 2) },
        });

        l.Add(new PrivacyTweak
        {
            Id = "app-background", Category = CatApps, Risk = "Advanced", Level = 0,
            Title = "Stop Store apps running in the background",
            Description = "Store apps can no longer run, sync or receive data while you are not using them. " +
                          "Side effect: notifications from apps such as Phone Link, WhatsApp, Mail and Teams (Store versions) may stop until you open them.",
            Sources = CspPrivacy,
            Reg = { D(HKLM, AppPrivacyPolicy, "LetAppsRunInBackground", 2) },
        });

        // DisableLocation: Policy CSP System/AllowLocation (Sensors.admx) and "Manage connections" 18.2 Location.
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
        //          https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "location", Category = CatApps, Risk = "Advanced", Level = 0,
            Title = "Turn off location services",
            Description = "Turns location off for Windows and every app, and greys out the setting. " +
                          "Side effect: Find my device, weather, maps, automatic time zone and location-based features stop working.",
            Sources = CspSystem + " " + ManageConnections,
            Reg = { D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1) },
        });

        // AllowFindMyDevice: Policy CSP Experience (FindMy.admx) and "Manage connections" Find My Device.
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
        //          https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "find-my-device", Category = CatApps, Risk = "Advanced", Level = 0,
            Title = "Turn off Find my device",
            Description = "Stops this PC registering its location with your Microsoft account. " +
                          "Side effect: you cannot locate, lock or wipe this PC from account.microsoft.com if it is lost or stolen.",
            Sources = CspExperience + " " + ManageConnections,
            Reg = { D(HKLM, @"SOFTWARE\Policies\Microsoft\FindMyDevice", "AllowFindMyDevice", 0) },
        });

        // ======================================================== Edge
        // All under HKLM\SOFTWARE\Policies\Microsoft\Edge, REG_DWORD, per the Edge policy reference.

        // Sources: https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/diagnosticdata
        //          https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/userfeedbackallowed
        l.Add(new PrivacyTweak
        {
            Id = "edge-diagnostics", Category = CatEdge, Risk = "Safe", Level = 1,
            Title = "Edge: send no diagnostic data or feedback",
            Description = "Turns off Edge's required and optional diagnostic data (separately from Windows' own setting, Edge 122 and later) and the in-browser feedback tool.",
            Sources = EdgePolicy + "diagnosticdata " + EdgePolicy + "userfeedbackallowed",
            Reg =
            {
                D(HKLM, EdgeKey, "DiagnosticData", 0),
                D(HKLM, EdgeKey, "UserFeedbackAllowed", 0),
            },
        });

        // Sources: .../personalizationreportingenabled, .../edgeshoppingassistantenabled,
        //          ConfigureDoNotTrack from "Manage connections" Microsoft Edge enterprise policies.
        l.Add(new PrivacyTweak
        {
            Id = "edge-personalisation", Category = CatEdge, Risk = "Safe", Level = 1,
            Title = "Edge: no personalised adverts or shopping assistant",
            Description = "Stops Microsoft using your Edge browsing history to personalise adverts, search and news; turns off the shopping assistant (price comparison and coupons fetched from Microsoft's servers for every shop you visit); sends Do Not Track.",
            Sources = EdgePolicy + "personalizationreportingenabled " + EdgePolicy + "edgeshoppingassistantenabled " + ManageConnections,
            Reg =
            {
                D(HKLM, EdgeKey, "PersonalizationReportingEnabled", 0),
                D(HKLM, EdgeKey, "EdgeShoppingAssistantEnabled", 0),
                D(HKLM, EdgeKey, "ConfigureDoNotTrack", 1),
            },
        });

        // Sources: .../startupboostenabled, .../backgroundmodeenabled
        l.Add(new PrivacyTweak
        {
            Id = "edge-background", Category = CatEdge, Risk = "Safe", Level = 2,
            Title = "Edge: don't run when it is closed",
            Description = "Turns off startup boost (Edge starting at sign-in) and background mode (Edge staying running after you close it). " +
                          "Side effect: Edge opens a little slower and extensions do not run while it is closed.",
            Sources = EdgePolicy + "startupboostenabled " + EdgePolicy + "backgroundmodeenabled",
            Reg =
            {
                D(HKLM, EdgeKey, "StartupBoostEnabled", 0),
                D(HKLM, EdgeKey, "BackgroundModeEnabled", 0),
            },
        });

        // Sources: .../showrecommendationsenabled, .../spotlightexperiencesandrecommendationsenabled
        l.Add(new PrivacyTweak
        {
            Id = "edge-recommendations", Category = CatEdge, Risk = "Safe", Level = 1,
            Title = "Edge: no recommendations or promotions",
            Description = "Stops Edge's \"Edge recommends\" pop-ups, feature promotions, coach marks and spotlight recommendations.",
            Sources = EdgePolicy + "showrecommendationsenabled " + EdgePolicy + "spotlightexperiencesandrecommendationsenabled",
            Reg =
            {
                D(HKLM, EdgeKey, "ShowRecommendationsEnabled", 0),
                D(HKLM, EdgeKey, "SpotlightExperiencesAndRecommendationsEnabled", 0),
            },
        });

        // Source: .../microsoft365copilotchaticonenabled (Edge 139+; the only toolbar control from Edge 141).
        l.Add(new PrivacyTweak
        {
            Id = "edge-copilot", Category = CatEdge, Risk = "Safe", Level = 2,
            Title = "Edge: hide the Copilot button",
            Description = "Removes the Copilot button from Edge's toolbar.",
            Sources = EdgePolicy + "microsoft365copilotchaticonenabled",
            Reg = { D(HKLM, EdgeKey, "Microsoft365CopilotChatIconEnabled", 0) },
        });

        // Source: .../newtabpagecontentenabled
        l.Add(new PrivacyTweak
        {
            Id = "edge-newtab", Category = CatEdge, Risk = "Moderate", Level = 3,
            Title = "Edge: no MSN news on the new tab page",
            Description = "Turns off the Microsoft news feed and promoted content on Edge's new tab page. " +
                          "Side effect: the new tab page's content setting is locked to off.",
            Sources = EdgePolicy + "newtabpagecontentenabled",
            Reg = { D(HKLM, EdgeKey, "NewTabPageContentEnabled", 0) },
        });

        // SearchSuggestEnabled: "Manage connections" Microsoft Edge enterprise policies; HubsSidebarEnabled: Edge reference.
        // Sources: https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        //          https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/hubssidebarenabled
        l.Add(new PrivacyTweak
        {
            Id = "edge-suggest-sidebar", Category = CatEdge, Risk = "Moderate", Level = 3,
            Title = "Edge: no search suggestions or sidebar",
            Description = "Stops Edge sending what you type in the address bar to the search engine for suggestions, and hides the sidebar. " +
                          "Side effect: no suggestions while typing in the address bar, and sidebar apps are gone.",
            Sources = ManageConnections + " " + EdgePolicy + "hubssidebarenabled",
            Reg =
            {
                D(HKLM, EdgeKey, "SearchSuggestEnabled", 0),
                D(HKLM, EdgeKey, "HubsSidebarEnabled", 0),
            },
        });

        // ======================================================== Updates & delivery

        // DODownloadMode: Policy CSP DeliveryOptimization (SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization),
        // 0 = HTTP only, no peering. Registry value name DODownloadMode as written by WinUtil.
        // Sources: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-deliveryoptimization
        //          https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        l.Add(new PrivacyTweak
        {
            Id = "delivery-optimisation", Category = CatUpdates, Risk = "Safe", Level = 2,
            Title = "Turn off peer-to-peer update sharing",
            Description = "Sets Delivery Optimisation to download from Microsoft only, so this PC does not upload update files to, or download them from, other PCs. " +
                          "Windows Update keeps working; updates may download more slowly on networks with several PCs.",
            Sources = CspDelivery + " " + WinUtilTelemetry,
            Reg = { D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0) },
        });

        // Offline maps: "Manage connections" section Offline maps.
        // Source: https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
        l.Add(new PrivacyTweak
        {
            Id = "maps-updates", Category = CatUpdates, Risk = "Safe", Level = 2,
            Title = "Stop automatic offline map updates",
            Description = "Stops Windows downloading and updating offline maps in the background, and the network traffic from the Offline maps settings page.",
            Sources = ManageConnections,
            Reg =
            {
                D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Maps", "AutoDownloadAndUpdateMapData", 0),
                D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Maps", "AllowUntriggeredNetworkTrafficOnSettingsPage", 0),
            },
        });

        // ======================================================== Lock screen & Start

        // RotatingLockScreenOverlayEnabled / SubscribedContent-338387 (PDQ). DisableThirdPartySuggestions: Policy CSP
        // Experience/AllowThirdPartySuggestionsInWindowsSpotlight (Pro and above).
        // Sources: https://www.pdq.com/blog/how-to-disable-ads-on-windows/
        //          https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
        l.Add(new PrivacyTweak
        {
            Id = "lockscreen-ads", Category = CatStart, Risk = "Safe", Level = 1,
            Title = "No fun facts or adverts on the lock screen",
            Description = "Removes the \"fun facts, tips, tricks and more\" overlay and third-party suggestions from Windows Spotlight. Spotlight pictures can stay.",
            Sources = PdqAds + " " + CspExperience,
            Reg =
            {
                D(HKCU, CDM, "RotatingLockScreenOverlayEnabled", 0),
                D(HKCU, CDM, "SubscribedContent-338387Enabled", 0),
                D(HKCU, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableThirdPartySuggestions", 1),
            },
        });

        // SubscribedContent-338388 + SystemPaneSuggestionsEnabled (WinUtil), Start_IrisRecommendations (PDQ).
        // Sources: https://winutil.christitus.com/dev/tweaks/essential-tweaks/telemetry
        //          https://www.pdq.com/blog/how-to-disable-ads-on-windows/
        l.Add(new PrivacyTweak
        {
            Id = "start-suggestions", Category = CatStart, Risk = "Safe", Level = 1,
            Title = "No promoted apps or recommendations in Start",
            Description = "Stops Start showing promoted apps and \"recommendations for tips, shortcuts, new apps and more\".",
            Sources = WinUtilTelemetry + " " + PdqAds,
            Reg =
            {
                D(HKCU, CDM, "SubscribedContent-338388Enabled", 0),
                D(HKCU, CDM, "SystemPaneSuggestionsEnabled", 0),
                D(HKCU, ExplorerAdvanced, "Start_IrisRecommendations", 0),
            },
        });

        // AllowNewsAndInterests: Policy CSP NewsAndInterests (SOFTWARE\Policies\Microsoft\Dsh), Windows 11 21H2+.
        // Source: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests
        l.Add(new PrivacyTweak
        {
            Id = "widgets", Category = CatStart, Risk = "Moderate", Level = 2,
            Title = "Turn off Widgets (news and interests)",
            Description = "Turns off the Widgets board and its taskbar button, which load the MSN news feed and adverts. " +
                          "Side effect: the weather and widgets on the taskbar are gone.",
            Sources = CspNews,
            Reg = { D(HKLM, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0) },
        });

        return l;
    }

    // ------------------------------------------------------------- system queries

    /// <summary>
    /// One batched PowerShell query for every task in the catalogue. Null if the query itself failed
    /// (every task came back missing), so the page can say "unknown" rather than "not present".
    /// </summary>
    public static async Task<Dictionary<string, string>> QueryTaskStatesAsync()
    {
        var paths = AllTasks;
        if (paths.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var d = await Change.GetTaskStatesAsync(paths);
            if (d == null || d.Count == 0 || d.Values.All(v => v == null)) return null;
            return d;
        }
        catch (Exception ex)
        {
            Log.Write("privacy task query: " + ex.Message);
            return null;
        }
    }

    static string PsQuote(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

    /// <summary>Which of the catalogue's firewall rules exist. Null if the firewall could not be read.</summary>
    public static async Task<HashSet<string>> QueryFirewallRulesAsync()
    {
        var names = All.SelectMany(t => t.Firewall).Select(f => f.RuleName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return set;
        try
        {
            var sb = new StringBuilder();
            foreach (var n in names)
                sb.Append("if(Get-NetFirewallRule -Name " + PsQuote(n) + " -EA SilentlyContinue){'FW|'+" + PsQuote(n) + "};");
            sb.Append("'FWDONE'");
            var r = await Shell.PowerShellAsync(sb.ToString(), 60_000);
            if (r.Output == null || !r.Output.Contains("FWDONE")) return null;
            foreach (var line in r.Output.Split('\n'))
            {
                string l = line.Trim();
                if (l.StartsWith("FW|", StringComparison.Ordinal)) set.Add(l.Substring(3));
            }
            return set;
        }
        catch (Exception ex)
        {
            Log.Write("privacy firewall query: " + ex.Message);
            return null;
        }
    }
}
