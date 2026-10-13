using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winnow.Core;

public sealed class CatalogueFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("categories")] public List<CatalogueCategory> Categories { get; set; } = new();
}

public sealed class CatalogueCategory
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("apps")] public List<CatalogueApp> Apps { get; set; } = new();
}

public sealed class CatalogueApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    /// <summary>winget source: "winget" (default) or "msstore".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "winget";

    public string SourceOrDefault => string.IsNullOrWhiteSpace(Source) ? "winget" : Source.Trim();
}

/// <summary>Outcome of one winget install / upgrade, from its exit code.</summary>
public sealed class CatalogueOutcome
{
    public bool Ok;
    public bool Cancelled;
    public string Text = "";
}

/// <summary>
/// The Install page's list of popular apps (Assets/catalogue.json, embedded),
/// or winnow-catalogue.json beside Winnow.exe when the owner has customised it.
/// </summary>
public static class Catalogue
{
    const string ResourceName = "Winnow.catalogue.json";

    static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string EmbeddedText()
    {
        using var s = typeof(Catalogue).Assembly.GetManifestResourceStream(ResourceName);
        if (s == null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static CatalogueFile Parse(string json)
    {
        var f = JsonSerializer.Deserialize<CatalogueFile>(json, ReadOpts) ?? new CatalogueFile();
        f.Categories ??= new List<CatalogueCategory>();
        foreach (var c in f.Categories.ToList())
        {
            if (c == null) { f.Categories.Remove(c); continue; }
            c.Name ??= "";
            c.Apps = (c.Apps ?? new List<CatalogueApp>()).Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id)).ToList();
            foreach (var a in c.Apps)
            {
                a.Id = a.Id.Trim();
                if (string.IsNullOrWhiteSpace(a.Name)) a.Name = a.Id;
                a.Description ??= "";
                a.Tags ??= new List<string>();
            }
        }
        return f;
    }

    /// <summary>
    /// Load the customised list if there is one, else the built-in list.
    /// origin says which; warning is non-empty if the custom file could not be read.
    /// </summary>
    public static (CatalogueFile file, string origin, string warning) Load()
    {
        string warning = "";
        try
        {
            if (File.Exists(Paths.CatalogueOverride))
            {
                var f = Parse(File.ReadAllText(Paths.CatalogueOverride));
                if (f.Categories.Count > 0) return (f, "Your list: " + Paths.CatalogueOverride, "");
                warning = "winnow-catalogue.json has no categories - using the built-in list.";
            }
        }
        catch (Exception ex)
        {
            warning = "Could not read winnow-catalogue.json (" + ex.Message + ") - using the built-in list.";
        }
        try
        {
            string text = EmbeddedText();
            if (text == null) return (new CatalogueFile(), "", "The built-in list is missing from Winnow.exe.");
            return (Parse(text), "Built-in list", warning);
        }
        catch (Exception ex)
        {
            return (new CatalogueFile(), "", "The built-in list could not be read: " + ex.Message);
        }
    }

