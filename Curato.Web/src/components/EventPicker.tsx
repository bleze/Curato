export interface EventSummary {
  id: number
  title: string
  startAt: string
  endAt: string
  /** Already organized into a real on-disk folder — refused as a move/merge target (see LibraryRepository.MoveMediaItemToEvent/MergeEventInto). */
  isOrganized: boolean
  /** Non-null when already grouped into a Trip — unused by the picker itself, but shared with LibraryGrid's selection-bar logic (e.g. offering "Remove from Trip" only when the selection includes one). */
  tripGroupId: number | null
}

/** 0 when referenceMs falls inside [startAt, endAt]; otherwise the gap to whichever boundary is closer. */
function distanceFromRange(referenceMs: number, startAt: string, endAt: string): number {
  const start = new Date(startAt).getTime()
  const end = new Date(endAt).getTime()
  if (referenceMs >= start && referenceMs <= end) return 0
  return referenceMs < start ? start - referenceMs : referenceMs - end
}

/**
 * Searchable Event picker shared by the Lightbox's "Move to Event" button,
 * each grid thumbnail's own Move icon, and an Event header's "Merge into…"
 * action. Sorted by closeness to referenceTakenAt rather than plain
 * chronological/recency order — confirmed live as what's actually wanted:
 * the whole reason to move something is usually a wrong split or a wrong
 * date (e.g. photos someone else shared, often mis-dated in iCloud), so the
 * event you're looking for is almost always nearby in time, not just recent.
 */
export function EventPicker({
  allEvents,
  referenceTakenAt,
  excludeEventIds,
  search,
  onSearchChange,
  onPick,
  onCancel,
  allowNewEvent = true,
  allowOrganizedTargets = false,
}: {
  allEvents: EventSummary[]
  referenceTakenAt: string
  /** Never offered as a target — typically the item's own current Event, or every Event already selected for a merge. */
  excludeEventIds?: number[]
  search: string
  onSearchChange: (value: string) => void
  onPick: (targetEventId: number | null) => void
  onCancel: () => void
  /** Hide the "+ move to a new Event" option — meaningless for a whole-Event merge, where there's nothing to merge a new empty Event into. */
  allowNewEvent?: boolean
  /** A single item's Move can target an already-organized Event (the file physically relocates there); a whole-Event Merge still can't. */
  allowOrganizedTargets?: boolean
}) {
  const query = search.trim().toLowerCase()
  const candidates = excludeEventIds ? allEvents.filter((e) => !excludeEventIds.includes(e.id)) : allEvents
  const matches = query ? candidates.filter((e) => e.title.toLowerCase().includes(query)) : candidates

  const referenceMs = new Date(referenceTakenAt).getTime()
  const sorted = [...matches].sort(
    (a, b) => distanceFromRange(referenceMs, a.startAt, a.endAt) - distanceFromRange(referenceMs, b.startAt, b.endAt),
  )

  return (
    <div className="move-to-event-picker" onClick={(e) => e.stopPropagation()}>
      <input
        type="text"
        placeholder="Search events by title…"
        value={search}
        onChange={(e) => onSearchChange(e.target.value)}
        autoFocus
      />
      <ul>
        {allowNewEvent && (
          <li>
            <button type="button" onClick={() => onPick(null)}>
              + Move to a new Event
            </button>
          </li>
        )}
        {sorted.map((e) => (
          <li key={e.id}>
            <button
              type="button"
              disabled={e.isOrganized && !allowOrganizedTargets}
              onClick={() => onPick(e.id)}
              title={e.isOrganized && !allowOrganizedTargets ? "Already organized into a real folder — can't merge into it here" : undefined}
            >
              {e.title} <span className="move-to-event-date">{new Date(e.startAt).toLocaleDateString()}</span>
            </button>
          </li>
        ))}
        {sorted.length === 0 && <li className="move-to-event-empty">No matching events</li>}
      </ul>
      <button type="button" className="move-to-event-cancel" onClick={onCancel}>
        Cancel
      </button>
    </div>
  )
}
