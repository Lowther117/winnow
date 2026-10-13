using System.Collections.Concurrent;
using System.Diagnostics;

namespace Winnow.Core;

/// <summary>
/// One folder in a size scan. Only folders are kept in memory (a whole drive
/// can hold millions of files); the files of the folder being viewed are
/// listed again on demand, and the largest files are kept separately.
/// </summary>
public sealed class FolderNode
{
    public string Name = "";
    internal string RootPath;                  // set on the scan root only
    public FolderNode Parent;
    public List<FolderNode> Children = new();

    public long OwnSize, OwnFiles;             // files directly in this folder
    public DateTime OwnNewest = DateTime.MinValue;

    public long Size, Files, Folders;          // everything below, this folder included
    public DateTime Newest = DateTime.MinValue; // newest file modification time below (local)
    public bool Denied;                        // this folder could not be read
    public int DeniedBelow;                    // unreadable folders here and below

    /// <summary>Built from the parent chain, so the tree does not hold a full path per folder.</summary>
    public string FullPath => Parent == null ? (RootPath ?? Name) : Path.Combine(Parent.FullPath, Name);
}

public sealed class FolderFile
{
    public string Name = "";
    public string Dir = "";
    public long Size;
    public DateTime Modified;
    public string FullPath => Path.Combine(Dir, Name);
}

public sealed class FolderScan
{
    public FolderNode Root;
    public List<FolderFile> Largest = new();   // largest first
    public int Links;                          // junctions / symbolic links not followed
    public int CloudFiles;                     // cloud-only placeholders (not on this disk)
    public TimeSpan Elapsed;
    public bool Cancelled;
}

/// <summary>
/// WizTree/WinDirStat-lite. Folders are read one level at a time
/// (EnumerateFileSystemInfos, no recursion inside the framework) by a small
/// pool of worker threads sharing one queue, so a huge folder such as
/// C:\Windows\WinSxS does not leave the other threads idle. Reparse points
/// (junctions, symbolic links, mount points) are never followed.
/// Sizes are logical file sizes (FileInfo.Length), not size on disk.
/// </summary>
public sealed class FolderSizer
{
    public const int TopFiles = 200;

    long _files, _bytes;
    int _pending;
    volatile string _current = "";

    /// <summary>Files counted so far (safe to read from any thread while scanning).</summary>
    public long FilesSeen => Interlocked.Read(ref _files);
    public long BytesSeen => Interlocked.Read(ref _bytes);
    /// <summary>The folder a worker is reading right now.</summary>
    public string Current => _current;

