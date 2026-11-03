using System.Runtime.InteropServices;

namespace Satsuma.Core.Tools;

/// <summary>
/// Deletes a file the way the Delete key does, so it can be got back.
///
/// <see cref="File.Delete"/> is not that. It unlinks the file and there is no undo, which
/// is fine for a temporary file and not fine for someone's original. Routing a delete to
/// the Recycle Bin is something you have to ask for, through the shell, and this is the
/// asking.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;             // no progress dialog
    private const ushort FOF_NOCONFIRMATION = 0x0010;     // no "are you sure"
    private const ushort FOF_ALLOWUNDO = 0x0040;          // the whole point
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW operation);

    /// <summary>
    /// True when a delete on this path would actually reach the Recycle Bin.
    ///
    /// A network share has no bin, and nor does a drive that has gone away. Satsuma asks
    /// before replacing anything, because the answer decides whether replacing it is a
    /// reversible act or a destructive one, and only one of those is worth doing quietly.
    /// </summary>
    public static bool Covers(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);

            // A UNC path belongs to another machine's file system; its deletes are final.
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;

            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends one file or folder to the Recycle Bin. Returns false if it is still there.
    ///
    /// The one dialog this can raise is Windows' own warning that the item is too big for
    /// the bin, or that the bin is turned off, and so would be destroyed rather than
    /// filed. That is worth interrupting someone for; nothing else here is.
    /// </summary>
    public static bool Send(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return true;

        // pFrom is a list, terminated by an empty entry: one trailing null comes from the
        // marshaller, and this is the other.
        var operation = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = path + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                     | FOF_NOERRORUI | FOF_WANTNUKEWARNING
        };

        var code = SHFileOperationW(ref operation);

        return code == 0
               && !operation.fAnyOperationsAborted
               && !File.Exists(path)
               && !Directory.Exists(path);
    }
}
