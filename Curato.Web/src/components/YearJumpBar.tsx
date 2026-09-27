import type { DateMarker } from './DateScrubber'

/**
 * A row of year buttons above the grid — for a library spanning enough years
 * that DateScrubber's own right-edge rail (16px wide, every year's label
 * crammed into whatever fraction of it that year's content happens to
 * occupy) starts overlapping its own labels and its thumb shrinks to a
 * sliver (confirmed live with 25+ years of photos). Reuses the exact same
 * `isMajor` markers (one per year, positioned at that year's first item) and
 * jump mechanism DateScrubber already has — this is just a second, coarser
 * way to reach the same targets, not a replacement for it (the rail still
 * reaches individual months).
 */
export function YearJumpBar({
  markers,
  onJump,
}: {
  markers: DateMarker[]
  /** Fraction (0-1) of the grid's scrollable range to jump to — same callback LibraryGrid already wires into DateScrubber. */
  onJump: (fraction: number) => void
}) {
  const years = markers.filter((m) => m.isMajor)
  if (years.length < 2) {
    return null
  }

  return (
    <div className="year-jump-bar">
      {years.map((marker) => (
        <button
          key={marker.label}
          type="button"
          title={`Jump to ${marker.label}`}
          onClick={() => onJump(marker.heightFraction)}
        >
          {marker.label}
        </button>
      ))}
    </div>
  )
}
