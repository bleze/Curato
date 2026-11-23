import { useCallback, useEffect, useRef, useState } from 'react'
import type { FavoriteCategoryDto, MediaItemDto, ResetTakenAtResult } from '../api'
import { bumpImageVersion, fullImageUrl, rawFileUrl, rotateMediaItem } from '../api'
import type { EventSummary } from './EventPicker'
import { EventPicker } from './EventPicker'
import { FavoriteCategoryMenu } from './FavoriteCategoryMenu'
import { formatTakenAt, toDatetimeLocalValue } from '../format'
import { useUse24HourTime } from '../timeFormat'

const MIN_ZOOM = 1
const MAX_ZOOM = 4
const ZOOM_STEP = 0.4

export function Lightbox({
  items,
  index,
  libraryPath,
  allEvents,
  currentEventId,
  onClose,
  onNavigate,
  onTakenAtChanged,
  onResetTakenAt,
  onRotated,
  onMoveToEvent,
  onStartSlideshow,
  onDelete,
  favoriteCategories,
  onSetFavoriteCategory,
}: {
  items: MediaItemDto[]
  index: number
  libraryPath: string
  /** Every Event in the library (standalone or nested under a Trip) — powers the "Move to Event" picker below. Omit (along with currentEventId/onMoveToEvent) to hide that feature entirely — e.g. TagGrid's items can span many different Events, so there's no single "current Event" to exclude. */
  allEvents?: EventSummary[]
  /** The Event these items are currently shown under — excluded from the picker (moving "to" the event it's already in is a no-op). */
  currentEventId?: number
  onClose: () => void
  onNavigate: (index: number) => void
  onTakenAtChanged: (mediaItemId: number, takenAt: string) => void
  /** Re-reads the real Exif/video date straight off the file and uses that instead — the recovery path for a bad manual edit, since the file itself is never touched and always has the ground truth. Omit to hide the button (e.g. wherever the caller hasn't wired it up yet). */
  onResetTakenAt?: (mediaItemId: number) => Promise<ResetTakenAtResult | null>
  onRotated: (mediaItemId: number) => void
  onMoveToEvent?: (mediaItemId: number, targetEventId: number | null) => void
  /** Starts a fullscreen Slideshow over these same items, from the photo/video currently open — closes this Lightbox first (see Slideshow.tsx). */
  onStartSlideshow: () => void
  /** Moves the current item to the Recycle Bin (behind a confirm step — see confirmingDelete below) and closes the Lightbox. */
  onDelete: (mediaItemId: number) => void
  /** All favorite categories, for the star button's picker. Omit (along with onSetFavoriteCategory) to hide the star entirely — e.g. wherever a MediaItemDto doesn't carry favoriteCategoryId yet. */
  favoriteCategories?: FavoriteCategoryDto[]
  onSetFavoriteCategory?: (mediaItemId: number, tagId: number | null) => void
}) {
  const item = items[index]
  const use24Hour = useUse24HourTime()
  const [playingLive, setPlayingLive] = useState(false)
  const [zoom, setZoom] = useState(1)
  const [pan, setPan] = useState({ x: 0, y: 0 })
  const [dragging, setDragging] = useState(false)
  const [editingDate, setEditingDate] = useState(false)
  const [draftDate, setDraftDate] = useState('')
  const [rotating, setRotating] = useState(false)
  const [movingToEvent, setMovingToEvent] = useState(false)
  const [moveSearch, setMoveSearch] = useState('')
  const [pickingFavorite, setPickingFavorite] = useState(false)
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [resettingTakenAt, setResettingTakenAt] = useState(false)
  // bumpImageVersion (api.ts) is keyed by mediaItemId, shared across every
  // view of this item (grid thumbnails included) — this local counter just
  // forces Lightbox itself to re-render and pick up the new version after a
  // rotate, since bumping a plain module-level Map doesn't trigger React on
  // its own. Deliberately NOT reset on navigation (an earlier local-only
  // cache-bust counter used to do that): resetting it would make the <img>
  // go back to a version-less URL and show the browser's stale pre-rotation
  // cached response again if you navigated away from a rotated photo and
  // back to it.
  const [, forceRerender] = useState(0)
  const dragStart = useRef<{ x: number; y: number; panX: number; panY: number; moved: boolean } | null>(null)
  // See handleConfirmDelete/the [items]-watching effect below: the
  // optimistic navigation right after confirming a delete uses the current,
  // still-pre-delete `items` array — correct immediately, but one item
  // short of where the real post-delete array will put that same photo
  // once the parent's data actually refreshes (everything after the
  // deleted item shifts down by one index). This remembers which photo
  // (by id, not index) to re-settle on once that happens.
  const pendingAdvanceRef = useRef<{ id: number; countBeforeDelete: number } | null>(null)
  // Shared by both <video> branches below (the current item's own video, and
  // a Live Photo's paired motion clip) — they're mutually exclusive, never
  // both rendered at once, so one ref covers whichever is actually mounted.
  const videoRef = useRef<HTMLVideoElement>(null)
  const cancelDeleteButtonRef = useRef<HTMLButtonElement>(null)
  const confirmDeleteButtonRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    setPlayingLive(false)
    setZoom(1)
    setPan({ x: 0, y: 0 })
    setEditingDate(false)
    setMovingToEvent(false)
    setMoveSearch('')
    setPickingFavorite(false)
    setConfirmingDelete(false)
  }, [index])

  const handleRotate = useCallback(
    async (deltaDegrees: number) => {
      if (rotating) return
      setRotating(true)
      try {
        await rotateMediaItem(libraryPath, item.id, deltaDegrees)
        bumpImageVersion(item.id)
        forceRerender((n) => n + 1)
        onRotated(item.id)
      } finally {
        setRotating(false)
      }
    },
    [rotating, libraryPath, item.id, onRotated],
  )

  // Same single/multi-category logic as the grid tile's own star — see
  // LibraryGrid's handleStarClick.
  function handleFavoriteClick() {
    if (!favoriteCategories || !onSetFavoriteCategory || favoriteCategories.length === 0) return
    if (favoriteCategories.length === 1) {
      const only = favoriteCategories[0]
      onSetFavoriteCategory(item.id, item.favoriteCategoryId === only.id ? null : only.id)
      return
    }
    setPickingFavorite(true)
  }

  function handleConfirmDelete() {
    // Confirmed live as a real crash, not just a hypothetical: the server's
    // delete can race this video's own still-open streaming connection
    // (RecycleBinService's File.Move hit a Windows sharing violation even
    // after a short server-side retry, meaning the lock genuinely outlived
    // that window rather than just trailing off instantly) — changing
    // `src` via the optimistic navigate below doesn't necessarily make the
    // browser abort the in-flight request for the OLD one immediately.
    // Explicitly pausing and clearing the element first forces that abort
    // synchronously, before the delete request is even sent.
    if (videoRef.current) {
      videoRef.current.pause()
      videoRef.current.removeAttribute('src')
      videoRef.current.load()
    }

    // Progresses to the next photo/video instead of closing the viewer —
    // confirmed live as the wanted behavior: reviewing and culling a batch
    // shouldn't mean re-opening the grid and re-picking the next item after
    // every single delete. Falls back to the previous item if this was the
    // last one, and only closes outright if it was the only item left.
    const nextIndex = index < items.length - 1 ? index + 1 : index > 0 ? index - 1 : null
    if (nextIndex !== null) {
      pendingAdvanceRef.current = { id: items[nextIndex].id, countBeforeDelete: items.length }
      onNavigate(nextIndex)
    } else {
      onClose()
    }
    onDelete(item.id)
    setConfirmingDelete(false)
  }

  // Corrects the optimistic index above once the parent's `items` prop
  // actually reflects the deletion (recognized by its length having
  // dropped below what it was when Delete was confirmed, not just a new
  // array reference with the same still-pre-delete content) — re-finds the
  // intended photo by id, since removing an earlier item shifts every
  // later one down by one index.
  useEffect(() => {
    const pending = pendingAdvanceRef.current
    if (!pending || items.length >= pending.countBeforeDelete) return
    pendingAdvanceRef.current = null
    const freshIndex = items.findIndex((i) => i.id === pending.id)
    if (freshIndex >= 0 && freshIndex !== index) {
      onNavigate(freshIndex)
    }
  }, [items, index, onNavigate])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      // Don't hijack L/R (or anything else) while a text field has focus —
      // the date-edit input is the one place that's reachable from here.
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return

      if (confirmingDelete) {
        // Confirmed live as a real bug: without this, Left/Right kept
        // navigating the photo *underneath* the dialog instead of moving
        // between its own two buttons, and Escape closed the whole viewer
        // instead of just dismissing the confirm step. The dialog owns the
        // keyboard entirely while it's open.
        if (e.key === 'Escape') setConfirmingDelete(false)
        if (e.key === 'ArrowLeft') {
          e.preventDefault()
          cancelDeleteButtonRef.current?.focus()
        }
        if (e.key === 'ArrowRight') {
          e.preventDefault()
          confirmDeleteButtonRef.current?.focus()
        }
        return
      }

      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowRight' && index < items.length - 1) onNavigate(index + 1)
      if (e.key === 'ArrowLeft' && index > 0) onNavigate(index - 1)
      if ((e.key === 'l' || e.key === 'L') && item.kind === 'Photo') handleRotate(-90)
      if ((e.key === 'r' || e.key === 'R') && item.kind === 'Photo') handleRotate(90)
      // Same confirm step as clicking the Delete button — never deletes
      // outright, so an accidental press can't silently move a file.
      if (e.key === 'Delete') setConfirmingDelete(true)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
    // handleRotate is memoized (useCallback) specifically so it can sit in
    // this dependency array without re-subscribing the listener on every
    // render, while still always being the version that closes over the
    // current `rotating` value — omitting it would let this effect call a
    // stale closure whose in-flight guard never saw a real rotate happen.
  }, [index, items.length, onClose, onNavigate, item.kind, handleRotate, confirmingDelete])

  if (!item) return null

  // The Event title when it's known (more meaningful than a raw filename,
  // and already how the rest of the app refers to a group of photos) —
  // falls back to the bare filename alone, never the full relative path:
  // the caption already shows the real date/time, so the folder structure
  // underneath it is redundant, just extra text to read past.
  const currentEventTitle = allEvents?.find((e) => e.id === currentEventId)?.title
  const bareFileName = item.relativePath.split('/').pop()
  const captionLabel = currentEventTitle ?? bareFileName

  function clampZoom(z: number) {
    return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, z))
  }

  function handleWheel(e: React.WheelEvent) {
    e.preventDefault()
    e.stopPropagation()
    setZoom((z) => {
      const next = clampZoom(z + (e.deltaY > 0 ? -ZOOM_STEP : ZOOM_STEP))
      if (next === MIN_ZOOM) setPan({ x: 0, y: 0 })
      return next
    })
  }

  function toggleZoom() {
    if (zoom > MIN_ZOOM) {
      setZoom(MIN_ZOOM)
      setPan({ x: 0, y: 0 })
    } else {
      setZoom(2)
    }
  }

  function handleMouseDown(e: React.MouseEvent) {
    e.stopPropagation()
    dragStart.current = { x: e.clientX, y: e.clientY, panX: pan.x, panY: pan.y, moved: false }
    if (zoom > MIN_ZOOM) setDragging(true)
  }

  function handleMouseMove(e: React.MouseEvent) {
    if (!dragStart.current) return
    const dx = e.clientX - dragStart.current.x
    const dy = e.clientY - dragStart.current.y
    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) dragStart.current.moved = true
    if (zoom > MIN_ZOOM) {
      setPan({ x: dragStart.current.panX + dx, y: dragStart.current.panY + dy })
    }
  }

  function endDrag() {
    // A plain click (mousedown+mouseup with no real movement) toggles zoom —
    // matches the zoom-in cursor shown on hover instead of requiring a
    // separate double-click gesture. A real drag (used to pan while already
    // zoomed in) must NOT also toggle zoom when the mouse is released.
    const wasClick = dragStart.current !== null && !dragStart.current.moved
    setDragging(false)
    dragStart.current = null
    if (wasClick) toggleZoom()
  }

  return (
    <div className="lightbox-overlay" onClick={onClose}>
      <button type="button" className="lightbox-close" onClick={onClose} title="Close" aria-label="Close">
        <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <path d="M6 6l12 12M18 6L6 18" />
        </svg>
      </button>
      <div className="lightbox-toolbar" onClick={(e) => e.stopPropagation()}>
        {item.kind === 'Photo' && (
          <>
            <button
              type="button"
              onClick={() => handleRotate(-90)}
              disabled={rotating}
              title="Rotate left (L) — a saved edit, the original file is never touched"
              aria-label="Rotate left"
            >
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M1 4v6h6" />
                <path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
              </svg>
            </button>
            <button
              type="button"
              onClick={() => handleRotate(90)}
              disabled={rotating}
              title="Rotate right (R) — a saved edit, the original file is never touched"
              aria-label="Rotate right"
            >
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M23 4v6h-6" />
                <path d="M20.49 15a9 9 0 1 1-2.13-9.36L23 10" />
              </svg>
            </button>
          </>
        )}
        {allEvents && onMoveToEvent && (
          <>
            <button
              type="button"
              className="lightbox-move-btn"
              onClick={() => setMovingToEvent((v) => !v)}
              title="Move this item to a different Event — fixes a wrong grouping (e.g. a corrected timestamp)"
              aria-label="Move to Event"
            >
              <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M9 5l7 7-7 7" />
              </svg>
              <span className="lightbox-btn-label">Move</span>
            </button>
            {movingToEvent && (
              <EventPicker
                allEvents={allEvents}
                referenceTakenAt={item.takenAt}
                excludeEventIds={currentEventId === undefined ? undefined : [currentEventId]}
                search={moveSearch}
                onSearchChange={setMoveSearch}
                onPick={(targetEventId) => {
                  onMoveToEvent(item.id, targetEventId)
                  setMovingToEvent(false)
                  setMoveSearch('')
                }}
                onCancel={() => setMovingToEvent(false)}
              />
            )}
          </>
        )}
        {favoriteCategories && onSetFavoriteCategory && favoriteCategories.length > 0 && (
          <>
            <button
              type="button"
              className={item.favoriteCategoryId !== null ? 'is-set' : ''}
              onClick={handleFavoriteClick}
              title="Favorite category"
              aria-label="Favorite category"
            >
              <svg
                className="star-icon"
                viewBox="0 0 24 24"
                width="16"
                height="16"
                fill={item.favoriteCategoryId !== null ? 'currentColor' : 'none'}
                stroke="currentColor"
                strokeWidth="2.5"
                strokeLinejoin="round"
              >
                <path d="M12 .587l3.668 7.568 8.332 1.151-6.064 5.828 1.48 8.279-7.416-3.967-7.416 3.967 1.481-8.279-6.064-5.828 8.332-1.151z" />
              </svg>
            </button>
            {pickingFavorite && (
              <FavoriteCategoryMenu
                categories={favoriteCategories}
                currentId={item.favoriteCategoryId}
                onPick={(tagId) => {
                  onSetFavoriteCategory(item.id, tagId)
                  setPickingFavorite(false)
                }}
                onCancel={() => setPickingFavorite(false)}
              />
            )}
          </>
        )}
        <button
          type="button"
          className="lightbox-move-btn slideshow-start-btn"
          onClick={onStartSlideshow}
          title="Start a slideshow from here"
          aria-label="Slideshow"
        >
          <svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M8 5v14l11-7z" /></svg>
          <span className="lightbox-btn-label">Slideshow</span>
        </button>
        <button
          type="button"
          className="lightbox-delete-btn"
          onClick={() => setConfirmingDelete(true)}
          title="Move to Recycle Bin (Delete key)"
          aria-label="Delete"
        >
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6h16z" />
          </svg>
        </button>
      </div>
      {index > 0 && (
        <button
          type="button"
          className="lightbox-nav lightbox-prev"
          onClick={(e) => {
            e.stopPropagation()
            onNavigate(index - 1)
          }}
          aria-label="Previous"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M15 5l-7 7 7 7" />
          </svg>
        </button>
      )}
      {index < items.length - 1 && (
        <button
          type="button"
          className="lightbox-nav lightbox-next"
          onClick={(e) => {
            e.stopPropagation()
            onNavigate(index + 1)
          }}
          aria-label="Next"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M9 5l7 7-7 7" />
          </svg>
        </button>
      )}
      <div
        className="lightbox-content"
        onClick={(e) => e.stopPropagation()}
        onWheel={item.kind === 'Photo' ? handleWheel : undefined}
        onMouseMove={handleMouseMove}
        onMouseUp={endDrag}
        onMouseLeave={endDrag}
      >
        {item.kind === 'Video' ? (
          <video ref={videoRef} src={rawFileUrl(libraryPath, item.relativePath)} controls autoPlay />
        ) : item.liveVideoRelativePath && playingLive ? (
          <video ref={videoRef} src={rawFileUrl(libraryPath, item.liveVideoRelativePath)} autoPlay loop muted playsInline />
        ) : (
          <img
            src={fullImageUrl(libraryPath, item.relativePath, item.id)}
            alt=""
            onMouseDown={handleMouseDown}
            draggable={false}
            style={{
              transform: `scale(${zoom}) translate(${pan.x / zoom}px, ${pan.y / zoom}px)`,
              transition: dragging ? 'none' : 'transform 0.15s ease',
              cursor: zoom > MIN_ZOOM ? (dragging ? 'grabbing' : 'grab') : 'zoom-in',
            }}
          />
        )}
      </div>
      {item.liveVideoRelativePath && (
        <button
          type="button"
          className="lightbox-live-toggle"
          onClick={(e) => {
            e.stopPropagation()
            setPlayingLive((p) => !p)
          }}
        >
          {playingLive ? '◼ Stop' : '▶ Live'}
        </button>
      )}
      <div className="lightbox-caption" onClick={(e) => e.stopPropagation()}>
        {editingDate ? (
          <span className="lightbox-caption-edit">
            <input
              type="datetime-local"
              value={draftDate}
              onChange={(e) => setDraftDate(e.target.value)}
              onKeyDown={(e) => {
                // The component's own global keydown handler deliberately
                // ignores everything while a text field has focus (so typing
                // "l"/"r" into this same input doesn't also rotate the
                // photo), so this input needs its own Enter/Escape handling.
                if (e.key === 'Enter') {
                  if (!draftDate) return
                  onTakenAtChanged(item.id, draftDate)
                  setEditingDate(false)
                } else if (e.key === 'Escape') {
                  setEditingDate(false)
                }
              }}
              autoFocus
            />
            <button
              type="button"
              className="lightbox-caption-save"
              onClick={() => {
                if (!draftDate) return
                onTakenAtChanged(item.id, draftDate)
                setEditingDate(false)
              }}
            >
              Save
            </button>
            <button type="button" className="lightbox-caption-cancel" onClick={() => setEditingDate(false)}>
              Cancel
            </button>
            {onResetTakenAt && (
              <button
                type="button"
                className="lightbox-caption-reset"
                title="Re-read this file's real Exif/video date and use that instead — recovers the original date without needing to remember what it was, since the file itself is never touched."
                disabled={resettingTakenAt}
                onClick={async () => {
                  setResettingTakenAt(true)
                  try {
                    await onResetTakenAt(item.id)
                    setEditingDate(false)
                  } finally {
                    setResettingTakenAt(false)
                  }
                }}
              >
                {resettingTakenAt ? 'Resetting…' : 'Reset to Exif'}
              </button>
            )}
          </span>
        ) : (
          <>
            {formatTakenAt(item.takenAt, use24Hour)}
            {item.location && <> &middot; {item.location}</>}
            {item.takenAtSource === 'FileSystem' && (
              <span className="lightbox-caption-hint" title="No Exif or video creation date was found — this is just the file's last-modified timestamp, which can be wrong (e.g. after a copy or download).">
                {' '}(file date, not confirmed)
              </span>
            )}
            {item.takenAtSource === 'ExifModifyDate' && (
              <span className="lightbox-caption-hint" title="The camera's original date tag was missing (common after sharing through iMessage/WhatsApp/some cloud re-encodes) — this is a less-authoritative Exif fallback, not necessarily wrong.">
                {' '}(exif fallback)
              </span>
            )}
            {item.takenAtSource === 'Manual' && (
              <span className="lightbox-caption-hint" title="Set by hand — automatic date detection found nothing usable for this file.">
                {' '}(manual)
              </span>
            )}{' '}
            <button
              type="button"
              className="lightbox-caption-edit-btn"
              title="Correct this date/time"
              onClick={() => {
                setDraftDate(toDatetimeLocalValue(item.takenAt))
                setEditingDate(true)
              }}
            >
              &#9998;
            </button>{' '}
            &middot; {captionLabel} &middot; {index + 1} / {items.length}
          </>
        )}
      </div>

      {confirmingDelete && (
        <div className="event-picker-overlay" onClick={(e) => e.stopPropagation()}>
          <div className="confirm-dialog">
            <p>Move this {item.kind === 'Video' ? 'video' : 'photo'} to the Recycle Bin?</p>
            <div className="confirm-dialog-actions">
              <button
                type="button"
                className="modal-footer-cancel"
                ref={cancelDeleteButtonRef}
                onClick={() => setConfirmingDelete(false)}
              >
                Cancel
              </button>
              <button
                type="button"
                className="danger"
                ref={confirmDeleteButtonRef}
                autoFocus
                onClick={handleConfirmDelete}
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

