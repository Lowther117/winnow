using System.Text.RegularExpressions;

namespace Winnow.Core;

/// <summary>One preinstalled app (or family of apps) Winnow offers to remove.</summary>
public sealed class BloatEntry
{
    public string Pattern = "";       // Appx package Name; * is a wildcard
    public string Name = "";
    public string Description = "";
    public string Risk = BloatList.Safe;
    public bool Recommended;

    public BloatEntry(string pattern, string name, string risk, bool recommended, string description)
    {
        Pattern = pattern; Name = name; Risk = risk; Recommended = recommended; Description = description;
    }
}

/// <summary>
/// Curated list of Microsoft, OEM and promoted third-party apps that ship with
/// Windows 11 (24H2 / 25H2), with a plain-English note and a recommendation.
/// Only entries actually installed are shown. Package names that are not
/// present simply never match, so older names are harmless to keep.
/// </summary>
public static class BloatList
{
    public const string Safe = "Safe to remove";
    public const string KeepIfUsed = "Keep if you use it";
    public const string Moderate = "Moderate";
    public const string Advanced = "Advanced";

    public static readonly BloatEntry[] Entries =
    {
        // ---- Microsoft extras
        new("Microsoft.BingNews", "Microsoft News", Safe, true, "MSN news headlines app. Most stories open in Edge anyway."),
        new("Microsoft.BingWeather", "Weather (MSN)", Safe, true, "MSN Weather app. The taskbar and Widgets show weather without it."),
        new("Microsoft.BingFinance", "Money (MSN)", Safe, true, "Old MSN finance app from Windows 10."),
        new("Microsoft.BingSports", "Sports (MSN)", Safe, true, "Old MSN sports app from Windows 10."),
        new("Microsoft.BingTravel", "Travel (MSN)", Safe, true, "Old MSN travel app from Windows 10."),
        new("Microsoft.BingFoodAndDrink", "Food & Drink (MSN)", Safe, true, "Old MSN recipes app from Windows 10."),
        new("Microsoft.BingHealthAndFitness", "Health & Fitness (MSN)", Safe, true, "Old MSN fitness app from Windows 10."),
        new("Microsoft.BingTranslator", "Translator", Safe, true, "Old Bing Translator app."),
        new("Microsoft.BingSearch", "Bing Search", Moderate, false, "Adds Bing web results to Windows search. Searching for files, apps and settings keeps working without it; web results in Start may stop."),
        new("Microsoft.Getstarted", "Tips", Safe, true, "Windows tips and getting-started pages."),
        new("Microsoft.GetHelp", "Get Help", Moderate, false, "Microsoft's support app. Windows 11's troubleshooters in Settings now open through it, so removing it breaks 'Troubleshoot'."),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", Safe, true, "Card games with adverts unless you pay for Premium."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", Safe, true, "A launcher and advert for Microsoft 365. Removing it does not remove Word, Excel or other installed Office apps."),
        new("Microsoft.People", "People", Safe, true, "Old contacts app. Retired by Microsoft."),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", Safe, true, "Desktop automation tool aimed at businesses."),
        new("Microsoft.Todos", "Microsoft To Do", KeepIfUsed, false, "To-do lists synced with your Microsoft account."),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", Safe, true, "Sends feedback and bug reports to Microsoft."),
        new("Microsoft.ZuneMusic", "Media Player", KeepIfUsed, false, "Windows' built-in music and video player. Remove only if you use another player such as VLC."),
        new("Microsoft.ZuneVideo", "Films & TV", Safe, true, "Old video player and film shop. Media Player and VLC play the same files."),
        new("Clipchamp.Clipchamp", "Clipchamp", Safe, true, "Microsoft's online video editor. Needs a Microsoft account; exports are limited without a subscription."),
        new("MicrosoftCorporationII.QuickAssist", "Quick Assist", KeepIfUsed, false, "Lets someone you trust take control of your PC to help. Keep it if you give or receive remote help."),
        new("Microsoft.WindowsAlarms", "Clock", KeepIfUsed, false, "Alarms, timers, stopwatch and Focus sessions."),
        new("Microsoft.WindowsMaps", "Maps", Safe, true, "Offline maps app. Retired by Microsoft."),
        new("Microsoft.MicrosoftStickyNotes", "Sticky Notes", KeepIfUsed, false, "Desktop notes synced with OneNote and your Microsoft account."),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder", KeepIfUsed, false, "Simple voice and audio recorder."),
        new("Microsoft.549981C3F5F10", "Cortana", Safe, true, "The retired Cortana assistant app."),
        new("Microsoft.Copilot", "Copilot", Safe, true, "The Microsoft Copilot AI chat app. Copilot in Edge and on the web is unaffected."),
        new("Microsoft.Windows.Ai.Copilot.Provider", "Copilot provider (older builds)", Safe, true, "The Copilot side panel component from Windows 11 23H2."),
        new("Microsoft.Windows.AIHub", "AI Hub", Safe, true, "Copilot+ PC showcase of AI apps from the Store."),
        new("Microsoft.MicrosoftJournal", "Microsoft Journal", Safe, true, "Pen note-taking app for touch screens."),
        new("Microsoft.MicrosoftPowerBIForWindows", "Power BI", Safe, true, "Business dashboard viewer."),
        new("Microsoft.Office.Sway", "Sway", Safe, true, "Microsoft's web presentation app."),
        new("Microsoft.Office.OneNote", "OneNote for Windows 10", KeepIfUsed, false, "The old Store version of OneNote. The desktop OneNote is separate."),
        new("Microsoft.Messaging", "Messaging", Safe, true, "Old Windows 10 messaging app."),
        new("Microsoft.3DBuilder", "3D Builder", Safe, true, "Old 3D printing app."),
        new("Microsoft.Microsoft3DViewer", "3D Viewer", Safe, true, "Viewer for 3D models. Retired by Microsoft."),
        new("Microsoft.Print3D", "Print 3D", Safe, true, "Old 3D printing app."),
        new("Microsoft.MSPaint", "Paint 3D", Safe, true, "Paint 3D (retired). This is not the normal Paint app, which Winnow never offers to remove."),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal", Safe, true, "Setup for Windows Mixed Reality headsets, which Microsoft has retired."),
        new("Microsoft.OneConnect", "Mobile Plans", Safe, true, "Buys mobile data plans for SIM-equipped PCs."),
        new("Microsoft.SkypeApp", "Skype", Safe, true, "Skype closed in May 2025."),
        new("Microsoft.NetworkSpeedTest", "Network Speed Test", Safe, true, "Old Bing speed test app."),
        new("Microsoft.windowscommunicationsapps", "Mail and Calendar", Safe, true, "The old Mail and Calendar apps, which stopped syncing at the end of 2024."),
        new("Microsoft.PCManager", "PC Manager", Safe, true, "Microsoft's clean-up and 'boost' app."),
        new("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family Safety", Safe, true, "Parental controls dashboard. Keep it if you manage a child's account."),
        new("Microsoft.OutlookForWindows", "Outlook (new)", KeepIfUsed, false, "The new Outlook for Windows mail app. Classic Outlook from Microsoft 365 is separate."),
        new("MSTeams", "Microsoft Teams", KeepIfUsed, false, "The current Teams app, for work, school and personal accounts."),
        new("MicrosoftTeams", "Teams Chat (personal)", Safe, true, "The older personal Teams chat that came with Windows 11 22H2."),
        new("Microsoft.YourPhone", "Phone Link", KeepIfUsed, false, "Shows your phone's texts, calls and photos on the PC."),
        new("Microsoft.Windows.DevHome", "Dev Home", Safe, true, "Developer dashboard. Microsoft retired it in 2025."),
        new("Microsoft.Edge.GameAssist", "Edge Game Assist", KeepIfUsed, false, "An Edge browser inside Game Bar for guides while you play."),
        new("MicrosoftWindows.Client.WebExperience", "Widgets", Moderate, false, "Powers the taskbar Widgets board (news, weather, sport). Removing it removes Widgets; Windows updates may bring it back."),

