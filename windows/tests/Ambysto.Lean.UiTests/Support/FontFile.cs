using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>The horizontal advances of a TrueType font file, read from its own tables (head, hhea, hmtx and the Unicode
/// cmap, format 4): the oracle for "this text is drawn in this font", compared with the character boxes UI Automation
/// reports for the rendered text.</summary>
internal sealed class FontFile
{
    private readonly ushort[] advances;
    private readonly Dictionary<char, ushort> glyphs;

    private FontFile(string path, int unitsPerEm, ushort[] advances, Dictionary<char, ushort> glyphs)
    {
        Path = path;
        UnitsPerEm = unitsPerEm;
        this.advances = advances;
        this.glyphs = glyphs;
    }

    public string Path { get; }

    public int UnitsPerEm { get; }

    /// <summary>Reads the font at <paramref name="path"/>.</summary>
    public static FontFile Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var tables = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = U16(data, 4);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + (16 * i);
            tables[System.Text.Encoding.ASCII.GetString(data, record, 4)] = (int)U32(data, record + 8);
        }

        var unitsPerEm = U16(data, tables["head"] + 18);
        var metrics = U16(data, tables["hhea"] + 34);
        var glyphCount = U16(data, tables["maxp"] + 4);
        var hmtx = tables["hmtx"];
        var advances = new ushort[glyphCount];
        for (var g = 0; g < glyphCount; g++)
        {
            advances[g] = U16(data, hmtx + (4 * Math.Min(g, metrics - 1)));
        }

        return new FontFile(path, unitsPerEm, advances, ReadCmap(data, tables["cmap"]));
    }

    /// <summary>The advance of <paramref name="c"/> in font units, or null when the font has no glyph for it.</summary>
    public int? Advance(char c) => glyphs.TryGetValue(c, out var glyph) && glyph != 0 ? advances[glyph] : null;

    private static Dictionary<char, ushort> ReadCmap(byte[] data, int cmap)
    {
        var map = new Dictionary<char, ushort>();
        var count = U16(data, cmap + 2);
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + (8 * i);
            var platform = U16(data, record);
            var encoding = U16(data, record + 2);
            var table = cmap + (int)U32(data, record + 4);
            if (U16(data, table) != 4 || !((platform == 3 && encoding == 1) || platform == 0))
            {
                continue;
            }

            var segments = U16(data, table + 6) / 2;
            var ends = table + 14;
            var starts = ends + (2 * segments) + 2;
            var deltas = starts + (2 * segments);
            var offsets = deltas + (2 * segments);
            for (var s = 0; s < segments; s++)
            {
                var end = U16(data, ends + (2 * s));
                var start = U16(data, starts + (2 * s));
                var delta = (short)U16(data, deltas + (2 * s));
                var rangeOffset = U16(data, offsets + (2 * s));
                for (var c = start; c <= end && c != 0xFFFF; c++)
                {
                    ushort glyph;
                    if (rangeOffset == 0)
                    {
                        glyph = (ushort)(c + delta);
                    }
                    else
                    {
                        var at = offsets + (2 * s) + rangeOffset + (2 * (c - start));
                        glyph = U16(data, at);
                        if (glyph != 0)
                        {
                            glyph = (ushort)(glyph + delta);
                        }
                    }

                    map.TryAdd((char)c, glyph);
                }
            }

            return map;
        }

        throw new InvalidDataException("no Unicode format 4 cmap");
    }

    private static ushort U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at, 2));

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
}
