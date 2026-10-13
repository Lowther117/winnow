using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Winnow.Core;

// The Disk Space page's clean-up list: what each item is, how it is measured
// before anything is touched, and how it is cleaned. Files are deleted for
// good (not to the Recycle Bin), one at a time, so a file in use is counted
// and skipped rather than stopping the run. Reparse points (junctions,
// symbolic links) are never followed, so a link inside a temp folder can
// never lead a clean-up somewhere else.

public enum CleanRisk { Safe, Moderate, Advanced }

/// <summary>A folder (or single file) whose matching files an item deletes.</summary>
public sealed class CleanTarget
{
    public string Folder = "";
    public string SingleFile = "";      // set instead of Folder for one file (e.g. MEMORY.DMP)
    public string Pattern = "*";        // file name wildcard
    public bool Recurse = true;
    public double MinAgeHours;          // files changed more recently than this are left alone
    public bool PruneDirs;              // remove subfolders left empty (never the folder itself)
    public string[] SkipDirs = System.Array.Empty<string>();   // folder names never entered (e.g. .NET's single-file extraction folder)
}

/// <summary>What an item would free, measured before cleaning. Null = could not be measured.</summary>
public sealed class CleanMeasure
{
    public long? Bytes;
    public long? Files;
    public bool Available = true;       // false = nothing to do on this PC
    public bool Estimate;               // Bytes is an estimate (labelled "est.")
    public string Note = "";
    public string Raw = "";             // raw tool output, shown when it could not be parsed
}

public sealed class CleanTally
{
    public long Freed, Deleted, Skipped, SkippedBytes;
    public long? FreedEstimate;         // from the change in free space (component store)
}

public sealed class CleanContext
{
    public CleanTally Tally = new();
    public Action<string> Say = _ => { };        // a log line (called from worker threads)
    public Action<string> Progress = _ => { };   // a status-line update (called from worker threads)
    public CancellationToken Token;
    public bool ResetBase;
    public CleanMeasure Measured;                // the measurement the user saw
    public List<string> Restart = new();         // services to start again afterwards
}

public sealed class CleanItem
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string Where = "";                    // tooltip: the paths involved
    public string ConfirmNote = "";              // extra line for the confirmation
    public CleanRisk Risk;
    public bool NeedsAdmin;
    public bool DefaultOn;
    public bool MeasureOnDemand;                 // measured only when asked (slow)
    public bool CannotCancel;                    // runs an external tool that must finish
    public string[] Processes = System.Array.Empty<string>();   // image names that must not be running
    public string AppName = "";
    public Func<List<CleanTarget>> Targets;
    public Func<CancellationToken, Task<CleanMeasure>> CustomMeasure;
    public Func<CleanContext, Task> CustomClean;
    public Func<CleanContext, Task> Before;
    public Func<CleanContext, Task> After;
}

public static class CleanupCatalog
{
    static string Win => Paths.WindowsDir;
    static string Local => Paths.LocalAppData;
    static string LocalLow => Path.Combine(Path.GetDirectoryName(Paths.LocalAppData.TrimEnd('\\')) ?? Paths.LocalAppData, "LocalLow");
    static string SystemRoot => Path.GetPathRoot(Paths.WindowsDir) ?? @"C:\";
    static string WindowsOld => Path.Combine(SystemRoot, "Windows.old");
    static string DoCacheDir => Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
    static string DismExe => Path.Combine(Win, @"System32\Dism.exe");
    static string CleanMgrExe => Path.Combine(Win, @"System32\cleanmgr.exe");

    const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    const string PreviousInstallations = VolumeCaches + @"\Previous Installations";

    static CleanTarget T(string folder, string pattern = "*", bool recurse = true, double minAgeHours = 0, bool prune = false) =>
        new CleanTarget { Folder = folder ?? "", Pattern = pattern, Recurse = recurse, MinAgeHours = minAgeHours, PruneDirs = prune };

    static CleanTarget WithSkip(CleanTarget t, params string[] names) { t.SkipDirs = names; return t; }

    static CleanTarget One(string file) => new CleanTarget { SingleFile = file ?? "", Recurse = false };

    // ================================================================ catalogue

