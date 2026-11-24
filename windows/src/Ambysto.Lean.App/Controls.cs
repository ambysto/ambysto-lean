using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>The automation peer of a container that UI Automation shows as a group (the volume card, the map). A panel has
/// no peer of its own, so without this its AutomationId would not reach Narrator or the UI tests.</summary>
public sealed class GroupPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    /// <inheritdoc />
    protected override bool IsControlElementCore() => true;

    /// <inheritdoc />
    protected override bool IsContentElementCore() => true;
}

/// <summary>The automation peer of a menu separator: UI Automation shows a separator, which WinUI's own MenuFlyoutSeparator does not.</summary>
public sealed class SeparatorPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Separator;

    /// <inheritdoc />
    protected override bool IsControlElementCore() => true;

    /// <inheritdoc />
    protected override bool IsContentElementCore() => false;
}

/// <summary>A separator for a menu (ASP-15): a menu item that is off and drawn as a thin rule (the style LeanMenuSeparator), with a peer that
/// UI Automation reads as a separator. WinUI's MenuFlyoutSeparator has no peer, so Narrator and the UI tests would not see it.</summary>
public sealed partial class MenuSeparatorItem : MenuFlyoutItem
{
    /// <summary>A separator.</summary>
    public MenuSeparatorItem()
    {
        IsEnabled = false;
        Style = LeanStyles.Get("LeanMenuSeparator");
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new SeparatorPeer(this);
}

/// <summary>A stack panel exposed as a group.</summary>
public sealed partial class GroupPanel : StackPanel
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

/// <summary>A grid exposed as a group: a view whose list takes the room that is left and scrolls inside it.</summary>
public sealed partial class GroupGrid : Grid
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

/// <summary>The canvas that holds the map's tiles, exposed as a group.</summary>
public sealed partial class MapCanvas : Canvas
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

/// <summary>A thin vertical rule (a grid with a background) between two items of a <see cref="WrapPanel"/> (ASP-18). Decoration: the panel places it, and the
/// markup gives it the Raw accessibility view.</summary>
public sealed partial class BandDivider : Grid
{
}

/// <summary>A panel that lays its children out left to right and starts a new line when the next one does not fit (the main window's
/// button row, ASP-12: JetBrains Mono is wider than the system font). The children keep their order, so the tab order is unchanged.
/// A <see cref="BandDivider"/> child (ASP-18) is decoration between two items: it sits in the middle of the gap before the
/// item that follows it, and takes no room of its own. Where that item starts a line the divider is not drawn, so none is ever at the
/// start or the end of a line. <see cref="CenterLines"/> centres the items of a line on the line, as the dividers are.</summary>
public sealed partial class WrapPanel : Panel
{
    /// <summary>The space between two children on a line.</summary>
    public double HorizontalSpacing { get; set; }

    /// <summary>The space between two lines.</summary>
    public double VerticalSpacing { get; set; }

    /// <summary>True to give every item of a line the height of the line (the drive cards, ASP-22: the cards of a line are as high as the
    /// tallest, as they were in a horizontal stack).</summary>
    public bool StretchLines { get; set; }

    /// <summary>True to centre the items of a line vertically on the line (the default puts them at its top).</summary>
    public bool CenterLines { get; set; }

    /// <inheritdoc />
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, usedWidth = 0, usedHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(availableSize.Width, availableSize.Height));
            if (child is BandDivider)
            {
                continue; // takes no room: it sits in the gap
            }

            var size = child.DesiredSize;
            if (lineWidth > 0 && lineWidth + HorizontalSpacing + size.Width > availableSize.Width)
            {
                usedWidth = Math.Max(usedWidth, lineWidth);
                usedHeight += lineHeight + VerticalSpacing;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += (lineWidth > 0 ? HorizontalSpacing : 0) + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        usedWidth = Math.Max(usedWidth, lineWidth);
        usedHeight += lineHeight;
        return new Windows.Foundation.Size(usedWidth, usedHeight);
    }

    /// <inheritdoc />
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        // First the places: each item with its line and x, each divider with the item that follows it. Then the lines' heights are
        // known, and everything is arranged.
        var places = new List<(UIElement Child, int Line, double X, UIElement? Divider)>();
        var lineHeights = new List<double> { 0 };
        double x = 0;
        UIElement? divider = null;
        foreach (var child in Children)
        {
            if (child is BandDivider)
            {
                divider = child;
                // Not drawn until an item puts it in a gap (a zero-size slot would clip it, the opacity does not change the layout).
                child.Opacity = 0;
                child.Arrange(new Windows.Foundation.Rect(0, 0, child.DesiredSize.Width, child.DesiredSize.Height));
                continue;
            }

            var size = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + size.Width > finalSize.Width)
            {
                lineHeights.Add(0);
                x = 0;
            }

            var gap = x > 0;
            if (gap)
            {
                x += HorizontalSpacing;
            }

            places.Add((child, lineHeights.Count - 1, x, gap ? divider : null));
            x += size.Width;
            lineHeights[^1] = Math.Max(lineHeights[^1], size.Height);
            divider = null;
        }

        var tops = new double[lineHeights.Count];
        double y = 0;
        for (var i = 0; i < lineHeights.Count; i++)
        {
            tops[i] = y;
            y += lineHeights[i] + (i < lineHeights.Count - 1 ? VerticalSpacing : 0);
        }

        foreach (var (child, line, left, before) in places)
        {
            var size = child.DesiredSize;
            var top = tops[line] + (CenterLines ? (lineHeights[line] - size.Height) / 2 : 0);
            child.Arrange(new Windows.Foundation.Rect(left, top, size.Width, StretchLines ? lineHeights[line] : size.Height));
            if (before is not null)
            {
                var d = before.DesiredSize;
                before.Opacity = 1;
                before.Arrange(new Windows.Foundation.Rect(left - (HorizontalSpacing / 2) - (d.Width / 2), tops[line] + ((lineHeights[line] - d.Height) / 2), d.Width, d.Height));
            }
        }

        return new Windows.Foundation.Size(finalSize.Width, y);
    }
}
