using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Find;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Ambysto.Lean.App;

/// <summary>The helpers of the Duplicates view (ASP-15, intent/2026-10-18-asp-15-duplicates-helpers.md): the type tabs, Select by rule and
/// the picture preview. They list and mark; they move nothing (a move starts only from Move, through the confirmation). Which copy a
/// rule keeps is the Core's (<see cref="DuplicateRules"/>), what may be ticked is <see cref="DuplicateSelection"/>'s, and a preview is read
/// only by <see cref="PicturePreview"/> (the cloud-file guard) and decoded here from those bytes, never through the shell's thumbnail
/// provider.</summary>
public sealed partial class MainWindow
{
    /// <summary>The widest a previewed picture is decoded and drawn, in pixels (the intent: at most 320 px wide).</summary>
    private const int PreviewWidth = 320;

    // A rule that would tick more than this many copies asks first (owner 2026-10-19).
    private const int RuleAsksAbove = 100;

    // The program files the question counts (owner 2026-10-19): what builds and package caches fill a duplicates list with.
    private static readonly HashSet<string> ProgramFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".sys", ".ocx", ".drv", ".cpl", ".pyd", ".so", ".node", ".jar", ".winmd",
    };

    // The tab shown: 0 is All files, 1 to 7 the groups of GroupOrder. The tabs only filter what is shown: every ticked copy of every tab
    // is still moved by Move.
    private int duplicateTab;
    private bool duplicateTabsBuilding;
    private bool previewOpening;
    private readonly MenuFlyout ruleMenu = new();

    private RowActions DuplicateRowActions => new(CreateDuplicateActionButtons, (_, _) => { });

    // Every visible string, from Copy.Text.
    private void InitializeDuplicateHelpers()
    {
        SelectByRuleButton.Content = Text.SelectByRule;
        AutomationProperties.SetName(SelectByRuleButton, Text.SelectByRule);
        AddRuleItem(Text.RuleNewest, "RuleNewest", () => _ = ApplyRuleAsync(KeepRule.NewestModified, null));
        AddRuleItem(Text.RuleOldest, "RuleOldest", () => _ = ApplyRuleAsync(KeepRule.OldestModified, null));
        AddRuleItem(Text.RuleShortest, "RuleShortest", () => _ = ApplyRuleAsync(KeepRule.ShortestPath, null));
        AddRuleItem(Text.RuleInFolder, "RuleInFolder", () => _ = ApplyFolderRuleAsync());
        ruleMenu.Items.Add(new MenuSeparatorItem());
        AddRuleItem(Text.ClearSelection, "ClearSelection", ClearDuplicateSelection);
        SelectByRuleButton.Flyout = ruleMenu;
    }

    private void AddRuleItem(string label, string automationId, Action action)
    {
        var item = new MenuFlyoutItem { Text = label };
        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, label);
        item.Click += (_, _) => action();
        ruleMenu.Items.Add(item);
    }

    // ---- the type tabs ------------------------------------------------------------------------------------------------

    // The groups the chosen tab shows.
    private List<DuplicateGroupModel> ShownDuplicates() =>
        duplicateTab == 0 ? [.. duplicateModels] : duplicateModels.Where(m => m.Type == GroupOrder[duplicateTab - 1]).ToList(); // always a new list: the repeater only rebuilds for another source

    // One tab per type, all seven always shown, an empty one with "(0)"; the chosen one stays chosen across a rebuild.
    private void BuildDuplicateTabs()
    {
        duplicateTabsBuilding = true;
        try
        {
            DuplicateTypeTabs.Children.Clear();
            DuplicateTypes.Visibility = duplicateModels.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (duplicateModels.Count == 0)
            {
                return;
            }

            AddDuplicateTab(0, Text.TypeTab(Text.AllFiles, duplicateModels.Count));
            for (var i = 0; i < GroupOrder.Length; i++)
            {
                var kind = GroupOrder[i];
                AddDuplicateTab(i + 1, Text.TypeTab(TypeName(kind), duplicateModels.Count(m => m.Type == kind)));
            }
        }
        finally
        {
            duplicateTabsBuilding = false;
        }
    }

    private void AddDuplicateTab(int index, string label)
    {
        var tab = new RadioButton { Content = label, GroupName = "DuplicateTypeTabs", Style = LeanStyles.Tab, IsChecked = index == duplicateTab };
        AutomationProperties.SetName(tab, label);
        AutomationProperties.SetAutomationId(tab, "DuplicateType");
        tab.Checked += (_, _) =>
        {
            if (!duplicateTabsBuilding && duplicateTab != index)
            {
                duplicateTab = index;
                ShowDuplicates(toTop: true);
            }
        };
        DuplicateTypeTabs.Children.Add(tab);
    }

    // The list of the chosen tab. The groups are rebuilt (their ticks and Keep choices live in the models, so none is lost); a rule or a
    // prune keeps the place in the list, a new tab starts at the top.
    private void ShowDuplicates(bool toTop)
    {
        var offset = toTop ? 0 : DuplicatesScroll.VerticalOffset;
        DuplicatesList.ItemsSource = ShownDuplicates();
        DuplicatesScroll.UpdateLayout();
        DuplicatesScroll.ChangeView(null, offset, null, true);
    }

    // The status line the Duplicates view wrote last: it goes when the user leaves the view, and no other view's message does. A search
    // that goes on, or ends, while another view is shown leaves that view's status line alone.
    private string? duplicatesStatus;

    private void SetDuplicatesStatus(string text)
    {
        if (!ReferenceEquals(currentView, DuplicatesView))
        {
            return;
        }

        duplicatesStatus = text;
        StatusText.Text = text;
    }

    private void ClearDuplicatesStatus()
    {
        if (duplicatesStatus is not null && StatusText.Text == duplicatesStatus)
        {
            StatusText.Text = string.Empty;
        }

        duplicatesStatus = null;
    }

    // ---- select by rule -----------------------------------------------------------------------------------------------

    // The rule sets the kept copy of every group of the tab shown, and ticks the others; a group the rule has no answer for (the folder
    // rule with no copy inside the folder) is left as it is. The status line says what was ticked. Nothing is ticked before this.
    // Suggest (ASP-17) is the Newest rule run from its own button: the same plan and the same question over 100, then a Why column
    // that shows "Older copy; the newest is kept" on each copy it ticked, and the status line Text.SuggestTicked.
    private async Task ApplyRuleAsync(KeepRule rule, string? folder, bool suggest = false)
    {
        if (MoveBlocked)
        {
            return;
        }

        var shownModels = duplicateModels; // the list this plan is for
        // What the rule would tick, worked out before anything is ticked: per group, every copy that is not the kept one and has no hint.
        var plan = new List<(DuplicateGroupModel Model, DuplicateCopy Keep)>();
        int wouldTick = 0, programFiles = 0;
        foreach (var model in ShownDuplicates())
        {
            DuplicateCopy? keep;
            try
            {
                keep = DuplicateRules.Keep(model.Group, rule, folder);
            }
            catch (ArgumentException)
            {
                return; // not a usable folder: nothing changes
            }

            if (keep is null)
            {
                continue;
            }

            plan.Add((model, keep));
            foreach (var copy in model.Group.Copies.Where(c => !ReferenceEquals(c, keep) && c.Hint is null))
            {
                wouldTick++;
                programFiles += ProgramFileTypes.Contains(Path.GetExtension(copy.Node.Path)) ? 1 : 0;
            }
        }

        if (wouldTick > RuleAsksAbove && !await ConfirmRuleAsync(Text.RuleConfirm(wouldTick, plan.Count, programFiles), suggest ? Text.Suggest : Text.SelectByRule))
        {
            return; // Cancel: no tick changes
        }

        if (MoveBlocked || !ReferenceEquals(shownModels, duplicateModels))
        {
            return; // something started, or the list was replaced, while the question was open: the plan is dropped
        }

        // A rule of the user's own, or Suggest again, starts from no reasons: a Why belongs to the Suggest that ticked the copy.
        ClearDuplicatesWhy();
        int copies = 0, groups = 0;
        foreach (var (model, keep) in plan)
        {
            model.ApplyKeep(keep);
            groups++;
            copies += model.Selection.Ticked.Count;
            if (suggest)
            {
                model.MarkSuggested(model.Selection.Ticked);
            }
        }

        duplicatesWhy = suggest;
        ShowDuplicates(toTop: false);
        UpdateMoveControls();
        SetDuplicatesStatus(suggest ? Text.SuggestTicked(copies) : Text.SelectedByRule(copies, groups));
    }

    // The question before a large rule: the text, Tick and Cancel (the default, so Enter ticks nothing).
    private async Task<bool> ConfirmRuleAsync(string question, string title)
    {
        if (Content?.XamlRoot is not { } root)
        {
            return false;
        }

        var line = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };
        AutomationProperties.SetName(line, question);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = line,
            PrimaryButtonText = Text.RuleConfirmTick,
            PrimaryButtonStyle = LeanStyles.Primary,
            RequestedTheme = ElementTheme.Dark,
            CloseButtonText = Text.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ConfirmRule");
        confirmDialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (COMException)
        {
            return false; // another dialog is open: nothing is ticked
        }
        finally
        {
            confirmDialogOpen = false;
        }
    }

    private async Task ApplyFolderRuleAsync()
    {
        try
        {
            if (await PickFolderAsync() is { } folder)
            {
                await ApplyRuleAsync(KeepRule.InsideFolder, folder);
            }
        }
        catch (COMException)
        {
            StatusText.Text = Text.FolderPickerFailed; // the picker could not open: nothing changes
        }
    }

    // Unticks every group of the tab shown; each Keep choice stays as it is.
    private void ClearDuplicateSelection()
    {
        if (MoveBlocked)
        {
            return;
        }

        foreach (var model in ShownDuplicates())
        {
            model.ClearTicks();
        }

        ClearDuplicatesWhy(); // Clear selection takes Suggest's Why column away
        ShowDuplicates(toTop: false);
        UpdateMoveControls();
        SetDuplicatesStatus(Text.SelectionCleared);
    }

    // ---- row actions and the preview ----------------------------------------------------------------------------------

    // Show in File Explorer (the magnifier) on every row, and the eye on the rows of the picture types; shown on hover and focus, reached with Tab.
    private List<Button> CreateDuplicateActionButtons(TableRow row)
    {
        var buttons = new List<Button>();
        var reveal = RowButton("", Text.ShowInFileExplorer, "RevealRow"); // Segoe Fluent Icons: Search
        reveal.Click += (_, _) => _ = RevealPathAsync(RowNode(row)?.Path);
        buttons.Add(reveal);
        if (RowNode(row) is { } node && PicturePreview.IsPictureType(node.Path))
        {
            var eye = RowButton("", Text.PreviewPicture, "PreviewCopy"); // RedEye
            eye.Click += (_, _) => _ = ShowPreviewAsync(row);
            buttons.Add(eye);
        }

        return buttons;
    }

    // The flyout of one picture: the picture, its size in pixels and its path, or why there is none. The file is read by the Core's guard
    // (an online-only file is not opened) and decoded here from the bytes.
    private async Task ShowPreviewAsync(TableRow row)
    {
        if (previewOpening || RowNode(row) is not { } node)
        {
            return;
        }

        previewOpening = true;
        try
        {
            var path = node.Path;
            var read = await Task.Run(() => PicturePreview.Read(path));
            var content = new StackPanel { Spacing = 8, MaxWidth = PreviewWidth + 24 };
            string? problem = read.Result == PreviewResult.OnlineOnly ? Text.PreviewOnlineOnly : read.Bytes is null ? Text.PreviewUnavailable : null;
            if (problem is null && await DecodePreviewAsync(read.Bytes!) is { } picture)
            {
                var image = new Image { Source = picture.Image, Stretch = Stretch.Uniform, MaxWidth = PreviewWidth, MaxHeight = 400, HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(image, node.Name);
                content.Children.Add(image);
                content.Children.Add(new TextBlock { Text = Text.PreviewSize(picture.Width, picture.Height) });
            }
            else
            {
                content.Children.Add(new TextBlock { Text = problem ?? Text.PreviewUnavailable, TextWrapping = TextWrapping.Wrap });
            }

            content.Children.Add(new TextBlock { Text = path, Style = LeanStyles.Note, TextWrapping = TextWrapping.Wrap });
            new Flyout { Content = content }.ShowAt(row.Item);
        }
        finally
        {
            previewOpening = false;
        }
    }

    // The picture's bytes decoded in memory: its size in pixels, and an image at most PreviewWidth px wide; null when no decoder reads it.
    private static async Task<(BitmapImage Image, int Width, int Height)?> DecodePreviewAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = Math.Min(width, PreviewWidth) };
            await image.SetSourceAsync(stream);
            return (image, width, height);
        }
        catch (Exception)
        {
            return null; // a file no decoder reads shows the "no preview" text, never an error
        }
    }
}
