using System;
using Ambysto.Lean.Core.Copy;

namespace Ambysto.Lean.Core.Uninstall;

/// <summary>Lean never runs an uninstaller (owner decision 2026-10-16): "Uninstall…" opens Settings > Apps and names the
/// entry to remove. The app opens <see cref="AppsPage"/> through its one launcher module.</summary>
public static class SettingsHandOff
{
    /// <summary><c>ms-settings:appsfeatures</c>, the list page (there is no deep link to one desktop app).</summary>
    public static readonly Uri AppsPage = new("ms-settings:appsfeatures");

    /// <summary>What Lean tells the user to remove, by its name as Settings lists it.</summary>
    public static string Instruction(InstalledApp app) => Text.UninstallInSettings(app.DisplayName);
}