    public static List<CleanItem> Build()
    {
        var items = new List<CleanItem>
        {
            new CleanItem
            {
                Id = "usertemp", Name = "Your temporary files", Risk = CleanRisk.Safe, DefaultOn = true,
                Description = "Files apps left in your temp folder. Anything changed in the last 24 hours, or still open, is left alone.",
                Where = Path.GetTempPath(),
                // %TEMP%\.net holds the native files single-file .NET apps (Winnow included) unpack and reuse - never touched.
                Targets = () => new List<CleanTarget> { WithSkip(T(Path.GetTempPath(), minAgeHours: 24, prune: true), ".net") },
            },
            new CleanItem
            {
                Id = "wintemp", Name = "Windows temporary files", Risk = CleanRisk.Safe, DefaultOn = true, NeedsAdmin = true,
                Description = "Temporary files written by Windows and installers. Anything changed in the last 24 hours, or still open, is left alone.",
                Where = Path.Combine(Win, "Temp"),
                Targets = () => new List<CleanTarget> { T(Path.Combine(Win, "Temp"), minAgeHours: 24, prune: true) },
            },
            new CleanItem
            {
                Id = "wucache", Name = "Windows Update download cache", Risk = CleanRisk.Moderate, NeedsAdmin = true,
                Description = "Update packages Windows has already downloaded. Winnow stops the Windows Update and BITS services while it cleans, " +
                              "then starts them again. An update waiting to install may have to download again.",
                Where = Path.Combine(Win, @"SoftwareDistribution\Download"),
                Targets = () => new List<CleanTarget> { T(Path.Combine(Win, @"SoftwareDistribution\Download"), prune: true) },
                Before = StopUpdateServicesAsync,
                After = StartServicesAsync,
            },
            new CleanItem
            {
                Id = "docache", Name = "Delivery Optimisation cache", Risk = CleanRisk.Safe, DefaultOn = true, NeedsAdmin = true,
                Description = "Update and Store app files kept to share with other PCs. Removed with Windows' own Delete-DeliveryOptimizationCache command.",
                Where = DoCacheDir,
                CustomMeasure = MeasureDeliveryOptimisationAsync,
                CustomClean = CleanDeliveryOptimisationAsync,
            },
            new CleanItem
            {
                Id = "thumbs", Name = "Thumbnail cache", Risk = CleanRisk.Safe, DefaultOn = true,
                Description = "Picture and video previews Explorer has saved. Explorer rebuilds them as you browse. " +
                              "Explorer keeps some of these files open, so those are skipped.",
                Where = Path.Combine(Local, @"Microsoft\Windows\Explorer") + @"\thumbcache_*.db",
                Targets = () => new List<CleanTarget> { T(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", recurse: false) },
            },
            new CleanItem
            {
                Id = "wer", Name = "Windows error reports", Risk = CleanRisk.Safe, DefaultOn = true,
                Description = "Problem reports already sent to Microsoft or waiting in the queue. Only useful when troubleshooting.",
                Where = Path.Combine(Paths.ProgramData, @"Microsoft\Windows\WER") + "  ·  " + Path.Combine(Local, @"Microsoft\Windows\WER"),
                Targets = () => new List<CleanTarget>
                {
                    T(Path.Combine(Paths.ProgramData, @"Microsoft\Windows\WER\ReportArchive"), prune: true),
                    T(Path.Combine(Paths.ProgramData, @"Microsoft\Windows\WER\ReportQueue"), prune: true),
                    T(Path.Combine(Paths.ProgramData, @"Microsoft\Windows\WER\Temp"), prune: true),
                    T(Path.Combine(Local, @"Microsoft\Windows\WER"), prune: true),
                },
            },
            new CleanItem
            {
                Id = "dumps", Name = "Crash dumps", Risk = CleanRisk.Safe, DefaultOn = true, NeedsAdmin = true,
                Description = "Memory dumps written when Windows or an app crashed, including live kernel reports. Only needed to diagnose those crashes.",
                Where = string.Join("  ·  ", Path.Combine(Win, "Minidump"), Path.Combine(Win, "MEMORY.DMP"),
                                    Path.Combine(Win, "LiveKernelReports"), Path.Combine(Local, "CrashDumps")),
                Targets = () => new List<CleanTarget>
                {
                    T(Path.Combine(Win, "Minidump"), "*.dmp"),
                    One(Path.Combine(Win, "MEMORY.DMP")),
                    T(Path.Combine(Win, "LiveKernelReports"), "*.dmp"),
                    T(Path.Combine(Local, "CrashDumps"), "*.dmp"),
                },
            },
            new CleanItem
            {
                Id = "cbslogs", Name = "Old Windows servicing logs", Risk = CleanRisk.Safe, DefaultOn = true, NeedsAdmin = true,
                Description = "Archived update logs (CbsPersist) older than 7 days. Only used to diagnose update problems. The current CBS.log is never touched.",
                Where = Path.Combine(Win, @"Logs\CBS") + @"\CbsPersist_*",
                Targets = () => new List<CleanTarget> { T(Path.Combine(Win, @"Logs\CBS"), "CbsPersist_*", recurse: false, minAgeHours: 7 * 24) },
            },
            new CleanItem
            {
                Id = "recycle", Name = "Recycle Bin", Risk = CleanRisk.Moderate,
                Description = "Empties the Recycle Bin on every drive. Nothing in it can be restored afterwards.",
                Where = "Recycle Bin (all drives)",
                ConfirmNote = "Recycle Bin: everything in it is gone for good.",
                CustomMeasure = MeasureRecycleBinAsync,
                CustomClean = CleanRecycleBinAsync,
            },
            Browser("edge", "Microsoft Edge", "msedge.exe", Path.Combine(Local, @"Microsoft\Edge\User Data"),
                    " Edge can keep running in the background (Startup boost); turn that off in Edge's settings if it is always skipped."),
            Browser("chrome", "Google Chrome", "chrome.exe", Path.Combine(Local, @"Google\Chrome\User Data"), ""),
            Browser("brave", "Brave", "brave.exe", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), ""),
            new CleanItem
            {
                Id = "firefox", Name = "Firefox cache", Risk = CleanRisk.Safe, DefaultOn = true,
                Processes = new[] { "firefox.exe" }, AppName = "Firefox",
                Description = "Cached web pages and images for every Firefox profile. Sign-ins, cookies, history, passwords and settings are not touched. " +
                              "Skipped while Firefox is running, so close it first.",
                Where = Path.Combine(Local, @"Mozilla\Firefox\Profiles") + @"\*\cache2",
                Targets = FirefoxTargets,
            },
            new CleanItem
            {
                Id = "d3dshaders", Name = "DirectX shader cache", Risk = CleanRisk.Moderate,
                Description = "Compiled graphics shaders for games and apps. They are rebuilt automatically, so the next launch of each game " +
                              "may stutter or load slowly while that happens.",
                Where = Path.Combine(Local, "D3DSCache"),
                Targets = () => new List<CleanTarget> { T(Path.Combine(Local, "D3DSCache")) },
            },
            new CleanItem
            {
                Id = "gpushaders", Name = "Graphics driver shader caches", Risk = CleanRisk.Moderate,
                Description = "NVIDIA, AMD and Intel driver shader caches. Rebuilt automatically; games may stutter on their next launch while that happens.",
                Where = "NVIDIA DXCache/GLCache, AMD DxCache/DxcCache/VkCache/GLCache, Intel ShaderCache (AppData\\Local and AppData\\LocalLow)",
                Targets = GpuShaderTargets,
            },
            new CleanItem
            {
                Id = "windowsold", Name = "Previous Windows installation (Windows.old)", Risk = CleanRisk.Advanced, NeedsAdmin = true,
                CannotCancel = true,
                Description = "The copy of your old Windows kept after an upgrade. Removing it means you can no longer go back to the previous version. " +
                              "Removed with Windows' own Disk Cleanup handler, which can take several minutes.",
                Where = WindowsOld,
                ConfirmNote = "Windows.old: you will no longer be able to go back to the previous version of Windows.",
                CustomMeasure = MeasureWindowsOldAsync,
                CustomClean = CleanWindowsOldAsync,
            },
            new CleanItem
            {
                Id = "winsxs", Name = "Component store clean-up (WinSxS)", Risk = CleanRisk.Advanced, NeedsAdmin = true,
                MeasureOnDemand = true, CannotCancel = true,
                Description = "Removes superseded versions of Windows components with DISM /StartComponentCleanup. Slow (often 10 to 30 minutes), " +
                              "cannot be stopped part-way and cannot be undone.",
                Where = Path.Combine(Win, "WinSxS"),
                ConfirmNote = "Component store: DISM cannot be stopped once it starts and the clean-up cannot be undone.",
                CustomMeasure = AnalyseComponentStoreAsync,
                CustomClean = CleanComponentStoreAsync,
            },
        };
        return items;
    }

    static CleanItem Browser(string id, string name, string exe, string userData, string extra) => new CleanItem
    {
        Id = id, Name = name + " cache", Risk = CleanRisk.Safe, DefaultOn = true,
        Processes = new[] { exe }, AppName = name,
        Description = $"Cached web pages, scripts and graphics shaders for every {name} profile. Sign-ins, cookies, history, passwords and " +
                      $"settings are not touched. Skipped while {name} is running, so close it first." + extra,
        Where = userData + @"\<profile>\Cache\Cache_Data, Code Cache, GPUCache",
        Targets = () => ChromiumTargets(userData),
    };

    /// <summary>Only the cache folders of each Chromium profile - never the profile itself.</summary>
    static List<CleanTarget> ChromiumTargets(string userData)
    {
        var list = new List<CleanTarget>();
        if (!Directory.Exists(userData)) return list;
        list.Add(T(Path.Combine(userData, "ShaderCache")));
        list.Add(T(Path.Combine(userData, "GrShaderCache")));
        string[] dirs;
        try { dirs = Directory.GetDirectories(userData); }
        catch { dirs = System.Array.Empty<string>(); }
        foreach (var d in dirs)
        {
            if (!File.Exists(Path.Combine(d, "Preferences"))) continue;   // a real profile folder (Default, Profile 1, ...)
            list.Add(T(Path.Combine(d, @"Cache\Cache_Data")));
            list.Add(T(Path.Combine(d, "Code Cache")));
            list.Add(T(Path.Combine(d, "GPUCache")));
        }
        return list;
    }

    static List<CleanTarget> FirefoxTargets()
    {
        var list = new List<CleanTarget>();
        string profiles = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) return list;
        string[] dirs;
        try { dirs = Directory.GetDirectories(profiles); }
        catch { dirs = System.Array.Empty<string>(); }
        foreach (var d in dirs) list.Add(T(Path.Combine(d, "cache2")));
        return list;
    }

