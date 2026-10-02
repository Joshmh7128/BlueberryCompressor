using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoCompressor.Core.Util;

/// <summary>The handful of Win32 calls the app needs: suspend/resume, Recycle Bin, DPAPI, keep-awake.</summary>
public static class Native
{
    // ---- pause / resume a running encoder ----

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);

    public static bool Suspend(Process process)
    {
        try { return !process.HasExited && NtSuspendProcess(process.Handle) == 0; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
    }

    public static bool Resume(Process process)
    {
        try { return !process.HasExited && NtResumeProcess(process.Handle) == 0; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
    }

    // ---- Recycle Bin ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Send a file to the Recycle Bin. FOF_WANTNUKEWARNING makes Windows ask before destroying a file
    /// it cannot recycle (too large for the bin, or a drive without one) instead of silently deleting it.
    /// </summary>
    public static void Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + '\0', // the list must be double-null terminated; marshalling adds the second
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING),
        };
        int result = SHFileOperationW(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Could not move '{path}' to the Recycle Bin (shell error 0x{result:X}).");
        if (File.Exists(path))
            throw new IOException($"'{path}' is still present after recycling.");
    }

    // ---- DPAPI: keep saved passwords readable only by this Windows user ----

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try { return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(protectedBase64), protect: false)); }
        catch (Exception ex) when (ex is FormatException or Win32Exception) { return ""; }
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var blobIn = new DATA_BLOB { cbData = input.Length, pbData = handle.AddrOfPinnedObject() };
            DATA_BLOB blobOut;
            bool ok = protect
                ? CryptProtectData(ref blobIn, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out blobOut)
                : CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out blobOut);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, output, 0, blobOut.cbData);
                return output;
            }
            finally { LocalFree(blobOut.pbData); }
        }
        finally { handle.Free(); }
    }

    // ---- keep the machine awake during long queue runs ----

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    /// <summary>Reset the system idle timer. Call periodically while work is in progress.</summary>
    public static void PokeAwake() => SetThreadExecutionState(ES_SYSTEM_REQUIRED);
}
