import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { EventDto, LibraryDto, MediaItemDto, TripGroupDto } from '../api'
import { bumpImageVersion, rotateMediaItem, thumbnailUrl } from '../api'
import type { DateMarker } from './DateScrubber'
import { DateScrubber } from './DateScrubber'
import { formatTakenAt } from '../format'
import { Lightbox } from './Lightbox'
import { useThumbMinWidth } from '../thumbSize'
import { useUse24HourTime } from '../timeFormat'

// Mirrors the old CSS grid's `repeat(auto-fill, minmax(120px, 1fr))` — kept
// in JS now so row height can be computed for virtualization instead of
// letting the browser lay out an unbounded number of rows at once. The
// minimum itself is a user preference now (thumbSize.ts), not a constant.
const GAP = 6
// These are exact, not generous guesses — see the getItemKey/estimateSize
// comment below for why headers are never remeasured against reality once
// rendered (that's the scrollbar drag-handle desync bug's real remaining
// cause). Several estimate-vs-reality mismatches have already come from
// this file guessing wrong here, all confirmed live:
// (1) an under-filled EVENT_HEADER_HEIGHT left a dead gap below every
//     non-Organized event's header, and (2) a title or "Will move to" hint
//     that wrapped to a second line made the reserved slot too SHORT,
//     which is worse — it corrupts the next block's position, not just
//     wastes space. Rather than keep guessing a buffer, .editable-title-text
//     and .proposed-path (App.css) are both forced to a single line via
//     `text-overflow: ellipsis` — the actual rendered height of a header is
//     now a true constant, not a function of arbitrary-length content, so
//     these numbers can be tight instead of generous. TripGroup headers
//     still stack `.proposed-path` on its own line below the title (real
//     vertical space, not wasted) rather than cramming it into the same row
//     — its own container is wide enough that this hasn't needed the same
//     right-aligned same-row treatment .event-header got.
// (3) The visual gap under a header has to live *inside* this number, not
//     in CSS margin — every block here is `position: absolute`, placed by
//     summing estimateSize()'s return values, not normal document flow, so
//     a margin between two virtualized siblings is silently ignored
//     (confirmed live: bumping .event-header's margin-bottom from 8px to
//     20px changed nothing on screen). EVENT_HEADER_CONTENT_HEIGHT is the
//     header's real measured height (padding-top + its one text row, no
//     wrap possible per (2) above); EVENT_HEADER_GAP is genuine breathing
//     room added on top of that, big enough that a hovered thumbnail's
//     `transform: scale(1.08)` (App.css) has room to grow into without
//     visually overlapping the "Will move to" pill above it.
const EVENT_HEADER_CONTENT_HEIGHT = 50
const EVENT_HEADER_GAP = 11
const EVENT_HEADER_HEIGHT = EVENT_HEADER_CONTENT_HEIGHT + EVENT_HEADER_GAP
const TRIP_HEADER_HEIGHT = 84
const PROPOSED_PATH_HEIGHT = 54

type Block =
  | { type: 'trip-header'; key: string; trip: TripGroupDto }
  | { type: 'event-header'; key: string; event: EventDto }
  | { type: 'thumb-row'; key: string; event: EventDto; items: MediaItemDto[]; startIndex: number }

// Shared by the virtualizer's own estimateSize and buildDateMarkers's
// cumulative-height math below — both need the exact same per-block height
// to agree, or the date scrubber's markers wouldn't actually line up with
// where their content really sits (which is exactly the bug this fixes:
// markers used to be spaced by counting distinct months rather than by real
// rendered height, so they never matched the native scrollbar next to them,
// which moves by real pixel height like every scrollbar does).
function blockHeight(block: Block, rowHeight: number): number {
  if (block.type === 'trip-header') {
    return TRIP_HEADER_HEIGHT + (block.trip.proposedFolderPath ? PROPOSED_PATH_HEIGHT : 0)
  }
  if (block.type === 'event-header') {
    return EVENT_HEADER_HEIGHT
  }
  return rowHeight
}

function formatRange(startAt: string, endAt: string): string {
  const start = new Date(startAt)
  const end = new Date(endAt)
  const sameDay = start.toDateString() === end.toDateString()
  const dateFmt: Intl.DateTimeFormatOptions = { year: 'numeric', month: 'short', day: 'numeric' }
  return sameDay
    ? start.toLocaleDateString(undefined, dateFmt)
    : `${start.toLocaleDateString(undefined, dateFmt)} – ${end.toLocaleDateString(undefined, dateFmt)}`
}

