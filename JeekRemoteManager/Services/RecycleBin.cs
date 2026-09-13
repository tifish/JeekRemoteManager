using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace JeekRemoteManager.Services;

/// <summary>
/// Sends files and folders to the Windows Recycle Bin instead of deleting them outright,
/// so a removal from the connections tree — by hand or by an agent — can be undone.
/// Runs without any shell UI: an agent's delete must never stop on a dialog.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Moves a file or a whole folder to the Recycle Bin. Throws if it could not.</summary>
    public static void Send(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // pFrom is a list of paths ending in a double null; the marshaller adds one.
            pFrom = Path.GetFullPath(path) + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };

        var result = SHFileOperation(ref operation);
        if (result != 0)
            throw new IOException($"Could not move '{path}' to the Recycle Bin.", new Win32Exception(result));
        if (operation.fAnyOperationsAborted)
            throw new IOException($"Moving '{path}' to the Recycle Bin was aborted.");
    }
}
