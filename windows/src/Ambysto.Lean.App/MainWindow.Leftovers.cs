using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Copy;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Paths;
using Ambysto.Lean.Core.Rules;
using Ambysto.Lean.Core.Trash;
using Ambysto.Lean.Core.Uninstall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Ambysto.Lean.App;

/// <summary>The leftovers of an app that the watch reports gone (plan group 11, docs/spec/04 Windows note). The Core's matcher
/// decides what counts as a leftover; this file lists the candidates, each measured when it is offered, with nothing ticked.</summary>
public sealed partial class MainWindow
{
    // A candidate and its scan: the folder is measured for its size and its stamp when it is offered. Node is null when it could not
    // be measured, and then the candidate is shown, not selectable.
    private sealed record LeftoverOffer(LeftoverCandidate Candidate, ScanNode? Node, string? Note)
    {
        public bool Selectable => Candidate.Selectable && Node is not null && Note is null;
    }

    private List<LeftoverOffer> leftoverOffers = [];

    // The app whose leftovers are on show, and the run that lists them again when the protected folders change.
    private InstalledApp? leftoversOf;
    private CancellationTokenSource? leftoversRefresh;

    private void ClearLeftovers()
    {
        leftoversRefresh?.Cancel();
        leftoversOf = null;
        leftoverOffers = [];
        LeftoverTable.Clear();
        LeftoverTable.Visibility = Visibility.Collapsed;
        MoveLeftoversButton.Visibility = Visibility.Collapsed;
        LeftoverMessage.Text = string.Empty;
        UpdateMoveControls();
    }