// Trips and standalone events each arrive pre-sorted from the server, but as
// two separate arrays — merged here into one true chronological timeline
// instead of "every trip, then every standalone event", so scrolling (and
// the date scrubber) actually reflects when things happened.
type TimelineEntry = { date: string } & (
  | { kind: 'trip'; trip: TripGroupDto }
  | { kind: 'event'; event: EventDto }
)

function buildTimeline(library: LibraryDto): TimelineEntry[] {
  const entries: TimelineEntry[] = [
    ...library.trips.map((trip): TimelineEntry => ({ kind: 'trip', date: trip.startDate, trip })),
    ...library.standaloneEvents.map((event): TimelineEntry => ({ kind: 'event', date: event.startAt, event })),
  ]
  entries.sort((a, b) => a.date.localeCompare(b.date))
  return entries
}

function buildBlocks(library: LibraryDto, columnCount: number): Block[] {
  const blocks: Block[] = []

  function pushEvent(event: EventDto) {
    blocks.push({ type: 'event-header', key: `eh-${event.id}`, event })
    for (let i = 0; i < event.items.length; i += columnCount) {
      blocks.push({
        type: 'thumb-row',
        key: `tr-${event.id}-${i}`,
        event,
        items: event.items.slice(i, i + columnCount),
        startIndex: i,
      })
    }
  }

  for (const entry of buildTimeline(library)) {
    if (entry.kind === 'trip') {
      blocks.push({ type: 'trip-header', key: `th-${entry.trip.id}`, trip: entry.trip })
      entry.trip.events.forEach(pushEvent)
    } else {
      pushEvent(entry.event)
    }
  }

  return blocks
}

function blockDate(block: Block): string | null {
  if (block.type === 'trip-header') return block.trip.startDate
  if (block.type === 'event-header') return block.event.startAt
  return block.event.startAt
}

// heightFraction is cumulative rendered height *before* each marker's block,
// as a fraction of the grid's total height — the same quantity a native
// scrollbar's thumb position represents, so the two agree with each other.
// (Confirmed live this needed fixing: marker positions used to come from
// counting distinct months instead, which only matches real proportional
// height when every month happens to have the same number of photos.)
// scrollableRange is (total content height - viewport height) — the same
// quantity a native scrollbar's thumb position is computed against
// (thumbFraction = scrollTop / scrollableRange, never / totalHeight). Using
// plain totalHeight here looked right in isolation (a click landed exactly
// on the real first item of a year) but confirmed live as still wrong in
// context: the native scrollbar's own handle sat measurably away from where
// that same marker's label was drawn, since the two were computed against
// different denominators. Content within the last one viewport's worth of
// height can never have a distinct scrollbar position anyway (you're
// already at max scroll once any of it is visible), so its heightFraction
// is clamped to 1 rather than allowed past it.
function buildDateMarkers(blocks: Block[], rowHeight: number, scrollableRange: number): DateMarker[] {
  const markers: DateMarker[] = []
  let lastMonthKey = ''
  let lastYear = -1
  let cumulativeHeight = 0

  blocks.forEach((block) => {
    const dateStr = blockDate(block)
    if (dateStr) {
      const date = new Date(dateStr)
      const monthKey = `${date.getFullYear()}-${date.getMonth()}`
      if (monthKey !== lastMonthKey) {
        lastMonthKey = monthKey

        const isMajor = date.getFullYear() !== lastYear
        if (isMajor) lastYear = date.getFullYear()

        markers.push({
          heightFraction: scrollableRange > 0 ? Math.min(1, cumulativeHeight / scrollableRange) : 0,
          label: String(date.getFullYear()),
          tooltipLabel: date.toLocaleDateString(undefined, { year: 'numeric', month: 'short' }),
          isMajor,
        })
      }
    }

    cumulativeHeight += blockHeight(block, rowHeight)
  })

  return markers
}

