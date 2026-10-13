using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Winnow.Core;

/// <summary>
/// One recorded change. Everything Winnow changes on the system - registry
/// values, service start types, scheduled tasks, firewall rules, moved files -
/// goes through <see cref="Change"/>, which writes one of these first with the
/// value as it was, so any single change can be put back on its own.
/// </summary>
public sealed class JournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Group { get; set; } = "";        // the tweak / action that made it, e.g. "privacy.telemetry"
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "";         // Registry | Service | Task | Firewall | File | Note
    public string Hive { get; set; } = "";         // HKLM | HKCU (Registry)
    public string Path { get; set; } = "";         // key path / service name / task path / rule name / file path
    public string Name { get; set; } = "";         // value name (Registry) / destination (File)
    public bool BeforeExists { get; set; }
    public string BeforeKind { get; set; } = "";   // RegistryValueKind name
    public string BeforeData { get; set; } = "";   // serialised value
    public string AfterData { get; set; } = "";
    public bool Revertible { get; set; } = true;
    public bool Reverted { get; set; }
}

public static class Journal
{
    static List<JournalEntry> _entries = new();
    static readonly object _lock = new();
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static event Action Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(Paths.JournalFile))
                _entries = JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(Paths.JournalFile)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("journal load: " + ex.Message);
            // keep the unreadable file rather than overwrite someone's undo history
            try { File.Copy(Paths.JournalFile, Paths.JournalFile + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            _entries = new();
        }
    }

    public static List<JournalEntry> Entries { get { lock (_lock) return _entries.ToList(); } }

    public static IEnumerable<JournalEntry> ForGroup(string group) =>
        Entries.Where(e => e.Group == group && !e.Reverted);

    public static void Add(JournalEntry e)
    {
        lock (_lock) { _entries.Add(e); Save(); }
        Fire();
    }

    static void Save()
    {
        try { File.WriteAllText(Paths.JournalFile, JsonSerializer.Serialize(_entries, Opts)); }
        catch (Exception ex) { Log.Write("journal save: " + ex.Message); }
    }

    static void Fire() { try { Changed?.Invoke(); } catch { } }

    /// <summary>Put one change back the way it was.</summary>
    public static async Task<(bool ok, string message)> RevertAsync(JournalEntry e)
    {
        if (e.Reverted) return (true, "already reverted");
        if (!e.Revertible) return (false, "this change cannot be undone automatically");
        (bool ok, string msg) r;
        try
        {
            r = e.Kind switch
            {
                "Registry" => Change.RestoreRegistry(e),
                "Service" => await Change.RestoreServiceAsync(e),
                "Task" => await Change.RestoreTaskAsync(e),
                "Firewall" => await Change.RemoveFirewallRuleAsync(e.Path),
                "File" => Change.RestoreFile(e),
                _ => (false, "unknown change type")
            };
        }
        catch (Exception ex) { r = (false, ex.Message); }
        if (r.ok)
        {
            lock (_lock) { e.Reverted = true; var m = _entries.FirstOrDefault(x => x.Id == e.Id); if (m != null) m.Reverted = true; Save(); }
            Fire();
        }
        return r;
    }

    /// <summary>Revert every live change in a group, newest first.</summary>
    public static async Task<(int ok, int failed, string detail)> RevertGroupAsync(string group)
    {
        int ok = 0, failed = 0;
        var sb = new StringBuilder();
        foreach (var e in ForGroup(group).OrderByDescending(x => x.Time).ToList())
        {
            var r = await RevertAsync(e);
            if (r.ok) ok++;
            else { failed++; sb.AppendLine(e.Description + ": " + r.message); }
        }
        return (ok, failed, sb.ToString());
    }
}

/// <summary>
/// The only way Winnow changes the system. Each method journals the current
/// state, then makes the change. Methods never throw; they report.
/// </summary>
public static class Change
{
    // ------------------------------------------------------------ registry

    public static RegistryKey Root(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Registry.Users,
        _ => throw new ArgumentException("unknown hive " + hive)
    };

    /// <summary>Current value, or null if the key or value is absent.</summary>
    public static object GetRegistry(string hive, string path, string name)
    {
        try
        {
            using var k = Root(hive).OpenSubKey(path);
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch { return null; }
    }

    public static bool RegistryEquals(string hive, string path, string name, object want)
    {
        var cur = GetRegistry(hive, path, name);
        if (cur == null || want == null) return cur == null && want == null;
        return Serialise(cur) == Serialise(want);
    }

