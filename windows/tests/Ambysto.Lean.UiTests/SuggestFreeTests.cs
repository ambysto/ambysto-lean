using System;
using System.Linq;
using System.Windows.Automation;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.UiTests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests;

/// <summary>ASP-17 phase C (intent/2026-10-20-asp-17-suggest.md, "In Free"), Release build, Console. In Free the Suggest button of
/// each of the three views (Temporary files, Large and old here; Duplicates in <c>DuplicatesHelpersTests</c>) is shown, named
/// <see cref="Text.Suggest"/>, disabled, with <see cref="Text.ProLabel"/> beside it (<c>SuggestProLabel</c>); a real mouse click on
/// it does nothing: no tick, no dialog, no Why column, no status text. Nothing is moved.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SuggestFreeTests
{
    // Temporary files: the fixture holds items older and newer than a month, so a click that ticked would be seen.
    [TestMethod]
    [TestCategory("Console")]
    public void TemporaryFilesSuggestIsDisabledWithItsProLabelAndAClickDoesNothing()
    {
        ProApp.RequireReleaseApp();
        using var temp = new SuggestTempFixture("free");
        using var app = AppSession.Start();
        var view = Ui.OpenView(app, "TemporaryFiles", "TempView");
        foreach (var item in temp.Suggested.Concat(temp.NotSuggested))
        {
            Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "TempItem", item.Name) is not null, TimeSpan.FromSeconds(90)),
                "no TempItem for " + item.Name + ": " + Table.Dump(view, "TempItem"));
        }

        var button = SuggestUi.AssertFree(view, "Temporary files");
        var status = SuggestUi.Status(app);
        var windows = SuggestUi.Windows(app);

        SuggestUi.MouseClick(button);
        System.Threading.Thread.Sleep(Ui.NeverWatch);

        Assert.IsTrue(Table.Controls(view, "TempItem").All(b => Table.State(b) == ToggleState.Off), "a click on the disabled Suggest ticked a row: " + Table.Dump(view, "TempItem"));
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a click on the disabled Suggest showed a Why column");
        Assert.IsFalse(Table.Controls(view, "TempItem").Any(b => Table.RowName(b).Contains(SuggestUi.TempWhyPrefix(), StringComparison.Ordinal)), "a Why text is in a row");
        Assert.AreEqual(windows, SuggestUi.Windows(app), "a click on the disabled Suggest opened or changed a window");
        Assert.AreEqual(status, SuggestUi.Status(app), "a click on the disabled Suggest changed the status line");
        Assert.IsFalse(SuggestUi.Button(view).Current.IsEnabled, "Suggest got enabled by a click");
        Assert.IsFalse(AppSession.FindAll(view, "MoveTemp").Any(m => m.Current.IsEnabled), "Move got enabled: something was ticked");
    }

    // Large and old: a small scan with an old installer in Downloads and an old 150 MB file; the list shows the second.
    [TestMethod]
    [TestCategory("Console")]
    public void LargeAndOldSuggestIsDisabledWithItsProLabelAndAClickDoesNothing()
    {
        ProApp.RequireReleaseApp();
        using var fixture = new DownloadsFixture("free");
        fixture.File("installer.zip", 200 * ByteUnits.KB, 200);
        fixture.File("medium.bin", 150 * ByteUnits.MB, 400);
        using var app = AppSession.Start("--scan", fixture.Path);
        Ui.WaitForScan(app);
        var view = Ui.OpenView(app, "LargeOld", "LargeOldView");
        Assert.IsTrue(AppSession.WaitUntil(() => Table.ControlWith(view, "LargeOldSelect", "medium.bin") is not null, Ui.ElementTimeout), "no row for medium.bin: " + Table.Dump(view, "LargeOldSelect"));

        var button = SuggestUi.AssertFree(view, "Large and old");
        var rows = Table.Dump(view, "LargeOldSelect");
        var status = SuggestUi.Status(app);
        var windows = SuggestUi.Windows(app);

        SuggestUi.MouseClick(button);
        System.Threading.Thread.Sleep(Ui.NeverWatch);

        Assert.AreEqual(rows, Table.Dump(view, "LargeOldSelect"), "a click on the disabled Suggest changed the list (it must not replace it with the suggested files)");
        Assert.IsTrue(Table.Controls(view, "LargeOldSelect").All(b => Table.State(b) == ToggleState.Off), "a click on the disabled Suggest ticked a row");
        Assert.IsNull(Table.ControlWith(view, "LargeOldSelect", "installer.zip"), "a click on the disabled Suggest listed the installer");
        Assert.IsFalse(SuggestUi.HasWhyColumn(view), "a click on the disabled Suggest showed a Why column");
        Assert.IsNull(AppSession.TryFind(view, "ClearSuggestion"), "a click on the disabled Suggest showed ClearSuggestion");
        Assert.AreEqual(windows, SuggestUi.Windows(app), "a click on the disabled Suggest opened or changed a window");
        Assert.AreEqual(status, SuggestUi.Status(app), "a click on the disabled Suggest changed the status line");
        Assert.IsTrue(AppSession.FindAll(view, "MinimumSize").All(f => f.Current.IsEnabled), "a click on the disabled Suggest disabled a filter");
    }
}
