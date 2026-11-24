using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>ASP-20 phase A (plan "Tests", second bullet): the Command rows read with the real finder and the real runner, on
/// the machine's own PATH. Read-only: only the tools' location queries (<c>dotnet nuget locals plugins-cache --list</c> and
/// the like) run; no clean command. Session 0.</summary>
[TestClass]
public sealed class CommandRowsRealTests
{
    [TestMethod]
    public async Task TheRealReadReachesALocationOrLocationUnknownForTheNugetPluginsRowAndNeverThrows()
    {
        var rows = await CommandRows.ReadAsync(new FakeCommandEnvironment(), new PathProgramFinder(), new ProcessCommandRunner(), () => [], CancellationToken.None);
        Assert.AreEqual(5, rows.Count);
        var plugins = rows.Single(r => r.Entry.Id == "nuget-plugins");
        if (plugins.ProgramPath is null)
        {
            Assert.Inconclusive("dotnet.exe is not on this machine's PATH");
        }

        Assert.IsTrue(plugins.Location is not null || plugins.State == CommandRowState.LocationUnknown,
            $"nuget-plugins: location {plugins.Location ?? "(null)"}, state {plugins.State}");
        foreach (var row in rows)
        {
            Assert.AreNotEqual(CommandRowState.ToolRunning, row.State, row.Entry.Id + ": the process list given was empty");
            if (row.State is CommandRowState.Ready or CommandRowState.Empty)
            {
                Assert.IsTrue(Directory.Exists(row.Location), row.Entry.Id + ": " + row.Location);
            }
        }
    }
}
