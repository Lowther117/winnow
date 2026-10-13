using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Winnow.Core;

/// <summary>One thing that starts with Windows: a Run/RunOnce value, a Startup-folder file or a scheduled task.</summary>
public sealed class StartupEntry
{
    public string Kind = "";          // Run | RunOnce | Folder | Task
    public string Name = "";
    public string Source = "";        // shown to the user, e.g. "Registry HKCU Run"
    public string Command = "";
    public string Location = "";
    public string Hive = "";          // HKCU | HKLM (registry and Startup-folder entries)
    public string KeyPath = "";       // registry key that holds the value (Run / RunOnce entries)
    public string ValueName = "";     // registry value name, or the file name for Startup-folder items
    public string ApprovedKey = "";   // Explorer\StartupApproved\... key under Hive, "" when the entry has no approved state
    public string FilePath = "";      // Startup-folder file
    public string TaskPath = "";      // \Folder\TaskName
    public string Trigger = "";       // scheduled tasks: "At log on" / "At startup"
    public string Target = "";        // the program it runs, "" if it could not be worked out
    public bool TargetMissing;        // Target was worked out and is not on disk
    public string Publisher = "";     // CompanyName of Target, "" if unknown
    public bool Enabled = true;
    public bool CanToggle;
    public bool CanDelete;
}

/// <summary>
/// Reads everything that starts with Windows, and turns entries on and off the
/// way Task Manager does: a binary value under Explorer\StartupApproved
/// (Run, Run32, StartupFolder). The first byte is even (0x02 / 0x06) when the
/// entry is enabled and odd (0x03 / 0x07) when disabled; bytes 4..11 hold the
/// FILETIME it was disabled. An absent value means enabled. RunOnce has no
/// approved state.
/// </summary>
public static class StartupScan
{
    public const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    static readonly Dictionary<string, string> _company = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();

    // ---------------------------------------------------------------- scan

    /// <summary>Run / RunOnce keys and both Startup folders. Call off the UI thread.</summary>
    public static List<StartupEntry> ScanRegistryAndFolders()
    {
        var list = new List<StartupEntry>();
        AddRun(list, "HKCU", RunKey, "Run", "Run", "Registry HKCU Run");
        AddRun(list, "HKLM", RunKey, "Run", "Run", "Registry HKLM Run");
        AddRun(list, "HKLM", Run32Key, "Run", "Run32", "Registry HKLM Run (32-bit)");
        AddRun(list, "HKCU", RunOnceKey, "RunOnce", null, "Registry HKCU RunOnce");
        AddRun(list, "HKLM", RunOnceKey, "RunOnce", null, "Registry HKLM RunOnce");
        AddFolder(list, SafeFolder(Environment.SpecialFolder.Startup), "HKCU", "Startup folder (you)");
        AddFolder(list, SafeFolder(Environment.SpecialFolder.CommonStartup), "HKLM", "Startup folder (all users)");
        return list;
    }

    static string SafeFolder(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f); } catch { return ""; }
    }

    static void AddRun(List<StartupEntry> list, string hive, string key, string kind, string approvedSub, string source)
    {
        try
        {
            using var k = Change.Root(hive).OpenSubKey(key);
            if (k == null) return;
            foreach (var name in k.GetValueNames())
            {
                string cmd;
                try { cmd = Convert.ToString(k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? ""; }
                catch { cmd = ""; }
                if (name.Length == 0 && cmd.Length == 0) continue;   // an empty (Default) value is not an entry
                var e = new StartupEntry
                {
                    Kind = kind,
                    Name = name.Length == 0 ? "(Default)" : name,
                    Source = source,
                    Command = cmd,
                    Location = hive + "\\" + key,
                    Hive = hive,
                    KeyPath = key,
                    ValueName = name,
                    ApprovedKey = approvedSub == null || name.Length == 0 ? "" : ApprovedBase + approvedSub,
                    CanDelete = true,
                };
                e.CanToggle = e.ApprovedKey.Length > 0;
                if (e.CanToggle) e.Enabled = IsApproved(hive, e.ApprovedKey, name);
                string exe = ResolveExe(cmd, out bool found);
                e.Target = exe;
                e.TargetMissing = exe.Length > 0 && !found;
                if (found) e.Publisher = CompanyOf(exe);
                list.Add(e);
            }
        }
        catch (Exception ex) { Log.Write("startup scan " + hive + "\\" + key + ": " + ex.Message); }
    }

    static void AddFolder(List<StartupEntry> list, string folder, string hive, string source)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                string file = Path.GetFileName(f);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var e = new StartupEntry
                {
                    Kind = "Folder",
                    Name = Path.GetFileNameWithoutExtension(f),
                    Source = source,
                    Location = folder,
                    Hive = hive,
                    ValueName = file,
                    ApprovedKey = ApprovedBase + "StartupFolder",
                    FilePath = f,
                    CanToggle = true,
                    CanDelete = true,
                    Command = f,
                    Target = f,
                };
                e.Enabled = IsApproved(hive, e.ApprovedKey, file);
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var (target, args) = StartupLnk.Read(f);
                    if (target.Length > 0)
                    {
                        e.Command = Shell.Quote(target) + (args.Length > 0 ? " " + args : "");
                        e.Target = target;
                        e.TargetMissing = !File.Exists(target) && !Directory.Exists(target);
                    }
                }
                if (File.Exists(e.Target)) e.Publisher = CompanyOf(e.Target);
                list.Add(e);
            }
        }
        catch (Exception ex) { Log.Write("startup scan folder " + folder + ": " + ex.Message); }
    }

    const string TaskScript =
