using Curato.Core;
using Curato.Data;
using Curato.Media;
using Curato.Picasa;

namespace Curato.Organizer;

public sealed record PicasaImportResult(
    int IniFilesFound,
    bool ContactsFileFound,
    bool AlbumsFileFound,
    int PhotosMatched,
    int PhotosWithNewData,
    int FacesImported,
    int PeopleMatched,
    int EditsImported,
    int TagsApplied);

/// <summary>
/// PLAN.md §8 — opportunistic, one-time, opt-in import of legacy Picasa data
/// found under a library root. Never touches an original file's bytes:
/// rotate/crop become inert <see cref="Edit"/> rows (§10's render-time
/// pipeline doesn't exist yet, so these have no visible effect until it
/// does), and faces/albums/star ratings land as ordinary Face/Tag rows.
///
/// Idempotent per MediaItem via <see cref="PicasaImportRepository.HasPicasaDataImported"/>
/// - safe to run again after a re-scan finds new files without re-importing
/// (or duplicating) data for photos already covered by a previous run.
///
/// Deliberately skips the `filters=` key entirely (see
/// <c>PicasaPhotoMetadataParser</c>) - that's the least-documented part of the
/// reverse-engineered format, and guessing wrong there risks fabricating an
/// edit that Picasa never actually applied.
/// </summary>
public sealed class PicasaImportService(LibraryDatabase database, LibraryQueryService query, PicasaImportRepository repository)
{
    private const string FavoriteTagName = "Favorite";

    public PicasaImportResult Import()
    {
        var libraryRoot = database.LibraryRoot;
        var iniFiles = FindFiles(libraryRoot, "Picasa.ini", ".picasa.ini");
        var contactsFile = FindFiles(libraryRoot, "contacts.xml").FirstOrDefault();
        var albumsFile = FindFiles(libraryRoot, "Picasa2Albums.ini").FirstOrDefault();

        var contacts = contactsFile is not null
            ? PicasaContactsParser.Parse(File.ReadAllText(contactsFile))
            : new Dictionary<string, string>();

        var albums = albumsFile is not null
            ? PicasaAlbumsParser.Parse(File.ReadAllText(albumsFile))
            : new Dictionary<string, string>();

        var mediaByRelativePath = query.GetAllMediaItems()
            .ToDictionary(m => m.RelativePath, m => m, StringComparer.OrdinalIgnoreCase);

        var photosMatched = 0;
        var photosWithNewData = 0;
        var facesImported = 0;
        var matchedPeople = new HashSet<long>();
        var editsImported = 0;
        var tagsApplied = 0;
        long? favoriteTagId = null;

        foreach (var iniFile in iniFiles)
        {
            var folderRelative = Path.GetRelativePath(libraryRoot, Path.GetDirectoryName(iniFile)!).Replace('\\', '/');
            var sections = PicasaIniParser.Parse(File.ReadAllText(iniFile));

            foreach (var section in sections)
            {
                var relativePath = folderRelative == "." ? section.Name : $"{folderRelative}/{section.Name}";
                if (!mediaByRelativePath.TryGetValue(relativePath, out var mediaItem))
                {
                    continue; // not a photo Curato knows about — an album marker, folder-level settings, etc.
                }

                photosMatched++;
                if (repository.HasBeenImported(mediaItem.Id))
                {
                    continue; // a previous import run already covered this item
                }

                var metadata = PicasaPhotoMetadataParser.Parse(section.Values);
                var importedSomething = false;

                if (metadata.Star)
                {
                    favoriteTagId ??= repository.GetOrCreateTag(FavoriteTagName, isFavoriteShortcut: true);
                    repository.AddMediaItemToTag(favoriteTagId.Value, mediaItem.Id);
                    tagsApplied++;
                    importedSomething = true;
                }

                foreach (var albumId in metadata.AlbumIds)
                {
                    if (!albums.TryGetValue(albumId, out var albumName))
                    {
                        continue;
                    }

                    var tagId = repository.GetOrCreateTag(albumName);
                    repository.AddMediaItemToTag(tagId, mediaItem.Id);
                    tagsApplied++;
                    importedSomething = true;
                }

                if (metadata.RotationDegrees is { } degrees and not 0)
                {
                    repository.InsertEdit(
                        mediaItem.Id, EditOperationType.Rotate, $$"""{"degrees":{{degrees}}}""",
                        pipelineOrder: 0, RecordSource.Picasa);
                    editsImported++;
                    importedSomething = true;
                }

                var needsDimensions = metadata.CropRectHex is not null || metadata.Faces.Count > 0;
                var dimensions = needsDimensions
                    ? ImageDimensionReader.TryReadDimensions(Path.Combine(libraryRoot, mediaItem.RelativePath))
                    : null;

                if (metadata.CropRectHex is { } cropHex && dimensions is { } cropDims &&
                    PicasaRect.ToPixelRect(cropHex, cropDims.Width, cropDims.Height) is { } cropRect)
                {
                    repository.InsertEdit(
                        mediaItem.Id, EditOperationType.Crop,
                        $$"""{"x":{{cropRect.X}},"y":{{cropRect.Y}},"width":{{cropRect.Width}},"height":{{cropRect.Height}}}""",
                        pipelineOrder: 1, RecordSource.Picasa);
                    editsImported++;
                    importedSomething = true;
                }

                if (dimensions is { } faceDims)
                {
                    foreach (var face in metadata.Faces)
                    {
                        if (PicasaRect.ToPixelRect(face.RectHex, faceDims.Width, faceDims.Height) is not { } faceRect)
                        {
                            continue;
                        }

                        long? personId = contacts.TryGetValue(face.ContactId, out var name)
                            ? repository.GetOrCreatePerson(name)
                            : null;

                        repository.InsertFace(mediaItem.Id, personId, faceRect, RecordSource.Picasa);
                        facesImported++;
                        importedSomething = true;
                        if (personId is { } pid)
                        {
                            matchedPeople.Add(pid);
                        }
                    }
                }

                repository.MarkImported(mediaItem.Id);
                if (importedSomething)
                {
                    photosWithNewData++;
                }
            }
        }

        return new PicasaImportResult(
            IniFilesFound: iniFiles.Count,
            ContactsFileFound: contactsFile is not null,
            AlbumsFileFound: albumsFile is not null,
            PhotosMatched: photosMatched,
            PhotosWithNewData: photosWithNewData,
            FacesImported: facesImported,
            PeopleMatched: matchedPeople.Count,
            EditsImported: editsImported,
            TagsApplied: tagsApplied);
    }

    private static List<string> FindFiles(string root, params string[] fileNames)
    {
        var matches = new List<string>();
        foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var normalized = filePath.Replace('\\', '/');
            if (normalized.Contains("/.curato/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(filePath);
            if (fileNames.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(filePath);
            }
        }

        return matches;
    }
}
