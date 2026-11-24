using System.Collections.Generic;
using Ambysto.Lean.Core.Copy;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ambysto.Lean.App;

/// <summary>The sidebar (ASP-14, intent/2026-10-18-asp-14-start-and-sidebar.md): the app name, the six views and, at the bottom,
/// Settings and About. Each item is a button that keeps the AutomationId the view button had before; it shows a Segoe Fluent Icons
/// glyph and its label, and its automation name is the label. Exactly one item is current: a 2px ink bar on its left and the ground
/// behind it. Choosing an item shows its view and cancels nothing that runs (ASP-9).</summary>
public sealed partial class MainWindow
{
    // The bar inside each item's content; filled only on the current item.
    private readonly Dictionary<Button, Microsoft.UI.Xaml.Shapes.Rectangle> sidebarBars = [];
    private Button? currentSidebarItem;

    // Every visible string, from Copy.Text. Segoe Fluent Icons: Home, Folder, Page, Copy, Package, History, Settings, Info.
    private void InitializeSidebar()
    {
        BrandRun.Text = Text.BrandName;
        ProductRun.Text = Text.ProductName;
        AddSidebarItem(OverviewButton, "", Text.Overview);
        AddSidebarItem(LargeOldButton, "", Text.LargeOld);
        AddSidebarItem(TemporaryFilesButton, "", Text.TemporaryFiles);
        AddSidebarItem(DuplicatesButton, "", Text.Duplicates);
        AddSidebarItem(UninstallButton, "", Text.Uninstall);
        AddSidebarItem(AppsAndCachesButton, "\uECAA", Text.AppsAndCaches); // Segoe Fluent Icons: AppIconDefault
        AddSidebarItem(HistoryButton, "", Text.History);
        AddSidebarItem(SettingsButton, "", Text.Settings);
        AddSidebarItem(AboutButton, "", Text.About);
        AddSidebarItem(BuyCoffeeButton, "\uEB51", Text.BuyMeACoffee);
        SetCurrentSidebarItem(OverviewButton);
    }

    private void AddSidebarItem(Button item, string glyph, string label)
    {
        var bar = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 2, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(bar);
        var icon = new FontIcon { Glyph = glyph, FontSize = 16, Margin = new Thickness(18, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 1);
        content.Children.Add(icon);
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 2);
        content.Children.Add(text);
        item.Content = content;
        AutomationProperties.SetName(item, label);
        sidebarBars[item] = bar;
    }

    // Exactly one item is current: the one whose view is shown (Overview when none is).
    private void SetCurrentSidebarItem(Button current)
    {
        currentSidebarItem = current;
        foreach (var (item, bar) in sidebarBars)
        {
            var isCurrent = ReferenceEquals(item, current);
            bar.Fill = isCurrent ? new SolidColorBrush(BrandPalette.SidebarBar) : null;
            if (isCurrent && BrandPalette.SidebarCurrentGround is { } ground)
            {
                item.Background = new SolidColorBrush(ground);
            }
            else
            {
                item.ClearValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty);
            }
        }
    }

    // High Contrast switched: the bar and the ground are drawn again.
    private void RefreshSidebar()
    {
        if (currentSidebarItem is { } current)
        {
            SetCurrentSidebarItem(current);
        }

        ProductRun.Foreground = new SolidColorBrush(BrandPalette.Accent);
    }

    private void OnOverviewClick(object sender, RoutedEventArgs e) => ShowView(null);
}
