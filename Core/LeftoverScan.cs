using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Winnow.Core;

public enum LeftoverKind { Folder, EmptyFolder, RegistryKey, UninstallEntry }

/// <summary>One thing an uninstalled program appears to have left behind.</summary>
public sealed class LeftoverItem : INotifyPropertyChanged
{
    bool _selected;
    string _status = "";

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; On(nameof(Selected)); } }
    }

    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; On(nameof(Status)); } }
    }

    public LeftoverKind Kind { get; set; }
    public string Location { get; set; } = "";     // folder path, or the full registry key (HKEY_...)
    public string Hive { get; set; } = "";         // HKLM | HKCU for registry kinds
    public string KeyPath { get; set; } = "";      // key path under the hive
    public long? SizeBytes { get; set; }
    public DateTime? Modified { get; set; }
    public string Confidence { get; set; } = "Low";   // High | Medium | Low
    public string Reason { get; set; } = "";

    public string KindText => Kind switch
    {
        LeftoverKind.Folder => "Folder",
        LeftoverKind.EmptyFolder => "Empty folder",
        LeftoverKind.RegistryKey => "Registry key",
        _ => "Uninstall entry",
    };
    public bool IsRegistry => Kind == LeftoverKind.RegistryKey || Kind == LeftoverKind.UninstallEntry;
    public string SizeText => SizeBytes.HasValue ? Fmt.Bytes(SizeBytes.Value) : Fmt.Na;
    public long SizeSort => SizeBytes ?? -1;
    public string ModifiedText => Modified.HasValue ? Modified.Value.ToString("yyyy-MM-dd") : Fmt.Na;
    public string ModifiedSort => Modified.HasValue ? Modified.Value.ToString("yyyyMMddHHmmss") : "";
    public int ConfidenceRank => Confidence == "High" ? 3 : Confidence == "Medium" ? 2 : 1;

    public event PropertyChangedEventHandler PropertyChanged;
    void On(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Look only for remnants of this program (used straight after an uninstall).</summary>
public sealed class LeftoverFocus
{
    public string Name = "";
    public string Publisher = "";
    public string InstallLocation = "";
}

public sealed class LeftoverResult
{
    public List<LeftoverItem> Items = new();
    public List<string> Notes = new();
}

/// <summary>
/// Finds folders and registry keys left behind by software that is no longer
/// installed. Deliberately conservative: anything that matches an installed
/// program, a Store app, a running process, a service, a startup entry or a
/// scheduled task is left out, and Windows / Microsoft / driver locations are
/// never proposed at all.
/// </summary>
public static class LeftoverScan
{
    // Folder names that are never proposed, wherever they are.
    static readonly HashSet<string> DenyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Intel", "AMD", "ATI", "ATI Technologies", "NVIDIA", "NVIDIA Corporation", "Realtek",
        "Dolby", "DTS", "Waves", "Conexant", "Synaptics", "Elan", "Elantech", "Qualcomm", "MediaTek", "Broadcom",
        "Killer Networking", "Rivet Networks", "Thunderbolt", "Package Cache", "Packages", "Temp", "Tmp",
        "Common Files", "Uninstall Information", "Internet Explorer", "Reference Assemblies", "MSBuild", "dotnet",
        "ModifiableWindowsApps", "WindowsApps", "WindowsPowerShell", "PowerShell", "Windows Defender",
        "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player", "Windows NT",
        "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "Desktop",
        "Documents", "Favorites", "Start Menu", "Templates", "Application Data", "History", "Comms",
        "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache", "PeerDistRepub", "Publishers", "VirtualStore",
        "PlaceholderTileLogoFolder", "Programs", "ssh", "SoftwareDistribution", "USOPrivate", "USOShared",
        "WindowsHolographicDevices", "PackageManagement", "NuGet", "pip", "npm", "npm-cache", "node-gyp", "pnpm",
        "Yarn", "electron", "electron-builder", "ms-playwright", "go-build", "Downloaded Installations",
        "IsolatedStorage", "assembly", "SquirrelTemp", "fontconfig", "GroupPolicy", "ElevatedDiagnostics",
        "Diagnosis", "DeviceSync", "Temporary Internet Files", "WinGet", "chocolatey", "OneDrive", "Edge",
        "drivers", "System32", "SysWOW64", "WinSxS", "Microsoft Shared", "Microsoft.NET", "Windows Kits",
        "Microsoft Help", "regid.1991-06.com.microsoft", "Package Store", "PawnIO", "Winnow-removed-registry",
    };

    // Registry keys under Software that are never proposed.
    static readonly HashSet<string> DenyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Classes", "Clients", "Policies", "RegisteredApplications", "ODBC", "Wow6432Node",
        "Intel", "AMD", "ATI", "ATI Technologies", "NVIDIA Corporation", "NVIDIA", "Realtek", "Realtek Semiconductor Corp.",
        "Dolby", "DTS", "Waves Audio", "Khronos", "Partner", "Setup", "System", "AppDataLow", "DefaultUserEnvironment",
        "Description", "Hardware", "OEM", "OpenSSH", "SyncIntegrationClients", "IM Providers", "Qualcomm", "Synaptics",
        "Elantech", "Conexant", "MozillaPlugins", "Chromium", "Package Cache", "Packages", "Temp",
    };

    // Words too generic to tie a folder to a program.
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "software", "corporation", "corp", "company", "systems", "system", "technologies", "technology", "games", "game",
        "studio", "studios", "group", "limited", "international", "entertainment", "digital", "media", "online",
        "interactive", "solutions", "update", "updater", "updates", "helper", "service", "services", "manager", "tools",
        "tool", "driver", "drivers", "client", "desktop", "player", "edition", "version", "runtime", "framework",
        "library", "launcher", "data", "user", "users", "temp", "cache", "caches", "logs", "config", "settings",
        "common", "commonfiles", "shared", "files", "plugins", "plugin", "program", "programs", "apps", "application",
        "applications", "setup", "install", "installer", "installation", "uninstall", "windows", "win64", "win32",
        "x64", "x86", "64bit", "32bit", "free", "home", "portable", "local", "roaming", "locallow", "appdata",
        "programdata", "programfiles", "programfilesx86", "default", "support", "center", "centre", "utility",
        "utilities", "package", "packages", "components", "component", "core", "host", "agent", "browser", "viewer",
        "editor", "plus", "beta", "preview", "release", "stable", "system32", "syswow64", "current", "bin", "lib",
        "resources", "assets", "app", "the", "and", "for", "with", "inc", "ltd", "llc", "gmbh",
    };

    static readonly string[] CompanyWords =
    {
        "inc", "ltd", "llc", "gmbh", "corp", "corporation", "co", "limited", "sro", "srl", "sa", "ag", "bv", "plc", "pty",
        "oy", "ab", "as", "kk", "software", "technologies", "technology", "systems", "group", "studios", "studio",
        "games", "entertainment", "interactive", "company", "the",
    };

    static readonly Regex Paren = new(@"\([^)]*\)|\[[^\]]*\]");
    static readonly Regex TrailVersion = new(@"[\s_\-]+v?\d+(?:[\.\-_]\d+)*.*$");
    static readonly Regex WordSplit = new(@"[^a-z0-9]+");
    static readonly Regex TaskCommand = new(@"<Command>([^<]+)</Command>", RegexOptions.IgnoreCase);

    // ================================================================ helpers

    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    static string StripVersion(string s)
    {
        s = Paren.Replace(s ?? "", " ");
        s = TrailVersion.Replace(s, "");
        return s.Trim();
    }

    static string StripCompany(string publisher)
    {
        var words = WordSplit.Split((publisher ?? "").ToLowerInvariant()).Where(w => w.Length > 0 && !CompanyWords.Contains(w));
        return string.Concat(words);
    }

    static IEnumerable<string> WordsOf(string s) =>
        WordSplit.Split((s ?? "").ToLowerInvariant())
                 .Where(w => w.Length >= 4 && !Stop.Contains(w) && !w.All(char.IsDigit));

    static string Lower(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return (path ?? "").TrimEnd('\\').ToLowerInvariant(); }
    }

    static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        string p = Lower(path), f = Lower(folder);
        return p == f || p.StartsWith(f + "\\");
    }

    /// <summary>What is installed, running or referenced right now.</summary>
    sealed class Known
    {
        public readonly HashSet<string> Keys = new();
        public readonly HashSet<string> Words = new();
        public readonly List<string> InUsePaths = new();
        readonly HashSet<string> _stop;

        public Known(HashSet<string> stop) { _stop = stop; }

        public void AddName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            foreach (var v in new[] { Norm(s), Norm(StripVersion(s)) })
                if (v.Length >= 3 && !_stop.Contains(v)) Keys.Add(v);
            foreach (var w in WordsOf(s)) if (!_stop.Contains(w)) Words.Add(w);
        }

        public void AddCompany(string publisher)
        {
            if (string.IsNullOrWhiteSpace(publisher)) return;
            AddName(publisher);
            string c = StripCompany(publisher);
            if (c.Length >= 3 && !_stop.Contains(c)) Keys.Add(c);
        }

        /// <summary>Record a path that is in use, and its folder names as known names.</summary>
        public void AddPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (!Path.IsPathRooted(path)) return;
                string full = Lower(path);
                if (full.Length < 4) return;
                InUsePaths.Add(full);
                var segs = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < segs.Length; i++)
                {
                    string seg = segs[i];
                    if (i == segs.Length - 1 && Path.HasExtension(seg)) seg = Path.GetFileNameWithoutExtension(seg);
                    string n = Norm(seg);
                    if (n.Length >= 3 && !_stop.Contains(n)) Keys.Add(n);
                    foreach (var w in WordsOf(seg)) if (!_stop.Contains(w)) Words.Add(w);
                }
            }
            catch { }
        }

        public bool IsKnownName(string name)
        {
            string n = Norm(name);
            if (n.Length < 3) return true;                 // too short to judge safely
            if (Keys.Contains(n)) return true;
            foreach (var k in Keys)
            {
                if (n.Length >= 4 && k.Contains(n)) return true;
                if (k.Length >= 4 && n.Contains(k)) return true;
            }
            foreach (var w in WordsOf(name)) if (Words.Contains(w)) return true;
            return false;
        }

        /// <summary>True when something in use lives in (or is) this folder.</summary>
        public bool InUse(string folder)
        {
            string c = Lower(folder);
            foreach (var p in InUsePaths)
                if (p == c || p.StartsWith(c + "\\")) return true;
            return false;
        }
    }

    // ================================================================ safety

    static IEnumerable<string> SpecialFolders()
    {
        var list = new List<string>
        {
            Paths.WindowsDir, Paths.ProgramFiles, Paths.ProgramFilesX86, Paths.ProgramData, Paths.LocalAppData, Paths.RoamingAppData,
            Path.Combine(Paths.LocalAppData, "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Paths.Downloads, Paths.AppDir,
        };
        return list.Where(s => !string.IsNullOrEmpty(s));
    }

    /// <summary>Locations Winnow never deletes, whatever the scan says.</summary>
    public static bool IsNeverTouch(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return true;
            string full = Lower(path);
            string root = Lower(Path.GetPathRoot(path) ?? "");
            if (full == root || full.Length <= 3) return true;
            if (IsUnder(full, Paths.WindowsDir)) return true;
            // the folder is, or contains, one of the special folders
            foreach (var sf in SpecialFolders())
            {
                string s = Lower(sf);
                if (s == full || s.StartsWith(full + "\\")) return true;
            }
            var segs = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < segs.Length; i++)
            {
                string seg = segs[i];
                if (seg.StartsWith("microsoft", StringComparison.OrdinalIgnoreCase) ||
                    seg.StartsWith("windows", StringComparison.OrdinalIgnoreCase) ||
                    seg.StartsWith("regid.", StringComparison.OrdinalIgnoreCase)) return true;
                if (seg is "system32" or "syswow64" or "winsxs" or "drivers" or "common files" or "package cache" or "packages" or "windowsapps")
                    return true;
            }
            string name = Path.GetFileName(full);
            return DenyNames.Contains(name);
        }
        catch { return true; }
    }

    static bool IsNeverTouchKey(LeftoverItem item)
    {
        string kp = (item.KeyPath ?? "").Trim('\\');
        var segs = kp.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2 || !segs[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Kind == LeftoverKind.UninstallEntry)
            return kp.IndexOf(@"\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase) < 0 ||
                   kp.EndsWith(@"\Uninstall", StringComparison.OrdinalIgnoreCase);
        int i = 1;
        if (segs[i].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) i++;
        if (i >= segs.Length) return true;
        return DenyKeys.Contains(segs[i]) || segs[i].StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
               segs[i].StartsWith("Windows", StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ in-use evidence

    static List<string> RunningPaths()
    {
        var list = new List<string>();
        try
        {
            var snap = Sampler.Latest;
            if (snap != null)
                foreach (var ps in snap.Processes.ToList())
                {
                    try
                    {
                        var d = ProcessInfo.Get(ps);
                        if (!string.IsNullOrEmpty(d.Path)) list.Add(d.Path);
                    }
                    catch { }
                }
        }
        catch { }
        if (list.Count == 0)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string f = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(f)) list.Add(f);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return list;
    }

    static IEnumerable<string> ServicePaths()
    {
        var list = new List<string>();
        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (svc == null) return list;
            foreach (var name in svc.GetSubKeyNames())
            {
                try
                {
                    using var s = svc.OpenSubKey(name);
                    if (s == null) continue;
                    if (s.GetValue("ImagePath") is string ip) list.Add(AppInventory.ExtractPath(ip));
                    using var prm = s.OpenSubKey("Parameters");
                    if (prm?.GetValue("ServiceDll") is string dll) list.Add(AppInventory.ExtractPath(dll));
                }
                catch { }
            }
        }
        catch { }
        return list.Where(p => p != null);
    }

    static IEnumerable<string> StartupPaths()
    {
        var list = new List<string>();
        var keys = new (RegistryKey root, string path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        };
        foreach (var (root, path) in keys)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) continue;
                foreach (var v in k.GetValueNames())
                    if (k.GetValue(v) is string s) list.Add(AppInventory.ExtractPath(s));
            }
            catch { }
        }
        return list.Where(p => p != null);
    }

    static IEnumerable<string> TaskPaths(CancellationToken ct)
    {
        var list = new List<string>();
        try
        {
            string dir = Path.Combine(Paths.WindowsDir, "System32", "Tasks");
            if (!Directory.Exists(dir)) return list;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (++n > 5000) break;
                try
                {
                    string xml = File.ReadAllText(f);
                    foreach (Match m in TaskCommand.Matches(xml))
                        list.Add(AppInventory.ExtractPath(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)));
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return list.Where(p => p != null);
    }

    // ================================================================ measuring

    static (long bytes, DateTime? newest, int files, bool complete) Measure(string dir, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0, dirs = 0;
        bool complete = true;
        DateTime newest = DateTime.MinValue;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(dir));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            if (++dirs > 50_000 || files > 500_000) { complete = false; break; }
            try
            {
                if (d.LastWriteTime > newest) newest = d.LastWriteTime;
                foreach (var fsi in d.EnumerateFileSystemInfos())
                {
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (fsi is FileInfo fi)
                    {
                        bytes += fi.Length;
                        files++;
                        if (fi.LastWriteTime > newest) newest = fi.LastWriteTime;
                    }
                    else if (fsi is DirectoryInfo sub) stack.Push(sub);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { complete = false; }
        }
        return (bytes, newest == DateTime.MinValue ? (DateTime?)null : newest, files, complete);
    }

    // ================================================================ the scan

    sealed class ScanRoot
    {
        public string Folder = "";
        public string Label = "";
        public string Confidence = "Low";     // for general findings here
        public bool ProgramRoot;              // Program Files style: empty folders are High
        public bool AppData;                  // settings and saved games may live here
    }

    /// <summary>
    /// Run the scan. Call from a background thread. focus == null scans for remnants of anything
    /// no longer installed; otherwise only for the named program.
    /// </summary>
    public static LeftoverResult Run(LeftoverFocus focus, IEnumerable<string> storeNames, IProgress<string> progress, CancellationToken ct)
    {
        var result = new LeftoverResult();
        var stop = new HashSet<string>(Stop, StringComparer.OrdinalIgnoreCase);
        string user = Norm(Environment.UserName);
        if (user.Length > 0) stop.Add(user);
        var known = new Known(stop);

        // ---- what is installed
        progress?.Report("Reading installed programs…");
        var programs = AppInventory.ReadPrograms();
        // Hidden entries (SystemComponent / ParentKeyName: shared runtimes, components, updates) are never
        // proposed as stale, but their names and folders still count as "in use".
        foreach (var p in AppInventory.ReadPrograms(includeHidden: true))
        {
            known.AddName(p.Name);
            known.AddCompany(p.Publisher);
            known.AddPath(p.InstallLocation);
            known.AddPath(AppInventory.ExtractPath(p.UninstallString));
            known.AddPath(AppInventory.ExtractPath(p.QuietUninstallString));
            known.AddPath(AppInventory.ExtractPath(p.DisplayIcon));
        }
        ct.ThrowIfCancellationRequested();

        progress?.Report("Reading Store apps…");
        var stores = new List<string>();
        if (storeNames != null) stores.AddRange(storeNames);
        try
        {
            string pk = Path.Combine(Paths.LocalAppData, "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.GetDirectories(pk))
                {
                    string fam = Path.GetFileName(d);
                    int us = fam.IndexOf('_');
                    stores.Add(us > 0 ? fam.Substring(0, us) : fam);
                }
        }
        catch { }
        foreach (var s in stores.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            known.AddName(s);
            foreach (var part in s.Split('.')) known.AddName(part);
        }

        progress?.Report("Checking running programs, services, startup entries and scheduled tasks…");
        foreach (var p in RunningPaths()) known.AddPath(p);
        foreach (var p in ServicePaths()) known.AddPath(p);
        foreach (var p in StartupPaths()) known.AddPath(p);
        foreach (var p in TaskPaths(ct)) known.AddPath(p);
        known.AddPath(Paths.AppDir);
        ct.ThrowIfCancellationRequested();

        // ---- folders
        var roots = new List<ScanRoot>
        {
            new ScanRoot { Folder = Paths.ProgramFiles, Label = "Program Files", Confidence = "Medium", ProgramRoot = true },
            new ScanRoot { Folder = Paths.ProgramFilesX86, Label = "Program Files (x86)", Confidence = "Medium", ProgramRoot = true },
            new ScanRoot { Folder = Path.Combine(Paths.LocalAppData, "Programs"), Label = @"AppData\Local\Programs", Confidence = "Medium", ProgramRoot = true },
            new ScanRoot { Folder = Paths.ProgramData, Label = "ProgramData", Confidence = "Medium" },
            new ScanRoot { Folder = Paths.RoamingAppData, Label = @"AppData\Roaming", Confidence = "Low", AppData = true },
            new ScanRoot { Folder = Paths.LocalAppData, Label = @"AppData\Local", Confidence = "Low", AppData = true },
        };
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(Path.Combine(Paths.ProgramData, "Package Cache")))
            result.Notes.Add(@"ProgramData\Package Cache holds installers that Windows needs to repair or uninstall programs, so Winnow leaves it alone. " +
                             "Use Windows' own Disk Clean-up if you need that space.");

        // Focus: the program's own install folder, if the uninstaller left it.
        if (focus != null && !string.IsNullOrWhiteSpace(focus.InstallLocation))
        {
            string loc = focus.InstallLocation.Trim().Trim('"');
            try
            {
                if (Path.IsPathRooted(loc) && Directory.Exists(loc) && !IsNeverTouch(loc) && !known.InUse(loc))
                {
                    // Only pre-tick (High) when the folder is plainly the product's own, not a shared vendor folder
                    // such as "C:\Program Files\Contoso" that other products from the same publisher may use.
                    string folder = Norm(Path.GetFileName(loc.TrimEnd('\\')));
                    string product = Norm(focus.Name), productBare = Norm(StripVersion(focus.Name));
                    bool ownFolder = folder.Length >= 3 &&
                        (folder == product || folder == productBare ||
                         (productBare.Length >= 4 && folder.Contains(productBare)) || (product.Length >= 4 && folder.Contains(product)));
                    AddFolder(result, seenItems, loc, ownFolder ? "High" : "Medium",
                              "The uninstaller left " + focus.Name + "'s install folder behind", false, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(root.Folder) || !seenRoots.Add(Lower(root.Folder)) || !Directory.Exists(root.Folder)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root.Folder); }
            catch { continue; }
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("Checking " + dir);
                string name;
                try
                {
                    var di = new DirectoryInfo(dir);
                    var attr = di.Attributes;
                    if ((attr & FileAttributes.ReparsePoint) != 0 || (attr & FileAttributes.System) != 0) continue;
                    name = di.Name;
                }
                catch { continue; }
                if (IsNeverTouch(dir) || known.InUse(dir)) continue;

                if (focus != null)
                {
                    FocusFolder(result, seenItems, known, focus, dir, name, root, ct);
                    continue;
                }

                if (known.IsKnownName(name)) continue;
                string conf = root.Confidence;
                if (name.StartsWith("{") || name.StartsWith(".")) conf = "Low";
                string reason = "No installed program matches '" + name + "'";
                if (root.AppData) reason += " - may hold settings or saved games";
                AddFolder(result, seenItems, dir, conf, reason, root.ProgramRoot, ct);
            }
        }

        // ---- registry
        progress?.Report("Checking the registry…");
        var regRoots = new[]
        {
            ("HKCU", @"Software"),
            ("HKLM", @"SOFTWARE"),
            ("HKLM", @"SOFTWARE\WOW6432Node"),
        };
        foreach (var (hive, path) in regRoots)
        {
            ct.ThrowIfCancellationRequested();
            string[] names;
            try
            {
                using var k = Change.Root(hive).OpenSubKey(path);
                if (k == null) continue;
                names = k.GetSubKeyNames();
            }
            catch { continue; }
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                if (DenyKeys.Contains(name) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                string keyPath = path + "\\" + name;
                if (focus != null)
                {
                    FocusKey(result, seenItems, known, focus, hive, keyPath, name);
                    continue;
                }
                if (known.IsKnownName(name)) continue;
                AddKey(result, seenItems, LeftoverKind.RegistryKey, hive, keyPath, "Low", "No installed program matches '" + name + "'");
            }
        }

        // ---- orphaned Uninstall entries (general scan only)
        if (focus == null)
        {
            progress?.Report("Checking for stale entries in the installed programs list…");
            foreach (var p in programs)
            {
                if (p.IsMsi) continue;
                string cmd = string.IsNullOrWhiteSpace(p.UninstallString) ? p.QuietUninstallString : p.UninstallString;
                if (string.IsNullOrWhiteSpace(cmd) || cmd.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string exe = AppInventory.ExtractPath(cmd);
                if (exe == null || exe.StartsWith(@"\\")) continue;
                try
                {
                    string drive = Path.GetPathRoot(exe);
                    if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) continue;   // drive not connected: can't tell
                    if (IsUnder(exe, Paths.WindowsDir) || File.Exists(exe)) continue;
                    bool hasLoc = p.InstallLocation.Length > 0 && Path.IsPathRooted(p.InstallLocation);
                    if (hasLoc)
                    {
                        string locDrive = Path.GetPathRoot(p.InstallLocation);
                        if (string.IsNullOrEmpty(locDrive) || !Directory.Exists(locDrive)) continue;
                        if (Directory.Exists(p.InstallLocation)) continue;
                    }
                    AddKey(result, seenItems, LeftoverKind.UninstallEntry, p.Hive, p.KeyPath, hasLoc ? "High" : "Medium",
                        "'" + p.Name + "' is listed as installed, but its uninstaller" + (hasLoc ? " and install folder are" : " is") +
                        " missing (" + exe + ")");
                }
                catch { }
            }
        }

        result.Items = result.Items
            .OrderByDescending(i => i.ConfidenceRank)
            .ThenBy(i => i.IsRegistry)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var i in result.Items) i.Selected = i.Confidence == "High";
        return result;
    }

    // 3 = exact product name, 2 = product name partly, 1 = publisher, 0 = nothing
    static int FocusMatch(LeftoverFocus f, string name)
    {
        string n = Norm(name);
        if (n.Length < 3) return 0;
        var products = new List<string> { Norm(f.Name), Norm(StripVersion(f.Name)) };
        if (!string.IsNullOrWhiteSpace(f.InstallLocation))
        {
            try { products.Add(Norm(Path.GetFileName(f.InstallLocation.Trim().Trim('"').TrimEnd('\\')))); } catch { }
        }
        products = products.Where(p => p.Length >= 3).Distinct().ToList();
        if (products.Any(p => p == n)) return 3;
        if (products.Any(p => (n.Length >= 4 && p.Contains(n)) || (p.Length >= 4 && n.Contains(p)))) return 2;
        var pubs = new[] { Norm(f.Publisher), StripCompany(f.Publisher) }.Where(p => p.Length >= 3).Distinct();
        if (pubs.Any(p => p == n || (n.Length >= 4 && p.Contains(n)) || (p.Length >= 4 && n.Contains(p)))) return 1;
        return 0;
    }

    static void FocusFolder(LeftoverResult result, HashSet<string> seen, Known known, LeftoverFocus focus, string dir, string name,
                            ScanRoot root, CancellationToken ct)
    {
        int fm = FocusMatch(focus, name);
        if (fm == 0) return;
        if (known.IsKnownName(name))
        {
            // A folder still used by other installed products: look one level down for this product only.
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                string sn = Path.GetFileName(sub);
                int sm = FocusMatch(focus, sn);
                if (sm < 2 || known.IsKnownName(sn) || known.InUse(sub) || IsNeverTouch(sub)) continue;
                try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                AddFolder(result, seen, sub, sm == 3 ? "High" : "Medium", "Named after " + focus.Name + ", which is no longer installed", false, ct);
            }
            return;
        }
        string conf = fm == 3 ? "High" : "Medium";
        string reason = fm == 1
            ? "Named after " + focus.Publisher + ", and no other installed program is from them"
            : "Named after " + focus.Name + ", which is no longer installed";
        if (root.AppData) reason += " - may hold settings or saved games";
        AddFolder(result, seen, dir, conf, reason, false, ct);
    }

    static void FocusKey(LeftoverResult result, HashSet<string> seen, Known known, LeftoverFocus focus, string hive, string keyPath, string name)
    {
        int fm = FocusMatch(focus, name);
        if (fm == 0) return;
        if (known.IsKnownName(name))
        {
            string[] subs;
            try
            {
                using var k = Change.Root(hive).OpenSubKey(keyPath);
                if (k == null) return;
                subs = k.GetSubKeyNames();
            }
            catch { return; }
            foreach (var sn in subs)
            {
                int sm = FocusMatch(focus, sn);
                if (sm < 2 || known.IsKnownName(sn)) continue;
                AddKey(result, seen, LeftoverKind.RegistryKey, hive, keyPath + "\\" + sn, sm == 3 ? "High" : "Medium",
                    "Settings for " + focus.Name + ", which is no longer installed");
            }
            return;
        }
        AddKey(result, seen, LeftoverKind.RegistryKey, hive, keyPath, fm == 3 ? "High" : "Medium",
            fm == 1 ? "Settings key for " + focus.Publisher + ", and no other installed program is from them"
                    : "Settings for " + focus.Name + ", which is no longer installed");
    }

    static void AddFolder(LeftoverResult result, HashSet<string> seen, string dir, string conf, string reason, bool programRoot, CancellationToken ct)
    {
        if (!seen.Add(Lower(dir))) return;
        var (bytes, newest, files, complete) = Measure(dir, ct);
        var item = new LeftoverItem
        {
            Kind = LeftoverKind.Folder, Location = dir, SizeBytes = bytes, Modified = newest, Confidence = conf, Reason = reason,
        };
        if (files == 0 && complete)
        {
            item.Kind = LeftoverKind.EmptyFolder;
            item.Confidence = programRoot ? "High" : (conf == "High" ? "High" : "Medium");
            item.Reason = "Empty folder. " + reason;
        }
        if (!complete)
        {
            item.Reason += " (some files could not be read)";
            if (item.Confidence == "High") item.Confidence = "Medium";
        }
        result.Items.Add(item);
    }

    static void AddKey(LeftoverResult result, HashSet<string> seen, LeftoverKind kind, string hive, string keyPath, string conf, string reason)
    {
        string full = (hive == "HKCU" ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\") + keyPath;
        if (!seen.Add(full)) return;
        var item = new LeftoverItem { Kind = kind, Hive = hive, KeyPath = keyPath, Location = full, Confidence = conf, Reason = reason };
        if (IsNeverTouchKey(item)) return;
        result.Items.Add(item);
    }

    // ================================================================ removal

    /// <summary>
    /// Folders go to the Recycle Bin; registry keys are exported to a .reg file
    /// beside Winnow.exe first and only deleted if the export worked.
    /// </summary>
    public static async Task<(bool ok, string message)> RemoveAsync(LeftoverItem item)
    {
        try
        {
            return item.IsRegistry ? await RemoveKeyAsync(item) : await RemoveFolderAsync(item.Location);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    static async Task<(bool ok, string message)> RemoveFolderAsync(string path)
    {
        if (IsNeverTouch(path)) return (false, "Winnow never removes this location");
        if (!Directory.Exists(path)) return (true, "already gone");
        var running = await Task.Run(() => RunningPaths());
        string lp = Lower(path);
        if (running.Any(r => Lower(r).StartsWith(lp + "\\"))) return (false, "a running program is using it - close it and try again");
        int code = await RecycleAsync(path);
        if (Directory.Exists(path))
            return (false, code == LeftoverRecycle.Aborted ? "cancelled" :
                           "Windows could not move it to the Recycle Bin (code " + code + ") - a file may be in use");
        Change.Note("leftovers.files", "Moved leftover folder to the Recycle Bin: " + path, path);
        return (true, "moved to the Recycle Bin");
    }

    static string BackupDir()
    {
        foreach (var baseDir in new[] { Paths.AppDir, Paths.DefaultSaveDir() })
        {
            try
            {
                string d = Path.Combine(baseDir, "Winnow-removed-registry");
                Directory.CreateDirectory(d);
                string probe = Path.Combine(d, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return d;
            }
            catch { }
        }
        return null;
    }

    static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in s ?? "") sb.Append(bad.Contains(c) ? '_' : c);
        string r = sb.ToString().Trim();
        if (r.Length > 80) r = r.Substring(0, 80);
        return r.Length == 0 ? "key" : r;
    }

    static async Task<(bool ok, string message)> RemoveKeyAsync(LeftoverItem item)
    {
        if (IsNeverTouchKey(item)) return (false, "Winnow never removes this key");
        RegistryKey root = Change.Root(item.Hive);
        using (var k = root.OpenSubKey(item.KeyPath))
            if (k == null) return (true, "already gone");
        string dir = BackupDir();
        if (dir == null) return (false, "nowhere writable to save the backup .reg file - key left alone");
        string leaf = item.KeyPath.Substring(item.KeyPath.LastIndexOf('\\') + 1);
        string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + " " + SafeFileName(leaf) + ".reg");
        string regExe = Path.Combine(Paths.WindowsDir, "System32", "reg.exe");
        var r = await Shell.RunAsync(regExe, "export \"" + item.Hive + "\\" + item.KeyPath + "\" \"" + file + "\" /y", 60_000);
        if (!r.Ok || !File.Exists(file))
            return (false, "could not back up the key, so it was left alone: " + r.Output.Trim());
        try { root.DeleteSubKeyTree(item.KeyPath, false); }
        catch (Exception ex) { return (false, "backed up to " + file + " but could not delete: " + ex.Message); }
        Change.Note("leftovers.registry", "Removed registry key " + item.Location + " - double-click the backup to restore it", file);
        return (true, "removed (backup: " + Path.GetFileName(file) + ")");
    }

    /// <summary>Move a folder to the Recycle Bin with SHFileOperation on an STA thread. 0 = success.</summary>
    static Task<int> RecycleAsync(string path)
    {
        var tcs = new TaskCompletionSource<int>();
        var t = new Thread(() =>
        {
            try
            {
                var op = new LeftoverRecycle.SHFILEOPSTRUCT
                {
                    hwnd = IntPtr.Zero,
                    wFunc = LeftoverRecycle.FO_DELETE,
                    pFrom = path + "\0",            // the marshaller adds the second terminating null
                    pTo = null,
                    fFlags = (ushort)(LeftoverRecycle.FOF_ALLOWUNDO | LeftoverRecycle.FOF_NOCONFIRMATION |
                                      LeftoverRecycle.FOF_SILENT | LeftoverRecycle.FOF_WANTNUKEWARNING),
                };
                int r = LeftoverRecycle.SHFileOperation(ref op);
                tcs.TrySetResult(r == 0 && op.fAnyOperationsAborted ? LeftoverRecycle.Aborted : r);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }
}

/// <summary>Recycle Bin via the shell (recoverable delete). 64-bit layout.</summary>
internal static class LeftoverRecycle
{
    public const uint FO_DELETE = 3;
    public const int FOF_SILENT = 0x0004;
    public const int FOF_NOCONFIRMATION = 0x0010;
    public const int FOF_ALLOWUNDO = 0x0040;
    public const int FOF_WANTNUKEWARNING = 0x4000;
    public const int Aborted = -2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
