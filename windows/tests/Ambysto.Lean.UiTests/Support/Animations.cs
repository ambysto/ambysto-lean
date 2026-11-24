using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Windows' "Animation effects" (SPI_SETCLIENTAREAANIMATION, what WinUI's UISettings.AnimationsEnabled reads)
/// turned off for one test: set for the session with SPIF_SENDCHANGE only, never written to the user profile
/// (no SPIF_UPDATEINIFILE), and set back to what it was on dispose. A logoff also restores it.</summary>
internal sealed partial class Animations : IDisposable
{
    private const uint GetClientAreaAnimation = 0x1042;
    private const uint SetClientAreaAnimation = 0x1043;
    private const uint SendChange = 0x0002;
    private readonly bool before;

    private Animations(bool before) => this.before = before;

    /// <summary>Turns animations off for the session; Inconclusive when Windows does not take it.</summary>
    public static Animations Off()
    {
        var before = Read();
        Set(false);
        var animations = new Animations(before);
        if (Read())
        {
            animations.Dispose();
            Assert.Inconclusive("Windows did not turn animation effects off for the session");
        }

        return animations;
    }

    public void Dispose() => Set(before);

    private static bool Read()
    {
        if (!SystemParametersInfoGet(GetClientAreaAnimation, 0, out var on, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SPI_GETCLIENTAREAANIMATION");
        }

        return on;
    }

    private static void Set(bool on)
    {
        if (!SystemParametersInfoSet(SetClientAreaAnimation, 0, on ? 1 : 0, SendChange))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SPI_SETCLIENTAREAANIMATION");
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoGet(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoSet(uint action, uint param, nint value, uint winIni);
}
