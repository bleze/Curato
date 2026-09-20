import { useVirtualizer } from '@tanstack/react-virtual'
import { useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { EventDto, LibraryDto, MediaItemDto, TripGroupDto } from '../api'
import { thumbnailUrl } from '../api'
import type { DateMarker } from './DateScrubber'
import { DateScrubber } from './DateScrubber'
import { Lightbox } from './Lightbox'

// Mirrors the old CSS grid's `repeat(auto-fill, minmax(120px, 1fr))` — kept
// in JS now so row height can be computed for virtualization instead of
// letting the browser lay out an unbounded number of rows at once.
const THUMB_MIN_WIDTH = 120
const GAP = 6
// These are deliberately generous, worst-case estimates rather than
// tightly-measured ones — see the getItemKey/estimateSize comment below for
// why headers are no longer remeasured against reality at all. Title text
// (h3, arbitrary length) and the "Will move to" hint (a folder path,
// arbitrary length) can each wrap to a second line at narrower column
// counts, so the estimate has to comfortably cover a 2-line wrap for both,
// not just the common single-line case.
const EVENT_HEADER_HEIGHT = 78
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
}: {
  library: LibraryDto
  libraryPath: string
  onDelete: (mediaItemId: number) => void
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
  const [lightbox, setLightbox] = useState<{ items: MediaItemDto[]; index: number } | null>(null)

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
    const observer = new ResizeObserver((entries) => setContainerWidth(entries[0].contentRect.width))
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

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
        return EVENT_HEADER_HEIGHT + (block.event.proposedFolderPath ? PROPOSED_PATH_HEIGHT : 0)
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
        <div className="library-scroll library-scroll-with-scrubber" ref={scrollRef} tabIndex={0} />
      </div>
    )
  }

  if (blocks.length === 0) {
    return <p>No media found in this folder.</p>
  }

  return (
    <div className="library-scroll-wrapper">
      <div className="library-scroll library-scroll-with-scrubber" ref={scrollRef} tabIndex={0}>
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
                    {block.trip.title}
                    {block.trip.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
                  </h2>
                  {block.trip.proposedFolderPath && (
                    <p className="proposed-path">Will move to: {block.trip.proposedFolderPath}</p>
                  )}
                </div>
              )}

              {block.type === 'event-header' && (
                <header className="event-header">
                  <h3>{block.event.title}</h3>
                  <span className="event-meta">
                    {formatRange(block.event.startAt, block.event.endAt)} &middot;{' '}
                    {block.event.items.length} items
                    {block.event.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
                  </span>
                  {block.event.proposedFolderPath && (
                    <p className="proposed-path">Will move to: {block.event.proposedFolderPath}</p>
                  )}
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
      />

      {lightbox && (
        <Lightbox
          items={lightbox.items}
          index={lightbox.index}
          libraryPath={libraryPath}
          onClose={() => setLightbox(null)}
          onNavigate={(index) => setLightbox((current) => (current ? { ...current, index } : current))}
        />
      )}
    </div>
  )
}

function ThumbTile({
  item,
  libraryPath,
  onOpen,
  onDelete,
}: {
  item: MediaItemDto
  libraryPath: string
  onOpen: () => void
  onDelete: (mediaItemId: number) => void
}) {
  return (
    <div className="thumb" title={item.relativePath}>
      {item.kind === 'Photo' ? (
        <img
          src={thumbnailUrl(libraryPath, item.relativePath)}
          alt=""
          loading="lazy"
          onClick={onOpen}
          onError={(e) => {
            e.currentTarget.replaceWith(
              Object.assign(document.createElement('div'), {
                className: 'thumb-fallback',
                textContent: 'No preview',
              }),
            )
          }}
        />
      ) : (
        <div className="thumb-fallback" onClick={onOpen}>
          Video
        </div>
      )}
      {item.liveVideoRelativePath && <span className="live-badge">LIVE</span>}
      <button type="button" className="thumb-delete" title="Move to Recycle Bin" onClick={() => onDelete(item.id)}>
        &times;
      </button>
    </div>
  )
}
