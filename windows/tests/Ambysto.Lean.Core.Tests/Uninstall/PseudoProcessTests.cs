using System;
using System.Diagnostics;
using System.Linq;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Uninstall;

/// <summary>Lean Windows v1 release blocker (hard review 2026-10-18, phase A, MINOR 3), probed 2026-10-22: with WSL 2
/// running, the hypervisor's minimal process vmmemWSL has no image that the system query or a limited handle can name, so
/// the process read threw and "app is gone" never fired. A process neither read can name is skipped only when it is a
/// kernel pseudo-process or one of the hypervisor's memory processes (vmmem, vmmemWSL); any other stays an error.</summary>
[TestClass]
public sealed class PseudoProcessTests
{
    [TestMethod]
    [DataRow(4, "System")]
    [DataRow(100, "System")]
    [DataRow(100, "Registry")]
    [DataRow(100, "Secure System")]
    [DataRow(100, "Memory Compression")]
    [DataRow(100, "MemCompression")]
    [DataRow(100, "vmmem")]
    [DataRow(100, "vmmemWSL")]
    public void KernelAndHypervisorMemoryProcessesArePseudo(int id, string name) =>
        Assert.IsTrue(RegistryUninstallSource.IsPseudoProcess(id, name), $"{name} ({id}) is not treated as a pseudo-process");

    [TestMethod]
    [DataRow(100, "")]
    [DataRow(100, "vmmemX")]
    [DataRow(100, "vmmem.exe")]
    [DataRow(100, "myvmmem")]
    [DataRow(100, "vmwp")]
    [DataRow(100, "wslservice")]
    [DataRow(100, "chrome")]
    public void AnyOtherNameIsNotPseudo(int id, string name) =>
        Assert.IsFalse(RegistryUninstallSource.IsPseudoProcess(id, name), $"{name} ({id}) is treated as a pseudo-process; an unreadable process of that name would be left out");

    // On a machine where a VM's memory process runs (the WSL snapshot layer: start a distribution first), the real read
    // of every process's image must not throw. Inconclusive where none runs.
    [TestMethod]
    public void TheProcessReadDoesNotThrowWhileAVirtualMachineMemoryProcessRuns()
    {
        var running = Process.GetProcesses().Where(p => p.ProcessName is "vmmem" or "vmmemWSL").Select(p => $"{p.ProcessName} ({p.Id})").ToList();
        if (running.Count == 0)
        {
            Assert.Inconclusive("no vmmem or vmmemWSL process runs on this machine (start a WSL 2 distribution to run this test)");
        }

        var paths = new RegistryUninstallSource().ReadProcessImagePaths();
        Assert.IsTrue(paths.Count > 0, "the read named no image at all while " + string.Join(", ", running) + " ran");
    }
}
