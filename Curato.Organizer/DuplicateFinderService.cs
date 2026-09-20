using Curato.Data;
using Curato.Media;

namespace Curato.Organizer;

public sealed record DuplicateGroup(IReadOnlyList<long> MediaItemIds, long SuggestedKeepId);

public sealed record DuplicateFinderResult(IReadOnlyList<DuplicateGroup> Groups);

/// <summary>
/// PLAN.md §7 — a separate, manually-triggered action, never run
/// automatically during scan/organize. Two-tier detection: MediaItems
/// already share (FileSizeBytes, QuickHash) from being grouped during scan
/// (§3), so only files that collide there ever pay for a full-file
/// <see cref="ContentHasher"/> read — computed once and cached on the
/// MediaItem row, never re-hashed on a later run. Only a genuine
/// (FileSizeBytes, QuickHash, ContentHash) match across all three is ever
/// reported: astronomically unlikely for two different files, so this never
/// wrongly flags a real difference as a duplicate.
///
/// Resolving *what to do* about a group is the caller's job — this only
/// finds and suggests; nothing is ever deleted here. Confirmed removals are
/// expected to go through the Recycle Bin (§5), same as any other deletion.
/// </summary>
public sealed class DuplicateFinderService(LibraryDatabase database, LibraryQueryService query, LibraryRepository repository)
{
    public DuplicateFinderResult FindDuplicates()
    {
        var libraryRoot = database.LibraryRoot;
        var groups = new List<DuplicateGroup>();

        var quickHashGroups = query.GetAllMediaItems()
            .GroupBy(item => (item.FileSizeBytes, item.QuickHash))
            .Where(g => g.Count() > 1);

        foreach (var quickHashGroup in quickHashGroups)
        {
            var withContentHash = new List<Core.MediaItem>();
            foreach (var item in quickHashGroup)
            {
                if (item.ContentHash is null)
                {
                    var fullPath = Path.Combine(libraryRoot, item.RelativePath);
                    if (!File.Exists(fullPath))
                    {
                        continue; // stale DB row for a file that's since moved/vanished outside Curato
                    }

                    item.ContentHash = ContentHasher.ComputeContentHash(fullPath);
                    repository.SetContentHash(item.Id, item.ContentHash);
                }

                withContentHash.Add(item);
            }

            foreach (var contentHashGroup in withContentHash.GroupBy(item => item.ContentHash))
            {
                var members = contentHashGroup.ToList();
                if (members.Count < 2)
                {
                    continue;
                }

                // Byte-identical files can't differ in resolution/duration,
                // so the only real tie-breaker left is which copy was
                // imported first (§7's "original import folder" criterion).
                var suggestedKeep = members.OrderBy(m => m.ImportedAt).ThenBy(m => m.Id).First();
                groups.Add(new DuplicateGroup(
                    members.OrderBy(m => m.Id).Select(m => m.Id).ToList(), suggestedKeep.Id));
            }
        }

        return new DuplicateFinderResult(groups);
    }
}
