using Curato.Data;

namespace Curato.Organizer;

/// <summary>
/// Finds and removes a leftover iCloud-sync placeholder stub before it can
/// ever be mistaken for a real new file — see
/// <c>Curato.iCloud.iCloudSyncRunner.SeedAlreadyDownloadedStubs</c> for how
/// one gets created (a zero-byte, correctly-sized placeholder at the
/// library root, standing in for something already indexed elsewhere, so
/// icloudpd's own dedup skips re-downloading it) and
/// <c>iCloudSyncRunner.CleanupStubs</c> for the normal happy-path removal
/// right after a sync finishes.
///
/// Originally only run at the start of the *next iCloud sync* — confirmed
/// live as not enough: a sync interrupted between seeding stubs and
/// reaching cleanup (the hosting process killed outright, e.g. during an
/// unrelated restart) can leave stubs sitting at the root indefinitely if
/// the next thing to touch the library is an ordinary Scan rather than
/// another sync — and once a Scan indexes one as a real MediaItem, it looks
/// to the user like a newly-synced photo/video that's mysteriously all
/// zeros. Called from <see cref="ScanService"/> too now, for exactly that
/// reason: self-heals regardless of what triggers the next Scan, not just
/// an iCloud sync.
///
/// A stub is identifiable after the fact without any tracking list: it's a
/// file sitting loose at the root whose name matches an already-indexed
/// MediaItem elsewhere, sized exactly right, but containing nothing but
/// zero bytes — confirmed live that no genuine photo/video file is ever
/// all-zero for its entire length, so this can't misidentify (and delete) a
/// real file a user happened to drop at the root with a colliding
/// name/size.
/// </summary>
public static class OrphanedStubCleanup
{
    public static void CleanupOrphanedStubs(string libraryFolder, LibraryQueryService query)
    {
        var expectedSizeByFileName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.GetAllMediaItems())
        {
            expectedSizeByFileName[Path.GetFileName(item.RelativePath)] = item.FileSizeBytes;
        }

        foreach (var filePath in Directory.EnumerateFiles(libraryFolder))
        {
            if (!expectedSizeByFileName.TryGetValue(Path.GetFileName(filePath), out var expectedSize))
            {
                continue;
            }

            try
            {
                if (new FileInfo(filePath).Length != expectedSize || !IsAllZeroBytes(filePath))
                {
                    continue;
                }

                File.Delete(filePath);
            }
            catch (IOException)
            {
                // Best-effort — try again next time.
            }
        }
    }

    public static bool IsAllZeroBytes(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        Span<byte> buffer = stackalloc byte[8192];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer)) > 0)
        {
            if (buffer[..bytesRead].IndexOfAnyExcept((byte)0) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
