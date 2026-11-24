using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests;

// ASP-33: the Pro module's tests compile in only when LEAN_PRO is defined. If the detection in
// windows/Directory.Build.props broke silently in the private tree, the Pro tests would drop out unnoticed.
[TestClass]
public sealed class ModuleMatchesTreeTests
{
    private const int MaxLevels = 12;

    [TestMethod]
    public void TheTestBuildHasTheProModuleExactlyWhereTheModuleFolderExists()
    {
        string root = FindRepoRoot();
        bool folderExists = File.Exists(Path.Combine(root, "windows", "pro", "Ambysto.Lean.Pro", "Ambysto.Lean.Pro.csproj"));

#if LEAN_PRO
        // ASP-33: LEAN_PRO is defined, so the module folder must be there and its assembly must load.
        Assert.IsTrue(folderExists, "LEAN_PRO is defined but windows/pro/Ambysto.Lean.Pro/Ambysto.Lean.Pro.csproj does not exist.");
        Assert.IsNotNull(typeof(Ambysto.Lean.Pro.ProModule).Assembly, "The Ambysto.Lean.Pro assembly did not load.");
#else
        // ASP-33: the public variant may run from the private tree only on purpose (LEAN_PRO_OFF=1).
        if (!string.Equals(Environment.GetEnvironmentVariable("LEAN_PRO_OFF"), "1", StringComparison.Ordinal))
        {
            Assert.IsFalse(folderExists, "windows/pro exists but the tests were built without LEAN_PRO: LeanPro detection is broken, or set LEAN_PRO_OFF=1 on purpose");
        }

        string[] referenced = typeof(ModuleMatchesTreeTests).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(
            referenced.Contains("Ambysto.Lean.Pro", StringComparer.Ordinal),
            "LEAN_PRO is not defined but the test assembly references Ambysto.Lean.Pro.");
#endif
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int level = 0; level < MaxLevels && dir is not null; level++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "windows", "Ambysto.Lean.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("Repo root (a folder holding windows/Ambysto.Lean.slnx) not found within " + MaxLevels + " levels above " + AppContext.BaseDirectory + ".");
        return string.Empty;
    }
}
