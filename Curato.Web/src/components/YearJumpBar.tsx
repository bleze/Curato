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
 *
 * An Adopted folder (e.g. a themed folder mixed in alongside the main
 * timeline) can put a year's content at more than one place in scroll order,
 * so `markers` isn't strictly chronological — confirmed live, this produced
 * duplicate, out-of-order year buttons (e.g. "2001 2003 2001 2002"). Only
 * one button per year is useful here, so this keeps the first (topmost)
 * occurrence of each year and displays buttons in calendar order rather
 * than raw scroll order.
 */
export function YearJumpBar({
  markers,
  beforeDigitalScansFraction,
  onJump,
}: {
  markers: DateMarker[]
  /** Scroll fraction of the first Event/Trip still needing its dates fixed (see FolderTitleParser.IsBeforeDigitalScansFolder), or null if none. Rendered as a leading "Before" button since that content has no trustworthy year of its own to put on a year button. */
  beforeDigitalScansFraction: number | null
  /** Fraction (0-1) of the grid's scrollable range to jump to — same callback LibraryGrid already wires into DateScrubber. */
  onJump: (fraction: number) => void
}) {
  const firstFractionByYear = new Map<string, number>()
  for (const marker of markers) {
    if (marker.isMajor && !firstFractionByYear.has(marker.label)) {
      firstFractionByYear.set(marker.label, marker.heightFraction)
    }
  }
  const years = [...firstFractionByYear.entries()].sort(([a], [b]) => Number(a) - Number(b))

  if (years.length < 2 && beforeDigitalScansFraction === null) {
    return null
  }

  return (
    <div className="year-jump-bar">
      {beforeDigitalScansFraction !== null && (
        <button
          type="button"
          className="year-jump-bar-before"
          title="Jump to undated/mis-dated scans still needing their dates fixed"
          onClick={() => onJump(beforeDigitalScansFraction)}
        >
          Before
        </button>
      )}
      {years.map(([label, heightFraction]) => (
        <button key={label} type="button" title={`Jump to ${label}`} onClick={() => onJump(heightFraction)}>
          {label}
        </button>
      ))}
    </div>
  )
}
