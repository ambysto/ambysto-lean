using System;
using System.Collections.Generic;
using Ambysto.Lean.Core.Measure;
using Ambysto.Lean.Core.Rules;

namespace Ambysto.Lean.Core.Trash;

/// <summary>One item to move: the scanned node (by id; docs/spec/02 "the UI passes ids, never paths") and, for a
/// duplicate, the copy that is kept.</summary>
public sealed record SessionItem(ScanNode Node, ScanNode? KeptCopy);

/// <summary>One confirmation (docs/spec/05 <c>sessionId</c>): the items the user approved, all of one module.</summary>
public sealed class DeletionSession
{
    /// <summary>A session of <paramref name="items"/> from <paramref name="module"/>, with a new session id.</summary>
    /// <exception cref="ArgumentException">An item is not a scanned node (id 0), appears twice (the same path), or (for duplicates) has
    /// no kept copy, or keeps itself.</exception>
    public DeletionSession(LeanModule module, IReadOnlyList<SessionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // By path, not by scan id: the leftovers of one uninstall come from one scan each, so their ids repeat.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Node);
            if (item.Node.Id == 0)
            {
                throw new ArgumentException("only scanned items can be moved (an item has no scan id)", nameof(items));
            }

            if (!seen.Add(item.Node.Path))
            {
                throw new ArgumentException("an item appears twice in one session", nameof(items));
            }

            if (module == LeanModule.Duplicates && (item.KeptCopy is null || ReferenceEquals(item.KeptCopy, item.Node) || item.KeptCopy.Id == item.Node.Id))
            {
                throw new ArgumentException("a duplicate is moved only with the copy that is kept, which is another copy", nameof(items));
            }
        }

        Module = module;
        Items = [.. items];
        Id = Guid.NewGuid().ToString("N");
    }

    /// <summary>The session id the journal records.</summary>
    public string Id { get; }

    /// <summary>The module.</summary>
    public LeanModule Module { get; }

    /// <summary>The items, in the order they are moved.</summary>
    public IReadOnlyList<SessionItem> Items { get; }
}
