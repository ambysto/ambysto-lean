using System;
using System.Runtime.InteropServices;

namespace Ambysto.Lean.App;

/// <summary>The least size of the main window (ASP-22, "as Steady": a fixed minimum in DIP, multiplied by the window's DPI scale so it is the same
/// physical size at 100 %, 125 % and 150 %). The window's default size (MainWindow.xaml.cs) is unchanged. WM_GETMINMAXINFO takes raw pixels
/// (the outer size of the window, frame and title bar included), so the DIP size is scaled here, each time Windows asks, with the window's
/// current DPI: it follows the window to another monitor. The minimum never exceeds the work area of the monitor the window is on (read each
/// time too): on a small screen at a high scale (1280 x 720 at 125 % is 1024 x 576 DIP of work area less the taskbar) a window forced to be
/// larger than the work area would sit under the taskbar.</summary>
public sealed partial class MainWindow
{
    /// <summary>The least width and height of the window, in DIP: the sidebar (220) and one whole drive card (250) fit (docs/spec/01).</summary>
    internal const double MinimumWindowDips = 560;

    private const uint WmGetMinMaxInfo = 0x0024;
    private const int MinTrackSizeOffset = 24; // MINMAXINFO.ptMinTrackSize: after ptReserved, ptMaxSize and ptMaxPosition (3 x 8 bytes)
    private const nuint MinSizeSubclassId = 0x4C45414E;
    private const uint MonitorDefaultToNearest = 2;

    private nint minSizeHandle;

    // Called before the default size is set, so that size is held to the minimum too. A failure leaves the window without a minimum, never without
    // the app.
    private unsafe void InitializeMinimumSize()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (SetWindowSubclass(handle, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint>)&MinimumSizeProc, MinSizeSubclassId, 0))
        {
            minSizeHandle = handle;
            Closed += (_, _) =>
            {
                unsafe
                {
                    RemoveWindowSubclass(minSizeHandle, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint>)&MinimumSizeProc, MinSizeSubclassId);
                }
            };
        }
    }

    [UnmanagedCallersOnly]
    private static nint MinimumSizeProc(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(handle, message, wParam, lParam);
        if (message == WmGetMinMaxInfo && lParam != 0)
        {
            var dpi = GetDpiForWindow(handle);
            var scale = (dpi == 0 ? 96 : dpi) / 96.0;
            var pixels = (int)Math.Round(MinimumWindowDips * scale);
            var width = pixels;
            var height = pixels;
            var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(MonitorFromWindow(handle, MonitorDefaultToNearest), ref monitor))
            {
                width = Math.Min(width, monitor.WorkRight - monitor.WorkLeft);
                height = Math.Min(height, monitor.WorkBottom - monitor.WorkTop);
            }

            Marshal.WriteInt32(lParam, MinTrackSizeOffset, width);
            Marshal.WriteInt32(lParam, MinTrackSizeOffset + 4, height);
        }

        return result;
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window, nint callback, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window, nint callback, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    // MONITORINFO: the monitor's rectangle, then its work area (without the taskbar).
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int WorkLeft;
        public int WorkTop;
        public int WorkRight;
        public int WorkBottom;
        public uint Flags;
    }
}