    /// <summary>Write the built-in list beside Winnow.exe (if not already there) so it can be edited.</summary>
    public static (bool ok, string message) WriteOverride()
    {
        try
        {
            if (File.Exists(Paths.CatalogueOverride)) return (true, "exists");
            string text = EmbeddedText();
            if (text == null) return (false, "the built-in list is missing from Winnow.exe");
            File.WriteAllText(Paths.CatalogueOverride, text, new UTF8Encoding(false));
            return (true, "created");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ------------------------------------------------------------ winget list

    /// <summary>
    /// Which catalogue ids appear in `winget list` output. Reads the Id column by the
    /// header's position, also checks every word on the row (column widths can shift
    /// with wide characters), expands ids truncated with "…", and for rows winget could
    /// not match to a source (ARP\..., MSIX\...) or when the header could not be found,
    /// matches the row's name against the catalogue names.
    /// </summary>
    public static HashSet<string> InstalledFromWingetList(string output, IEnumerable<CatalogueApp> apps)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = apps.Where(a => a != null && a.Id.Length > 0).ToList();
        var ids = new HashSet<string>(list.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var lines = (output ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int sep = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length >= 10 && t.All(c => c == '-' || c == '─')) { sep = i; break; }
        }

        int idStart = -1, idEnd = -1;
        if (sep > 0)
        {
            string header = lines[sep - 1];
            var starts = new List<int>();
            for (int i = 0; i < header.Length; i++)
                if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
            if (starts.Count >= 2)
            {
                idStart = starts[1];
                idEnd = starts.Count >= 3 ? starts[2] : -1;
            }
        }

        void Check(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            token = token.Trim();
            if (ids.Contains(token)) { result.Add(token); return; }
            bool truncated = token.EndsWith("…") || token.EndsWith("...");
            if (!truncated) return;
            string prefix = token.TrimEnd('…', '.');
            if (prefix.Length < 6) return;
            foreach (var id in ids)
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(id);
        }

        for (int i = sep + 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Trim().Length == 0) continue;
            string cell = null;
            if (idStart >= 0 && line.Length > idStart)
            {
                cell = idEnd > idStart && line.Length > idEnd ? line.Substring(idStart, idEnd - idStart) : line.Substring(idStart);
                cell = cell.Trim();
                int sp = cell.IndexOf(' ');
                if (sp > 0) cell = cell.Substring(0, sp);
                Check(cell);
            }
            foreach (var tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Check(tok);

            bool unsourced = cell == null || cell.Length == 0 ||
                             cell.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) ||
                             cell.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase);
            if (!unsourced) continue;
            string trimmed = line.TrimStart();
            foreach (var a in list)
            {
                if (a.Name.Length < 4 || result.Contains(a.Id)) continue;
                if (trimmed.StartsWith(a.Name, StringComparison.OrdinalIgnoreCase) &&
                    (trimmed.Length == a.Name.Length || !char.IsLetterOrDigit(trimmed[a.Name.Length])))
                    result.Add(a.Id);
            }
        }
        return result;
    }

    // ------------------------------------------------------------ winget output and results

    /// <summary>A winget output line worth showing, or null for spinner / progress-bar noise.</summary>
    public static string CleanLine(string line)
    {
        if (line == null) return null;
        string t = line.Replace("\r", "").TrimEnd();
        string s = t.Trim();
        if (s.Length == 0) return null;
        if (s.IndexOfAny(new[] { '█', '▒', '▓', '░', '�' }) >= 0) return null;
        if (s.All(c => c == '-' || c == '\\' || c == '|' || c == '/' || c == ' ')) return null;
        return t;
    }

    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    public const int UpdateAllHasFailure = unchecked((int)0x8A15002C);
    public const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    public const int InstallAlreadyInstalled = unchecked((int)0x8A15010D);
    public const int NoApplicationsFound = unchecked((int)0x8A150014);
    public const int MultipleApplicationsFound = unchecked((int)0x8A150016);
    public const int AgreementsNotAccepted = unchecked((int)0x8A150041);
    public const int ProhibitsElevation = unchecked((int)0x8A150056);
    public const int PackageInUse = unchecked((int)0x8A150101);
    public const int NoNetwork = unchecked((int)0x8A150107);
    public const int RebootToFinish = unchecked((int)0x8A150109);
    public const int RebootForInstall = unchecked((int)0x8A15010A);
    public const int RebootInitiated = unchecked((int)0x8A15010B);
    public const int CancelledByUser = unchecked((int)0x8A15010C);
    public const int Downgrade = unchecked((int)0x8A15010E);
    public const int BlockedByPolicy = unchecked((int)0x8A15010F);

    /// <summary>Turn a winget exit code into a plain result. lastError is the last meaningful output line.</summary>
    public static CatalogueOutcome Describe(ShellResult r, bool cancelled, bool upgrade, string lastError)
    {
        if (cancelled) return new CatalogueOutcome { Cancelled = true, Text = "Cancelled" };
        if (r.TimedOut) return new CatalogueOutcome { Text = "Timed out - winget was stopped" };
        int c = r.ExitCode;
        switch (c)
        {
            case 0: return new CatalogueOutcome { Ok = true, Text = upgrade ? "Upgraded" : "Installed" };
            case UpdateNotApplicable: return new CatalogueOutcome { Ok = true, Text = "Already up to date" };
            case PackageAlreadyInstalled:
            case InstallAlreadyInstalled: return new CatalogueOutcome { Ok = true, Text = "Already installed" };
            case RebootToFinish:
            case RebootInitiated: return new CatalogueOutcome { Ok = true, Text = "Installed - restart Windows to finish" };
            case RebootForInstall: return new CatalogueOutcome { Text = "Windows needs a restart before this can be installed" };
            case NoApplicationsFound:
                return new CatalogueOutcome { Text = upgrade ? "Not installed, so nothing to upgrade" : "Not found in winget - the id may have changed" };
            case MultipleApplicationsFound: return new CatalogueOutcome { Text = "More than one package matched this id" };
            case AgreementsNotAccepted: return new CatalogueOutcome { Text = "The package's licence agreement was not accepted" };
            case ProhibitsElevation:
                return new CatalogueOutcome { Text = "This installer refuses to run as administrator. Install it from a normal (not elevated) Command Prompt with winget" };
            case PackageInUse: return new CatalogueOutcome { Text = "The app is running - close it and try again" };
            case NoNetwork: return new CatalogueOutcome { Text = "No internet connection" };
            case CancelledByUser: return new CatalogueOutcome { Text = "Cancelled in the installer" };
            case Downgrade: return new CatalogueOutcome { Text = "A newer version is already installed" };
            case BlockedByPolicy: return new CatalogueOutcome { Text = "Blocked by policy on this PC" };
            case UpdateAllHasFailure: return new CatalogueOutcome { Text = "Some upgrades failed - see the log" };
        }
        string code = c == -1 ? "winget did not run" : "exit code 0x" + c.ToString("X8");
        string detail = string.IsNullOrWhiteSpace(lastError) ? "" : " - " + lastError.Trim();
        return new CatalogueOutcome { Text = "Failed (" + code + ")" + detail };
    }
}
