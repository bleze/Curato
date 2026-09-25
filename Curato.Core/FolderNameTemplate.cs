namespace Curato.Core;

/// <summary>
/// PLAN.md §3, Step 4 — renders the default folder-naming template for
/// CuratoManaged Events/Trips. Only ever applies to CuratoManaged content;
/// Adopted folders keep whatever name they already had (§3, Step 0) and
/// never go through this.
/// </summary>
public static class FolderNameTemplate
{
    /// <summary>
    /// An Event's own folder name, e.g. "2026-07-11 Evening Bonfire". The
    /// title is sanitized (FolderNameSanitizer), not validated — a title
    /// lives freely in the database with no filesystem restrictions, so
    /// whatever isn't safe there is silently converted at the one place it
    /// actually becomes a real folder name, rather than being rejected
    /// earlier at the database layer for a rule that doesn't apply to it yet.
    /// </summary>
    public static string RenderEventFolderName(Event @event) =>
        $"{@event.StartAt:yyyy-MM-dd} {FolderNameSanitizer.Sanitize(@event.Title)}";

    /// <summary>A TripGroup's own folder name, e.g. "2026-07-10 to 2026-07-14 Summer Vacation" — see RenderEventFolderName for why the title is sanitized here, not validated earlier.</summary>
    public static string RenderTripFolderName(TripGroup trip) =>
        $"{FormatDateRange(trip.StartDate, trip.EndDate)} {FolderNameSanitizer.Sanitize(trip.Title)}";

    /// <summary>
    /// The Event's full path relative to the library root: `{year}/{date}
    /// {title}` when standalone, or nested under its trip's own path (no
    /// year repeated a second time one level down) when it belongs to one.
    /// </summary>
    public static string RenderEventPath(Event @event, TripGroup? trip) =>
        trip is null
            ? $"{@event.StartAt:yyyy}/{RenderEventFolderName(@event)}"
            : $"{RenderTripPath(trip)}/{RenderEventFolderName(@event)}";

    /// <summary>The TripGroup's full path relative to the library root: `{year}/{dateRange} {tripTitle}`.</summary>
    public static string RenderTripPath(TripGroup trip) =>
        $"{trip.StartDate:yyyy}/{RenderTripFolderName(trip)}";

    private static string FormatDateRange(DateOnly start, DateOnly end) =>
        start == end ? start.ToString("yyyy-MM-dd") : $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}";
}