        // ---- Xbox (you play PC games: these stay unticked)
        new("Microsoft.GamingApp", "Xbox app", Advanced, false, "Needed for PC Game Pass, the Xbox store and installing Game Pass games."),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", Advanced, false, "Win+G overlay: capture, performance widgets and party chat. Some games' recording features use it."),
        new("Microsoft.XboxGameOverlay", "Xbox Game Overlay", Advanced, false, "Part of Game Bar used by some games for overlays."),
        new("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", Advanced, false, "Signs games in to Xbox Live. Removing it breaks sign-in in Game Pass and many Microsoft-published games."),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text", Advanced, false, "Speech-to-text for Xbox party chat."),
        new("Microsoft.Xbox.TCUI", "Xbox TCUI", Advanced, false, "Xbox friends and profile pop-ups that games use."),
        new("Microsoft.XboxApp", "Xbox Console Companion", Advanced, false, "The old Xbox app for Windows 10."),

        // ---- Promoted third-party apps
        new("*LinkedInforWindows", "LinkedIn", Safe, true, "LinkedIn app placed on the Start menu."),
        new("SpotifyAB.SpotifyMusic", "Spotify (Store)", KeepIfUsed, false, "Spotify from the Store. Remove it if it was put there for you and you do not use it."),
        new("Disney.37853FC22B2CE", "Disney+", Safe, true, "Promoted streaming app."),
        new("4DF9E0F8.Netflix", "Netflix", KeepIfUsed, false, "Netflix app. The website works the same."),
        new("AmazonVideo.PrimeVideo", "Prime Video", Safe, true, "Promoted streaming app."),
        new("Amazon.com.Amazon", "Amazon", Safe, true, "Promoted shopping app."),
        new("BytedancePte.Ltd.TikTok", "TikTok", Safe, true, "Promoted app."),
        new("Facebook.Instagram*", "Instagram", Safe, true, "Promoted app."),
        new("Facebook.Facebook*", "Facebook", Safe, true, "Promoted app."),
        new("*Twitter*", "X (Twitter)", Safe, true, "Promoted app."),
        new("king.com.*", "King games (Candy Crush and others)", Safe, true, "Promoted free-to-play games."),
        new("*Duolingo*", "Duolingo", Safe, true, "Promoted app."),
        new("*Flipboard*", "Flipboard", Safe, true, "Promoted news app."),
        new("*AdobePhotoshopExpress*", "Adobe Photoshop Express", Safe, true, "Promoted photo app."),
        new("*PicsArt*", "PicsArt", Safe, true, "Promoted photo app."),
        new("*McAfee*", "McAfee (Store)", Safe, true, "Promoted antivirus trial. Windows Security already protects the PC."),
        new("ROBLOXCORPORATION.ROBLOX", "Roblox", KeepIfUsed, false, "Roblox game platform."),
        new("5319275A.WhatsAppDesktop", "WhatsApp", KeepIfUsed, false, "WhatsApp for Windows."),

