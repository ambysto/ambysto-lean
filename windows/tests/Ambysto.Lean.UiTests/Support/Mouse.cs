using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Real mouse input to whatever window is under the cursor: the cursor is moved by SendInput to a screen point (physical
/// pixels, as UI Automation's bounding rectangles are) and the mouse's back button (XButton1), or (ASP-14, only on a
/// disabled drive card) the left button, is pressed and released there; or (ASP-14 row actions) it only rests there. Nothing else is clicked.</summary>
internal static partial class Mouse
{
    private const uint XDown = 0x0080;
    private const uint XUp = 0x0100;
    private const uint XButton1 = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint InputMouse = 0;
    private const uint MouseEventFMove = 0x0001;
    private const uint MouseEventFAbsolute = 0x8000;
    private const uint MouseEventFVirtualDesk = 0x4000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>Moves the cursor to (<paramref name="x"/>, <paramref name="y"/>) and presses XButton1 there.</summary>
    public static void BackButtonAt(double x, double y)
    {
        MoveTo(x, y);
        mouse_event(XDown, 0, 0, XButton1, nuint.Zero);
        Thread.Sleep(50);
        mouse_event(XUp, 0, 0, XButton1, nuint.Zero);
        Thread.Sleep(300);
    }

    /// <summary>Moves the cursor to (<paramref name="x"/>, <paramref name="y"/>) and clicks the left button there.</summary>
    public static void LeftClickAt(double x, double y)
    {
        MoveTo(x, y);
        mouse_event(LeftDown, 0, 0, 0, nuint.Zero);
        Thread.Sleep(50);
        mouse_event(LeftUp, 0, 0, 0, nuint.Zero);
        Thread.Sleep(300);
    }

    /// <summary>Moves the cursor to (<paramref name="x"/>, <paramref name="y"/>) and leaves it there (a hover); nothing is clicked.</summary>
    public static void HoverAt(double x, double y)
    {
        MoveTo(x, y);
        Thread.Sleep(400);
    }

    /// <summary>Moves the pointer by <c>SendInput</c> (MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK), as a real mouse does: WPF and
    /// WinUI see a pointer move and a hover, which <c>SetCursorPos</c> alone does not give them. The point is in physical pixels,
    /// normalised to the virtual desktop (the thread is per-monitor DPI aware, so the metrics are physical too); the cursor is then
    /// read back and must be on the point, within 2 px.</summary>
    private static void MoveTo(double x, double y)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var left = GetSystemMetrics(SmXVirtualScreen);
            var top = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"the virtual desktop has no size ({width} x {height})");
            }

            var input = new Input
            {
                Type = InputMouse,
                Mouse = new MouseInput
                {
                    Dx = Normalise(x, left, width),
                    Dy = Normalise(y, top, height),
                    Flags = MouseEventFMove | MouseEventFAbsolute | MouseEventFVirtualDesk,
                },
            };
            if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SendInput");
            }

            Thread.Sleep(150);
            if (!GetCursorPos(out var at) || Math.Abs(at.X - (int)x) > 2 || Math.Abs(at.Y - (int)y) > 2)
            {
                throw new InvalidOperationException($"the pointer is at ({at.X}, {at.Y}) after a move to ({(int)x}, {(int)y})");
            }
        }
        finally
        {
            if (previous != nint.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }

        Thread.Sleep(150);
    }

    /// <summary>The 0..65535 value that lands on the centre of pixel <paramref name="pixel"/> of a desktop <paramref name="size"/> wide from <paramref name="origin"/>.</summary>
    private static int Normalise(double pixel, int origin, int size) =>
        Math.Clamp((int)Math.Round(((((int)pixel) - origin) + 0.5) * 65536.0 / size), 0, 65535);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll", EntryPoint = "mouse_event")]
    private static partial void mouse_event(uint flags, int dx, int dy, uint data, nuint extraInfo);

    [LibraryImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);
}
