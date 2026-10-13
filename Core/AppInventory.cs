using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Winnow.Core;

/// <summary>One classic (Win32) program from an Uninstall registry key.</summary>
public sealed class AppsProgram
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public long? SizeKb { get; set; }
    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string DisplayIcon { get; set; } = "";
    public bool IsMsi { get; set; }
    public bool Machine { get; set; }
    public bool Is32Bit { get; set; }
    public string Hive { get; set; } = "HKLM";       // HKLM | HKCU
    public string KeyPath { get; set; } = "";         // path under the hive, e.g. SOFTWARE\...\Uninstall\{GUID}
    public string KeyName { get; set; } = "";

    public string FullKey => (Hive == "HKCU" ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\") + KeyPath;
    public string Scope => Machine ? (Is32Bit ? "All users · 32-bit" : "All users · 64-bit") : "This user";
    public string InstallDateText => InstallDate.HasValue ? InstallDate.Value.ToString("yyyy-MM-dd") : Fmt.Na;
    public string InstallDateSort => InstallDate.HasValue ? InstallDate.Value.ToString("yyyyMMdd") : "";
    public string SizeText => SizeKb.HasValue ? Fmt.Bytes(SizeKb.Value * 1024.0) : Fmt.Na;
    public long SizeSort => SizeKb ?? -1;
    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString) || !string.IsNullOrWhiteSpace(QuietUninstallString);
}

