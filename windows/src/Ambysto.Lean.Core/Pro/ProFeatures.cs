using System;
using System.Collections.Generic;
using Ambysto.Lean.Core.Find;
using Ambysto.Lean.Core.Measure;

namespace Ambysto.Lean.Core.Pro;

/// <summary>What a report holds (phase A: the scan; the journal joins in phase B with the journal itself).</summary>
public sealed record ScanReport(DateTimeOffset Time, VolumeCardFacts Volume, Coverage Coverage, IReadOnlyList<ScanNode> TopFolders, IReadOnlyList<LargeOldRow> LargeOld, IReadOnlyList<DuplicateGroup> Duplicates);

/// <summary>The open-at-login switch of Pro (docs/spec/06): this app's own startup entry.</summary>
public interface ILoginItem
{
    /// <summary>True when the entry exists.</summary>
    bool IsOn { get; }

    /// <summary>Creates the entry.</summary>
    void TurnOn();

    /// <summary>Removes the entry.</summary>
    void TurnOff();
}

/// <summary>The low-disk alert of Pro: one check per call.</summary>
public interface ILowDiskAlert
{
    /// <summary>True when the alert fires now.</summary>
    bool Check();
}

/// <summary>The four Pro features, behind one seam (ASP-33). The public build has <see cref="NoProFeatures"/>; a release
/// build adds the private module, which implements this. The licence check and the "Pro" labels do not live here: they
/// stay in the public code and decide only whether the locked state shows.</summary>
public interface IProFeatures
{
    /// <summary>False when this build holds no Pro features.</summary>
    bool Available { get; }

    /// <summary>The login item for <paramref name="executablePath"/>; null when unavailable. Throws
    /// <see cref="ArgumentException"/> for a path it cannot use.</summary>
    ILoginItem? CreateLoginItem(string executablePath);

    /// <summary>A low-disk alert reading free space through <paramref name="freeBytes"/>; null when unavailable.</summary>
    ILowDiskAlert? CreateLowDiskAlert(Func<long> freeBytes, TimeProvider time);

    /// <summary>How often the alert is checked.</summary>
    TimeSpan LowDiskCheckInterval { get; }

    /// <summary>The free space below which the alert fires, in bytes.</summary>
    long LowDiskThresholdBytes { get; }

    /// <summary>The report as Markdown or JSON text; null when unavailable. Throws <see cref="ArgumentException"/> for a
    /// name that is not valid text.</summary>
    string? ExportReport(ScanReport report, bool asJson);
}

/// <summary>The public build: no Pro feature is available, whatever the key says.</summary>
public sealed class NoProFeatures : IProFeatures
{
    /// <summary>The one instance.</summary>
    public static NoProFeatures Instance { get; } = new();

    private NoProFeatures()
    {
    }

    /// <inheritdoc />
    public bool Available => false;

    /// <inheritdoc />
    public ILoginItem? CreateLoginItem(string executablePath) => null;

    /// <inheritdoc />
    public ILowDiskAlert? CreateLowDiskAlert(Func<long> freeBytes, TimeProvider time) => null;

    /// <inheritdoc />
    public TimeSpan LowDiskCheckInterval => TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public long LowDiskThresholdBytes => 10 * ByteUnits.GB;

    /// <inheritdoc />
    public string? ExportReport(ScanReport report, bool asJson) => null;
}
