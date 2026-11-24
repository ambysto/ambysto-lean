using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Ambysto.Lean.Core.Measure;
using Microsoft.Win32.SafeHandles;

namespace Ambysto.Lean.Core.Rules;

/// <summary>What the pre-check reads about one folder or file: whether it is a directory, whether it is a link (a
/// name-surrogate reparse point: junction, symbolic link, mount point), and who owns it. The attributes are read by name
/// first, and an item marked recall-on-open or recall-on-data-access is never opened (as the Kit and the walker do);
/// anything else is read through a handle opened for attributes and the security descriptor only, never for data. An item
/// that cannot be read, or a cloud item, has no facts and is never the user's.</summary>
internal readonly record struct OwnerFacts(bool IsDirectory, bool IsReparsePoint, bool IsLink, SecurityIdentifier? Owner)
{
    private const uint ReadControl = 0x00020000;
    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x1;

    private static readonly SecurityIdentifier? CurrentUser = ReadCurrentUser();

    /// <summary>The facts of <paramref name="path"/>, or null when it cannot be opened (missing, access denied).</summary>
    public static unsafe OwnerFacts? Read(string path)
    {
        var byName = RulesNative.GetFileAttributes(MeasureNative.ToExtendedPath(path));
        if (byName == RulesNative.InvalidFileAttributes
            || (byName & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess)) != 0)
        {
            return null;
        }

        using var handle = MeasureNative.CreateFile(
            MeasureNative.ToExtendedPath(path),
            ReadControl | MeasureNative.FileReadAttributes,
            MeasureNative.ShareAll,
            0,
            MeasureNative.OpenExisting,
            MeasureNative.FlagBackupSemantics | MeasureNative.FlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var tag = stackalloc uint[2];
        if (!MeasureNative.GetFileInformationByHandleEx(handle, MeasureNative.FileAttributeTagInfoClass, (byte*)tag, 8))
        {
            return null;
        }

        var attributes = tag[0];
        if ((attributes & (MeasureNative.AttributeRecallOnOpen | MeasureNative.AttributeRecallOnDataAccess)) != 0)
        {
            return null;
        }

        var isReparsePoint = (attributes & MeasureNative.AttributeReparsePoint) != 0;
        var isLink = isReparsePoint && (tag[1] & MeasureNative.ReparseTagNameSurrogate) != 0;
        return new OwnerFacts((attributes & MeasureNative.AttributeDirectory) != 0, isReparsePoint, isLink, ReadOwner(handle));
    }

    /// <summary>True when the owner is known and is the current user. The group "Administrators" is not the user: a folder
    /// made from an elevated prompt or by an installer does not count (intent decision 7).</summary>
    public bool IsOwnedByCurrentUser => Owner is not null && CurrentUser is not null && Owner.Equals(CurrentUser);

    private static SecurityIdentifier? ReadOwner(SafeFileHandle handle)
    {
        var result = RulesNative.GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation, out var owner, 0, 0, 0, out var descriptor);
        if (result != 0)
        {
            return null;
        }

        try
        {
            return owner == 0 ? null : new SecurityIdentifier(owner);
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            RulesNative.LocalFree(descriptor);
        }
    }

    private static SecurityIdentifier? ReadCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }
}

/// <summary>The read-only security calls of the pre-check.</summary>
internal static partial class RulesNative
{
    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo")]
    public static partial uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, out nint owner, nint group, nint dacl, nint sacl, out nint securityDescriptor);

    public const uint InvalidFileAttributes = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetFileAttributes(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    public static partial nint LocalFree(nint memory);
}
