using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>Scratch per-user uninstall keys for the ASP-16 value tests: <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AmbystoLeanTest-&lt;guid&gt;</c>.
/// Every key it made is removed on <see cref="Dispose"/> (with the deny rule a test set), also when the test failed (use
/// <c>using</c>); the Uninstall key too, when this class made it and it is empty again. No real entry is touched.</summary>
internal sealed class ScratchUninstallKeys : IDisposable
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly bool parentExisted;
    private readonly List<string> made = [];

    public ScratchUninstallKeys()
    {
        using var parent = Registry.CurrentUser.OpenSubKey(UninstallPath);
        parentExisted = parent is not null;
    }

    /// <summary>A new key; <paramref name="values"/> writes its values. Returns the key name.</summary>
    public string Make(Action<RegistryKey>? values = null)
    {
        var name = "AmbystoLeanTest-" + Guid.NewGuid().ToString("D");
        made.Add(name); // before the key exists: a failure while writing it still removes what was made
        using var key = Registry.CurrentUser.CreateSubKey(UninstallPath + @"\" + name);
        values?.Invoke(key);
        return name;
    }

    /// <summary>A key with a display name and one more value (<paramref name="valueName"/> set to <paramref name="value"/> as <paramref name="kind"/>; none when the value is null).</summary>
    public string MakeWith(string displayName, string valueName, object? value, RegistryValueKind kind) => Make(key =>
    {
        key.SetValue("DisplayName", displayName);
        if (value is not null)
        {
            key.SetValue(valueName, value, kind);
        }
    });

    /// <summary>Writes a value into an existing scratch key (this moves its last write time).</summary>
    public static void SetValue(string name, string valueName, string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallPath + @"\" + name, writable: true)
                        ?? throw new InvalidOperationException("the scratch key " + name + " is gone");
        key.SetValue(valueName, value);
    }

    /// <summary>Denies this user reading the values and subkeys of scratch key <paramref name="name"/>.</summary>
    public static void DenyRead(string name) => ChangeDeny(name, add: true);

    private static void ChangeDeny(string name, bool add)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        using var key = Registry.CurrentUser.OpenSubKey(UninstallPath + @"\" + name, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
        if (key is null)
        {
            return;
        }

        var security = key.GetAccessControl(AccessControlSections.Access);
        if (add)
        {
            security.AddAccessRule(new RegistryAccessRule(me, RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny));
        }
        else
        {
            security.RemoveAccessRuleAll(new RegistryAccessRule(me, RegistryRights.QueryValues, AccessControlType.Deny));
        }

        key.SetAccessControl(security);
    }

    public void Dispose()
    {
        foreach (var name in made)
        {
            try
            {
                ChangeDeny(name, add: false);
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallPath + @"\" + name, throwOnMissingSubKey: false);
            }
        }

        if (!parentExisted)
        {
            using (var parent = Registry.CurrentUser.OpenSubKey(UninstallPath))
            {
                if (parent is null || parent.SubKeyCount != 0 || parent.ValueCount != 0)
                {
                    return;
                }
            }

            Registry.CurrentUser.DeleteSubKey(UninstallPath, throwOnMissingSubKey: false);
        }
    }
}