    // The app is gone: look up its leftovers off the UI thread. The matcher is given every entry the source lists (hidden ones
    // included, plus the Store packages) and the other apps that vanished since the view opened.
    private async Task OfferLeftoversAsync(InstalledApp gone, CancellationToken token, int? noticeContext = null)
    {
        LeftoverMessage.Text = Text.LeftoversOf(gone.DisplayName);
        try
        {
            var context = await CurrentContextAsync(token);
            var known = knownApps.ToList();
            IReadOnlyList<InstalledApp> latestOwners = [];
            var offers = await Task.Run(async () =>
            {
                var owners = UninstallInventory.ReadOwners(new RegistryUninstallSource());
                latestOwners = owners;
                var justRemoved = known.Where(app => !SameApp(app, gone) && !StillListed(app, owners)).ToList();
                var result = new List<LeftoverOffer>();
                foreach (var candidate in LeftoverMatcher.Match(gone, owners, justRemoved, context))
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(await MeasureCandidateAsync(candidate, token).ConfigureAwait(false));
                }

                return result;
            }, token);

            RememberApps(latestOwners);
            if (token.IsCancellationRequested)
            {
                return;
            }

            ShowLeftovers(gone, offers);
            if (noticeContext is { } contextId)
                await ShowUninstallResultAsync(new UninstallNotice(gone, contextId, offers.Count == 0 ? Text.UninstallNoLeftovers : Text.UninstallFoundLeftovers(offers.Count)));
        }
        catch (OperationCanceledException)
        {
            // Another app was selected or the view was left: the offer is for the previous one.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (token.IsCancellationRequested) return;
            LeftoverMessage.Text = Text.LeftoversReadFailed;
            if (noticeContext is { } contextId)
                await ShowUninstallResultAsync(new UninstallNotice(gone, contextId, Text.UninstallSearchFailed));
        }
    }

    // Adds what the inventory lists and the window has not seen yet; nothing is ever removed (UI thread).
    private void RememberApps(IReadOnlyList<InstalledApp> owners)
    {
        foreach (var app in owners)
        {
            if (!knownApps.Any(k => SameApp(k, app)))
            {
                knownApps.Add(app);
            }
        }
    }

    private static bool SameApp(InstalledApp a, InstalledApp b) =>
        ReferenceEquals(a, b)
        || (a.Entry is { } x && b.Entry is { } y && x.KeyName == y.KeyName && x.Scope == y.Scope && x.View == y.View)
        || (a.Entry is null && b.Entry is null && a.Package is { } p && b.Package is { } q && p.FullName == q.FullName);

    private static bool StillListed(InstalledApp app, IReadOnlyList<InstalledApp> owners) =>
        app.Entry is { } entry
            ? owners.Any(o => o.Entry is { } e && e.KeyName == entry.KeyName && e.Scope == entry.Scope && e.View == entry.View)
            : app.Package is { } package && owners.Any(o => o.Package is { } p && p.FullName == package.FullName);

    private static async Task<LeftoverOffer> MeasureCandidateAsync(LeftoverCandidate candidate, CancellationToken token)
    {
        try
        {
            var measured = await new Scanner().ScanAsync(candidate.Path, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return measured.Cancelled || measured.Root.Stamp is null
                ? new LeftoverOffer(candidate, null, Text.LeftoverNotMeasured)
                : new LeftoverOffer(candidate, measured.Root, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new LeftoverOffer(candidate, null, Text.LeftoverNotMeasured);
        }
    }

    // The folders in Settings changed: the candidates are worked out again (the matcher marks one inside or holding a protected
    // folder as shown, not selectable), so that none looks movable while the gate would refuse it. The old rows leave at once;
    // the new ones come back unticked.
    private void RefreshLeftovers()
    {
        if (leftoversOf is not { } gone)
        {
            return;
        }

        leftoversRefresh?.Cancel();
        var cancel = new CancellationTokenSource();
        leftoversRefresh = cancel;
        leftoverOffers = [];
        LeftoverTable.Clear();
        UpdateMoveControls();
        _ = OfferLeftoversAsync(gone, cancel.Token);
    }

    private void ShowLeftovers(InstalledApp gone, List<LeftoverOffer> offers)
    {
        leftoversOf = gone;
        leftoverOffers = offers;
        LeftoverTable.Clear();
        foreach (var offer in offers)
        {
            AddLeftoverRow(offer);
        }

        LeftoverMessage.Text = offers.Count == 0 ? Text.NoLeftoversFound : Text.LeftoversOf(gone.DisplayName);
        LeftoverTable.Visibility = Visibility.Visible;
        MoveLeftoversButton.Visibility = Visibility.Visible;
        UpdateMoveControls();
    }

    // One row: Move (none ticked), Folder, Size and Note. A candidate that may not be selected has a disabled box and says why in the
    // Note column.
    private void AddLeftoverRow(LeftoverOffer offer)
    {
        var size = offer.Node is { } node ? ByteUnits.Format(node.AllocatedBytes) : Text.Unknown;
        var note = offer.Candidate.Note ?? offer.Note;
        var (tickCell, box) = LeanTable.TickCell("LeftoverItem", offer.Selectable);
        box.Checked += OnLeftoverTickChanged;
        box.Unchecked += OnLeftoverTickChanged;
        LeftoverTable.AddRow("LeftoverRow", offer,
        [
            tickCell,
            LeanTable.FolderLabel(offer.Candidate.Path),
            LeanTable.SizeLabel(size, offer.Node?.AllocatedBytes ?? -1),
            note is null ? LeanTable.Empty() : LeanTable.Label(note),
        ], tick: box, displayOnly: !offer.Selectable);
    }

    private void OnLeftoverTickChanged(object sender, RoutedEventArgs e) => UpdateMoveControls();

    private List<LeftoverOffer> TickedLeftovers() =>
        LeftoverTable.Rows.Where(r => r.IsTicked).Select(r => r.Tag).OfType<LeftoverOffer>().Where(o => o.Selectable).ToList();

    private void OnMoveLeftoversClick(object sender, RoutedEventArgs e)
    {
        if (MoveBlocked)
        {
            return;
        }

        var items = TickedLeftovers().Select(o => new SessionItem(o.Node!, null)).ToList();

        // The module's roots are exactly the selectable candidates the matcher produced and that were offered.
        var offered = leftoverOffers.Where(o => o.Selectable).Select(o => o.Candidate.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (items.Count > 0)
        {
            _ = BeginMoveAsync(LeanModule.Uninstall, items, offered);
        }
    }

    // A moved leftover leaves the list; the rest stay, still unticked or as the user left them.
    private void PruneLeftovers(HashSet<string> trashedPaths)
    {
        if (leftoverOffers.Count == 0)
        {
            return;
        }

        leftoverOffers = leftoverOffers.Where(o => !IsAtOrBelowAny(o.Candidate.Path, trashedPaths)).ToList();
        LeftoverTable.RemoveWhere(r => r.Tag is LeftoverOffer offer && IsAtOrBelowAny(offer.Candidate.Path, trashedPaths));

        if (leftoverOffers.Count == 0)
        {
            LeftoverMessage.Text = Text.NoLeftoversFound;
        }
    }
}
