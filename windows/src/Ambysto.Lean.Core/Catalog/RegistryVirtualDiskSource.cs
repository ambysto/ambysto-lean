using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>The real <see cref="IVirtualDiskSource"/> (ASP-20 phase C): the WSL distributions registered for this user, from
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{guid}</c> (<c>DistributionName</c>, <c>BasePath</c>; the key Microsoft
/// Learn's "How to manage WSL disk space" reads to find a distribution's ext4.vhdx). Every key is opened read-only; a key or
/// value that cannot be read, or is not text, is left out.</summary>
internal sealed class RegistryVirtualDiskSource : IVirtualDiskSource
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    /// <inheritdoc />
    public IReadOnlyList<(string Name, string BasePath)> WslDistributions()
    {
        var distributions = new List<(string Name, string BasePath)>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (lxss is null)
            {
                return distributions;
            }

            foreach (var name in lxss.GetSubKeyNames())
            {
                try
                {
                    using var distribution = lxss.OpenSubKey(name);
                    if (distribution?.GetValue("DistributionName") is string { Length: > 0 } display
                        && distribution.GetValue("BasePath") is string { Length: > 0 } basePath)
                    {
                        distributions.Add((display, basePath));
                    }
                }
                catch (Exception exception) when (IsReadFailure(exception))
                {
                    // One key that cannot be read is left out.
                }
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            // The list cannot be read: no distributions.
        }

        return distributions;
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or ArgumentException or ObjectDisposedException;
}
