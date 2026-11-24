using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ambysto.Kit.Safety;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KitIdentity = Ambysto.Kit.Safety.FileIdentity;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>Builds the Kit's result types for the <c>IItemGate</c> doubles. <see cref="GateResult"/>,
/// <see cref="ApprovedItem"/>, <see cref="ItemFacts"/>, <see cref="InUseResult"/> and <see cref="FileFactsResult"/> have
/// only internal constructors in <c>Ambysto.Kit.Safety</c> (no factory is public), so a double of Lean's seam can only make
/// them through reflection. Each constructor is looked up by its exact parameter list; a Kit change that breaks the list
/// fails the test with a message naming it, never silently.</summary>
internal static class KitObjects
{
    private const BindingFlags Ctor = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>The Kit's facts for one item; the profile's volume serial is <paramref name="volumeSerial"/> unless
    /// <paramref name="systemVolumeSerial"/> gives another (an item off the profile's volume).</summary>
    public static ItemFacts Facts(string canonicalPath, ulong volumeSerial, IReadOnlyList<OwnerFact>? owners = null, bool isSystemLocation = false, ulong? systemVolumeSerial = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profiles = Path.GetDirectoryName(profile) ?? profile;
        return Make<ItemFacts>([typeof(string), typeof(string), typeof(string), typeof(ulong), typeof(ulong), typeof(IReadOnlyList<OwnerFact>), typeof(bool)],
            canonicalPath, profile, profiles, volumeSerial, systemVolumeSerial ?? volumeSerial, owners ?? Array.Empty<OwnerFact>(), isSystemLocation);
    }

    public static ApprovedItem Approved(string canonicalPath, FileStamp stamp, long allocatedBytes, long logicalBytes, int fileCount) =>
        Make<ApprovedItem>([typeof(string), typeof(FileStamp), typeof(long), typeof(long), typeof(int), typeof(ItemFacts)],
            canonicalPath, stamp, allocatedBytes, logicalBytes, fileCount, Facts(canonicalPath, stamp.Identity.VolumeSerial));

    public static GateResult Approve(ApprovedItem item) => Make<GateResult>([typeof(ApprovedItem)], item);

    public static GateResult Refuse(RefusalReason reason, string? detail = null) =>
        Make<GateResult>([typeof(RefusalReason), typeof(string)], reason, detail);

    public static InUseResult InUse(IReadOnlyList<InUseHolder> holders, int unreadableProcessCount = 0) =>
        Make<InUseResult>([typeof(IReadOnlyList<InUseHolder>), typeof(int)], holders, unreadableProcessCount);

    public static InUseResult NotInUse() => InUse(Array.Empty<InUseHolder>());

    public static FileFactsResult FactsOk(string canonicalPath, FileStamp stamp) =>
        Make<FileFactsResult>([typeof(string), typeof(FileStamp?), typeof(bool), typeof(RefusalReason?), typeof(string)],
            canonicalPath, (FileStamp?)stamp, false, null, null);

    public static FileFactsResult FactsFailed(RefusalReason reason) =>
        Make<FileFactsResult>([typeof(string), typeof(FileStamp?), typeof(bool), typeof(RefusalReason?), typeof(string)],
            null, null, false, (RefusalReason?)reason, null);

    /// <summary>The real facts of <paramref name="path"/> (the Kit's own read), required to succeed.</summary>
    public static FileFactsResult RealFacts(string path)
    {
        var facts = FileFacts.Read(path);
        Assert.IsTrue(facts.Succeeded, $"FileFacts.Read({path}) failed: {facts.FailureReason} {facts.Detail}");
        return facts;
    }

    public static FileStamp RealStamp(string path) => RealFacts(path).Stamp!.Value;

    /// <summary>The same stamp with another identity (one more in the file id).</summary>
    public static FileStamp WithOtherIdentity(FileStamp stamp) =>
        stamp with { Identity = new KitIdentity(stamp.Identity.VolumeSerial, stamp.Identity.FileId + 1) };

    /// <summary>The logical size and file count of a file, or of every file below a folder (the Recycle Bin's measure).</summary>
    public static (long Logical, int Files) Measure(string path)
    {
        if (File.Exists(path))
        {
            return (new FileInfo(path).Length, 1);
        }

        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f).Length).ToList();
        return (files.Sum(), files.Count);
    }

    /// <summary>An approval of the item now at <paramref name="realPath"/>, measured now, under the canonical spelling
    /// <paramref name="approvedPath"/> (by default the Kit's canonical path).</summary>
    public static GateResult ApproveNow(string realPath, string? approvedPath = null)
    {
        var facts = RealFacts(realPath);
        var (logical, files) = Measure(realPath);
        return Approve(Approved(approvedPath ?? facts.CanonicalPath!, facts.Stamp!.Value, logical, logical, files));
    }

    private static T Make<T>(Type[] parameters, params object?[] arguments)
    {
        var ctor = typeof(T).GetConstructor(Ctor, binder: null, parameters, modifiers: null);
        if (ctor is null)
        {
            Assert.Fail($"the Kit's {typeof(T).Name} has no constructor ({string.Join(", ", parameters.Select(p => p.Name))}); update KitObjects to the Kit's current shape");
        }

        try
        {
            return (T)ctor!.Invoke(arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
    }
}
