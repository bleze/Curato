namespace Curato.Data;

/// <summary>
/// Phase 1 subset of PLAN.md §2's data model — MediaItem/Event/TripGroup only
/// for now; Tag/Edit/DeletedItem/BackupDestination/Person/Face land with their
/// respective features. `CREATE TABLE IF NOT EXISTS` throughout since this
/// runs on every startup, not via a separate migration step yet.
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
            EventId INTEGER NOT NULL REFERENCES Event (Id),
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem (Id),
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
            BatchId INTEGER NOT NULL REFERENCES OrganizeBatch (Id),
            MediaItemId INTEGER NOT NULL REFERENCES MediaItem (Id),
            OldRelativePath TEXT NOT NULL,
            NewRelativePath TEXT NOT NULL
        );
        """;
}
