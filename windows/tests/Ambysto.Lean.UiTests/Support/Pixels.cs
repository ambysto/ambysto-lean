using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>An sRGB colour without alpha, as the screen shows it.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>"#rrggbb".</summary>
    public static Rgb Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var value = int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>True when every channel is within <paramref name="tolerance"/> of <paramref name="other"/>'s.</summary>
    public bool Near(Rgb other, int tolerance) =>
        Math.Abs(R - other.R) <= tolerance && Math.Abs(G - other.G) <= tolerance && Math.Abs(B - other.B) <= tolerance;

    public int Brightest => Math.Max(R, Math.Max(G, B));

    public override string ToString() => "#" + R.ToString("x2", CultureInfo.InvariantCulture) + G.ToString("x2", CultureInfo.InvariantCulture) + B.ToString("x2", CultureInfo.InvariantCulture);
}

/// <summary>The theme contract's colours (intent/2026-10-18-asp-12-brand-theme.md), and the tolerances the theme tests
/// allow. A solid fill is drawn exactly, so area samples use a tight tolerance and ask a share of the area to match (text
/// and anti-aliased edges are the rest); ground and panel differ by only 5 levels per channel, so their tolerance is 2.</summary>
internal static class Brand
{
    public static readonly Rgb Ground = Rgb.Parse("#0b0b0c");
    public static readonly Rgb Panel = Rgb.Parse("#101011");
    public static readonly Rgb Sidebar = Rgb.Parse("#161618");
    public static readonly Rgb Ink = Rgb.Parse("#e8e8e6");
    public static readonly Rgb Mute = Rgb.Parse("#8a8a8c");
    public static readonly Rgb Rule = Rgb.Parse("#38383d");
    public static readonly Rgb Dim = Rgb.Parse("#4a4a50");
    public static readonly Rgb Divider = Rgb.Parse("#68686e");
    public static readonly Rgb Coral = Rgb.Parse("#f99a8f");

    /// <summary>For the dark neutrals (ground, panel, rule): below half their 5-level distance.</summary>
    public const int NeutralTolerance = 2;

    /// <summary>For ink, tones and borders.</summary>
    public const int Tolerance = 6;
}

/// <summary>A copy of a rectangle of the screen (physical pixels, the coordinates of UI Automation's bounding rectangles),
/// taken with BitBlt from the screen in the console session: what the user sees, whatever window draws it.</summary>
internal sealed partial class ScreenCapture
{
    private readonly int[] pixels;

