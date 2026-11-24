using System.Collections.Generic;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>Where the inventory and "app is gone" read their facts. The real one reads the registry (both hives, both
/// views), the Store packages and the running processes, and changes nothing; tests give their own with decoys.</summary>
public interface IUninstallSource
{
    /// <summary>Every uninstall entry of both hives and both views, system components included.</summary>
    IReadOnlyList<UninstallEntry> ReadEntries();

    /// <summary>The Store packages installed for this user.</summary>
    IReadOnlyList<StorePackage> ReadStorePackages();

    /// <summary>The full image paths of the running processes this user can read.</summary>
    IReadOnlyList<string> ReadProcessImagePaths();
}
