using System;
using Ambysto.Kit.Safety;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Trash;

namespace Ambysto.Lean.Core.Catalog;

/// <summary>Whether Lean may start a clean or location command here (hard review round 1, item 1): never as administrator,
/// as SYSTEM or outside the desktop session, as a move or a Put back would not either (docs/spec/02).</summary>
public interface ICommandEnvironment
{
    /// <summary>The reason no command may start here, or null when one may.</summary>
    string? Refusal();
}

/// <summary>The Kit's own environment check, read through the restore check that Put back uses first (docs/spec/05). The
/// path asked about is the user's <c>%LOCALAPPDATA%</c>, which exists, so the one answer that proves the environment was
/// checked and passed is "a destination already exists" (the check after the environment's). Every other answer refuses,
/// and so does an exception from the gate: it fails closed (hard review round 2, item 1).</summary>
public sealed class KitCommandEnvironment : ICommandEnvironment
{
    private readonly IItemGate gate;

    /// <summary>The check over <paramref name="gate"/>.</summary>
    public KitCommandEnvironment(IItemGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        this.gate = gate;
    }

    /// <inheritdoc/>
    public string? Refusal()
    {
        GateResult result;
        try
        {
            result = gate.CheckRestoreDestination(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
#pragma warning disable CA1031 // any failure of the check is a refusal, never a pass
        catch (Exception)
#pragma warning restore CA1031
        {
            return Text.CleanEnvironmentUnknown;
        }

        return result.Reason switch
        {
            RefusalReason.RestoreDestinationExists => null,
            RefusalReason.Elevated => Text.CleanNotAsAdministrator,
            RefusalReason.NotInteractive => Text.CleanNotOutsideDesktop,
            _ => Text.CleanEnvironmentUnknown,
        };
    }
}