    private ScreenCapture(int left, int top, int width, int height, int[] pixels)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        this.pixels = pixels;
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    /// <summary>Copies the screen rectangle <paramref name="area"/> (clipped to the virtual screen).</summary>
    public static ScreenCapture Of(Int32Rect area)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var screenLeft = GetSystemMetrics(SmXVirtualScreen);
            var screenTop = GetSystemMetrics(SmYVirtualScreen);
            var left = Math.Max(area.X, screenLeft);
            var top = Math.Max(area.Y, screenTop);
            var right = Math.Min(area.X + area.Width, screenLeft + GetSystemMetrics(SmCxVirtualScreen));
            var bottom = Math.Min(area.Y + area.Height, screenTop + GetSystemMetrics(SmCyVirtualScreen));
            var width = Math.Max(1, right - left);
            var height = Math.Max(1, bottom - top);
            var screen = GetDC(nint.Zero);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(memory, bitmap);
            try
            {
                if (!BitBlt(memory, 0, 0, width, height, screen, left, top, SrcCopy | CaptureBlt))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "BitBlt");
                }

                SelectObject(memory, old);
                var buffer = new int[width * height];
                var header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                };
                unsafe
                {
                    fixed (int* p = buffer)
                    {
                        if (GetDIBits(memory, bitmap, 0, (uint)height, (nint)p, ref header, 0) == 0)
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDIBits");
                        }
                    }
                }

                return new ScreenCapture(left, top, width, height, buffer);
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(memory);
                _ = ReleaseDC(nint.Zero, screen);
            }
        }
        finally
        {
            if (previous != nint.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    public bool Contains(int x, int y) => x >= Left && y >= Top && x < Right && y < Bottom;

    /// <summary>The pixel at screen point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Rgb At(int x, int y)
    {
        if (!Contains(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the capture {Left},{Top} {Width}x{Height}");
        }

        var value = pixels[((y - Top) * Width) + (x - Left)];
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>Every pixel of the screen rectangle (<paramref name="left"/>, <paramref name="top"/>) to (<paramref name="right"/>,
    /// <paramref name="bottom"/>), right and bottom excluded, clipped to the capture.</summary>
    public List<Rgb> Area(int left, int top, int right, int bottom)
    {
        var list = new List<Rgb>();
        for (var y = Math.Max(top, Top); y < Math.Min(bottom, Bottom); y++)
        {
            for (var x = Math.Max(left, Left); x < Math.Min(right, Right); x++)
            {
                list.Add(At(x, y));
            }
        }

        return list;
    }

    /// <summary>The share of <paramref name="samples"/> within <paramref name="tolerance"/> of <paramref name="colour"/>.</summary>
    public static double Share(IReadOnlyCollection<Rgb> samples, Rgb colour, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return samples.Count == 0 ? 0 : samples.Count(s => s.Near(colour, tolerance)) / (double)samples.Count;
    }

    /// <summary>The most frequent colours of <paramref name="samples"/>, for a failure message.</summary>
    public static string Commonest(IReadOnlyCollection<Rgb> samples, int count = 4)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return "(no pixels)";
        }

        return string.Join(", ", samples.GroupBy(s => s).OrderByDescending(g => g.Count()).Take(count)
            .Select(g => g.Key + " " + (100.0 * g.Count() / samples.Count).ToString("F0", CultureInfo.InvariantCulture) + " %"));
    }

    /// <summary>Saves the capture as a PNG under %TEMP%\LeanUiTheme and returns its path (for looking at a failure).</summary>
    public string Save(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LeanUiTheme");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, pixels, Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private static readonly nint PerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Colors0;
        public uint Colors1;
        public uint Colors2;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "BitBlt", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits", SetLastError = true)]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, nint bits, ref BitmapInfoHeader info, uint usage);
}

/// <summary>The app's main window on screen: brought to the front, the cursor moved off it (no hover, no tooltip), and its
/// visible frame (DWM's extended frame bounds, without the invisible resize borders) and client area.</summary>
internal static partial class WindowOnScreen
{
    /// <summary>Brings <paramref name="window"/> to the front, parks the cursor on the window's empty left padding next to
    /// <paramref name="parkBeside"/> (ground that reacts to nothing), and waits for transitions and tooltips to settle.</summary>
    public static void Settle(AutomationElementHandle window, System.Windows.Rect parkBeside)
    {
        SetForegroundWindow(window.Handle);
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            SetCursorPos((int)parkBeside.Left - 8, (int)(parkBeside.Top + (parkBeside.Height / 2)));
        }
        finally
        {
            if (previous != nint.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }

        System.Threading.Thread.Sleep(1500);
    }

    /// <summary>The visible frame of the window, title bar included.</summary>
    public static Int32Rect Frame(AutomationElementHandle window)
    {
        if (DwmGetWindowAttribute(window.Handle, DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<NativeRect>()) != 0)
        {
            GetWindowRect(window.Handle, out rect);
        }

        return new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>The client area of the window in screen coordinates (below the title bar).</summary>
    public static Int32Rect Client(AutomationElementHandle window)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            GetClientRect(window.Handle, out var rect);
            var origin = new NativePoint();
            ClientToScreen(window.Handle, ref origin);
            return new Int32Rect(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        finally
        {
            if (previous != nint.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    /// <summary>Physical pixels per device-independent pixel for the window (1 at 96 DPI).</summary>
    public static double Scale(AutomationElementHandle window) => GetDpiForWindow(window.Handle) / 96.0;

    private const int DwmwaExtendedFrameBounds = 9;
    private static readonly nint PerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);
}

/// <summary>A top-level window's native handle.</summary>
internal readonly record struct AutomationElementHandle(nint Handle);

/// <summary>Windows display settings the theme tests read (never write).</summary>
internal static partial class DisplaySettings
{
    /// <summary>True when Windows High Contrast is on (SPI_GETHIGHCONTRAST, HCF_HIGHCONTRASTON).</summary>
    public static bool HighContrastOn()
    {
        var info = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        return SystemParametersInfoW(GetHighContrast, info.Size, ref info, 0) && (info.Flags & HighContrastOnFlag) != 0;
    }

    /// <summary>"light" or "dark": the Windows app mode (HKCU …\Themes\Personalize AppsUseLightTheme), read only.</summary>
    public static string AppMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? "dark" : "light";
    }

    private const uint GetHighContrast = 0x0042;
    private const uint HighContrastOnFlag = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint parameter, ref HighContrast value, uint winIni);
}
