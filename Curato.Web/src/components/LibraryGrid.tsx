import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { EventDto, LibraryDto, MediaItemDto, TripGroupDto } from '../api'
import { bumpImageVersion, rotateMediaItem, thumbnailUrl } from '../api'
import type { DateMarker } from './DateScrubber'
import { DateScrubber } from './DateScrubber'
import { Lightbox } from './Lightbox'

// Mirrors the old CSS grid's `repeat(auto-fill, minmax(120px, 1fr))` — kept
// in JS now so row height can be computed for virtualization instead of
// letting the browser lay out an unbounded number of rows at once.
const THUMB_MIN_WIDTH = 120
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

function buildDateMarkers(blocks: Block[]): DateMarker[] {
  const markers: DateMarker[] = []
  let lastMonthKey = ''
  let lastYear = -1

  blocks.forEach((block, index) => {
    const dateStr = blockDate(block)
    if (!dateStr) return

    const date = new Date(dateStr)
    const monthKey = `${date.getFullYear()}-${date.getMonth()}`
    if (monthKey === lastMonthKey) return
    lastMonthKey = monthKey

    const isMajor = date.getFullYear() !== lastYear
    if (isMajor) lastYear = date.getFullYear()

    markers.push({
      blockIndex: index,
      label: String(date.getFullYear()),
      tooltipLabel: date.toLocaleDateString(undefined, { year: 'numeric', month: 'short' }),
      isMajor,
    })
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

  const columnCount = containerWidth !== null ? Math.max(1, Math.floor((containerWidth + GAP) / (THUMB_MIN_WIDTH + GAP))) : 1
  const thumbSize = containerWidth !== null ? (containerWidth - (columnCount - 1) * GAP) / columnCount : THUMB_MIN_WIDTH
  const rowHeight = thumbSize + GAP

  const blocks = useMemo(() => buildBlocks(library, columnCount), [library, columnCount])
  const dateMarkers = useMemo(() => buildDateMarkers(blocks), [blocks])

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
    estimateSize: (index) => {
      const block = blocks[index]
      if (block.type === 'trip-header') {
        return TRIP_HEADER_HEIGHT + (block.trip.proposedFolderPath ? PROPOSED_PATH_HEIGHT : 0)
      }
      if (block.type === 'event-header') {
        return EVENT_HEADER_HEIGHT
      }
      return rowHeight
    },
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
        onJump={(blockIndex) => virtualizer.scrollToIndex(blockIndex, { align: 'start' })}
        rightOffset={scrollbarWidth}
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
  onOpen,
  onDelete,
  onHoverChange,
}: {
  item: MediaItemDto
  libraryPath: string
  onOpen: () => void
  onDelete: (mediaItemId: number) => void
  onHoverChange: (hovering: boolean) => void
}) {
  return (
    <div
      className="thumb"
      title={item.relativePath}
      onMouseEnter={() => onHoverChange(true)}
      onMouseLeave={() => onHoverChange(false)}
    >
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
