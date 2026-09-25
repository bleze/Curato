import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { MediaItemDto } from '../api'
import { bumpImageVersion, rotateMediaItem, thumbnailUrl } from '../api'
import { Lightbox } from './Lightbox'

// Mirrors LibraryGrid's sizing — kept in sync manually since the two grids'
// block shapes differ enough (this one has no trip/event headers) that
// sharing one component would need more indirection than the duplication
// it'd save.
const THUMB_MIN_WIDTH = 120
const GAP = 6

export function TagGrid({
  items,
  libraryPath,
  onDelete,
  onTakenAtChanged,
  onRotated,
}: {
  items: MediaItemDto[]
  libraryPath: string
  onDelete: (mediaItemId: number) => void
  onTakenAtChanged: (mediaItemId: number, takenAt: string) => void
  onRotated: (mediaItemId: number) => void
}) {
  const scrollRef = useRef<HTMLDivElement>(null)
  // null = not yet measured — see LibraryGrid's identical fix for why this
  // never falls back to a guessed column count anymore.
  const [containerWidth, setContainerWidth] = useState<number | null>(null)
  const [lightbox, setLightbox] = useState<number | null>(null)
  // See LibraryGrid's identical hover-to-rotate feature for why this is a
  // ref, not state, and how it's non-destructive (a saved Edit row only).
  const hoveredPhotoRef = useRef<MediaItemDto | null>(null)
  const [, forceRerenderForRotate] = useState(0)

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
    const observer = new ResizeObserver((entries) => setContainerWidth(entries[0].contentRect.width))
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  const columnCount = containerWidth !== null ? Math.max(1, Math.floor((containerWidth + GAP) / (THUMB_MIN_WIDTH + GAP))) : 1
  const thumbSize = containerWidth !== null ? (containerWidth - (columnCount - 1) * GAP) / columnCount : THUMB_MIN_WIDTH
  const rowHeight = thumbSize + GAP

  const rows = useMemo(() => {
    const result: MediaItemDto[][] = []
    for (let i = 0; i < items.length; i += columnCount) {
      result.push(items.slice(i, i + columnCount))
    }
    return result
  }, [items, columnCount])

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    getItemKey: (index) => rows[index]?.[0]?.id ?? index,
    estimateSize: () => rowHeight,
    overscan: 6,
  })

  if (containerWidth === null) {
    return <div className="library-scroll" ref={scrollRef} tabIndex={0} />
  }

  if (items.length === 0) {
    return <p>This album is empty.</p>
  }

  return (
    <div className="library-scroll" ref={scrollRef} tabIndex={0}>
      <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
        {virtualizer.getVirtualItems().map((virtualRow) => {
          const row = rows[virtualRow.index]
          return (
            <div
              key={row[0].id}
              data-index={virtualRow.index}
              // No measureElement here at all: every row is a uniform
              // thumb-row whose height we compute exactly from the
              // container's real measured width, so there's nothing to
              // correct. See LibraryGrid's identical reasoning — letting
              // TanStack Virtual "correct" an already-exact estimate just
              // introduces sub-pixel drift that desyncs the native
              // scrollbar's drag handle from the mouse mid-drag.
              style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                transform: `translateY(${virtualRow.start}px)`,
              }}
            >
              <div className="thumb-grid" style={{ gridTemplateColumns: `repeat(${columnCount}, 1fr)` }}>
                {row.map((item, i) => (
                  <div
                    className="thumb"
                    key={item.id}
                    title={item.relativePath}
                    onMouseEnter={() => {
                      hoveredPhotoRef.current = item.kind === 'Photo' ? item : null
                    }}
                    onMouseLeave={() => {
                      hoveredPhotoRef.current = null
                    }}
                  >
                    {/* See LibraryGrid's identical isScrolling suspension —
                        without it a scrollbar drag across a large distance
                        floods the browser's per-origin connection cap with
                        requests for every row scrolled past on the way,
                        starving the rows actually left on screen once the
                        drag stops. No <img> at all while suspended, rather
                        than one with no src — that still renders Chrome's
                        broken-image glyph and border instead of the plain
                        .thumb background. */}
                    {!virtualizer.isScrolling && (
                      <img
                        src={thumbnailUrl(libraryPath, item.relativePath, item.id)}
                        alt=""
                        loading="lazy"
                        onClick={() => setLightbox(virtualRow.index * columnCount + i)}
                        onError={(e) => {
                          e.currentTarget.replaceWith(
                            Object.assign(document.createElement('div'), {
                              className: 'thumb-fallback',
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
                    <button
                      type="button"
                      className="thumb-delete"
                      title="Move to Recycle Bin"
                      onClick={() => onDelete(item.id)}
                    >
                      &times;
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )
        })}
      </div>

      {lightbox !== null && (
        <Lightbox
          items={items}
          index={lightbox}
          libraryPath={libraryPath}
          onClose={() => setLightbox(null)}
          onNavigate={setLightbox}
          onTakenAtChanged={onTakenAtChanged}
          onRotated={onRotated}
        />
      )}
    </div>
  )
}
