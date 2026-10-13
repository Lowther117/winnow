using System.Runtime.InteropServices;

namespace Winnow.Core;

/// <summary>
/// Shell calls used by the Disk Space page: the Recycle Bin (size and empty)
/// and "delete to Recycle Bin". 64-bit layouts (shellapi.h only packs these
/// structs to 1 byte on 32-bit Windows, which Winnow never targets).
/// Shell calls run on their own STA thread so the UI thread never waits on them.
/// </summary>
internal static class DiskNative
{
    // ------------------------------------------------------------ Recycle Bin

    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

    public const uint SHERB_NOCONFIRMATION = 0x1;
    public const uint SHERB_NOPROGRESSUI = 0x2;
    public const uint SHERB_NOSOUND = 0x4;

    // ------------------------------------------------------- SHFileOperation

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;               // double-null terminated list
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public const uint FO_DELETE = 0x3;
    public const ushort FOF_NOCONFIRMATION = 0x10;
    public const ushort FOF_ALLOWUNDO = 0x40;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    // ------------------------------------------------------------- helpers

    /// <summary>Run work on a fresh STA thread (what shell APIs expect) and await the result.</summary>
    public static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        th.IsBackground = true;
        th.Name = "Winnow shell";
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }

    /// <summary>Size and item count of the Recycle Bin on every drive, plus the HRESULT (0 = read OK).</summary>
    public static (long bytes, long items, int hr) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(null, ref info);
        return (info.i64Size, info.i64NumItems, hr);
    }

    /// <summary>Empty the Recycle Bin on every drive with no confirmation, progress window or sound. Returns the HRESULT.</summary>
    public static int EmptyRecycleBin() =>
        SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    /// <summary>
    /// Send files or folders to the Recycle Bin. Winnow asks first, so the shell's own
    /// confirmation is off - except that Windows still warns before permanently deleting
    /// anything too big for the Recycle Bin (FOF_WANTNUKEWARNING).
    /// </summary>
    public static Task<(bool ok, string message)> SendToRecycleBin(IReadOnlyList<string> fullPaths)
    {
        return RunSta<(bool ok, string message)>(() =>
        {
            if (fullPaths == null || fullPaths.Count == 0) return (false, "nothing selected");
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", fullPaths) + "\0\0",
                pTo = null,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
                lpszProgressTitle = null,
            };
            int r = SHFileOperation(ref op);
            if (r != 0) return (false, "Windows reported error 0x" + r.ToString("X"));
            if (op.fAnyOperationsAborted) return (false, "cancelled");
            return (true, "sent to the Recycle Bin");
        });
    }
}