    static List<CleanTarget> GpuShaderTargets() => new List<CleanTarget>
    {
        T(Path.Combine(Local, @"NVIDIA\DXCache")),
        T(Path.Combine(Local, @"NVIDIA\GLCache")),
        T(Path.Combine(LocalLow, @"NVIDIA\PerDriverVersion\DXCache")),
        T(Path.Combine(LocalLow, @"NVIDIA\PerDriverVersion\GLCache")),
        T(Path.Combine(Local, @"AMD\DxCache")),
        T(Path.Combine(Local, @"AMD\DxcCache")),
        T(Path.Combine(Local, @"AMD\VkCache")),
        T(Path.Combine(Local, @"AMD\GLCache")),
        T(Path.Combine(LocalLow, @"Intel\ShaderCache")),
    };

    // ================================================================ engine

    /// <summary>Which of the item's programs is running, or null. Uses the sampler's process list.</summary>
    public static string RunningApp(CleanItem item)
    {
        if (item.Processes.Length == 0) return null;
        var snap = Sampler.Latest;
        if (snap != null)
        {
            foreach (var p in snap.Processes)
                foreach (var exe in item.Processes)
                    if (string.Equals(p.Name, exe, StringComparison.OrdinalIgnoreCase)) return exe;
            return null;
        }
        foreach (var exe in item.Processes)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)); }
            catch { continue; }
            bool any = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            if (any) return exe;
        }
        return null;
    }

    public static async Task<CleanMeasure> MeasureAsync(CleanItem item, CancellationToken ct)
    {
        try
        {
            if (item.CustomMeasure != null) return await item.CustomMeasure(ct);
            return await Task.Run(() =>
            {
                var targets = item.Targets != null ? item.Targets() : new List<CleanTarget>();
                return MeasureTargets(targets, ct);
            }, ct);
        }
        catch (OperationCanceledException) { return new CleanMeasure { Note = "Measuring was cancelled." }; }
        catch (Exception ex)
        {
            Log.Write("disk measure " + item.Id + ": " + ex);
            return new CleanMeasure { Note = "Could not measure: " + ex.Message };
        }
    }

    public static async Task CleanAsync(CleanItem item, CleanContext c)
    {
        try
        {
            if (item.Before != null) await item.Before(c);
            if (item.CustomClean != null) await item.CustomClean(c);
            else
            {
                await Task.Run(() =>
                {
                    var targets = item.Targets != null ? item.Targets() : new List<CleanTarget>();
                    CleanTargets(targets, c);
                });
            }
        }
        finally
        {
            if (item.After != null)
            {
                try { await item.After(c); }
                catch (Exception ex) { c.Say("  could not finish: " + ex.Message); }
            }
        }
    }

    static bool Exists(CleanTarget t) =>
        !string.IsNullOrEmpty(t.SingleFile)
            ? Path.IsPathFullyQualified(t.SingleFile) && File.Exists(t.SingleFile)
            : SafeFolder(t.Folder) && Directory.Exists(t.Folder);

    /// <summary>
    /// Last line of defence: a target folder must be an absolute path and never a drive root or a
    /// top-level system folder (e.g. TEMP pointed at C:\ or C:\Windows, or a known folder that came back empty).
    /// </summary>
    static bool SafeFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder)) return false;
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\'); }
        catch { return false; }
        string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
        if (full.Length == 0 || full.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
        var blocked = new[]
        {
            Win, Path.Combine(Win, "System32"), Paths.ProgramFiles, Paths.ProgramFilesX86, Paths.ProgramData,
            Local, Paths.RoamingAppData, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root + "\\Users",
        };
        foreach (var b in blocked)
            if (!string.IsNullOrEmpty(b) && full.Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static long Len(FileInfo f)
    {
        try { return f.Length; }
        catch { return 0; }
    }

    /// <summary>Newer of creation and last-write time: an installer can unpack an old file into temp a minute ago.</summary>
    static DateTime NewestTime(FileInfo f)
    {
        try
        {
            var w = f.LastWriteTimeUtc;
            var c = f.CreationTimeUtc;
            return w > c ? w : c;
        }
        catch { return DateTime.MaxValue; }   // unknown age: treat as new, leave it
    }

    static IEnumerable<FileInfo> OneFile(string path)
    {
        FileInfo f = null;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) == 0) f = fi;
        }
        catch { }
        if (f != null) yield return f;
    }

    /// <summary>The files a target would delete: matching name, old enough.</summary>
    static IEnumerable<FileInfo> Matching(CleanTarget t, CancellationToken ct)
    {
        DateTime cutoff = t.MinAgeHours > 0 ? DateTime.UtcNow.AddHours(-t.MinAgeHours) : DateTime.MaxValue;
        IEnumerable<FileInfo> source = !string.IsNullOrEmpty(t.SingleFile) ? OneFile(t.SingleFile) : CleanWalk.Files(t.Folder, t.Recurse, t.SkipDirs, ct);
        bool anyName = t.Pattern == "*";
        foreach (var f in source)
        {
            if (!anyName && !FileSystemName.MatchesSimpleExpression(t.Pattern, f.Name, true)) continue;
            if (t.MinAgeHours > 0 && NewestTime(f) > cutoff) continue;
            yield return f;
        }
    }

    static CleanMeasure MeasureTargets(List<CleanTarget> targets, CancellationToken ct)
    {
        long bytes = 0, files = 0;
        bool exists = false;
        foreach (var t in targets)
        {
            if (!Exists(t)) continue;
            exists = true;
            foreach (var f in Matching(t, ct))
            {
                bytes += Len(f);
                files++;
            }
        }
        ct.ThrowIfCancellationRequested();
        if (!exists) return new CleanMeasure { Bytes = 0, Files = 0, Available = false, Note = "Nothing to clean here on this PC." };
        return new CleanMeasure { Bytes = bytes, Files = files, Available = files > 0, Note = files == 0 ? "Already clean." : "" };
    }

    static void DeleteOne(FileInfo f, CleanTally t)
    {
        long len = Len(f);
        try
        {
            if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
            f.Delete();
            t.Freed += len;
            t.Deleted++;
        }
        catch
        {
            t.Skipped++;              // in use, or protected
            t.SkippedBytes += len;
        }
    }

    static void CleanTargets(List<CleanTarget> targets, CleanContext c)
    {
        var tally = c.Tally;
        long lastReport = 0;
        foreach (var t in targets)
        {
            if (c.Token.IsCancellationRequested) return;
            if (!Exists(t)) continue;
            var files = Matching(t, c.Token).ToList();
            long freedBefore = tally.Freed, deletedBefore = tally.Deleted, skippedBefore = tally.Skipped;
            foreach (var f in files)
            {
                if (c.Token.IsCancellationRequested) break;
                DeleteOne(f, tally);
                long done = tally.Deleted + tally.Skipped;
                if (done - lastReport >= 250)
                {
                    lastReport = done;
                    c.Progress($"{tally.Deleted:N0} files deleted, {Fmt.Bytes(tally.Freed)} freed…");
                }
            }
            if (t.PruneDirs && string.IsNullOrEmpty(t.SingleFile) && !c.Token.IsCancellationRequested)
                CleanWalk.PruneEmpty(t.Folder, t.MinAgeHours, t.SkipDirs, c.Token);
            if (files.Count > 0)
            {
                string where = string.IsNullOrEmpty(t.SingleFile) ? t.Folder : t.SingleFile;
                long skippedHere = tally.Skipped - skippedBefore;
                c.Say($"  {where}: {tally.Deleted - deletedBefore:N0} deleted ({Fmt.Bytes(tally.Freed - freedBefore)})" +
                      (skippedHere > 0 ? $", {skippedHere:N0} in use or protected" : ""));
            }
        }
    }

    static long FreeSpace(string root)
    {
        try { return new DriveInfo(root).TotalFreeSpace; }
        catch { return -1; }
    }

    // ================================================================ Recycle Bin

    static async Task<CleanMeasure> MeasureRecycleBinAsync(CancellationToken ct)
    {
        var (bytes, items, hr) = await DiskNative.RunSta(DiskNative.QueryRecycleBin);
        if (hr != 0) return new CleanMeasure { Note = "Could not read the Recycle Bin (error 0x" + hr.ToString("X8") + ")." };
        return new CleanMeasure
        {
            Bytes = bytes, Files = items, Available = items > 0,
            Note = items == 0 ? "The Recycle Bin is empty." : "",
        };
    }

    static async Task CleanRecycleBinAsync(CleanContext c)
    {
        long beforeBytes = c.Measured?.Bytes ?? 0, beforeItems = c.Measured?.Files ?? 0;
        int hr = await DiskNative.RunSta(DiskNative.EmptyRecycleBin);
        var after = await MeasureRecycleBinAsync(CancellationToken.None);
        long leftBytes = after.Bytes ?? 0, leftItems = after.Files ?? 0;
        c.Tally.Freed += Math.Max(0, beforeBytes - leftBytes);
        c.Tally.Deleted += Math.Max(0, beforeItems - leftItems);
        if (leftItems > 0)
        {
            c.Tally.Skipped += leftItems;
            c.Tally.SkippedBytes += leftBytes;
            c.Say($"  {leftItems:N0} items are still in the Recycle Bin (error 0x{hr:X8}).");
        }
        else c.Say("  Recycle Bin emptied.");
    }

    // ================================================================ Delivery Optimisation

    static async Task<CleanMeasure> MeasureDeliveryOptimisationAsync(CancellationToken ct)
    {
        var r = await Shell.PowerShellAsync(
            "try{$s=Get-DeliveryOptimizationPerfSnap -ErrorAction Stop;$n=if($null -ne $s.FileCount){$s.FileCount}else{$s.Files};" +
            "'DOSIZE|'+[string]$s.CacheSizeBytes+'|'+[string]$n}" +
            "catch{'DOERR|'+$_.Exception.Message}", 90_000, null, ct);
        foreach (var raw in r.Output.Split('\n'))
        {
            var p = raw.Trim().Split('|');
            if (p.Length == 3 && p[0] == "DOSIZE" && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long b))
            {
                long f = long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ff) ? ff : 0;
                return new CleanMeasure { Bytes = b, Files = f, Available = b > 0 || f > 0, Note = b == 0 && f == 0 ? "The cache is empty." : "" };
            }
        }
        ct.ThrowIfCancellationRequested();
        // The cmdlet did not answer: measure the default cache folder instead.
        var m = await Task.Run(() => MeasureTargets(new List<CleanTarget> { T(DoCacheDir) }, ct), ct);
        if (m.Available) m.Note = "Measured from the cache folder (Get-DeliveryOptimizationPerfSnap did not answer).";
        return m;
    }

    static async Task CleanDeliveryOptimisationAsync(CleanContext c)
    {
        var r = await Shell.PowerShellAsync(
            "try{Delete-DeliveryOptimizationCache -Force -ErrorAction Stop;'DODONE'}catch{'DOERR|'+$_.Exception.Message}", 600_000);
        if (!r.Output.Contains("DODONE")) c.Say("  Delete-DeliveryOptimizationCache: " + r.Output.Trim());
        var after = await MeasureDeliveryOptimisationAsync(CancellationToken.None);
        long before = c.Measured?.Bytes ?? 0, beforeFiles = c.Measured?.Files ?? 0;
        long left = after.Bytes ?? 0, leftFiles = after.Files ?? 0;
        c.Tally.Freed += Math.Max(0, before - left);
        c.Tally.Deleted += Math.Max(0, beforeFiles - leftFiles);
        if (left > 0)
        {
            c.Tally.Skipped += leftFiles;
            c.Tally.SkippedBytes += left;
            c.Say($"  {Fmt.Bytes(left)} is still in the cache (files in use or pinned by your organisation).");
        }
    }

    // ================================================================ Windows Update services

    static bool IsStopped(string scOutput) => Regex.IsMatch(scOutput ?? "", @"\b1\s+STOPPED\b");

    static async Task StopUpdateServicesAsync(CleanContext c)
    {
        foreach (var svc in new[] { "wuauserv", "bits" })
        {
            var q = await Shell.RunAsync("sc.exe", "query " + svc, 15_000);
            if (!q.Ok || IsStopped(q.Output)) continue;   // not installed, or already stopped
            c.Say($"  stopping {svc}…");
            c.Restart.Add(svc);
            await Shell.RunAsync("sc.exe", "stop " + svc, 30_000);
            for (int i = 0; i < 60; i++)
            {
                var s = await Shell.RunAsync("sc.exe", "query " + svc, 15_000);
                if (IsStopped(s.Output)) break;
                await Task.Delay(500);
            }
        }
    }

    static async Task StartServicesAsync(CleanContext c)
    {
        foreach (var svc in c.Restart)
        {
            var r = await Shell.RunAsync("sc.exe", "start " + svc, 30_000);
            c.Say(r.Ok ? $"  started {svc} again" : $"  could not start {svc} again: {r.Output.Trim()}");
        }
    }

    // ================================================================ Windows.old

    static Task<CleanMeasure> MeasureWindowsOldAsync(CancellationToken ct) => Task.Run(() =>
    {
        if (!Directory.Exists(WindowsOld))
            return new CleanMeasure { Bytes = 0, Files = 0, Available = false, Note = "There is no previous Windows installation on this PC." };
        var m = MeasureTargets(new List<CleanTarget> { T(WindowsOld) }, ct);
        m.Available = true;
        m.Note = "Some folders inside may be locked, so the real size can be larger.";
        return m;
    }, ct);

    /// <summary>A free /sageset number: no VolumeCaches handler already has StateFlagsNNNN.</summary>
    static int FreeSageSlot()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(VolumeCaches);
            string[] names = root != null ? root.GetSubKeyNames() : System.Array.Empty<string>();
            for (int n = 117; n < 9999; n++)
            {
                string v = "StateFlags" + n.ToString("0000", CultureInfo.InvariantCulture);
                bool used = false;
                foreach (var s in names)
                {
                    using var k = root.OpenSubKey(s);
                    if (k != null && k.GetValue(v) != null) { used = true; break; }
                }
                if (!used) return n;
            }
        }
        catch { }
        return 117;
    }

    static async Task CleanWindowsOldAsync(CleanContext c)
    {
        const string settingsHint = " Use Settings › System › Storage › Temporary files › Previous Windows installation(s) instead.";
        if (!File.Exists(CleanMgrExe))
        {
            c.Say("  Disk Cleanup (cleanmgr.exe) is not on this PC." + settingsHint);
            return;
        }
        bool hasHandler = false;
        try { using (var k = Registry.LocalMachine.OpenSubKey(PreviousInstallations)) hasHandler = k != null; }
        catch { }
        if (!hasHandler)
        {
            c.Say("  Disk Cleanup's 'Previous Installations' handler is missing." + settingsHint);
            return;
        }

        // Tick only "Previous Windows installation(s)" for one Disk Cleanup run, then untick it.
        int slot = FreeSageSlot();
        string valueName = "StateFlags" + slot.ToString("0000", CultureInfo.InvariantCulture);
        var set = Change.SetRegistry("HKLM", PreviousInstallations, valueName, 2, RegistryValueKind.DWord,
                                     "disk.windowsold", "Select 'Previous Windows installation(s)' for one Disk Cleanup run");
        if (!set.ok)
        {
            c.Say("  could not select the Disk Cleanup handler: " + set.message);
            return;
        }
        c.Say($"  running cleanmgr /sagerun:{slot} - this can take several minutes…");
        c.Progress("Removing Windows.old with Disk Cleanup…");
        var r = await Shell.RunAsync(CleanMgrExe, "/sagerun:" + slot.ToString(CultureInfo.InvariantCulture), 3_600_000);
        Change.DeleteRegistryValue("HKLM", PreviousInstallations, valueName, "disk.windowsold", "Remove the one-off Disk Cleanup selection");
        if (r.TimedOut) c.Say("  Disk Cleanup did not finish within an hour.");

        long before = c.Measured?.Bytes ?? 0, beforeFiles = c.Measured?.Files ?? 0;
        if (!Directory.Exists(WindowsOld))
        {
            c.Tally.Freed += before;
            c.Tally.Deleted += beforeFiles;
            c.Say("  Windows.old removed.");
            return;
        }
        var after = await Task.Run(() => MeasureTargets(new List<CleanTarget> { T(WindowsOld) }, CancellationToken.None));
        long left = after.Bytes ?? 0;
        c.Tally.Freed += Math.Max(0, before - left);
        c.Tally.Deleted += Math.Max(0, beforeFiles - (after.Files ?? 0));
        c.Say($"  Windows.old is still there ({Fmt.Bytes(left)})." + settingsHint);
    }

    // ================================================================ component store (DISM)

    static string DismField(string output, string label)
    {
        var m = Regex.Match(output ?? "", @"^\s*" + Regex.Escape(label) + @"\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    static long? ParseDismSize(string s)
    {
        if (s == null) return null;
        var m = Regex.Match(s, @"([\d.,]+)\s*(bytes|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string num = m.Groups[1].Value;
        num = num.Contains('.') ? num.Replace(",", "") : num.Replace(',', '.');
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        double mult = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)(v * mult);
    }

    static async Task<CleanMeasure> AnalyseComponentStoreAsync(CancellationToken ct)
    {
        // /English keeps the labels parseable on any display language.
        var r = await Shell.RunAsync(DismExe, "/Online /Cleanup-Image /AnalyzeComponentStore /English", 1_800_000, null, ct);
        string o = r.Output ?? "";
        string actual = DismField(o, "Actual Size of Component Store");
        string backups = DismField(o, "Backups and Disabled Features");
        string cache = DismField(o, "Cache and Temporary Data");
        string packages = DismField(o, "Number of Reclaimable Packages");
        string recommended = DismField(o, "Component Store Cleanup Recommended");
        string last = DismField(o, "Date of Last Cleanup");
        if (actual == null && packages == null && recommended == null)
        {
            return new CleanMeasure
            {
                Note = r.TimedOut ? "DISM did not finish in time." : "Could not read DISM's answer - its full output is in the log below.",
                Raw = o.Trim(),
            };
        }
        long? b = ParseDismSize(backups), ca = ParseDismSize(cache);
        var m = new CleanMeasure { Estimate = true, Raw = o.Trim() };
        if (b.HasValue || ca.HasValue) m.Bytes = (b ?? 0) + (ca ?? 0);
        m.Note = $"Store {actual ?? Fmt.Na} · backups and disabled features {backups ?? Fmt.Na} · cache {cache ?? Fmt.Na} · " +
                 $"reclaimable packages {packages ?? Fmt.Na} · clean-up recommended: {recommended ?? Fmt.Na}" +
                 (last != null ? " · last clean-up " + last : "");
        return m;
    }

    static async Task CleanComponentStoreAsync(CleanContext c)
    {
        long freeBefore = FreeSpace(SystemRoot);
        string args = "/Online /Cleanup-Image /StartComponentCleanup" + (c.ResetBase ? " /ResetBase" : "") + " /English";
        c.Say("  DISM " + args + " - slow, and cannot be stopped part-way…");
        var r = await Shell.RunAsync(DismExe, args, 4 * 3_600_000, line =>
        {
            string t = (line ?? "").Trim();
            if (t.Length == 0) return;
            if (t.StartsWith("[") && t.Contains('%')) c.Progress("Component store clean-up: " + t.Trim('[', ']', '=', ' '));
            else c.Say("  " + t);
        });
        long freeAfter = FreeSpace(SystemRoot);
        if (freeBefore >= 0 && freeAfter >= 0) c.Tally.FreedEstimate = (c.Tally.FreedEstimate ?? 0) + Math.Max(0, freeAfter - freeBefore);
        bool restart = !r.TimedOut && r.ExitCode == 3010;   // ERROR_SUCCESS_REBOOT_REQUIRED
        c.Say(r.Ok ? "  DISM finished." : restart ? "  DISM finished - restart Windows to complete the clean-up."
              : $"  DISM ended with exit code {r.ExitCode}" + (r.TimedOut ? " (timed out)." : "."));
    }
}

/// <summary>File walking for the clean-up: one folder level at a time, never through a reparse point.</summary>
internal static class CleanWalk
{
    static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    static FileSystemInfo[] ListDir(DirectoryInfo d)
    {
        try { return d.EnumerateFileSystemInfos("*", Flat).ToArray(); }
        catch { return System.Array.Empty<FileSystemInfo>(); }
    }

    static DirectoryInfo Root(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            var d = new DirectoryInfo(path);
            if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return d;
        }
        catch { return null; }
    }

    static bool Skipped(DirectoryInfo d, string[] skip)
    {
        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        if (skip == null) return false;
        foreach (var n in skip)
            if (string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static IEnumerable<FileInfo> Files(string path, bool recurse, string[] skip, CancellationToken ct)
    {
        var root = Root(path);
        if (root == null) yield break;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var d = stack.Pop();
            foreach (var e in ListDir(d))
            {
                if (e is FileInfo f) yield return f;
                else if (recurse && e is DirectoryInfo sub && !Skipped(sub, skip)) stack.Push(sub);
            }
        }
    }

    /// <summary>Remove empty subfolders (deepest first), never the folder itself. Folders newer than minAgeHours stay.</summary>
    public static void PruneEmpty(string path, double minAgeHours, string[] skip, CancellationToken ct)
    {
        var root = Root(path);
        if (root == null) return;
        DateTime cutoff = minAgeHours > 0 ? DateTime.UtcNow.AddHours(-minAgeHours) : DateTime.MaxValue;
        var all = new List<DirectoryInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var e in ListDir(d))
            {
                if (e is DirectoryInfo sub && !Skipped(sub, skip))
                {
                    all.Add(sub);
                    stack.Push(sub);
                }
            }
        }
        // every folder was added after its parent, so walking backwards empties children first
        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                if (all[i].CreationTimeUtc <= cutoff) all[i].Delete(false);   // throws (and is skipped) unless empty
            }
            catch { }
        }
    }
}