    static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,      // we want to know which folders could not be read
        AttributesToSkip = 0,            // include hidden and system files
        ReturnSpecialDirectories = false,
    };

    const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>OneDrive-style placeholder whose data is not on this disk.</summary>
    static bool CloudOnly(FileAttributes a) => (a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    sealed class Worker
    {
        public readonly PriorityQueue<FolderFile, long> Top = new();   // min-heap: smallest of the kept files on top
        public int Links, Cloud;

        public void Offer(string name, string dir, long size, DateTime modified)
        {
            if (Top.Count < TopFiles)
                Top.Enqueue(new FolderFile { Name = name, Dir = dir, Size = size, Modified = modified }, size);
            else if (Top.TryPeek(out _, out long min) && size > min)
                Top.EnqueueDequeue(new FolderFile { Name = name, Dir = dir, Size = size, Modified = modified }, size);
        }
    }

    public Task<FolderScan> ScanAsync(string rootPath, CancellationToken ct) => Task.Run(() => Scan(rootPath, ct));

    /// <summary>Scan a drive or folder. Blocking - call through ScanAsync from the UI.</summary>
    public FolderScan Scan(string rootPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string full = Path.GetFullPath(rootPath);
        var root = new FolderNode { Name = full, RootPath = full };

        var queue = new ConcurrentQueue<FolderNode>();
        _pending = 1;
        queue.Enqueue(root);

        int n = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var workers = new Worker[n];
        var tasks = new Task[n];
        for (int i = 0; i < n; i++)
        {
            var w = new Worker();
            workers[i] = w;
            tasks[i] = Task.Factory.StartNew(() => Loop(w, queue, ct), CancellationToken.None,
                                             TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        try { Task.WaitAll(tasks); }
        catch (AggregateException ex) { Log.Write("folder scan: " + ex.InnerException?.Message); }

        // Totals: every child appears after its parent in this list, so walk it backwards.
        var order = new List<FolderNode> { root };
        for (int i = 0; i < order.Count; i++) order.AddRange(order[i].Children);
        for (int i = order.Count - 1; i >= 0; i--) Total(order[i]);

        var scan = new FolderScan
        {
            Root = root,
            Elapsed = sw.Elapsed,
            Cancelled = ct.IsCancellationRequested,
            Links = workers.Sum(w => w.Links),
            CloudFiles = workers.Sum(w => w.Cloud),
        };
        scan.Largest = workers.SelectMany(w => w.Top.UnorderedItems.Select(x => x.Element))
                              .OrderByDescending(f => f.Size)
                              .Take(TopFiles)
                              .ToList();
        _current = "";
        return scan;
    }

    void Loop(Worker w, ConcurrentQueue<FolderNode> queue, CancellationToken ct)
    {
        var spin = new SpinWait();
        while (!ct.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var node))
            {
                try { Read(node, w, ct); }
                catch (Exception ex) { node.Denied = true; Log.Write("folder scan: " + ex.Message); }
                // count the children before this folder is marked done, so _pending never touches 0 early
                foreach (var c in node.Children)
                {
                    Interlocked.Increment(ref _pending);
                    queue.Enqueue(c);
                }
                Interlocked.Decrement(ref _pending);
                spin.Reset();
            }
            else if (Volatile.Read(ref _pending) == 0) break;
            else spin.SpinOnce();
        }
    }

    void Read(FolderNode node, Worker w, CancellationToken ct)
    {
        string dir = node.FullPath;
        _current = dir;
        long files = 0, bytes = 0;
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Flat))
            {
                if (ct.IsCancellationRequested) break;
                var a = e.Attributes;
                if ((a & FileAttributes.Directory) != 0)
                {
                    if ((a & FileAttributes.ReparsePoint) != 0) { w.Links++; continue; }
                    node.Children.Add(new FolderNode { Name = e.Name, Parent = node });
                }
                else
                {
                    if (CloudOnly(a)) { w.Cloud++; continue; }
                    long len = e is FileInfo fi ? fi.Length : 0;
                    DateTime mod = e.LastWriteTime;
                    node.OwnSize += len;
                    node.OwnFiles++;
                    if (mod > node.OwnNewest) node.OwnNewest = mod;
                    w.Offer(e.Name, dir, len, mod);
                    files++;
                    bytes += len;
                }
            }
        }
        catch (Exception) { node.Denied = true; }   // access denied, folder vanished, bad path ...
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, bytes);
    }

    static void Total(FolderNode n)
    {
        long size = n.OwnSize, files = n.OwnFiles, folders = 0;
        DateTime newest = n.OwnNewest;
        int denied = n.Denied ? 1 : 0;
        foreach (var c in n.Children)
        {
            size += c.Size;
            files += c.Files;
            folders += 1 + c.Folders;
            denied += c.DeniedBelow;
            if (c.Newest > newest) newest = c.Newest;
        }
        n.Size = size;
        n.Files = files;
        n.Folders = folders;
        n.Newest = newest;
        n.DeniedBelow = denied;
        if (n.Children.Count > 1) n.Children.Sort((x, y) => y.Size.CompareTo(x.Size));
    }

    /// <summary>The files directly inside one folder (listed fresh, for the drill-down view).</summary>
    public static List<FolderFile> ListFiles(string dir)
    {
        var list = new List<FolderFile>();
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Flat))
            {
                if (CloudOnly(f.Attributes)) continue;
                list.Add(new FolderFile { Name = f.Name, Dir = dir, Size = f.Length, Modified = f.LastWriteTime });
            }
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Find the node for a folder path inside a scanned tree, or null.</summary>
    public static FolderNode Find(FolderNode root, string dir)
    {
        if (root == null || string.IsNullOrEmpty(dir)) return null;
        string r = root.FullPath.TrimEnd('\\');
        string d = dir.TrimEnd('\\');
        if (d.Equals(r, StringComparison.OrdinalIgnoreCase)) return root;
        if (!d.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var node = root;
        foreach (var part in d.Substring(r.Length + 1).Split('\\'))
        {
            node = node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node == null) return null;
        }
        return node;
    }
}
