using System;
using System.Runtime.InteropServices;
using Ambysto.Lean.Core.Map;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Ambysto.Lean.App;

/// <summary>The map's colours and the title bar's (ASP-12, intent/2026-10-18-asp-12-brand-theme.md). The frame colours are read from
/// Theme.xaml, so that file stays the one place that names them; the seven file-type tones live here and nowhere else. When Windows
/// High Contrast is on, every tile is drawn in the system's window colour with the window text colour as border and label, and the
/// callers follow <see cref="HighContrastChanged"/>.</summary>
internal static class BrandPalette
{
    // The brand's tones (ambysto-assets brand/DESIGN.md): the map uses six of the seven, never the app's own tone.
    private static readonly Color VideoTone = Color.FromArgb(0xFF, 0xB0, 0xAD, 0xFF);               // Optimer
    private static readonly Color DiskImageTone = Color.FromArgb(0xFF, 0x62, 0xC4, 0xF5);           // Steady
    private static readonly Color ArchiveTone = Color.FromArgb(0xFF, 0xDF, 0xAE, 0x5B);             // Tune
    private static readonly Color DocumentTone = Color.FromArgb(0xFF, 0x4E, 0xCF, 0xBD);            // DevBox
    private static readonly Color ApplicationTone = Color.FromArgb(0xFF, 0x9F, 0xC6, 0x75);         // Power
    private static readonly Color VirtualMachineImageTone = Color.FromArgb(0xFF, 0xE6, 0x9A, 0xD4); // Guard

    private static readonly Lazy<AccessibilitySettings> Accessibility = new(() => new AccessibilitySettings());
    private static readonly Lazy<UISettings> Settings = new(() => new UISettings());

    /// <summary>True while Windows High Contrast is on.</summary>
    public static bool HighContrast
    {
        get
        {
            try
            {
                return Accessibility.Value.HighContrast;
            }
            catch (COMException)
            {
                return false; // no system setting to read: the dark theme
            }
        }
    }

