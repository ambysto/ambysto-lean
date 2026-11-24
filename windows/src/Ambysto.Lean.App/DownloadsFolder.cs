using System;
using System.Runtime.InteropServices;

namespace Ambysto.Lean.App;

/// <summary>The Downloads known folder of this user (<c>FOLDERID_Downloads</c>), which the installer rule of Suggest in Large and old
/// needs (ASP-17, intent phase A). It reads the folder the user has set, through the shell; it opens nothing. It asks the shell, so
/// the caller runs it off the UI thread.</summary>
internal static partial class DownloadsFolder
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The folder's path, or null when the shell has none (no installer rule then).</summary>
    public static string? Read()
    {
        nint path = nint.Zero;
        try
        {
            return SHGetKnownFolderPath(in FolderIdDownloads, 0, nint.Zero, out path) == 0 && path != nint.Zero
                ? Marshal.PtrToStringUni(path)
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (path != nint.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, nint token, out nint path);
}
