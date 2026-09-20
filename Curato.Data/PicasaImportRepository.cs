using Curato.Core;
using Dapper;

namespace Curato.Data;

/// <summary>
/// Persistence for PLAN.md §8's Picasa import. Deciding what a `.picasa.ini`
/// file means is <c>Curato.Picasa</c>'s job (pure parsing) and
/// <c>Curato.Organizer.PicasaImportService</c>'s job (matching parsed data to
/// MediaItems); this only ever writes what it's told and checks what's
/// already there, so re-running the import doesn't duplicate rows.
/// </summary>
public sealed class PicasaImportRepository(LibraryDatabase database)
{
    /// <summary>
    /// Whether Picasa import has already processed this MediaItem — an
    /// explicit marker rather than inferring it from Face/Edit rows existing,
    /// since a photo whose only Picasa data was e.g. `star=yes` leaves no
    /// Face/Edit row behind at all.
    /// </summary>
    public bool HasBeenImported(long mediaItemId)
    {
        using var connection = database.OpenConnection();
        return connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM PicasaImportedMediaItem WHERE MediaItemId = @Id",
            new { Id = mediaItemId }) > 0;
    }

    public void MarkImported(long mediaItemId)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            "INSERT INTO PicasaImportedMediaItem (MediaItemId) VALUES (@Id) ON CONFLICT (MediaItemId) DO NOTHING",
            new { Id = mediaItemId });
    }

    public long GetOrCreatePerson(string name)
    {
        using var connection = database.OpenConnection();
        var existingId = connection.ExecuteScalar<long?>(
            "SELECT Id FROM Person WHERE Name = @Name", new { Name = name });
        if (existingId is { } id)
        {
            return id;
        }

        return connection.ExecuteScalar<long>(
            "INSERT INTO Person (Name, RepresentativeFaceId) VALUES (@Name, NULL) RETURNING Id",
            new { Name = name });
    }

    public long GetOrCreateTag(string name, bool isFavoriteShortcut = false)
    {
        using var connection = database.OpenConnection();
        var existingId = connection.ExecuteScalar<long?>(
            "SELECT Id FROM Tag WHERE Name = @Name", new { Name = name });
        if (existingId is { } id)
        {
            return id;
        }

        var nextSortOrder = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Tag");
        return connection.ExecuteScalar<long>("""
            INSERT INTO Tag (Name, IsFavoriteShortcut, SortOrder)
            VALUES (@Name, @IsFavoriteShortcut, @SortOrder)
            RETURNING Id
            """, new { Name = name, IsFavoriteShortcut = isFavoriteShortcut, SortOrder = nextSortOrder });
    }

    public void AddMediaItemToTag(long tagId, long mediaItemId)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO MediaItemTag (TagId, MediaItemId, AddedAt, ManualSortOrder)
            VALUES (@TagId, @MediaItemId, @AddedAt, NULL)
            ON CONFLICT (TagId, MediaItemId) DO NOTHING
            """, new { TagId = tagId, MediaItemId = mediaItemId, AddedAt = DateTime.UtcNow.ToString("O") });
    }

    public void InsertFace(long mediaItemId, long? personId, PixelRect boundingBox, RecordSource source)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO Face
                (MediaItemId, PersonId, BoundingBoxX, BoundingBoxY, BoundingBoxWidth, BoundingBoxHeight,
                 Embedding, Confidence, Source, Ignored)
            VALUES
                (@MediaItemId, @PersonId, @X, @Y, @Width, @Height, NULL, NULL, @Source, 0)
            """, new
        {
            MediaItemId = mediaItemId,
            PersonId = personId,
            boundingBox.X,
            boundingBox.Y,
            boundingBox.Width,
            boundingBox.Height,
            Source = source.ToString()
        });
    }

    public void InsertEdit(long mediaItemId, EditOperationType operationType, string parametersJson, int pipelineOrder, RecordSource source)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO Edit (MediaItemId, OperationType, Parameters, PipelineOrder, Source)
            VALUES (@MediaItemId, @OperationType, @Parameters, @PipelineOrder, @Source)
            """, new
        {
            MediaItemId = mediaItemId,
            OperationType = operationType.ToString(),
            Parameters = parametersJson,
            PipelineOrder = pipelineOrder,
            Source = source.ToString()
        });
    }
}
