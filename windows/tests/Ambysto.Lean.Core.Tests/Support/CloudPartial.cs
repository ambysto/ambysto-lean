using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>Test-only range dehydration, using the same provider connection/protected-handle APIs as
/// AmbyCloud's Program.cs DehydrateOne. AmbyCloud's FULL policy makes a prefix read hydrate the whole file;
/// removing just its tail produces a real partial placeholder without modifying the machine's registered providers.</summary>
internal static class CloudPartial
{
    public static void KeepPrefix(CloudRoot cloud, string relative, long prefixBytes)
    {
        Assert.IsTrue(prefixBytes > 0 && prefixBytes % 65536 == 0, "keep a nonempty, aligned prefix");
        // Hydrate only this test's file. This intentional fetch is excluded from the refusal test's baseline below.
        _ = File.ReadAllBytes(cloud.Combine(relative));
        cloud.StopServe();
        try
        {
            var callbacks = new[] { new CallbackRegistration { Type = uint.MaxValue, Callback = IntPtr.Zero } };
            Marshal.ThrowExceptionForHR(CfConnectSyncRoot(cloud.Root, callbacks, IntPtr.Zero, 0, out var connection));
            try
            {
                Marshal.ThrowExceptionForHR(CfOpenFileWithOplock(cloud.Combine(relative), 0x2, out var handle));
                try
                {
                    Marshal.ThrowExceptionForHR(CfDehydratePlaceholder(CfGetWin32HandleFromProtectedHandle(handle), prefixBytes, -1, 0, IntPtr.Zero));
                }
                finally { CfCloseHandle(handle); }
            }
            finally { Marshal.ThrowExceptionForHR(CfDisconnectSyncRoot(connection)); }
        }
        finally { cloud.Serve(); }
        var status = cloud.Status(relative);
        Assert.IsTrue(status.IsPlaceholder && status.RecallOnDataAccess && status.OnDisk > 0 && status.OnDisk < status.Size,
            $"fixture must be partially hydrated: placeholder={status.IsPlaceholder}, recall={status.RecallOnDataAccess}, onDisk={status.OnDisk}, size={status.Size}");
        cloud.MarkFetchBaseline();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackRegistration { public uint Type; public IntPtr Callback; }
    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int CfConnectSyncRoot(string root, CallbackRegistration[] callbacks, IntPtr context, uint flags, out long connection);
    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern int CfDisconnectSyncRoot(long connection);
    [DllImport("cldapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int CfOpenFileWithOplock(string path, uint flags, out IntPtr handle);
    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr handle);
    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern void CfCloseHandle(IntPtr handle);
    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern int CfDehydratePlaceholder(IntPtr handle, long offset, long length, uint flags, IntPtr overlapped);
}