    public static (bool ok, string message) SetRegistry(string hive, string path, string name, object value,
        RegistryValueKind kind, string group, string description)
    {
        try
        {
            var root = Root(hive);
            object before = null;
            RegistryValueKind beforeKind = RegistryValueKind.Unknown;
            using (var k = root.OpenSubKey(path))
            {
                if (k != null && k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    before = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    beforeKind = k.GetValueKind(name);
                }
            }
            if (before != null && beforeKind == kind && Serialise(before) == Serialise(value))
                return (true, "already set");

            // Write first, journal second: a write that fails must not leave an "undo" for a change that never happened.
            using (var k = root.CreateSubKey(path, true))
                k.SetValue(name, value, kind);
            Journal.Add(new JournalEntry
            {
                Group = group, Description = description, Kind = "Registry", Hive = hive, Path = path, Name = name,
                BeforeExists = before != null, BeforeKind = before != null ? beforeKind.ToString() : "",
                BeforeData = before != null ? Serialise(before) : "", AfterData = Serialise(value),
            });
            return (true, "set");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool ok, string message) DeleteRegistryValue(string hive, string path, string name, string group, string description)
    {
        try
        {
            var root = Root(hive);
            using var k = root.OpenSubKey(path, true);
            if (k == null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return (true, "not present");
            var before = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = k.GetValueKind(name);
            k.DeleteValue(name, false);
            Journal.Add(new JournalEntry
            {
                Group = group, Description = description, Kind = "Registry", Hive = hive, Path = path, Name = name,
                BeforeExists = true, BeforeKind = kind.ToString(), BeforeData = Serialise(before), AfterData = "(deleted)",
            });
            return (true, "deleted");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    internal static (bool ok, string message) RestoreRegistry(JournalEntry e)
    {
        var root = Root(e.Hive);
        if (!e.BeforeExists)
        {
            using var k = root.OpenSubKey(e.Path, true);
            k?.DeleteValue(e.Name, false);
            return (true, "removed");
        }
        var kind = Enum.Parse<RegistryValueKind>(e.BeforeKind);
        using (var k = root.CreateSubKey(e.Path, true))
            k.SetValue(e.Name, Deserialise(e.BeforeData, kind), kind);
        return (true, "restored");
    }

    static string Serialise(object v) => v switch
    {
        null => "",
        string[] a => string.Join("\u0001", a),
        byte[] b => Convert.ToBase64String(b),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)
    };

    static object Deserialise(string s, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)long.Parse(s, System.Globalization.CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => s.Length == 0 ? System.Array.Empty<string>() : s.Split('\u0001'),
        RegistryValueKind.Binary => Convert.FromBase64String(s),
        _ => s
    };

    // ------------------------------------------------------------ services

    /// <summary>Boot | System | Automatic | AutomaticDelayed | Manual | Disabled, or null if no such service.</summary>
    public static string GetServiceStart(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (k == null || k.GetValue("Start") is not int start) return null;
            bool delayed = k.GetValue("DelayedAutostart") is int d && d == 1;
            return start switch
            {
                0 => "Boot", 1 => "System", 2 => delayed ? "AutomaticDelayed" : "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown"
            };
        }
        catch { return null; }
    }

    static string ScStart(string mode) => mode switch
    {
        "Automatic" => "auto", "AutomaticDelayed" => "delayed-auto", "Manual" => "demand", "Disabled" => "disabled",
        "Boot" => "boot", "System" => "system", _ => "demand"
    };

    static int RegStart(string mode) => mode switch
    {
        "Boot" => 0, "System" => 1, "Automatic" or "AutomaticDelayed" => 2, "Manual" => 3, "Disabled" => 4, _ => 3
    };

    /// <summary>Set a service's start type (and optionally stop it now). Falls back to the registry for protected services.</summary>
    public static async Task<(bool ok, string message)> SetServiceStartAsync(string name, string mode, string group,
        string description, bool stopNow = false)
    {
        string before = GetServiceStart(name);
        if (before == null) return (true, "service not present on this PC");
        if (before == mode && !stopNow) return (true, "already " + mode);
        if (before != mode)
        {
            Journal.Add(new JournalEntry
            {
                Group = group, Description = description, Kind = "Service", Path = name,
                BeforeExists = true, BeforeData = before, AfterData = mode,
            });
            var r = await Shell.RunAsync("sc.exe", $"config \"{name}\" start= {ScStart(mode)}", 30_000);
            if (!r.Ok)
            {
                // Protected services refuse sc.exe; the registry value still takes effect at next boot.
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, true);
                    k?.SetValue("Start", RegStart(mode), RegistryValueKind.DWord);
                    if (mode == "AutomaticDelayed") k?.SetValue("DelayedAutostart", 1, RegistryValueKind.DWord);
                }
                catch (Exception ex) { return (false, "sc.exe: " + r.Output.Trim() + " / registry: " + ex.Message); }
            }
        }
        if (stopNow || mode == "Disabled")
            await Shell.RunAsync("sc.exe", $"stop \"{name}\"", 30_000);
        return (true, "set to " + mode);
    }

