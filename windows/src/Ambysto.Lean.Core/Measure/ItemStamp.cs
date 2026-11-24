using System;

namespace Ambysto.Lean.Core.Measure;

/// <summary>What identifies one file or folder at one moment: the volume serial, the 128-bit file id, the last write time
/// and the length (null for a folder). The same fields as the Ambysto Kit's <c>FileStamp</c>, which phase B compares it
/// with at move time; phase A reads it itself, from the folder batches of the walk. <c>LastWriteTimeUtcTicks</c> is a
/// Windows FILETIME (100 ns intervals since 1601-01-01 UTC), as in the Kit, not <see cref="DateTime.Ticks"/>.</summary>
public readonly record struct ItemStamp(ulong VolumeSerial, UInt128 FileId, long LastWriteTimeUtcTicks, long? Length);
