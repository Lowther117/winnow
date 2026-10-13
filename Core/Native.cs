using System.Runtime.InteropServices;
using System.Text;

namespace Winnow.Core;

/// <summary>
/// Every Win32 / NT call Winnow makes, in one place. 64-bit only (x64 and
/// ARM64 share the same struct layouts), which is all build-exe.bat produces.
/// </summary>
internal static class Native
{
    // ------------------------------------------------------------------ ntdll
    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern uint NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    public static extern uint NtResumeProcess(IntPtr process);

    // CharSet.Unicode on the struct itself: szCSDVersion is 128 WCHARs (276-byte struct), not 128 ANSI bytes.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RTL_OSVERSIONINFOW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    public static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW info);

    // --------------------------------------------------------------- kernel32
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_VM_READ = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    /// <summary>True in <paramref name="critical"/> when ending the process blue-screens Windows (csrss, wininit, smss...).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessCritical(IntPtr process, out bool critical);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeAvailable, out ulong total, out ulong totalFree);

    // ---------------------------------------------------------------- shell32
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x100;
    public const uint SHGFI_SMALLICON = 0x1;
    public const uint SHGFI_LARGEICON = 0x0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr icon);

    // Bringing an already-open Winnow window to the front (single-instance launch).
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    // ----------------------------------------------------------------- dwmapi
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // --------------------------------------------------------------- advapi32
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenUser = 1;

    // ----------------------------------------------------------------- helpers

    /// <summary>Read a UNICODE_STRING (Length, MaximumLength, pad, Buffer) at <paramref name="at"/>.</summary>
    public static string ReadUnicodeString(IntPtr at)
    {
        int len = (ushort)Marshal.ReadInt16(at);
        IntPtr buf = Marshal.ReadIntPtr(at, 8);
        if (len == 0 || buf == IntPtr.Zero) return "";
        return Marshal.PtrToStringUni(buf, len / 2);
    }

    public static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FOLDERID_Downloads, 0, IntPtr.Zero, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                string s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s;
            }
        }
        catch { }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dl = Path.Combine(home, "Downloads");
        return Directory.Exists(dl) ? dl : home;
    }
}
