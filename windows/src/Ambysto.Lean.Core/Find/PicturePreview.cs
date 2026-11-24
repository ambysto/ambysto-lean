using System;
using System.Collections.Generic;
using System.IO;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;

namespace Ambysto.Lean.Core.Find;

/// <summary>What a preview read found (ASP-15).</summary>
public enum PreviewResult
{
    /// <summary>The bytes were read from a local file.</summary>
    Read,

    /// <summary>The file is online-only (recall on data access, recall on open, or offline): it was not opened.</summary>
    OnlineOnly,

    /// <summary>The file is larger than the limit: it was not read.</summary>
    TooLarge,

    /// <summary>The file could not be opened or read.</summary>
    Unreadable,
}

/// <summary>The outcome of <see cref="PicturePreview.Read"/>: <see cref="Bytes"/> only with <see cref="PreviewResult.Read"/>.</summary>
public sealed record PreviewRead(PreviewResult Result, byte[]? Bytes);

/// <summary>ASP-15 (owner decision 2026-10-18): a picture preview for local files only. HARD (the "do not download cloud
/// files" rule): the attributes are read first, and a file with FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS,
/// FILE_ATTRIBUTE_RECALL_ON_OPEN or FILE_ATTRIBUTE_OFFLINE is never opened; any other is opened with
/// FILE_FLAG_OPEN_NO_RECALL and read up to <see cref="MaxBytes"/>. Never through the shell's thumbnail provider.</summary>
public static class PicturePreview
{
    /// <summary>20 MB: a larger picture is not read.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>True for the preview types: .jpg, .jpeg, .png, .gif, .bmp, .webp, .tif, .tiff (case ignored).</summary>
    public static bool IsPictureType(string path) => path is not null && PictureTypes.Contains(Path.GetExtension(path));

    /// <summary>Reads <paramref name="path"/> for a preview, under the cloud guard.</summary>
    public static PreviewRead Read(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Unreadable;
        }

        // 1. The attributes, read by name before any open: an online-only file is never opened.
        var extended = MeasureNative.ToExtendedPath(path);
        var attributes = RulesNative.GetFileAttributes(extended);
        if (attributes == RulesNative.InvalidFileAttributes || (attributes & MeasureNative.AttributeDirectory) != 0)
        {
            return Unreadable;
        }

        if ((attributes & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess | AttributeOffline)) != 0)
        {
            return new PreviewRead(PreviewResult.OnlineOnly, null);
        }

        // 2. Opened without following a link (FILE_FLAG_OPEN_REPARSE_POINT) and so that the handle cannot recall data
        // (FILE_FLAG_OPEN_NO_RECALL), then checked again by the handle (hard review 2026-10-18, MAJOR): the name may have been
        // replaced since step 1, by a link to an online-only file or by a placeholder. A link (a name-surrogate tag) is refused;
        // a cloud placeholder that is hydrated has no recall attribute and is read; anything with a recall or offline
        // attribute is refused before any byte is read.
        using var handle = MeasureNative.CreateFile(extended, GenericRead, MeasureNative.ShareAll, 0, MeasureNative.OpenExisting,
            FlagOpenNoRecall | MeasureNative.FlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            return Unreadable;
        }

        var verdict = CheckHandle(handle);
        if (verdict is not null)
        {
            return verdict;
        }

        try
        {
            var length = RandomAccess.GetLength(handle);
            if (length > MaxBytes)
            {
                return new PreviewRead(PreviewResult.TooLarge, null);
            }

            // 3. At most the length read now; a file that grew meanwhile is read only up to it.
            var bytes = new byte[length];
            var done = 0;
            while (done < bytes.Length)
            {
                var read = RandomAccess.Read(handle, bytes.AsSpan(done), done);
                if (read == 0)
                {
                    return Unreadable; // shorter than its length: changed while it was read
                }

                done += read;
            }

            return new PreviewRead(PreviewResult.Read, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable;
        }
    }

    // The facts of the opened item, from its handle: null when it may be read, otherwise the answer to give.
    private static unsafe PreviewRead? CheckHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var tag = stackalloc uint[2];
        if (!MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileAttributeTagInfoClass, (byte*)tag, 8))
        {
            return Unreadable;
        }

        var attributes = tag[0];
        if ((attributes & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess | AttributeOffline)) != 0)
        {
            return new PreviewRead(PreviewResult.OnlineOnly, null);
        }

        if ((attributes & MeasureNative.AttributeDirectory) != 0
            || ((attributes & MeasureNative.AttributeReparsePoint) != 0 && (tag[1] & MeasureNative.ReparseTagNameSurrogate) != 0))
        {
            return Unreadable; // a folder, or a link: never followed
        }

        return null;
    }

    private const uint GenericRead = 0x80000000;
    private const uint FlagOpenNoRecall = 0x00100000;
    private const uint AttributeOffline = 0x1000;

    private static readonly PreviewRead Unreadable = new(PreviewResult.Unreadable, null);

    private static readonly HashSet<string> PictureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
    };
}
