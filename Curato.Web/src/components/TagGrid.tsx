import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { FavoriteCategoryDto, MediaItemDto } from '../api'
import { bumpImageVersion, rotateMediaItem, thumbnailUrl } from '../api'
import { FavoriteCategoryMenu } from './FavoriteCategoryMenu'
import { formatTakenAt } from '../format'
import { Lightbox } from './Lightbox'
import { Slideshow } from './Slideshow'
import { useThumbMinWidth } from '../thumbSize'
import { useUse24HourTime } from '../timeFormat'

// Mirrors LibraryGrid's sizing — kept in sync manually since the two grids'
// block shapes differ enough (this one has no trip/event headers) that
// sharing one component would need more indirection than the duplication
// it'd save. The minimum itself is a user preference now (thumbSize.ts).
const GAP = 6

export function TagGrid({
  items,
  libraryPath,
  onDelete,
  onTakenAtChanged,
  onResetTakenAt,
  onRotated,
  favoriteCategories,
  onSetFavoriteCategory,
}: {
  items: MediaItemDto[]
  libraryPath: string
  onDelete: (mediaItemId: number) => void
  onTakenAtChanged: (mediaItemId: number, takenAt: string) => void
  onResetTakenAt?: (mediaItemId: number) => Promise<void>
  onRotated: (mediaItemId: number) => void
  /** All favorite categories, for the star button's picker. Omit (along with onSetFavoriteCategory) to hide the star entirely — matches Lightbox's identical optional pattern. */
  favoriteCategories?: FavoriteCategoryDto[]
  onSetFavoriteCategory?: (mediaItemId: number, tagId: number | null) => void
}) {
  const scrollRef = useRef<HTMLDivElement>(null)
  // null = not yet measured — see LibraryGrid's identical fix for why this
  // never falls back to a guessed column count anymore.
  const [containerWidth, setContainerWidth] = useState<number | null>(null)
  const [lightbox, setLightbox] = useState<number | null>(null)
  const [slideshow, setSlideshow] = useState<number | null>(null)
  // See LibraryGrid's identical star-click/picker pattern.
  const [favoritePickerItem, setFavoritePickerItem] = useState<MediaItemDto | null>(null)
  function handleStarClick(item: MediaItemDto) {
    if (!favoriteCategories || !onSetFavoriteCategory) return
    if (favoriteCategories.length === 1) {
      const only = favoriteCategories[0]
      onSetFavoriteCategory(item.id, item.favoriteCategoryId === only.id ? null : only.id)
      return
    }
    setFavoritePickerItem(item)
  }
  // See LibraryGrid's identical hover-to-rotate feature for why this is a
  // ref, not state, and how it's non-destructive (a saved Edit row only).
  const hoveredPhotoRef = useRef<MediaItemDto | null>(null)
  const [, forceRerenderForRotate] = useState(0)
  // See LibraryGrid's identical requestedIdsRef — without it, isScrolling
  // being a blanket "some scroll is happening" flag suspended every tile
  // including ones already showing a loaded thumbnail, flickering them out
  // and back in on even a small scroll.
  const requestedIdsRef = useRef<Set<number>>(new Set())
  const use24Hour = useUse24HourTime()
  const thumbMinWidth = useThumbMinWidth()

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

  const columnCount = containerWidth !== null ? Math.max(1, Math.floor((containerWidth + GAP) / (thumbMinWidth + GAP))) : 1
  const thumbSize = containerWidth !== null ? (containerWidth - (columnCount - 1) * GAP) / columnCount : thumbMinWidth
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
                {row.map((item, i) => {
                  // See LibraryGrid's ThumbTile for why this reads/writes the
                  // ref synchronously during render rather than in an effect.
                  // oxlint-disable-next-line react/refs
                  const alreadyRequested = requestedIdsRef.current.has(item.id)
                  const shouldLoad = !virtualizer.isScrolling || alreadyRequested
                  // oxlint-disable-next-line react/refs
                  if (shouldLoad) requestedIdsRef.current.add(item.id)

                  return (
                  <div
                    className="thumb"
                    key={item.id}
                    title={`${item.relativePath}\n${formatTakenAt(item.takenAt, use24Hour)}`}
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
                        .thumb background. Also gated by requestedIdsRef (see
                        its declaration above) so an already-loaded tile
                        never flickers away just because some scroll,
                        anywhere, is currently in progress. */}
                    {shouldLoad && (
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
                      <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6h16z" />
                      </svg>
                    </button>
                    {favoriteCategories && favoriteCategories.length > 0 && (
                      <button
                        type="button"
                        className={`thumb-favorite ${item.favoriteCategoryId !== null ? 'is-set' : ''}`}
                        title="Favorite category"
                        onClick={(e) => {
                          e.stopPropagation()
                          handleStarClick(item)
                        }}
                      >
                        <svg
                          viewBox="0 0 24 24"
                          width="11"
                          height="11"
                          fill={item.favoriteCategoryId !== null ? 'currentColor' : 'none'}
                          stroke="currentColor"
                          strokeWidth="2.5"
                          strokeLinejoin="round"
                        >
                          <path d="M12 .587l3.668 7.568 8.332 1.151-6.064 5.828 1.48 8.279-7.416-3.967-7.416 3.967 1.481-8.279-6.064-5.828 8.332-1.151z" />
                        </svg>
                      </button>
                    )}
                  </div>
                  )
                })}
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
          onResetTakenAt={onResetTakenAt}
          onRotated={onRotated}
          onStartSlideshow={() => {
            setSlideshow(lightbox)
            setLightbox(null)
          }}
          onDelete={onDelete}
          favoriteCategories={favoriteCategories}
          onSetFavoriteCategory={onSetFavoriteCategory}
        />
      )}

      {slideshow !== null && (
        <Slideshow items={items} startIndex={slideshow} libraryPath={libraryPath} onClose={() => setSlideshow(null)} />
      )}

      {favoritePickerItem && (
        <div className="event-picker-overlay" onClick={() => setFavoritePickerItem(null)}>
          <FavoriteCategoryMenu
            categories={favoriteCategories ?? []}
            currentId={favoritePickerItem.favoriteCategoryId}
            onPick={(tagId) => {
              onSetFavoriteCategory?.(favoritePickerItem.id, tagId)
              setFavoritePickerItem(null)
            }}
            onCancel={() => setFavoritePickerItem(null)}
          />
        </div>
      )}
    </div>
  )
}
