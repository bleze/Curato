using Curato.Core;
using Curato.Data;

namespace Curato.Organizer;

public sealed record DeletedItemDto(
    long Id, string OriginalRelativePath, string Kind, DateTime DeletedAt, DateTime PurgeAt, string Source);

/// <summary>
/// PLAN.md §5 — every deletion in Curato goes through here rather than ever
/// calling File.Delete directly, so a mis-click (or a future Duplicate
/// Finder acting on a bad match) is always recoverable for a grace period.
///
/// Deleting a MediaItem moves its file into
/// <c>&lt;LibraryRoot&gt;/.curato/RecycleBin/&lt;guid&gt;/&lt;filename&gt;</c> and drops the
/// MediaItem row (and its Event membership) entirely; restoring puts the
/// file back at its original path and leaves it for the next scan to
/// re-discover, rather than trying to reconstruct its old Event/Trip
/// membership, which may no longer even make sense. If that was the Event's
/// last remaining item, the now-empty Event (and its TripGroup, if that's
/// now empty too) is removed rather than left behind as a photo-less stub.
///
/// Auto-purge (§5: items older than the retention window get removed
/// automatically) isn't a standing background timer yet - there's no
/// registry of "every library this server knows about" for one to iterate
/// over. Instead, <see cref="PurgeExpired"/> runs opportunistically whenever
/// a caller lists the bin for a given library, which is an honest
/// approximation for a single-library-at-a-time server.
/// </summary>
public sealed class RecycleBinService(LibraryDatabase database, RecycleBinRepository repository)
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    private const string RecycleBinFolderName = "RecycleBin";

    /// <summary>
    /// Deletes one MediaItem — and, if it's the still-frame half of an Apple
    /// Live Photo pair (§3), its paired .MOV motion clip too, so one click on
    /// what looks like a single photo doesn't leave an orphaned sidecar file
    /// behind. The paired video gets its own DeletedItem row (same source),
    /// independently restorable/purgeable.
    /// </summary>
    public DeletedItemDto? DeleteMediaItem(long mediaItemId, string source)
    {
        var mediaItem = repository.GetMediaItemPathAndKind(mediaItemId);
        if (mediaItem is null)
        {
            return null;
        }

        var (id, relativePath, kind, livePhotoVideoItemId, fileSizeBytes, quickHash) = mediaItem.Value;
        var libraryRoot = database.LibraryRoot;
        var sourceFullPath = Path.Combine(libraryRoot, relativePath);

        var trashRelativePath = $".curato/{RecycleBinFolderName}/{Guid.NewGuid():N}/{Path.GetFileName(relativePath)}";
        var trashFullPath = Path.Combine(libraryRoot, trashRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(trashFullPath)!);
        if (File.Exists(sourceFullPath))
        {
            MoveWithRetryForTransientLock(sourceFullPath, trashFullPath);
        }

        var emptiedEvents = repository.RemoveMediaItem(id, fileSizeBytes, quickHash);
        foreach (var emptiedEvent in emptiedEvents)
        {
            FolderCleanup.DeleteIfEmpty(libraryRoot, emptiedEvent.FolderPath);
        }

        if (livePhotoVideoItemId is { } videoId)
        {
            DeleteMediaItem(videoId, source);
        }

        var deletedAt = DateTime.UtcNow;
        var purgeAt = deletedAt + RetentionPeriod;
        var deletedItemId = repository.InsertDeletedItem(
            relativePath, trashRelativePath, kind, deletedAt, purgeAt, source, id);

        return new DeletedItemDto(deletedItemId, relativePath, kind, deletedAt, purgeAt, source);
    }

    /// <summary>
    /// Confirmed live as a real crash, not a hypothetical: deleting a video
    /// right after watching it (e.g. the Lightbox's own Delete-and-advance
    /// flow) can hit a Win32 sharing violation — MediaController's /raw
    /// endpoint streams it via a FileStream (PhysicalFile,
    /// enableRangeProcessing). The Lightbox itself now explicitly pauses and
    /// unloads its &lt;video&gt; element before ever calling this, to abort
    /// that connection client-side as early as possible — but confirmed live
    /// that a short (1.5s) server-side retry window alone wasn't always
    /// enough for the actual socket teardown and FileStream release to catch
    /// up, especially over a network-hosted library, so this backstop is
    /// deliberately generous (several seconds) rather than tuned to the
    /// fastest case. A real, permanently-locked file (e.g. open in another
    /// program) still eventually surfaces as a failure, just later.
    /// </summary>
    private static void MoveWithRetryForTransientLock(string sourceFullPath, string trashFullPath)
    {
        const int maxAttempts = 25;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(sourceFullPath, trashFullPath);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Bulk form of <see cref="DeleteMediaItem"/> — used for "delete this
    /// whole Event" (the caller resolves the Event's own MediaItemIds first,
    /// this just reuses the already-tested single-item path per member so
    /// Live Photo pairing and emptied-Event/Trip cleanup keep working
    /// exactly the same way). An id with nothing left to delete (e.g. a Live
    /// Photo's paired video, already recycled by its still-frame's own call
    /// earlier in the same batch) is silently skipped rather than failing
    /// the rest of the batch.
    /// </summary>
    public IReadOnlyList<DeletedItemDto> DeleteMediaItems(IReadOnlyList<long> mediaItemIds, string source)
    {
        var deleted = new List<DeletedItemDto>();
        foreach (var mediaItemId in mediaItemIds)
        {
            if (DeleteMediaItem(mediaItemId, source) is { } result)
            {
                deleted.Add(result);
            }
        }

        return deleted;
    }

    public IReadOnlyList<DeletedItemDto> ListPendingDeletion()
    {
        PurgeExpired();
        return repository.GetAllDeletedItems()
            .Select(d => new DeletedItemDto(d.Id, d.OriginalRelativePath, d.Kind, d.DeletedAt, d.PurgeAt, d.Source))
            .ToList();
    }

    public string? GetTrashedFileFullPath(long deletedItemId)
    {
        var item = repository.GetDeletedItem(deletedItemId);
        return item is null ? null : Path.Combine(database.LibraryRoot, item.TrashRelativePath);
    }

    /// <summary>
    /// The rotation/crop this item had in the live library — kept around
    /// past its MediaItem row's own deletion for exactly this (see
    /// RemoveMediaItem), so the Recycle Bin's own viewer doesn't show an
    /// un-rotated photo just because it's sitting in the trash. Empty for a
    /// DeletedItem row from before this MediaItemId column existed.
    /// </summary>
    public IReadOnlyList<Edit> GetEditsForDeletedItem(long deletedItemId)
    {
        var item = repository.GetDeletedItem(deletedItemId);
        if (item?.MediaItemId is not { } mediaItemId)
        {
            return [];
        }

        return new LibraryQueryService(database).GetEditsForMediaItemId(mediaItemId);
    }

    /// <returns>false if the DeletedItem row doesn't exist.</returns>
    public bool Restore(long deletedItemId)
    {
        var item = repository.GetDeletedItem(deletedItemId);
        if (item is null)
        {
            return false;
        }

        var libraryRoot = database.LibraryRoot;
        var trashFullPath = Path.Combine(libraryRoot, item.TrashRelativePath);
        var restoredFullPath = Path.Combine(libraryRoot, item.OriginalRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(restoredFullPath)!);
        if (File.Exists(trashFullPath))
        {
            File.Move(trashFullPath, restoredFullPath, overwrite: false);
            TryDeleteTrashContainer(trashFullPath);
        }

        repository.DeleteDeletedItemRow(deletedItemId);
        return true;
    }

    /// <returns>false if the DeletedItem row doesn't exist.</returns>
    public bool DeletePermanently(long deletedItemId)
    {
        var item = repository.GetDeletedItem(deletedItemId);
        if (item is null)
        {
            return false;
        }

        var trashFullPath = Path.Combine(database.LibraryRoot, item.TrashRelativePath);
        if (File.Exists(trashFullPath))
        {
            File.Delete(trashFullPath);
            TryDeleteTrashContainer(trashFullPath);
        }

        repository.DeleteDeletedItemRow(deletedItemId);
        return true;
    }

    public void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var item in repository.GetAllDeletedItems())
        {
            if (item.PurgeAt <= now)
            {
                var trashFullPath = Path.Combine(database.LibraryRoot, item.TrashRelativePath);
                if (File.Exists(trashFullPath))
                {
                    File.Delete(trashFullPath);
                    TryDeleteTrashContainer(trashFullPath);
                }

                repository.DeleteDeletedItemRow(item.Id);
            }
        }
    }

    private static void TryDeleteTrashContainer(string trashFullPath)
    {
        var container = Path.GetDirectoryName(trashFullPath);
        if (container is not null && Directory.Exists(container) && Directory.GetFileSystemEntries(container).Length == 0)
        {
            Directory.Delete(container);
        }
    }

}