/// <summary>One Store (Appx/MSIX) package installed for the current user.</summary>
public sealed class AppsStorePackage : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string FamilyName { get; set; } = "";
    public string PublisherDn { get; set; } = "";
    public string Version { get; set; } = "";
    public string SignatureKind { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public bool IsFramework { get; set; }
    public bool NonRemovable { get; set; }
    public string ProvisionedName { get; set; } = "";   // PackageName of the provisioned copy, if any

    public string Publisher
    {
        get
        {
            var m = Regex.Match(PublisherDn ?? "", @"CN=([^,]+)");
            return m.Success ? m.Groups[1].Value.Trim().Trim('"') : PublisherDn ?? "";
        }
    }
    public string ProvisionedText => ProvisionedName.Length > 0 ? "Yes" : "No";

    string _status = "";
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

/// <summary>A provisioned (staged for new users) package.</summary>
public sealed class AppsProvisioned
{
    public string DisplayName { get; set; } = "";
    public string PackageName { get; set; } = "";
}

/// <summary>Reads what is installed and runs uninstallers.</summary>
public static class AppInventory
{
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // ------------------------------------------------------------ Win32 programs

    /// <summary>Every visible program from HKLM (64 and 32-bit) and HKCU. Slow-ish: call off the UI thread.
    /// includeHidden also returns SystemComponent / ParentKeyName entries (updates, shared runtimes and
    /// components) - not for showing, but so "is anything else using this folder?" checks see them.</summary>
    public static List<AppsProgram> ReadPrograms(bool includeHidden = false)
    {
        var list = new List<AppsProgram>();
        ReadUninstallKey(Registry.LocalMachine, "HKLM", UninstallPath, true, false, list, includeHidden);
        ReadUninstallKey(Registry.LocalMachine, "HKLM", UninstallPath32, true, true, list, includeHidden);
        ReadUninstallKey(Registry.CurrentUser, "HKCU", UninstallPath, false, false, list, includeHidden);
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void ReadUninstallKey(RegistryKey root, string hive, string path, bool machine, bool is32, List<AppsProgram> into,
                                 bool includeHidden)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            if (k == null) return;
            foreach (var sub in k.GetSubKeyNames())
            {
                try
                {
                    using var s = k.OpenSubKey(sub);
                    if (s == null) continue;
                    string name = Str(s, "DisplayName").Trim();
                    if (name.Length == 0) continue;
                    if (!includeHidden && Int(s, "SystemComponent") == 1) continue;
                    if (!includeHidden && Str(s, "ParentKeyName").Length > 0) continue;
                    var p = new AppsProgram
                    {
                        Name = name,
                        Publisher = Str(s, "Publisher").Trim(),
                        Version = Str(s, "DisplayVersion").Trim(),
                        InstallLocation = Str(s, "InstallLocation").Trim().Trim('"'),
                        UninstallString = Str(s, "UninstallString").Trim(),
                        QuietUninstallString = Str(s, "QuietUninstallString").Trim(),
                        DisplayIcon = Str(s, "DisplayIcon").Trim(),
                        IsMsi = Int(s, "WindowsInstaller") == 1,
                        Machine = machine,
                        Is32Bit = is32,
                        Hive = hive,
                        KeyPath = path + "\\" + sub,
                        KeyName = sub,
                    };
                    long? size = Long(s, "EstimatedSize");
                    if (size.HasValue && size.Value > 0) p.SizeKb = size.Value;
                    string date = Str(s, "InstallDate").Trim();
                    if (date.Length == 8 && DateTime.TryParseExact(date, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                        p.InstallDate = dt;
                    into.Add(p);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Write("read uninstall " + hive + "\\" + path + ": " + ex.Message); }
    }

    static string Str(RegistryKey k, string name)
    {
        try { return Convert.ToString(k.GetValue(name), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
        catch { return ""; }
    }

    static int Int(RegistryKey k, string name)
    {
        try
        {
            object v = k.GetValue(name);
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is string s && int.TryParse(s, out int n)) return n;
        }
        catch { }
        return 0;
    }

    static long? Long(RegistryKey k, string name)
    {
        try
        {
            object v = k.GetValue(name);
            if (v is int i) return (uint)i;
            if (v is long l) return l;
            if (v is string s && long.TryParse(s, out long n)) return n;
        }
        catch { }
        return null;
    }

    /// <summary>True while the program's Uninstall key is still in the registry.</summary>
    public static bool StillListed(AppsProgram p)
    {
        try
        {
            using var k = Change.Root(p.Hive).OpenSubKey(p.KeyPath);
            return k != null;
        }
        catch { return false; }
    }

    static readonly Regex MsiGuid = new(@"msiexec(?:\.exe)?""?\s+.*?/([IX])\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase);

    /// <summary>
    /// The command Winnow runs to uninstall: QuietUninstallString if present, else UninstallString.
    /// "MsiExec.exe /I{GUID}" (which opens the repair/change dialog) becomes "msiexec.exe /x {GUID}".
    /// </summary>
    public static (string exe, string args) UninstallCommand(AppsProgram p)
    {
        string cmd = !string.IsNullOrWhiteSpace(p.QuietUninstallString) ? p.QuietUninstallString : p.UninstallString;
        return ParseCommand(cmd);
    }

    /// <summary>Split a command line into the program and its arguments. (null, null) if empty.</summary>
    public static (string exe, string args) ParseCommand(string cmd)
    {
        cmd = (cmd ?? "").Trim();
        if (cmd.Length == 0) return (null, null);
        var m = MsiGuid.Match(cmd);
        if (m.Success && m.Groups[1].Value.Equals("I", StringComparison.OrdinalIgnoreCase))
            return ("msiexec.exe", "/x " + m.Groups[2].Value);
        cmd = Environment.ExpandEnvironmentVariables(cmd);
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 1) return (cmd.Substring(1, end - 1), cmd.Substring(end + 1).Trim());
            return (cmd.Trim('"'), "");
        }
        // Unquoted path with spaces: take the first "...exe" prefix that exists on disk.
        int idx = 0;
        while ((idx = cmd.IndexOf(".exe", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            string cand = cmd.Substring(0, idx + 4);
            try { if (File.Exists(cand)) return (cand, cmd.Substring(idx + 4).Trim()); } catch { }
            idx += 4;
        }
        int first = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (first > 0) return (cmd.Substring(0, first + 4), cmd.Substring(first + 4).Trim());
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd.Substring(0, sp), cmd.Substring(sp + 1).Trim());
    }

    /// <summary>
    /// Pull a file path out of a command line or registry value (quoted or not, with
    /// arguments, environment variables or a \??\ prefix). Null when no rooted path is found.
    /// </summary>
    public static string ExtractPath(string cmd)
    {
        try
        {
            cmd = Environment.ExpandEnvironmentVariables((cmd ?? "").Trim());
            if (cmd.StartsWith(@"\??\")) cmd = cmd.Substring(4);
            if (cmd.Length == 0) return null;
            string path;
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                path = end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
            }
            else
            {
                int best = -1;
                foreach (var ext in new[] { ".exe", ".dll", ".sys", ".ico", ".bat", ".cmd" })
                {
                    int i = cmd.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                    if (i > 0 && (best < 0 || i + ext.Length < best)) best = i + ext.Length;
                }
                path = best > 0 ? cmd.Substring(0, best) : cmd;
                int comma = path.IndexOf(',');            // DisplayIcon "C:\x\app.exe,0"
                if (comma > 0) path = path.Substring(0, comma);
            }
            path = path.Trim();
            if (path.Length < 3 || !Path.IsPathRooted(path)) return null;
            return path;
        }
        catch { return null; }
    }

    /// <summary>
    /// Start the vendor's uninstaller with its own window (UseShellExecute) and wait for it to exit.
    /// Returns (started, exit code or null, error message).
    /// </summary>
    public static async Task<(bool started, int? exitCode, string error)> RunUninstallerAsync(AppsProgram p)
    {
        var (exe, args) = UninstallCommand(p);
        if (string.IsNullOrEmpty(exe)) return (false, null, "This program has no uninstall command in the registry.");
        // A bare name such as "msiexec.exe" must come from System32, not from Winnow's own folder.
        if (!Path.IsPathRooted(exe) && File.Exists(Paths.SystemExe(exe))) exe = Paths.SystemExe(exe);
        try
        {
            var psi = new ProcessStartInfo(exe, args ?? "") { UseShellExecute = true, WorkingDirectory = Paths.WindowsDir };
            using var proc = Process.Start(psi);
            if (proc == null) return (true, null, "");
            await Task.Run(() => proc.WaitForExit());
            int? code = null;
            try { code = proc.ExitCode; } catch { }
            return (true, code, "");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
        {
            return (false, null, "The uninstaller is missing: " + exe + ". The program's files are probably already gone - " +
                                 "the Leftovers tab can remove the stale entry.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, null, "Cancelled at the Windows security prompt.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    // ------------------------------------------------------------ Store packages

    /// <summary>
    /// One PowerShell call: the current user's Appx packages plus (when elevated) the provisioned list.
    /// Frameworks and non-removable packages are kept here; the caller filters.
    /// </summary>
    public static async Task<(List<AppsStorePackage> apps, List<AppsProvisioned> provisioned, string error)> ReadStoreAsync(CancellationToken ct = default)
    {
        var apps = new List<AppsStorePackage>();
        var prov = new List<AppsProvisioned>();
        string script =
            "$a=@(Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName,Publisher," +
            "@{n='Version';e={[string]$_.Version}},@{n='IsFramework';e={[bool]$_.IsFramework}}," +
            "@{n='NonRemovable';e={[bool]$_.NonRemovable}},@{n='SignatureKind';e={[string]$_.SignatureKind}}," +
            "@{n='InstallLocation';e={[string]$_.InstallLocation}});" +
            "$p=@();" +
            (Elevation.IsAdmin
                ? "try{$p=@(Get-AppxProvisionedPackage -Online -EA Stop | Select-Object @{n='DisplayName';e={[string]$_.DisplayName}},@{n='PackageName';e={[string]$_.PackageName}})}catch{};"
                : "") +
            "'WINNOW-JSON';" +
            "ConvertTo-Json -InputObject @{apps=$a;prov=$p} -Depth 4 -Compress";
        var r = await Shell.PowerShellAsync(script, 180_000, null, ct);
        string o = r.Output ?? "";
        int at = o.IndexOf("WINNOW-JSON", StringComparison.Ordinal);
        if (at < 0) return (apps, prov, FirstLine(o, "PowerShell returned nothing"));
        // stdout and stderr are read on separate threads, so a late stderr line can land after the JSON;
        // the JSON itself is one compressed line starting with '{'.
        string json = o.Substring(at + "WINNOW-JSON".Length);
        json = json.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("{", StringComparison.Ordinal)) ?? json.Trim();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("apps", out var a))
                foreach (var e in Items(a))
                    apps.Add(new AppsStorePackage
                    {
                        Name = JStr(e, "Name"),
                        FullName = JStr(e, "PackageFullName"),
                        FamilyName = JStr(e, "PackageFamilyName"),
                        PublisherDn = JStr(e, "Publisher"),
                        Version = JStr(e, "Version"),
                        SignatureKind = JStr(e, "SignatureKind"),
                        InstallLocation = JStr(e, "InstallLocation"),
                        IsFramework = JBool(e, "IsFramework"),
                        NonRemovable = JBool(e, "NonRemovable"),
                    });
            if (root.TryGetProperty("prov", out var pv))
                foreach (var e in Items(pv))
                    prov.Add(new AppsProvisioned { DisplayName = JStr(e, "DisplayName"), PackageName = JStr(e, "PackageName") });
        }
        catch (Exception ex) { return (apps, prov, "Could not read the package list: " + ex.Message); }

        foreach (var app in apps)
        {
            var hit = prov.FirstOrDefault(x => x.DisplayName.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) app.ProvisionedName = hit.PackageName;
        }
        return (apps, prov, "");
    }

    static IEnumerable<JsonElement> Items(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object) yield return x;
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            // Windows PowerShell 5.1 sometimes writes an array as {"value":[...],"Count":n}.
            if (e.TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.Array && e.TryGetProperty("Count", out _))
            {
                foreach (var x in inner.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.Object) yield return x;
            }
            else yield return e;
        }
    }

    static string JStr(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.ToString(),
        };
    }

    static bool JBool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.String) return string.Equals(v.GetString(), "True", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    static string FirstLine(string text, string fallback)
    {
        var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? fallback;
    }

    static string PsQ(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

    /// <summary>Remove one Store package (and optionally its provisioned copy). Each step is noted in the journal.</summary>
    public static async Task<(bool ok, string message)> RemoveStoreAsync(string name, string fullName, string provisionedName,
        bool allUsers, bool deprovision, Action<string> log, CancellationToken ct = default)
    {
        var msgs = new List<string>();
        bool ok = true;
        if (!string.IsNullOrEmpty(fullName))
        {
            string script = "try{Remove-AppxPackage -Package " + PsQ(fullName) + (allUsers ? " -AllUsers" : "") +
                            " -EA Stop;'WINNOW-OK'}catch{'WINNOW-ERR '+$_.Exception.Message}";
            var r = await Shell.PowerShellAsync(script, 300_000, null, ct);
            if (r.Output.Contains("WINNOW-OK"))
            {
                Change.Note("apps.store", "Removed Store app " + name + (allUsers ? " (all users)" : ""), fullName);
                log?.Invoke("  removed " + fullName + (allUsers ? " for all users" : ""));
            }
            else
            {
                ok = false;
                string err = ErrText(r.Output);
                msgs.Add(err);
                log?.Invoke("  could not remove: " + err);
            }
        }
        if (deprovision && !string.IsNullOrEmpty(provisionedName) && !ct.IsCancellationRequested)
        {
            string script = "try{Remove-AppxProvisionedPackage -Online -PackageName " + PsQ(provisionedName) +
                            " -EA Stop|Out-Null;'WINNOW-OK'}catch{'WINNOW-ERR '+$_.Exception.Message}";
            var r = await Shell.PowerShellAsync(script, 300_000, null, ct);
            if (r.Output.Contains("WINNOW-OK"))
            {
                Change.Note("apps.store", "Stopped " + name + " installing for new users (deprovisioned)", provisionedName);
                log?.Invoke("  deprovisioned " + provisionedName + " - new user accounts will not get it");
            }
            else
            {
                ok = false;
                string err = ErrText(r.Output);
                msgs.Add("deprovision: " + err);
                log?.Invoke("  could not deprovision: " + err);
            }
        }
        return (ok, ok ? "removed" : string.Join("; ", msgs));
    }

    static string ErrText(string output)
    {
        var line = (output ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("WINNOW-ERR"));
        if (line != null) return line.Substring("WINNOW-ERR".Length).Trim();
        return FirstLine(output, "no output from PowerShell");
    }

    // ------------------------------------------------------------ CSV

    public static string ToCsv(IEnumerable<AppsProgram> programs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Publisher,Version,Installed,Size KB,Scope,Install location,Uninstall command,Registry key");
        foreach (var p in programs)
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(p.Name), Csv(p.Publisher), Csv(p.Version), Csv(p.InstallDate.HasValue ? p.InstallDate.Value.ToString("yyyy-MM-dd") : ""),
                Csv(p.SizeKb.HasValue ? p.SizeKb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""),
                Csv(p.Scope), Csv(p.InstallLocation),
                Csv(!string.IsNullOrWhiteSpace(p.QuietUninstallString) ? p.QuietUninstallString : p.UninstallString),
                Csv(p.FullKey),
            }));
        return sb.ToString();
    }

    static string Csv(string s)
    {
        s ??= "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