export function LibraryGrid({
  library,
  libraryPath,
  onDelete,
  onTakenAtChanged,
  onRotated,
  onRenameEvent,
  onRenameTrip,
}: {
  library: LibraryDto
  libraryPath: string
  onDelete: (mediaItemId: number) => void
  onTakenAtChanged: (mediaItemId: number, takenAt: string) => void
  onRotated: (mediaItemId: number) => void
  onRenameEvent: (eventId: number, title: string) => void
  onRenameTrip: (tripGroupId: number, title: string) => void
}) {
  const scrollRef = useRef<HTMLDivElement>(null)
  // null = not yet measured. Deliberately never guess a fallback column
  // count here anymore: a guessed value (previously 4) could leak through
  // as visibly huge, pixelated thumbnails whenever a race meant no
  // subsequent resize ever corrected it — reported as intermittent and
  // fixed by a full page reload, both consistent with a timing race rather
  // than a permanently wrong calculation. Waiting for a real, confirmed
  // ResizeObserver measurement before rendering any thumbnails at all
  // removes the wrong state entirely rather than needing to find and patch
  // every path that could produce it.
  const [containerWidth, setContainerWidth] = useState<number | null>(null)
  // The scroll container's own viewport height — needed for the date
  // scrubber's markers/jump math below (buildDateMarkers, onJump) to match
  // the native scrollbar's own thumb-position convention: a thumb's
  // position is scrollTop / (scrollHeight - clientHeight), not
  // scrollTop / scrollHeight. Confirmed live as a real, reproducible bug
  // from omitting the clientHeight term: even with the height-proportional
  // marker math already exactly correct, the native scrollbar's handle sat
  // measurably away from where a marker's label was drawn, since the two
  // were being computed against two different denominators.
  const [containerHeight, setContainerHeight] = useState<number | null>(null)
  // The DateScrubber rail used to sit at a hardcoded `right: 0`, which is
  // exactly where the real native scrollbar also renders — confirmed live
  // (measuring both elements' actual pixel bounds) that they occupied the
  // identical strip, so every drag on what looked like the scrollbar was
  // actually captured by DateScrubber's "jump to nearest year marker"
  // handler instead. That handler only supports a handful of discrete
  // marker positions, so it could never reach the true bottom of a year's
  // content — reported as "you cannot scroll to the bottom." Measuring the
  // browser's actual scrollbar width (0 on some platforms/overlay
  // scrollbars, ~15-17px on Windows) and shifting the rail to sit just to
  // its left gives the real scrollbar its own untouched, fully draggable
  // strip.
  const [scrollbarWidth, setScrollbarWidth] = useState(0)
  const [lightbox, setLightbox] = useState<{ items: MediaItemDto[]; index: number } | null>(null)
  // A ref, not state — this changes on every mouse move between thumbnails,
  // and none of that needs to trigger a re-render of the (potentially huge)
  // virtualized grid; a keydown handler only ever needs its *current* value
  // at the moment L/R is actually pressed.
  const hoveredPhotoRef = useRef<MediaItemDto | null>(null)
  const [, forceRerenderForRotate] = useState(0)
  // Tracks every item whose <img> has ever actually been mounted with a real
  // src — NOT reset on scroll. virtualizer.isScrolling is a blanket flag for
  // "a scroll is happening right now" with no idea which tiles are already
  // loaded vs. genuinely new; suspending every tile by that flag alone made
  // already-visible, already-loaded thumbnails flicker out and back in on
  // even a one-row scroll, since they'd suspend and then immediately
  // re-request. Consulting this set lets a tile that's already been
  // requested once just keep rendering through any later isScrolling spans,
  // so only tiles that have genuinely never been requested before suspend.
  const requestedIdsRef = useRef<Set<number>>(new Set())
  const use24Hour = useUse24HourTime()
  const thumbMinWidth = useThumbMinWidth()

  // Lets you rotate a photo by hovering its grid thumbnail and pressing
  // L/R, without opening the fullscreen viewer first — same non-destructive
  // Edit-row mechanism as the Lightbox toolbar (rotateMediaItem never
  // touches the original file).
  useEffect(() => {
    async function onKeyDown(e: KeyboardEvent) {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return
      if (e.key !== 'l' && e.key !== 'L' && e.key !== 'r' && e.key !== 'R') return

      const hovered = hoveredPhotoRef.current
      if (!hovered) return

      await rotateMediaItem(libraryPath, hovered.id, e.key === 'r' || e.key === 'R' ? 90 : -90)
      bumpImageVersion(hovered.id)
      forceRerenderForRotate((n) => n + 1)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [libraryPath])

  useLayoutEffect(() => {
    const el = scrollRef.current
    if (!el) return
    // No synchronous getBoundingClientRect read here on purpose (there
    // used to be one, to avoid a one-frame delay): it returns the
    // border-box, which — unlike ResizeObserver's contentRect — does NOT
    // exclude a scrollbar's width, only padding. Manually subtracting just
    // the known padding still left it wrong by the scrollbar's width
    // whenever one was actually present, which is most of the time for
    // this view. ResizeObserver's own first callback is the only
    // measurement used now, since it's the one that's actually guaranteed
    // correct (content-box, padding and scrollbar both already excluded).
    const observer = new ResizeObserver((entries) => {
      setContainerWidth(entries[0].contentRect.width)
      setContainerHeight(entries[0].contentRect.height)
      setScrollbarWidth(el.offsetWidth - el.clientWidth)
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  // Kept in sync with .date-scrubber's own CSS width (App.css) — the total
  // gutter this view reserves so thumbnails never render underneath either
  // the scrubber rail or the real scrollbar next to it.
  const DATE_SCRUBBER_WIDTH = 16
  const scrollGutter = scrollbarWidth + DATE_SCRUBBER_WIDTH

  const columnCount = containerWidth !== null ? Math.max(1, Math.floor((containerWidth + GAP) / (thumbMinWidth + GAP))) : 1
  const thumbSize = containerWidth !== null ? (containerWidth - (columnCount - 1) * GAP) / columnCount : thumbMinWidth
  const rowHeight = thumbSize + GAP

  const blocks = useMemo(() => buildBlocks(library, columnCount), [library, columnCount])
  // Computed independently of the virtualizer's own getTotalSize() (even
  // though the two are numerically identical, confirmed live) so this and
  // the scrubber's onJump wiring below share one unambiguous source for the
  // same quantity, rather than each recomputing it its own way.
  const totalContentHeight = useMemo(
    () => blocks.reduce((sum, block) => sum + blockHeight(block, rowHeight), 0),
    [blocks, rowHeight],
  )
  const scrollableRange = containerHeight !== null ? Math.max(0, totalContentHeight - containerHeight) : totalContentHeight
  const dateMarkers = useMemo(
    () => buildDateMarkers(blocks, rowHeight, scrollableRange),
    [blocks, rowHeight, scrollableRange],
  )

  const virtualizer = useVirtualizer({
    count: blocks.length,
    getScrollElement: () => scrollRef.current,
    // Without this, TanStack Virtual's internal size-measurement cache is
    // keyed by raw array index. `blocks` gets rebuilt (different length/
    // shape) whenever `library` changes — e.g. after a delete, which shifts
    // every later row's item-chunking by one — or when `columnCount` first
    // settles from its initial guess to the real measured value. A stale
    // index-keyed cache then gets misapplied to a structurally different
    // block, corrupting both getTotalSize() (the "list ends early" bug) and
    // individual row positioning (the overlapping/offset-labels bug).
    // Keying by the block's own stable id fixes both.
    getItemKey: (index) => blocks[index].key,
    estimateSize: (index) => blockHeight(blocks[index], rowHeight),
    overscan: 6,
  })

  // The scroll container itself must always render (so its ref attaches
  // and ResizeObserver has something to measure) — only its *content* waits
  // for a real measurement.
  if (containerWidth === null) {
    return (
      <div className="library-scroll-wrapper">
        <div
          className="library-scroll library-scroll-with-scrubber"
          ref={scrollRef}
          tabIndex={0}
          style={{ paddingRight: scrollGutter }}
        />
      </div>
    )
  }

  if (blocks.length === 0) {
    return <p>No media found in this folder.</p>
  }

  return (
    <div className="library-scroll-wrapper">
      <div
        className="library-scroll library-scroll-with-scrubber"
        ref={scrollRef}
        tabIndex={0}
        style={{ paddingRight: scrollGutter }}
      >
      <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
        {virtualizer.getVirtualItems().map((virtualRow) => {
          const block = blocks[virtualRow.index]
          return (
            <div
              key={block.key}
              data-index={virtualRow.index}
              // No measureElement for ANY block type now, including headers
              // (previously still remeasured — see EVENT_HEADER_HEIGHT etc.
              // above for why that was the scrollbar drag-handle bug's real
              // remaining cause). getTotalSize() is now fixed the moment
              // blocks are built and never changes during a scroll session.
              style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                transform: `translateY(${virtualRow.start}px)`,
              }}
            >
              {block.type === 'trip-header' && (
                <div className="trip-header-block">
                  <h2>
                    <EditableTitle
                      title={block.trip.title}
                      onRename={(title) => onRenameTrip(block.trip.id, title)}
                    />
                    {block.trip.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
                  </h2>
                  {block.trip.proposedFolderPath && (
                    <p className="proposed-path">Will move to: {block.trip.proposedFolderPath}</p>
                  )}
                </div>
              )}

              {block.type === 'event-header' && (
                <header className="event-header">
                  <div className="event-header-row">
                    <h3>
                      <EditableTitle
                        title={block.event.title}
                        onRename={(title) => onRenameEvent(block.event.id, title)}
                      />
                    </h3>
                    <span className="event-meta">
                      {formatRange(block.event.startAt, block.event.endAt)} &middot;{' '}
                      {block.event.items.length} items
                      {block.event.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
                    </span>
                    {block.event.proposedFolderPath && (
                      <p className="proposed-path" title={`Will move to: ${block.event.proposedFolderPath}`}>
                        Will move to: {block.event.proposedFolderPath}
                      </p>
                    )}
                  </div>
                </header>
              )}

              {block.type === 'thumb-row' && (
                <div className="thumb-grid" style={{ gridTemplateColumns: `repeat(${columnCount}, 1fr)` }}>
                  {block.items.map((item, i) => (
                    <ThumbTile
                      key={item.id}
                      item={item}
                      libraryPath={libraryPath}
                      suspendLoad={virtualizer.isScrolling}
                      requestedIdsRef={requestedIdsRef}
                      use24Hour={use24Hour}
                      onOpen={() => setLightbox({ items: block.event.items, index: block.startIndex + i })}
                      onDelete={onDelete}
                      onHoverChange={(hovering) => {
                        hoveredPhotoRef.current = hovering && item.kind === 'Photo' ? item : null
                      }}
                    />
                  ))}
                </div>
              )}
            </div>
          )
        })}
      </div>
      </div>

      <DateScrubber
        markers={dateMarkers}
        onJump={(fraction) => virtualizer.scrollToOffset(fraction * scrollableRange, { align: 'start' })}
        rightOffset={scrollbarWidth}
        edgeInset={scrollbarWidth}
      />

      {lightbox && (
        <Lightbox
          items={lightbox.items}
          index={lightbox.index}
          libraryPath={libraryPath}
          onClose={() => setLightbox(null)}
          onNavigate={(index) => setLightbox((current) => (current ? { ...current, index } : current))}
          onTakenAtChanged={onTakenAtChanged}
          onRotated={onRotated}
        />
      )}
    </div>
  )
}

// Deliberately stays a single line, matching the h2/h3's normal single-line
// height, whether displaying or editing — LibraryGrid's virtualizer no
// longer remeasures header blocks against reality after they render (see
// the getItemKey/estimateSize comment above), so a taller editing state
// here would silently reintroduce the scrollbar drag-handle desync bug that
// took four attempts to actually fix.
// A title lives freely in the database — no filesystem restrictions apply
// to it here at all. Whatever isn't safe in a real folder name gets
// silently sanitized server-side (Curato.Core.FolderNameSanitizer) at the
// one point it actually becomes one, rather than being rejected this early
// for a rule that doesn't even apply yet (a CuratoManaged Event/Trip might
// not have a folder at all until Organize runs). Empty is still rejected
// here since it's meaningless as a title, database or not.
function validateFolderName(title: string): string | null {
  if (!title) return 'Title cannot be empty.'
  return null
}

function EditableTitle({ title, onRename }: { title: string; onRename: (newTitle: string) => void }) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(title)

  if (!editing) {
    return (
      <>
        <span className="editable-title-text" title={title}>
          {title}
        </span>
        <button
          type="button"
          className="title-rename-button"
          title="Rename"
          aria-label="Rename"
          onClick={() => {
            setDraft(title)
            setEditing(true)
          }}
        >
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M17 3a2.828 2.828 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
          </svg>
        </button>
      </>
    )
  }

  const trimmedDraft = draft.trim()
  const validationError = validateFolderName(trimmedDraft)

  function commit() {
    if (validationError) return // Save is already disabled; belt-and-suspenders against a stray Enter
    setEditing(false)
    if (trimmedDraft !== title) {
      onRename(trimmedDraft)
    }
  }

  return (
    <span className="title-edit" onClick={(e) => e.stopPropagation()}>
      <input
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit()
          if (e.key === 'Escape') setEditing(false)
        }}
        // Validity shown via border color + a native tooltip rather than a
        // separate error line — this input lives in a virtualized row whose
        // height is a hard-coded constant (see EVENT_HEADER_HEIGHT above);
        // adding a second line here would silently reintroduce the
        // scrollbar drag-handle desync bug that took real effort to fix.
        className={validationError ? 'title-edit-input-invalid' : undefined}
        title={validationError ?? undefined}
        autoFocus
      />
      <button type="button" onClick={commit} disabled={!!validationError} title={validationError ?? undefined}>
        Save
      </button>
      <button type="button" onClick={() => setEditing(false)}>
        Cancel
      </button>
    </span>
  )
}

function ThumbTile({
  item,
  libraryPath,
  suspendLoad,
  requestedIdsRef,
  use24Hour,
  onOpen,
  onDelete,
  onHoverChange,
}: {
  item: MediaItemDto
  libraryPath: string
  use24Hour: boolean
  // While the virtualizer reports active scrolling (a scrollbar drag across
  // a large distance fires a real DOM scroll event, and therefore a fresh
  // batch of newly-mounted row/image elements, on every pixel of travel —
  // not just once at the final resting position) every row passed through
  // along the way was mounting an <img> with a real src, so all of them
  // queued a thumbnail request. The browser's ~6-connections-per-origin cap
  // then had to drain that whole backlog of requests for rows the user
  // already scrolled past before it could even start on the rows actually
  // left on screen — confirmed live: an instant, non-dragged scrollTop jump
  // loads its thumbnails in under 40ms, but a real scrollbar drag left the
  // final rows waiting multiple seconds behind that backlog. Omitting `src`
  // while still scrolling (TanStack Virtual's own recommended pattern for
  // this) means only the row(s) still on screen once scrolling actually
  // stops ever ask for an image.
  suspendLoad: boolean
  // See LibraryGrid's declaration — this tile only actually honors
  // suspendLoad the first time it's ever asked to render, so a tile that
  // was already showing a loaded thumbnail before this scroll started never
  // flickers away just because *some* scroll is in progress.
  requestedIdsRef: React.RefObject<Set<number>>
  onOpen: () => void
  onDelete: (mediaItemId: number) => void
  onHoverChange: (hovering: boolean) => void
}) {
  // Deliberately reading/writing the ref synchronously during render rather
  // than in an effect: an effect only runs after the first paint, so an
  // already-seen tile would still flash blank for one frame before the
  // effect discovered that and re-rendered it with the real image — exactly
  // the flicker this exists to prevent. This project has no React Compiler
  // in its build, so there's no memoization to invalidate; the Set mutation
  // is idempotent (a re-render of the same tile just re-adds the same id).
  // oxlint-disable-next-line react/refs
  const alreadyRequested = requestedIdsRef.current.has(item.id)
  const shouldLoad = !suspendLoad || alreadyRequested
  // oxlint-disable-next-line react/refs
  if (shouldLoad) requestedIdsRef.current.add(item.id)

  return (
    <div
      className="thumb"
      title={`${item.relativePath}\n${formatTakenAt(item.takenAt, use24Hour)}`}
      onMouseEnter={() => onHoverChange(true)}
      onMouseLeave={() => onHoverChange(false)}
    >
      {/* No <img> at all while suspended, rather than one with no src —
          an <img> with a missing/empty src still renders Chrome's own
          broken-image glyph and border, which looked far worse than the
          plain-grey .thumb background it now falls back to showing
          instead (confirmed live: reported as an ugly bordered icon in
          the corner of every not-yet-loaded tile while scrolling). */}
      {shouldLoad && (
        <img
          src={thumbnailUrl(libraryPath, item.relativePath, item.id)}
          alt=""
          loading="lazy"
          onClick={onOpen}
          onError={(e) => {
            e.currentTarget.replaceWith(
              Object.assign(document.createElement('div'), {
                className: 'thumb-fallback',
                // A video already shows its own play-icon badge as the "this
                // is a video" signal — "No preview" text underneath it just
                // looked cluttered, so only photos get the text explanation.
                textContent: item.kind === 'Photo' ? 'No preview' : '',
              }),
            )
          }}
        />
      )}
      {item.kind === 'Video' && (
        <div className="video-play-badge" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="32" height="32">
            <circle cx="12" cy="12" r="11" fill="rgba(0,0,0,0.55)" />
            <path d="M9.5 7.5v9l7-4.5z" fill="white" />
          </svg>
        </div>
      )}
      {item.liveVideoRelativePath && <span className="live-badge">LIVE</span>}
      <button type="button" className="thumb-delete" title="Move to Recycle Bin" onClick={() => onDelete(item.id)}>
        &times;
      </button>
    </div>
  )
}
