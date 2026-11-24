using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>Opt-in criterion 3 evidence, called after every PutBackTests case. Run in an unlocked, dedicated console
/// desktop with LEAN_PUTBACK_VISUAL_PROOF set to an absolute artifact directory. Captures that desktop (which should
/// contain only the proof runner), records the case and visible testhost windows, then fails if any such window exists.
/// This is post-case evidence; a call blocked forever by a modal still needs the runner's timeout to fail the run.</summary>
internal static class PutBackVisualProof
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Capture(TestContext context)
    {
        var directory = Environment.GetEnvironmentVariable("LEAN_PUTBACK_VISUAL_PROOF");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Assert.IsTrue(Path.IsPathFullyQualified(directory), "visual proof directory must be absolute");
        Assert.IsTrue(Environment.UserInteractive && Process.GetCurrentProcess().SessionId != 0, "visual proof requires the console desktop, never session 0");
        var desktop = OpenInputDesktop(0, false, 0x100);
        Assert.AreNotEqual(IntPtr.Zero, desktop, "unlock the console desktop for visual proof");
        _ = CloseDesktop(desktop);
        Directory.CreateDirectory(directory);

        var windows = new List<object>();
        var pid = Environment.ProcessId;
        WindowCallback callback = (window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var owner);
            if (owner == (uint)pid && IsWindowVisible(window))
            {
                var title = new char[1024];
                var className = new char[256];
                var titleLength = GetWindowText(window, title, title.Length);
                var classLength = GetClassName(window, className, className.Length);
                windows.Add(new { handle = window.ToInt64(), title = new string(title, 0, Math.Max(0, titleLength)), className = new string(className, 0, Math.Max(0, classLength)) });
            }
            return true;
        };
        if (!EnumWindows(callback, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "EnumWindows");
        var displayName = context.GetType().GetProperty("TestDisplayName")?.GetValue(context)?.ToString() ?? context.TestName;
        var parameters = context.GetType().GetProperty("TestData")?.GetValue(context) as object[];
        var name = string.Concat(context.TestName.SelectSafeFileName());
        var stem = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N"));
        var bitmap = stem + ".bmp";
        SaveDesktop(bitmap);
        var record = stem + ".json";
        File.WriteAllText(record, JsonSerializer.Serialize(new
        {
            testName = context.TestName, displayName, parameters, outcomeBeforeCleanup = context.CurrentTestOutcome.ToString(),
            utc = DateTimeOffset.UtcNow, processId = pid, sessionId = Process.GetCurrentProcess().SessionId,
            visibleTestProcessWindows = windows, noVisibleTestProcessWindow = windows.Count == 0, screenshot = Path.GetFileName(bitmap),
            scope = "After case completed; only current testhost process windows are asserted. Dedicated console desktop screenshot."
        }, JsonOptions));
        context.AddResultFile(bitmap);
        context.AddResultFile(record);
        Assert.AreEqual(0, windows.Count, "Put back must show no dialog; inspect " + record + " and " + bitmap);
    }

    private static IEnumerable<char> SelectSafeFileName(this string value)
    {
        foreach (var c in value) yield return char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
    }

    private static void SaveDesktop(string path)
    {
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        var screen = GetDC(IntPtr.Zero);
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            var width = GetSystemMetrics(78);
            var height = GetSystemMetrics(79);
            Assert.IsTrue(screen != IntPtr.Zero && width > 0 && height > 0, "readable desktop DC");
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "create screenshot bitmap");
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, GetSystemMetrics(76), GetSystemMetrics(77), 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "BitBlt");
            _ = SelectObject(memory, old);
            old = IntPtr.Zero;
            var pixels = new byte[checked(width * height * 4)];
            var header = new BitmapHeader { Size = 40, Width = width, Height = height, Planes = 1, BitCount = 32, ImageSize = (uint)pixels.Length };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) != height)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDIBits");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(file);
            writer.Write((ushort)0x4D42); writer.Write(checked(54 + pixels.Length)); writer.Write(0); writer.Write(54);
            writer.Write(header.Size); writer.Write(width); writer.Write(height); writer.Write(header.Planes); writer.Write(header.BitCount);
            writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(pixels);
        }
        finally
        {
            if (old != IntPtr.Zero) _ = SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
            if (memory != IntPtr.Zero) _ = DeleteDC(memory);
            if (screen != IntPtr.Zero) _ = ReleaseDC(IntPtr.Zero, screen);
            if (previousDpi != IntPtr.Zero) _ = SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
        public uint Compression; public uint ImageSize; public int XPelsPerMeter; public int YPelsPerMeter; public uint ColorsUsed; public uint ColorsImportant;
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, [Out] char[] text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, [Out] char[] text, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapHeader header, uint usage);
}
