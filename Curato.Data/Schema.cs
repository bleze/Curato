namespace Curato.Data;

/// <summary>
/// Phase 1 subset of PLAN.md §2's data model — MediaItem/Event/TripGroup only
/// for now; Tag/Edit/DeletedItem/BackupDestination/Person/Face land with their
/// respective features. `CREATE TABLE IF NOT EXISTS` throughout since this
/// runs on every startup, not via a separate migration step yet.
///
/// Every foreign key into MediaItem carries `ON DELETE CASCADE` (or, for
/// MediaItem's own self-reference, `ON DELETE SET NULL` — see
/// <see cref="ColumnMigrations"/>) — confirmed live that relying on
/// application code to remember every referencing table before deleting a
/// MediaItem is fragile: only <c>EventMediaItem</c> was ever cleaned up in
/// <c>RecycleBinRepository.RemoveMediaItem</c>, so deleting any item with a
/// Tag, Edit, Face, or Picasa-import marker failed with a raw SQLite
/// "FOREIGN KEY constraint failed". `RemoveMediaItem` still deletes each
/// child row explicitly rather than relying on CASCADE alone, since
/// `CREATE TABLE IF NOT EXISTS` can't retroactively add this clause to a
/// table an existing library.db already created without a full rebuild —
/// the explicit deletes are what actually protects an existing database;
/// CASCADE is what protects the *next* table someone adds here without
/// remembering to update that method too.
/// </summary>
internal static class Schema
{
    public const string CreateTables = """
        CREATE TABLE IF NOT EXISTS MediaItem (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RelativePath TEXT NOT NULL UNIQUE,
            Kind TEXT NOT NULL,
            FileSizeBytes INTEGER NOT NULL,
            QuickHash TEXT NOT NULL,
            ContentHash TEXT NULL,
            TakenAt TEXT NOT NULL,
            TakenAtSource TEXT NOT NULL,
            Width INTEGER NULL,
            Height INTEGER NULL,
            DurationMs INTEGER NULL,
            ImportedAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_MediaItem_TakenAt ON MediaItem (TakenAt);
        CREATE INDEX IF NOT EXISTS IX_MediaItem_QuickHash ON MediaItem (FileSizeBytes, QuickHash);

        CREATE TABLE IF NOT EXISTS TripGroup (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            StartDate TEXT NOT NULL,
            EndDate TEXT NOT NULL,
            Title TEXT NOT NULL,
            TitleIsAuto INTEGER NOT NULL,
            FolderPath TEXT NULL,
            NamingMode TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Event (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            StartAt TEXT NOT NULL,
            EndAt TEXT NOT NULL,
            Title TEXT NOT NULL,
            TitleIsAuto INTEGER NOT NULL,
            FolderPath TEXT NULL,
            TripGroupId INTEGER NULL REFERENCES TripGroup (Id),
            NamingMode TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS EventMediaItem (
            EventId INTEGER NOT NULL REFERENCES Event (Id) ON DELETE CASCADE,
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem (Id) ON DELETE CASCADE,
            SortOrder INTEGER NOT NULL,
            PRIMARY KEY (EventId, MediaItemId)
        );

        -- The undo log for Organize (PLAN.md §4): every file move it ever
        -- performs is recorded here before/after paths, per batch, so a
        -- run can be reverted. Restoring "undo" as a user-facing action is
        -- not built yet - this only persists the data for it.
        CREATE TABLE IF NOT EXISTS OrganizeBatch (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            StartedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS OrganizeMove (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BatchId INTEGER NOT NULL REFERENCES OrganizeBatch (Id) ON DELETE CASCADE,
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem (Id) ON DELETE CASCADE,
            OldRelativePath TEXT NOT NULL,
            NewRelativePath TEXT NOT NULL
        );

        -- PLAN.md §5, Recycle Bin. Deleting a MediaItem removes its row (and
        -- its EventMediaItem links) and moves the physical file into
        -- `.curato/RecycleBin/<TrashRelativePath>` rather than deleting it -
        -- everything the Recycle Bin UI needs to show/restore/purge it lives
        -- here instead, since the MediaItem row itself is gone. Restoring
        -- just puts the file back at OriginalRelativePath and lets the next
        -- scan re-discover it, rather than trying to re-create its exact
        -- former Event/Trip membership.
        -- PLAN.md §9. A Tag *is* an Album — Picasa import (§8) lands star
        -- ratings and Picasa albums here directly, same mechanism either way.
        CREATE TABLE IF NOT EXISTS Tag (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE,
            IsFavoriteShortcut INTEGER NOT NULL,
            SortOrder INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS MediaItemTag (
            TagId INTEGER NOT NULL REFERENCES Tag (Id) ON DELETE CASCADE,
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem (Id) ON DELETE CASCADE,
            AddedAt TEXT NOT NULL,
            ManualSortOrder INTEGER NULL,
            PRIMARY KEY (TagId, MediaItemId)
        );

        -- PLAN.md §10. Non-destructive; never touches the original file's
        -- bytes. Picasa import (§8) lands rotate/crop here with Source =
        -- 'Picasa' — inert metadata until §10's render-time pipeline exists.
        CREATE TABLE IF NOT EXISTS Edit (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem (Id) ON DELETE CASCADE,
            OperationType TEXT NOT NULL,
            Parameters TEXT NOT NULL,
            PipelineOrder INTEGER NOT NULL,
            Source TEXT NOT NULL
        );

        -- PLAN.md §14 (built early for Picasa import, §8, to land into).
        CREATE TABLE IF NOT EXISTS Person (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            RepresentativeFaceId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS Face (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem (Id) ON DELETE CASCADE,
            PersonId INTEGER NULL REFERENCES Person (Id),
            BoundingBoxX INTEGER NOT NULL,
            BoundingBoxY INTEGER NOT NULL,
            BoundingBoxWidth INTEGER NOT NULL,
            BoundingBoxHeight INTEGER NOT NULL,
            Embedding BLOB NULL,
            Confidence REAL NULL,
            Source TEXT NOT NULL,
            Ignored INTEGER NOT NULL
        );

        -- Explicit idempotency marker for Picasa import (§8) — a photo whose
        -- only Picasa data was e.g. `star=yes` leaves no Face/Edit row behind,
        -- so "already imported" can't be inferred from those tables alone.
        CREATE TABLE IF NOT EXISTS PicasaImportedMediaItem (
            MediaItemId INTEGER PRIMARY KEY REFERENCES MediaItem (Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS DeletedItem (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            OriginalRelativePath TEXT NOT NULL,
            TrashRelativePath TEXT NOT NULL,
            Kind TEXT NOT NULL,
            DeletedAt TEXT NOT NULL,
            PurgeAt TEXT NOT NULL,
            Source TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Additive columns for tables that already shipped, applied by
    /// <see cref="LibraryDatabase.EnsureSchema"/> only when missing (checked
    /// via `PRAGMA table_info`, since this SQLite build doesn't support
    /// `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`) — the closest thing this
    /// project has to a migration list until a real one exists.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column, string Definition)> ColumnMigrations =
    [
        // SET NULL, not CASCADE: this is MediaItem's own self-reference (a
        // photo pointing at its paired Live Photo video), so cascading
        // would delete the *photo* if its video were ever removed on its
        // own — the photo is a perfectly good standalone item and should
        // survive losing its motion-clip pairing, not disappear with it.
        ("MediaItem", "LivePhotoVideoItemId", "INTEGER NULL REFERENCES MediaItem (Id) ON DELETE SET NULL")
    ];
}
