using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Leftovers offered without uninstalling a real app (the way ReturnToOverviewTests' app-gone watch test does it): the
/// test's own per-user entry under HKCU's Uninstall key (<c>AmbystoLeanUiTest-&lt;guid&gt;</c>, display name
/// <c>LeanUiGoneApp-&lt;id&gt;</c>, install location <c>%LOCALAPPDATA%\Programs\LeanUiGoneApp-&lt;id&gt;</c>) and a leftover
/// folder <c>%APPDATA%\LeanUiGoneApp-&lt;id&gt;</c>, each holding one 64 KB file. <see cref="OfferLeftovers"/> presses
/// UninstallInSettings on that row (the watch starts and Windows Settings opens; the test closes it), goes back to the
/// map, removes the entry and waits for <c>Text.AppGone</c>, then opens Uninstall again with both folders offered as
/// <c>LeftoverItem</c>. MoveLeftovers is never pressed. On dispose the key and both folders are removed and any Settings
/// window it opened is closed.</summary>
internal sealed class GoneApp : IDisposable
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const long FileBytes = 64 * 1024;

    private readonly List<AutomationElement> settingsBefore;

    public GoneApp()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        Name = "LeanUiGoneApp-" + id;
        KeyPath = UninstallPath + @"\AmbystoLeanUiTest-" + Guid.NewGuid().ToString("D");
        Leftover = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
        Install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
        settingsBefore = SettingsWindows();
        using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath))
        {
            Assert.IsNull(existing, "precondition: the scratch key exists already");
        }

        Assert.IsFalse(Directory.Exists(Leftover) || Directory.Exists(Install), "precondition: a scratch folder exists already");
        Directory.CreateDirectory(Leftover);
        File.WriteAllBytes(Path.Combine(Leftover, "settings.dat"), new byte[FileBytes]);
        Directory.CreateDirectory(Install);
        File.WriteAllBytes(Path.Combine(Install, "app.dat"), new byte[FileBytes]);
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("DisplayName", Name);
        key.SetValue("Publisher", "LeanUiTestVendor-" + id);
        key.SetValue("DisplayVersion", "1.0");
        key.SetValue("InstallLocation", Install);
        key.SetValue("UninstallString", "\"" + Path.Combine(Install, "uninstall.exe") + "\"");
    }

    public string Name { get; }

    public string KeyPath { get; }

    /// <summary>The leftover folder in %APPDATA%.</summary>
    public string Leftover { get; }

    /// <summary>The install location, also a leftover once the entry is gone.</summary>
    public string Install { get; }

    /// <summary>Drives the watch until Uninstall, opened again, offers both folders; returns the UninstallView.</summary>
    public AutomationElement OfferLeftovers(AppSession app)
    {
        var view = Ui.OpenView(app, "Uninstall", "UninstallView");
        var list = AppSession.Find(view, "UninstallList", Ui.ElementTimeout);
        var row = AppSession.WaitFor(() => RowNamed(list, Name), Ui.ElementTimeout);
        Assert.IsNotNull(row, $"precondition: no UninstallRow named {Name}");
        Ui.SelectionItemOf(row).Select();
        var offer = AppSession.Find(view, "UninstallInSettings", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => offer.Current.IsEnabled, TimeSpan.FromSeconds(3)), "precondition: UninstallInSettings is disabled on the test's row");
        Ui.Click(offer);
        var instruction = AppSession.Find(view, "UninstallInstruction", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => instruction.Current.Name == Text.UninstallInSettings(Name), Ui.ElementTimeout),
            $"precondition: the watch did not start: UninstallInstruction \"{instruction.Current.Name}\"");
        AppSession.WaitUntil(() => SettingsWindows().Count > settingsBefore.Count, TimeSpan.FromSeconds(10));
        CloseNewSettingsWindows();

        Ui.BackToOverview(app, "UninstallView");
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath);
        var status = AppSession.Find(app.Window(Ui.WindowTimeout), "Status", Ui.ElementTimeout);
        Assert.IsTrue(AppSession.WaitUntil(() => status.Current.Name == Text.AppGone(Name), TimeSpan.FromSeconds(60)),
            $"precondition: the status never said \"{Text.AppGone(Name)}\" within 60 s of removing the entry (status \"{status.Current.Name}\")");

        view = Ui.OpenView(app, "Uninstall", "UninstallView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LeftoverItem", Leftover) is not null && Table.ControlWith(view, "LeftoverItem", Install) is not null, Ui.ElementTimeout),
            $"precondition: Uninstall opened again does not offer both leftovers ({Leftover}, {Install}): {Table.Dump(view, "LeftoverItem")}");
        DismissResultIfShown(app);
        return view;
    }

    /// <summary>Legacy leftover/table tests deliberately dismiss ASP-28's informational result before driving the
    /// background view. This is fixture-specific, never a global Ui auto-dismiss; result acceptance tests inspect it.
    /// Leaving the view may have invalidated the notification, so its absence is permitted here.</summary>
    public static void DismissResultIfShown(AppSession app)
    {
        var result = AppSession.WaitFor(() => app.FindInAnyWindow("UninstallResult") is { } shown && !shown.Current.IsOffscreen ? shown : null,
            TimeSpan.FromSeconds(2));
        if (result is null) return;
        Ui.Click(AppSession.Find(result, "UninstallResultDetails", Ui.ElementTimeout));
        Assert.IsTrue(AppSession.WaitUntil(() => app.FindInAnyWindow("UninstallResult") is not { } remaining || remaining.Current.IsOffscreen,
            Ui.ElementTimeout), "legacy test could not dismiss the informational uninstall result");
    }

    /// <summary>Windows Settings' top-level windows now (the hand-off opens Settings > Apps).</summary>
    private static List<AutomationElement> SettingsWindows() =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "ApplicationFrameWindow"))
            .Cast<AutomationElement>().Where(w =>
            {
                try
                {
                    return w.Current.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase);
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }).ToList();

    /// <summary>Closes every Windows Settings window that was not open when this entry was made.</summary>
    private void CloseNewSettingsWindows()
    {
        var known = settingsBefore.Select(Ui.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var window in SettingsWindows().Where(w => !known.Contains(Ui.Key(w))))
        {
            try
            {
                if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                {
                    ((WindowPattern)pattern).Close();
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>The UninstallRow named <paramref name="name"/> in <paramref name="list"/>, realised if the list virtualises it.</summary>
    private static AutomationElement? RowNamed(AutomationElement list, string name)
    {
        var row = AppSession.FindAll(list, "UninstallRow").FirstOrDefault(r => r.Current.Name == name);
        if (row is not null || !list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var container))
        {
            return row;
        }

        try
        {
            var item = ((ItemContainerPattern)container).FindItemByProperty(null, AutomationElement.NameProperty, name);
            if (item is not null && item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized))
            {
                ((VirtualizedItemPattern)virtualized).Realize();
            }

            if (item is not null && item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
            {
                ((ScrollItemPattern)scroll).ScrollIntoView();
            }

            return item;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        CloseNewSettingsWindows();
        if (Registry.CurrentUser.OpenSubKey(KeyPath) is { } left)
        {
            left.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath);
        }

        foreach (var folder in new[] { Leftover, Install })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
