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
const EVENT_HEADER_HEIGHT = 54
const TRIP_HEADER_HEIGHT = 60

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
  const [containerWidth, setContainerWidth] = useState(0)
  const [lightbox, setLightbox] = useState<{ items: MediaItemDto[]; index: number } | null>(null)

  useLayoutEffect(() => {
    const el = scrollRef.current
    if (!el) return
    // Measure synchronously before paint so the very first render already
    // uses the real column count instead of a placeholder guess — avoids a
    // pointless blocks-array rebuild (and virtualizer cache churn) a moment
    // after mount purely because ResizeObserver's first callback is async.
    setContainerWidth(el.getBoundingClientRect().width)
    const observer = new ResizeObserver((entries) => setContainerWidth(entries[0].contentRect.width))
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  const columnCount = containerWidth > 0 ? Math.max(1, Math.floor((containerWidth + GAP) / (THUMB_MIN_WIDTH + GAP))) : 4
  const thumbSize = containerWidth > 0 ? (containerWidth - (columnCount - 1) * GAP) / columnCount : THUMB_MIN_WIDTH
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
      if (block.type === 'trip-header') return TRIP_HEADER_HEIGHT
      if (block.type === 'event-header') return EVENT_HEADER_HEIGHT
      return rowHeight
    },
    overscan: 6,
  })

  if (blocks.length === 0) {
    return <p>No media found in this folder.</p>
  }

  return (
    <div className="library-scroll-wrapper">
      <div className="library-scroll" ref={scrollRef}>
      <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
        {virtualizer.getVirtualItems().map((virtualRow) => {
          const block = blocks[virtualRow.index]
          return (
            <div
              key={block.key}
              data-index={virtualRow.index}
              ref={virtualizer.measureElement}
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