        // ---- PC makers
        new("AD2F1837.*", "HP apps", Moderate, false, "HP's preinstalled apps. Some (Support Assistant, Power Manager) handle drivers, BIOS updates or battery settings."),
        new("DellInc.*", "Dell apps", Moderate, false, "Dell's preinstalled apps. SupportAssist handles driver and BIOS updates."),
        new("E046963F.LenovoCompanion", "Lenovo Vantage", Moderate, false, "Lenovo's settings and updates app: battery modes, driver and BIOS updates."),
        new("LenovoCompanyLimited.*", "Lenovo apps", Moderate, false, "Lenovo's preinstalled apps and services."),
    };

    /// <summary>Shown on the page: what Winnow deliberately never offers.</summary>
    public const string NeverOffered =
        "Deliberately not offered: Microsoft Store, App Installer (winget), Photos, Calculator, Notepad, Terminal, Paint, " +
        "Snipping Tool, Windows Security, Edge WebView2, the Start menu and shell components (StartExperiencesApp, " +
        "ShellExperienceHost) and app frameworks (VCLibs, UI.Xaml, .NET Native, Windows App Runtime). Removing these breaks Windows or other apps.";

    /// <summary>Packages that the Store apps tab warns about before removal.</summary>
    static readonly string[] ProtectedPatterns =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Windows.Photos",
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.WindowsTerminal", "Microsoft.Paint",
        "Microsoft.ScreenSketch", "Microsoft.SecHealthUI", "Microsoft.Windows.SecHealthUI", "Microsoft.StartExperiencesApp",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Win32WebViewHost",
        "Microsoft.MicrosoftEdge*", "Microsoft.WebExperience*", "*WebView*", "Microsoft.VCLibs*", "Microsoft.UI.Xaml*",
        "Microsoft.NET.Native*", "Microsoft.WindowsAppRuntime*", "Microsoft.Services.Store.Engagement",
        "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.Windows.CloudExperienceHost", "Microsoft.LockApp", "*ImageExtension", "*VideoExtension*", "Microsoft.HEVCVideoExtension",
        "Microsoft.WebMediaExtensions", "Microsoft.RawImageExtension", "Microsoft.VP9VideoExtensions", "Microsoft.AV1VideoExtension",
    };

    static readonly Dictionary<string, Regex> _rx = new(StringComparer.OrdinalIgnoreCase);

    public static bool Matches(string pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(name)) return false;
        if (pattern.IndexOf('*') < 0) return pattern.Equals(name, StringComparison.OrdinalIgnoreCase);
        Regex rx;
        lock (_rx)
        {
            if (!_rx.TryGetValue(pattern, out rx))
            {
                rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                _rx[pattern] = rx;
            }
        }
        return rx.IsMatch(name);
    }

    /// <summary>True for Windows components and frameworks the Store tab should warn about.</summary>
    public static bool IsProtected(string packageName) => ProtectedPatterns.Any(p => Matches(p, packageName));
}