    /// <summary>Raised (on a worker thread) when the system colours change, High Contrast switched on or off among them: callers
    /// compare <see cref="HighContrast"/> with what they last drew. <c>AccessibilitySettings.HighContrastChanged</c> is subscribed too, but
    /// in this unpackaged app subscribing to it fails (E_NOT_FOUND, observed 2026-10-18 on Windows 11 24H2), so
    /// <c>UISettings.ColorValuesChanged</c>, which fires when the system colours change, carries the signal.</summary>
    public static event Action? HighContrastChanged
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (Gate)
            {
                if (handlers is null)
                {
                    try
                    {
                        Accessibility.Value.HighContrastChanged += OnHighContrastChanged;
                    }
                    catch (COMException)
                    {
                        // The other source below still reports it.
                    }

                    try
                    {
                        Settings.Value.ColorValuesChanged += OnColorsChanged;
                    }
                    catch (COMException)
                    {
                        // No signal at all: the window follows High Contrast at the next start.
                    }
                }

                handlers += value;
            }
        }
        remove
        {
            lock (Gate)
            {
                handlers -= value;
                if (handlers is null)
                {
                    try
                    {
                        Accessibility.Value.HighContrastChanged -= OnHighContrastChanged;
                    }
                    catch (COMException)
                    {
                        // Never subscribed.
                    }

                    try
                    {
                        Settings.Value.ColorValuesChanged -= OnColorsChanged;
                    }
                    catch (COMException)
                    {
                        // Never subscribed.
                    }
                }
            }
        }
    }

    /// <summary>False when Windows' animation effects are off (Settings > Accessibility > Visual effects): nothing blinks or sweeps then.</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return Settings.Value.AnimationsEnabled;
            }
            catch (COMException)
            {
                return true; // no system setting to read: the default
            }
        }
    }

    private static readonly object Gate = new();
    private static Action? handlers;

    private static void OnHighContrastChanged(AccessibilitySettings sender, object args) => handlers?.Invoke();

    private static void OnColorsChanged(UISettings sender, object args) => handlers?.Invoke();

    // ---- the frame, from Theme.xaml ----------------------------------------------------------------------------------

    /// <summary>The window ground.</summary>
    public static Color Ground => Frame("LeanGround");

    /// <summary>Panels, dialogs, table headers.</summary>
    public static Color Panel => Frame("LeanPanel");

    /// <summary>Primary text.</summary>
    public static Color Ink => Frame("LeanInk");

    /// <summary>Secondary text.</summary>
    public static Color Mute => Frame("LeanMute");

    /// <summary>Hairlines.</summary>
    public static Color Rule => Frame("LeanRule");

    /// <summary>Dim lines and unlit cells.</summary>
    public static Color Dim => Frame("LeanDim");

    /// <summary>The app's own tone (Space Coral): the accent, never a fill of a button or a surface.</summary>
    public static Color Tone => Frame("LeanTone");

    private static Color Frame(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Color colour
        ? colour
        : throw new InvalidOperationException($"Theme.xaml has no colour '{key}'.");

    /// <summary>The system's High Contrast window colour: the <c>SystemColorWindowColor</c> theme resource, read from the app's resources
    /// while High Contrast is on (<c>UIColorType.Background</c> follows the app mode, not High Contrast).</summary>
    public static Color SystemWindow => SystemColour("SystemColorWindowColor", UIColorType.Background);

    /// <summary>The system's High Contrast window text colour: the <c>SystemColorWindowTextColor</c> theme resource.</summary>
    public static Color SystemWindowText => SystemColour("SystemColorWindowTextColor", UIColorType.Foreground);

    private static Color SystemColour(string key, UIColorType fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Color colour
            ? colour
            : Settings.Value.GetColorValue(fallback);

    /// <summary>The ground of a row's action buttons (ASP-14): the list's hover fill, so they sit on the row; the system's window colour in High Contrast.</summary>
    public static Color RowActionsGround => HighContrast ? SystemWindow : Frame("LeanSelection");

    // ---- the sidebar and the drive cards (ASP-14) --------------------------------------------------------------------

    /// <summary>The bar of the current sidebar item: the ink (the system's text colour in High Contrast).</summary>
    public static Color SidebarBar => HighContrast ? SystemWindowText : Ink;

    /// <summary>The ground behind the current sidebar item; null in High Contrast, where the item keeps the system's button colour.</summary>
    public static Color? SidebarCurrentGround => HighContrast ? null : Ground;

    /// <summary>The ground of a duplicates group (ASP-15): neighbouring groups alternate between the panel and the ground colour; null in
    /// High Contrast, where every group keeps the system's window colour.</summary>
    public static Color? GroupGround(int index) => HighContrast ? null : index % 2 == 0 ? Panel : Ground;

    /// <summary>A lit cell of a drive card's meter: the ink, the tone for the drive Lean scans (the system's text colour in High Contrast).</summary>
    public static Color MeterLit(bool scannedDrive) => HighContrast ? SystemWindowText : scannedDrive ? Tone : Ink;

    /// <summary>An unlit cell of a drive card's meter: the dim line (the system's window colour with a text-coloured edge in High Contrast).</summary>
    public static Color MeterDim => HighContrast ? SystemWindow : Dim;

    /// <summary>The edge of a meter cell: only in High Contrast, so an unlit cell can be told from the card.</summary>
    public static Color? MeterEdge => HighContrast ? SystemWindowText : null;

    /// <summary>The accent in the sidebar's app name: the tone (the system's text colour in High Contrast).</summary>
    public static Color Accent => HighContrast ? SystemWindowText : Tone;

    // ---- the map -----------------------------------------------------------------------------------------------------

    /// <summary>The fill of a tile.</summary>
    public static Color Fill(TileModel model) => model.Kind switch
    {
        TileKind.NotMeasured => HighContrast ? SystemWindow : Panel,
        TileKind.SmallerItems => HighContrast ? SystemWindow : Dim,
        _ => Fill(model.Colour),
    };

    /// <summary>The fill of a file-type group (the legend's swatch too).</summary>
    public static Color Fill(TypeGroupKind kind)
    {
        if (HighContrast)
        {
            return SystemWindow;
        }

        return kind switch
        {
            TypeGroupKind.Video => VideoTone,
            TypeGroupKind.DiskImage => DiskImageTone,
            TypeGroupKind.Archive => ArchiveTone,
            TypeGroupKind.Document => DocumentTone,
            TypeGroupKind.Application => ApplicationTone,
            TypeGroupKind.VirtualMachineImage => VirtualMachineImageTone,
            TypeGroupKind.OpenFolder => Panel,
            _ => Mute,
        };
    }

    /// <summary>The label on a tile: the ground on a tone, the ink on a neutral. The "other" group's mute grey takes the ground too,
    /// for the contrast (about 5.6 to 1; the ink would give 3.2 to 1).</summary>
    public static Color Label(TileModel model) => HighContrast ? SystemWindowText : model.Kind switch
    {
        TileKind.NotMeasured or TileKind.SmallerItems => Ink,
        _ => model.Colour == TypeGroupKind.OpenFolder ? Ink : Ground,
    };

    /// <summary>A tile's edge: 1px of the ground between tiles; a folder drawn with its children has a 1px rule; the hovered or focused
    /// tile has the ink (2px, set by the caller).</summary>
    public static Color Border(TileModel model, bool emphasised)
    {
        if (HighContrast)
        {
            return SystemWindowText;
        }

        if (emphasised)
        {
            return Ink;
        }

        return model.Kind == TileKind.Item && model.Colour == TypeGroupKind.OpenFolder ? Rule : Ground;
    }

    /// <summary>The border of a legend swatch.</summary>
    public static Color SwatchBorder(TypeGroupKind kind) => HighContrast ? SystemWindowText : kind == TypeGroupKind.OpenFolder ? Rule : Ground;

    /// <summary>The hatching of the "Not measured" tile.</summary>
    public static Color Hatch => HighContrast ? SystemWindowText : Dim;
}

/// <summary>The styles of Theme.xaml, for the elements built in code.</summary>
internal static class LeanStyles
{
    /// <summary>A style of Theme.xaml by its key.</summary>
    public static Style Get(string key) => (Style)Application.Current.Resources[key];

    public static Style Title => Get("LeanTitle");

    public static Style Label => Get("LeanLabel");

    public static Style Medium => Get("LeanMedium");

    public static Style Note => Get("LeanNote");

    public static Style Sentence => Get("LeanSentence");

    public static Style MuteText => Get("LeanMuteText");

    public static Style Primary => Get("LeanPrimaryButton");

    public static Style TableHeader => Get("LeanTableHeader");

    public static Style TableRow => Get("LeanTableRow");

    public static Style Tab => Get("LeanTab");

    /// <summary>The band behind a table column (ASP-22): every other one has the tone, in High Contrast every one has a divider.</summary>
    public static Style ColumnBand(int column) => Get(column % 2 == 0 ? "LeanColumnBandEven" : "LeanColumnBand");
}
