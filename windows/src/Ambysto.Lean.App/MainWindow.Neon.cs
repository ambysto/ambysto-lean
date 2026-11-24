using System.Collections.Generic;
#if LEAN_NEON
using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
#endif

namespace Ambysto.Lean.App;

/// <summary>The neon of the scan readout (ASP-14; the brand book's Neon Rule, ambysto-assets#2). It is built behind the build switch
/// <c>LeanNeon</c>, which is off until the owner merges that PR: with the switch off every method here is empty. With it on:
/// a Composition <c>DropShadow</c> glow in the tone (8 px, 40 %) on the meter's head cell and on the cursor; a 1px tone sweep line with
/// the same glow that crosses each zone still being read on the map in 1.2 s and starts again; faint scanlines (1px rule colour, every
/// 3 px, 4 %) on the readout panel only. When the scan ends they all fade out over 0.4 s and the finished map is crisp. With Windows'
/// animation effects off nothing blinks or sweeps and only a static glow stays on the meter's head; under High Contrast there is
/// none of it. No image files: the glow is a Composition shadow with the tone as its colour.</summary>
public sealed partial class MainWindow
{
#if LEAN_NEON
    private const float GlowBlur = 8;
    private const float GlowOpacity = 0.4f;
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(400);
    private const double ScanlineOpacity = 0.04;
    private const double ScanlineStep = 3;

    private sealed class Sweep(SpriteVisual sprite, CompositionPropertySet place)
    {
        public SpriteVisual Sprite { get; } = sprite;

        public CompositionPropertySet Place { get; } = place;
    }

    private Compositor? neonCompositor;
    private ContainerVisual? sweepRoot;
    private CompositionPropertySet? sweepClock;
    private readonly Dictionary<string, Sweep> sweeps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SpriteVisual> glows = [];
    private int glowHeadCell = -1;
    private bool neonActive;
    private DispatcherTimer? neonFadeTimer;

    private void InitializeNeon()
    {
        neonCompositor = ElementCompositionPreview.GetElementVisual(ScanReadout).Compositor;
        ScanReadout.SizeChanged += (_, _) => RebuildScanlines();
    }

    private void NeonBegin()
    {
        neonFadeTimer?.Stop();
        ClearNeon();
        readoutHold = false;
        // No neon at all under High Contrast or with Windows' animation effects off (spec 01; stricter than the brand rule).
        neonActive = !BrandPalette.HighContrast && BrandPalette.AnimationsEnabled;
        if (!neonActive)
        {
            return;
        }

        Scanlines.Opacity = ScanlineOpacity;
        Scanlines.Visibility = Visibility.Visible;
        RebuildScanlines();
        AttachGlow(ScanCursor);
    }

    // High Contrast or animation effects turned off while the scan runs: all of it goes at once, and stays off until the next scan.
    private void NeonSuspendIfNeeded()
    {
        if (neonActive && (BrandPalette.HighContrast || !BrandPalette.AnimationsEnabled))
        {
            neonActive = false;
            neonFadeTimer?.Stop();
            ClearNeon();
            readoutHold = false;
        }
    }

    // The glow on the meter's head cell follows the head; with animation effects off there is no neon at all (NeonSuspendIfNeeded), so it is not drawn then.
    private void NeonMeterHead(int head)
    {
        if (!neonActive || head == glowHeadCell)
        {
            return;
        }

        if (glowHeadCell >= 0)
        {
            ElementCompositionPreview.SetElementChildVisual(meterCells[glowHeadCell], null);
        }

        glowHeadCell = head;
        if (head >= 0)
        {
            AttachGlow(meterCells[head]);
        }
    }

    private void AttachGlow(FrameworkElement element)
    {
        if (neonCompositor is not { } compositor)
        {
            return;
        }

        var tone = ToneColor();
        var shadow = compositor.CreateDropShadow();
        shadow.Color = tone;
        shadow.BlurRadius = GlowBlur;
        shadow.Opacity = GlowOpacity;
        shadow.Offset = Vector3.Zero;
        var glow = compositor.CreateSpriteVisual();
        glow.Size = new Vector2((float)element.Width, (float)element.Height);
        glow.Brush = compositor.CreateColorBrush(tone);
        glow.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(element, glow);
        glows.Add(glow);
    }

    private static Color ToneColor() => BrandPalette.Tone;

