using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Windows High Contrast (the built-in "High Contrast Black") on for the length of one test, and the setting put back on
/// dispose. It is changed with SPI_SETHIGHCONTRAST and SPIF_SENDCHANGE only (no SPIF_UPDATEINIFILE), so nothing is written to the
/// user's registry (the reason AccessibilityTests gave for never switching it): the change lasts for the session and is undone
/// here, in <c>Dispose</c>, from the state read before. Inconclusive when High Contrast is already on (then the test would not
/// be what it says, and the user's own setting is left alone). Use <c>using</c>, and start the app after the scope, so that it
/// starts under the system colours. Also reads the system colours.</summary>
internal sealed partial class HighContrastScope : IDisposable
{
    private const uint GetHighContrast = 0x0042;
    private const uint SetHighContrast = 0x0043;
    private const uint SendChange = 0x0002;
    private const uint On = 0x00000001;
    private const uint Available = 0x00000002;

    private readonly nint schemeBuffer = Marshal.AllocHGlobal(520 * 2);
    private readonly nint blackScheme = Marshal.StringToHGlobalUni("High Contrast Black");
    private HighContrastInfo before;
    private bool changed;

    public HighContrastScope()
    {
        before = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>(), Scheme = schemeBuffer };
        Assert.IsTrue(SystemParametersInfoW(GetHighContrast, before.Size, ref before, 0), "SPI_GETHIGHCONTRAST failed");
        if ((before.Flags & On) != 0)
        {
            Dispose();
            Assert.Inconclusive("Windows High Contrast is already on: the test would not switch it, and the user's own setting is left as it is");
        }

        var on = new HighContrastInfo { Size = before.Size, Flags = On | Available, Scheme = blackScheme };
        Assert.IsTrue(SystemParametersInfoW(SetHighContrast, on.Size, ref on, SendChange), "SPI_SETHIGHCONTRAST (on) failed");
        changed = true;
        try
        {
            Assert.IsTrue(DisplaySettings.HighContrastOn() || Wait(true), "High Contrast did not switch on");
            Thread.Sleep(2500); // the system repaints with the new colours
        }
        catch
        {
            Dispose(); // a failed constructor is never disposed by "using": switch High Contrast off here, or it stays on for the session
            throw;
        }
    }

    private static bool Wait(bool on)
    {
        for (var i = 0; i < 50; i++)
        {
            if (DisplaySettings.HighContrastOn() == on)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>A system colour (GetSysColor index) as an <see cref="Rgb"/>: COLOR_WINDOW 5, COLOR_WINDOWTEXT 8, COLOR_BTNFACE 15,
    /// COLOR_MENU 4, COLOR_DESKTOP 1, COLOR_HIGHLIGHT 13.</summary>
    public static Rgb System(int index)
    {
        var value = GetSysColor(index);
        return new Rgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
    }

    public void Dispose()
    {
        try
        {
            if (changed)
            {
                var clock = Stopwatch.StartNew();
                void Log(string what) => Console.WriteLine($"HighContrastScope [{clock.Elapsed.TotalSeconds:F1} s]: {what}");
                var restore = before;
                restore.Scheme = schemeBuffer;
                var set = SystemParametersInfoW(SetHighContrast, restore.Size, ref restore, SendChange);
                Log($"restore flags {restore.Flags}: {set}, error {Marshal.GetLastPInvokeError()}");
                // Gates 2026-10-22 to 10-23: the switch off did not hold (High Contrast stayed on for the rest of the run, the registry at
                // hcblack.theme), although every call returned True with error 0 (asking for the off again changed nothing), while the same
                // call made later in the console session did. The system may think it is already off while the theme change of the switch on
                // is still applying. So when the off does not hold after a wait, the on is sent again, given about 3 s, and the off after it.
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    if (Wait(false))
                    {
                        Log("High Contrast is off");
                        break;
                    }

                    Log($"still on after waiting (attempt {attempt}): sending ON");
                    var on = new HighContrastInfo { Size = restore.Size, Flags = On | Available, Scheme = blackScheme };
                    set = SystemParametersInfoW(SetHighContrast, on.Size, ref on, SendChange);
                    Log($"on again: {set}, error {Marshal.GetLastPInvokeError()}");
                    Thread.Sleep(3000);
                    var off = new HighContrastInfo { Size = restore.Size, Flags = 0, Scheme = schemeBuffer };
                    set = SystemParametersInfoW(SetHighContrast, off.Size, ref off, SendChange);
                    Log($"off after the on: {set}, error {Marshal.GetLastPInvokeError()}");
                }

                Log(Wait(false) ? "final check: off" : "final check: still on");
                Assert.IsFalse(DisplaySettings.HighContrastOn(), "High Contrast is still on after the test: switch it off (Settings > Accessibility > Contrast themes)");

                changed = false;
                Thread.Sleep(1500);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(schemeBuffer);
            Marshal.FreeHGlobal(blackScheme);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint Scheme;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint parameter, ref HighContrastInfo value, uint winIni);

    [LibraryImport("user32.dll")]
    private static partial uint GetSysColor(int index);
}