    internal static async Task<(bool ok, string message)> RestoreServiceAsync(JournalEntry e)
    {
        var r = await Shell.RunAsync("sc.exe", $"config \"{e.Path}\" start= {ScStart(e.BeforeData)}", 30_000);
        if (r.Ok) return (true, "restored to " + e.BeforeData);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + e.Path, true);
            if (k == null) return (false, "service no longer exists");
            k.SetValue("Start", RegStart(e.BeforeData), RegistryValueKind.DWord);
            k.SetValue("DelayedAutostart", e.BeforeData == "AutomaticDelayed" ? 1 : 0, RegistryValueKind.DWord);
            return (true, "restored in the registry (takes effect after restart)");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ----------------------------------------------------- scheduled tasks

    static string PsQ(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

    static (string folder, string name) SplitTask(string path)
    {
        path = path.Replace('/', '\\');
        if (!path.StartsWith("\\")) path = "\\" + path;
        int i = path.LastIndexOf('\\');
        return (path[..(i + 1)], path[(i + 1)..]);
    }

    /// <summary>Ready | Disabled | Running | Queued, or null if the task does not exist.</summary>
    public static async Task<Dictionary<string, string>> GetTaskStatesAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var sb = new StringBuilder();
        foreach (var p in list)
        {
            var (f, n) = SplitTask(p);
            sb.Append($"$t=Get-ScheduledTask -TaskPath {PsQ(f)} -TaskName {PsQ(n)} -EA SilentlyContinue;");
            sb.Append($"if($t){{'TASK|'+{PsQ(p)}+'|'+$t.State}}else{{'TASK|'+{PsQ(p)}+'|'}};");
        }
        var r = await Shell.PowerShellAsync(sb.ToString(), 120_000);
        var result = list.ToDictionary(p => p, p => (string)null, StringComparer.OrdinalIgnoreCase);
        foreach (var line in r.Output.Split('\n'))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length == 3 && parts[0] == "TASK")
                result[parts[1]] = parts[2].Length == 0 ? null : parts[2];
        }
        return result;
    }

    /// <summary>Enable or disable scheduled tasks in one PowerShell call. Missing tasks are skipped, not failures.</summary>
    public static async Task<(int changed, int missing, string detail)> SetTasksEnabledAsync(IEnumerable<string> paths, bool enabled,
        string group, string description)
    {
        var states = await GetTaskStatesAsync(paths);
        int missing = states.Count(kv => kv.Value == null);
        var todo = states.Where(kv => kv.Value != null && (kv.Value == "Disabled") == enabled).Select(kv => kv.Key).ToList();
        if (todo.Count == 0) return (0, missing, "");
        foreach (var p in todo)
            Journal.Add(new JournalEntry
            {
                Group = group, Description = description + " - " + p, Kind = "Task", Path = p,
                BeforeExists = true, BeforeData = states[p] == "Disabled" ? "Disabled" : "Enabled",
                AfterData = enabled ? "Enabled" : "Disabled",
            });
        var sb = new StringBuilder();
        foreach (var p in todo)
        {
            var (f, n) = SplitTask(p);
            sb.Append($"try{{{(enabled ? "Enable" : "Disable")}-ScheduledTask -TaskPath {PsQ(f)} -TaskName {PsQ(n)} -EA Stop|Out-Null;'OK|'+{PsQ(p)}}}catch{{'ERR|'+{PsQ(p)}+'|'+$_.Exception.Message}};");
        }
        var r = await Shell.PowerShellAsync(sb.ToString(), 120_000);
        var errors = r.Output.Split('\n').Where(l => l.StartsWith("ERR|")).Select(l => l.Trim()).ToList();
        return (todo.Count - errors.Count, missing, string.Join("\n", errors));
    }

    internal static async Task<(bool ok, string message)> RestoreTaskAsync(JournalEntry e)
    {
        var (f, n) = SplitTask(e.Path);
        string verb = e.BeforeData == "Disabled" ? "Disable" : "Enable";
        var r = await Shell.PowerShellAsync($"try{{{verb}-ScheduledTask -TaskPath {PsQ(f)} -TaskName {PsQ(n)} -EA Stop|Out-Null;'OK'}}catch{{'ERR '+$_.Exception.Message}}");
        return r.Output.Contains("OK") ? (true, "task " + verb.ToLowerInvariant() + "d") : (false, r.Output.Trim());
    }

    // ------------------------------------------------------------ firewall

    /// <summary>Add an outbound block rule. Either a program path or a list of remote addresses (or both).</summary>
    public static async Task<(bool ok, string message)> AddFirewallBlockAsync(string ruleName, string program,
        IEnumerable<string> remoteAddresses, string group, string description)
    {
        string addr = remoteAddresses != null && remoteAddresses.Any()
            ? " -RemoteAddress @(" + string.Join(",", remoteAddresses.Select(PsQ)) + ")" : "";
        string prog = string.IsNullOrEmpty(program) ? "" : " -Program " + PsQ(program);
        string script =
            $"Remove-NetFirewallRule -Name {PsQ(ruleName)} -EA SilentlyContinue;" +
            $"try{{New-NetFirewallRule -Name {PsQ(ruleName)} -DisplayName {PsQ(ruleName)} -Group 'Winnow' -Direction Outbound -Action Block{prog}{addr} -EA Stop|Out-Null;'OK'}}catch{{'ERR '+$_.Exception.Message}}";
        var r = await Shell.PowerShellAsync(script);
        if (!r.Output.Contains("OK")) return (false, r.Output.Trim());
        Journal.Add(new JournalEntry
        {
            Group = group, Description = description, Kind = "Firewall", Path = ruleName,
            BeforeExists = false, AfterData = "outbound block" + (prog.Length > 0 ? " " + program : "") ,
        });
        return (true, "rule added");
    }

    internal static async Task<(bool ok, string message)> RemoveFirewallRuleAsync(string ruleName)
    {
        var r = await Shell.PowerShellAsync($"Remove-NetFirewallRule -Name {PsQ(ruleName)} -EA SilentlyContinue;'OK'");
        return (r.Output.Contains("OK"), "rule removed");
    }

    // --------------------------------------------------------------- files

    /// <summary>Move a file (e.g. out of a Startup folder) so it can be moved back.</summary>
    public static (bool ok, string message) MoveFile(string from, string to, string group, string description)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
            File.Move(from, to, overwrite: true);
            Journal.Add(new JournalEntry
            {
                Group = group, Description = description, Kind = "File", Path = from, Name = to, BeforeExists = true,
            });
            return (true, "moved");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    internal static (bool ok, string message) RestoreFile(JournalEntry e)
    {
        if (!File.Exists(e.Name)) return (false, "the moved file is no longer at " + e.Name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(e.Path));
        File.Move(e.Name, e.Path, overwrite: true);
        return (true, "moved back");
    }

    /// <summary>Record something that cannot be undone automatically (an uninstall, a deletion).</summary>
    public static void Note(string group, string description, string detail = "")
    {
        Journal.Add(new JournalEntry { Group = group, Description = description, Kind = "Note", AfterData = detail, Revertible = false });
    }

    // ------------------------------------------------------ restore points

    /// <summary>Create a System Restore point. Turns System Restore on for the system drive if it is off.</summary>
    public static async Task<(bool ok, string message)> CreateRestorePointAsync(string description)
    {
        string script =
            "try { Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -EA Stop } catch {};" +
            "$k='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore';" +
            "$old=(Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -EA SilentlyContinue).SystemRestorePointCreationFrequency;" +
            "Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -EA SilentlyContinue;" +
            $"try {{ Checkpoint-Computer -Description {PsQ(description)} -RestorePointType MODIFY_SETTINGS -EA Stop; 'OK' }} catch {{ 'ERR ' + $_.Exception.Message }};" +
            "if ($null -eq $old) { Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -EA SilentlyContinue } " +
            "else { Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $old -Type DWord -EA SilentlyContinue }";
        var r = await Shell.PowerShellAsync(script, 300_000);
        string o = r.Output.Trim();
        return o.Contains("OK") && !o.Contains("ERR") ? (true, "restore point created") : (false, o.Length > 0 ? o : "no output");
    }
}
