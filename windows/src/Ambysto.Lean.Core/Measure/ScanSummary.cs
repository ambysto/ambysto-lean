using System;

namespace Ambysto.Lean.Core.Measure;

/// <summary>What a scan read (docs/spec/01 Windows note): files and folders read, items that could not be read, the time
/// it took, and whether it was cancelled.</summary>
public sealed record ScanSummary(long Files, long Folders, long Unreadable, TimeSpan Elapsed, bool Cancelled);