    // A 1px tone line with the same glow crosses each zone still being read, in 1.2 s, and starts again. One clock runs them all; each
    // line takes its place from a property set that every redraw of the map updates.
    private void NeonSweeps(List<TileModel> models)
    {
        if (!neonActive || !BrandPalette.AnimationsEnabled || neonCompositor is not { } compositor)
        {
            return;
        }

        if (sweepRoot is null)
        {
            sweepRoot = compositor.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(MapArea, sweepRoot);
            sweepClock = compositor.CreatePropertySet();
            sweepClock.InsertScalar("Phase", 0f);
            var phase = compositor.CreateScalarKeyFrameAnimation();
            phase.InsertKeyFrame(0f, 0f);
            phase.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
            phase.Duration = SweepPeriod;
            phase.IterationBehavior = AnimationIterationBehavior.Forever;
            sweepClock.StartAnimation("Phase", phase);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            // Only a zone still being read (drawn like the "Not measured" tile; its help text is the zone's path).
            if (model.Kind != Ambysto.Lean.Core.Map.TileKind.NotMeasured || model.HelpText.Length == 0)
            {
                continue;
            }

            seen.Add(model.HelpText);
            if (!sweeps.TryGetValue(model.HelpText, out var sweep))
            {
                sweep = NewSweep(compositor);
                sweeps[model.HelpText] = sweep;
                sweepRoot.Children.InsertAtTop(sweep.Sprite);
            }

            sweep.Place.InsertScalar("X", (float)model.Bounds.X);
            sweep.Place.InsertScalar("Y", (float)model.Bounds.Y);
            sweep.Place.InsertScalar("W", (float)Math.Max(1, model.Bounds.Width - 1));
            sweep.Sprite.Size = new Vector2(1, (float)Math.Max(1, model.Bounds.Height));
        }

        foreach (var path in sweeps.Keys.ToArray())
        {
            if (!seen.Contains(path))
            {
                sweepRoot.Children.Remove(sweeps[path].Sprite);
                sweeps.Remove(path);
            }
        }
    }

    private Sweep NewSweep(Compositor compositor)
    {
        var tone = ToneColor();
        var shadow = compositor.CreateDropShadow();
        shadow.Color = tone;
        shadow.BlurRadius = GlowBlur;
        shadow.Opacity = GlowOpacity;
        shadow.Offset = Vector3.Zero;
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = compositor.CreateColorBrush(tone);
        sprite.Shadow = shadow;
        var place = compositor.CreatePropertySet();
        place.InsertScalar("X", 0f);
        place.InsertScalar("Y", 0f);
        place.InsertScalar("W", 1f);
        var follow = compositor.CreateExpressionAnimation("Vector3(place.X + place.W * clock.Phase, place.Y, 0)");
        follow.SetReferenceParameter("place", place);
        follow.SetReferenceParameter("clock", sweepClock!);
        sprite.StartAnimation("Offset", follow);
        return new Sweep(sprite, place);
    }

    // The scan ended: the glow, the sweeps and the scanlines fade out over 0.4 s, then they are gone and the readout with them.
    private void NeonEnd()
    {
        if (!neonActive || neonCompositor is not { } compositor)
        {
            return;
        }

        neonActive = false;
        readoutHold = true;
        foreach (var visual in glows)
        {
            Fade(compositor, visual);
        }

        if (sweepRoot is not null)
        {
            Fade(compositor, sweepRoot);
        }

        var fade = new DoubleAnimation { From = ScanlineOpacity, To = 0, Duration = new Duration(FadeTime) };
        Storyboard.SetTarget(fade, Scanlines);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var board = new Storyboard();
        board.Children.Add(fade);
        board.Begin();

        neonFadeTimer ??= new DispatcherTimer { Interval = FadeTime };
        neonFadeTimer.Tick -= OnNeonFaded;
        neonFadeTimer.Tick += OnNeonFaded;
        neonFadeTimer.Start();
    }

    private static void Fade(Compositor compositor, Visual visual)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 1f);
        animation.InsertKeyFrame(1f, 0f);
        animation.Duration = FadeTime;
        visual.StartAnimation("Opacity", animation);
    }

    private void OnNeonFaded(object? sender, object e)
    {
        neonFadeTimer?.Stop();
        ClearNeon();
        readoutHold = false;
        UpdateOverview();
    }

    private void ClearNeon()
    {
        foreach (var cell in meterCells)
        {
            ElementCompositionPreview.SetElementChildVisual(cell, null);
        }

        ElementCompositionPreview.SetElementChildVisual(ScanCursor, null);
        glows.Clear();
        glowHeadCell = -1;
        if (sweepRoot is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(MapArea, null);
            sweepRoot.Dispose();
            sweepRoot = null;
        }

        sweepClock?.Dispose();
        sweepClock = null;
        foreach (var sweep in sweeps.Values)
        {
            sweep.Sprite.Dispose();
        }

        sweeps.Clear();
        Scanlines.Children.Clear();
        Scanlines.Visibility = Visibility.Collapsed;
    }

    // 1px lines of the rule colour every 3 px across the readout panel, at 4 % opacity.
    private void RebuildScanlines()
    {
        Scanlines.Children.Clear();
        if (!neonActive)
        {
            return;
        }

        var width = ScanReadout.ActualWidth;
        var height = ScanReadout.ActualHeight;
        var rule = (Brush)Application.Current.Resources["LeanRuleBrush"];
        for (var y = 1.0; y < height; y += ScanlineStep)
        {
            var line = new Rectangle { Width = width, Height = 1, Fill = rule, IsHitTestVisible = false };
            Canvas.SetTop(line, y);
            Scanlines.Children.Add(line);
        }
    }
#else
    private static void InitializeNeon()
    {
    }

    private static void NeonBegin()
    {
    }

    private static void NeonEnd()
    {
    }

    private static void NeonMeterHead(int head)
    {
    }

    private static void NeonSweeps(List<TileModel> models)
    {
    }

    private static void NeonSuspendIfNeeded()
    {
    }
#endif
}
