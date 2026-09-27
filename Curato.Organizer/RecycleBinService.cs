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
            File.Move(sourceFullPath, trashFullPath);
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
            relativePath, trashRelativePath, kind, deletedAt, purgeAt, source);

        return new DeletedItemDto(deletedItemId, relativePath, kind, deletedAt, purgeAt, source);
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