@"$out = @()
foreach ($t in @(Get-ScheduledTask -EA SilentlyContinue)) {
  if ($t.TaskPath -like '\Microsoft\*') { continue }
  $kinds = @($t.Triggers | Where-Object { $_ } | ForEach-Object { $_.CimClass.CimClassName })
  $logon = $kinds -contains 'MSFT_TaskLogonTrigger'
  $boot = $kinds -contains 'MSFT_TaskBootTrigger'
  if (-not ($logon -or $boot)) { continue }
  $exe = ''
  $arg = ''
  $acts = @($t.Actions | Where-Object { $_ -and $_.CimClass.CimClassName -eq 'MSFT_TaskExecAction' })
  if ($acts.Count -gt 0) { $exe = [string]$acts[0].Execute; $arg = [string]$acts[0].Arguments }
  $trig = 'At startup'
  if ($logon -and $boot) { $trig = 'At log on and at startup' } elseif ($logon) { $trig = 'At log on' }
  $out += [pscustomobject]@{ Path = [string]($t.TaskPath + $t.TaskName); Name = [string]$t.TaskName; State = [string]$t.State; Author = [string]$t.Author; Execute = $exe; Arguments = $arg; Trigger = $trig }
}
'JSON:' + (ConvertTo-Json -InputObject $out -Depth 3 -Compress)";

    /// <summary>Scheduled tasks outside \Microsoft\ with a log-on or boot trigger. Error is "" on success.</summary>
    public static async Task<(List<StartupEntry> list, string error)> ScanTasksAsync()
    {
        var list = new List<StartupEntry>();
        var r = await Shell.PowerShellAsync(TaskScript, 120_000);
        string line = null;
        foreach (var l in r.Output.Split('\n'))
        {
            string t = l.Replace("﻿", "").Trim();   // a UTF-8 BOM can lead the first line of redirected output
            if (t.StartsWith("JSON:", StringComparison.Ordinal)) { line = t; break; }
        }
        if (line == null)
        {
            string first = r.Output.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "no output";
            return (list, r.TimedOut ? "timed out" : first);
        }
        string json = line.Substring(5).Trim();
        if (json.Length == 0) return (list, "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = new List<JsonElement>();
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) { foreach (var x in root.EnumerateArray()) items.Add(x); }
            else if (root.ValueKind == JsonValueKind.Object) items.Add(root);
            foreach (var it in items)
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                string path = Str(it, "Path");
                string exeRaw = Str(it, "Execute");
                string args = Str(it, "Arguments");
                string state = Str(it, "State");
                var e = new StartupEntry
                {
                    Kind = "Task",
                    Name = Str(it, "Name"),
                    Source = "Scheduled task",
                    TaskPath = path,
                    Location = path,
                    Trigger = Str(it, "Trigger"),
                    Enabled = !state.Equals("Disabled", StringComparison.OrdinalIgnoreCase),
                    CanToggle = path.Length > 0,
                    CanDelete = false,
                };
                if (exeRaw.Length > 0)
                {
                    string q = exeRaw.Trim();
                    if (!q.StartsWith("\"") && q.IndexOf(' ') >= 0) q = "\"" + q + "\"";
                    e.Command = q + (args.Length > 0 ? " " + args : "");
                    string exe = ResolveExe(q, out bool found);
                    e.Target = exe;
                    e.TargetMissing = exe.Length > 0 && !found;
                    if (found) e.Publisher = CompanyOf(exe);
                }
                else e.Command = "(no program action)";
                list.Add(e);
            }
        }
        catch (Exception ex) { return (list, "could not read the task list: " + ex.Message); }
        return (list, "");
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ----------------------------------------------------- approved state

    /// <summary>True unless the StartupApproved value marks the entry disabled (odd first byte).</summary>
    public static bool IsApproved(string hive, string approvedKey, string valueName)
    {
        if (string.IsNullOrEmpty(approvedKey)) return true;
        var v = Change.GetRegistry(hive, approvedKey, valueName) as byte[];
        if (v == null || v.Length == 0) return true;
        return (v[0] & 0x01) == 0;
    }

    /// <summary>
    /// The StartupApproved bytes for the new state: the current value with the
    /// enabled bit flipped (0x02 to 0x03, 0x06 to 0x07 and back) and bytes 4..11
    /// set to now (disabling) or zero (enabling). A missing or odd-sized value
    /// becomes the standard 12 bytes.
    /// </summary>
    public static byte[] ApprovedBytes(byte[] current, bool enable)
    {
        var b = new byte[Math.Max(12, current?.Length ?? 0)];
        if (current != null && current.Length >= 12) System.Array.Copy(current, b, current.Length);
        if (b[0] != 0x02 && b[0] != 0x03 && b[0] != 0x06 && b[0] != 0x07) b[0] = 0x02;
        b[0] = enable ? (byte)(b[0] & 0xFE) : (byte)(b[0] | 0x01);
        byte[] ft = enable ? new byte[8] : BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
        System.Array.Copy(ft, 0, b, 4, 8);
        return b;
    }

    /// <summary>
    /// Turn a Run or Startup-folder entry on or off through the change journal.
    /// Call on the UI thread (the journal raises its Changed event on the caller's thread).
    /// </summary>
    public static (bool ok, string message) SetApproved(StartupEntry e, bool enable)
    {
        if (!e.CanToggle || e.ApprovedKey.Length == 0) return (false, "this entry has no on/off switch");
        var cur = Change.GetRegistry(e.Hive, e.ApprovedKey, e.ValueName) as byte[];
        if (enable && (cur == null || cur.Length == 0 || (cur[0] & 0x01) == 0)) return (true, "already enabled");
        var bytes = ApprovedBytes(cur, enable);
        return Change.SetRegistry(e.Hive, e.ApprovedKey, e.ValueName, bytes, RegistryValueKind.Binary,
            "startup." + e.Name, (enable ? "Enable startup: " : "Disable startup: ") + e.Name);
    }

    // ----------------------------------------------------- command → exe

    /// <summary>
    /// The program a command line runs: the quoted path, or the text up to the
    /// first ".exe" that ends a token, or the first word. Environment variables
    /// are expanded and bare names are looked up in System32, Windows and PATH.
    /// <paramref name="found"/> says whether the file exists.
    /// </summary>
    public static string ResolveExe(string command, out bool found)
    {
        found = false;
        if (string.IsNullOrWhiteSpace(command)) return "";
        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith(@"\??\")) c = c.Substring(4);
        string path;
        if (c.StartsWith("\""))
        {
            int q = c.IndexOf('"', 1);
            path = q > 1 ? c.Substring(1, q - 1) : c.Trim('"');
        }
        else
        {
            int i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            while (i >= 0 && i + 4 < c.Length && !char.IsWhiteSpace(c[i + 4]) && c[i + 4] != '"' && c[i + 4] != ',')
                i = c.IndexOf(".exe", i + 4, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) path = c.Substring(0, i + 4);
            else
            {
                int sp = c.IndexOf(' ');
                path = sp > 0 ? c.Substring(0, sp) : c;
            }
        }
        path = path.Trim().Trim('"');
        if (path.Length == 0) return "";
        try
        {
            if (Path.IsPathRooted(path))
            {
                found = File.Exists(path);
                return path;
            }
            string hit = FindOnSearchPath(path);
            if (hit != null) { found = true; return hit; }
        }
        catch { }
        return path;
    }

    static string FindOnSearchPath(string name)
    {
        var names = new List<string> { name };
        if (Path.GetExtension(name).Length == 0) names.Insert(0, name + ".exe");
        var dirs = new List<string> { Path.Combine(Paths.WindowsDir, "System32"), Paths.WindowsDir };
        string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in envPath.Split(';'))
        {
            string t = d.Trim().Trim('"');
            if (t.Length > 0) dirs.Add(Environment.ExpandEnvironmentVariables(t));
        }
        foreach (var d in dirs)
        {
            foreach (var n in names)
            {
                try
                {
                    string p = Path.Combine(d, n);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>FileVersionInfo.CompanyName, cached. "" when unreadable.</summary>
    public static string CompanyOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        lock (_lock) if (_company.TryGetValue(path, out var c)) return c;
        string company = "";
        try
        {
            if (File.Exists(path)) company = (FileVersionInfo.GetVersionInfo(path).CompanyName ?? "").Trim();
        }
        catch { }
        lock (_lock)
        {
            if (_company.Count > 4000) _company.Clear();
            _company[path] = company;
        }
        return company;
    }
}

/// <summary>
/// Minimal reader for Windows shortcut (.lnk) files, per the published
/// MS-SHLLINK format: the local target path from LinkInfo (or the relative
/// path) and the command-line arguments from StringData. Best effort - an
/// unreadable shortcut gives ("", "").
/// </summary>
public static class StartupLnk
{
    const uint HasLinkTargetIDList = 0x01, HasLinkInfo = 0x02, HasName = 0x04, HasRelativePath = 0x08,
               HasWorkingDir = 0x10, HasArguments = 0x20, IsUnicode = 0x80;

    public static (string target, string args) Read(string lnkPath)
    {
        try
        {
            var fi = new FileInfo(lnkPath);
            if (!fi.Exists || fi.Length < 76 || fi.Length > 4_000_000) return ("", "");
            byte[] b = File.ReadAllBytes(lnkPath);
            if (BitConverter.ToUInt32(b, 0) != 0x4C) return ("", "");
            uint flags = BitConverter.ToUInt32(b, 20);
            int pos = 76;
            if ((flags & HasLinkTargetIDList) != 0) pos += 2 + BitConverter.ToUInt16(b, pos);

            string target = "";
            if ((flags & HasLinkInfo) != 0)
            {
                int li = pos;
                int liSize = BitConverter.ToInt32(b, li);
                int liHeader = BitConverter.ToInt32(b, li + 4);
                uint liFlags = BitConverter.ToUInt32(b, li + 8);
                if ((liFlags & 0x01) != 0)   // VolumeIDAndLocalBasePath
                {
                    string basePath = "", suffix = "";
                    if (liHeader >= 0x24)
                    {
                        int uo = BitConverter.ToInt32(b, li + 28);
                        int us = BitConverter.ToInt32(b, li + 32);
                        if (uo > 0) basePath = ReadZ(b, li + uo, true);
                        if (us > 0) suffix = ReadZ(b, li + us, true);
                    }
                    if (basePath.Length == 0)
                    {
                        int o = BitConverter.ToInt32(b, li + 16);
                        int s = BitConverter.ToInt32(b, li + 24);
                        if (o > 0) basePath = ReadZ(b, li + o, false);
                        if (s > 0) suffix = ReadZ(b, li + s, false);
                    }
                    if (basePath.Length > 0) target = basePath + suffix;
                }
                if (liSize <= 0) return (target, "");
                pos += liSize;
            }

            bool uni = (flags & IsUnicode) != 0;
            string relative = "", args = "";
            foreach (uint mask in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments })
            {
                if ((flags & mask) == 0) continue;
                int count = BitConverter.ToUInt16(b, pos);
                pos += 2;
                int bytes = uni ? count * 2 : count;
                if (pos + bytes > b.Length) break;
                string s = uni ? Encoding.Unicode.GetString(b, pos, bytes) : Encoding.ASCII.GetString(b, pos, bytes);
                pos += bytes;
                if (mask == HasRelativePath) relative = s;
                else if (mask == HasArguments) args = s;
            }

            if (target.Length == 0 && relative.Length > 0)
            {
                try { target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(lnkPath) ?? "", relative)); } catch { }
            }
            if (target.Length > 0) target = Environment.ExpandEnvironmentVariables(target);
            if (target.Length > 0 && !Path.IsPathRooted(target)) target = "";
            return (target, args.Trim());
        }
        catch { return ("", ""); }
    }

    /// <summary>A NUL-terminated string. ANSI strings with non-ASCII bytes are skipped (their code page is unknown).</summary>
    static string ReadZ(byte[] b, int at, bool unicode)
    {
        if (at < 0 || at >= b.Length) return "";
        int end = at;
        if (unicode)
        {
            while (end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0)) end += 2;
            return Encoding.Unicode.GetString(b, at, end - at);
        }
        while (end < b.Length && b[end] != 0)
        {
            if (b[end] >= 0x80) return "";
            end++;
        }
        return Encoding.ASCII.GetString(b, at, end - at);
    }
}
